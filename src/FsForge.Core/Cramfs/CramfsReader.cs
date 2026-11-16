using System.Text;
using FsForge.Core.Compression;
using static FsForge.Core.Cramfs.CramfsFormat;

namespace FsForge.Core.Cramfs;

/// <summary>
/// Reads CramFS images of both byte orders (as mkfs.cramfs makes them for little- and big-endian machines):
/// zlib blocks, holes, uncompressed blocks and direct block pointers (Linux 4.15+), the 512-byte boot pad.
/// CramFS has no times and no hard links: files with the same content share their data, which is kept so.
/// </summary>
public sealed class CramfsReader : IDisposable
{
    private readonly IBlockSource _source;
    private readonly bool _ownsSource;
    private readonly bool _big;
    private readonly long _superStart;
    private readonly uint _flags;
    private Inode _root;
    private int _blockSize = 4096;

    private CramfsReader(IBlockSource source, bool ownsSource, long superStart, bool big, uint flags)
    {
        _source = source;
        _ownsSource = ownsSource;
        _superStart = superStart;
        _big = big;
        _flags = flags;
        Info = null!;
    }

    public CramfsInfo Info { get; private set; }

    /// <summary>Non-fatal problems found while reading.</summary>
    public List<string> Warnings { get; } = new();

    // ---------------------------------------------------------- detection

    /// <summary>Where the superblock is and its byte order: at 0, or at 512 behind room for boot code.</summary>
    private static (long Start, bool Big)? Find(IBlockSource source)
    {
        Span<byte> sb = stackalloc byte[SuperSize];
        foreach (long start in new long[] { 0, 512 })
        {
            if (source.Length < start + SuperSize) break;
            source.ReadExactly(start, sb);
            foreach (bool big in new[] { false, true })
            {
                if (U32(sb, 0, big) != Magic) continue;
                Inode root = Inode.Parse(sb[RootInodeOffset..], big);
                if ((root.Mode & UnixMode.S_IFMT) == UnixMode.S_IFDIR) return (start, big);
            }
        }
        return null;
    }

    /// <summary>A CramFS superblock (of either byte order) whose root is a directory.</summary>
    public static bool IsCramfs(IBlockSource source) => Find(source) is not null;

    public static CramfsReader Open(IBlockSource source, bool ownsSource = true)
    {
        (long start, bool big) = Find(source) ?? throw new FsException("Not a CramFS image.");
        byte[] sb = source.ReadBytes(start, SuperSize);
        uint size = U32(sb, 4, big), flags = U32(sb, 8, big);
        if ((flags & ~SupportedFlags) != 0)
            throw new FsException($"The CramFS image uses features this program does not know (flags 0x{flags:X}).");
        var r = new CramfsReader(source, ownsSource, start, big, flags) { _root = Inode.Parse(sb.AsSpan(RootInodeOffset), big) };

        bool v2 = (flags & FlagFsidV2) != 0;
        if ((flags & FlagWrongSignature) == 0 && !sb.AsSpan(16, 16).SequenceEqual(Signature))
            r.Warnings.Add("The CramFS superblock has an unexpected signature.");
        bool? crcOk = null;
        if (v2)
        {
            if (size < start + SuperSize) throw new FsException("Corrupt CramFS superblock: invalid size.");
            if (size > source.Length) throw new FsException($"The CramFS image is truncated: it should be {size} bytes, the file has {source.Length}.");
            crcOk = r.Checksum(size) == U32(sb, 32, big);
            if (crcOk == false) r.Warnings.Add("The CRC of the CramFS image does not match: the image is damaged or was changed after it was made.");
        }
        r._blockSize = r.DetectBlockSize();
        r.Info = new CramfsInfo
        {
            BigEndian = big, BlockSize = r._blockSize, Size = v2 ? size : 0, Padded = start == 512, Flags = flags,
            VolumeName = Encoding.UTF8.GetString(sb.AsSpan(48, 16)).TrimEnd('\0'),
            Edition = v2 ? U32(sb, 36, big) : 0, Blocks = v2 ? U32(sb, 40, big) : 0, Files = v2 ? U32(sb, 44, big) : 0,
            CrcOk = crcOk,
        };
        return r;
    }

    /// <summary>CRC-32 of the filesystem with the CRC field taken as zero, as mkfs.cramfs computes it.</summary>
    private uint Checksum(uint size)
    {
        uint crc = 0;
        byte[] buf = new byte[1 << 16];
        for (long pos = _superStart; pos < size;)
        {
            int n = (int)Math.Min(buf.Length, size - pos);
            _source.ReadExactly(pos, buf.AsSpan(0, n));
            // The CRC field lies at 32..35 of the superblock.
            for (long i = Math.Max(pos, _superStart + 32); i < Math.Min(pos + n, _superStart + 36); i++) buf[i - pos] = 0;
            crc = Crc32.Update(crc, buf.AsSpan(0, n));
            pos += n;
        }
        return crc;
    }

    /// <summary>
    /// The block size is not stored: it is the page size of the machine the image was made for. The first file
    /// of more than one 4 KiB block tells it: its first block decompresses to a whole block only at the right size.
    /// </summary>
    private int DetectBlockSize()
    {
        int tried = 0;
        foreach ((Inode ino, _) in Entries(_root, depthLimit: 64))
        {
            if ((ino.Mode & UnixMode.S_IFMT) != UnixMode.S_IFREG || ino.Size <= 4096 || ino.Offset == 0) continue;
            if (++tried > 8) break;
            foreach (int bs in new[] { 4096, 8192, 16384, 32768, 65536 })
            {
                try
                {
                    byte[] block = new byte[bs];
                    if (ReadBlock(ino, 0, bs, block) == Math.Min(bs, ino.Size)) return bs;
                }
                catch (FsException)
                {
                }
            }
        }
        return 4096;
    }

    /// <summary>All inodes below a directory (depth first), with their paths relative to it.</summary>
    private IEnumerable<(Inode Ino, string Path)> Entries(Inode dir, int depthLimit)
    {
        var stack = new Stack<(Inode, string, int)>();
        stack.Push((dir, "", 0));
        var seen = new HashSet<uint>();
        while (stack.Count > 0)
        {
            (Inode d, string prefix, int depth) = stack.Pop();
            if (d.Size == 0 || !seen.Add(d.Offset)) continue;
            List<(Inode, string)> list;
            try
            {
                list = ReadDirectory(d, prefix);
            }
            catch (FsException)
            {
                continue;
            }
            foreach ((Inode ino, string name) in list)
            {
                string path = prefix + "/" + name;
                yield return (ino, path);
                if ((ino.Mode & UnixMode.S_IFMT) == UnixMode.S_IFDIR && depth < depthLimit) stack.Push((ino, path, depth + 1));
            }
        }
    }

    private List<(Inode Ino, string Name)> ReadDirectory(Inode dir, string path)
    {
        var list = new List<(Inode, string)>();
        if (dir.Size == 0) return list;
        long start = (long)dir.Offset * 4, end = start + dir.Size;
        if (dir.Offset == 0 || end > _source.Length)
            throw new FsException($"Corrupt CramFS image: the directory '{(path.Length == 0 ? "/" : path)}' lies outside the image.");
        byte[] buf = _source.ReadBytes(start, (int)dir.Size);
        int p = 0;
        while (p < buf.Length)
        {
            if (p + InodeSize > buf.Length) throw new FsException($"Corrupt CramFS image: a truncated entry in '{(path.Length == 0 ? "/" : path)}'.");
            Inode ino = Inode.Parse(buf.AsSpan(p), _big);
            int nameBytes = ino.NameLen * 4;
            if (nameBytes == 0 || p + InodeSize + nameBytes > buf.Length)
                throw new FsException($"Corrupt CramFS image: an entry of '{(path.Length == 0 ? "/" : path)}' has an invalid name.");
            ReadOnlySpan<byte> raw = buf.AsSpan(p + InodeSize, nameBytes);
            int nul = raw.IndexOf((byte)0);
            list.Add((ino, PosixName.Decode(nul >= 0 ? raw[..nul] : raw)));
            p += InodeSize + nameBytes;
        }
        return list;
    }

    // --------------------------------------------------------------- tree

    public FsTree ReadTree(IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var tree = new FsTree();
        FsInode r = tree.Root.Inode;
        (r.Mode, r.Uid, r.Gid) = (_root.Mode & 0xFFF, _root.Uid, _root.Gid);

        var shared = new Dictionary<uint, IFileData>();
        var seenDirs = new HashSet<uint>();
        var stack = new Stack<(FsNode Node, Inode Ino, int Depth)>();
        stack.Push((tree.Root, _root, 0));
        long done = 0;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            (FsNode dir, Inode dirIno, int depth) = stack.Pop();
            if (dirIno.Size > 0 && !seenDirs.Add(dirIno.Offset))
                throw new FsException("Corrupt CramFS image: a directory is referenced more than once.");
            if (depth > 1000) throw new FsException("Corrupt CramFS image: the directories are nested too deeply.");
            foreach ((Inode ino, string name) in ReadDirectory(dirIno, dir.IsRoot ? "" : dir.Path))
            {
                string path = dir.IsRoot ? "/" + name : dir.Path + "/" + name;
                if (name is "" or "." or ".." || name.Contains('/') || dir.Find(name) is not null)
                {
                    Warnings.Add($"Invalid or duplicate directory entry '{name}' in '{dir.Path}' was skipped.");
                    continue;
                }
                FsNodeType? type = UnixMode.TypeFromMode(ino.Mode);
                if (type is null)
                {
                    Warnings.Add($"'{path}' has an unknown file type (mode {Convert.ToString(ino.Mode, 8)}) and was skipped.");
                    continue;
                }
                var inode = new FsInode { Type = type.Value, Mode = ino.Mode & 0xFFF, Uid = ino.Uid, Gid = ino.Gid };
                switch (type.Value)
                {
                    case FsNodeType.File:
                        if (ino.Size > 0)
                        {
                            if (ino.Offset == 0) throw new FsException($"Corrupt CramFS image: the file '{path}' has no data.");
                            if (!shared.TryGetValue(ino.Offset, out IFileData? data))
                                shared.Add(ino.Offset, data = new CramfsFileData(this, ino));
                            // Files with the same content share it; one that is longer or shorter cannot.
                            inode.Data = data.Length == ino.Size ? data : new CramfsFileData(this, ino);
                        }
                        break;
                    case FsNodeType.Symlink:
                        if (ino.Size is 0 or > 65536 || ino.Offset == 0)
                            throw new FsException($"Corrupt CramFS image: the symbolic link '{path}' has an invalid length.");
                        inode.Mode = 0x1FF;
                        using (Stream s = new CramfsFileData(this, ino).OpenRead())
                        {
                            byte[] target = new byte[ino.Size];
                            s.ReadExactly(target);
                            inode.LinkTarget = PosixName.Decode(target);
                        }
                        break;
                    case FsNodeType.CharDevice or FsNodeType.BlockDevice:
                        // The kernel decodes the old 16-bit device number: 8 bits of major, 8 of minor.
                        (inode.DevMajor, inode.DevMinor) = ((ino.Size >> 8) & 0xFF, ino.Size & 0xFF);
                        break;
                }
                FsNode node = tree.Add(dir, name, inode);
                if (type == FsNodeType.Directory) stack.Push((node, ino, depth + 1));
                if ((++done & 0x3FF) == 0) progress?.Report(new ProgressInfo("read", done, Info.Files, path));
            }
        }
        progress?.Report(new ProgressInfo("read", done, done));
        return tree;
    }

    // --------------------------------------------------------------- data

    /// <summary>Decompresses block <paramref name="index"/> of a file into <paramref name="dst"/>; returns its length.</summary>
    private int ReadBlock(Inode ino, long index, int blockSize, Span<byte> dst)
    {
        long blocks = (ino.Size + blockSize - 1) / blockSize;
        long table = (long)ino.Offset * 4;
        int want = (int)Math.Min(blockSize, ino.Size - index * blockSize);
        if (table + blocks * 4 > _source.Length) throw new FsException("Corrupt CramFS image: the block list of a file lies outside the image.");
        Span<byte> b = stackalloc byte[4];
        _source.ReadExactly(table + index * 4, b);
        uint ptr = U32(b, 0, _big);
        bool ext = (_flags & FlagExtBlockPointers) != 0;
        bool uncompressed = ext && (ptr & BlkFlagUncompressed) != 0;
        long start, length;
        if (ext && (ptr & BlkFlagDirectPtr) != 0)
        {
            start = (long)(ptr & ~BlkFlags) * 4;
            if (uncompressed) length = want;
            else
            {
                Span<byte> len = stackalloc byte[2];
                _source.ReadExactly(start, len);
                length = _big ? (len[0] << 8) | len[1] : len[0] | (len[1] << 8);
                start += 2;
            }
        }
        else
        {
            long end = ext ? ptr & ~BlkFlags : ptr;
            if (index == 0) start = table + blocks * 4;
            else
            {
                _source.ReadExactly(table + (index - 1) * 4, b);
                uint prev = U32(b, 0, _big);
                if (ext && (prev & BlkFlagDirectPtr) != 0)
                    throw new FsException("Corrupt CramFS image: a block follows one with a direct pointer.");
                start = ext ? prev & ~BlkFlags : prev;
            }
            length = end - start;
        }
        if (length < 0 || length > 2L * blockSize || start + length > _source.Length)
            throw new FsException("Corrupt CramFS image: a block of a file lies outside the image.");
        dst = dst[..want];
        if (length == 0)
        {
            dst.Clear();                                    // a hole
            return want;
        }
        byte[] packed = _source.ReadBytes(start, (int)length);
        if (uncompressed)
        {
            if (length != want) throw new FsException("Corrupt CramFS image: an uncompressed block has the wrong length.");
            packed.CopyTo(dst);
            return want;
        }
        int n = new ZlibCodec().Decompress(packed, dst);
        if (n != want) throw new FsException("Corrupt CramFS image: a block decompresses to the wrong length.");
        return n;
    }

    private sealed class CramfsFileData(CramfsReader reader, Inode ino) : IFileData
    {
        public long Length => ino.Size;
        public Stream OpenRead() => new CramfsFileStream(reader, ino);
    }

    /// <summary>Forward-only stream that decompresses one block at a time.</summary>
    private sealed class CramfsFileStream(CramfsReader reader, Inode ino) : Stream
    {
        private readonly byte[] _block = new byte[reader._blockSize];
        private long _position, _blockIndex = -1;
        private int _blockLength;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => ino.Size;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int total = 0;
            while (buffer.Length > 0 && _position < ino.Size)
            {
                long index = _position / reader._blockSize;
                if (index != _blockIndex)
                {
                    _blockLength = reader.ReadBlock(ino, index, reader._blockSize, _block);
                    _blockIndex = index;
                }
                int at = (int)(_position - index * reader._blockSize);
                int n = Math.Min(buffer.Length, _blockLength - at);
                _block.AsSpan(at, n).CopyTo(buffer);
                buffer = buffer[n..];
                _position += n;
                total += n;
            }
            return total;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public void Dispose()
    {
        if (_ownsSource) _source.Dispose();
    }
}
