using System.Buffers.Binary;
using System.Numerics;

namespace FsForge.Core.Ext;

/// <summary>
/// Replay of the JBD2 journal of an ext3/ext4 filesystem that was not cleanly unmounted.
/// <para>
/// Nothing is written to the image. The journal is scanned the way the kernel and e2fsck do it
/// (scan, revoke and replay passes over the committed transactions) and the result is a map from
/// filesystem blocks to the journal blocks that hold their newest committed content. Reading the
/// image through <see cref="JournalOverlaySource"/> then shows the filesystem as it is after recovery.
/// </para>
/// Supported: journal superblock v1 and v2, 32- and 64-bit block numbers, revoke records,
/// escaped blocks, checksums v1, v2 and v3, async commit. Fast-commit records are not replayed.
/// </summary>
internal static class ExtJournal
{
    private const uint Magic = 0xC03B3998;
    private const int Descriptor = 1, Commit = 2, SuperblockV1 = 3, SuperblockV2 = 4, Revoke = 5;

    private const uint IncompatRevoke = 0x1, Incompat64Bit = 0x2, IncompatAsyncCommit = 0x4,
        IncompatCsumV2 = 0x8, IncompatCsumV3 = 0x10, IncompatFastCommit = 0x20;
    private const uint KnownIncompat = IncompatRevoke | Incompat64Bit | IncompatAsyncCommit | IncompatCsumV2 | IncompatCsumV3 | IncompatFastCommit;

    private const int FlagEscape = 1, FlagSameUuid = 2, FlagLastTag = 8;
    private const uint CompatChecksum = 0x1;
    private const int Crc32Checksum = 1, Crc32cChecksum = 4;
    private const int DefaultFastCommitBlocks = 256;

    /// <summary>Where the replayed content of one filesystem block is found.</summary>
    /// <param name="JournalBlock">Logical block of the journal inode holding the content.</param>
    /// <param name="Escaped">The block started with the journal magic, which was zeroed in the journal.</param>
    internal readonly record struct Replayed(long JournalBlock, bool Escaped);

    internal sealed class Result
    {
        /// <summary>Filesystem block → its newest committed copy in the journal.</summary>
        public Dictionary<long, Replayed> Blocks { get; } = new();

        public int Transactions { get; set; }
        public bool FastCommit { get; set; }
        public List<string> Warnings { get; } = new();
    }

    private sealed record Tag(long FsBlock, long JournalBlock, int Flags, uint Checksum);

    private sealed class Transaction(uint sequence)
    {
        public uint Sequence { get; } = sequence;
        public List<Tag> Tags { get; } = new();
        public List<long> Revoked { get; } = new();
    }

    /// <summary>
    /// Scans the journal and works out the replayed blocks.
    /// </summary>
    /// <param name="readBlock">Reads one logical block of the journal inode into the buffer.</param>
    /// <param name="journalBlocks">Size of the journal inode in blocks.</param>
    /// <param name="blockSize">Block size of the filesystem.</param>
    /// <param name="fsBlocks">Number of blocks in the filesystem.</param>
    public static Result Replay(Action<long, byte[]> readBlock, long journalBlocks, int blockSize, long fsBlocks)
    {
        var result = new Result();
        byte[] buf = new byte[blockSize];
        if (journalBlocks < 2) throw new FsException("The journal is too small.");

        // ---- journal superblock
        readBlock(0, buf);
        if (Be32(buf, 0) != Magic) throw new FsException("The journal superblock has a bad magic number.");
        int sbType = (int)Be32(buf, 4);
        if (sbType is not (SuperblockV1 or SuperblockV2)) throw new FsException("The journal superblock has an unknown type.");
        if (Be32(buf, 0x0C) != blockSize) throw new FsException("The journal block size differs from the filesystem block size.");
        long maxLen = Be32(buf, 0x10);
        long first = Be32(buf, 0x14);
        uint sequence = Be32(buf, 0x18);
        long start = Be32(buf, 0x1C);
        uint compat = sbType == SuperblockV2 ? Be32(buf, 0x24) : 0;
        uint incompat = sbType == SuperblockV2 ? Be32(buf, 0x28) : 0;

        if (maxLen > journalBlocks || maxLen < 2) throw new FsException("The journal superblock gives an invalid journal length.");
        if (first < 1 || first >= maxLen) throw new FsException("The journal superblock gives an invalid first log block.");
        if ((incompat & ~KnownIncompat) != 0)
            throw new FsException($"The journal uses unsupported features (incompat 0x{incompat & ~KnownIncompat:X}).");
        if (start == 0) return result;     // the journal is empty: nothing to replay

        bool is64 = (incompat & Incompat64Bit) != 0;
        bool csumV3 = (incompat & IncompatCsumV3) != 0;
        bool csumV2 = !csumV3 && (incompat & IncompatCsumV2) != 0;
        bool csum = csumV2 || csumV3;
        // Checksum v1: a crc32 of the descriptor and data blocks of each transaction, kept in its commit block.
        bool csumV1 = !csum && (compat & CompatChecksum) != 0;
        byte[] data = csumV1 ? new byte[blockSize] : Array.Empty<byte>();
        uint sum = ~0u;
        uint seed = 0;
        if (csum)
        {
            if (buf[0x50] != Crc32cChecksum) throw new FsException("The journal uses an unknown checksum type.");
            byte[] sb = buf.AsSpan(0, 1024).ToArray();
            uint stored = Be32(sb, 0xFC);
            BinaryPrimitives.WriteUInt32BigEndian(sb.AsSpan(0xFC), 0);
            if (Crc32c(~0u, sb) != stored) throw new FsException("The journal superblock checksum does not match.");
            seed = Crc32c(~0u, buf.AsSpan(0x30, 16));
        }

        // The log occupies [first, last); with fast commits the tail of the journal is the fast-commit area.
        long last = maxLen;
        if ((incompat & IncompatFastCommit) != 0)
        {
            long fc = Be32(buf, 0x54);
            if (fc == 0) fc = DefaultFastCommitBlocks;
            last = maxLen - fc;
            if (last - first < 2) throw new FsException("The journal fast-commit area leaves no room for the log.");
            result.FastCommit = true;
        }
        if (start < first || start >= last) throw new FsException("The journal superblock gives an invalid log start.");

        int tagBytes = csumV3 ? 16 : (csumV2 ? 10 : 8) + (is64 ? 4 : 0);
        int tailBytes = csum ? 4 : 0;
        long logBlocks = last - first;

        long Wrap(long b) => b >= last ? b - logBlocks : b;

        // ---- scan pass: collect the committed transactions
        var committed = new List<Transaction>();
        long pos = start;
        uint next = sequence;
        Transaction? current = null;
        for (long scanned = 0; scanned < logBlocks; scanned++)
        {
            readBlock(pos, buf);
            if (Be32(buf, 0) != Magic || Be32(buf, 8) != next) break;
            int type = (int)Be32(buf, 4);
            if (current is null)
            {
                current = new Transaction(next);
                sum = ~0u;
            }
            pos = Wrap(pos + 1);

            if (type == Descriptor)
            {
                if (csum && !TailValid(buf, seed))
                {
                    result.Warnings.Add($"Journal transaction {next} has a damaged descriptor block; the journal was replayed up to the transaction before it.");
                    break;
                }
                if (csumV1) sum = Crc32Be(sum, buf);
                int firstTag = current.Tags.Count;
                int p = 12, limit = blockSize - tailBytes;
                while (p + tagBytes <= limit)
                {
                    long block = Be32(buf, p);
                    if (is64) block |= (long)Be32(buf, p + 8) << 32;
                    int flags = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(p + 6));
                    uint check = csumV3 ? Be32(buf, p + 12) : csumV2 ? BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(p + 4)) : 0u;
                    current.Tags.Add(new Tag(block, pos, flags, check));
                    pos = Wrap(pos + 1);
                    scanned++;
                    p += tagBytes;
                    if ((flags & FlagSameUuid) == 0) p += 16;
                    if ((flags & FlagLastTag) != 0) break;
                }
                if (csumV1)
                {
                    for (int i = firstTag; i < current.Tags.Count; i++)
                    {
                        readBlock(current.Tags[i].JournalBlock, data);
                        sum = Crc32Be(sum, data);
                    }
                }
            }
            else if (type == Commit)
            {
                if ((csum && !CommitValid(buf, seed)) || (csumV1 && !CommitValidV1(buf, sum)))
                {
                    result.Warnings.Add($"Journal transaction {next} has a bad commit checksum; the journal was replayed up to the transaction before it.");
                    break;
                }
                committed.Add(current);
                current = null;
                next++;
            }
            else if (type == Revoke)
            {
                if (csum && !TailValid(buf, seed))
                {
                    result.Warnings.Add($"Journal transaction {next} has a damaged revoke block; the journal was replayed up to the transaction before it.");
                    break;
                }
                int count = (int)Be32(buf, 0x0C);
                int record = is64 ? 8 : 4;
                if (count < 16 || count > blockSize - tailBytes) break;
                for (int p = 16; p + record <= count; p += record)
                    current.Revoked.Add(is64 ? (long)BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(p)) : Be32(buf, p));
            }
            else
            {
                break;      // anything else ends the log
            }
        }
        // "current" (if any) is a transaction without a commit block: it was never completed and is dropped.

        // ---- revoke pass: the newest revoking transaction of every block
        var revoked = new Dictionary<long, uint>();
        foreach (Transaction t in committed)
            foreach (long block in t.Revoked)
                if (!revoked.TryGetValue(block, out uint seq) || SeqGreater(t.Sequence, seq)) revoked[block] = t.Sequence;

        // ---- replay pass: later transactions overwrite earlier ones
        int badChecksums = 0, outOfRange = 0;
        foreach (Transaction t in committed)
        {
            foreach (Tag tag in t.Tags)
            {
                // A block revoked by this or a later transaction keeps the content it has outside the journal.
                if (revoked.TryGetValue(tag.FsBlock, out uint rseq) && !SeqGreater(t.Sequence, rseq)) continue;
                if (tag.FsBlock < 0 || tag.FsBlock >= fsBlocks)
                {
                    outOfRange++;
                    continue;
                }
                if (csum)
                {
                    readBlock(tag.JournalBlock, buf);
                    if (!TagValid(buf, seed, t.Sequence, tag.Checksum, csumV3))
                    {
                        badChecksums++;
                        continue;
                    }
                }
                result.Blocks[tag.FsBlock] = new Replayed(tag.JournalBlock, (tag.Flags & FlagEscape) != 0);
            }
        }
        result.Transactions = committed.Count;
        if (badChecksums > 0)
            result.Warnings.Add($"Journal: {badChecksums} block(s) with a bad checksum were not replayed.");
        if (outOfRange > 0)
            result.Warnings.Add($"Journal: {outOfRange} block(s) outside the filesystem were not replayed.");
        return result;
    }

    // ------------------------------------------------------------ checksums

    private static bool TailValid(byte[] block, uint seed)
    {
        int at = block.Length - 4;
        uint stored = Be32(block, at);
        byte[] copy = (byte[])block.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(at), 0);
        return Crc32c(seed, copy) == stored;
    }

    private static bool CommitValid(byte[] block, uint seed)
    {
        uint stored = Be32(block, 0x10);
        byte[] copy = (byte[])block.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(0x10), 0);
        return Crc32c(seed, copy) == stored;
    }

    private static bool CommitValidV1(byte[] block, uint sum)
    {
        int type = block[0x0C], size = block[0x0D];
        uint stored = Be32(block, 0x10);
        return (type == Crc32Checksum && size == 4 && stored == sum) || (type == 0 && size == 0 && stored == 0);
    }

    private static bool TagValid(byte[] data, uint seed, uint sequence, uint stored, bool v3)
    {
        Span<byte> seq = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(seq, sequence);
        uint crc = Crc32c(Crc32c(seed, seq), data);
        return v3 ? crc == stored : (crc & 0xFFFF) == stored;
    }

    /// <summary>CRC32C without pre- and post-inversion, like the kernel's crc32c().</summary>
    internal static uint Crc32c(uint crc, ReadOnlySpan<byte> data)
    {
        while (data.Length >= 8)
        {
            crc = BitOperations.Crc32C(crc, BinaryPrimitives.ReadUInt64LittleEndian(data));
            data = data[8..];
        }
        foreach (byte b in data) crc = BitOperations.Crc32C(crc, b);
        return crc;
    }

    private static readonly uint[] Crc32BeTable = BuildCrc32BeTable();

    private static uint[] BuildCrc32BeTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i << 24;
            for (int k = 0; k < 8; k++) c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
            t[i] = c;
        }
        return t;
    }

    /// <summary>MSB-first CRC32 without inversion, like the kernel's crc32_be().</summary>
    internal static uint Crc32Be(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = (crc << 8) ^ Crc32BeTable[(crc >> 24) ^ b];
        return crc;
    }

    /// <summary>Transaction IDs wrap around; "a after b" as the kernel's tid_gt.</summary>
    private static bool SeqGreater(uint a, uint b) => (int)(a - b) > 0;

    private static uint Be32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b[o..]);
}

/// <summary>
/// View of an ext image with the journal replayed: blocks recovered from the journal are read from
/// their journal copies, everything else from the image itself. Read-only and thread-safe.
/// </summary>
internal sealed class JournalOverlaySource : IBlockSource
{
    private const uint JournalMagic = 0xC03B3998;

    private readonly IBlockSource _inner;
    private readonly int _blockSize;
    private readonly long[] _blocks;                 // sorted replayed filesystem blocks
    private readonly long[] _offsets;                // byte offset of each one's journal copy in the image
    private readonly bool[] _escaped;

    public JournalOverlaySource(IBlockSource inner, int blockSize, IReadOnlyDictionary<long, (long Offset, bool Escaped)> blocks)
    {
        _inner = inner;
        _blockSize = blockSize;
        _blocks = blocks.Keys.Order().ToArray();
        _offsets = new long[_blocks.Length];
        _escaped = new bool[_blocks.Length];
        for (int i = 0; i < _blocks.Length; i++)
            (_offsets[i], _escaped[i]) = blocks[_blocks[i]];
    }

    public long Length => _inner.Length;

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= Length || buffer.IsEmpty) return 0;
        if (buffer.Length > Length - offset) buffer = buffer[..(int)(Length - offset)];
        long block = offset / _blockSize;
        int inBlock = (int)(offset % _blockSize);
        int i = Array.BinarySearch(_blocks, block);
        if (i >= 0)
        {
            int n = Math.Min(buffer.Length, _blockSize - inBlock);
            _inner.ReadExactly(_offsets[i] + inBlock, buffer[..n]);
            if (_escaped[i] && inBlock < 4)
            {
                // The journal stores such a block with its first four bytes zeroed.
                Span<byte> magic = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(magic, JournalMagic);
                int m = Math.Min(4 - inBlock, n);
                magic.Slice(inBlock, m).CopyTo(buffer);
            }
            return n;
        }
        // Read the image directly up to the next replayed block.
        int nextIndex = ~i;
        if (nextIndex < _blocks.Length)
        {
            long limit = _blocks[nextIndex] * _blockSize - offset;
            if (limit < buffer.Length) buffer = buffer[..(int)limit];
        }
        return _inner.Read(offset, buffer);
    }

    public void Dispose() { }
}
