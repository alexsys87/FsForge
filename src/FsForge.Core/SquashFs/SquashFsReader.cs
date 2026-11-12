using System.Collections.Concurrent;
using FsForge.Core.Compression;
using static FsForge.Core.SquashFs.SquashFsFormat;

namespace FsForge.Core.SquashFs;

/// <summary>Reader for SquashFS 4.0 images.</summary>
public sealed class SquashFsReader : IDisposable
{
    private readonly IBlockSource _source;
    private readonly bool _ownsSource;
    private readonly ConcurrentBag<IBlockCodec> _codecs = new();
    private readonly Dictionary<long, MetaBlock> _metaCache = new();
    private readonly object _fragLock = new();
    private readonly LinkedList<(uint Index, byte[] Data)> _fragCache = new();
    private const int FragCacheSize = 6;

    private long _inodeTable, _dirTable, _fragTable, _exportTable, _idTable, _xattrIdTable;
    private ulong _rootRef;
    private uint[] _ids = Array.Empty<uint>();
    private (long Start, uint Size)[] _fragments = Array.Empty<(long, uint)>();
    private long _xattrKvStart;
    private (ulong Ref, uint Count)[] _xattrIds = Array.Empty<(ulong, uint)>();

    private sealed record MetaBlock(byte[] Data, long Next);

    private SquashFsReader(IBlockSource source, bool ownsSource)
    {
        _source = source;
        _ownsSource = ownsSource;
        Info = null!;
    }

    public SquashFsInfo Info { get; private set; }

    /// <summary>Non-fatal problems found while reading (unsupported xattr namespace etc.).</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>Quick check of the superblock at the start of the source.</summary>
    public static bool IsSquashFs(IBlockSource source, long offset = 0)
    {
        if (source.Length - offset < SuperblockSize) return false;
        Span<byte> sb = stackalloc byte[SuperblockSize];
        source.ReadExactly(offset, sb);
        return LooksValid(sb, source.Length - offset);
    }

    private static bool LooksValid(ReadOnlySpan<byte> sb, long available)
    {
        if (Le.U32(sb, 0) != Magic) return false;
        if (Le.U16(sb, 28) != 4 || Le.U16(sb, 30) != 0) return false;
        uint blockSize = Le.U32(sb, 12);
        int blockLog = Le.U16(sb, 22);
        if (blockLog is < 12 or > 20 || blockSize != 1u << blockLog) return false;
        int comp = Le.U16(sb, 20);
        if (comp is < 1 or > 6) return false;
        ulong bytesUsed = Le.U64(sb, 40);
        return bytesUsed >= SuperblockSize && bytesUsed <= (ulong)available + 4096;
    }

    /// <summary>
    /// Searches a firmware blob for an embedded SquashFS superblock; returns its offset or -1.
    /// </summary>
    public static long FindEmbedded(IBlockSource source, long startOffset = 0, long maxScan = 256L << 20, CancellationToken ct = default)
    {
        long end = Math.Min(source.Length, startOffset + maxScan);
        byte[] buf = new byte[1 << 20];
        Span<byte> sb = stackalloc byte[SuperblockSize];
        for (long pos = startOffset; pos < end;)
        {
            ct.ThrowIfCancellationRequested();
            int n = source.Read(pos, buf);
            if (n < 4) break;
            for (int i = 0; i + 4 <= n; i++)
            {
                if (buf[i] != (byte)'h' || buf[i + 1] != (byte)'s' || buf[i + 2] != (byte)'q' || buf[i + 3] != (byte)'s') continue;
                long at = pos + i;
                if (source.Length - at < SuperblockSize) break;
                source.ReadExactly(at, sb);
                if (LooksValid(sb, source.Length - at)) return at;
            }
            pos += n - 3;
        }
        return -1;
    }

    public static SquashFsReader Open(IBlockSource source, bool ownsSource = true)
    {
        var reader = new SquashFsReader(source, ownsSource);
        try
        {
            reader.ReadSuperblock();
            return reader;
        }
        catch
        {
            if (ownsSource) source.Dispose();
            throw;
        }
    }

    private void ReadSuperblock()
    {
        if (_source.Length < SuperblockSize) throw new FsException("The file is too small to be a SquashFS image.");
        byte[] sb = _source.ReadBytes(0, SuperblockSize);
        uint magic = Le.U32(sb, 0);
        if (magic == MagicSwapped) throw new FsException("Big-endian or pre-4.0 SquashFS images are not supported.");
        if (magic != Magic) throw new FsException("Not a SquashFS image (bad magic).");
        int major = Le.U16(sb, 28), minor = Le.U16(sb, 30);
        if (major != 4) throw new FsException($"SquashFS version {major}.{minor} is not supported (only 4.0).");

        int blockSize = (int)Le.U32(sb, 12);
        int blockLog = Le.U16(sb, 22);
        if (blockLog is < 12 or > 20 || blockSize != 1 << blockLog) throw new FsException("Corrupt SquashFS superblock: invalid block size.");
        int compression = Le.U16(sb, 20);
        int flags = Le.U16(sb, 24);

        _rootRef = Le.U64(sb, 32);
        long bytesUsed = (long)Le.U64(sb, 40);
        _idTable = (long)Le.U64(sb, 48);
        _xattrIdTable = (long)Le.U64(sb, 56);
        _inodeTable = (long)Le.U64(sb, 64);
        _dirTable = (long)Le.U64(sb, 72);
        _fragTable = (long)Le.U64(sb, 80);
        _exportTable = (long)Le.U64(sb, 88);
        if (bytesUsed > _source.Length) Warnings.Add("The image is shorter than the size recorded in its superblock.");

        int xzDict = 0, gzipLevel = 9, zstdLevel = 15;
        bool lz4hc = false;
        var filters = new List<BcjFilter>();
        if ((flags & FlagCompressorOptions) != 0)
        {
            // The options block is a metadata block that is always stored uncompressed.
            int hdr = Le.U16(_source.ReadBytes(SuperblockSize, 2), 0);
            int size = hdr & 0x7FFF;
            if ((hdr & 0x8000) != 0 && size is >= 4 and <= 64)
            {
                byte[] opt = _source.ReadBytes(SuperblockSize + 2, size);
                switch ((SquashCompression)compression)
                {
                    case SquashCompression.Xz when size >= 8:
                        xzDict = (int)Le.U32(opt, 0);
                        uint f = Le.U32(opt, 4);
                        foreach (BcjFilter bf in Enum.GetValues<BcjFilter>())
                            if ((f & (uint)XzFilterFlag(bf)) != 0) filters.Add(bf);
                        break;
                    case SquashCompression.Gzip when size >= 4:
                        gzipLevel = (int)Le.U32(opt, 0);
                        break;
                    case SquashCompression.Zstd when size >= 4:
                        zstdLevel = (int)Le.U32(opt, 0);
                        break;
                    case SquashCompression.Lz4 when size >= 8:
                        lz4hc = (Le.U32(opt, 4) & 1) != 0;
                        break;
                }
            }
        }

        Info = new SquashFsInfo
        {
            VersionMajor = major,
            VersionMinor = minor,
            BlockSize = blockSize,
            Compression = (SquashCompression)compression,
            Flags = flags,
            BytesUsed = bytesUsed,
            MkfsTime = Le.U32(sb, 8),
            InodeCount = Le.U32(sb, 4),
            FragmentCount = Le.U32(sb, 16),
            IdCount = Le.U16(sb, 26),
            XzDictionarySize = xzDict,
            XzFilters = filters,
            GzipLevel = gzipLevel is >= 1 and <= 9 ? gzipLevel : 9,
            ZstdLevel = zstdLevel is >= 1 and <= 22 ? zstdLevel : 15,
            Lz4HighCompression = lz4hc,
            HasXattrs = (ulong)_xattrIdTable != InvalidBlock,
        };

        // Fail early for unknown compressors.
        Return(Rent());

        ReadIdTable();
        ReadFragmentTable();
        ReadXattrIdTable();
    }

    // ------------------------------------------------------------- codecs

    private IBlockCodec Rent() => _codecs.TryTake(out IBlockCodec? c) ? c : CreateDecoder(Info);

    private void Return(IBlockCodec codec) => _codecs.Add(codec);

    private int Decompress(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        IBlockCodec codec = Rent();
        try
        {
            return codec.Decompress(src, dst);
        }
        finally
        {
            Return(codec);
        }
    }

    // ----------------------------------------------------------- metadata

    private MetaBlock GetMetaBlock(long pos)
    {
        lock (_metaCache)
        {
            if (_metaCache.TryGetValue(pos, out MetaBlock? cached)) return cached;
        }
        if (pos < SuperblockSize || pos + 2 > _source.Length) throw new FsException("Corrupt SquashFS image: metadata block is out of range.");
        Span<byte> h = stackalloc byte[2];
        _source.ReadExactly(pos, h);
        int hdr = Le.U16(h, 0);
        int size = hdr & 0x7FFF;
        bool uncompressed = (hdr & 0x8000) != 0;
        if (size == 0 || size > MetadataSize) throw new FsException("Corrupt SquashFS image: invalid metadata block size.");
        byte[] raw = _source.ReadBytes(pos + 2, size);
        byte[] data;
        if (uncompressed)
        {
            data = raw;
        }
        else
        {
            byte[] tmp = new byte[MetadataSize];
            int n = Decompress(raw, tmp);
            data = n == MetadataSize ? tmp : tmp.AsSpan(0, n).ToArray();
        }
        var block = new MetaBlock(data, pos + 2 + size);
        lock (_metaCache) _metaCache[pos] = block;
        return block;
    }

    /// <summary>Sequential reader over a chain of metadata blocks.</summary>
    private sealed class MetaCursor
    {
        private readonly SquashFsReader _r;
        private MetaBlock _block = null!;
        private int _offset;
        private readonly byte[] _tmp = new byte[8];

        public MetaCursor(SquashFsReader r) => _r = r;

        public long BlockPos { get; private set; }
        public int Offset => _offset;

        public void Seek(long blockPos, int offset)
        {
            BlockPos = blockPos;
            _block = _r.GetMetaBlock(blockPos);
            if (offset > _block.Data.Length) throw new FsException("Corrupt SquashFS image: metadata offset is out of range.");
            _offset = offset;
        }

        public void Read(Span<byte> dst)
        {
            while (dst.Length > 0)
            {
                if (_offset == _block.Data.Length)
                {
                    BlockPos = _block.Next;
                    _block = _r.GetMetaBlock(BlockPos);
                    _offset = 0;
                }
                int n = Math.Min(dst.Length, _block.Data.Length - _offset);
                _block.Data.AsSpan(_offset, n).CopyTo(dst);
                _offset += n;
                dst = dst[n..];
            }
        }

        public void Skip(int count)
        {
            Span<byte> tmp = stackalloc byte[64];
            while (count > 0)
            {
                int n = Math.Min(count, tmp.Length);
                Read(tmp[..n]);
                count -= n;
            }
        }

        public int U16() { Read(_tmp.AsSpan(0, 2)); return Le.U16(_tmp, 0); }
        public uint U32() { Read(_tmp.AsSpan(0, 4)); return Le.U32(_tmp, 0); }
        public ulong U64() { Read(_tmp.AsSpan(0, 8)); return Le.U64(_tmp, 0); }

        public byte[] Bytes(int count)
        {
            byte[] b = new byte[count];
            Read(b);
            return b;
        }
    }

    /// <summary>Reads a table that is stored as metadata blocks located through an index of 64-bit pointers.</summary>
    private byte[] ReadIndexedTable(long indexPos, long byteCount)
    {
        int blocks = (int)((byteCount + MetadataSize - 1) / MetadataSize);
        byte[] index = _source.ReadBytes(indexPos, blocks * 8);
        byte[] result = new byte[byteCount];
        int done = 0;
        for (int i = 0; i < blocks; i++)
        {
            MetaBlock b = GetMetaBlock((long)Le.U64(index, i * 8));
            int n = (int)Math.Min(b.Data.Length, byteCount - done);
            b.Data.AsSpan(0, n).CopyTo(result.AsSpan(done));
            done += n;
        }
        if (done != byteCount) throw new FsException("Corrupt SquashFS image: lookup table is truncated.");
        return result;
    }

    private void ReadIdTable()
    {
        int count = Info.IdCount;
        if (count == 0) throw new FsException("Corrupt SquashFS image: the id table is empty.");
        byte[] raw = ReadIndexedTable(_idTable, (long)count * 4);
        _ids = new uint[count];
        for (int i = 0; i < count; i++) _ids[i] = Le.U32(raw, i * 4);
    }

    private void ReadFragmentTable()
    {
        uint count = Info.FragmentCount;
        if (count == 0 || (ulong)_fragTable == InvalidBlock) return;
        byte[] raw = ReadIndexedTable(_fragTable, (long)count * 16);
        _fragments = new (long, uint)[count];
        for (int i = 0; i < count; i++)
            _fragments[i] = ((long)Le.U64(raw, i * 16), Le.U32(raw, i * 16 + 8));
    }

    private void ReadXattrIdTable()
    {
        if ((ulong)_xattrIdTable == InvalidBlock) return;
        byte[] hdr = _source.ReadBytes(_xattrIdTable, 16);
        _xattrKvStart = (long)Le.U64(hdr, 0);
        uint count = Le.U32(hdr, 8);
        if (count == 0) return;
        byte[] raw = ReadIndexedTable(_xattrIdTable + 16, (long)count * 16);
        _xattrIds = new (ulong, uint)[count];
        for (int i = 0; i < count; i++)
            _xattrIds[i] = (Le.U64(raw, i * 16), Le.U32(raw, i * 16 + 8));
    }

    private SortedDictionary<string, byte[]>? ReadXattrs(uint index)
    {
        if (index == InvalidXattr || index >= _xattrIds.Length) return null;
        (ulong xref, uint count) = _xattrIds[index];
        var result = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var cur = new MetaCursor(this);
        cur.Seek(_xattrKvStart + (long)(xref >> 16), (int)(xref & 0xFFFF));
        for (uint i = 0; i < count; i++)
        {
            int type = cur.U16();
            int nameSize = cur.U16();
            string name = PosixName.Decode(cur.Bytes(nameSize));
            uint valueSize = cur.U32();
            byte[] value;
            if ((type & XattrOutOfLine) != 0)
            {
                ulong vref = cur.U64();
                var vc = new MetaCursor(this);
                vc.Seek(_xattrKvStart + (long)(vref >> 16), (int)(vref & 0xFFFF));
                uint realSize = vc.U32();
                if (realSize > 1 << 20) throw new FsException("Corrupt SquashFS image: xattr value is too large.");
                value = vc.Bytes((int)realSize);
            }
            else
            {
                if (valueSize > 1 << 20) throw new FsException("Corrupt SquashFS image: xattr value is too large.");
                value = cur.Bytes((int)valueSize);
            }
            int prefix = type & 0xFF;
            if (prefix >= XattrPrefixes.Length)
            {
                Warnings.Add($"Extended attribute with unknown namespace id {prefix} was skipped.");
                continue;
            }
            result[XattrPrefixes[prefix] + name] = value;
        }
        return result.Count > 0 ? result : null;
    }

    // ---------------------------------------------------------------- tree

    private uint Id(int index)
    {
        if (index >= _ids.Length) throw new FsException("Corrupt SquashFS image: id index is out of range.");
        return _ids[index];
    }

    private sealed class DirWork
    {
        public required FsNode Node;
        public long ListingBlock;
        public int ListingOffset;
        public int ListingSize;
    }

    /// <summary>Builds the complete tree. File contents stay in the image and are read on demand.</summary>
    public FsTree ReadTree(IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var tree = new FsTree();
        var cur = new MetaCursor(this);
        var hardLinks = new Dictionary<uint, FsInode>();
        var seenDirs = new HashSet<ulong>();
        var stack = new Stack<DirWork>();
        long done = 0;

        // Root directory inode.
        (FsInode rootInode, DirWork? rootWork, _) = ReadInode(cur, _rootRef, tree.Root, isRoot: true);
        tree.Root.Inode.Mode = rootInode.Mode;
        tree.Root.Inode.Uid = rootInode.Uid;
        tree.Root.Inode.Gid = rootInode.Gid;
        tree.Root.Inode.MTime = rootInode.MTime;
        tree.Root.Inode.XAttrs = rootInode.XAttrs;
        if (rootWork is null) throw new FsException("Corrupt SquashFS image: the root inode is not a directory.");
        seenDirs.Add(_rootRef);
        stack.Push(rootWork);

        var dirCur = new MetaCursor(this);
        byte[] nameBuf = new byte[256];
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            DirWork work = stack.Pop();
            if (work.ListingSize <= 0) continue;
            dirCur.Seek(_dirTable + work.ListingBlock, work.ListingOffset);
            int remaining = work.ListingSize;
            while (remaining > 0)
            {
                if (remaining < 12) throw new FsException("Corrupt SquashFS image: truncated directory header.");
                uint count = dirCur.U32() + 1;
                uint startBlock = dirCur.U32();
                uint inodeBase = dirCur.U32();
                remaining -= 12;
                if (count > 256) throw new FsException("Corrupt SquashFS image: too many entries in a directory header.");
                for (uint i = 0; i < count; i++)
                {
                    if (remaining < 8) throw new FsException("Corrupt SquashFS image: truncated directory entry.");
                    int offset = dirCur.U16();
                    dirCur.U16();   // inode number delta, not needed: the inode carries its number
                    dirCur.U16();   // type
                    int nameLen = dirCur.U16() + 1;
                    if (nameLen > 256) throw new FsException("Corrupt SquashFS image: a directory entry name is too long.");
                    dirCur.Read(nameBuf.AsSpan(0, nameLen));
                    remaining -= 8 + nameLen;
                    string name = PosixName.Decode(nameBuf.AsSpan(0, nameLen));

                    ulong iref = ((ulong)startBlock << 16) | (uint)offset;
                    (FsInode inode, DirWork? sub, uint number) = ReadInode(cur, iref, null, isRoot: false);
                    if (inode.Type != FsNodeType.Directory)
                    {
                        // Hard links: every entry of the same inode number shares one inode object.
                        if (hardLinks.TryGetValue(number, out FsInode? known)) inode = known;
                        else hardLinks.Add(number, inode);
                    }
                    else if (!seenDirs.Add(iref))
                    {
                        throw new FsException("Corrupt SquashFS image: a directory is referenced more than once.");
                    }

                    if (name is "." or ".." || nameLen > 255 || name.Contains('/') || name.Contains('\0') || work.Node.Find(name) is not null)
                    {
                        Warnings.Add($"Invalid or duplicate directory entry '{name}' in '{work.Node.Path}' was skipped.");
                        continue;
                    }
                    FsNode node = tree.Add(work.Node, name, inode);
                    if (sub is not null)
                    {
                        sub.Node = node;
                        stack.Push(sub);
                    }
                    if ((++done & 0x3FF) == 0) progress?.Report(new ProgressInfo("read", done, Info.InodeCount, node.Path));
                }
            }
        }
        progress?.Report(new ProgressInfo("read", done, done));
        return tree;
    }

    private (FsInode Inode, DirWork? Dir, uint Number) ReadInode(MetaCursor cur, ulong iref, FsNode? rootNode, bool isRoot)
    {
        cur.Seek(_inodeTable + (long)(iref >> 16), (int)(iref & 0xFFFF));
        int type = cur.U16();
        int mode = cur.U16();
        int uidIdx = cur.U16();
        int gidIdx = cur.U16();
        uint mtime = cur.U32();
        uint number = cur.U32();

        var inode = new FsInode
        {
            Mode = mode & 0xFFF,
            Uid = Id(uidIdx),
            Gid = Id(gidIdx),
            MTime = mtime,
        };
        DirWork? dir = null;
        uint xattr = InvalidXattr;

        switch (type)
        {
            case TypeDir:
            {
                uint startBlock = cur.U32();
                cur.U32();                          // nlink
                int fileSize = cur.U16();
                int offset = cur.U16();
                cur.U32();                          // parent inode
                inode.Type = FsNodeType.Directory;
                dir = new DirWork { Node = rootNode!, ListingBlock = startBlock, ListingOffset = offset, ListingSize = fileSize - 3 };
                break;
            }
            case TypeDir + TypeExtOffset:
            {
                cur.U32();                          // nlink
                uint fileSize = cur.U32();
                uint startBlock = cur.U32();
                cur.U32();                          // parent inode
                cur.U16();                          // index count (the index is only a lookup accelerator)
                int offset = cur.U16();
                xattr = cur.U32();
                inode.Type = FsNodeType.Directory;
                dir = new DirWork { Node = rootNode!, ListingBlock = startBlock, ListingOffset = offset, ListingSize = (int)fileSize - 3 };
                break;
            }
            case TypeFile:
            case TypeFile + TypeExtOffset:
            {
                long startBlock, fileSize;
                uint fragment, fragOffset;
                if (type == TypeFile)
                {
                    startBlock = cur.U32();
                    fragment = cur.U32();
                    fragOffset = cur.U32();
                    fileSize = cur.U32();
                }
                else
                {
                    startBlock = (long)cur.U64();
                    fileSize = (long)cur.U64();
                    cur.U64();                      // sparse bytes
                    cur.U32();                      // nlink
                    fragment = cur.U32();
                    fragOffset = cur.U32();
                    xattr = cur.U32();
                }
                if (fileSize < 0) throw new FsException("Corrupt SquashFS image: negative file size.");
                long blocks = fragment == InvalidFragment
                    ? (fileSize + Info.BlockSize - 1) / Info.BlockSize
                    : fileSize / Info.BlockSize;
                if (blocks > int.MaxValue / 8) throw new FsException("Corrupt SquashFS image: file has too many blocks.");
                var sizes = new uint[blocks];
                for (int i = 0; i < sizes.Length; i++) sizes[i] = cur.U32();
                if (fragment != InvalidFragment && fragment >= _fragments.Length)
                    throw new FsException("Corrupt SquashFS image: fragment index is out of range.");
                inode.Type = FsNodeType.File;
                inode.Data = fileSize == 0 ? null : new SquashFileData(this, startBlock, sizes, fragment, fragOffset, fileSize);
                break;
            }
            case TypeSymlink:
            case TypeSymlink + TypeExtOffset:
            {
                cur.U32();                          // nlink
                uint size = cur.U32();
                if (size > 65535) throw new FsException("Corrupt SquashFS image: symlink target is too long.");
                inode.Type = FsNodeType.Symlink;
                inode.LinkTarget = PosixName.Decode(cur.Bytes((int)size));
                if (type != TypeSymlink) xattr = cur.U32();
                break;
            }
            case TypeBlock:
            case TypeChar:
            case TypeBlock + TypeExtOffset:
            case TypeChar + TypeExtOffset:
            {
                cur.U32();                          // nlink
                (inode.DevMajor, inode.DevMinor) = DecodeDev(cur.U32());
                bool ext = type > TypeExtOffset;
                if (ext) xattr = cur.U32();
                inode.Type = (ext ? type - TypeExtOffset : type) == TypeBlock ? FsNodeType.BlockDevice : FsNodeType.CharDevice;
                break;
            }
            case TypeFifo:
            case TypeSocket:
            case TypeFifo + TypeExtOffset:
            case TypeSocket + TypeExtOffset:
            {
                cur.U32();                          // nlink
                bool ext = type > TypeExtOffset;
                if (ext) xattr = cur.U32();
                inode.Type = (ext ? type - TypeExtOffset : type) == TypeFifo ? FsNodeType.Fifo : FsNodeType.Socket;
                break;
            }
            default:
                throw new FsException($"Corrupt SquashFS image: unknown inode type {type}.");
        }

        if (xattr != InvalidXattr) inode.XAttrs = ReadXattrs(xattr);
        return (inode, dir, number);
    }

    // ----------------------------------------------------------- file data

    private byte[] ReadFragmentBlock(uint index)
    {
        lock (_fragLock)
        {
            for (LinkedListNode<(uint Index, byte[] Data)>? n = _fragCache.First; n is not null; n = n.Next)
            {
                if (n.Value.Index != index) continue;
                _fragCache.Remove(n);
                _fragCache.AddFirst(n);
                return n.Value.Data;
            }
        }
        (long start, uint sizeWord) = _fragments[index];
        byte[] data = new byte[Info.BlockSize];
        int produced = ReadDataBlock(start, sizeWord, data);
        if (produced != data.Length) Array.Resize(ref data, produced);
        lock (_fragLock)
        {
            _fragCache.AddFirst((index, data));
            if (_fragCache.Count > FragCacheSize) _fragCache.RemoveLast();
        }
        return data;
    }

    /// <summary>Reads one data or fragment block into <paramref name="dst"/>; returns its uncompressed size.</summary>
    private int ReadDataBlock(long position, uint sizeWord, Span<byte> dst)
    {
        int size = (int)(sizeWord & 0xFFFFFF);
        if (size > Info.BlockSize) throw new FsException("Corrupt SquashFS image: invalid data block size.");
        if ((sizeWord & BlockUncompressed) != 0)
        {
            if (size > dst.Length) throw new FsException("Corrupt SquashFS image: invalid data block size.");
            _source.ReadExactly(position, dst[..size]);
            return size;
        }
        byte[] raw = System.Buffers.ArrayPool<byte>.Shared.Rent(size);
        try
        {
            _source.ReadExactly(position, raw.AsSpan(0, size));
            return Decompress(raw.AsSpan(0, size), dst);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(raw);
        }
    }

    private sealed class SquashFileData(SquashFsReader reader, long startBlock, uint[] blockSizes, uint fragment, uint fragOffset, long length) : ISparseFileData
    {
        public long Length => length;

        public long DataBytesHint
        {
            get
            {
                // A block with a zero size word is a hole; everything else may hold data.
                long blockSize = reader.Info.BlockSize;
                long bytes = 0;
                foreach (uint size in blockSizes) if (size != 0) bytes += blockSize;
                if (fragment != InvalidFragment) bytes += length % blockSize;
                return Math.Min(bytes, length);
            }
        }

        public Stream OpenRead() => new SquashFileStream(reader, startBlock, blockSizes, fragment, fragOffset, length);
    }

    /// <summary>Forward-only stream that decompresses a file block by block.</summary>
    private sealed class SquashFileStream : Stream
    {
        private readonly SquashFsReader _r;
        private readonly uint[] _sizes;
        private readonly uint _fragment, _fragOffset;
        private readonly long _length;
        private long _diskPos;
        private long _position;
        private int _blockIndex;
        private byte[]? _buf;
        private int _bufPos, _bufLen;

        public SquashFileStream(SquashFsReader r, long startBlock, uint[] sizes, uint fragment, uint fragOffset, long length)
        {
            _r = r;
            _diskPos = startBlock;
            _sizes = sizes;
            _fragment = fragment;
            _fragOffset = fragOffset;
            _length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        private bool Fill()
        {
            long left = _length - _position;
            if (left <= 0) return false;
            int blockSize = _r.Info.BlockSize;
            if (_blockIndex < _sizes.Length)
            {
                _buf ??= System.Buffers.ArrayPool<byte>.Shared.Rent(blockSize);
                int expect = (int)Math.Min(blockSize, left);
                uint word = _sizes[_blockIndex++];
                int stored = (int)(word & 0xFFFFFF);
                if (stored == 0)
                {
                    // Sparse block: nothing is stored on disk.
                    _buf.AsSpan(0, expect).Clear();
                }
                else
                {
                    int got = _r.ReadDataBlock(_diskPos, word, _buf.AsSpan(0, blockSize));
                    if (got < expect) throw new FsException("Corrupt SquashFS image: data block is shorter than expected.");
                    _diskPos += stored;
                }
                _bufPos = 0;
                _bufLen = expect;
                return true;
            }
            if (_fragment == InvalidFragment) throw new FsException("Corrupt SquashFS image: file data is truncated.");
            byte[] frag = _r.ReadFragmentBlock(_fragment);
            if (_fragOffset + left > frag.Length) throw new FsException("Corrupt SquashFS image: fragment is out of range.");
            _buf ??= System.Buffers.ArrayPool<byte>.Shared.Rent(blockSize);
            frag.AsSpan((int)_fragOffset, (int)left).CopyTo(_buf);
            _bufPos = 0;
            _bufLen = (int)left;
            return true;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0) return 0;
            if (_bufPos == _bufLen && !Fill()) return 0;
            int n = Math.Min(buffer.Length, _bufLen - _bufPos);
            _buf.AsSpan(_bufPos, n).CopyTo(buffer);
            _bufPos += n;
            _position += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (_buf is not null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(_buf);
                _buf = null;
            }
            base.Dispose(disposing);
        }
    }

    public void Dispose()
    {
        while (_codecs.TryTake(out IBlockCodec? c)) (c as IDisposable)?.Dispose();
        if (_ownsSource) _source.Dispose();
    }
}
