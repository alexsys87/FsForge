using System.Buffers.Binary;
using System.Text;
using FsForge.Core.Ext;
using static FsForge.Core.F2fs.F2fsFormat;

namespace FsForge.Core.F2fs;

/// <summary>
/// Writes a tree as an F2FS image laid out as mkfs.f2fs and the kernel lay it out: 4 KiB blocks in 2 MiB
/// segments; the superblock and its copy, a checkpoint pack with the summaries of the six current segments,
/// SIT, NAT and SSA, then the main area. Nodes and data go to the logs the kernel would choose (directory nodes
/// hot, file nodes warm, indirect and xattr nodes cold; dentry blocks hot data, file data warm data). Directory
/// entries are filed in the hash buckets the kernel looks them up in; blocks of zeros become holes; extended
/// attributes and POSIX ACLs go to xattr nodes. Reserved and overprovisioned space are chosen as mkfs.f2fs
/// chooses them, so the kernel can mount the image read-write.
/// </summary>
public sealed class F2fsWriter
{
    private const int B = 4096, LogBps = 9, Bps = 1 << LogBps;
    private static readonly int Api = AddrsPerInode(B), Apb = AddrsPerBlock(B), Npb = AddrsPerBlock(B);
    private static readonly int Nr = DentriesInBlock(B), BitmapBytes = DentryBitmapSize(B);
    private const uint RootIno = 3, NodeIno = 1, MetaIno = 2;
    private const ulong CpVersion = 1;

    private readonly F2fsOptions _o;
    private readonly List<string> _warnings = new();
    private long _droppedXattrs, _xattrObjects;

    private F2fsWriter(F2fsOptions options) => _o = options;

    public static IReadOnlyList<string> Write(FsTree tree, Stream output, F2fsOptions options,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        options.Validate();
        if (!output.CanSeek) throw new InvalidOperationException("The F2FS writer needs a seekable stream.");
        var w = new F2fsWriter(options);
        w.Run(tree, output, progress, ct);
        return w._warnings;
    }

    // ---------------------------------------------------------- planning

    private sealed class NodeSpec
    {
        public int Ofs;                 // offset of the node in the inode's tree (0 is the inode)
        public bool Indirect;
        public int Parent = -1;         // index of the parent in the list (-1: the inode)
        public int Slot;                // where its nid goes in the parent (inode: i_nid index)
        public long FirstBlock, Count;  // direct nodes: the file blocks they map
        public uint Nid, Addr;
        public uint[] Ptrs = Array.Empty<uint>();
    }

    private sealed class Ino
    {
        public required FsInode Inode;
        public required FsNode Node;
        public uint Nid, Parent, Addr, XattrNid, XattrAddr;
        public byte[] Name = Array.Empty<byte>();
        public int Links, Depth;
        public long Blocks;                 // file blocks the size covers
        public long Size;
        public List<NodeSpec> Nodes = new();
        public uint[] Map = Array.Empty<uint>();
        public byte[]? Xattrs;
        public Dictionary<long, byte[]>? DirBlocks;
        public bool IsDir => Inode.Type == FsNodeType.Directory;
    }

    /// <summary>The direct and indirect nodes a file of <paramref name="blocks"/> blocks needs, as get_node_path numbers them.</summary>
    private static List<NodeSpec> NodesFor(long blocks)
    {
        var list = new List<NodeSpec>();
        long b = Api;
        void Direct(int ofs, int parent, int slot)
        {
            list.Add(new NodeSpec { Ofs = ofs, Parent = parent, Slot = slot, FirstBlock = b, Count = Math.Min(Apb, blocks - b) });
            b += Apb;
        }
        int Ind(int ofs, int parent, int slot)
        {
            list.Add(new NodeSpec { Ofs = ofs, Indirect = true, Parent = parent, Slot = slot });
            return list.Count - 1;
        }
        if (blocks > b) Direct(1, -1, 0);
        if (blocks > b) Direct(2, -1, 1);
        if (blocks > b)
        {
            int ind = Ind(3, -1, 2);
            for (int k = 0; k < Npb && blocks > b; k++) Direct(4 + k, ind, k);
        }
        if (blocks > b)
        {
            int ind = Ind(4 + Npb, -1, 3);
            for (int k = 0; k < Npb && blocks > b; k++) Direct(5 + Npb + k, ind, k);
        }
        if (blocks > b)
        {
            int dind = Ind(5 + 2 * Npb, -1, 4);
            for (int k1 = 0; k1 < Npb && blocks > b; k1++)
            {
                int ind = Ind(6 + 2 * Npb + k1 * (Npb + 1), dind, k1);
                for (int k2 = 0; k2 < Npb && blocks > b; k2++) Direct(7 + 2 * Npb + k1 * (Npb + 1) + k2, ind, k2);
            }
        }
        if (blocks > b) throw new FsException("A file is too large for F2FS.");
        return list;
    }

    private void Run(FsTree tree, Stream output, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        // Inodes in depth-first order, the root first; each inode once, with the number of its names.
        var inodes = new List<Ino>();
        var byInode = new Dictionary<FsInode, Ino>(ReferenceEqualityComparer.Instance);
        var order = new List<(FsNode Node, Ino Ino)>();
        uint nextNid = RootIno;
        Ino Get(FsNode n, uint parent)
        {
            if (byInode.TryGetValue(n.Inode, out Ino? known))
            {
                known.Links++;
                return known;
            }
            var ino = new Ino { Inode = n.Inode, Node = n, Nid = nextNid++, Parent = parent, Links = 1, Name = n.IsRoot ? Array.Empty<byte>() : PosixName.Encode(n.Name) };
            if (ino.Name.Length > NameLen) throw new FsException($"'{n.Path}': the name is longer than {NameLen} bytes, the limit of F2FS.");
            byInode.Add(n.Inode, ino);
            inodes.Add(ino);
            return ino;
        }
        void Walk(FsNode dir, Ino dirIno)
        {
            foreach (FsNode k in dir.SortedChildren())
            {
                Ino child = Get(k, dirIno.Nid);
                order.Add((k, child));
                if (k.IsDirectory) Walk(k, child);
            }
        }
        Ino root = Get(tree.Root, RootIno);
        Walk(tree.Root, root);

        // Directory blocks, sizes, xattrs, then the nodes every inode needs.
        foreach (Ino ino in inodes)
        {
            ct.ThrowIfCancellationRequested();
            FsInode i = ino.Inode;
            switch (i.Type)
            {
                case FsNodeType.Directory:
                    ino.Links = 2 + ino.Node.Children.Count(c => c.IsDirectory);
                    BuildDirectory(ino.Node, ino, byInode);
                    break;
                case FsNodeType.File:
                    ino.Size = i.Size;
                    ino.Blocks = (ino.Size + B - 1) / B;
                    break;
                case FsNodeType.Symlink:
                    ino.Size = PosixName.Encode(i.LinkTarget ?? "").Length;
                    if (ino.Size > B) throw new FsException("A symbolic link target longer than 4096 bytes cannot be stored in F2FS.");
                    ino.Blocks = ino.Size > 0 ? 1 : 0;
                    break;
            }
            ino.Nodes = NodesFor(ino.Blocks);
            ino.Map = new uint[ino.Blocks];
            if (i.HasXAttrs)
            {
                _xattrObjects++;
                if (_o.StoreXattrs) ino.Xattrs = BuildXattrs(i);
            }
        }
        if (_droppedXattrs > 0)
            _warnings.Add($"The extended attributes of {_droppedXattrs} object(s) do not fit into an F2FS xattr node (4 KiB) and were left out.");
        if (_xattrObjects > 0 && !_o.StoreXattrs) _warnings.Add($"Extended attributes of {_xattrObjects} object(s) were not stored, as asked.");

        // Nids: the inodes have theirs; then every other node.
        foreach (Ino ino in inodes)
        {
            foreach (NodeSpec n in ino.Nodes) n.Nid = nextNid++;
            if (ino.Xattrs is not null) ino.XattrNid = nextNid++;
        }

        // Blocks of every log. Node logs: directories hot, files warm, indirect and xattr nodes cold.
        long hotNode = inodes.Where(x => x.IsDir).Sum(x => 1 + x.Nodes.Count(n => !n.Indirect));
        long warmNode = inodes.Where(x => !x.IsDir).Sum(x => 1 + x.Nodes.Count(n => !n.Indirect));
        long coldNode = inodes.Sum(x => x.Nodes.Count(n => n.Indirect) + (x.Xattrs is null ? 0 : 1));
        long hotData = inodes.Where(x => x.IsDir).Sum(x => x.DirBlocks!.Count);
        // Room for file data: every block, or what a sparse source says it holds (holes are not written).
        long warmDataMax = inodes.Where(x => !x.IsDir).Sum(x => x.Inode.Data is ISparseFileData sp
            ? Math.Min(x.Blocks, (sp.DataBytesHint + B - 1) / B + 2) : x.Blocks);
        long Segs(long n) => n / Bps + 1;                       // the last segment of a log stays open (current)
        long used = Segs(hotNode) + Segs(warmNode) + Segs(coldNode) + Segs(hotData) + 1 + Segs(warmDataMax);
        long validMax = hotNode + warmNode + coldNode + hotData + warmDataMax;
        Layout lay = PickLayout(used, validMax);

        // Main area: segments of each log one after another; warm data last, as it is written as it comes.
        long seg = 0;
        (long Start, long Count) Area(long blocks) { var a = (seg, Segs(blocks)); seg += a.Item2; return a; }
        var areas = new (long Start, long Count)[6];
        areas[HotNode] = Area(hotNode);
        areas[WarmNode] = Area(warmNode);
        areas[ColdNode] = Area(coldNode);
        areas[HotData] = Area(hotData);
        areas[ColdData] = Area(0);
        areas[WarmData] = Area(warmDataMax);
        var next = new long[6];
        uint Alloc(int log, out long index)
        {
            index = next[log]++;
            // Warm data is the last log: it may run on into the free segments behind it, not past the main area.
            if (log == WarmData ? areas[log].Start * Bps + index >= (long)lay.MainSegs * Bps - 1 : index >= areas[log].Count * Bps)
                throw new FsNoSpaceException("The content does not fit into the F2FS filesystem.");
            return (uint)(lay.MainBlk + areas[log].Start * Bps + index);
        }

        // Summaries of every used block: the node it belongs to and its position there.
        var sumNid = new Dictionary<uint, (uint Nid, ushort Ofs)>();
        foreach (Ino ino in inodes)
        {
            int nodeLog = ino.IsDir ? HotNode : WarmNode;
            ino.Addr = Alloc(nodeLog, out _);
            sumNid[ino.Addr] = (ino.Nid, 0);
            foreach (NodeSpec n in ino.Nodes)
            {
                n.Addr = Alloc(n.Indirect ? ColdNode : nodeLog, out _);
                sumNid[n.Addr] = (n.Nid, 0);
            }
            if (ino.Xattrs is not null)
            {
                ino.XattrAddr = Alloc(ColdNode, out _);
                sumNid[ino.XattrAddr] = (ino.XattrNid, 0);
            }
            if (ino.IsDir)
                foreach (long index in ino.DirBlocks!.Keys.Order())
                    ino.Map[index] = Alloc(HotData, out _);
        }

        long baseOffset = output.Position;
        output.SetLength(baseOffset + lay.BlockCount * B);
        void WriteBlock(long addr, ReadOnlySpan<byte> block)
        {
            output.Position = baseOffset + addr * B;
            output.Write(block);
        }

        // File data, streamed; blocks of zeros stay holes.
        long totalBytes = inodes.Where(x => x.Inode.Type == FsNodeType.File).Sum(x => x.Size), doneBytes = 0;
        byte[] buf = new byte[B];
        output.Position = baseOffset + (lay.MainBlk + areas[WarmData].Start * Bps) * B;
        foreach (Ino ino in inodes)
        {
            if (ino.Inode.Type == FsNodeType.Symlink && ino.Size > 0)
            {
                Array.Clear(buf);
                PosixName.Encode(ino.Inode.LinkTarget ?? "").CopyTo(buf, 0);
                ino.Map[0] = Alloc(WarmData, out _);
                output.Write(buf);
                continue;
            }
            if (ino.Inode.Type != FsNodeType.File || ino.Size == 0) continue;
            using Stream s = ino.Inode.Data!.OpenRead();
            for (long b = 0; b < ino.Blocks; b++)
            {
                ct.ThrowIfCancellationRequested();
                int want = (int)Math.Min(B, ino.Size - b * B), got = 0;
                while (got < want)
                {
                    int n = s.Read(buf, got, want - got);
                    if (n <= 0) throw new FsException($"A file became shorter while it was being written (inode {ino.Nid}).");
                    got += n;
                }
                buf.AsSpan(want).Clear();
                doneBytes += want;
                if (!buf.AsSpan().ContainsAnyExcept((byte)0)) continue;
                ino.Map[b] = Alloc(WarmData, out _);
                output.Write(buf);
                progress?.Report(new ProgressInfo("write", doneBytes, totalBytes));
            }
        }

        // Data summaries: the inode or the direct node whose address array holds the block.
        foreach (Ino ino in inodes)
        {
            for (long b = 0; b < Math.Min(Api, ino.Blocks); b++)
                if (ino.Map[b] != 0) sumNid[ino.Map[b]] = (ino.Nid, (ushort)b);
            foreach (NodeSpec d in ino.Nodes.Where(n => !n.Indirect))
                for (long b = 0; b < d.Count; b++)
                    if (ino.Map[d.FirstBlock + b] is var a and not 0) sumNid[a] = (d.Nid, (ushort)b);
        }

        // Nodes.
        foreach (Ino ino in inodes)
        {
            ct.ThrowIfCancellationRequested();
            WriteBlock(ino.Addr, InodeBlock(ino));
            foreach (NodeSpec n in ino.Nodes)
            {
                byte[] blk = new byte[B];
                if (n.Indirect)
                {
                    int self = ino.Nodes.IndexOf(n);
                    foreach (NodeSpec c in ino.Nodes.Where(c => c.Parent == self))
                        BinaryPrimitives.WriteUInt32LittleEndian(blk.AsSpan(c.Slot * 4), c.Nid);
                }
                else
                    for (long b = 0; b < n.Count; b++) BinaryPrimitives.WriteUInt32LittleEndian(blk.AsSpan((int)b * 4), ino.Map[n.FirstBlock + b]);
                Footer(blk, n.Nid, ino.Nid, (uint)n.Ofs, !ino.IsDir, n.Addr);
                WriteBlock(n.Addr, blk);
            }
            if (ino.Xattrs is not null)
            {
                byte[] blk = new byte[B];
                ino.Xattrs.CopyTo(blk, 0);
                Footer(blk, ino.XattrNid, ino.Nid, XattrNodeOffset, !ino.IsDir, ino.XattrAddr);
                WriteBlock(ino.XattrAddr, blk);
            }
            if (ino.IsDir)
                foreach ((long index, byte[] blk) in ino.DirBlocks!) WriteBlock(ino.Map[index], blk);
        }

        WriteMetadata(output, baseOffset, lay, inodes, areas, next, sumNid, nextNid);
        output.Position = baseOffset + lay.BlockCount * B;
        output.Flush();
    }

    // ------------------------------------------------------------ layout

    private sealed record Layout(long BlockCount, int SegmentCount, int SitSegs, int NatSegs, int SsaSegs, int MainSegs,
        uint CpBlk, uint SitBlk, uint NatBlk, uint SsaBlk, uint MainBlk, int Reserved, int Overprov);

    /// <summary>The areas of a filesystem of <paramref name="segmentCount"/> segments, as mkfs.f2fs divides it.</summary>
    private static Layout? MakeLayout(int segmentCount, long blockCount)
    {
        const int ckpt = 2;
        int sitSegs = (int)(((segmentCount + SitEntriesPerBlock(B) - 1) / SitEntriesPerBlock(B) + Bps - 1) / Bps);
        long avail = (long)(segmentCount - ckpt - sitSegs * 2) * Bps;
        if (avail <= 0) return null;
        int natSegs = (int)(((avail + NatEntriesPerBlock(B) - 1) / NatEntriesPerBlock(B) + Bps - 1) / Bps);
        int sitBitmap = sitSegs * Bps / 8;
        int maxNatBitmap = 4092 - 193 + 1 - sitBitmap;
        if (maxNatBitmap < 64) return null;                          // would need a checkpoint payload: too large
        natSegs = Math.Min(natSegs, maxNatBitmap * 8 >> LogBps);
        avail = (long)(segmentCount - ckpt - sitSegs * 2 - natSegs * 2) * Bps;
        int ssaSegs = (int)((avail / Bps + 1 + Bps - 1) / Bps);
        int meta = ckpt + sitSegs * 2 + natSegs * 2 + ssaSegs;
        int main = segmentCount - meta;
        if (main < 8) return null;
        // Overprovisioning and reserved segments as mkfs.f2fs computes them (get_best_overprovision).
        double best = 0, bestSpace = 0;
        (double from, double to, double step) = main < 256 ? (10.0, 95.0, 5.0) : (0.01, 10.0, 0.01);
        for (double c = from; c <= to + 1e-9; c += step)
        {
            double reserved = 2 * (100 / c + 1) + 6;
            double ovp = (main - reserved) * c / 100;
            double space = main - reserved - ovp;
            if (space > bestSpace) (bestSpace, best) = (space, c);
        }
        if (best == 0) return null;
        int rsvd = (int)(2 * (100 / best + 1) + 6);
        int ovpSegs = (int)((main - rsvd) * best / 100) + rsvd;
        if (main - 2 < rsvd || ovpSegs >= main) return null;
        const uint seg0 = 512;
        uint cp = seg0, sit = cp + ckpt * Bps, nat = sit + (uint)(sitSegs * 2 * Bps), ssa = nat + (uint)(natSegs * 2 * Bps), mainBlk = ssa + (uint)(ssaSegs * Bps);
        return new Layout(blockCount, segmentCount, sitSegs * 2, natSegs * 2, ssaSegs, main, cp, sit, nat, ssa, mainBlk, rsvd, ovpSegs);
    }

    private Layout PickLayout(long usedSegs, long validMax)
    {
        long extra = (_o.ExtraFreeBytes + B - 1) / B;
        if (_o.SizeBytes > 0)
        {
            long blocks = _o.SizeBytes / B;
            Layout l = MakeLayout((int)Math.Min(int.MaxValue, (blocks - 512) / Bps), blocks)
                       ?? throw new FsException($"An F2FS filesystem of {_o.SizeBytes >> 20} MiB is too small (or too large) to be made.");
            if (l.MainSegs < usedSegs || (long)(l.MainSegs - l.Overprov) * Bps < validMax)
                throw new FsNoSpaceException($"The content does not fit into an F2FS filesystem of {_o.SizeBytes >> 20} MiB: it needs about " +
                                      $"{(validMax * B) >> 20} MiB of data in {usedSegs} segments, the filesystem has room for {((long)(l.MainSegs - l.Overprov) * B * Bps) >> 20} MiB.");
            return l;
        }
        for (int segs = (int)Math.Max(16, usedSegs + 8); segs < 1 << 24; segs += Math.Max(1, segs / 64))
        {
            Layout? l = MakeLayout(segs, 512 + (long)segs * Bps);
            if (l is null) continue;
            if (l.MainSegs >= usedSegs && (long)(l.MainSegs - l.Overprov) * Bps >= validMax + extra) return l;
        }
        throw new FsException("The content is too large for an F2FS image.");
    }

    // ------------------------------------------------------- directories

    private void BuildDirectory(FsNode dir, Ino ino, Dictionary<FsInode, Ino> byInode)
    {
        var blocks = new Dictionary<long, byte[]>();
        int depth = 1;
        byte[] BlockAt(long index) => blocks.TryGetValue(index, out byte[]? b) ? b : blocks[index] = new byte[B];
        int dentAt = B - (DirEntrySize + SlotLen) * Nr, namesAt = dentAt + DirEntrySize * Nr;
        void Put(byte[] blk, int slot, uint hash, uint ino2, byte[] name, int type)
        {
            int slots = Math.Max(1, (name.Length + SlotLen - 1) / SlotLen);
            for (int i = 0; i < slots; i++) SetBitLe(blk, slot + i);
            int e = dentAt + slot * DirEntrySize;
            BinaryPrimitives.WriteUInt32LittleEndian(blk.AsSpan(e), hash);
            BinaryPrimitives.WriteUInt32LittleEndian(blk.AsSpan(e + 4), ino2);
            BinaryPrimitives.WriteUInt16LittleEndian(blk.AsSpan(e + 8), (ushort)name.Length);
            blk[e + 10] = (byte)type;
            name.CopyTo(blk, namesAt + slot * SlotLen);
        }
        int FreeRun(byte[] blk, int slots)
        {
            for (int i = 0; i + slots <= Nr; i++)
            {
                int j = 0;
                while (j < slots && !TestBitLe(blk, i + j)) j++;
                if (j == slots) return i;
                i += j;
            }
            return -1;
        }
        byte[] first = BlockAt(0);
        Put(first, 0, 0, ino.Nid, "."u8.ToArray(), 2);
        Put(first, 1, 0, ino.Parent, ".."u8.ToArray(), 2);
        foreach (FsNode k in dir.SortedChildren())
        {
            byte[] name = PosixName.Encode(k.Name);
            uint hash = DentryHash(name);
            int slots = Math.Max(1, (name.Length + SlotLen - 1) / SlotLen);
            bool placed = false;
            for (int level = 0; level < MaxDirHashDepth && !placed; level++)
            {
                long bucket = hash % DirBuckets(level, 0);
                long start = DirBlockIndex(level, 0, bucket);
                for (int i = 0; i < BucketBlocks(level) && !placed; i++)
                {
                    byte[] blk = BlockAt(start + i);
                    int slot = FreeRun(blk, slots);
                    if (slot < 0)
                    {
                        if (!blk.AsSpan(0, BitmapBytes).ContainsAnyExcept((byte)0)) blocks.Remove(start + i);
                        continue;
                    }
                    Put(blk, slot, hash, byInode[k.Inode].Nid, name, FileType(k.Type));
                    depth = Math.Max(depth, level + 1);
                    placed = true;
                }
            }
            if (!placed) throw new FsException($"The directory '{dir.Path}' holds too many entries for F2FS.");
        }
        // Blocks that were looked at but got nothing stay holes.
        foreach (long index in blocks.Where(p => !p.Value.AsSpan(0, BitmapBytes).ContainsAnyExcept((byte)0)).Select(p => p.Key).ToList())
            blocks.Remove(index);
        ino.DirBlocks = blocks;
        ino.Depth = depth;
        ino.Blocks = blocks.Keys.Max() + 1;
        ino.Size = ino.Blocks * B;
    }

    // ------------------------------------------------------------ xattrs

    private byte[]? BuildXattrs(FsInode inode)
    {
        var ms = new MemoryStream();
        Span<byte> head = stackalloc byte[24];
        head.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(head, XattrMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(head[4..], 1);
        ms.Write(head);
        foreach ((string full, byte[] raw) in inode.XAttrs!)
        {
            if (!SplitXattrName(full, out int index, out string suffix)) continue;
            byte[] value = index is 2 or 3 ? ExtFormat.AclXattrToDisk(raw) ?? raw : raw;
            byte[] name = Encoding.UTF8.GetBytes(suffix);
            if (name.Length > 255 || value.Length > 0xFFFF) continue;
            int size = (4 + name.Length + value.Length + 3) & ~3;
            byte[] e = new byte[size];
            e[0] = (byte)index;
            e[1] = (byte)name.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(2), (ushort)value.Length);
            name.CopyTo(e, 4);
            value.CopyTo(e, 4 + name.Length);
            ms.Write(e);
        }
        ms.Write(new byte[4]);
        if (ms.Length > B - NodeFooterSize)
        {
            _droppedXattrs++;
            return null;
        }
        return ms.Length > 28 ? ms.ToArray() : null;
    }

    // ------------------------------------------------------------- nodes

    private static void Footer(byte[] blk, uint nid, uint ino, uint ofs, bool cold, uint addr)
    {
        Span<byte> f = blk.AsSpan(B - NodeFooterSize);
        BinaryPrimitives.WriteUInt32LittleEndian(f, nid);
        BinaryPrimitives.WriteUInt32LittleEndian(f[4..], ino);
        BinaryPrimitives.WriteUInt32LittleEndian(f[8..], (ofs << 3) | (cold ? 1u : 0u));
        BinaryPrimitives.WriteUInt64LittleEndian(f[12..], CpVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(f[20..], addr + 1);
    }

    private byte[] InodeBlock(Ino ino)
    {
        FsInode i = ino.Inode;
        byte[] b = new byte[B];
        Span<byte> s = b;
        int mode = UnixMode.TypeBits(i.Type) | (i.Type == FsNodeType.Symlink ? 0x1FF : i.Mode & 0xFFF);
        uint uid = _o.AllRoot ? 0 : i.Uid, gid = _o.AllRoot ? 0 : i.Gid;
        long allocated = ino.Map.Count(a => a != 0);
        BinaryPrimitives.WriteUInt16LittleEndian(s, (ushort)mode);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], uid);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], gid);
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], (uint)ino.Links);
        BinaryPrimitives.WriteUInt64LittleEndian(s[16..], (ulong)ino.Size);
        BinaryPrimitives.WriteUInt64LittleEndian(s[24..], (ulong)(1 + allocated + ino.Nodes.Count + (ino.Xattrs is null ? 0 : 1)));
        foreach (int at in new[] { 32, 40, 48 }) BinaryPrimitives.WriteInt64LittleEndian(s[at..], i.MTime);
        if (ino.IsDir) BinaryPrimitives.WriteUInt32LittleEndian(s[72..], (uint)ino.Depth);
        BinaryPrimitives.WriteUInt32LittleEndian(s[76..], ino.XattrNid);
        BinaryPrimitives.WriteUInt32LittleEndian(s[84..], ino.Parent);
        BinaryPrimitives.WriteUInt32LittleEndian(s[88..], (uint)ino.Name.Length);
        ino.Name.CopyTo(b, 92);
        switch (i.Type)
        {
            case FsNodeType.CharDevice or FsNodeType.BlockDevice:
                uint major = i.DevMajor, minor = i.DevMinor;
                if (major < 256 && minor < 256) BinaryPrimitives.WriteUInt32LittleEndian(s[OffsetOfIAddr..], (major << 8) | minor);
                else BinaryPrimitives.WriteUInt32LittleEndian(s[(OffsetOfIAddr + 4)..], (minor & 0xFF) | (major << 8) | ((minor & ~0xFFu) << 12));
                break;
            default:
                for (int k = 0; k < Math.Min(Api, ino.Map.Length); k++) BinaryPrimitives.WriteUInt32LittleEndian(s[(OffsetOfIAddr + k * 4)..], ino.Map[k]);
                break;
        }
        int nidAt = OffsetOfIAddr + Api * 4;
        foreach (NodeSpec n in ino.Nodes.Where(n => n.Parent == -1)) BinaryPrimitives.WriteUInt32LittleEndian(s[(nidAt + n.Slot * 4)..], n.Nid);
        Footer(b, ino.Nid, ino.Nid, 0, !ino.IsDir, ino.Addr);
        return b;
    }

    // ---------------------------------------------------------- metadata

    private void WriteMetadata(Stream output, long baseOffset, Layout lay, List<Ino> inodes, (long Start, long Count)[] areas, long[] next,
        Dictionary<uint, (uint Nid, ushort Ofs)> sums, uint nextNid)
    {
        void At(long addr, ReadOnlySpan<byte> data)
        {
            output.Position = baseOffset + addr * B;
            output.Write(data);
        }
        void Zero(long addr, long blocks)
        {
            byte[] z = new byte[Math.Min(blocks, 256) * B];
            for (long a = 0; a < blocks; a += 256) At(addr + a, z.AsSpan(0, (int)(Math.Min(256, blocks - a) * B)));
        }
        // Everything before the main area is written in full: a partition may hold old data there.
        Zero(0, lay.MainBlk);

        // Segments: valid blocks, their count and the log they belong to.
        int mainSegs = lay.MainSegs;
        var valid = new Dictionary<long, byte[]>();
        var segType = new Dictionary<long, int>();
        var cur = new long[6];
        var curOff = new int[6];
        for (int log = 0; log < 6; log++)
        {
            for (long sgm = areas[log].Start; sgm < areas[log].Start + areas[log].Count; sgm++) segType[sgm] = log;
            cur[log] = areas[log].Start + next[log] / Bps;
            curOff[log] = (int)(next[log] % Bps);
        }
        foreach (uint addr in sums.Keys)
        {
            long rel = addr - lay.MainBlk, sgm = rel / Bps;
            if (!valid.TryGetValue(sgm, out byte[]? map)) valid[sgm] = map = new byte[Bps / 8];
            SetBitMsb(map, rel % Bps);
        }
        long usedSegs = 0;
        byte[] sitBlock = new byte[B];
        int perSit = SitEntriesPerBlock(B);
        for (long sitIndex = 0; sitIndex * perSit < mainSegs; sitIndex++)
        {
            Array.Clear(sitBlock);
            for (int k = 0; k < perSit; k++)
            {
                long sgm = sitIndex * perSit + k;
                if (sgm >= mainSegs) break;
                bool isCur = Array.IndexOf(cur, sgm) >= 0;
                valid.TryGetValue(sgm, out byte[]? map);
                int count = map is null ? 0 : map.Sum(x => System.Numerics.BitOperations.PopCount(x));
                if (count == 0 && !isCur) continue;
                usedSegs++;
                int type = segType.GetValueOrDefault(sgm);
                int e = k * SitEntrySize;
                BinaryPrimitives.WriteUInt16LittleEndian(sitBlock.AsSpan(e), (ushort)((type << 10) | count));
                map?.CopyTo(sitBlock, e + 2);
            }
            At(lay.SitBlk + sitIndex, sitBlock);
        }

        // SSA: a summary block for every main segment.
        int entries = EntriesInSum(B);
        byte[] Summary(long sgm)
        {
            byte[] blk = new byte[B];
            int type = segType.GetValueOrDefault(sgm, -1);
            for (int k = 0; k < Bps; k++)
            {
                uint addr = (uint)(lay.MainBlk + sgm * Bps + k);
                if (!sums.TryGetValue(addr, out var e)) continue;
                BinaryPrimitives.WriteUInt32LittleEndian(blk.AsSpan(k * SummarySize), e.Nid);
                BinaryPrimitives.WriteUInt16LittleEndian(blk.AsSpan(k * SummarySize + 5), e.Ofs);
            }
            blk[B - SumFooterSize] = (byte)(type >= HotNode ? 1 : 0);
            return blk;
        }
        foreach (long sgm in segType.Keys) At(lay.SsaBlk + sgm, Summary(sgm));

        // NAT: the node and meta inodes, then every node.
        var nat = new SortedDictionary<uint, (uint Ino, uint Addr)> { [NodeIno] = (NodeIno, 1), [MetaIno] = (MetaIno, 1) };
        foreach (Ino ino in inodes)
        {
            nat[ino.Nid] = (ino.Nid, ino.Addr);
            foreach (NodeSpec n in ino.Nodes) nat[n.Nid] = (ino.Nid, n.Addr);
            if (ino.Xattrs is not null) nat[ino.XattrNid] = (ino.Nid, ino.XattrAddr);
        }
        int perNat = NatEntriesPerBlock(B);
        long natBlocks = (long)lay.NatSegs / 2 * Bps;
        if (nextNid > natBlocks * perNat) throw new FsException("The content has more objects than this F2FS filesystem can hold.");
        foreach (var group in nat.GroupBy(p => p.Key / (uint)perNat))
        {
            byte[] blk = new byte[B];
            foreach ((uint nid, (uint ino, uint addr)) in group)
            {
                int e = (int)(nid % perNat) * NatEntrySize;
                BinaryPrimitives.WriteUInt32LittleEndian(blk.AsSpan(e + 1), ino);
                BinaryPrimitives.WriteUInt32LittleEndian(blk.AsSpan(e + 5), addr);
            }
            long off = group.Key;
            At(lay.NatBlk + (off << 1) - (off & (Bps - 1)), blk);
        }

        // Checkpoint pack 1: header, data and node summaries of the current segments, header again. Pack 2 stays empty.
        long validBlocks = sums.Count;
        uint nodes = (uint)inodes.Sum(x => 1 + x.Nodes.Count + (x.Xattrs is null ? 0 : 1));
        byte[] cp = new byte[B];
        Span<byte> c = cp;
        BinaryPrimitives.WriteUInt64LittleEndian(c, CpVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(c[8..], (ulong)(lay.MainSegs - lay.Overprov) * Bps);
        BinaryPrimitives.WriteUInt64LittleEndian(c[16..], (ulong)validBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(c[24..], (uint)lay.Reserved);
        BinaryPrimitives.WriteUInt32LittleEndian(c[28..], (uint)lay.Overprov);
        BinaryPrimitives.WriteUInt32LittleEndian(c[32..], (uint)(lay.MainSegs - usedSegs));
        for (int k = 0; k < 8; k++)
        {
            bool live = k < 3;
            BinaryPrimitives.WriteUInt32LittleEndian(c[(36 + k * 4)..], live ? (uint)cur[HotNode + k] : 0xFFFFFFFF);
            BinaryPrimitives.WriteUInt16LittleEndian(c[(68 + k * 2)..], live ? (ushort)curOff[HotNode + k] : (ushort)0);
            BinaryPrimitives.WriteUInt32LittleEndian(c[(84 + k * 4)..], live ? (uint)cur[HotData + k] : 0xFFFFFFFF);
            BinaryPrimitives.WriteUInt16LittleEndian(c[(116 + k * 2)..], live ? (ushort)curOff[HotData + k] : (ushort)0);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(c[132..], CpUmount);
        BinaryPrimitives.WriteUInt32LittleEndian(c[136..], 8);
        BinaryPrimitives.WriteUInt32LittleEndian(c[140..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(c[144..], nodes);
        BinaryPrimitives.WriteUInt32LittleEndian(c[148..], (uint)inodes.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(c[152..], nextNid);
        BinaryPrimitives.WriteUInt32LittleEndian(c[156..], (uint)(lay.SitSegs / 2 * Bps / 8));
        BinaryPrimitives.WriteUInt32LittleEndian(c[160..], (uint)(lay.NatSegs / 2 * Bps / 8));
        BinaryPrimitives.WriteUInt32LittleEndian(c[164..], 4092);
        BinaryPrimitives.WriteUInt32LittleEndian(c[4092..], Crc(cp.AsSpan(0, 4092)));
        At(lay.CpBlk, cp);
        for (int t = 0; t < 3; t++) At(lay.CpBlk + 1 + t, Summary(cur[HotData + t]));
        for (int t = 0; t < 3; t++) At(lay.CpBlk + 4 + t, Summary(cur[HotNode + t]));
        At(lay.CpBlk + 7, cp);
        // The second pack is a copy, as fsck.f2fs leaves it: the kernel takes either.
        for (int k = 0; k < 8; k++)
        {
            output.Position = baseOffset + (lay.CpBlk + k) * B;
            byte[] blk = new byte[B];
            output.ReadExactly(blk);
            At(lay.CpBlk + Bps + k, blk);
        }

        // Superblock and its copy.
        byte[] sb = new byte[B];
        Span<byte> p = sb.AsSpan(SuperOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(p, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(p[4..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(p[6..], 16);
        BinaryPrimitives.WriteUInt32LittleEndian(p[8..], 9);
        BinaryPrimitives.WriteUInt32LittleEndian(p[12..], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(p[16..], 12);
        BinaryPrimitives.WriteUInt32LittleEndian(p[20..], LogBps);
        BinaryPrimitives.WriteUInt32LittleEndian(p[24..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(p[28..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(p[36..], (ulong)lay.BlockCount);
        BinaryPrimitives.WriteUInt32LittleEndian(p[44..], (uint)lay.MainSegs);
        BinaryPrimitives.WriteUInt32LittleEndian(p[48..], (uint)lay.SegmentCount);
        BinaryPrimitives.WriteUInt32LittleEndian(p[52..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(p[56..], (uint)lay.SitSegs);
        BinaryPrimitives.WriteUInt32LittleEndian(p[60..], (uint)lay.NatSegs);
        BinaryPrimitives.WriteUInt32LittleEndian(p[64..], (uint)lay.SsaSegs);
        BinaryPrimitives.WriteUInt32LittleEndian(p[68..], (uint)lay.MainSegs);
        BinaryPrimitives.WriteUInt32LittleEndian(p[72..], 512);
        BinaryPrimitives.WriteUInt32LittleEndian(p[76..], lay.CpBlk);
        BinaryPrimitives.WriteUInt32LittleEndian(p[80..], lay.SitBlk);
        BinaryPrimitives.WriteUInt32LittleEndian(p[84..], lay.NatBlk);
        BinaryPrimitives.WriteUInt32LittleEndian(p[88..], lay.SsaBlk);
        BinaryPrimitives.WriteUInt32LittleEndian(p[92..], lay.MainBlk);
        BinaryPrimitives.WriteUInt32LittleEndian(p[96..], RootIno);
        BinaryPrimitives.WriteUInt32LittleEndian(p[100..], NodeIno);
        BinaryPrimitives.WriteUInt32LittleEndian(p[104..], MetaIno);
        (_o.Uuid ?? Guid.NewGuid()).TryWriteBytes(p.Slice(108, 16));
        Encoding.Unicode.GetBytes(_o.VolumeName).AsSpan(0, Math.Min(_o.VolumeName.Length * 2, 1022)).CopyTo(p[124..]);
        Encoding.ASCII.GetBytes("FsForge").CopyTo(p[1668..]);
        Encoding.ASCII.GetBytes("FsForge").CopyTo(p[1924..]);
        At(0, sb);
        At(1, sb);
        _ = entries;
    }
}
