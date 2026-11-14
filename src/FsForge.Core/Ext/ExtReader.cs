using System.Text;
using static FsForge.Core.Ext.ExtFormat;

namespace FsForge.Core.Ext;

/// <summary>Reader for ext2, ext3 and ext4 images (block maps, extents, inline data, xattrs).</summary>
public sealed class ExtReader : IDisposable
{
    private readonly IBlockSource _image;
    private readonly bool _ownsSource;

    /// <summary>What the reader reads: the image itself, or the image with its journal replayed.</summary>
    private IBlockSource _source;

    private int _blockSize;
    private long _blocksCount;
    private uint _inodesCount, _blocksPerGroup, _inodesPerGroup, _firstDataBlock, _firstMetaBg;
    private int _inodeSize, _descSize;
    private uint _incompat, _roCompat;
    private bool _is64Bit;
    private long[] _inodeTables = Array.Empty<long>();
    private uint _journalInode;

    // Journal recovery done while opening (see ReplayJournal).
    private bool _replayed;
    private int _replayedTransactions, _replayedBlocks;

    private ExtReader(IBlockSource source, bool ownsSource)
    {
        _image = source;
        _source = source;
        _ownsSource = ownsSource;
        Info = null!;
    }

    public ExtInfo Info { get; private set; }
    public List<string> Warnings { get; } = new();

    public static bool IsExt(IBlockSource source)
    {
        if (source.Length < SuperblockOffset + 1024) return false;
        Span<byte> b = stackalloc byte[2];
        source.ReadExactly(SuperblockOffset + 0x38, b);
        return Le.U16(b, 0) == Magic;
    }

    public static ExtReader Open(IBlockSource source, bool ownsSource = true)
    {
        var r = new ExtReader(source, ownsSource);
        try
        {
            r.ReadSuperblock();
            if (r.Info.NeedsRecovery) r.ReplayJournal();
            return r;
        }
        catch
        {
            if (ownsSource) source.Dispose();
            throw;
        }
    }

    // ---------------------------------------------------------- superblock

    private void ReadSuperblock()
    {
        if (_source.Length < SuperblockOffset + 1024) throw new FsException("The file is too small to be an ext image.");
        byte[] sb = _source.ReadBytes(SuperblockOffset, 1024);
        if (Le.U16(sb, 0x38) != Magic) throw new FsException("Not an ext2/ext3/ext4 image (bad magic).");

        uint logBlock = Le.U32(sb, 0x18);
        if (logBlock > 6) throw new FsException("Corrupt ext superblock: invalid block size.");
        _blockSize = 1024 << (int)logBlock;
        uint compat = Le.U32(sb, 0x5C);
        _incompat = Le.U32(sb, 0x60);
        _roCompat = Le.U32(sb, 0x64);
        uint rev = Le.U32(sb, 0x4C);
        _is64Bit = (_incompat & Incompat64Bit) != 0;

        _inodesCount = Le.U32(sb, 0x00);
        _blocksCount = Le.U32(sb, 0x04);
        long reserved = Le.U32(sb, 0x08);
        long freeBlocks = Le.U32(sb, 0x0C);
        if (_is64Bit)
        {
            _blocksCount |= (long)Le.U32(sb, 0x150) << 32;
            reserved |= (long)Le.U32(sb, 0x154) << 32;
            freeBlocks |= (long)Le.U32(sb, 0x158) << 32;
        }
        _firstDataBlock = Le.U32(sb, 0x14);
        _blocksPerGroup = Le.U32(sb, 0x20);
        _inodesPerGroup = Le.U32(sb, 0x28);
        _inodeSize = rev >= 1 ? Le.U16(sb, 0x58) : 128;
        _descSize = _is64Bit ? Math.Max(32, (int)Le.U16(sb, 0xFE)) : 32;
        _firstMetaBg = Le.U32(sb, 0x104);

        if (_blocksPerGroup == 0 || _inodesPerGroup == 0 || _inodeSize < 128 || _inodeSize > _blockSize || _blocksCount <= _firstDataBlock)
            throw new FsException("Corrupt ext superblock: invalid geometry.");

        uint unsupported = _incompat & (IncompatCompression | IncompatJournalDev | IncompatEncrypt | IncompatDirData);
        if ((unsupported & IncompatEncrypt) != 0) throw new FsException("Encrypted ext4 images are not supported.");
        if ((unsupported & IncompatJournalDev) != 0) throw new FsException("This is an external ext journal device, not a filesystem.");
        if (unsupported != 0) throw new FsException($"The image uses unsupported ext features (incompat 0x{unsupported:X}).");

        // After a replay the superblock may still carry the flag (it is cleared on disk only by a real recovery).
        if (_replayed) _incompat &= ~IncompatRecover;
        bool needsRecovery = (_incompat & IncompatRecover) != 0;
        if (_blocksCount * _blockSize > _source.Length + _blockSize)
            Warnings.Add("The image file is shorter than the filesystem it contains.");

        bool hasJournal = (compat & CompatHasJournal) != 0;
        const uint ext4Incompat = IncompatExtents | Incompat64Bit | IncompatFlexBg | IncompatMetaBg | IncompatInlineData | IncompatEaInode | IncompatMmp;
        const uint ext4Ro = RoHugeFile | RoGdtCsum | RoDirNlink | RoExtraIsize | RoMetadataCsum | RoBigalloc | RoQuota;
        ExtKind kind = (_incompat & ext4Incompat) != 0 || (_roCompat & ext4Ro) != 0 ? ExtKind.Ext4
            : hasJournal ? ExtKind.Ext3 : ExtKind.Ext2;

        int labelLen = sb.AsSpan(0x78, 16).IndexOf((byte)0);
        string label = Encoding.UTF8.GetString(sb, 0x78, labelLen < 0 ? 16 : labelLen);

        // Group descriptors.
        long groups = (_blocksCount - _firstDataBlock + _blocksPerGroup - 1) / _blocksPerGroup;
        if (groups <= 0 || groups > 1 << 24) throw new FsException("Corrupt ext superblock: invalid group count.");
        _inodeTables = new long[groups];
        int perBlock = _blockSize / _descSize;
        bool metaBg = (_incompat & IncompatMetaBg) != 0;
        bool sparseSuper = (_roCompat & RoSparseSuper) != 0;
        byte[] buf = new byte[_blockSize];
        long loaded = -1;
        for (long g = 0; g < groups; g++)
        {
            long n = g / perBlock;
            if (n != loaded)
            {
                long block;
                if (!metaBg || n < _firstMetaBg)
                {
                    block = _firstDataBlock + 1 + n;
                }
                else
                {
                    uint bg = (uint)(perBlock * n);
                    int hasSuper = GroupHasSuper(bg, sparseSuper) ? 1 : 0;
                    block = _firstDataBlock + (long)bg * _blocksPerGroup + hasSuper;
                }
                ReadBlock(block, buf);
                loaded = n;
            }
            int off = (int)(g % perBlock) * _descSize;
            long table = Le.U32(buf, off + 8);
            if (_is64Bit && _descSize >= 64) table |= (long)Le.U32(buf, off + 0x28) << 32;
            _inodeTables[g] = table;
        }

        _journalInode = hasJournal ? Le.U32(sb, 0xE0) : 0;
        long journalBytes = 0;
        if (hasJournal && Le.U32(sb, 0xE0) != 0)
            journalBytes = ((long)Le.U32(sb, 0x10C + 15 * 4) << 32) | Le.U32(sb, 0x10C + 16 * 4);

        Info = new ExtInfo
        {
            Kind = kind,
            BlockSize = _blockSize,
            BlocksCount = _blocksCount,
            FreeBlocks = freeBlocks,
            ReservedBlocks = reserved,
            InodesCount = _inodesCount,
            FreeInodes = Le.U32(sb, 0x10),
            InodeSize = _inodeSize,
            BlocksPerGroup = _blocksPerGroup,
            InodesPerGroup = _inodesPerGroup,
            FeatureCompat = compat,
            FeatureIncompat = _incompat,
            FeatureRoCompat = _roCompat,
            Label = label,
            Uuid = new Guid(sb.AsSpan(0x68, 16), bigEndian: true),
            HasJournal = hasJournal,
            JournalBytes = journalBytes,
            NeedsRecovery = needsRecovery,
            JournalReplayed = _replayed,
            ReplayedTransactions = _replayedTransactions,
            ReplayedBlocks = _replayedBlocks,
            Clean = (Le.U16(sb, 0x3A) & 1) != 0,
            MkfsTime = Le.U32(sb, 0x108),
            WriteTime = Le.U32(sb, 0x30),
        };
    }

    // ------------------------------------------------------------- journal

    private const string LostChanges = " Changes recorded only in the journal are missing from what is shown and saved.";

    /// <summary>
    /// Replays the journal of a filesystem that was not cleanly unmounted. The image is not changed:
    /// from here on every read goes through an overlay that serves recovered blocks from the journal,
    /// and the superblock and group descriptors are read again from that view.
    /// </summary>
    private void ReplayJournal()
    {
        if (_journalInode == 0)
        {
            Warnings.Add("The filesystem was not cleanly unmounted and its journal is on a separate device, so it was not replayed." + LostChanges);
            return;
        }

        ExtJournal.Result result;
        Dictionary<long, (long Offset, bool Escaped)> map;
        try
        {
            RawInode journal = ReadInode(_journalInode);
            List<Run> runs = MapBlocks(journal);
            long journalBlocks = journal.Size / _blockSize;

            long Physical(long logical)
            {
                // Runs are sorted by logical block.
                int lo = 0, hi = runs.Count - 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    Run r = runs[mid];
                    if (logical < r.Logical) hi = mid - 1;
                    else if (logical >= r.Logical + r.Count) lo = mid + 1;
                    else return r.Phys + (logical - r.Logical);
                }
                throw new FsException($"Block {logical} of the journal is not allocated.");
            }

            result = ExtJournal.Replay((logical, buf) => ReadBlock(Physical(logical), buf), journalBlocks, _blockSize, _blocksCount);
            map = new Dictionary<long, (long, bool)>(result.Blocks.Count);
            foreach ((long block, ExtJournal.Replayed r) in result.Blocks)
                map[block] = (Physical(r.JournalBlock) * _blockSize, r.Escaped);
        }
        catch (FsException ex)
        {
            Warnings.Add("The filesystem was not cleanly unmounted and its journal could not be replayed: " + ex.Message + LostChanges);
            return;
        }

        // Read the filesystem again as it is after recovery.
        _replayed = true;
        _replayedTransactions = result.Transactions;
        _replayedBlocks = map.Count;
        if (map.Count > 0) _source = new JournalOverlaySource(_image, _blockSize, map);
        Warnings.Clear();
        try
        {
            ReadSuperblock();
        }
        catch (FsException ex)
        {
            // The recovered metadata is unusable: fall back to the image as it is outside the journal.
            _source = _image;
            _replayed = false;
            _replayedTransactions = _replayedBlocks = 0;
            Warnings.Clear();
            ReadSuperblock();
            Warnings.Add("The filesystem was not cleanly unmounted and its replayed journal gives invalid metadata (" + ex.Message + "), so it was not used." + LostChanges);
            return;
        }

        if (result.Transactions > 0)
            Warnings.Add($"The filesystem was not cleanly unmounted; its journal was replayed while reading " +
                         $"({result.Transactions} transaction(s), {map.Count} block(s)). The image file itself is unchanged.");
        Warnings.AddRange(result.Warnings);
        if (result.FastCommit && result.Transactions > 0)
            Warnings.Add("The journal uses fast commits, which are not replayed: the very last changes may be missing.");
    }

    private void ReadBlock(long block, Span<byte> dst)
    {
        if (block <= 0 || block >= _blocksCount) throw new FsException($"Corrupt ext image: block {block} is out of range.");
        _source.ReadExactly(block * _blockSize, dst[.._blockSize]);
    }

    // --------------------------------------------------------------- inodes

    private sealed class RawInode
    {
        public uint Number;
        public int Mode;
        public uint Uid, Gid, Flags;
        public long Size, MTime;
        public int Links;
        public long BlocksLo;
        public long FileAcl;
        public byte[] Block = Array.Empty<byte>();     // i_block, 60 bytes
        public byte[] Raw = Array.Empty<byte>();
    }

    private RawInode ReadInode(uint number)
    {
        if (number == 0 || number > _inodesCount) throw new FsException($"Corrupt ext image: inode {number} is out of range.");
        long group = (number - 1) / _inodesPerGroup;
        long index = (number - 1) % _inodesPerGroup;
        long pos = _inodeTables[group] * _blockSize + index * _inodeSize;
        byte[] raw = _source.ReadBytes(pos, _inodeSize);
        var ino = new RawInode
        {
            Number = number,
            Raw = raw,
            Mode = Le.U16(raw, 0x00),
            Uid = Le.U16(raw, 0x02) | ((uint)Le.U16(raw, 0x78) << 16),
            Gid = Le.U16(raw, 0x18) | ((uint)Le.U16(raw, 0x7A) << 16),
            Links = Le.U16(raw, 0x1A),
            BlocksLo = Le.U32(raw, 0x1C),
            Flags = Le.U32(raw, 0x20),
            Block = raw.AsSpan(0x28, 60).ToArray(),
            FileAcl = Le.U32(raw, 0x68) | ((long)Le.U16(raw, 0x76) << 32),
        };
        long size = Le.U32(raw, 0x04);
        int type = ino.Mode & UnixMode.S_IFMT;
        if (type == UnixMode.S_IFREG || type == UnixMode.S_IFDIR) size |= (long)Le.U32(raw, 0x6C) << 32;
        ino.Size = size;

        long mtime = (int)Le.U32(raw, 0x10);
        if (_inodeSize > 128)
        {
            int extra = Le.U16(raw, 0x80);
            if (extra >= 12 && 128 + extra <= _inodeSize) mtime += (long)(Le.U32(raw, 0x88) & 3) << 32;
        }
        ino.MTime = mtime;
        return ino;
    }

    // -------------------------------------------------------- block mapping

    /// <summary>Contiguous run of file blocks. Phys 0 means a hole (or an unwritten extent).</summary>
    internal readonly record struct Run(long Logical, long Phys, long Count);

    private List<Run> MapBlocks(RawInode ino)
    {
        var runs = new List<Run>();
        long fileBlocks = (ino.Size + _blockSize - 1) / _blockSize;
        if ((ino.Flags & FlagExtents) != 0)
        {
            WalkExtents(ino.Block, runs, 0, ino.Number);
            runs.Sort(static (a, b) => a.Logical.CompareTo(b.Logical));
        }
        else
        {
            long logical = 0;
            for (int i = 0; i < 12 && logical < fileBlocks; i++, logical++)
                AddBlock(runs, logical, Le.U32(ino.Block, i * 4));
            for (int level = 1; level <= 3 && logical < fileBlocks; level++)
                logical = WalkIndirect(Le.U32(ino.Block, (11 + level) * 4), level, logical, fileBlocks, runs);
        }
        return runs;
    }

    private static void AddBlock(List<Run> runs, long logical, long phys)
    {
        if (phys == 0) return;
        if (runs.Count > 0)
        {
            Run last = runs[^1];
            if (last.Logical + last.Count == logical && last.Phys + last.Count == phys)
            {
                runs[^1] = last with { Count = last.Count + 1 };
                return;
            }
        }
        runs.Add(new Run(logical, phys, 1));
    }

    private long WalkIndirect(long block, int level, long logical, long fileBlocks, List<Run> runs)
    {
        int perBlock = _blockSize / 4;
        long span = 1;
        for (int i = 1; i < level; i++) span *= perBlock;
        if (block == 0) return logical + span * perBlock;      // hole covering the whole subtree
        byte[] buf = new byte[_blockSize];
        ReadBlock(block, buf);
        for (int i = 0; i < perBlock && logical < fileBlocks; i++)
        {
            uint entry = Le.U32(buf, i * 4);
            if (level == 1)
            {
                AddBlock(runs, logical, entry);
                logical++;
            }
            else
            {
                logical = WalkIndirect(entry, level - 1, logical, fileBlocks, runs);
            }
        }
        return logical;
    }

    private void WalkExtents(ReadOnlySpan<byte> node, List<Run> runs, int level, uint inode)
    {
        if (Le.U16(node, 0) != ExtentMagic) throw new FsException($"Corrupt ext image: bad extent header in inode {inode}.");
        int entries = Le.U16(node, 2);
        int depth = Le.U16(node, 6);
        if (level > 6 || 12 + entries * 12 > node.Length) throw new FsException($"Corrupt ext image: bad extent tree in inode {inode}.");
        for (int i = 0; i < entries; i++)
        {
            int o = 12 + i * 12;
            if (depth == 0)
            {
                long logical = Le.U32(node, o);
                int len = Le.U16(node, o + 4);
                long phys = ((long)Le.U16(node, o + 6) << 32) | Le.U32(node, o + 8);
                if (len > 32768) continue;      // unwritten extent: reads as zeros
                if (len == 0) continue;
                if (phys <= 0 || phys + len > _blocksCount) throw new FsException($"Corrupt ext image: extent of inode {inode} is out of range.");
                runs.Add(new Run(logical, phys, len));
            }
            else
            {
                long child = Le.U32(node, o + 4) | ((long)Le.U16(node, o + 8) << 32);
                byte[] buf = new byte[_blockSize];
                ReadBlock(child, buf);
                WalkExtents(buf, runs, level + 1, inode);
            }
        }
    }

    // ------------------------------------------------------------ file data

    private sealed class ExtFileData(ExtReader reader, List<Run> runs, long length) : ISparseFileData
    {
        public long Length => length;

        public long DataBytesHint
        {
            get
            {
                long blocks = 0;
                foreach (Run r in runs) if (r.Phys != 0) blocks += r.Count;
                return Math.Min(blocks * reader._blockSize, length);
            }
        }

        public Stream OpenRead() => new ExtFileStream(reader, runs, length);
    }

    private sealed class ExtFileStream(ExtReader r, List<Run> runs, long length) : Stream
    {
        private long _position;
        private int _runIndex;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set
            {
                _position = Math.Clamp(value, 0, length);
                _runIndex = 0;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            long left = length - _position;
            if (left <= 0 || buffer.Length == 0) return 0;
            if (buffer.Length > left) buffer = buffer[..(int)left];
            int bs = r._blockSize;
            long block = _position / bs;
            int inBlock = (int)(_position % bs);

            while (_runIndex < runs.Count && runs[_runIndex].Logical + runs[_runIndex].Count <= block) _runIndex++;
            int n;
            if (_runIndex < runs.Count && runs[_runIndex].Logical <= block)
            {
                Run run = runs[_runIndex];
                long runBytesLeft = (run.Logical + run.Count - block) * bs - inBlock;
                n = (int)Math.Min(buffer.Length, runBytesLeft);
                r._source.ReadExactly((run.Phys + (block - run.Logical)) * bs + inBlock, buffer[..n]);
            }
            else
            {
                // Hole: zeros up to the next mapped run or the end of the request.
                long holeEnd = _runIndex < runs.Count ? runs[_runIndex].Logical * bs : length;
                n = (int)Math.Min(buffer.Length, holeEnd - _position);
                buffer[..n].Clear();
            }
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => length + offset,
            };
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private byte[] ReadAll(RawInode ino, long limit)
    {
        if (ino.Size > limit) throw new FsException($"Corrupt ext image: inode {ino.Number} is unexpectedly large.");
        byte[] data = new byte[ino.Size];
        using var s = new ExtFileStream(this, MapBlocks(ino), ino.Size);
        s.ReadExactly(data);
        return data;
    }

    // --------------------------------------------------------------- xattrs

    private void ReadXattrs(RawInode ino, out SortedDictionary<string, byte[]>? result, out byte[]? inlineData)
    {
        result = null;
        inlineData = null;
        var list = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);

        // In-inode area that follows the fixed fields.
        if (_inodeSize > 128)
        {
            int extra = Le.U16(ino.Raw, 0x80);
            int start = 128 + extra;
            if (extra >= 4 && start + 8 <= _inodeSize && Le.U32(ino.Raw, start) == XattrMagic)
                ParseXattrEntries(ino, ino.Raw, start + 4, start + 4, _inodeSize, list, ref inlineData);
        }

        if (ino.FileAcl != 0)
        {
            byte[] block = new byte[_blockSize];
            try
            {
                ReadBlock(ino.FileAcl, block);
                if (Le.U32(block, 0) == XattrMagic)
                    ParseXattrEntries(ino, block, 32, 0, _blockSize, list, ref inlineData);
                else
                    Warnings.Add($"Inode {ino.Number}: invalid extended attribute block was ignored.");
            }
            catch (FsException)
            {
                Warnings.Add($"Inode {ino.Number}: unreadable extended attribute block was ignored.");
            }
        }
        if (list.Count > 0) result = list;
    }

    private void ParseXattrEntries(RawInode ino, byte[] buf, int pos, int valueBase, int end,
        SortedDictionary<string, byte[]> list, ref byte[]? inlineData)
    {
        while (pos + 16 <= end)
        {
            if (Le.U32(buf, pos) == 0) break;              // terminator: four zero bytes
            int nameLen = buf[pos];
            int nameIndex = buf[pos + 1];
            int valueOffs = Le.U16(buf, pos + 2);
            uint valueInum = Le.U32(buf, pos + 4);
            uint valueSize = Le.U32(buf, pos + 8);
            if (pos + 16 + nameLen > end) break;
            string suffix = PosixName.Decode(buf.AsSpan(pos + 16, nameLen));
            pos += (16 + nameLen + 3) & ~3;

            byte[] value;
            if (valueInum != 0)
            {
                try
                {
                    value = ReadAll(ReadInode(valueInum), 1 << 26);
                }
                catch (FsException)
                {
                    Warnings.Add($"Inode {ino.Number}: unreadable large extended attribute was skipped.");
                    continue;
                }
            }
            else
            {
                if (valueSize > end || valueBase + valueOffs + valueSize > end) continue;
                value = buf.AsSpan(valueBase + valueOffs, (int)valueSize).ToArray();
            }

            if (nameIndex == 7 && suffix == "data")
            {
                inlineData = value;
                continue;
            }
            string? prefix = XattrPrefix(nameIndex);
            if (prefix is null)
            {
                Warnings.Add($"Inode {ino.Number}: extended attribute with unknown name index {nameIndex} was skipped.");
                continue;
            }
            if (nameIndex is 2 or 3)
            {
                byte[]? converted = AclDiskToXattr(value);
                if (converted is null) continue;
                value = converted;
            }
            list[prefix + suffix] = value;
        }
    }

    // ----------------------------------------------------------------- tree

    private readonly record struct DirEntry(uint Inode, string Name);

    private static void ParseDirBlock(ReadOnlySpan<byte> block, List<DirEntry> entries)
    {
        int pos = 0;
        while (pos + 8 <= block.Length)
        {
            uint inode = Le.U32(block, pos);
            int recLen = Le.U16(block, pos + 4);
            int nameLen = block[pos + 6];
            if (recLen == 0 && block.Length == 65536) recLen = 65536;
            if (recLen < 8 || pos + recLen > block.Length) break;
            if (inode != 0 && nameLen > 0 && 8 + nameLen <= recLen)
            {
                ReadOnlySpan<byte> name = block.Slice(pos + 8, nameLen);
                bool dot = (nameLen == 1 && name[0] == '.') || (nameLen == 2 && name[0] == '.' && name[1] == '.');
                if (!dot) entries.Add(new DirEntry(inode, PosixName.Decode(name)));
            }
            pos += recLen;
        }
    }

    private List<DirEntry> ReadDirectory(RawInode ino, byte[]? inlineData)
    {
        var entries = new List<DirEntry>();
        if ((ino.Flags & FlagInlineData) != 0)
        {
            // i_block: parent inode (4 bytes) followed by entries; the rest lives in "system.data".
            int first = (int)Math.Min(60, ino.Size);
            if (first > 4) ParseDirBlock(ino.Block.AsSpan(4, first - 4), entries);
            if (inlineData is { Length: > 0 }) ParseDirBlock(inlineData, entries);
            return entries;
        }
        if (ino.Size > 1L << 31) throw new FsException($"Corrupt ext image: directory inode {ino.Number} is too large.");
        List<Run> runs = MapBlocks(ino);
        byte[] buf = new byte[_blockSize];
        long blocks = ino.Size / _blockSize;
        foreach (Run run in runs)
        {
            for (long i = 0; i < run.Count && run.Logical + i < blocks; i++)
            {
                ReadBlock(run.Phys + i, buf);
                ParseDirBlock(buf, entries);
            }
        }
        return entries;
    }

    private FsInode BuildInode(RawInode raw, out List<DirEntry>? children)
    {
        children = null;
        FsNodeType? type = UnixMode.TypeFromMode(raw.Mode);
        if (type is null) throw new FsException($"Corrupt ext image: inode {raw.Number} has an invalid type.");
        ReadXattrs(raw, out SortedDictionary<string, byte[]>? xattrs, out byte[]? inlineData);
        var inode = new FsInode
        {
            Type = type.Value,
            Mode = raw.Mode & 0xFFF,
            Uid = raw.Uid,
            Gid = raw.Gid,
            MTime = raw.MTime,
            XAttrs = xattrs,
        };
        bool inline = (raw.Flags & FlagInlineData) != 0;
        switch (type.Value)
        {
            case FsNodeType.Directory:
                children = ReadDirectory(raw, inlineData);
                break;

            case FsNodeType.File:
                if (raw.Size == 0) break;
                if (inline)
                {
                    inode.Data = new MemoryFileData(InlineBytes(raw, inlineData));
                }
                else
                {
                    inode.Data = new ExtFileData(this, MapBlocks(raw), raw.Size);
                }
                break;

            case FsNodeType.Symlink:
            {
                byte[] target;
                if (raw.Size > 65536) throw new FsException($"Corrupt ext image: symlink inode {raw.Number} is too long.");
                if (inline)
                {
                    target = InlineBytes(raw, inlineData);
                }
                else
                {
                    long eaBlocks = raw.FileAcl != 0 ? _blockSize / 512 : 0;
                    bool fast = raw.Size < 60 && (raw.BlocksLo - eaBlocks == 0 ||
                                                  ((raw.Flags & FlagExtents) != 0 && Le.U16(raw.Block, 0) != ExtentMagic));
                    target = fast ? raw.Block.AsSpan(0, (int)raw.Size).ToArray() : ReadAll(raw, 65536);
                }
                int nul = Array.IndexOf(target, (byte)0);
                inode.LinkTarget = PosixName.Decode(nul >= 0 ? target.AsSpan(0, nul) : target);
                break;
            }

            case FsNodeType.CharDevice:
            case FsNodeType.BlockDevice:
            {
                uint oldDev = Le.U32(raw.Block, 0);
                if (oldDev != 0)
                {
                    inode.DevMajor = (oldDev >> 8) & 0xFF;
                    inode.DevMinor = oldDev & 0xFF;
                }
                else
                {
                    uint dev = Le.U32(raw.Block, 4);
                    inode.DevMajor = (dev & 0xFFF00) >> 8;
                    inode.DevMinor = (dev & 0xFF) | ((dev >> 12) & 0xFFF00);
                }
                break;
            }
        }
        return inode;
    }

    private static byte[] InlineBytes(RawInode raw, byte[]? extra)
    {
        byte[] data = new byte[raw.Size];
        int first = (int)Math.Min(60, raw.Size);
        raw.Block.AsSpan(0, first).CopyTo(data);
        if (raw.Size > 60 && extra is not null)
            extra.AsSpan(0, (int)Math.Min(extra.Length, raw.Size - 60)).CopyTo(data.AsSpan(60));
        return data;
    }

    /// <summary>Builds the complete tree. File contents stay in the image and are read on demand.</summary>
    public FsTree ReadTree(IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var tree = new FsTree();
        var hardLinks = new Dictionary<uint, FsInode>();
        var seenDirs = new HashSet<uint> { RootInode };
        long used = _inodesCount - Info.FreeInodes;
        long done = 0;

        FsInode root = BuildInode(ReadInode(RootInode), out List<DirEntry>? rootEntries);
        if (rootEntries is null) throw new FsException("Corrupt ext image: the root inode is not a directory.");
        tree.Root.Inode.Mode = root.Mode;
        tree.Root.Inode.Uid = root.Uid;
        tree.Root.Inode.Gid = root.Gid;
        tree.Root.Inode.MTime = root.MTime;
        tree.Root.Inode.XAttrs = root.XAttrs;

        var stack = new Stack<(FsNode Dir, List<DirEntry> Entries)>();
        stack.Push((tree.Root, rootEntries));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            (FsNode dir, List<DirEntry> entries) = stack.Pop();
            foreach (DirEntry e in entries)
            {
                if (e.Name.Contains('/') || dir.Find(e.Name) is not null)
                {
                    Warnings.Add($"Invalid or duplicate directory entry '{e.Name}' in '{dir.Path}' was skipped.");
                    continue;
                }
                if (hardLinks.TryGetValue(e.Inode, out FsInode? known))
                {
                    tree.Add(dir, e.Name, known);
                    continue;
                }
                FsInode inode;
                List<DirEntry>? sub;
                try
                {
                    RawInode raw = ReadInode(e.Inode);
                    if (raw.Mode == 0 || raw.Links == 0 && Le.U32(raw.Raw, 0x14) != 0)
                    {
                        Warnings.Add($"'{dir.Path}/{e.Name}' points to a deleted inode and was skipped.");
                        continue;
                    }
                    inode = BuildInode(raw, out sub);
                }
                catch (FsException ex)
                {
                    Warnings.Add($"'{dir.Path}/{e.Name}' was skipped: {ex.Message}");
                    continue;
                }
                if (inode.Type == FsNodeType.Directory)
                {
                    if (!seenDirs.Add(e.Inode))
                    {
                        Warnings.Add($"Directory '{dir.Path}/{e.Name}' is linked more than once and was skipped.");
                        continue;
                    }
                }
                else
                {
                    hardLinks.Add(e.Inode, inode);
                }
                FsNode node = tree.Add(dir, e.Name, inode);
                if (sub is not null) stack.Push((node, sub));
                if ((++done & 0x3FF) == 0) progress?.Report(new ProgressInfo("read", done, used, node.Path));
            }
        }
        progress?.Report(new ProgressInfo("read", done, done));
        return tree;
    }

    public void Dispose()
    {
        if (_ownsSource) _image.Dispose();
    }
}
