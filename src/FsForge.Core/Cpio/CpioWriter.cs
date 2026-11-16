using FsForge.Core.Tar;
using static FsForge.Core.Cpio.CpioFormat;

namespace FsForge.Core.Cpio;

/// <summary>
/// Writes a tree as a cpio archive in the newc (or crc) format, the one the Linux kernel unpacks as an
/// initramfs. Entries come in the order "find" lists them (a directory before what it holds), hard links
/// share an inode number and carry the data with their last name (as GNU cpio and the kernel's
/// gen_init_cpio do), and the archive ends with "TRAILER!!!".
/// </summary>
public sealed class CpioWriter
{
    private readonly Stream _out;
    private readonly CpioOptions _opt;
    private readonly List<string> _warnings = new();
    private readonly byte[] _hdr = new byte[NewcHeaderSize];
    private readonly byte[] _prefix;
    private long _pos;
    private long _clampedTimes, _xattrs;

    private CpioWriter(Stream output, CpioOptions options)
    {
        _out = output;
        _opt = options;
        _prefix = PosixName.Encode(options.PathPrefix);
    }

    /// <summary>Writes the subtree below <paramref name="start"/> (the whole tree when null) to <paramref name="output"/>.</summary>
    public static IReadOnlyList<string> Write(FsTree tree, Stream output, CpioOptions options, FsNode? start = null,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        options.Validate();
        start ??= tree.Root;
        if (!start.IsDirectory) throw new FsException("Only a directory can be written as a cpio archive.");

        Stream compressed = ArchiveCodec.OpenCompressor(output, options.CompressorOptions());
        var w = new CpioWriter(compressed, options);
        try
        {
            w.WriteAll(start, progress, ct);
        }
        finally
        {
            // Disposing the compressor writes its trailer; the caller's stream stays open.
            compressed.Dispose();
        }
        output.Flush();
        return w._warnings;
    }

    private void WriteAll(FsNode start, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var nodes = FsTree.Walk(start).ToList();
        long total = 0, done = 0;
        var counted = new HashSet<FsInode>(ReferenceEqualityComparer.Instance);
        // The name that carries the data of a hard-linked file: its last one in the archive.
        var lastName = new Dictionary<FsInode, FsNode>(ReferenceEqualityComparer.Instance);
        var names = new Dictionary<FsInode, int>(ReferenceEqualityComparer.Instance);
        foreach (FsNode n in nodes)
        {
            if (n.Type == FsNodeType.File && counted.Add(n.Inode)) total += n.Inode.Size;
            if (n.IsDirectory || ReferenceEquals(n, start)) continue;
            lastName[n.Inode] = n;
            names[n.Inode] = names.GetValueOrDefault(n.Inode) + 1;
        }

        var inodeNumbers = new Dictionary<FsInode, long>(ReferenceEqualityComparer.Instance);
        long nextIno = 1;
        byte[] buffer = new byte[1 << 17];
        foreach (FsNode node in nodes)
        {
            ct.ThrowIfCancellationRequested();
            bool isStart = ReferenceEquals(node, start);
            if (isStart && !_opt.IncludeRootEntry) continue;
            FsInode inode = node.Inode;
            if (!inodeNumbers.TryGetValue(inode, out long ino))
            {
                ino = nextIno++;
                inodeNumbers.Add(inode, ino);
            }
            if (inode.HasXAttrs) _xattrs++;

            byte[] name = isStart ? "."u8.ToArray() : MemberName(node, start);
            long links = node.IsDirectory
                ? 2 + node.Children.Count(c => c.IsDirectory)
                : isStart ? 1 : names.GetValueOrDefault(inode, 1);
            int mode = UnixMode.TypeBits(inode.Type) | (inode.Type == FsNodeType.Symlink ? 0x1FF : inode.Mode & 0xFFF);

            byte[]? linkTarget = null;
            long size = 0;
            bool carriesData = false;
            switch (inode.Type)
            {
                case FsNodeType.Symlink:
                    linkTarget = PosixName.Encode(inode.LinkTarget ?? "");
                    size = linkTarget.Length;
                    break;
                case FsNodeType.File:
                    carriesData = links <= 1 || ReferenceEquals(lastName.GetValueOrDefault(inode), node);
                    if (carriesData) size = inode.Size;
                    if (size > uint.MaxValue)
                        throw new FsException($"'{node.Path}' is larger than 4 GiB, the limit of a file in a cpio archive.");
                    break;
            }

            uint check = 0;
            if (_opt.Header == CpioHeader.Crc && carriesData && size > 0) check = Sum(inode.Data!, buffer);
            WriteHeader(name, ino, mode, inode, links, size, check);
            if (linkTarget is not null) Write(linkTarget);
            else if (carriesData && size > 0)
            {
                using Stream s = inode.Data!.OpenRead();
                long left = size;
                while (left > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = s.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (n <= 0) throw new FsException($"'{node.Path}' became shorter while it was being written.");
                    Write(buffer.AsSpan(0, n));
                    left -= n;
                    done += n;
                    progress?.Report(new ProgressInfo("archive", done, total, node.Path));
                }
            }
            Pad(4);
        }

        // The trailer, then zeros up to a multiple of 512 bytes.
        WriteHeader(System.Text.Encoding.ASCII.GetBytes(Trailer), 0, 0, null, 1, 0, 0);
        Pad(4);
        if (_opt.Pad512) Pad(512);

        if (_clampedTimes > 0)
            _warnings.Add($"{_clampedTimes} object(s) have a modification time before 1970 or after 2106, which a cpio header cannot hold; it was clamped.");
        if (_xattrs > 0)
            _warnings.Add($"{_xattrs} object(s) have extended attributes (for example SELinux labels or capabilities), which cpio does not store: they were left out.");
    }

    private byte[] MemberName(FsNode node, FsNode start)
    {
        string relative = start.IsRoot ? node.RelativePath : node.Path[(start.Path.Length + 1)..];
        byte[] rel = PosixName.Encode(relative);
        byte[] name = new byte[_prefix.Length + rel.Length];
        _prefix.CopyTo(name, 0);
        rel.CopyTo(name, _prefix.Length);
        return name;
    }

    private void WriteHeader(byte[] name, long ino, int mode, FsInode? inode, long links, long size, uint check)
    {
        long mtime = inode?.MTime ?? 0;
        if (inode is not null && (mtime < 0 || mtime > uint.MaxValue))
        {
            _clampedTimes++;
            mtime = Math.Clamp(mtime, 0, uint.MaxValue);
        }
        uint uid = inode is null || _opt.AllRoot ? 0 : inode.Uid, gid = inode is null || _opt.AllRoot ? 0 : inode.Gid;
        bool device = inode?.Type is FsNodeType.CharDevice or FsNodeType.BlockDevice;
        Span<byte> h = _hdr;
        (_opt.Header == CpioHeader.Crc ? MagicCrc : MagicNewc).CopyTo(h);
        WriteHex(h[6..], ino);
        WriteHex(h[14..], mode);
        WriteHex(h[22..], uid);
        WriteHex(h[30..], gid);
        WriteHex(h[38..], links);
        WriteHex(h[46..], mtime);
        WriteHex(h[54..], size);
        WriteHex(h[62..], 0);                               // the device the file was on: none
        WriteHex(h[70..], 0);
        WriteHex(h[78..], device ? inode!.DevMajor : 0);
        WriteHex(h[86..], device ? inode!.DevMinor : 0);
        WriteHex(h[94..], name.Length + 1);
        WriteHex(h[102..], check);
        Write(h);
        Write(name);
        Write(stackalloc byte[1]);                          // the name ends with a NUL
        Pad(4);
    }

    private void Write(ReadOnlySpan<byte> data)
    {
        _out.Write(data);
        _pos += data.Length;
    }

    private static readonly byte[] Zeros = new byte[512];

    private void Pad(int unit)
    {
        int n = (int)((unit - _pos % unit) % unit);
        if (n > 0) Write(Zeros.AsSpan(0, n));
    }

    /// <summary>The checksum of the crc format: the sum of all bytes of the file.</summary>
    private static uint Sum(IFileData data, byte[] buffer)
    {
        uint sum = 0;
        using Stream s = data.OpenRead();
        int n;
        while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
            for (int i = 0; i < n; i++) sum += buffer[i];
        return sum;
    }
}
