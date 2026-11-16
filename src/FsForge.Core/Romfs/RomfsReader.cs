using System.Buffers.Binary;
using static FsForge.Core.Romfs.RomfsFormat;

namespace FsForge.Core.Romfs;

/// <summary>
/// Reads romfs images (genromfs, the uClinux and bootloader filesystem): directories, files, symbolic links,
/// devices, sockets, FIFOs and hard links. romfs stores no owners, times or permissions other than the
/// executable bit: objects get the permissions the kernel gives them, owner root and time zero.
/// </summary>
public sealed class RomfsReader : IDisposable
{
    private readonly IBlockSource _source;
    private readonly bool _ownsSource;
    private long _rootHeader;

    private RomfsReader(IBlockSource source, bool ownsSource)
    {
        _source = source;
        _ownsSource = ownsSource;
        Info = null!;
    }

    public RomfsInfo Info { get; private set; }

    /// <summary>Non-fatal problems found while reading.</summary>
    public List<string> Warnings { get; } = new();

    public static bool IsRomfs(IBlockSource source)
    {
        if (source.Length < 32) return false;
        Span<byte> head = stackalloc byte[8];
        source.ReadExactly(0, head);
        return head.SequenceEqual(Magic);
    }

    public static RomfsReader Open(IBlockSource source, bool ownsSource = true)
    {
        if (!IsRomfs(source)) throw new FsException("Not a romfs image.");
        var r = new RomfsReader(source, ownsSource);
        byte[] head = source.ReadBytes(0, (int)Math.Min(512, source.Length));
        uint size = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(8));
        if (size < 32) throw new FsException("Corrupt romfs superblock: invalid size.");
        if (size > source.Length) throw new FsException($"The romfs image is truncated: it should be {size} bytes, the file has {source.Length}.");
        bool sumOk = Sum(head.AsSpan(0, (int)Math.Min(512, size) & ~3)) == 0;
        if (!sumOk) r.Warnings.Add("The checksum of the romfs superblock is wrong: the kernel does not mount such an image.");
        (string volume, long nameEnd) = r.Name(16);
        r._rootHeader = Align16(nameEnd);
        r.Info = new RomfsInfo { VolumeName = volume, Size = size, ChecksumOk = sumOk };
        return r;
    }

    /// <summary>A zero-terminated name and the offset just past its terminator.</summary>
    private (string Name, long End) Name(long at)
    {
        byte[] buf = new byte[MaxNameLength + 1 + 128];
        int n = _source.Read(at, buf);
        int nul = Array.IndexOf(buf, (byte)0, 0, Math.Max(0, n));
        if (nul < 0) throw new FsException("Corrupt romfs image: a name is not terminated.");
        return (PosixName.Decode(buf.AsSpan(0, nul)), at + nul + 1);
    }

    private readonly record struct Header(long At, uint Next, uint Spec, uint Size, string Name, long DataAt)
    {
        public int Type => (int)(Next & 7);
        public bool Exec => (Next & ExecBit) != 0;
        public long NextAt => Next & ~15u;
    }

    private Header ReadHeader(long at)
    {
        if (at < 16 || (at & 15) != 0 || at + HeaderSize > _source.Length)
            throw new FsException("Corrupt romfs image: an entry lies outside the image.");
        Span<byte> h = stackalloc byte[HeaderSize];
        _source.ReadExactly(at, h);
        (string name, long end) = Name(at + HeaderSize);
        return new Header(at, BinaryPrimitives.ReadUInt32BigEndian(h), BinaryPrimitives.ReadUInt32BigEndian(h[4..]),
            BinaryPrimitives.ReadUInt32BigEndian(h[8..]), name, Align16(end));
    }

    public FsTree ReadTree(IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var tree = new FsTree();
        Header root = ReadHeader(_rootHeader);
        if (root.Type != TypeDirectory) throw new FsException("Corrupt romfs image: the first entry is not a directory.");
        tree.Root.Inode.Mode = ModeOf(TypeDirectory, root.Exec);

        var byHeader = new Dictionary<long, FsNode>();
        var links = new List<(FsNode Dir, Header H, string Path)>();
        var seenDirs = new HashSet<long> { root.At };
        var seenEntries = new HashSet<long>();
        var stack = new Stack<(FsNode Dir, long First, int Depth)>();
        stack.Push((tree.Root, root.Spec & ~15u, 0));
        long done = 0, align = 1 << 12;
        bool anyData = false;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            (FsNode dir, long at, int depth) = stack.Pop();
            if (depth > 1000) throw new FsException("Corrupt romfs image: the directories are nested too deeply.");
            while (at != 0)
            {
                if (!seenEntries.Add(at)) throw new FsException("Corrupt romfs image: the entries of a directory form a loop.");
                Header h = ReadHeader(at);
                at = h.NextAt;
                string path = dir.IsRoot ? "/" + h.Name : dir.Path + "/" + h.Name;
                if (h.Name is "." or "..") continue;
                if (h.Name.Length == 0 || h.Name.Contains('/') || dir.Find(h.Name) is not null)
                {
                    Warnings.Add($"Invalid or duplicate directory entry '{h.Name}' in '{dir.Path}' was skipped.");
                    continue;
                }
                if (h.Type == TypeHardLink)
                {
                    links.Add((dir, h, path));
                    continue;
                }
                var inode = new FsInode { Mode = ModeOf(h.Type, h.Exec) };
                switch (h.Type)
                {
                    case TypeDirectory:
                        inode.Type = FsNodeType.Directory;
                        break;
                    case TypeFile:
                        inode.Type = FsNodeType.File;
                        if (h.DataAt + h.Size > _source.Length) throw new FsException($"The romfs image is truncated: the data of '{path}' lies outside the file.");
                        if (h.Size > 0)
                        {
                            inode.Data = new SliceFileData(_source, h.DataAt, h.Size);
                            anyData = true;
                            while (align > 16 && h.DataAt % align != 0) align >>= 1;
                        }
                        break;
                    case TypeSymlink:
                        inode.Type = FsNodeType.Symlink;
                        if (h.Size > 65536 || h.DataAt + h.Size > _source.Length)
                            throw new FsException($"Corrupt romfs image: the symbolic link '{path}' has an invalid length.");
                        inode.LinkTarget = PosixName.Decode(_source.ReadBytes(h.DataAt, (int)h.Size));
                        break;
                    case TypeBlock or TypeChar:
                        inode.Type = h.Type == TypeBlock ? FsNodeType.BlockDevice : FsNodeType.CharDevice;
                        (inode.DevMajor, inode.DevMinor) = (h.Spec >> 16, h.Spec & 0xFFFF);
                        break;
                    case TypeSocket:
                        inode.Type = FsNodeType.Socket;
                        break;
                    default:
                        inode.Type = FsNodeType.Fifo;
                        break;
                }
                FsNode node = tree.Add(dir, h.Name, inode);
                byHeader[h.At] = node;
                if (h.Type == TypeDirectory)
                {
                    if (!seenDirs.Add(h.At)) throw new FsException("Corrupt romfs image: a directory is referenced more than once.");
                    stack.Push((node, h.Spec & ~15u, depth + 1));
                }
                if ((++done & 0x3FF) == 0) progress?.Report(new ProgressInfo("read", done, 0, path));
            }
        }

        // Hard links point at the header of another entry, which may come later in the image.
        foreach ((FsNode dir, Header h, string path) in links)
        {
            FsNode? target = null;
            long at = h.Spec & ~15u;
            for (int hops = 0; hops < 16 && target is null; hops++)
            {
                if (byHeader.TryGetValue(at, out FsNode? n)) target = n;
                else
                {
                    Header t;
                    try
                    {
                        t = ReadHeader(at);
                    }
                    catch (FsException)
                    {
                        break;
                    }
                    if (t.Type != TypeHardLink) break;
                    at = t.Spec & ~15u;
                }
            }
            if (dir.Find(h.Name) is not null) Warnings.Add($"Invalid or duplicate directory entry '{h.Name}' in '{dir.Path}' was skipped.");
            else if (target is null) Warnings.Add($"'{path}': the hard link target is not in the image; the link was skipped.");
            else if (target.IsDirectory) Warnings.Add($"'{path}' is a hard link to the directory '{target.Path}' and was skipped.");
            else tree.Add(dir, h.Name, target.Inode);
        }
        progress?.Report(new ProgressInfo("read", done, done));
        Info = new RomfsInfo { VolumeName = Info.VolumeName, Size = Info.Size, ChecksumOk = Info.ChecksumOk, DataAlignment = anyData ? (int)align : 16 };
        return tree;
    }

    public void Dispose()
    {
        if (_ownsSource) _source.Dispose();
    }
}
