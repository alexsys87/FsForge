using System.Text;
using FsForge.Core.Compression;
using static FsForge.Core.Jffs2.Jffs2Format;

namespace FsForge.Core.Jffs2;

/// <summary>
/// Reader for JFFS2 images of either byte order. JFFS2 is a log: the image is scanned from start to
/// end, every valid node is collected, and the newest version of each directory entry and each piece
/// of file data wins, the way the kernel builds its view at mount time. Nodes with a bad checksum
/// (an interrupted write) are skipped, so an image of a device that lost power reads like the kernel
/// would read it.
/// </summary>
public sealed class Jffs2Reader : IDisposable
{
    private readonly IBlockSource _source;
    private readonly bool _ownsSource;
    private readonly bool _be;

    // Scan results.
    private readonly Dictionary<uint, List<InodeNode>> _inodes = new();
    private readonly Dictionary<(uint Pino, string Name), DirentNode> _dirents = new();
    private readonly Dictionary<uint, XattrNode> _xattrs = new();
    private readonly Dictionary<(uint Ino, uint Xid), uint> _xrefs = new();
    private readonly Dictionary<Jffs2Compression, int> _compression = new();

    private Jffs2Reader(IBlockSource source, bool ownsSource, bool be)
    {
        _source = source;
        _ownsSource = ownsSource;
        _be = be;
        Info = null!;
    }

    public Jffs2Info Info { get; private set; }
    public List<string> Warnings { get; } = new();

    private sealed class InodeNode
    {
        public uint Version, Mode, ISize, MTime, Offset, CSize, DSize;
        public ushort Uid, Gid;
        public Jffs2Compression Compr;
        public long DataPos;
    }

    private sealed class DirentNode
    {
        public uint Version, Ino;
        public byte Type;
    }

    private sealed class XattrNode
    {
        public uint Version;
        public byte Prefix;
        public byte[] Name = Array.Empty<byte>(), Value = Array.Empty<byte>();
    }

    /// <summary>How far into a file a JFFS2 image may start (behind erased blocks of a flash dump).</summary>
    private const int SearchLimit = 4 << 20;

    // ---------------------------------------------------------- detection

    public static bool IsJffs2(IBlockSource source) => FirstNode(source, out _) >= 0;

    /// <summary>Offset of the first node (behind leading erased space), or -1.</summary>
    private static long FirstNode(IBlockSource source, out bool bigEndian)
    {
        bigEndian = false;
        long limit = Math.Min(source.Length, SearchLimit);
        byte[] buf = new byte[64 * 1024];
        for (long pos = 0; pos < limit; pos += buf.Length - HeaderSize)
        {
            int n = source.Read(pos, buf.AsSpan(0, (int)Math.Min(buf.Length, source.Length - pos)));
            if (n < HeaderSize) return -1;
            for (int i = 0; i + HeaderSize <= n; i += 4)
            {
                if (buf[i] == 0xFF && buf[i + 1] == 0xFF && buf[i + 2] == 0xFF && buf[i + 3] == 0xFF) continue;
                // The first thing that is not erased space must be a node.
                if (HeaderOrder(buf.AsSpan(i, HeaderSize)) is not { } be) return -1;
                bigEndian = be;
                return pos + i;
            }
        }
        return -1;
    }

    public static Jffs2Reader Open(IBlockSource source, bool ownsSource = true, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        try
        {
            long first = FirstNode(source, out bool be);
            if (first < 0) throw new FsException("Not a JFFS2 image.");
            var r = new Jffs2Reader(source, ownsSource, be);
            r.Scan(first, progress, ct);
            return r;
        }
        catch
        {
            if (ownsSource) source.Dispose();
            throw;
        }
    }

    // --------------------------------------------------------------- scan

    /// <summary>Window over the image for the scan, which reads it from start to end.</summary>
    private sealed class Window(IBlockSource source)
    {
        private readonly byte[] _buf = new byte[1 << 20];
        private long _start = -1;
        private int _length;

        /// <summary>The bytes at <paramref name="pos"/>; shorter than asked only at the end of the image.</summary>
        public ReadOnlySpan<byte> At(long pos, int count)
        {
            if (count > _buf.Length) return source.ReadBytes(pos, (int)Math.Min(count, source.Length - pos));
            if (_start < 0 || pos < _start || pos + count > _start + _length)
            {
                _start = pos;
                _length = source.Read(pos, _buf.AsSpan(0, (int)Math.Min(_buf.Length, source.Length - pos)));
                if (_length < 0) _length = 0;
            }
            int off = (int)(pos - _start);
            return _buf.AsSpan(off, Math.Min(count, _length - off));
        }
    }

    private void Scan(long first, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var win = new Window(_source);
        long length = _source.Length, pos = first, used = first;
        int nodes = 0, obsolete = 0, damaged = 0, cleanmarkerSize = 0;
        bool summary = false, oldMagic = false;
        var cleanmarkers = new List<long>();
        var spans = new List<(long Start, long End)>();
        var gapEnds = new List<long>();          // where a node follows a stretch of erased space
        long gapStart = -1;
        long nextReport = 0;

        while (pos + HeaderSize <= length)
        {
            if (pos >= nextReport)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new ProgressInfo("scan", pos, length));
                nextReport = pos + (4 << 20);
            }
            ReadOnlySpan<byte> h = win.At(pos, HeaderSize);
            if (h.Length < HeaderSize) break;
            if (U32(h, 0, false) == 0xFFFFFFFF)
            {
                if (gapStart < 0) gapStart = pos;
                pos += 4;          // erased space
                continue;
            }
            long erasedBefore = gapStart < 0 ? 0 : pos - gapStart;
            gapStart = -1;
            ushort magic = U16(h, 0, _be);
            if (magic == OldMagic)
            {
                oldMagic = true;
                pos += 4;
                continue;
            }
            if (magic != Magic || !HeaderCrcValid(h, _be))
            {
                // Not a node: dirt between nodes, or the remains of an interrupted write.
                if (magic == Magic) damaged++;
                pos += 4;
                continue;
            }
            ushort type = U16(h, 2, _be);
            uint totlen = U32(h, 4, _be);
            if (totlen < HeaderSize || pos + totlen > length)
            {
                damaged++;
                pos += 4;
                continue;
            }
            long next = Pad4(pos + totlen);
            if ((type & Accurate) == 0)
            {
                // Marked obsolete by the kernel: its content has been written again elsewhere.
                obsolete++;
                pos = next;
                continue;
            }

            bool ok = true;
            switch (type)
            {
                case NodeInode: ok = ScanInode(win, pos, totlen); break;
                case NodeDirent: ok = ScanDirent(win, pos, totlen); break;
                case NodeXattr: ok = ScanXattr(win, pos, totlen); break;
                case NodeXref: ok = ScanXref(win, pos, totlen); break;
                case NodeCleanmarker:
                    cleanmarkers.Add(pos);
                    cleanmarkerSize = (int)totlen;
                    break;
                case NodePadding: break;
                case NodeSummary: summary = true; break;
                default:
                    switch (type & CompatMask)
                    {
                        case Incompat:
                            throw new FsException($"The JFFS2 image has a node of an unknown incompatible type 0x{type:X4} at 0x{pos:X}; it cannot be read safely.");
                        case RoCompat:
                            Warnings.Add($"The JFFS2 image has nodes of an unknown type 0x{type:X4}; the kernel would mount it read-only.");
                            break;
                    }
                    break;
            }
            if (!ok)
            {
                damaged++;
                pos += 4;
                continue;
            }
            nodes++;
            if (type is not (NodeCleanmarker or NodePadding)) spans.Add((pos, pos + totlen));
            // mkfs.jffs2 and the kernel leave the end of an erase block erased when the next node does not fit.
            if (erasedBefore >= 16) gapEnds.Add(pos);
            used = pos + totlen;
            pos = next;
        }
        progress?.Report(new ProgressInfo("scan", length, length));
        if (oldMagic) Warnings.Add("The image has nodes of the pre-2001 JFFS2 format (magic 0x1984), which are not read.");
        if (damaged > 0)
            Warnings.Add($"{damaged} damaged JFFS2 node(s) were skipped (bad checksums, usually the remains of an interrupted write).");

        (int eraseSize, bool known) = EraseBlockSize(cleanmarkers, gapEnds, spans, used);
        Jffs2Compression main = _compression.Where(p => p.Key is not (Jffs2Compression.None or Jffs2Compression.Zero))
            .OrderByDescending(p => p.Value).Select(p => p.Key).DefaultIfEmpty(Jffs2Compression.Zlib).First();
        Info = new Jffs2Info
        {
            BigEndian = _be,
            EraseBlockSize = eraseSize,
            EraseBlockSizeKnown = known,
            Cleanmarkers = cleanmarkers.Count > 0,
            CleanmarkerSize = cleanmarkerSize > 0 ? cleanmarkerSize : 12,
            MainCompression = main,
            CompressionCounts = new Dictionary<Jffs2Compression, int>(_compression),
            Nodes = nodes,
            ObsoleteNodes = obsolete,
            DamagedNodes = damaged,
            HasSummary = summary,
            HasXattrs = _xattrs.Count > 0,
            UsedBytes = used,
            ImageBytes = length,
        };
        foreach (Jffs2Compression c in _compression.Keys)
            if (c is Jffs2Compression.RubinMips or Jffs2Compression.DynRubin or Jffs2Compression.Lzma or Jffs2Compression.Copy)
                Warnings.Add($"Some files are compressed with {c.ToString().ToLowerInvariant()}, which this program cannot unpack; reading them fails.");
    }

    /// <summary>
    /// The erase block size. Clean markers start every erase block, so their spacing gives it exactly.
    /// Otherwise the stretches of erased space that the writer leaves at the end of a block show where
    /// blocks end; an image without such gaps fits into one block. Nodes never cross a block boundary,
    /// which every candidate is checked against.
    /// </summary>
    private static (int Size, bool Known) EraseBlockSize(List<long> cleanmarkers, List<long> gapEnds, List<(long Start, long End)> spans, long used)
    {
        const int Min = 4096, Max = 16 << 20, Default = 64 * 1024;
        if (cleanmarkers.Count >= 2)
        {
            long g = 0;
            foreach (long c in cleanmarkers) g = Gcd(g, c - cleanmarkers[0]);
            g = Gcd(g, cleanmarkers[0]);
            if (g >= Min && g <= Max && (g & (g - 1)) == 0) return ((int)g, true);
        }

        bool Fits(long size)
        {
            foreach ((long s, long e) in spans)
                if (s / size != (e - 1) / size) return false;
            return true;
        }

        if (gapEnds.Count > 0)
        {
            // The largest power of two that divides every place where a block ended.
            long size = Max;
            foreach (long g in gapEnds)
                while (size > Min && g % size != 0) size >>= 1;
            if (gapEnds.All(g => g % size == 0) && Fits(size)) return ((int)size, false);
        }
        // Everything in one block (with clean markers there would otherwise be a second one): the block is at least that large.
        long one = Default;
        while (one < used && one < Max) one <<= 1;
        return Fits(one) ? ((int)one, false) : (Default, false);
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return Math.Abs(a);
    }

    private bool ScanInode(Window win, long pos, uint totlen)
    {
        if (totlen < InodeSize) return false;
        ReadOnlySpan<byte> n = win.At(pos, InodeSize);
        if (n.Length < InodeSize || Crc(n[..60]) != U32(n, 64, _be)) return false;
        var node = new InodeNode
        {
            Version = U32(n, 16, _be),
            Mode = U32(n, 20, _be),
            Uid = U16(n, 24, _be),
            Gid = U16(n, 26, _be),
            ISize = U32(n, 28, _be),
            MTime = U32(n, 36, _be),
            Offset = U32(n, 44, _be),
            CSize = U32(n, 48, _be),
            DSize = U32(n, 52, _be),
            Compr = (Jffs2Compression)n[56],
            DataPos = pos + InodeSize,
        };
        uint ino = U32(n, 12, _be);
        uint dataCrc = U32(n, 60, _be);
        if (InodeSize + (long)node.CSize > totlen || node.DSize > 1 << 24) return false;
        if (node.Compr != Jffs2Compression.Zero && node.CSize == 0 && node.DSize > 0) return false;
        if (node.Compr == Jffs2Compression.Zero ? node.CSize != 0 : node.CSize > 0 && Crc(win.At(node.DataPos, (int)node.CSize)) != dataCrc)
            return false;
        if (node.DSize > 0) _compression[node.Compr] = _compression.GetValueOrDefault(node.Compr) + 1;
        if (!_inodes.TryGetValue(ino, out List<InodeNode>? list)) _inodes[ino] = list = new List<InodeNode>();
        list.Add(node);
        return true;
    }

    private bool ScanDirent(Window win, long pos, uint totlen)
    {
        if (totlen < DirentSize) return false;
        ReadOnlySpan<byte> n = win.At(pos, DirentSize);
        if (n.Length < DirentSize || Crc(n[..32]) != U32(n, 32, _be)) return false;
        int nsize = n[28];
        if (DirentSize + nsize > totlen || nsize == 0) return false;
        uint pino = U32(n, 12, _be), version = U32(n, 16, _be), ino = U32(n, 20, _be), nameCrc = U32(n, 36, _be);
        byte type = n[29];
        ReadOnlySpan<byte> name = win.At(pos + DirentSize, nsize);
        if (name.Length < nsize || Crc(name) != nameCrc) return false;
        var key = (pino, PosixName.Decode(name));
        // The newest version of a name in a directory wins; a later node of the same version too (the kernel does the same).
        if (!_dirents.TryGetValue(key, out DirentNode? old) || version >= old.Version)
            _dirents[key] = new DirentNode { Version = version, Ino = ino, Type = type };
        return true;
    }

    private bool ScanXattr(Window win, long pos, uint totlen)
    {
        if (totlen < XattrSize) return false;
        ReadOnlySpan<byte> n = win.At(pos, XattrSize);
        if (n.Length < XattrSize || Crc(n[..28]) != U32(n, 28, _be)) return false;
        uint xid = U32(n, 12, _be), version = U32(n, 16, _be), dataCrc = U32(n, 24, _be);
        byte prefix = n[20];
        int nameLen = n[21], valueLen = U16(n, 22, _be);
        if (XattrSize + nameLen + 1 + valueLen > totlen) return false;
        ReadOnlySpan<byte> data = win.At(pos + XattrSize, nameLen + 1 + valueLen);
        if (data.Length < nameLen + 1 + valueLen || Crc(data) != dataCrc) return false;
        if (!_xattrs.TryGetValue(xid, out XattrNode? old) || version > old.Version || old.Version == XattrDeleted && version != XattrDeleted)
            _xattrs[xid] = new XattrNode
            {
                Version = version, Prefix = prefix,
                Name = data[..nameLen].ToArray(), Value = data.Slice(nameLen + 1, valueLen).ToArray(),
            };
        return true;
    }

    private bool ScanXref(Window win, long pos, uint totlen)
    {
        if (totlen < XrefSize) return false;
        ReadOnlySpan<byte> n = win.At(pos, XrefSize);
        if (n.Length < XrefSize || Crc(n[..24]) != U32(n, 24, _be)) return false;
        var key = (U32(n, 12, _be), U32(n, 16, _be));
        uint seq = U32(n, 20, _be);
        // The highest sequence number wins; an odd one marks the reference as deleted.
        if (!_xrefs.TryGetValue(key, out uint old) || (seq | XrefDeleted) > (old | XrefDeleted)) _xrefs[key] = seq;
        return true;
    }

    // --------------------------------------------------------------- tree

    /// <summary>A piece of a file: [Start, Start + Length) comes from the node's data at NodeOffset.</summary>
    private readonly record struct Frag(long Start, long Length, InodeNode Node, long NodeOffset);

    /// <summary>Builds the tree. File data stays in the image and is unpacked when read.</summary>
    public FsTree ReadTree(IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var tree = new FsTree();
        var byParent = new Dictionary<uint, List<(string Name, DirentNode Entry)>>();
        foreach (((uint pino, string name), DirentNode d) in _dirents)
        {
            if (d.Ino == 0) continue;          // a deletion
            if (!byParent.TryGetValue(pino, out var list)) byParent[pino] = list = new();
            list.Add((name, d));
        }

        // Extended attributes per inode.
        var xattrsOf = new Dictionary<uint, SortedDictionary<string, byte[]>>();
        foreach (((uint ino, uint xid), uint seq) in _xrefs)
        {
            if ((seq & XrefDeleted) != 0 || !_xattrs.TryGetValue(xid, out XattrNode? x) || x.Version == XattrDeleted) continue;
            if (XattrPrefix(x.Prefix) is not { } prefix)
            {
                Warnings.Add($"Inode {ino}: an extended attribute with the unknown prefix {x.Prefix} was skipped.");
                continue;
            }
            byte[] value = x.Value;
            if (x.Prefix is XprefixAclAccess or XprefixAclDefault)
            {
                if (AclToXattr(value, _be) is not { } acl)
                {
                    Warnings.Add($"Inode {ino}: a malformed POSIX ACL was skipped.");
                    continue;
                }
                value = acl;
            }
            string name = prefix + (x.Prefix is XprefixAclAccess or XprefixAclDefault ? "" : PosixName.Decode(x.Name));
            if (!xattrsOf.TryGetValue(ino, out var set)) xattrsOf[ino] = set = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            set[name] = value;
        }

        // The root: a node for inode 1 is optional (mkfs.jffs2 writes none; the kernel then fakes 0755).
        if (Latest(1) is { } rootNode)
        {
            tree.Root.Inode.Mode = (int)rootNode.Mode & 0xFFF;
            tree.Root.Inode.Uid = rootNode.Uid;
            tree.Root.Inode.Gid = rootNode.Gid;
            tree.Root.Inode.MTime = rootNode.MTime;
        }
        else
        {
            tree.Root.Inode.Mode = 0x1ED;
        }
        tree.Root.Inode.XAttrs = xattrsOf.GetValueOrDefault(1u);

        var built = new Dictionary<uint, FsInode>();
        var seenDirs = new HashSet<uint> { 1 };
        var stack = new Stack<(FsNode Dir, uint Ino)>();
        stack.Push((tree.Root, 1));
        long done = 0, total = _dirents.Count;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            (FsNode dir, uint dino) = stack.Pop();
            if (!byParent.TryGetValue(dino, out var entries)) continue;
            entries.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
            foreach ((string name, DirentNode d) in entries)
            {
                if ((++done & 0x3FF) == 0) progress?.Report(new ProgressInfo("read", done, total, dir.Path));
                if (name.Contains('/') || name is "." or ".." || dir.Find(name) is not null)
                {
                    Warnings.Add($"Invalid or duplicate directory entry '{name}' in '{dir.Path}' was skipped.");
                    continue;
                }
                if (built.TryGetValue(d.Ino, out FsInode? known))
                {
                    tree.Add(dir, name, known);
                    continue;
                }
                FsInode? inode;
                try
                {
                    inode = BuildInode(d.Ino, xattrsOf.GetValueOrDefault(d.Ino));
                }
                catch (FsException ex)
                {
                    Warnings.Add($"'{Join(dir, name)}' was skipped: {ex.Message}");
                    continue;
                }
                if (inode is null)
                {
                    Warnings.Add($"'{Join(dir, name)}' points to inode {d.Ino}, which has no valid node, and was skipped.");
                    continue;
                }
                if (inode.Type == FsNodeType.Directory)
                {
                    if (!seenDirs.Add(d.Ino))
                    {
                        Warnings.Add($"Directory '{Join(dir, name)}' is linked more than once and was skipped.");
                        continue;
                    }
                    FsNode sub = tree.Add(dir, name, inode);
                    stack.Push((sub, d.Ino));
                }
                else
                {
                    built[d.Ino] = inode;
                    tree.Add(dir, name, inode);
                }
            }
        }
        progress?.Report(new ProgressInfo("read", total, total));
        return tree;
    }

    private static string Join(FsNode dir, string name) => dir.IsRoot ? "/" + name : dir.Path + "/" + name;

    private InodeNode? Latest(uint ino)
    {
        if (!_inodes.TryGetValue(ino, out List<InodeNode>? list) || list.Count == 0) return null;
        InodeNode latest = list[0];
        foreach (InodeNode n in list)
            if (n.Version >= latest.Version) latest = n;
        return latest;
    }

    private FsInode? BuildInode(uint ino, SortedDictionary<string, byte[]>? xattrs)
    {
        if (!_inodes.TryGetValue(ino, out List<InodeNode>? nodes) || nodes.Count == 0) return null;
        nodes.Sort(static (a, b) => a.Version.CompareTo(b.Version));
        InodeNode latest = nodes[^1];
        FsNodeType type = UnixMode.TypeFromMode((int)latest.Mode)
                          ?? throw new FsException($"inode {ino} has an invalid mode 0{Convert.ToString(latest.Mode, 8)}.");
        var inode = new FsInode
        {
            Type = type,
            Mode = (int)latest.Mode & 0xFFF,
            Uid = latest.Uid,
            Gid = latest.Gid,
            MTime = latest.MTime,
            XAttrs = xattrs,
        };
        switch (type)
        {
            case FsNodeType.File:
            {
                List<Frag> frags = Fragments(nodes, latest.ISize);
                if (latest.ISize > 0) inode.Data = new Jffs2FileData(this, frags, latest.ISize);
                break;
            }
            case FsNodeType.Symlink:
            {
                byte[] target = NodeData(latest);
                if (target.Length == 0) throw new FsException($"the symbolic link of inode {ino} has no target.");
                inode.LinkTarget = PosixName.Decode(target);
                break;
            }
            case FsNodeType.CharDevice or FsNodeType.BlockDevice:
            {
                byte[] dev = NodeData(latest);
                if (dev.Length == 2)
                {
                    uint old = U16(dev, 0, _be);
                    inode.DevMajor = old >> 8;
                    inode.DevMinor = old & 0xFF;
                }
                else if (dev.Length == 4)
                {
                    uint v = U32(dev, 0, _be);
                    inode.DevMajor = (v & 0xFFF00) >> 8;
                    inode.DevMinor = (v & 0xFF) | ((v >> 12) & 0xFFF00);
                }
                else
                {
                    throw new FsException($"the device node of inode {ino} has {dev.Length} bytes of device number.");
                }
                break;
            }
        }
        return inode;
    }

    /// <summary>Lays the data nodes over each other in version order: a newer node replaces what it covers.</summary>
    private static List<Frag> Fragments(List<InodeNode> nodes, long size)
    {
        var frags = new List<Frag>();
        foreach (InodeNode n in nodes)
        {
            if (n.DSize == 0) continue;
            long start = n.Offset, end = (long)n.Offset + n.DSize;
            var next = new List<Frag>(frags.Count + 2);
            foreach (Frag f in frags)
            {
                long fs = f.Start, fe = f.Start + f.Length;
                if (fe <= start || fs >= end)
                {
                    next.Add(f);
                    continue;
                }
                if (fs < start) next.Add(f with { Length = start - fs });
                if (fe > end) next.Add(new Frag(end, fe - end, f.Node, f.NodeOffset + (end - fs)));
            }
            next.Add(new Frag(start, end - start, n, 0));
            next.Sort(static (a, b) => a.Start.CompareTo(b.Start));
            frags = next;
        }
        // The size of the newest node truncates the file; data behind it is not part of the file.
        var cut = new List<Frag>(frags.Count);
        foreach (Frag f in frags)
        {
            if (f.Start >= size) break;
            cut.Add(f.Start + f.Length > size ? f with { Length = size - f.Start } : f);
        }
        return cut;
    }

    /// <summary>The unpacked data of a node.</summary>
    private byte[] NodeData(InodeNode n)
    {
        byte[] output = new byte[n.DSize];
        Unpack(n, output);
        return output;
    }

    [ThreadStatic] private static LzoCodec? _lzo;

    private void Unpack(InodeNode n, Span<byte> output)
    {
        if (n.Compr == Jffs2Compression.Zero)
        {
            output.Clear();
            return;
        }
        byte[] packed = _source.ReadBytes(n.DataPos, (int)n.CSize);
        switch (n.Compr)
        {
            case Jffs2Compression.None:
                if (packed.Length < output.Length) throw new FsException("A JFFS2 data node is shorter than its size.");
                packed.AsSpan(0, output.Length).CopyTo(output);
                break;
            case Jffs2Compression.Zlib:
                if (new ZlibCodec().Decompress(packed, output) != output.Length) throw new FsException("Corrupt zlib data in a JFFS2 node.");
                break;
            case Jffs2Compression.Lzo:
                if ((_lzo ??= new LzoCodec()).Decompress(packed, output) != output.Length) throw new FsException("Corrupt LZO data in a JFFS2 node.");
                break;
            case Jffs2Compression.Rtime:
                RtimeDecompress(packed, output);
                break;
            default:
                throw new FsException($"JFFS2 data compressed with {n.Compr.ToString().ToLowerInvariant()} cannot be unpacked.");
        }
    }

    // ---------------------------------------------------------- file data

    private sealed class Jffs2FileData(Jffs2Reader reader, List<Frag> frags, long length) : ISparseFileData
    {
        public long Length => length;
        public long DataBytesHint => Math.Min(length, frags.Where(f => f.Node.Compr != Jffs2Compression.Zero).Sum(f => f.Length));
        public Stream OpenRead() => new Jffs2FileStream(reader, frags, length);
    }

    private sealed class Jffs2FileStream(Jffs2Reader r, List<Frag> frags, long length) : Stream
    {
        private long _position;
        private int _index;
        private InodeNode? _cached;
        private byte[] _data = Array.Empty<byte>();

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
                _index = 0;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            long left = length - _position;
            if (left <= 0 || buffer.Length == 0) return 0;
            if (buffer.Length > left) buffer = buffer[..(int)left];
            while (_index < frags.Count && frags[_index].Start + frags[_index].Length <= _position) _index++;
            int n;
            if (_index < frags.Count && frags[_index].Start <= _position)
            {
                Frag f = frags[_index];
                long inFrag = _position - f.Start;
                n = (int)Math.Min(buffer.Length, f.Length - inFrag);
                if (f.Node.Compr == Jffs2Compression.Zero)
                {
                    buffer[..n].Clear();
                }
                else
                {
                    if (!ReferenceEquals(_cached, f.Node))
                    {
                        if (_data.Length < f.Node.DSize) _data = new byte[f.Node.DSize];
                        r.Unpack(f.Node, _data.AsSpan(0, (int)f.Node.DSize));
                        _cached = f.Node;
                    }
                    _data.AsSpan((int)(f.NodeOffset + inFrag), n).CopyTo(buffer);
                }
            }
            else
            {
                // A hole: zeros up to the next piece of data.
                long holeEnd = _index < frags.Count ? frags[_index].Start : length;
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

    public void Dispose()
    {
        if (_ownsSource) _source.Dispose();
    }
}
