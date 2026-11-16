using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Compression;
using FsForge.Core.Compression;
using FsForge.Core.Ext;
using static FsForge.Core.Erofs.ErofsFormat;

namespace FsForge.Core.Erofs;

/// <summary>
/// Reader for EROFS images as mkfs.erofs makes them: compact and extended inodes, inline tails,
/// chunk-based files, files compressed with LZ4, MicroLZMA, DEFLATE and Zstandard (compact and full
/// cluster indexes, big clusters, inline tail clusters, fragments in the packed inode, deduplicated
/// clusters), shared and inline extended attributes with long name prefixes.
/// </summary>
public sealed class ErofsReader : IDisposable
{
    /// <summary>The longest piece of a file one compressed cluster may stand for (the kernel stops at 12 MiB).</summary>
    private const long MaxDecodedExtent = 64L << 20;

    private readonly IBlockSource _source;
    private readonly bool _ownsSource;
    private readonly ConcurrentBag<LzmaDecoder> _lzma = new();
    private readonly object _packedLock = new();

    private int _blkBits, _blkSize;
    private long _metaStart, _xattrStart;
    private ulong _rootNid, _packedNid;
    private long _epoch;
    private uint _compat, _incompat;
    private int _algorithms;                    // bit mask of the compression methods of the superblock
    private (int Base, byte[] Infix)[] _prefixes = Array.Empty<(int, byte[])>();
    private ExtentReader? _packed;

    private ErofsReader(IBlockSource source, bool ownsSource)
    {
        _source = source;
        _ownsSource = ownsSource;
        Info = null!;
    }

    public ErofsInfo Info { get; private set; }

    /// <summary>Non-fatal problems found while reading.</summary>
    public List<string> Warnings { get; } = new();

    // ---------------------------------------------------------- detection

    /// <summary>An EROFS superblock whose root inode is a directory inside the file.</summary>
    public static bool IsErofs(IBlockSource source)
    {
        if (source.Length < SuperOffset + SuperSize) return false;
        Span<byte> sb = stackalloc byte[SuperSize];
        source.ReadExactly(SuperOffset, sb);
        if (Le.U32(sb, 0) != Magic) return false;
        int bits = sb[12];
        if (bits is < 9 or > 16) return false;
        // The 16-bit root inode number; an image of the 48-bit layout keeps it elsewhere and is refused when opened.
        if ((Le.U32(sb, 80) & Incompat48Bit) != 0) return true;
        long root = ((long)Le.U32(sb, 40) << bits) + ((long)Le.U16(sb, 14) << SlotBits);
        if (root + InodeCompactSize > source.Length) return false;
        Span<byte> inode = stackalloc byte[8];
        source.ReadExactly(root, inode);
        return (Le.U16(inode, 0) & ~0x1F) == 0 && (Le.U16(inode, 4) & UnixMode.S_IFMT) == UnixMode.S_IFDIR;
    }

    public static ErofsReader Open(IBlockSource source, bool ownsSource = true)
    {
        var r = new ErofsReader(source, ownsSource);
        try
        {
            r.ReadSuperblock();
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
        if (_source.Length < SuperOffset + SuperSize) throw new FsException("The file is too small to be an EROFS image.");
        byte[] sb = _source.ReadBytes(SuperOffset, SuperSize);
        if (Le.U32(sb, 0) != Magic) throw new FsException("Not an EROFS image (bad magic).");
        _blkBits = sb[12];
        if (_blkBits is < 9 or > 16) throw new FsException($"Corrupt EROFS superblock: invalid block size (2^{_blkBits}).");
        _blkSize = 1 << _blkBits;
        _compat = Le.U32(sb, 8);
        _incompat = Le.U32(sb, 80);

        if ((_incompat & Incompat48Bit) != 0)
            throw new FsException("The EROFS image uses the 48-bit layout (mkfs.erofs -E48bit, Linux 6.15), which is not supported.");
        if ((_incompat & IncompatMetabox) != 0)
            throw new FsException("The EROFS image keeps its metadata compressed (mkfs.erofs -Emetabox, Linux 6.17), which is not supported.");
        if ((_incompat & ~IncompatSupported) != 0)
            throw new FsException($"The EROFS image uses features this program does not know (incompatible feature flags 0x{_incompat & ~IncompatSupported:X}).");
        if (sb[90] != 0) throw new FsException("The EROFS image uses directory blocks of their own size, which is not supported.");

        int sbSize = SuperSize + sb[13] * 16;
        _rootNid = Le.U16(sb, 14);
        _epoch = (long)Le.U64(sb, 24);
        long blocks = Le.U32(sb, 36);
        _metaStart = (long)Le.U32(sb, 40) << _blkBits;
        _xattrStart = (long)Le.U32(sb, 44) << _blkBits;
        _packedNid = Le.U64(sb, 96);
        int extraDevices = (_incompat & IncompatDeviceTable) != 0 ? Le.U16(sb, 86) : 0;
        if (extraDevices > 0)
            throw new FsException($"The EROFS image keeps file data on {extraDevices} more device(s) (blob files), which are not part of this file.");

        if ((_compat & CompatSbChecksum) != 0 && !ChecksumMatches())
            Warnings.Add("The checksum of the EROFS superblock does not match: the image is damaged or was changed without updating it. The kernel refuses to mount such an image.");
        if (blocks << _blkBits > _source.Length + _blkSize)
            Warnings.Add("The file is shorter than the EROFS filesystem it holds; reading the missing part fails.");

        // Compression methods and their parameters.
        int lz4MaxBlocks = 1, lzmaDict = 0;
        if ((_incompat & IncompatComprCfgs) != 0)
        {
            _algorithms = Le.U16(sb, 84);
            if ((_algorithms & ~((1 << AlgMax) - 1)) != 0)
                throw new FsException($"The EROFS image uses a compression method this program does not know (methods 0x{_algorithms:X}).");
            long pos = SuperOffset + sbSize;
            for (int alg = 0; alg < AlgMax; alg++)
            {
                if ((_algorithms & (1 << alg)) == 0) continue;
                byte[] cfg = ReadRecord(_source, ref pos);
                if (alg == AlgLz4 && cfg.Length >= 4) lz4MaxBlocks = Math.Max(1, (int)Le.U16(cfg, 2));
                if (alg == AlgLzma && cfg.Length >= 6)
                {
                    lzmaDict = (int)Math.Min(Le.U32(cfg, 0), int.MaxValue);
                    if (Le.U16(cfg, 4) != 0) throw new FsException("The EROFS image uses an LZMA variant this program does not know.");
                }
            }
        }
        else if (Le.U16(sb, 84) != 0 || (_incompat & IncompatZeroPadding) != 0)
        {
            _algorithms = 1 << AlgLz4;
        }

        ReadXattrPrefixes(sb[91], (long)Le.U32(sb, 92) << 2);

        var algorithms = new List<ErofsCompression>();
        for (int alg = 0; alg < AlgMax; alg++)
            if ((_algorithms & (1 << alg)) != 0) algorithms.Add(Algorithm(alg));
        int nameLength = sb.AsSpan(64, 16).IndexOf((byte)0);
        Info = new ErofsInfo
        {
            BlockSize = _blkSize,
            Blocks = blocks,
            InodeCount = (long)Math.Min(Le.U64(sb, 16), long.MaxValue),
            BuildTime = _epoch,
            Uuid = new Guid(sb.AsSpan(48, 16), bigEndian: true),
            VolumeName = PosixName.Decode(sb.AsSpan(64, nameLength < 0 ? 16 : nameLength)),
            FeatureCompat = _compat,
            FeatureIncompat = _incompat,
            Algorithms = algorithms,
            Compression = algorithms.Count > 0 ? algorithms[0] : ErofsCompression.None,
            PclusterSize = lz4MaxBlocks * _blkSize,
            BigPcluster = lz4MaxBlocks > 1,
            LzmaDictionarySize = lzmaDict,
            Fragments = (_incompat & IncompatFragments) != 0 && _packedNid != 0,
            Deduplicated = (_incompat & IncompatFragments) != 0 && _packedNid == 0,
        };
    }

    private bool ChecksumMatches()
    {
        int length = _blkSize > SuperOffset ? _blkSize - SuperOffset : _blkSize;
        if (SuperOffset + length > _source.Length) return false;
        byte[] data = _source.ReadBytes(SuperOffset, length);
        uint stored = Le.U32(data, 4);
        Le.W32(data, 4, 0);
        return ExtJournal.Crc32c(~0u, data) == stored;
    }

    /// <summary>A length-prefixed record: aligned to four bytes, a 16-bit length, the data.</summary>
    private static byte[] ReadRecord(IBlockSource source, ref long pos)
    {
        pos = (pos + 3) & ~3L;
        int length = Le.U16(source.ReadBytes(pos, 2), 0);
        if (length == 0) throw new FsException("Corrupt EROFS image: an empty metadata record.");
        byte[] data = source.ReadBytes(pos + 2, length);
        pos += 2 + length;
        return data;
    }

    private void ReadXattrPrefixes(int count, long pos)
    {
        if (count == 0) return;
        // The table lives in the packed inode when there is one, otherwise in the image itself.
        IBlockSource table = _source;
        if (_packedNid != 0 && (_compat & CompatPlainXattrPrefixes) == 0) table = new PackedSource(this);
        _prefixes = new (int, byte[])[count];
        for (int i = 0; i < count; i++)
        {
            byte[] record = ReadRecord(table, ref pos);
            if (record.Length > 256) throw new FsException("Corrupt EROFS image: an attribute name prefix is too long.");
            _prefixes[i] = (record[0], record[1..]);
        }
    }

    /// <summary>The content of the packed inode as a block source.</summary>
    private sealed class PackedSource(ErofsReader reader) : IBlockSource
    {
        public long Length => reader.Packed().Length;

        public int Read(long offset, Span<byte> buffer)
        {
            long left = Length - offset;
            if (offset < 0 || left <= 0) return 0;
            if (buffer.Length > left) buffer = buffer[..(int)left];
            reader.ReadPacked(offset, buffer);
            return buffer.Length;
        }

        public void Dispose() { }
    }

    // --------------------------------------------------------------- inodes

    private sealed class Ino
    {
        public ulong Nid;
        public long Pos;                // where the inode starts in the image
        public int Layout, InodeSize, XattrSize;
        public int Mode;
        public uint Uid, Gid;
        public long MTime, Size;
        public uint Union;              // i_u: start block, compressed blocks, device number or chunk format

        public long End => Pos + InodeSize + XattrSize;
    }

    private byte[] Meta(long pos, long count)
    {
        if (pos < 0 || count < 0 || count > int.MaxValue - 16 || pos + count > _source.Length)
            throw new FsException("Corrupt EROFS image: metadata lies outside the file.");
        return _source.ReadBytes(pos, (int)count);
    }

    private Ino ReadInode(ulong nid)
    {
        if (nid >> 58 != 0) throw new FsException($"Corrupt EROFS image: invalid inode number {nid}.");
        long pos = _metaStart + ((long)nid << SlotBits);
        if (pos + InodeCompactSize > _source.Length) throw new FsException($"Corrupt EROFS image: inode {nid} lies outside the file.");
        byte[] b = Meta(pos, Math.Min(InodeExtendedSize, _source.Length - pos));
        int format = Le.U16(b, 0);
        if ((format & ~0x1F) != 0) throw new FsException($"The EROFS inode {nid} has a format this program does not know (0x{format:X}).");
        var ino = new Ino { Nid = nid, Pos = pos, Layout = (format >> 1) & 7, Mode = Le.U16(b, 4), Union = Le.U32(b, 16) };
        if (ino.Layout > LayoutChunkBased) throw new FsException($"The EROFS inode {nid} has a data layout this program does not know ({ino.Layout}).");
        int icount = Le.U16(b, 2);
        ino.XattrSize = icount == 0 ? 0 : XattrHeaderSize + 4 * (icount - 1);
        if ((format & 1) != 0)
        {
            if (b.Length < InodeExtendedSize) throw new FsException($"Corrupt EROFS image: inode {nid} lies outside the file.");
            ino.InodeSize = InodeExtendedSize;
            ino.Size = (long)Le.U64(b, 8);
            ino.Uid = Le.U32(b, 24);
            ino.Gid = Le.U32(b, 28);
            ino.MTime = (long)Le.U64(b, 32);
        }
        else
        {
            ino.InodeSize = InodeCompactSize;
            ino.Size = Le.U32(b, 8);
            ino.Uid = Le.U16(b, 24);
            ino.Gid = Le.U16(b, 26);
            // The time of the image, plus (newer mkfs) a difference of the inode's own.
            ino.MTime = _epoch + Le.U32(b, 12);
        }
        if (ino.Size < 0) throw new FsException($"Corrupt EROFS image: inode {nid} has an invalid size.");
        return ino;
    }

    private SortedDictionary<string, byte[]>? ReadXattrs(Ino ino, string path)
    {
        if (ino.XattrSize == 0) return null;
        if (ino.XattrSize < XattrHeaderSize) throw new FsException("Corrupt EROFS image: invalid extended attribute area.");
        byte[] body = Meta(ino.Pos + ino.InodeSize, ino.XattrSize);
        int shared = body[4];
        int pos = XattrHeaderSize + 4 * shared;
        if (pos > body.Length) throw new FsException("Corrupt EROFS image: invalid extended attribute area.");
        var result = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);

        void Add(ReadOnlySpan<byte> entry, ReadOnlySpan<byte> rest)
        {
            int nameLength = entry[0], index = entry[1], valueLength = Le.U16(entry, 2);
            string? prefix;
            ReadOnlySpan<byte> infix = default;
            if ((index & XattrLongPrefix) != 0)
            {
                int p = index & ~XattrLongPrefix;
                if (p >= _prefixes.Length)
                {
                    Warnings.Add($"An extended attribute of '{path}' uses an unknown name prefix and was skipped.");
                    return;
                }
                infix = _prefixes[p].Infix;
                index = _prefixes[p].Base;
            }
            prefix = index < XattrPrefixes.Length ? XattrPrefixes[index] : null;
            if (prefix is null)
            {
                Warnings.Add($"An extended attribute of '{path}' is of an unknown namespace ({index}) and was skipped.");
                return;
            }
            string name = prefix + PosixName.Decode(infix) + PosixName.Decode(rest[..nameLength]);
            result[name] = rest.Slice(nameLength, valueLength).ToArray();
        }

        // Inline entries.
        while (pos + 4 <= body.Length)
        {
            int size = 4 + body[pos] + Le.U16(body, pos + 2);
            if (pos + size > body.Length) throw new FsException("Corrupt EROFS image: an extended attribute runs past its area.");
            Add(body.AsSpan(pos, 4), body.AsSpan(pos + 4, size - 4));
            pos += (size + 3) & ~3;
        }
        // Entries of the shared area, by their offset in units of four bytes.
        for (int i = 0; i < shared; i++)
        {
            long at = _xattrStart + (long)Le.U32(body, XattrHeaderSize + 4 * i) * 4;
            byte[] head = Meta(at, 4);
            Add(head, Meta(at + 4, head[0] + Le.U16(head, 2)));
        }
        return result.Count > 0 ? result : null;
    }

    // ----------------------------------------------------------------- tree

    /// <summary>Builds the complete tree. File contents stay in the image and are read on demand.</summary>
    public FsTree ReadTree(IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var tree = new FsTree();
        Ino root = ReadInode(_rootNid);
        if ((root.Mode & UnixMode.S_IFMT) != UnixMode.S_IFDIR) throw new FsException("Corrupt EROFS image: the root inode is not a directory.");
        FsInode r = tree.Root.Inode;
        r.Mode = root.Mode & 0xFFF;
        r.Uid = root.Uid;
        r.Gid = root.Gid;
        r.MTime = root.MTime;
        r.XAttrs = ReadXattrs(root, "/");

        var hardLinks = new Dictionary<ulong, FsInode>();
        var seenDirs = new HashSet<ulong> { _rootNid };
        var stack = new Stack<(FsNode Node, Ino Ino)>();
        stack.Push((tree.Root, root));
        long done = 0;

        // What the compressed files look like: the first of them are examined.
        var algorithmUse = new int[AlgMax];
        int sampled = 0, maxPcluster = 0;
        bool big = false, partial = false;

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            (FsNode dir, Ino dirIno) = stack.Pop();
            foreach ((string name, ulong nid, bool valid) in ReadDirectory(dirIno, dir.Path))
            {
                if (name is "." or "..") continue;
                string path = dir.IsRoot ? "/" + name : dir.Path + "/" + name;
                if (!valid || name.Length == 0 || name.Contains('/') || name.Contains('\0') || dir.Find(name) is not null)
                {
                    Warnings.Add($"Invalid or duplicate directory entry '{name}' in '{dir.Path}' was skipped.");
                    continue;
                }
                Ino ino = ReadInode(nid);
                FsNodeType? type = UnixMode.TypeFromMode(ino.Mode);
                if (type is null)
                {
                    Warnings.Add($"'{path}' has an unknown file type (mode {Convert.ToString(ino.Mode, 8)}) and was skipped.");
                    continue;
                }
                if (type == FsNodeType.Directory)
                {
                    if (!seenDirs.Add(nid)) throw new FsException("Corrupt EROFS image: a directory is referenced more than once.");
                }
                else if (hardLinks.TryGetValue(nid, out FsInode? known))
                {
                    tree.Add(dir, name, known);
                    continue;
                }

                var inode = new FsInode { Type = type.Value, Mode = ino.Mode & 0xFFF, Uid = ino.Uid, Gid = ino.Gid, MTime = ino.MTime };
                switch (type.Value)
                {
                    case FsNodeType.File:
                        if (ino.Size > 0) inode.Data = new ErofsFileData(this, ino);
                        if (ino.Size > 0 && sampled < 48 && ino.Layout is LayoutCompressedFull or LayoutCompressedCompact)
                        {
                            sampled++;
                            foreach (Extent e in BuildExtents(ino))
                            {
                                if (e.Kind != ExtentKind.Encoded || e.Inline) continue;
                                if (e.Algorithm < AlgMax) algorithmUse[e.Algorithm]++;
                                maxPcluster = Math.Max(maxPcluster, e.PhysicalLength);
                                big |= e.PhysicalLength > _blkSize;
                                partial |= e.Partial;
                            }
                        }
                        break;
                    case FsNodeType.Symlink:
                        if (ino.Size is <= 0 or > 65536) throw new FsException($"Corrupt EROFS image: the symbolic link '{path}' has an invalid length.");
                        inode.Mode = 0x1FF;
                        inode.LinkTarget = PosixName.Decode(ReadAll(ino));
                        break;
                    case FsNodeType.CharDevice or FsNodeType.BlockDevice:
                        (inode.DevMajor, inode.DevMinor) = DecodeDev(ino.Union);
                        break;
                }
                inode.XAttrs = ReadXattrs(ino, path);
                if (type != FsNodeType.Directory) hardLinks.Add(nid, inode);
                FsNode node = tree.Add(dir, name, inode);
                if (type == FsNodeType.Directory) stack.Push((node, ino));
                if ((++done & 0x3FF) == 0) progress?.Report(new ProgressInfo("read", done, Info.InodeCount, path));
            }
        }
        progress?.Report(new ProgressInfo("read", done, done));

        int most = Array.IndexOf(algorithmUse, algorithmUse.Max());
        Info = new ErofsInfo
        {
            BlockSize = Info.BlockSize, Blocks = Info.Blocks, InodeCount = Info.InodeCount, BuildTime = Info.BuildTime,
            Uuid = Info.Uuid, VolumeName = Info.VolumeName, FeatureCompat = Info.FeatureCompat, FeatureIncompat = Info.FeatureIncompat,
            Algorithms = Info.Algorithms, LzmaDictionarySize = Info.LzmaDictionarySize, Fragments = Info.Fragments,
            Compression = algorithmUse[most] > 0 ? Algorithm(most) : sampled > 0 || Info.Algorithms.Count == 0 ? ErofsCompression.None : Info.Compression,
            PclusterSize = Math.Max(maxPcluster, Info.Algorithms.Contains(ErofsCompression.Lz4) || maxPcluster == 0 ? Info.PclusterSize : 0),
            BigPcluster = Info.BigPcluster || big,
            Deduplicated = Info.Deduplicated || partial,
        };
        return tree;
    }

    /// <summary>The entries of a directory: name, inode number and whether the entry is well-formed.</summary>
    private List<(string Name, ulong Nid, bool Valid)> ReadDirectory(Ino ino, string path)
    {
        var entries = new List<(string, ulong, bool)>();
        if (ino.Size == 0) return entries;
        if (ino.Size > _source.Length) throw new FsException($"Corrupt EROFS image: the directory '{path}' is larger than the image.");
        byte[] data = ReadAll(ino);
        for (int start = 0; start < data.Length; start += _blkSize)
        {
            ReadOnlySpan<byte> block = data.AsSpan(start, Math.Min(_blkSize, data.Length - start));
            if (block.Length < DirentSize) throw new FsException($"Corrupt EROFS image: the directory '{path}' has a truncated block.");
            int first = Le.U16(block, 8);
            int count = first / DirentSize;
            if (first < DirentSize || first % DirentSize != 0 || first > block.Length)
                throw new FsException($"Corrupt EROFS image: the directory '{path}' has an invalid entry table.");
            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> e = block.Slice(i * DirentSize, DirentSize);
                int from = Le.U16(e, 8);
                int to = i + 1 < count ? Le.U16(block, (i + 1) * DirentSize + 8) : block.Length;
                if (from < first || to < from || to > block.Length)
                    throw new FsException($"Corrupt EROFS image: the directory '{path}' has an entry whose name lies outside its block.");
                ReadOnlySpan<byte> name = block[from..to];
                // The last name of a block runs to the end of the block or to a NUL.
                int nul = name.IndexOf((byte)0);
                if (nul >= 0) name = name[..nul];
                entries.Add((PosixName.Decode(name), Le.U64(e, 0), name.Length is > 0 and <= 255));
            }
        }
        return entries;
    }

    // -------------------------------------------------------------- extents

    private enum ExtentKind : byte
    {
        /// <summary>Nothing is stored: zeros.</summary>
        Hole,

        /// <summary>The bytes lie in the image as they are.</summary>
        Raw,

        /// <summary>A compressed cluster (or an "interlaced" plain one) that is unpacked as a whole.</summary>
        Encoded,

        /// <summary>The bytes lie in the packed inode.</summary>
        Fragment,
    }

    private const byte AlgInterlaced = 255;

    /// <summary>A piece of a file and where its bytes come from.</summary>
    private readonly record struct Extent(long Start, long Length, ExtentKind Kind, long Physical, int PhysicalLength = 0,
        byte Algorithm = 0, bool Partial = false, bool Inline = false)
    {
        public long End => Start + Length;
    }

    private Extent[] BuildExtents(Ino ino) => ino.Layout switch
    {
        LayoutFlatPlain or LayoutFlatInline => FlatExtents(ino),
        LayoutChunkBased => ChunkExtents(ino),
        _ => CompressedExtents(ino),
    };

    private Extent[] FlatExtents(Ino ino)
    {
        long size = ino.Size;
        bool inline = ino.Layout == LayoutFlatInline;
        if (!inline && ino.Union == NullAddr) return new[] { new Extent(0, size, ExtentKind.Hole, 0) };
        long blocks = (size + _blkSize - 1) >> _blkBits;
        long inBlocks = Math.Min(size, (blocks - (inline ? 1 : 0)) << _blkBits);
        var list = new List<Extent>(2);
        if (inBlocks > 0) list.Add(new Extent(0, inBlocks, ExtentKind.Raw, (long)ino.Union << _blkBits));
        if (size > inBlocks)
        {
            // The tail follows the inode and must not cross a block boundary.
            if ((ino.End & (_blkSize - 1)) + (size - inBlocks) > _blkSize)
                throw new FsException("Corrupt EROFS image: inline data crosses a block boundary.");
            list.Add(new Extent(inBlocks, size - inBlocks, ExtentKind.Raw, ino.End, Inline: true));
        }
        return list.ToArray();
    }

    private Extent[] ChunkExtents(Ino ino)
    {
        int format = (int)(ino.Union & 0xFFFF);
        if ((format & ~(ChunkBlkBitsMask | ChunkIndexes)) != 0)
            throw new FsException($"The EROFS inode {ino.Nid} uses a chunk format this program does not know (0x{format:X}).");
        int chunkBits = _blkBits + (format & ChunkBlkBitsMask);
        if (chunkBits > 40) throw new FsException("Corrupt EROFS image: invalid chunk size.");
        int unit = (format & ChunkIndexes) != 0 ? 8 : 4;
        long chunkSize = 1L << chunkBits;
        long chunks = (ino.Size + chunkSize - 1) >> chunkBits;
        byte[] table = Meta((ino.End + unit - 1) / unit * unit, chunks * unit);
        var list = new List<Extent>();
        for (long i = 0; i < chunks; i++)
        {
            int at = (int)(i * unit);
            uint block = unit == 8 ? Le.U32(table, at + 4) : Le.U32(table, at);
            if (unit == 8 && block != NullAddr && Le.U16(table, at + 2) != 0)
                throw new FsException("The EROFS image keeps file data on another device (a blob file), which is not part of this file.");
            long start = i << chunkBits, length = Math.Min(chunkSize, ino.Size - start);
            Extent e = block == NullAddr
                ? new Extent(start, length, ExtentKind.Hole, 0)
                : new Extent(start, length, ExtentKind.Raw, (long)block << _blkBits);
            // Chunks that follow each other in the image, and runs of holes, become one extent.
            if (list.Count > 0 && list[^1] is { } last && last.Kind == e.Kind && (e.Kind == ExtentKind.Hole || last.Physical + last.Length == e.Physical))
                list[^1] = last with { Length = last.Length + length };
            else
                list.Add(e);
        }
        return list.ToArray();
    }

    /// <summary>The start of a compressed extent: a "head" cluster of the index.</summary>
    private struct Head
    {
        public long Start;
        public int Type;
        public uint Block;
        public int Blocks;              // of the compressed cluster; 0 when the index does not say (one block)
        public bool Partial;
    }

    private Extent[] CompressedExtents(Ino ino)
    {
        long size = ino.Size;
        long headerPos = (ino.End + 7) & ~7L;
        byte[] h = Meta(headerPos, 8);
        if ((h[7] & 0x80) != 0)
        {
            // The whole file is a fragment: the header is its offset in the packed inode.
            return new[] { new Extent(0, size, ExtentKind.Fragment, (long)(Le.U64(h, 0) & ~(1UL << 63))) };
        }
        int advise = Le.U16(h, 4), algorithms = h[6];
        int clusterBits = _blkBits + (h[7] & 15);
        if (clusterBits > 24) throw new FsException("Corrupt EROFS image: invalid cluster size.");
        long clusterSize = 1L << clusterBits;
        long total = (size + clusterSize - 1) >> clusterBits;
        bool big1 = (advise & AdviseBigPcluster1) != 0, big2 = (advise & AdviseBigPcluster2) != 0;
        bool fragment = (advise & AdviseFragmentPcluster) != 0;
        int inlineSize = !fragment && (advise & AdviseInlinePcluster) != 0 ? Le.U16(h, 2) : 0;

        var heads = new List<Head>();
        bool afterHead = false;         // the previous cluster was a head: this one may carry its block count
        long inlinePos;

        void Cluster(long lcn, int type, int lo, uint block, bool partial)
        {
            if (type == ClusterNonHead)
            {
                if (heads.Count == 0) throw new FsException("Corrupt EROFS image: a compressed file does not start with a head cluster.");
                if (afterHead && (lo & D0BlockCount) != 0)
                {
                    Head head = heads[^1];
                    if (head.Type == ClusterHead1 ? big1 : big2)
                    {
                        head.Blocks = lo & ~D0BlockCount;
                        heads[^1] = head;
                    }
                }
                afterHead = false;
                return;
            }
            if (lo >= clusterSize) throw new FsException("Corrupt EROFS image: invalid offset in a cluster index.");
            long start = (lcn << clusterBits) + lo;
            if (heads.Count > 0 && start < heads[^1].Start) throw new FsException("Corrupt EROFS image: the cluster index is not in order.");
            heads.Add(new Head { Start = start, Type = type, Block = block, Partial = partial });
            afterHead = true;
        }

        if (ino.Layout == LayoutCompressedFull)
        {
            if ((advise & 1) != 0)
                throw new FsException("The EROFS image describes compressed files with extent records (mkfs.erofs 1.9), which is not supported.");
            byte[] index = Meta(headerPos + 16, total * 8);
            for (long lcn = 0; lcn < total; lcn++)
            {
                int at = (int)(lcn * 8);
                int word = Le.U16(index, at), type = word & 3;
                if (type == ClusterNonHead) Cluster(lcn, type, Le.U16(index, at + 4), 0, false);
                else Cluster(lcn, type, Le.U16(index, at + 2), Le.U32(index, at + 4), (word & PartialRef) != 0);
            }
            inlinePos = headerPos + 16 + total * 8;
        }
        else
        {
            if (clusterBits > 14) throw new FsException("Corrupt EROFS image: compact cluster indexes with a cluster size above 16 KiB.");
            // Packs of two clusters in eight bytes, and - where they can be aligned to 32 bytes - of sixteen in 32.
            long indexPos = headerPos + 8;
            long initial = ((32 - indexPos % 32) / 4) & 7;
            long twoByte = 0;
            if ((advise & AdviseCompacted2B) != 0 && initial < total)
            {
                if (clusterBits > 12) throw new FsException("Corrupt EROFS image: 2-byte compact cluster indexes with a cluster size above 4 KiB.");
                twoByte = (total - initial) / 16 * 16;
            }
            long first = Math.Min(initial, total), last = total - first - twoByte;
            long bytes = (first + 1) / 2 * 8 + twoByte * 2 + (last + 1) / 2 * 8;
            byte[] index = new byte[bytes + 8];
            Meta(indexPos, bytes).CopyTo(index, 0);
            int loBits = Math.Max(clusterBits, 12);
            bool big = big1;

            long lcn = 0, offset = 0;
            void Packs(long count, int perPack, int packBytes)
            {
                int bits = (packBytes - 4) * 8 / perPack;
                (int Lo, int Type) Entry(long pack, int i)
                {
                    int bit = bits * i;
                    uint v = Le.U32(index, (int)(pack + bit / 8)) >> (bit & 7);
                    return ((int)(v & ((1u << loBits) - 1)), (int)((v >> loBits) & 3));
                }
                for (long done = 0; done < count; done += perPack, offset += packBytes)
                {
                    long pack = offset;
                    uint baseBlock = Le.U32(index, (int)(pack + packBytes - 4));
                    for (int i = 0; i < perPack && done + i < count; i++, lcn++)
                    {
                        (int lo, int type) = Entry(pack, i);
                        if (type == ClusterNonHead)
                        {
                            Cluster(lcn, type, lo, 0, false);
                            continue;
                        }
                        // The block of a head: the base of the pack plus the blocks of the heads before it in the pack.
                        uint blocks;
                        int j = i;
                        if (!big)
                        {
                            blocks = 1;
                            while (j > 0)
                            {
                                (int l, int t) = Entry(pack, --j);
                                if (t == ClusterNonHead) j -= l;
                                if (j >= 0) blocks++;
                            }
                        }
                        else
                        {
                            blocks = 0;
                            while (j > 0)
                            {
                                (int l, int t) = Entry(pack, --j);
                                if (t != ClusterNonHead) blocks++;
                                else if ((l & D0BlockCount) != 0)
                                {
                                    j--;
                                    blocks += (uint)(l & ~D0BlockCount);
                                }
                                else
                                {
                                    if (l <= 1) throw new FsException("Corrupt EROFS image: invalid distance in a compact cluster index.");
                                    j -= l - 2;
                                }
                            }
                        }
                        Cluster(lcn, type, lo, baseBlock + blocks, false);
                    }
                }
            }
            Packs(first, 2, 8);
            Packs(twoByte, 16, 32);
            Packs(last, 2, 8);
            inlinePos = indexPos + bytes;
        }

        // From heads to extents: each one runs to the next head, the last one to the end of the file.
        var list = new List<Extent>(heads.Count);
        for (int i = 0; i < heads.Count; i++)
        {
            Head head = heads[i];
            long end = Math.Min(i + 1 < heads.Count ? heads[i + 1].Start : size, size);
            long length = end - head.Start;
            if (length <= 0) continue;
            if (length > MaxDecodedExtent)
                throw new FsException($"The EROFS image has a compressed cluster that stands for {length >> 20} MiB of data, which is more than this program unpacks at once.");
            int physical = Math.Max(head.Blocks, 1) << _blkBits;
            if (head.Blocks > MaxPclusterSize >> 9) throw new FsException("Corrupt EROFS image: a compressed cluster is larger than 1 MiB.");
            byte algorithm = (byte)(head.Type == ClusterHead2 ? algorithms >> 4 : algorithms & 15);
            if (head.Type == ClusterPlain)
            {
                if (length > physical) throw new FsException("Corrupt EROFS image: an uncompressed cluster is shorter than its data.");
                bool interlaced = (advise & AdviseInterlacedPcluster) != 0;
                list.Add(interlaced
                    ? new Extent(head.Start, length, ExtentKind.Encoded, (long)head.Block << _blkBits, physical, AlgInterlaced)
                    : new Extent(head.Start, length, ExtentKind.Raw, (long)head.Block << _blkBits));
            }
            else
            {
                if (algorithm >= AlgMax || (_algorithms & (1 << algorithm)) == 0)
                    throw new FsException($"Corrupt EROFS image: a file uses the compression method {AlgorithmName(algorithm)}, which the superblock does not announce.");
                list.Add(new Extent(head.Start, length, ExtentKind.Encoded, (long)head.Block << _blkBits, physical, algorithm, head.Partial));
            }
        }
        if (list.Count == 0) throw new FsException("Corrupt EROFS image: a compressed file has no clusters.");

        // The tail of the file may live next to the inode or in the packed inode instead of a cluster of its own.
        Extent tail = list[^1];
        if (fragment)
        {
            long offset = Le.U32(h, 0);
            if (ino.Layout == LayoutCompressedFull) offset |= (tail.Physical >> _blkBits) << 32;
            list[^1] = new Extent(tail.Start, tail.Length, ExtentKind.Fragment, offset);
        }
        else if (inlineSize > 0)
        {
            if ((inlinePos & (_blkSize - 1)) + inlineSize > _blkSize)
                throw new FsException("Corrupt EROFS image: an inline cluster crosses a block boundary.");
            if (tail.Kind == ExtentKind.Raw && tail.Length > inlineSize)
                throw new FsException("Corrupt EROFS image: an inline cluster is shorter than its data.");
            list[^1] = tail with { Physical = inlinePos, PhysicalLength = inlineSize, Inline = true };
        }
        return list.ToArray();
    }

    // ------------------------------------------------------------- decoding

    /// <summary>Unpacks an encoded extent into <paramref name="dst"/>, which has the length of the extent.</summary>
    private void Decode(in Extent e, Span<byte> dst)
    {
        byte[] raw = ArrayPool<byte>.Shared.Rent(e.PhysicalLength);
        try
        {
            Span<byte> input = raw.AsSpan(0, e.PhysicalLength);
            _source.ReadExactly(e.Physical, input);
            if (e.Algorithm == AlgInterlaced)
            {
                // The block is rotated so that the data lies where it would in a block-aligned file.
                int skip = (int)(e.Start & (_blkSize - 1));
                int right = Math.Min(_blkSize - skip, dst.Length);
                int from = e.PhysicalLength - (_blkSize - skip);
                if (from < 0) throw new FsException("Corrupt EROFS image: invalid interlaced cluster.");
                input.Slice(from, right).CopyTo(dst);
                input[..(dst.Length - right)].CopyTo(dst[right..]);
                return;
            }

            // The data sits at the end of the cluster; zeros fill the space in front of it.
            int margin = input.IndexOfAnyExcept((byte)0);
            if (margin < 0) throw new FsException("Corrupt EROFS image: an empty compressed cluster.");
            input = input[margin..];
            int produced;
            switch (e.Algorithm)
            {
                case AlgLz4:
                    produced = Lz4FixedEncoder.Decompress(input, dst);
                    break;
                case AlgLzma:
                {
                    if (!_lzma.TryTake(out LzmaDecoder? decoder)) decoder = new LzmaDecoder();
                    try
                    {
                        decoder.SetProps((byte)~input[0]);
                        decoder.ResetDictionary();
                        produced = decoder.Decode(dst, 0, 0, dst.Length, input, microLzma: true);
                    }
                    finally
                    {
                        _lzma.Add(decoder);
                    }
                    break;
                }
                case AlgDeflate:
                {
                    using var packed = new MemoryStream(raw, margin, input.Length, writable: false);
                    using var inflate = new DeflateStream(packed, CompressionMode.Decompress);
                    produced = ReadFully(inflate, dst);
                    break;
                }
                case AlgZstd:
                {
                    using var packed = new MemoryStream(raw, margin, input.Length, writable: false);
                    using var unzstd = new ZstdSharp.DecompressionStream(packed);
                    produced = ReadFully(unzstd, dst);
                    break;
                }
                default:
                    throw new FsException($"Corrupt EROFS image: unknown compression method {e.Algorithm}.");
            }
            if (produced != dst.Length)
                throw new FsException($"Corrupt EROFS image: a compressed cluster unpacks to {produced} bytes instead of {dst.Length}.");
        }
        catch (Exception ex) when (ex is InvalidDataException or ZstdSharp.ZstdException)
        {
            throw new FsException($"Corrupt EROFS image: a {AlgorithmName(e.Algorithm)} cluster cannot be unpacked ({ex.Message}).", ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    private static int ReadFully(Stream s, Span<byte> dst)
    {
        int total = 0;
        while (total < dst.Length)
        {
            int n = s.Read(dst[total..]);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>Reads through a list of extents; keeps the cluster it unpacked last.</summary>
    private sealed class ExtentReader(ErofsReader reader, Extent[] extents, long length, bool isPacked) : IDisposable
    {
        private byte[]? _decoded;
        private int _decodedIndex = -1;
        private int _index;

        public long Length => length;

        public void Read(long offset, Span<byte> dst)
        {
            while (dst.Length > 0)
            {
                // Sequential reads continue where the last one ended; anything else is looked up.
                if (_index >= extents.Length || offset < extents[_index].Start || offset >= extents[_index].End) _index = Find(offset);
                Extent e = extents[_index];
                int n = (int)Math.Min(dst.Length, e.End - offset);
                Span<byte> part = dst[..n];
                long inside = offset - e.Start;
                switch (e.Kind)
                {
                    case ExtentKind.Hole:
                        part.Clear();
                        break;
                    case ExtentKind.Raw:
                        reader._source.ReadExactly(e.Physical + inside, part);
                        break;
                    case ExtentKind.Fragment:
                        if (isPacked) throw new FsException("Corrupt EROFS image: the packed inode refers to itself.");
                        reader.ReadPacked(e.Physical + inside, part);
                        break;
                    default:
                        if (_decodedIndex != _index)
                        {
                            _decodedIndex = -1;
                            if (_decoded is null || _decoded.Length < e.Length)
                            {
                                if (_decoded is not null) ArrayPool<byte>.Shared.Return(_decoded);
                                _decoded = ArrayPool<byte>.Shared.Rent((int)e.Length);
                            }
                            reader.Decode(e, _decoded.AsSpan(0, (int)e.Length));
                            _decodedIndex = _index;
                        }
                        _decoded.AsSpan((int)inside, n).CopyTo(part);
                        break;
                }
                dst = dst[n..];
                offset += n;
            }
        }

        private int Find(long offset)
        {
            int lo = 0, hi = extents.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                if (offset < extents[mid].Start) hi = mid - 1;
                else if (offset >= extents[mid].End) lo = mid + 1;
                else return mid;
            }
            throw new FsException("Corrupt EROFS image: a part of a file is not mapped to any data.");
        }

        public void Dispose()
        {
            if (_decoded is not null) ArrayPool<byte>.Shared.Return(_decoded);
            _decoded = null;
            _decodedIndex = -1;
        }
    }

    private ExtentReader Open(Ino ino, bool isPacked = false)
    {
        Extent[] extents = BuildExtents(ino);
        long covered = 0;
        foreach (Extent e in extents)
        {
            if (e.Start != covered) throw new FsException("Corrupt EROFS image: a part of a file is not mapped to any data.");
            covered = e.End;
        }
        if (covered != ino.Size) throw new FsException("Corrupt EROFS image: a part of a file is not mapped to any data.");
        return new ExtentReader(this, extents, ino.Size, isPacked);
    }

    private byte[] ReadAll(Ino ino)
    {
        if (ino.Size > int.MaxValue - 64) throw new FsException("Corrupt EROFS image: a directory or link is too large.");
        byte[] data = new byte[ino.Size];
        using ExtentReader r = Open(ino);
        r.Read(0, data);
        return data;
    }

    private ExtentReader Packed()
    {
        lock (_packedLock)
        {
            if (_packed is not null) return _packed;
            if (_packedNid == 0) throw new FsException("Corrupt EROFS image: a file keeps data in the packed inode, but the image has none.");
            return _packed = Open(ReadInode(_packedNid), isPacked: true);
        }
    }

    private void ReadPacked(long offset, Span<byte> dst)
    {
        ExtentReader packed = Packed();
        if (offset < 0 || offset + dst.Length > packed.Length) throw new FsException("Corrupt EROFS image: a fragment lies outside the packed inode.");
        lock (_packedLock) packed.Read(offset, dst);
    }

    // ------------------------------------------------------------ file data

    private sealed class ErofsFileData(ErofsReader reader, Ino ino) : ISparseFileData
    {
        public long Length => ino.Size;

        public long DataBytesHint
        {
            get
            {
                // Only chunk-based files have holes that are cheap to find.
                if (ino.Layout != LayoutChunkBased) return ino.Size;
                long bytes = 0;
                foreach (Extent e in reader.BuildExtents(ino))
                    if (e.Kind != ExtentKind.Hole) bytes += e.Length;
                return bytes;
            }
        }

        public Stream OpenRead() => new ErofsFileStream(reader.Open(ino));
    }

    /// <summary>Forward-only stream over the extents of a file.</summary>
    private sealed class ErofsFileStream(ExtentReader extents) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => extents.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            long left = extents.Length - _position;
            if (left <= 0 || buffer.Length == 0) return 0;
            if (buffer.Length > left) buffer = buffer[..(int)left];
            extents.Read(_position, buffer);
            _position += buffer.Length;
            return buffer.Length;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) extents.Dispose();
            base.Dispose(disposing);
        }
    }

    public void Dispose()
    {
        lock (_packedLock)
        {
            _packed?.Dispose();
            _packed = null;
        }
        if (_ownsSource) _source.Dispose();
    }
}
