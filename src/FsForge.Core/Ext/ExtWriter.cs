using System.Buffers.Binary;
using System.Text;
using static FsForge.Core.Ext.ExtFormat;

namespace FsForge.Core.Ext;

/// <summary>
/// Builds a new ext2/ext3/ext4 image from a tree (the same job as "mke2fs -d").
/// The layout is the classic one: every block group holds its own bitmaps and inode table.
/// </summary>
public sealed class ExtWriter
{
    private sealed class NoSpaceException() : FsException("There is not enough free space in the image.");

    private readonly FsTree _tree;
    private readonly Stream _out;
    private readonly ExtOptions _opt;
    private readonly IProgress<ProgressInfo>? _progress;
    private readonly CancellationToken _ct;
    private readonly long _base;
    private readonly List<string> _warnings;
    private readonly bool _extents, _journal;
    private readonly int _bs, _isz;
    private readonly long _now;
    private readonly Guid _uuid;

    // Geometry.
    private long _blocks;               // total blocks
    private uint _firstDataBlock, _bpg, _ipg, _groups;
    private int _itb, _gdtBlocks;
    private long _journalBlocks;

    // Allocation state.
    private ulong[] _bitmap = Array.Empty<ulong>();
    private long _cursor;
    private long _allocated;            // blocks handed out by the allocator (content, maps, journal)
    private long _freeBlocks;           // free blocks of the finished image
    private bool _dirNlink;             // a directory has more subdirectories than a link count can hold

    // Inodes.
    private readonly List<Rec> _recs = new();
    private readonly Dictionary<FsInode, Rec> _byInode = new(ReferenceEqualityComparer.Instance);
    private Rec? _lostFound;
    private uint _usedInodes;
    private readonly uint[] _journalIBlock = new uint[15];
    private readonly Dictionary<string, XattrBlock> _xattrBlocks = new(StringComparer.Ordinal);
    private long _bytesDone, _bytesTotal;

    private const int MaxLinks = 65000;

    private sealed class Rec
    {
        public uint Number;
        public FsInode? Inode;                  // null for the synthetic lost+found
        public FsNode? Node;                    // one of the entries (the directory node for directories)
        public int Links;
        public long Size;
        public long Sectors;                    // i_blocks in 512-byte units
        public uint Flags;
        public readonly byte[] IBlock = new byte[60];
        public long FileAcl;
        public byte[]? InlineXattrs;            // bytes stored after the fixed inode fields
        public uint ParentNumber;
    }

    private sealed class XattrBlock
    {
        public required byte[] Data;
        public long Block;
        public int RefCount;
    }

    private readonly record struct Run(long Logical, long Phys, long Count);

    private ExtWriter(FsTree tree, Stream output, ExtOptions options, IProgress<ProgressInfo>? progress, CancellationToken ct, List<string> warnings)
    {
        _tree = tree;
        _out = output;
        _opt = options;
        _progress = progress;
        _ct = ct;
        _base = output.Position;
        _warnings = warnings;
        _bs = options.BlockSize;
        _isz = options.InodeSize;
        _extents = options.Kind == ExtKind.Ext4;
        _journal = options.Kind == ExtKind.Ext3 || (options.Kind == ExtKind.Ext4 && options.Journal);
        _now = options.Timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _uuid = options.Uuid is { } u && u != Guid.Empty ? u : Guid.NewGuid();
    }

    /// <summary>Writes the image at the current position of <paramref name="output"/> (must be seekable).</summary>
    public static IReadOnlyList<string> Write(FsTree tree, Stream output, ExtOptions options,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        options.Validate();
        if (!output.CanSeek) throw new ArgumentException("The output stream must be seekable.", nameof(output));
        long start = output.Position;
        var warnings = new List<string>();
        bool auto = options.SizeBytes == 0;
        long extra = 0, contentOverride = -1;
        bool tightened = false;
        for (int attempt = 0; ; attempt++)
        {
            warnings.Clear();
            output.Position = start;
            var w = new ExtWriter(tree, output, options, progress, ct, warnings);
            try
            {
                w.Execute(extra, contentOverride);
                if (auto && !tightened)
                {
                    // The size was planned from an upper bound (sparse files, shared attribute blocks).
                    // When that left a lot of unused space, plan again from the measured numbers.
                    long wanted = (options.ExtraFreeBytes + options.BlockSize - 1) / options.BlockSize;
                    long wasted = w._freeBlocks - wanted;
                    if (wasted > Math.Max(2048, w._blocks / 16))
                    {
                        tightened = true;
                        contentOverride = w._allocated - w._journalBlocks;
                        continue;
                    }
                }
                return warnings;
            }
            catch (NoSpaceException) when (auto && attempt < 14)
            {
                // The estimate was too optimistic (heavy fragmentation of the extent maps): grow and retry.
                extra += Math.Max(64L * options.BlockSize, w._blocks * options.BlockSize / 32);
            }
            catch (NoSpaceException)
            {
                throw new FsException(
                    $"The content does not fit into an image of {options.SizeBytes / 1048576.0:F1} MiB. " +
                    "Increase the image size or let it be chosen automatically.");
            }
        }
    }

    // --------------------------------------------------------------- driver

    private void Execute(long extraBytes, long contentOverride)
    {
        CollectInodes();
        PlanGeometry(extraBytes, contentOverride);

        _out.SetLength(_base);                      // drop any previous content so that untouched blocks read as zeros
        _out.SetLength(_base + Math.Max(_blocks * _bs, _opt.SizeBytes));
        ReserveMetadata();

        if (_journal) WriteJournal();
        foreach (Rec rec in _recs)
        {
            _ct.ThrowIfCancellationRequested();
            WriteContent(rec);
        }
        foreach (XattrBlock xb in _xattrBlocks.Values)
        {
            Le.W32(xb.Data, 4, (uint)xb.RefCount);
            WriteBlocks(xb.Block, xb.Data);
        }
        WriteInodeTables();
        WriteGroupMetadata();
        _out.Flush();
        _progress?.Report(new ProgressInfo("done", _bytesTotal, _bytesTotal));
    }

    // -------------------------------------------------------------- collect

    private void CollectInodes()
    {
        FsNode root = _tree.Root;
        var rootRec = new Rec { Number = RootInode, Inode = root.Inode, Node = root, ParentNumber = RootInode };
        _byInode[root.Inode] = rootRec;
        _recs.Add(rootRec);

        uint next = FirstInode;
        FsNode? lf = root.Find("lost+found");
        if (lf is { IsDirectory: true })
        {
            var r = new Rec { Number = next++, Inode = lf.Inode, Node = lf, ParentNumber = RootInode };
            _byInode[lf.Inode] = r;
            _recs.Add(r);
            _lostFound = r;
        }
        else if (lf is null && _opt.CreateLostFound)
        {
            _lostFound = new Rec { Number = next++, ParentNumber = RootInode, Links = 2 };
            _recs.Add(_lostFound);
        }

        foreach (FsNode n in FsTree.Walk(root, includeStart: false))
        {
            if (!_byInode.TryGetValue(n.Inode, out Rec? rec))
            {
                rec = new Rec { Number = next++, Inode = n.Inode, Node = n };
                _byInode.Add(n.Inode, rec);
                _recs.Add(rec);
                if (n.Type == FsNodeType.File) _bytesTotal += n.Inode.Size;
            }
            if (n.IsDirectory) rec.ParentNumber = _byInode[n.Parent!.Inode].Number;
            else rec.Links++;
        }

        // Directory link counts: "." + the entry in the parent + one ".." per subdirectory.
        foreach (Rec rec in _recs)
        {
            if (rec.Inode is { Type: FsNodeType.Symlink } link && link.Size >= _bs)
                throw new FsException(
                    $"The target of the symbolic link '{rec.Node!.Path}' is {link.Size} bytes long; " +
                    $"with {_bs}-byte blocks ext can store at most {_bs - 1}. Choose a larger block size.");
            if (rec.Node is not { IsDirectory: true } dir)
            {
                if (rec.Links > MaxLinks)
                    throw new FsException($"'{rec.Node?.Path}' has {rec.Links} hard links; ext supports at most {MaxLinks}.");
                continue;
            }
            int subdirs = 0;
            foreach (FsNode c in dir.Children) if (c.IsDirectory) subdirs++;
            if (ReferenceEquals(dir, root) && _lostFound is { Inode: null }) subdirs++;
            rec.Links = 2 + subdirs;
            if (rec.Links > MaxLinks)
            {
                // The link count is then stored as 1 ("unknown"), which needs the dir_nlink feature.
                if (!_dirNlink && !_extents)
                    _warnings.Add($"'{dir.Path}' has more than {MaxLinks - 2} subdirectories: the dir_nlink feature was enabled, " +
                                  "the image needs the ext4 driver to be mounted.");
                _dirNlink = true;
            }
        }
        _recs.Sort(static (a, b) => a.Number.CompareTo(b.Number));
        _usedInodes = next - 1;
    }

    // ------------------------------------------------------------- geometry

    private static long DefaultJournalBlocks(long blocks) => blocks switch
    {
        < 32768 => 1024,
        < 256 * 1024 => 4096,
        < 512 * 1024 => 8192,
        < 4096 * 1024 => 16384,
        < 8192 * 1024 => 32768,
        < 16384 * 1024 => 65536,
        _ => 131072,
    };

    private long MapOverhead(long fileBlocks)
    {
        if (fileBlocks == 0) return 0;
        if (_extents)
        {
            // Extents break at group boundaries and at the 32768-block limit.
            long extents = fileBlocks / 32768 + fileBlocks / _bpg + 2;
            if (extents <= 4) return 0;
            long perLeaf = (_bs - 12) / 12;
            long leaves = (extents + perLeaf - 1) / perLeaf;
            return leaves + (leaves > 4 ? (leaves + perLeaf - 1) / perLeaf : 0);
        }
        long a = _bs / 4;
        long n = 0, left = fileBlocks - 12;
        if (left <= 0) return 0;
        n++;                                        // single indirect
        left -= a;
        if (left <= 0) return n;
        n += 1 + (Math.Min(left, a * a) + a - 1) / a; // double indirect
        left -= a * a;
        if (left <= 0) return n;
        n += 1 + (left + a * a - 1) / (a * a) + (left + a - 1) / a;
        return n;
    }

    private long EstimateContentBlocks()
    {
        long total = 0;
        foreach (Rec rec in _recs)
        {
            if (rec.Inode is null)
            {
                total += 1;                         // lost+found
                continue;
            }
            long blocks = rec.Inode.Type switch
            {
                FsNodeType.File => (rec.Inode.Size + _bs - 1) / _bs,
                FsNodeType.Directory => DirBlocks(rec.Node!),
                FsNodeType.Symlink => rec.Inode.Size < 60 ? 0 : (rec.Inode.Size + _bs - 1) / _bs,
                _ => 0,
            };
            long map = MapOverhead(blocks);
            if (_opt.SparseFiles && rec.Inode is { Type: FsNodeType.File, Data: ISparseFileData sparse })
            {
                // Holes take no space; a block map needs at most three blocks per data block.
                long data = Math.Min(blocks, (sparse.DataBytesHint + _bs - 1) / _bs);
                if (data < blocks)
                {
                    map = Math.Min(map, _extents ? data / 64 + 2 : data * 3);
                    blocks = data;
                }
            }
            total += blocks + map;
            if (_opt.StoreXattrs && rec.Inode.HasXAttrs) total += 1;    // upper bound: a private xattr block
        }
        return total;
    }

    private long DirBlocks(FsNode dir)
    {
        long blocks = 1;
        int pos = 12 + 12;                          // "." and ".."
        foreach (FsNode c in dir.Children)
        {
            int need = 8 + ((PosixName.Encode(c.Name).Length + 3) & ~3);
            if (pos + need > _bs)
            {
                blocks++;
                pos = 0;
            }
            pos += need;
        }
        if (dir.IsRoot && _lostFound is { Inode: null } && pos + 20 > _bs) blocks++;
        return blocks;
    }

    private bool GroupHasSuperblock(uint g) => GroupHasSuper(g, sparseSuper: true);

    private long GroupStart(uint g) => _firstDataBlock + (long)g * _bpg;

    private long GroupBlockCount(uint g) => Math.Min(_bpg, _blocks - GroupStart(g));

    private long GroupOverhead(uint g) => (GroupHasSuperblock(g) ? 1 + _gdtBlocks : 0) + 2 + _itb;

    private long BlockBitmapBlock(uint g) => GroupStart(g) + (GroupHasSuperblock(g) ? 1 + _gdtBlocks : 0);

    /// <summary>Computes groups, inodes per group and table sizes for a given block count. Returns usable data blocks.</summary>
    private long SetGeometry(long blocks, uint wantInodes)
    {
        _blocks = blocks;
        _groups = (uint)((blocks - _firstDataBlock + _bpg - 1) / _bpg);
        _gdtBlocks = (int)(((long)_groups * 32 + _bs - 1) / _bs);
        int perBlock = _bs / _isz;
        uint round = (uint)Math.Max(8, perBlock);
        long ipg = (wantInodes + _groups - 1) / _groups;
        ipg = (ipg + round - 1) / round * round;
        ipg = Math.Clamp(ipg, round, 8L * _bs);
        _ipg = (uint)ipg;
        _itb = (int)(ipg / perBlock);
        long usable = blocks - _firstDataBlock;
        for (uint g = 0; g < _groups; g++) usable -= GroupOverhead(g);
        return usable;
    }

    private void PlanGeometry(long extraBytes, long contentOverride)
    {
        _firstDataBlock = _bs == 1024 ? 1u : 0u;
        _bpg = (uint)(8 * _bs);
        long content = contentOverride >= 0 ? contentOverride : EstimateContentBlocks();
        long slack = 16 + content / 200;
        bool auto = _opt.SizeBytes == 0;
        uint minInodes = _usedInodes;

        if (auto)
        {
            uint wantInodes = _opt.InodeCount > 0 ? Math.Max(_opt.InodeCount, minInodes) : minInodes + Math.Max(16, minInodes / 10);
            long extraBlocks = (_opt.ExtraFreeBytes + extraBytes + _bs - 1) / _bs;
            long blocks = Math.Max(64, content + slack + extraBlocks + 64);
            for (int i = 0; i < 64; i++)
            {
                _journalBlocks = !_journal ? 0
                    : _opt.JournalBytes > 0 ? Math.Max(1024, _opt.JournalBytes / _bs)
                    : DefaultJournalBlocks(blocks);
                long need = content + slack + extraBlocks + _journalBlocks;
                long usable = SetGeometry(blocks, wantInodes);
                // A trailing group that cannot even hold its own metadata would be invalid.
                long lastSize = GroupBlockCount(_groups - 1);
                if (usable >= need && lastSize >= GroupOverhead(_groups - 1) + 8) break;
                blocks += Math.Max(need - usable, 1) + (lastSize < GroupOverhead(_groups - 1) + 8 ? GroupOverhead(_groups - 1) + 8 : 0);
            }
        }
        else
        {
            long blocks = _opt.SizeBytes / _bs;
            if (blocks < 64) throw new FsException("The requested ext image size is too small.");
            if (blocks > uint.MaxValue) throw new FsException("Ext images larger than 2^32 blocks are not supported.");
            uint wantInodes = _opt.InodeCount > 0
                ? _opt.InodeCount
                : (uint)Math.Min(uint.MaxValue, Math.Max(minInodes + Math.Max(16, minInodes / 10), _opt.SizeBytes / 16384));
            if (wantInodes < minInodes)
            {
                _warnings.Add($"The inode count was raised from {wantInodes} to {minInodes} to hold all files.");
                wantInodes = minInodes + Math.Max(16, minInodes / 10);
            }
            SetGeometry(blocks, wantInodes);
            // mke2fs drops a last group that is too small to be useful.
            while (_groups > 1 && GroupBlockCount(_groups - 1) < GroupOverhead(_groups - 1) + 50)
                SetGeometry(GroupStart(_groups - 1), wantInodes);
            _journalBlocks = !_journal ? 0
                : _opt.JournalBytes > 0 ? Math.Max(1024, _opt.JournalBytes / _bs)
                : DefaultJournalBlocks(_blocks);
            long usable = _blocks - _firstDataBlock;
            for (uint g = 0; g < _groups; g++) usable -= GroupOverhead(g);
            if (usable < _journalBlocks + 16)
                throw new FsException("The requested ext image size is too small for the filesystem metadata and the journal.");
        }

        if ((ulong)_ipg * _groups < _usedInodes)
            throw new FsException($"The image cannot hold {_usedInodes} inodes with this geometry; increase the image size.");
    }

    // ----------------------------------------------------------- allocation

    private void Mark(long block)
    {
        _bitmap[block >> 6] |= 1UL << (int)(block & 63);
    }

    private bool IsUsed(long block) => (_bitmap[block >> 6] & (1UL << (int)(block & 63))) != 0;

    private void ReserveMetadata()
    {
        _bitmap = new ulong[(_blocks + 63) / 64];
        for (long b = 0; b < _firstDataBlock; b++) Mark(b);
        for (uint g = 0; g < _groups; g++)
        {
            long start = GroupStart(g);
            long n = GroupOverhead(g);
            for (long b = 0; b < n; b++) Mark(start + b);
        }
        _cursor = _firstDataBlock;
    }

    /// <summary>Allocates up to <paramref name="want"/> contiguous blocks; returns the first one.</summary>
    private long AllocRun(long want, out long got)
    {
        long b = _cursor;
        while (b < _blocks && IsUsed(b))
        {
            // Skip fully used words quickly.
            if ((b & 63) == 0 && _bitmap[b >> 6] == ulong.MaxValue) b += 64;
            else b++;
        }
        if (b >= _blocks) throw new NoSpaceException();
        long start = b;
        long n = 0;
        while (n < want && b < _blocks && !IsUsed(b))
        {
            Mark(b);
            b++;
            n++;
        }
        _cursor = b;
        _allocated += n;
        got = n;
        return start;
    }

    private long AllocOne() => AllocRun(1, out _);

    private void WriteBlocks(long block, ReadOnlySpan<byte> data)
    {
        _out.Position = _base + block * _bs;
        _out.Write(data);
    }

    // -------------------------------------------------------------- content

    private static void AddRun(List<Run> runs, long logical, long phys, long count)
    {
        if (runs.Count > 0)
        {
            Run last = runs[^1];
            if (last.Logical + last.Count == logical && last.Phys + last.Count == phys)
            {
                runs[^1] = last with { Count = last.Count + count };
                return;
            }
        }
        runs.Add(new Run(logical, phys, count));
    }

    /// <summary>Stores a byte stream in freshly allocated blocks and returns the mapping.</summary>
    private List<Run> StoreData(Stream data, long size, bool sparse, bool report)
    {
        var runs = new List<Run>();
        if (size == 0) return runs;
        int chunkBlocks = Math.Max(1, (1 << 20) / _bs);
        byte[] buf = System.Buffers.ArrayPool<byte>.Shared.Rent(chunkBlocks * _bs);
        try
        {
            long logical = 0;
            long left = size;
            while (left > 0)
            {
                _ct.ThrowIfCancellationRequested();
                int want = (int)Math.Min(left, (long)chunkBlocks * _bs);
                data.ReadExactly(buf, 0, want);
                int blocks = (want + _bs - 1) / _bs;
                if (want < blocks * _bs) Array.Clear(buf, want, blocks * _bs - want);

                int i = 0;
                while (i < blocks)
                {
                    if (sparse && buf.AsSpan(i * _bs, _bs).IndexOfAnyExcept((byte)0) < 0)
                    {
                        i++;
                        continue;
                    }
                    int j = i + 1;
                    while (j < blocks && !(sparse && buf.AsSpan(j * _bs, _bs).IndexOfAnyExcept((byte)0) < 0)) j++;
                    int pending = j - i;
                    int off = i;
                    while (pending > 0)
                    {
                        long start = AllocRun(pending, out long got);
                        WriteBlocks(start, buf.AsSpan(off * _bs, (int)got * _bs));
                        AddRun(runs, logical + off, start, got);
                        off += (int)got;
                        pending -= (int)got;
                    }
                    i = j;
                }
                logical += blocks;
                left -= want;
                if (report)
                {
                    _bytesDone += want;
                    _progress?.Report(new ProgressInfo("data", _bytesDone, _bytesTotal));
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buf);
        }
        return runs;
    }

    /// <summary>Fills i_block (and any extent or indirect blocks) from a block mapping; returns the blocks used by the map itself.</summary>
    private long BuildMap(List<Run> runs, long fileBlocks, Span<byte> iblock, out uint flags)
    {
        iblock.Clear();
        return _extents ? BuildExtents(runs, iblock, out flags) : BuildBlockMap(runs, fileBlocks, iblock, out flags);
    }

    private long BuildExtents(List<Run> runs, Span<byte> iblock, out uint flags)
    {
        flags = FlagExtents;
        // Split runs at the maximum extent length.
        var ext = new List<Run>(runs.Count);
        foreach (Run r in runs)
        {
            long done = 0;
            while (done < r.Count)
            {
                long n = Math.Min(32768, r.Count - done);
                ext.Add(new Run(r.Logical + done, r.Phys + done, n));
                done += n;
            }
        }

        static void Header(Span<byte> b, int entries, int max, int depth)
        {
            Le.W16(b, 0, ExtentMagic);
            Le.W16(b, 2, entries);
            Le.W16(b, 4, max);
            Le.W16(b, 6, depth);
            Le.W32(b, 8, 0);
        }

        static void Leaf(Span<byte> b, int index, Run r)
        {
            int o = 12 + index * 12;
            Le.W32(b, o, (uint)r.Logical);
            Le.W16(b, o + 4, (int)r.Count);
            Le.W16(b, o + 6, (int)(r.Phys >> 32));
            Le.W32(b, o + 8, (uint)r.Phys);
        }

        static void Index(Span<byte> b, int index, long logical, long child)
        {
            int o = 12 + index * 12;
            Le.W32(b, o, (uint)logical);
            Le.W32(b, o + 4, (uint)child);
            Le.W16(b, o + 8, (int)(child >> 32));
            Le.W16(b, o + 10, 0);
        }

        if (ext.Count <= 4)
        {
            Header(iblock, ext.Count, 4, 0);
            for (int i = 0; i < ext.Count; i++) Leaf(iblock, i, ext[i]);
            return 0;
        }

        int perBlock = (_bs - 12) / 12;
        long used = 0;
        byte[] buf = new byte[_bs];
        // Level 0: leaves.
        var level = new List<(long Logical, long Block)>();
        for (int i = 0; i < ext.Count; i += perBlock)
        {
            int n = Math.Min(perBlock, ext.Count - i);
            Array.Clear(buf);
            Header(buf, n, perBlock, 0);
            for (int k = 0; k < n; k++) Leaf(buf, k, ext[i + k]);
            long block = AllocOne();
            WriteBlocks(block, buf);
            used++;
            level.Add((ext[i].Logical, block));
        }
        int depth = 1;
        while (level.Count > 4)
        {
            var upper = new List<(long Logical, long Block)>();
            for (int i = 0; i < level.Count; i += perBlock)
            {
                int n = Math.Min(perBlock, level.Count - i);
                Array.Clear(buf);
                Header(buf, n, perBlock, depth);
                for (int k = 0; k < n; k++) Index(buf, k, level[i + k].Logical, level[i + k].Block);
                long block = AllocOne();
                WriteBlocks(block, buf);
                used++;
                upper.Add((level[i].Logical, block));
            }
            level = upper;
            depth++;
        }
        Header(iblock, level.Count, 4, depth);
        for (int i = 0; i < level.Count; i++) Index(iblock, i, level[i].Logical, level[i].Block);
        return used;
    }

    private long BuildBlockMap(List<Run> runs, long fileBlocks, Span<byte> iblock, out uint flags)
    {
        flags = 0;
        if (runs.Count == 0) return 0;
        if (fileBlocks > int.MaxValue / 8) throw new FsException("The file is too large for an ext2/ext3 block map; use ext4.");
        var map = new uint[fileBlocks];
        foreach (Run r in runs)
            for (long i = 0; i < r.Count; i++) map[r.Logical + i] = (uint)(r.Phys + i);

        int a = _bs / 4;
        long used = 0;
        for (int i = 0; i < 12 && i < map.Length; i++) Le.W32(iblock, i * 4, map[i]);
        long first = 12;
        long span = a;
        for (int level = 1; level <= 3 && first < map.Length; level++)
        {
            Le.W32(iblock, (11 + level) * 4, BuildIndirect(map, first, level, ref used));
            first += span;
            span *= a;
        }
        if (first < map.Length) throw new FsException("The file is too large for an ext2/ext3 block map; use ext4.");
        return used;
    }

    private uint BuildIndirect(uint[] map, long first, int level, ref long used)
    {
        int a = _bs / 4;
        long span = 1;
        for (int i = 1; i < level; i++) span *= a;
        long end = Math.Min(map.Length, first + span * a);
        bool any = false;
        for (long i = first; i < end; i++)
        {
            if (map[i] != 0)
            {
                any = true;
                break;
            }
        }
        if (!any) return 0;

        long block = AllocOne();
        used++;
        byte[] buf = new byte[_bs];
        for (int i = 0; i < a; i++)
        {
            long at = first + i * span;
            if (at >= map.Length) break;
            uint value = level == 1 ? map[at] : BuildIndirect(map, at, level - 1, ref used);
            Le.W32(buf, i * 4, value);
        }
        WriteBlocks(block, buf);
        return (uint)block;
    }

    private void StoreAndMap(Rec rec, Stream data, long size, bool sparse, bool report)
    {
        List<Run> runs = StoreData(data, size, sparse, report);
        long dataBlocks = 0;
        foreach (Run r in runs) dataBlocks += r.Count;
        long mapBlocks = BuildMap(runs, (size + _bs - 1) / _bs, rec.IBlock, out rec.Flags);
        rec.Size = size;
        rec.Sectors += (dataBlocks + mapBlocks) * (_bs / 512);
    }

    private void WriteContent(Rec rec)
    {
        if (rec.Inode is null)
        {
            // Synthetic lost+found: an empty directory.
            byte[] dir = BuildDirectory(rec.Number, rec.ParentNumber, Array.Empty<FsNode>(), addLostFound: false);
            StoreAndMap(rec, new MemoryStream(dir), dir.Length, sparse: false, report: false);
            return;
        }

        FsInode inode = rec.Inode;
        switch (inode.Type)
        {
            case FsNodeType.Directory:
            {
                FsNode node = rec.Node!;
                bool addLf = node.IsRoot && _lostFound is { Inode: null };
                byte[] dir = BuildDirectory(rec.Number, rec.ParentNumber, node.Children, addLf);
                StoreAndMap(rec, new MemoryStream(dir), dir.Length, sparse: false, report: false);
                break;
            }
            case FsNodeType.File:
                if (inode.Data is null || inode.Size == 0)
                {
                    BuildMap(new List<Run>(), 0, rec.IBlock, out rec.Flags);
                }
                else
                {
                    using Stream s = inode.Data.OpenRead();
                    StoreAndMap(rec, s, inode.Size, _opt.SparseFiles, report: true);
                }
                break;
            case FsNodeType.Symlink:
            {
                byte[] target = PosixName.Encode(inode.LinkTarget ?? "");
                if (target.Length < 60)
                {
                    target.CopyTo(rec.IBlock, 0);       // fast symlink: the target lives in i_block
                    rec.Size = target.Length;
                }
                else
                {
                    StoreAndMap(rec, new MemoryStream(target), target.Length, sparse: false, report: false);
                }
                break;
            }
            case FsNodeType.CharDevice:
            case FsNodeType.BlockDevice:
                if (inode.DevMajor < 256 && inode.DevMinor < 256)
                    Le.W32(rec.IBlock, 0, (inode.DevMajor << 8) | inode.DevMinor);
                else
                    Le.W32(rec.IBlock, 4, (inode.DevMinor & 0xFF) | (inode.DevMajor << 8) | ((inode.DevMinor & ~0xFFu) << 12));
                break;
        }
        if (_opt.StoreXattrs && inode.HasXAttrs) StoreXattrs(rec);
    }

    private byte[] BuildDirectory(uint self, uint parent, IReadOnlyCollection<FsNode> children, bool addLostFound)
    {
        var entries = new List<(uint Inode, int Type, byte[] Name)>(children.Count + 3)
        {
            (self, FtDir, new[] { (byte)'.' }),
            (parent, FtDir, new[] { (byte)'.', (byte)'.' }),
        };
        var sorted = new List<(byte[] Name, FsNode Node)>(children.Count);
        foreach (FsNode c in children) sorted.Add((PosixName.Encode(c.Name), c));
        sorted.Sort(static (x, y) => x.Name.AsSpan().SequenceCompareTo(y.Name));
        if (addLostFound) entries.Add((_lostFound!.Number, FtDir, Encoding.ASCII.GetBytes("lost+found")));
        foreach ((byte[] name, FsNode node) in sorted)
            entries.Add((_byInode[node.Inode].Number, FileType(node.Type), name));

        var ms = new MemoryStream();
        byte[] block = new byte[_bs];
        int pos = 0, last = -1;
        void Flush()
        {
            Le.W16(block, last + 4, _bs - last);        // the last entry owns the rest of the block
            ms.Write(block);
            Array.Clear(block);
            pos = 0;
            last = -1;
        }
        foreach ((uint ino, int type, byte[] name) in entries)
        {
            int need = 8 + ((name.Length + 3) & ~3);
            if (pos + need > _bs) Flush();
            Le.W32(block, pos, ino);
            Le.W16(block, pos + 4, need);
            block[pos + 6] = (byte)name.Length;
            block[pos + 7] = (byte)type;
            name.CopyTo(block, pos + 8);
            last = pos;
            pos += need;
        }
        Flush();
        return ms.ToArray();
    }

    // --------------------------------------------------------------- xattrs

    private void StoreXattrs(Rec rec)
    {
        var entries = new List<(int Index, byte[] Name, byte[] Value)>();
        foreach ((string full, byte[] raw) in rec.Inode!.XAttrs!)
        {
            if (!SplitXattrName(full, out int index, out string suffix))
            {
                _warnings.Add($"{rec.Node!.Path}: extended attribute '{full}' cannot be stored in ext and was skipped.");
                continue;
            }
            byte[] value = raw;
            if (index is 2 or 3)
            {
                byte[]? disk = AclXattrToDisk(raw);
                if (disk is null)
                {
                    _warnings.Add($"{rec.Node!.Path}: malformed POSIX ACL '{full}' was skipped.");
                    continue;
                }
                value = disk;
            }
            entries.Add((index, PosixName.Encode(suffix), value));
        }
        if (entries.Count == 0) return;

        int entryBytes = 0, valueBytes = 0;
        foreach ((_, byte[] name, byte[] value) in entries)
        {
            entryBytes += (16 + name.Length + 3) & ~3;
            valueBytes += (value.Length + 3) & ~3;
        }

        // In-inode area: magic, entries, terminator; values grow down from the end of the inode.
        int extra = _isz >= 256 ? 32 : 0;
        int room = _isz - 128 - extra;
        if (_isz >= 256 && 4 + entryBytes + 4 + valueBytes <= room)
        {
            byte[] area = new byte[room];
            Le.W32(area, 0, XattrMagic);
            FillXattrEntries(area, 4, 4, room, entries, hash: true);
            rec.InlineXattrs = area;
            return;
        }

        if (32 + entryBytes + 4 + valueBytes > _bs)
        {
            _warnings.Add($"{rec.Node!.Path}: the extended attributes do not fit into one {_bs}-byte block and were skipped.");
            return;
        }

        // The kernel requires the entries of an attribute block to be sorted.
        entries.Sort(static (x, y) =>
        {
            int c = x.Index.CompareTo(y.Index);
            if (c == 0) c = x.Name.Length.CompareTo(y.Name.Length);
            return c != 0 ? c : x.Name.AsSpan().SequenceCompareTo(y.Name);
        });
        byte[] block = new byte[_bs];
        Le.W32(block, 0, XattrMagic);
        Le.W32(block, 4, 1);
        Le.W32(block, 8, 1);
        uint blockHash = FillXattrEntries(block, 32, 0, _bs, entries, hash: true);
        Le.W32(block, 12, blockHash);

        string key = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(block));
        if (!_xattrBlocks.TryGetValue(key, out XattrBlock? xb) || xb.RefCount >= 1024)
        {
            xb = new XattrBlock { Data = block, Block = AllocOne() };
            _xattrBlocks[key] = xb;
        }
        xb.RefCount++;
        rec.FileAcl = xb.Block;
        rec.Sectors += _bs / 512;
    }

    /// <summary>Lays out attribute entries at <paramref name="pos"/> and their values at the end of the buffer.</summary>
    private static uint FillXattrEntries(byte[] buf, int pos, int valueBase, int end,
        List<(int Index, byte[] Name, byte[] Value)> entries, bool hash)
    {
        int valueEnd = end;
        uint blockHash = 0;
        bool hashValid = true;
        foreach ((int index, byte[] name, byte[] value) in entries)
        {
            int padded = (value.Length + 3) & ~3;
            valueEnd -= padded;
            value.CopyTo(buf, valueEnd);
            buf[pos] = (byte)name.Length;
            buf[pos + 1] = (byte)index;
            Le.W16(buf, pos + 2, value.Length == 0 ? 0 : valueEnd - valueBase);
            Le.W32(buf, pos + 4, 0);
            Le.W32(buf, pos + 8, (uint)value.Length);
            uint h = 0;
            if (hash)
            {
                h = EntryHash(name, buf.AsSpan(valueEnd, padded));
                if (h == 0) hashValid = false;
                blockHash = (blockHash << 16) ^ (blockHash >> 16) ^ h;
            }
            Le.W32(buf, pos + 12, h);
            name.CopyTo(buf, pos + 16);
            pos += (16 + name.Length + 3) & ~3;
        }
        return hashValid ? blockHash : 0;
    }

    // -------------------------------------------------------------- journal

    private void WriteJournal()
    {
        var runs = new List<Run>();
        long left = _journalBlocks, logical = 0;
        while (left > 0)
        {
            long start = AllocRun(left, out long got);
            AddRun(runs, logical, start, got);
            logical += got;
            left -= got;
        }

        // JBD2 superblock (big-endian), an empty and clean journal.
        byte[] jsb = new byte[_bs];
        BinaryPrimitives.WriteUInt32BigEndian(jsb.AsSpan(0x00), JournalMagic);
        BinaryPrimitives.WriteUInt32BigEndian(jsb.AsSpan(0x04), 4);                 // superblock v2
        BinaryPrimitives.WriteUInt32BigEndian(jsb.AsSpan(0x0C), (uint)_bs);
        BinaryPrimitives.WriteUInt32BigEndian(jsb.AsSpan(0x10), (uint)_journalBlocks);
        BinaryPrimitives.WriteUInt32BigEndian(jsb.AsSpan(0x14), 1);                 // first log block
        BinaryPrimitives.WriteUInt32BigEndian(jsb.AsSpan(0x18), 1);                 // first expected commit id
        BinaryPrimitives.WriteUInt32BigEndian(jsb.AsSpan(0x1C), 0);                 // start: 0 = journal is empty
        _uuid.TryWriteBytes(jsb.AsSpan(0x30, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt32BigEndian(jsb.AsSpan(0x40), 1);                 // number of users
        WriteBlocks(runs[0].Phys, jsb);

        var rec = new Rec { Number = JournalInode, Links = 1, Size = _journalBlocks * _bs };
        long mapBlocks = BuildMap(runs, _journalBlocks, rec.IBlock, out rec.Flags);
        rec.Sectors = (_journalBlocks + mapBlocks) * (_bs / 512);
        for (int i = 0; i < 15; i++) _journalIBlock[i] = Le.U32(rec.IBlock, i * 4);
        _journalRec = rec;
    }

    private Rec? _journalRec;

    // --------------------------------------------------------- inode tables

    private byte[] EncodeInode(Rec rec, int mode, uint uid, uint gid, long mtime)
    {
        byte[] b = new byte[_isz];
        // Times outside the representable range are clamped, as the kernel does, instead of wrapping.
        mtime = _isz >= 256
            ? Math.Clamp(mtime, int.MinValue, 0x37FFFFFFFL)
            : Math.Clamp(mtime, int.MinValue, int.MaxValue);
        Le.W16(b, 0x00, mode);
        Le.W16(b, 0x02, (int)(uid & 0xFFFF));
        Le.W32(b, 0x04, (uint)rec.Size);
        uint t = unchecked((uint)mtime);
        Le.W32(b, 0x08, t);
        Le.W32(b, 0x0C, t);
        Le.W32(b, 0x10, t);
        Le.W16(b, 0x18, (int)(gid & 0xFFFF));
        Le.W16(b, 0x1A, rec.Links > MaxLinks ? 1 : rec.Links);
        Le.W32(b, 0x1C, (uint)rec.Sectors);
        Le.W32(b, 0x20, rec.Flags);
        rec.IBlock.CopyTo(b, 0x28);
        Le.W32(b, 0x68, (uint)rec.FileAcl);
        if ((mode & UnixMode.S_IFMT) == UnixMode.S_IFREG) Le.W32(b, 0x6C, (uint)(rec.Size >> 32));
        Le.W16(b, 0x74, (int)(rec.Sectors >> 32));
        Le.W16(b, 0x76, (int)(rec.FileAcl >> 32));
        Le.W16(b, 0x78, (int)(uid >> 16));
        Le.W16(b, 0x7A, (int)(gid >> 16));
        if (_isz >= 256)
        {
            // Timestamps past 2038 keep their upper bits in the "extra" fields.
            uint epoch = (uint)(((mtime - (int)t) >> 32) & 3);
            Le.W16(b, 0x80, 32);
            Le.W32(b, 0x84, epoch);
            Le.W32(b, 0x88, epoch);
            Le.W32(b, 0x8C, epoch);
            Le.W32(b, 0x90, t);
            Le.W32(b, 0x94, epoch);
            rec.InlineXattrs?.CopyTo(b, 160);
        }
        return b;
    }

    private void WriteInodeTables()
    {
        var raw = new SortedDictionary<uint, byte[]>();
        foreach (Rec rec in _recs)
        {
            if (rec.Inode is null)
            {
                raw[rec.Number] = EncodeInode(rec, UnixMode.S_IFDIR | 0x1C0, 0, 0, _now);
                continue;
            }
            FsInode i = rec.Inode;
            uint uid = _opt.AllRoot ? 0 : i.Uid, gid = _opt.AllRoot ? 0 : i.Gid;
            int mode = UnixMode.TypeBits(i.Type) | (i.Type == FsNodeType.Symlink ? 0x1FF : i.Mode & 0xFFF);
            raw[rec.Number] = EncodeInode(rec, mode, uid, gid, i.MTime);
        }
        if (_journalRec is not null) raw[JournalInode] = EncodeInode(_journalRec, UnixMode.S_IFREG | 0x180, 0, 0, _now);

        // Write group by group so that every inode table is touched once.
        byte[] table = new byte[(long)_itb * _bs];
        uint group = uint.MaxValue;
        bool dirty = false;
        void Flush()
        {
            if (dirty) WriteBlocks(BlockBitmapBlock(group) + 2, table);
            dirty = false;
        }
        foreach ((uint number, byte[] bytes) in raw)
        {
            uint g = (number - 1) / _ipg;
            if (g != group)
            {
                Flush();
                Array.Clear(table);
                group = g;
            }
            bytes.CopyTo(table, (int)((number - 1) % _ipg) * _isz);
            dirty = true;
        }
        Flush();
    }

    // ------------------------------------------------- bitmaps, descriptors

    private void WriteGroupMetadata()
    {
        byte[] gdt = new byte[(long)_gdtBlocks * _bs];
        long freeBlocksTotal = 0;
        byte[] bmp = new byte[_bs];
        var dirsPerGroup = new uint[_groups];
        foreach (Rec rec in _recs)
            if (rec.Inode is null || rec.Inode.Type == FsNodeType.Directory) dirsPerGroup[(rec.Number - 1) / _ipg]++;

        for (uint g = 0; g < _groups; g++)
        {
            long start = GroupStart(g);
            long count = GroupBlockCount(g);

            // Block bitmap; bits past the end of the filesystem are padding and must be set.
            Array.Fill(bmp, (byte)0xFF);
            long free = 0;
            for (long i = 0; i < count; i++)
            {
                if (IsUsed(start + i)) continue;
                bmp[i >> 3] &= (byte)~(1 << (int)(i & 7));
                free++;
            }
            long bb = BlockBitmapBlock(g);
            WriteBlocks(bb, bmp);
            freeBlocksTotal += free;

            // Inode bitmap.
            Array.Fill(bmp, (byte)0xFF);
            long firstInode = (long)g * _ipg + 1;
            uint freeInodes = 0;
            for (uint i = 0; i < _ipg; i++)
            {
                if (firstInode + i <= _usedInodes) continue;
                bmp[i >> 3] &= (byte)~(1 << (int)(i & 7));
                freeInodes++;
            }
            WriteBlocks(bb + 1, bmp);

            int o = (int)g * 32;
            Le.W32(gdt, o + 0x00, (uint)bb);
            Le.W32(gdt, o + 0x04, (uint)(bb + 1));
            Le.W32(gdt, o + 0x08, (uint)(bb + 2));
            Le.W16(gdt, o + 0x0C, (int)free);
            Le.W16(gdt, o + 0x0E, (int)freeInodes);
            Le.W16(gdt, o + 0x10, (int)dirsPerGroup[g]);
        }

        _freeBlocks = freeBlocksTotal;
        long totalInodes = (long)_ipg * _groups;
        byte[] sb = BuildSuperblock(freeBlocksTotal, (uint)(totalInodes - _usedInodes));
        for (uint g = 0; g < _groups; g++)
        {
            if (!GroupHasSuperblock(g)) continue;
            long start = GroupStart(g);
            Le.W16(sb, 0x5A, (int)g);
            if (g == 0)
            {
                _out.Position = _base + SuperblockOffset;
                _out.Write(sb);
            }
            else
            {
                byte[] block = new byte[_bs];
                sb.CopyTo(block, 0);
                WriteBlocks(start, block);
            }
            WriteBlocks(start + 1, gdt);
        }
    }

    private byte[] BuildSuperblock(long freeBlocks, uint freeInodes)
    {
        byte[] sb = new byte[1024];
        uint compat = CompatExtAttr | CompatDirIndex;
        uint incompat = IncompatFiletype;
        uint ro = RoSparseSuper | RoLargeFile;
        if (_journal) compat |= CompatHasJournal;
        if (_dirNlink) ro |= RoDirNlink;
        if (_extents)
        {
            incompat |= IncompatExtents;
            ro |= RoHugeFile | RoDirNlink;
            if (_isz >= 256) ro |= RoExtraIsize;
        }

        Le.W32(sb, 0x00, _ipg * _groups);
        Le.W32(sb, 0x04, (uint)_blocks);
        Le.W32(sb, 0x08, (uint)(_blocks * _opt.ReservedPercent / 100.0));
        Le.W32(sb, 0x0C, (uint)freeBlocks);
        Le.W32(sb, 0x10, freeInodes);
        Le.W32(sb, 0x14, _firstDataBlock);
        uint logBlock = (uint)System.Numerics.BitOperations.Log2((uint)_bs) - 10;
        Le.W32(sb, 0x18, logBlock);
        Le.W32(sb, 0x1C, logBlock);
        Le.W32(sb, 0x20, _bpg);
        Le.W32(sb, 0x24, _bpg);
        Le.W32(sb, 0x28, _ipg);
        Le.W32(sb, 0x30, (uint)_now);                   // last write time
        Le.W16(sb, 0x36, 0xFFFF);                       // max mount count: disabled
        Le.W16(sb, 0x38, Magic);
        Le.W16(sb, 0x3A, 1);                            // state: clean
        Le.W16(sb, 0x3C, 1);                            // on errors: continue
        Le.W32(sb, 0x40, (uint)_now);                   // last check
        Le.W32(sb, 0x4C, 1);                            // revision: dynamic
        Le.W32(sb, 0x54, FirstInode);
        Le.W16(sb, 0x58, _isz);
        Le.W32(sb, 0x5C, compat);
        Le.W32(sb, 0x60, incompat);
        Le.W32(sb, 0x64, ro);
        _uuid.TryWriteBytes(sb.AsSpan(0x68, 16), bigEndian: true, out _);
        byte[] label = Encoding.UTF8.GetBytes(_opt.Label ?? "");
        label.AsSpan(0, Math.Min(16, label.Length)).CopyTo(sb.AsSpan(0x78));
        Guid.NewGuid().TryWriteBytes(sb.AsSpan(0xEC, 16));  // directory hash seed
        sb[0xFC] = 1;                                   // default hash: half MD4
        if (_journal)
        {
            Le.W32(sb, 0xE0, JournalInode);
            sb[0xFD] = 1;                               // s_jnl_blocks holds a copy of the journal inode's i_block
            for (int i = 0; i < 15; i++) Le.W32(sb, 0x10C + i * 4, _journalIBlock[i]);
            long jsize = _journalBlocks * _bs;
            Le.W32(sb, 0x10C + 15 * 4, (uint)(jsize >> 32));
            Le.W32(sb, 0x10C + 16 * 4, (uint)jsize);
        }
        Le.W32(sb, 0x108, (uint)_now);                  // mkfs time
        if (_extents && _isz >= 256)
        {
            Le.W16(sb, 0x15C, 32);
            Le.W16(sb, 0x15E, 32);
        }
        Le.W32(sb, 0x160, 1);                           // flags: signed directory hash
        return sb;
    }
}
