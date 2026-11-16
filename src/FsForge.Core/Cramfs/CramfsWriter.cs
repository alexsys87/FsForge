using System.Security.Cryptography;
using System.Text;
using FsForge.Core.Compression;
using static FsForge.Core.Cramfs.CramfsFormat;

namespace FsForge.Core.Cramfs;

/// <summary>
/// Writes a tree as a CramFS image the way mkfs.cramfs does: the superblock with the root inode, the entries of
/// every directory (breadth first, sorted by name, which the kernel's lookup relies on), then the data of files and
/// symbolic links (depth first): a table of block end pointers followed by zlib-compressed blocks. Files with the
/// same content (and the names of a hard-linked file) share one copy of the data. The image is padded to a whole
/// block and carries the CRC of the version 2 superblock.
/// </summary>
public sealed class CramfsWriter
{
    private readonly CramfsOptions _o;
    private readonly bool _big;
    private readonly List<string> _warnings = new();
    private long _bigUid, _bigGid, _bigDev, _links, _xattrs;

    private CramfsWriter(CramfsOptions options)
    {
        _o = options;
        _big = options.BigEndian;
    }

    public static IReadOnlyList<string> Write(FsTree tree, Stream output, CramfsOptions options,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default, string? tempDirectory = null)
    {
        options.Validate();
        var w = new CramfsWriter(options);
        w.Run(tree, output, progress, ct, tempDirectory);
        return w._warnings;
    }

    private sealed class Entry
    {
        public required FsNode Node;
        public byte[] Name = Array.Empty<byte>();
        public uint Offset;         // directory: its entries; file or link: its block pointers
        public uint Size;
    }

    private void Run(FsTree tree, Stream output, IProgress<ProgressInfo>? progress, CancellationToken ct, string? tempDirectory)
    {
        long superStart = _o.Pad ? 512 : 0;
        long metaStart = superStart + SuperSize;
        var entries = new Dictionary<FsNode, Entry>(ReferenceEqualityComparer.Instance);
        Entry E(FsNode n) => entries.TryGetValue(n, out Entry? e) ? e : entries[n] = new Entry { Node = n };

        // Directory entries, breadth first: where each directory's list starts and how long it is.
        var dirs = new List<FsNode> { tree.Root };
        long cursor = metaStart;
        long files = 1;
        for (int i = 0; i < dirs.Count; i++)
        {
            FsNode dir = dirs[i];
            List<FsNode> kids = Sorted(dir);
            long start = cursor;
            foreach (FsNode k in kids)
            {
                Entry e = E(k);
                e.Name = PosixName.Encode(k.Name);
                if (e.Name.Length > MaxNameLength)
                    throw new FsException($"'{k.Path}': the name is longer than {MaxNameLength} bytes, the limit of CramFS.");
                cursor += InodeSize + Align4(e.Name.Length);
                if (k.IsDirectory) dirs.Add(k);
                files++;
            }
            Entry de = E(dir);
            de.Size = Check(cursor - start, MaxFileSize, $"The directory '{dir.Path}' holds too many entries for CramFS (its list is limited to 16 MiB).");
            de.Offset = kids.Count == 0 ? 0 : Units(start);
        }

        // Data, depth first, into a temporary file; offsets are those it will have in the image.
        long dataStart = Align4(cursor);
        string spoolPath = Path.Combine(tempDirectory ?? Path.GetTempPath(), $"fsforge-cramfs-{Guid.NewGuid():N}.tmp");
        using var spool = new FileStream(spoolPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
        long totalBytes = tree.GetStats().DataBytes, doneBytes = 0;
        long blocks = 0;
        var byInode = new Dictionary<FsInode, uint>(ReferenceEqualityComparer.Instance);
        var byContent = new Dictionary<(bool, long, string), uint>();
        var linkNames = new Dictionary<FsInode, int>(ReferenceEqualityComparer.Instance);
        int workers = _o.Threads > 0 ? _o.Threads : Environment.ProcessorCount;

        void Data(FsNode dir)
        {
            foreach (FsNode k in Sorted(dir))
            {
                ct.ThrowIfCancellationRequested();
                FsInode ino = k.Inode;
                Entry e = E(k);
                if (ino.HasXAttrs) _xattrs++;
                if (ino.Type is not (FsNodeType.Directory or FsNodeType.File or FsNodeType.Symlink))
                {
                    if (ino.Type is FsNodeType.CharDevice or FsNodeType.BlockDevice)
                    {
                        if (ino.DevMajor > 255 || ino.DevMinor > 255) _bigDev++;
                        e.Size = ((ino.DevMajor & 0xFF) << 8) | (ino.DevMinor & 0xFF);
                    }
                    continue;
                }
                if (k.IsDirectory)
                {
                    Data(k);
                    continue;
                }
                if (ino.LinkCount > 1)
                {
                    int seen = linkNames.GetValueOrDefault(ino);
                    linkNames[ino] = seen + 1;
                    if (seen > 0) _links++;
                }
                byte[] content = ino.Type == FsNodeType.Symlink ? PosixName.Encode(ino.LinkTarget ?? "") : ReadAll(k);
                e.Size = Check(content.Length, MaxFileSize, $"'{k.Path}' is larger than 16 MiB, the limit of a file in CramFS.");
                if (content.Length == 0) continue;
                if (byInode.TryGetValue(ino, out uint known))
                {
                    e.Offset = known;
                    continue;
                }
                var key = (ino.Type == FsNodeType.Symlink, (long)content.Length, Convert.ToHexString(SHA256.HashData(content)));
                if (byContent.TryGetValue(key, out known))
                {
                    e.Offset = byInode[ino] = known;
                    continue;
                }
                long at = dataStart + Align4(spool.Length);
                spool.SetLength(at - dataStart);
                spool.Position = at - dataStart;
                if (at / 4 > 0x3FFFFFF) throw new FsException("The data does not fit into a CramFS image (offsets are limited to 256 MiB).");
                e.Offset = byInode[ino] = byContent[key] = Units(at);
                blocks += WriteFileData(spool, at, content, workers);
                doneBytes += ino.Type == FsNodeType.File ? content.Length : 0;
                progress?.Report(new ProgressInfo("write", doneBytes, totalBytes, k.Path));
            }
        }
        Data(tree.Root);

        long end = dataStart + spool.Length;
        long size = (end + _o.BlockSize - 1) / _o.BlockSize * _o.BlockSize;
        if (size > uint.MaxValue) throw new FsException("The image is too large for CramFS.");

        // Metadata: the entries of every directory in the order they were laid out.
        var meta = new MemoryStream();
        Span<byte> raw = stackalloc byte[InodeSize];
        foreach (FsNode dir in dirs)
        {
            foreach (FsNode k in Sorted(dir))
            {
                Entry e = entries[k];
                InodeOf(k.Inode, e).Write(raw, _big);
                meta.Write(raw);
                meta.Write(e.Name);
                meta.Write(new byte[Align4(e.Name.Length) - e.Name.Length]);
            }
        }
        if (metaStart + meta.Length != cursor) throw new InvalidOperationException("CramFS metadata layout mismatch.");
        meta.Write(new byte[dataStart - cursor]);

        // Superblock (the CRC field zero for now).
        byte[] sb = new byte[SuperSize];
        W32(sb, 0, Magic, _big);
        W32(sb, 4, (uint)size, _big);
        W32(sb, 8, FlagFsidV2 | FlagSortedDirs | (_o.Holes ? FlagHoles : 0), _big);
        Signature.CopyTo(sb.AsSpan(16));
        W32(sb, 36, _o.Edition, _big);
        W32(sb, 40, (uint)blocks, _big);
        W32(sb, 44, (uint)files, _big);
        Encoding.UTF8.GetBytes(_o.VolumeName).CopyTo(sb.AsSpan(48));
        InodeOf(tree.Root.Inode, entries[tree.Root]).Write(sb.AsSpan(RootInodeOffset), _big);

        byte[] tail = new byte[size - end];
        uint crc = Crc32.Update(0, sb);
        crc = Crc32.Update(crc, meta.GetBuffer().AsSpan(0, (int)meta.Length));
        byte[] buf = new byte[1 << 16];
        spool.Position = 0;
        int n;
        while ((n = spool.Read(buf)) > 0) crc = Crc32.Update(crc, buf.AsSpan(0, n));
        crc = Crc32.Update(crc, tail);
        W32(sb, 32, crc, _big);

        if (superStart > 0) output.Write(new byte[superStart]);
        output.Write(sb);
        output.Write(meta.GetBuffer().AsSpan(0, (int)meta.Length));
        spool.Position = 0;
        spool.CopyTo(output);
        output.Write(tail);
        output.Flush();

        if (_bigUid > 0) _warnings.Add($"{_bigUid} object(s) have an owner above 65535, which CramFS cannot store: it was cut to 16 bits.");
        if (_bigGid > 0) _warnings.Add($"{_bigGid} object(s) have a group above 255: CramFS keeps only 8 bits of it (as mkfs.cramfs does).");
        if (_bigDev > 0) _warnings.Add($"{_bigDev} device(s) have a major or minor number above 255, which CramFS cannot store: it was cut to 8 bits.");
        if (_links > 0) _warnings.Add($"CramFS has no hard links: {_links} additional name(s) of hard-linked files became separate files that share the data.");
        if (_xattrs > 0) _warnings.Add($"{_xattrs} object(s) have extended attributes (for example SELinux labels or capabilities), which CramFS does not store: they were left out.");
    }

    private Inode InodeOf(FsInode ino, Entry e)
    {
        uint uid = _o.AllRoot ? 0 : ino.Uid, gid = _o.AllRoot ? 0 : ino.Gid;
        if (uid > 0xFFFF) _bigUid++;
        if (gid > 0xFF) _bigGid++;
        int mode = UnixMode.TypeBits(ino.Type) | (ino.Type == FsNodeType.Symlink ? 0x1FF : ino.Mode & 0xFFF);
        int nameLen = e.Name.Length == 0 ? 0 : Align4(e.Name.Length) / 4;
        return new Inode(mode, uid & 0xFFFF, e.Size, gid & 0xFF, nameLen, e.Offset);
    }

    /// <summary>The block pointer table and the compressed blocks of one file; returns the number of blocks.</summary>
    private long WriteFileData(FileStream spool, long at, byte[] content, int workers)
    {
        int bs = _o.BlockSize;
        int count = (content.Length + bs - 1) / bs;
        var packed = new byte[count][];
        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = count > 1 ? workers : 1 }, i =>
        {
            ReadOnlySpan<byte> plain = content.AsSpan(i * bs, Math.Min(bs, content.Length - i * bs));
            if (_o.Holes && !plain.ContainsAnyExcept((byte)0))
            {
                packed[i] = Array.Empty<byte>();
                return;
            }
            byte[] dst = new byte[plain.Length + plain.Length / 8 + 64];
            int n = new ZlibCodec(_o.Level).Compress(plain, dst);
            if (n <= 0 || n > 2 * bs) throw new FsException("A CramFS block could not be compressed.");
            packed[i] = dst.AsSpan(0, n).ToArray();
        });
        byte[] table = new byte[count * 4];
        long pos = at + table.Length;
        for (int i = 0; i < count; i++)
        {
            pos += packed[i].Length;
            if (pos > uint.MaxValue) throw new FsException("The image is too large for CramFS.");
            W32(table, i * 4, (uint)pos, _big);
        }
        spool.Write(table);
        foreach (byte[] p in packed) spool.Write(p);
        return count;
    }

    private static byte[] ReadAll(FsNode node)
    {
        long size = node.Inode.Size;
        if (size > MaxFileSize) throw new FsException($"'{node.Path}' is larger than 16 MiB, the limit of a file in CramFS.");
        byte[] data = new byte[size];
        if (size == 0) return data;
        using Stream s = node.Inode.Data!.OpenRead();
        int got = 0;
        while (got < data.Length)
        {
            int n = s.Read(data, got, data.Length - got);
            if (n <= 0) throw new FsException($"'{node.Path}' became shorter while it was being written.");
            got += n;
        }
        return data;
    }

    /// <summary>Entries in the order the kernel's lookup expects: by the bytes of their names.</summary>
    private static List<FsNode> Sorted(FsNode dir)
    {
        var list = dir.Children.ToList();
        list.Sort((a, b) => PosixName.Encode(a.Name).AsSpan().SequenceCompareTo(PosixName.Encode(b.Name)));
        return list;
    }

    private static int Align4(int n) => (n + 3) & ~3;
    private static long Align4(long n) => (n + 3) & ~3L;

    private static uint Units(long offset)
    {
        if ((offset & 3) != 0) throw new InvalidOperationException("Unaligned CramFS offset.");
        if (offset / 4 > 0x3FFFFFF) throw new FsException("The metadata does not fit into a CramFS image (offsets are limited to 256 MiB).");
        return (uint)(offset / 4);
    }

    private static uint Check(long value, long max, string message) => value > max ? throw new FsException(message) : (uint)value;
}
