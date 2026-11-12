using System.Globalization;
using System.Text;

namespace FsForge.Core.Tar;

/// <summary>
/// Writes a tree as a tar archive, equivalent to
/// "tar -c --numeric-owner --xattrs -C rootfs ." followed by the selected compressor.
/// </summary>
public sealed class TarWriter
{
    private const int Block = 512;
    private const long OctalMax7 = (1L << 21) - 1;      // seven octal digits
    private const long OctalMax11 = (1L << 33) - 1;     // eleven octal digits

    private readonly Stream _out;
    private readonly TarOptions _opt;
    private readonly List<string> _warnings = new();
    private readonly byte[] _hdr = new byte[Block];
    private readonly byte[] _prefix;
    private long _droppedXattrs;

    private TarWriter(Stream output, TarOptions options)
    {
        _out = output;
        _opt = options;
        _prefix = PosixName.Encode(options.PathPrefix);
    }

    /// <summary>Writes the subtree below <paramref name="start"/> (the whole tree when null) to <paramref name="output"/>.</summary>
    public static IReadOnlyList<string> Write(FsTree tree, Stream output, TarOptions options, FsNode? start = null,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        options.Validate();
        start ??= tree.Root;
        if (!start.IsDirectory) throw new FsException("Only a directory can be exported as an archive.");

        Stream compressed = ArchiveCodec.OpenCompressor(output, options);
        var w = new TarWriter(compressed, options);
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
        long total = 0, done = 0;
        var counted = new HashSet<FsInode>(ReferenceEqualityComparer.Instance);
        foreach (FsNode n in FsTree.Walk(start))
            if (n.Type == FsNodeType.File && counted.Add(n.Inode)) total += n.Inode.Size;

        var firstPath = new Dictionary<FsInode, byte[]>(ReferenceEqualityComparer.Instance);
        byte[] buffer = new byte[1 << 17];

        foreach (FsNode node in FsTree.Walk(start))
        {
            ct.ThrowIfCancellationRequested();
            bool isStart = ReferenceEquals(node, start);
            if (isStart && !_opt.IncludeRootEntry) continue;

            byte[] path = MemberPath(node, start);
            FsInode inode = node.Inode;

            if (!node.IsDirectory && inode.LinkCount > 1)
            {
                if (firstPath.TryGetValue(inode, out byte[]? target))
                {
                    WriteHeader(path, target, '1', inode, 0, withXattrs: false);
                    continue;
                }
                firstPath.Add(inode, path);
            }

            switch (inode.Type)
            {
                case FsNodeType.Directory:
                    WriteHeader(path, null, '5', inode, 0, true);
                    break;
                case FsNodeType.Symlink:
                    WriteHeader(path, PosixName.Encode(inode.LinkTarget ?? ""), '2', inode, 0, true);
                    break;
                case FsNodeType.CharDevice:
                    WriteHeader(path, null, '3', inode, 0, true);
                    break;
                case FsNodeType.BlockDevice:
                    WriteHeader(path, null, '4', inode, 0, true);
                    break;
                case FsNodeType.Fifo:
                    WriteHeader(path, null, '6', inode, 0, true);
                    break;
                case FsNodeType.Socket:
                    _warnings.Add($"'{node.Path}': sockets cannot be stored in a tar archive; the entry was skipped.");
                    break;
                case FsNodeType.File:
                {
                    long size = inode.Size;
                    WriteHeader(path, null, '0', inode, size, true);
                    if (size > 0)
                    {
                        using Stream s = inode.Data!.OpenRead();
                        long left = size;
                        while (left > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            int n = s.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                            if (n <= 0) throw new FsException($"'{node.Path}': unexpected end of the file data.");
                            _out.Write(buffer, 0, n);
                            left -= n;
                            done += n;
                            progress?.Report(new ProgressInfo("data", done, total, node.Path));
                        }
                        Pad(size);
                    }
                    break;
                }
            }
        }

        // End of archive: two zero blocks. No padding to a 10 KiB record is added; readers do not need it.
        Array.Clear(_hdr);
        _out.Write(_hdr);
        _out.Write(_hdr);
        if (_droppedXattrs > 0)
            _warnings.Add($"Extended attributes of {_droppedXattrs} entries were not stored: the GNU tar format cannot hold them (use pax).");
        progress?.Report(new ProgressInfo("done", total, total));
    }

    private byte[] MemberPath(FsNode node, FsNode start)
    {
        if (ReferenceEquals(node, start))
        {
            // The root entry: "./" for the usual prefix, otherwise the prefix itself.
            return _prefix.Length > 0 ? _prefix : "./"u8.ToArray();
        }
        var parts = new Stack<byte[]>();
        int length = _prefix.Length;
        for (FsNode? n = node; n is not null && !ReferenceEquals(n, start); n = n.Parent)
        {
            byte[] b = PosixName.Encode(n.Name);
            parts.Push(b);
            length += b.Length + 1;
        }
        bool dir = node.IsDirectory;
        byte[] path = new byte[length - 1 + (dir ? 1 : 0)];
        _prefix.CopyTo(path, 0);
        int p = _prefix.Length;
        bool first = true;
        foreach (byte[] b in parts)
        {
            if (!first) path[p++] = (byte)'/';
            b.CopyTo(path, p);
            p += b.Length;
            first = false;
        }
        if (dir) path[p] = (byte)'/';
        return path;
    }

    private void Pad(long size)
    {
        int rest = (int)(size & (Block - 1));
        if (rest == 0) return;
        Span<byte> zeros = stackalloc byte[Block];
        zeros.Clear();
        _out.Write(zeros[..(Block - rest)]);
    }

    // -------------------------------------------------------------- headers

    private static bool SplitUstar(ReadOnlySpan<byte> path, out int split)
    {
        // name: up to 100 bytes; prefix: up to 155 bytes, joined with '/'.
        split = -1;
        if (path.Length <= 100) return true;
        if (path.Length > 256) return false;
        for (int i = Math.Min(155, path.Length - 2); i > 0; i--)
        {
            if (path[i] != (byte)'/') continue;
            if (path.Length - i - 1 <= 100)
            {
                split = i;
                return true;
            }
            return false;
        }
        return false;
    }

    private void WriteHeader(byte[] path, byte[]? link, char type, FsInode inode, long size, bool withXattrs)
    {
        uint uid = _opt.AllRoot ? 0 : inode.Uid;
        uint gid = _opt.AllRoot ? 0 : inode.Gid;
        long mtime = inode.MTime;
        bool device = type is '3' or '4';
        bool nameFits = SplitUstar(path, out int split);
        bool linkFits = link is null || link.Length <= 100;
        bool pax = _opt.Dialect == TarDialect.Pax;
        bool hasXattrs = withXattrs && _opt.StoreXattrs && inode.HasXAttrs;

        if (pax)
        {
            var records = new MemoryStream();
            if (!nameFits) AddRecord(records, "path", path);
            if (!linkFits) AddRecord(records, "linkpath", link!);
            if (uid > OctalMax7) AddRecord(records, "uid", Ascii(uid));
            if (gid > OctalMax7) AddRecord(records, "gid", Ascii(gid));
            if (size > OctalMax11) AddRecord(records, "size", Ascii(size));
            if (mtime is < 0 or > OctalMax11) AddRecord(records, "mtime", Ascii(mtime));
            if (device && inode.DevMajor > OctalMax7) AddRecord(records, "SCHILY.devmajor", Ascii(inode.DevMajor));
            if (device && inode.DevMinor > OctalMax7) AddRecord(records, "SCHILY.devminor", Ascii(inode.DevMinor));
            if (hasXattrs)
                foreach ((string name, byte[] value) in inode.XAttrs!)
                    AddRecord(records, "SCHILY.xattr." + name, value);

            if (records.Length > 0)
            {
                // The extended header is an ordinary member of type 'x' that precedes the real one.
                byte[] paxName = PaxHeaderName(path);
                FillHeader(paxName, -1, null, 'x', 0x1A4, 0, 0, records.Length, Math.Clamp(mtime, 0, OctalMax11), 0, 0, gnu: false);
                _out.Write(_hdr);
                records.Position = 0;
                records.CopyTo(_out);
                Pad(records.Length);
            }
        }
        else
        {
            if (hasXattrs) _droppedXattrs++;
            if (!linkFits) WriteGnuLong('K', link!);
            if (!nameFits) WriteGnuLong('L', path);
        }

        ReadOnlySpan<byte> shortPath = nameFits ? path : Truncate(path, 100);
        ReadOnlySpan<byte> shortLink = link is null ? default : linkFits ? link : Truncate(link, 100);
        FillHeader(shortPath, nameFits ? split : -1, link is null ? null : shortLink.ToArray(), type, inode.Type == FsNodeType.Symlink ? 0x1FF : inode.Mode & 0xFFF,
            uid, gid, size, mtime, device ? inode.DevMajor : 0, device ? inode.DevMinor : 0, gnu: !pax);
        _out.Write(_hdr);
    }

    private static ReadOnlySpan<byte> Truncate(ReadOnlySpan<byte> value, int max) => value.Length <= max ? value : value[..max];

    private static byte[] Ascii(long value) => Encoding.ASCII.GetBytes(value.ToString(CultureInfo.InvariantCulture));

    private static byte[] PaxHeaderName(ReadOnlySpan<byte> path)
    {
        // "PaxHeaders/<last component>", shortened to fit the 100-byte name field.
        ReadOnlySpan<byte> trimmed = path.Length > 1 && path[^1] == (byte)'/' ? path[..^1] : path;
        int slash = trimmed.LastIndexOf((byte)'/');
        ReadOnlySpan<byte> leaf = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        if (leaf.Length > 80) leaf = leaf[..80];
        byte[] name = new byte[11 + leaf.Length];
        "PaxHeaders/"u8.CopyTo(name);
        leaf.CopyTo(name.AsSpan(11));
        return name;
    }

    private static void AddRecord(MemoryStream ms, string key, byte[] value)
    {
        // "<length> <key>=<value>\n" where the length includes its own digits.
        byte[] k = Encoding.UTF8.GetBytes(key);
        int body = 1 + k.Length + 1 + value.Length + 1;
        int len = body + 1;
        while (true)
        {
            int digits = len.ToString(CultureInfo.InvariantCulture).Length;
            if (body + digits == len) break;
            len = body + digits;
        }
        ms.Write(Encoding.ASCII.GetBytes(len.ToString(CultureInfo.InvariantCulture)));
        ms.WriteByte((byte)' ');
        ms.Write(k);
        ms.WriteByte((byte)'=');
        ms.Write(value);
        ms.WriteByte((byte)'\n');
    }

    private void WriteGnuLong(char type, byte[] value)
    {
        FillHeader("././@LongLink"u8, -1, null, type, 0x1A4, 0, 0, value.Length + 1, 0, 0, 0, gnu: true);
        _out.Write(_hdr);
        _out.Write(value);
        _out.WriteByte(0);
        Pad(value.Length + 1);
    }

    private static void Number(Span<byte> field, long value, bool gnu)
    {
        if (TarNumbers.TryWriteOctal(field, value)) return;
        if (gnu)
        {
            TarNumbers.WriteBase256(field, value);
            return;
        }
        // Pax: the real value lives in the extended header; the classic field gets a harmless stand-in.
        TarNumbers.TryWriteOctal(field, 0);
    }

    private void FillHeader(ReadOnlySpan<byte> path, int split, byte[]? link, char type, int mode, uint uid, uint gid,
        long size, long mtime, uint major, uint minor, bool gnu)
    {
        Span<byte> h = _hdr;
        h.Clear();
        if (split >= 0)
        {
            path[..split].CopyTo(h.Slice(345, 155));
            path[(split + 1)..].CopyTo(h[..100]);
        }
        else
        {
            path.CopyTo(h[..100]);
        }
        Number(h.Slice(100, 8), mode, gnu);
        Number(h.Slice(108, 8), uid, gnu);
        Number(h.Slice(116, 8), gid, gnu);
        Number(h.Slice(124, 12), size, gnu);
        Number(h.Slice(136, 12), mtime, gnu);
        h[156] = (byte)type;
        link?.CopyTo(h.Slice(157, 100));
        if (gnu) "ustar  \0"u8.CopyTo(h.Slice(257, 8));
        else "ustar\000"u8.CopyTo(h.Slice(257, 8));
        if (type is '3' or '4')
        {
            Number(h.Slice(329, 8), major, gnu);
            Number(h.Slice(337, 8), minor, gnu);
        }
        else if (!gnu)
        {
            TarNumbers.TryWriteOctal(h.Slice(329, 8), 0);
            TarNumbers.TryWriteOctal(h.Slice(337, 8), 0);
        }

        // Checksum: sum of all bytes with the checksum field taken as spaces.
        h.Slice(148, 8).Fill((byte)' ');
        long sum = 0;
        foreach (byte b in h) sum += b;
        TarNumbers.TryWriteOctal(h.Slice(148, 7), sum);
        h[155] = (byte)' ';
    }
}
