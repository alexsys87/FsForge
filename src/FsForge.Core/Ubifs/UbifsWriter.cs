using FsForge.Core.Compression;

namespace FsForge.Core.Ubifs;

/// <summary>Parameters of a UBIFS image; the counterparts of the mkfs.ubifs options.</summary>
public sealed class UbifsOptions
{
    /// <summary>Minimal input/output unit of the flash (mkfs.ubifs -m): the page size of NAND, 1 for NOR.</summary>
    public int MinIoSize { get; set; } = 2048;

    /// <summary>Size of a logical erase block (mkfs.ubifs -e): the erase block of the flash without the UBI headers.</summary>
    public int LebSize { get; set; } = 126976;

    /// <summary>
    /// How many logical erase blocks the filesystem may grow to (mkfs.ubifs -c); 0 selects a limit of
    /// about 1 GiB, or four times the content when that is more.
    /// </summary>
    public int MaxLebCount { get; set; }

    /// <summary>
    /// Size, in logical erase blocks, of the volume the image is made for; 0 when it is not known.
    /// The journal is sized for it, so that the kernel accepts the filesystem in a volume of that size.
    /// </summary>
    public int VolumeLebCount { get; set; }

    /// <summary>Compressor of data blocks (mkfs.ubifs -x). Blocks that do not shrink are stored as they are.</summary>
    public UbifsCompression Compression { get; set; } = UbifsCompression.Lzo;

    /// <summary>Fanout of the index tree (mkfs.ubifs -f).</summary>
    public int Fanout { get; set; } = 8;

    /// <summary>Upper limit of the journal in bytes (mkfs.ubifs -j); 0 selects the mkfs.ubifs default.</summary>
    public long JournalBytes { get; set; }

    /// <summary>Number of erase blocks of the log (mkfs.ubifs -l); 0 selects what the journal needs.</summary>
    public int LogLebs { get; set; }

    /// <summary>Number of erase blocks for orphans (mkfs.ubifs -p).</summary>
    public int OrphanLebs { get; set; } = 1;

    /// <summary>
    /// Ask the kernel to fix up the free space on first mount (mkfs.ubifs -F). Needed when the image goes
    /// to NAND through a tool that does not skip erased pages properly.
    /// </summary>
    public bool SpaceFixup { get; set; }

    public Guid? Uuid { get; set; }
    public int ZstdLevel { get; set; } = 3;
    public bool StoreXattrs { get; set; } = true;
    public bool AllRoot { get; set; }

    public UbifsOptions Clone() => (UbifsOptions)MemberwiseClone();

    /// <summary>Options that reproduce the geometry of an existing image.</summary>
    public static UbifsOptions FromInfo(UbifsInfo info) => new()
    {
        MinIoSize = info.MinIoSize,
        LebSize = info.LebSize,
        MaxLebCount = info.MaxLebCount,
        Compression = Enum.IsDefined(info.DefaultCompression) ? info.DefaultCompression : UbifsCompression.Lzo,
        Fanout = info.Fanout >= 3 ? info.Fanout : 8,
        JournalBytes = info.JournalBytes,
        LogLebs = info.LogLebs,
        OrphanLebs = Math.Max(1, info.OrphanLebs),
        SpaceFixup = info.SpaceFixup,
        Uuid = info.Uuid,
    };

    public void Validate()
    {
        int minIo = Math.Max(8, MinIoSize);
        if (MinIoSize < 1 || (MinIoSize & (MinIoSize - 1)) != 0) throw new FsException("The UBIFS min. I/O unit size must be a power of two.");
        if (LebSize < UbifsWriter.MinLebSize) throw new FsException($"The UBIFS erase block (LEB) size must be at least {UbifsWriter.MinLebSize} bytes.");
        if (LebSize > UbifsWriter.MaxLebSize) throw new FsException($"The UBIFS erase block (LEB) size must be at most {UbifsWriter.MaxLebSize} bytes.");
        if (LebSize % minIo != 0 || LebSize % 8 != 0)
            throw new FsException("The UBIFS erase block (LEB) size must be a multiple of the min. I/O unit size and of 8.");
        if (MaxLebCount != 0 && MaxLebCount < UbifsWriter.MinLebCount)
            throw new FsException($"The maximum number of UBIFS erase blocks must be at least {UbifsWriter.MinLebCount}.");
        if (VolumeLebCount < 0 || JournalBytes < 0 || LogLebs < 0) throw new FsException("UBIFS sizes cannot be negative.");
        if (Fanout < 3 || Fanout > (LebSize - 28) / 28) throw new FsException($"The UBIFS index fanout must be between 3 and {(LebSize - 28) / 28}.");
        if (OrphanLebs < 1) throw new FsException("UBIFS needs at least one erase block for orphans.");
        if (!Enum.IsDefined(Compression)) throw new FsException("Unknown UBIFS compressor.");
    }
}

/// <summary>
/// Writer of UBIFS images. It lays the filesystem out the way mkfs.ubifs does: data and index nodes
/// in the main area, then the LEB properties tree, the master nodes, the superblock and an empty log.
/// </summary>
public sealed class UbifsWriter
{
    internal const int MinLebSize = 15 * 1024, MaxLebSize = 2 * 1024 * 1024, MinLebCount = 17;

    private const uint NodeMagic = 0x06101831;
    private const int BlockSize = 4096;
    private const int ChSize = 24, InoNodeSize = 160, DentNodeSize = 56, DataNodeSize = 48, IdxNodeSize = 28, BranchSize = 20,
        PadNodeSize = 28, SbNodeSize = 4096, MstNodeSize = 512, RefNodeSize = 64, CsNodeSize = 32;
    private const int MaxInoData = 4096, MaxNodeSize = InoNodeSize + MaxInoData, MinWriteSize = DataNodeSize + 8;
    private const int MinCompressLength = 128;
    private const int NodeIno = 0, NodeData = 1, NodeDent = 2, NodeXent = 3, NodePad = 5, NodeSb = 6, NodeMst = 7, NodeIdx = 9, NodeCs = 10;
    private const int KeyIno = 0, KeyData = 1, KeyDent = 2, KeyXent = 3;
    private const int SbLebs = 1, MstLebs = 2, LogLnum = 3, MinLogLebs = 2, MinLptLebs = 2;
    private const uint RootInode = 1, FirstInode = 64;
    private const int FlagCompr = 0x01, FlagXattr = 0x20;
    private const int LpropsIndex = 0x20;
    private const int LptFanout = 4, LptFanoutShift = 2, LptCrcBits = 16, LptTypeBits = 4;
    private const int LptPnode = 0, LptNnode = 1, LptLtab = 2, LptLsave = 3;
    private const int LsaveCount = 256, JheadCount = 1;

    private readonly FsTree _tree;
    private readonly Stream _out;
    private readonly long _origin;
    private readonly UbifsOptions _o;
    private readonly IProgress<ProgressInfo>? _progress;
    private readonly CancellationToken _ct;
    private readonly List<string> _warnings = new();

    // Geometry.
    private readonly int _minIo, _lebSize, _fanout, _maxLebCount, _logLebs, _orphLebs;
    private readonly long _maxBudBytes;
    private int _lptLebs, _mainFirst, _lptFirst, _lptLast, _lebCount, _mainLebs;
    private bool _bigLpt;
    private int _deadWm, _darkWm;

    // LEB properties tree geometry.
    private int _lptHeight, _spaceBits, _lptLnumBits, _lptOffsBits, _lptSpcBits, _pcntBits, _lnumBits;
    private int _pnodeSize, _nnodeSize, _ltabSize, _lsaveSize;
    private long _lptSize;

    // Write head in the main area.
    private readonly byte[] _lebBuf;
    private int _headLnum, _headOffs, _headFlags;
    private ulong _maxSqnum;
    private uint _highestInum = FirstInode;
    private readonly List<(int Free, int Dirty, int Flags)> _lprops = new();
    private long _totalFree, _totalDirty, _totalUsed, _totalDead, _totalDark;
    private int _emptyLebs, _idxLebs;

    // Index.
    private readonly record struct IndexEntry(ulong Key, byte[]? Name, int Lnum, int Offs, int Len);
    private readonly List<IndexEntry> _index = new();
    private (int Lnum, int Offs, int Len) _root;
    private long _indexSize;
    private int _iheadLnum, _iheadOffs, _gcLnum;

    // Results of writing the LPT.
    private int _lptLnum, _lptOffs, _nheadLnum, _nheadOffs, _ltabLnum, _ltabOffs, _lsaveLnum, _lsaveOffs;

    // Compression.
    private readonly LzoCodec? _lzo;
    private readonly RawDeflate? _deflate;
    private readonly ZstdSharp.Compressor? _zstd;
    private readonly byte[] _packed = new byte[BlockSize];

    private long _dataDone, _dataTotal;

    private UbifsWriter(FsTree tree, Stream output, UbifsOptions options, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        options.Validate();
        _tree = tree;
        _out = output;
        _origin = output.Position;
        _o = options;
        _progress = progress;
        _ct = ct;
        _minIo = Math.Max(8, options.MinIoSize);
        _lebSize = options.LebSize;
        _fanout = options.Fanout;
        _orphLebs = options.OrphanLebs;
        _lebBuf = new byte[_lebSize];

        TreeStats stats = tree.GetStats();
        _dataTotal = stats.DataBytes;
        _maxLebCount = options.MaxLebCount;
        if (_maxLebCount == 0)
        {
            // An upper bound of what the content takes, then room to grow: a volume is usually larger than its first image.
            long bound = stats.DataBytes / BlockSize * (DataNodeSize + BlockSize + 8) + stats.Total * 1024 + (64L << 10);
            long lebs = Math.Max((1L << 30) / _lebSize, 4 * (bound * 5 / 4 / _lebSize + 32));
            _maxLebCount = (int)Math.Min(int.MaxValue / 2, Math.Max(lebs, options.VolumeLebCount));
        }

        // The journal: an eighth of the main area, between 4 erase blocks and 8 MiB (as mkfs.ubifs chooses it).
        int room = options.VolumeLebCount > 0 ? Math.Min(_maxLebCount, options.VolumeLebCount) : _maxLebCount;
        _maxBudBytes = options.JournalBytes;
        if (_maxBudBytes == 0)
        {
            int lebs = room - SbLebs - MstLebs - _orphLebs - (options.LogLebs > 0 ? options.LogLebs : MinLogLebs) - MinLptLebs;
            _maxBudBytes = Math.Clamp(lebs / 8 * (long)_lebSize, 4L * _lebSize, Math.Max(4L * _lebSize, 8 << 20));
        }
        _logLebs = options.LogLebs > 0 ? options.LogLebs : MinLogLebsFor(_maxBudBytes) + 2;
        if (_logLebs < MinLogLebsFor(_maxBudBytes))
            throw new FsException($"The UBIFS log is too small for the journal: at least {MinLogLebsFor(_maxBudBytes)} erase blocks are needed.");
        if (_maxLebCount < MinLebCount || SbLebs + MstLebs + _logLebs + MinLptLebs + _orphLebs + 4 > _maxLebCount)
            throw new FsNoSpaceException("The maximum number of UBIFS erase blocks is too low for the fixed areas of the filesystem.");

        switch (options.Compression)
        {
            case UbifsCompression.Lzo: _lzo = new LzoCodec(); break;
            case UbifsCompression.Zlib: _deflate = new RawDeflate(11); break;
            case UbifsCompression.Zstd: _zstd = new ZstdSharp.Compressor(Math.Clamp(options.ZstdLevel, 1, 22)); break;
        }
    }

    /// <summary>Writes the tree as a UBIFS image at the current position of the stream; returns the warnings.</summary>
    public static IReadOnlyList<string> Write(FsTree tree, Stream output, UbifsOptions options,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var w = new UbifsWriter(tree, output, options, progress, ct);
        try
        {
            w.Run();
        }
        finally
        {
            w._zstd?.Dispose();
        }
        return w._warnings;
    }

    private static int Align(int value, int step) => (value + step - 1) / step * step;

    private int MinLogLebsFor(long maxBudBytes)
    {
        long buds = (maxBudBytes + _lebSize - 1) / _lebSize;
        long logSize = Align(RefNodeSize, _minIo) * buds + Align(CsNodeSize + RefNodeSize * (JheadCount + 2), _minIo);
        return (int)((logSize + _lebSize - 1) / _lebSize) + 1;
    }

    private void Run()
    {
        int maxMainLebs = _maxLebCount - SbLebs - MstLebs - _logLebs - _orphLebs;
        CalcDefaultLptGeometry(maxMainLebs);
        _mainFirst = LogLnum + _logLebs + _lptLebs + _orphLebs;
        _lptFirst = LogLnum + _logLebs;
        _lptLast = _lptFirst + _lptLebs - 1;
        _headLnum = _mainFirst;
        _deadWm = Align(MinWriteSize, _minIo);
        _darkWm = Align(MaxNodeSize, _minIo);

        WriteData();
        FlushNodes();
        // One empty erase block is set aside for the garbage collector.
        _gcLnum = _headLnum++;
        WriteLeb(_gcLnum, 0);
        SetLprops(_gcLnum, 0, 0);
        _emptyLebs++;
        WriteIndex();

        _lebCount = _headLnum;
        if (_lebCount > _maxLebCount)
            throw new FsNoSpaceException($"The content needs {_lebCount} erase blocks, more than the maximum of {_maxLebCount} set for the filesystem.");
        _mainLebs = _lebCount - _mainFirst;

        CreateLpt();
        for (int lnum = _nheadLnum + 1; lnum <= _lptLast; lnum++) WriteLeb(lnum, 0);
        WriteMaster();
        WriteSuperblock();
        WriteLog();
        for (int i = 0; i < _orphLebs; i++) WriteLeb(LogLnum + _logLebs + _lptLebs + i, 0);

        _out.Position = _origin + (long)_lebCount * _lebSize;
        _progress?.Report(new ProgressInfo("done", 1, 1));
    }

    // ------------------------------------------------------------ nodes

    /// <summary>Writes the first <paramref name="length"/> bytes of the block buffer as an erase block; the rest is erased flash.</summary>
    private void WriteLeb(int lnum, int length)
    {
        _lebBuf.AsSpan(length).Fill(0xFF);
        _out.Position = _origin + (long)lnum * _lebSize;
        _out.Write(_lebBuf, 0, _lebSize);
    }

    private static uint Crc(ReadOnlySpan<byte> data) => ~Crc32.Compute(data);

    /// <summary>Fills in the common header of a node: magic, length, sequence number and checksum.</summary>
    private void PrepareNode(Span<byte> node, int type)
    {
        Le.W32(node, 0, NodeMagic);
        Le.W64(node, 8, ++_maxSqnum);
        Le.W32(node, 16, (uint)node.Length);
        node[20] = (byte)type;
        node[21] = 0;
        node[22] = node[23] = 0;
        Le.W32(node, 4, Crc(node[8..]));
    }

    /// <summary>Pads what is in the buffer to the I/O unit: with a padding node, or with padding bytes when the gap is too small for one.</summary>
    private int DoPad(Span<byte> buf, int length)
    {
        int aligned = Align(length, 8), written = Align(aligned, _minIo);
        buf[length..aligned].Fill(0xFF);
        int pad = written - aligned;
        if (pad >= PadNodeSize)
        {
            Span<byte> node = buf.Slice(aligned, PadNodeSize);
            node.Clear();
            Le.W32(node, 0, NodeMagic);
            Le.W32(node, 16, PadNodeSize);
            node[20] = NodePad;
            Le.W32(node, 24, (uint)(pad - PadNodeSize));
            Le.W32(node, 4, Crc(node[8..]));
            buf.Slice(aligned + PadNodeSize, pad - PadNodeSize).Clear();
        }
        else if (pad > 0)
        {
            buf.Slice(aligned, pad).Fill(0xCE);
        }
        return written;
    }

    /// <summary>Writes a single node at the start of an erase block (master nodes, the log start).</summary>
    private void WriteNode(Span<byte> node, int type, int lnum)
    {
        PrepareNode(node, type);
        node.CopyTo(_lebBuf);
        WriteLeb(lnum, DoPad(_lebBuf, node.Length));
    }

    private int CalcDark(int space)
    {
        if (space < _darkWm) return space;
        return space - _darkWm < MinWriteSize ? space - MinWriteSize : _darkWm;
    }

    /// <summary>Records how much of an erase block of the main area is free and how much is wasted.</summary>
    private void SetLprops(int lnum, int offs, int flags)
    {
        int free = _lebSize - Align(offs, _minIo);
        int dirty = _lebSize - free - Align(offs, 8);
        int i = lnum - _mainFirst;
        while (_lprops.Count <= i) _lprops.Add((_lebSize, 0, 0));
        _lprops[i] = (free, dirty, flags);
        _totalFree += free;
        _totalDirty += dirty;
        if ((flags & LpropsIndex) != 0)
        {
            _idxLebs++;
            return;
        }
        int space = free + dirty;
        if (space < _deadWm) _totalDead += space;
        else _totalDark += CalcDark(space);
        _totalUsed += _lebSize - space;
    }

    private void FlushNodes()
    {
        if (_headOffs == 0) return;
        WriteLeb(_headLnum, DoPad(_lebBuf, _headOffs));
        SetLprops(_headLnum, _headOffs, _headFlags);
        _headLnum++;
        _headOffs = 0;
        if (_headLnum >= _maxLebCount)
            throw new FsNoSpaceException($"The content does not fit into the maximum of {_maxLebCount} erase blocks set for the filesystem.");
    }

    /// <summary>Puts a finished node at the write head and returns where it went.</summary>
    private (int Lnum, int Offs) Place(ReadOnlySpan<byte> node)
    {
        if (node.Length > _lebSize - _headOffs) FlushNodes();
        (int lnum, int offs) = (_headLnum, _headOffs);
        node.CopyTo(_lebBuf.AsSpan(offs));
        int aligned = Align(node.Length, 8);
        _lebBuf.AsSpan(offs + node.Length, aligned - node.Length).Fill(0xFF);
        _headOffs += aligned;
        return (lnum, offs);
    }

    private static ulong MakeKey(uint inum, int type, uint value) => (ulong)inum << 32 | (uint)type << 29 | value;

    private static void WriteKey(Span<byte> node, ulong key)
    {
        Le.W32(node, 24, (uint)(key >> 32));
        Le.W32(node, 28, (uint)key);
    }

    private void AddNode(Span<byte> node, int type, ulong key, byte[]? name = null)
    {
        WriteKey(node, key);
        PrepareNode(node, type);
        (int lnum, int offs) = Place(node);
        _index.Add(new IndexEntry(key, name, lnum, offs, node.Length));
    }

    /// <summary>The R5 hash of names, as the kernel computes it (on signed characters).</summary>
    private static uint R5(ReadOnlySpan<byte> name)
    {
        uint a = 0;
        foreach (byte c in name)
        {
            int s = (sbyte)c;
            a += (uint)(s << 4);
            a += (uint)(s >> 4);
            a *= 11;
        }
        a &= 0x1FFFFFFF;
        // The values 0 to 2 are reserved for ".", ".." and the end of a directory.
        return a <= 2 ? a + 3 : a;
    }

    // ------------------------------------------------------------- data

    private void WriteData()
    {
        var inums = new Dictionary<FsInode, uint>(ReferenceEqualityComparer.Instance) { [_tree.Root.Inode] = RootInode };
        byte[] node = new byte[Math.Max(InoNodeSize + MaxInoData, DentNodeSize + 256)];
        _headFlags = 0;
        foreach (FsNode n in _tree.Walk())
        {
            _ct.ThrowIfCancellationRequested();
            FsInode inode = n.Inode;
            if (n.IsRoot)
            {
                WriteInode(n, RootInode, node);
                continue;
            }
            if (!inums.TryGetValue(inode, out uint inum))
            {
                inums[inode] = inum = ++_highestInum;
                WriteInode(n, inum, node);
            }
            byte[] name = PosixName.Encode(n.Name);
            Span<byte> dent = node.AsSpan(0, DentNodeSize + name.Length + 1);
            dent.Clear();
            Le.W64(dent, 40, inum);
            dent[49] = DentType(inode.Type);
            Le.W16(dent, 50, name.Length);
            name.CopyTo(dent[DentNodeSize..]);
            AddNode(dent, NodeDent, MakeKey(inums[n.Parent!.Inode], KeyDent, R5(name)), name);
        }
    }

    private static byte DentType(FsNodeType type) => type switch
    {
        FsNodeType.File => 0,
        FsNodeType.Directory => 1,
        FsNodeType.Symlink => 2,
        FsNodeType.BlockDevice => 3,
        FsNodeType.CharDevice => 4,
        FsNodeType.Fifo => 5,
        _ => 6,
    };

    private static int DentSize(int nameLength) => Align(DentNodeSize + nameLength + 1, 8);

    /// <summary>Writes the content of an inode (data blocks, extended attributes) and then its inode node.</summary>
    private void WriteInode(FsNode n, uint inum, byte[] buffer)
    {
        FsInode inode = n.Inode;
        byte[] data = Array.Empty<byte>();
        long size = 0;
        // The kernel expects every node of an inode to be newer than the creation of the inode.
        ulong created = ++_maxSqnum;
        uint links = (uint)Math.Max(1, inode.LinkCount);
        switch (inode.Type)
        {
            case FsNodeType.Directory:
                size = InoNodeSize;
                links = 2;
                foreach (FsNode child in n.Children)
                {
                    size += DentSize(PosixName.Encode(child.Name).Length);
                    if (child.IsDirectory) links++;
                }
                break;
            case FsNodeType.File:
                size = WriteFile(n, inum);
                break;
            case FsNodeType.Symlink:
                data = PosixName.Encode(inode.LinkTarget ?? "");
                if (data.Length is 0 or > MaxInoData - 1)
                    throw new FsException($"The target of the symbolic link '{n.Path}' cannot be stored in UBIFS: it must be 1 to {MaxInoData - 1} bytes long.");
                size = data.Length;
                break;
            case FsNodeType.CharDevice or FsNodeType.BlockDevice:
                // The device number in the kernel's encoding; mkfs.ubifs always uses the 64-bit form.
                data = new byte[8];
                Le.W64(data, 0, (inode.DevMinor & 0xFF) | ((ulong)inode.DevMajor << 8) | ((ulong)(inode.DevMinor & ~0xFFu) << 12));
                break;
        }

        // Every extended attribute is an entry in the inode plus a hidden inode that holds the value.
        int xattrCount = 0, xattrSize = 0, xattrNames = 0;
        if (_o.StoreXattrs && inode.XAttrs is { Count: > 0 } xattrs)
        {
            foreach ((string name, byte[] value) in xattrs)
            {
                byte[] raw = PosixName.Encode(name);
                if (!(name.StartsWith("user.", StringComparison.Ordinal) || name.StartsWith("trusted.", StringComparison.Ordinal) || name.StartsWith("security.", StringComparison.Ordinal)))
                {
                    _warnings.Add($"'{n.Path}': the extended attribute '{name}' was not stored. UBIFS keeps only the user, trusted and security namespaces.");
                    continue;
                }
                if (raw.Length > 255 || value.Length > MaxInoData)
                {
                    _warnings.Add($"'{n.Path}': the extended attribute '{name}' is too large for UBIFS ({MaxInoData} bytes at most) and was not stored.");
                    continue;
                }
                uint holder = ++_highestInum;
                Span<byte> xent = buffer.AsSpan(0, DentNodeSize + raw.Length + 1);
                xent.Clear();
                Le.W64(xent, 40, holder);
                Le.W16(xent, 50, raw.Length);
                raw.CopyTo(xent[DentNodeSize..]);
                AddNode(xent, NodeXent, MakeKey(inum, KeyXent, R5(raw)), raw);

                Span<byte> x = buffer.AsSpan(0, InoNodeSize + value.Length);
                x.Clear();
                FillInode(x, ++_maxSqnum, value.Length, 1, inode, UnixMode.S_IFREG, FlagXattr, value);
                AddNode(x, NodeIno, MakeKey(holder, KeyIno, 0));

                xattrCount++;
                xattrSize += DentSize(raw.Length) + Align(InoNodeSize + value.Length + 1, 8);
                xattrNames += raw.Length;
            }
        }

        Span<byte> ino = buffer.AsSpan(0, InoNodeSize + data.Length);
        ino.Clear();
        int flags = _o.Compression != UbifsCompression.None ? FlagCompr : 0;
        FillInode(ino, created, size, links, inode, UnixMode.TypeBits(inode.Type) | (inode.Mode & 0xFFF), flags, data);
        Le.W32(ino, 116, (uint)xattrCount);
        Le.W32(ino, 120, (uint)xattrSize);
        Le.W32(ino, 128, (uint)xattrNames);
        AddNode(ino, NodeIno, MakeKey(inum, KeyIno, 0));
    }

    private void FillInode(Span<byte> node, ulong created, long size, uint links, FsInode owner, int mode, int flags, ReadOnlySpan<byte> data)
    {
        Le.W64(node, 40, created);                          // creation sequence number
        Le.W64(node, 48, (ulong)size);
        foreach (int at in stackalloc[] { 56, 64, 72 }) Le.W64(node, at, (ulong)owner.MTime);
        Le.W32(node, 92, links);
        Le.W32(node, 96, _o.AllRoot ? 0 : owner.Uid);
        Le.W32(node, 100, _o.AllRoot ? 0 : owner.Gid);
        Le.W32(node, 104, (uint)mode);
        Le.W32(node, 108, (uint)flags);
        Le.W32(node, 112, (uint)data.Length);
        Le.W16(node, 132, (int)_o.Compression);
        data.CopyTo(node[InoNodeSize..]);
    }

    /// <summary>Writes the data nodes of a file, one per 4 KiB block; blocks of zeros become holes. Returns the file size.</summary>
    private long WriteFile(FsNode n, uint inum)
    {
        if (n.Inode.Data is not { } content) return 0;
        byte[] block = new byte[BlockSize], node = new byte[DataNodeSize + BlockSize];
        long size = 0;
        using Stream s = content.OpenRead();
        for (uint number = 0; ; number++)
        {
            int read = s.ReadAtLeast(block, BlockSize, throwOnEndOfStream: false);
            if (read == 0) break;
            size += read;
            if ((number & 0xFF) == 0)
            {
                _ct.ThrowIfCancellationRequested();
                _progress?.Report(new ProgressInfo("data", _dataDone + size, _dataTotal, n.Path));
            }
            ReadOnlySpan<byte> plain = block.AsSpan(0, read);
            if (plain.IndexOfAnyExcept((byte)0) < 0) continue;
            if (number >= 1u << 29) throw new FsException($"'{n.Path}' is too large for UBIFS.");

            UbifsCompression used = UbifsCompression.None;
            ReadOnlySpan<byte> payload = plain;
            if (read >= MinCompressLength && _o.Compression != UbifsCompression.None)
            {
                // The result must be smaller than the block, or it is not worth unpacking.
                Span<byte> room = _packed.AsSpan(0, read - 1);
                int packed = _o.Compression switch
                {
                    UbifsCompression.Lzo => _lzo!.Compress(plain, room),
                    UbifsCompression.Zlib => _deflate!.Compress(plain, room),
                    _ => _zstd!.TryWrap(plain, room, out int written) ? written : -1,
                };
                if (packed > 0)
                {
                    payload = _packed.AsSpan(0, packed);
                    used = _o.Compression;
                }
            }
            Span<byte> dn = node.AsSpan(0, DataNodeSize + payload.Length);
            dn[..DataNodeSize].Clear();
            Le.W32(dn, 40, (uint)read);
            Le.W16(dn, 44, (int)used);
            payload.CopyTo(dn[DataNodeSize..]);
            AddNode(dn, NodeData, MakeKey(inum, KeyData, number));
            if (read < BlockSize) break;
        }
        if (size != content.Length) throw new FsException($"The content of '{n.Path}' changed while the image was being written.");
        _dataDone += size;
        return size;
    }

    // ------------------------------------------------------------ index

    private static int CompareEntries(IndexEntry a, IndexEntry b)
    {
        int c = a.Key.CompareTo(b.Key);
        if (c != 0 || a.Name is null || b.Name is null) return c;
        // Names with the same hash are told apart by the names themselves.
        int common = Math.Min(a.Name.Length, b.Name.Length);
        c = a.Name.AsSpan(0, common).SequenceCompareTo(b.Name.AsSpan(0, common));
        return c != 0 ? c : a.Name.Length.CompareTo(b.Name.Length);
    }

    private (int Lnum, int Offs, int Len) AddIndexNode(Span<byte> node)
    {
        PrepareNode(node, NodeIdx);
        (int lnum, int offs) = Place(node);
        _indexSize += Align(node.Length, 8);
        return (lnum, offs, node.Length);
    }

    /// <summary>Builds the index bottom-up: the leaves sorted by key, <c>fanout</c> of them per node, and so on up to the root.</summary>
    private void WriteIndex()
    {
        _headFlags = LpropsIndex;
        _index.Sort(CompareEntries);
        var level = new List<(ulong Key, int Lnum, int Offs, int Len)>(_index.Count);
        foreach (IndexEntry e in _index) level.Add((e.Key, e.Lnum, e.Offs, e.Len));
        _index.Clear();
        _index.TrimExcess();

        byte[] node = new byte[IdxNodeSize + _fanout * BranchSize];
        for (int height = 0; ; height++)
        {
            _ct.ThrowIfCancellationRequested();
            var next = new List<(ulong Key, int Lnum, int Offs, int Len)>((level.Count + _fanout - 1) / _fanout);
            for (int i = 0; i < level.Count; i += _fanout)
            {
                int count = Math.Min(_fanout, level.Count - i);
                Span<byte> idx = node.AsSpan(0, IdxNodeSize + count * BranchSize);
                idx.Clear();
                Le.W16(idx, 24, count);
                Le.W16(idx, 26, height);
                for (int k = 0; k < count; k++)
                {
                    Span<byte> br = idx.Slice(IdxNodeSize + k * BranchSize, BranchSize);
                    (ulong key, int lnum, int offs, int len) = level[i + k];
                    Le.W32(br, 0, (uint)lnum);
                    Le.W32(br, 4, (uint)offs);
                    Le.W32(br, 8, (uint)len);
                    Le.W32(br, 12, (uint)(key >> 32));
                    Le.W32(br, 16, (uint)key);
                }
                (int nl, int no, int nlen) = AddIndexNode(idx);
                next.Add((level[i].Key, nl, no, nlen));
            }
            level = next;
            if (level.Count == 1) break;
        }
        _root = (level[0].Lnum, level[0].Offs, level[0].Len);
        _iheadLnum = _headLnum;
        _iheadOffs = Align(_headOffs, _minIo);
        FlushNodes();
    }

    // -------------------------------------------- LEB properties tree

    private static int Fls(long value)
    {
        int bits = 0;
        for (; value > 0; value >>= 1) bits++;
        return bits;
    }

    private void CalcLptGeometry(int mainLebs)
    {
        int pnodeCount = (mainLebs + LptFanout - 1) / LptFanout;
        int n = (pnodeCount + LptFanout - 1) / LptFanout;
        int nnodeCount = n;
        while (n > 1)
        {
            n = (n + LptFanout - 1) / LptFanout;
            nnodeCount += n;
        }
        _lptHeight = 1;
        for (long m = LptFanout; m < pnodeCount; m <<= LptFanoutShift) _lptHeight++;

        _spaceBits = Fls(_lebSize) - 3;
        _lptLnumBits = Fls(_lptLebs);
        _lptOffsBits = Fls(_lebSize - 1);
        _lptSpcBits = Fls(_lebSize);
        _pcntBits = Fls((_maxLebCount + LptFanout - 1) / LptFanout - 1);
        _lnumBits = Fls(_maxLebCount - 1);

        int head = LptCrcBits + LptTypeBits + (_bigLpt ? _pcntBits : 0);
        _pnodeSize = (head + (_spaceBits * 2 + 1) * LptFanout + 7) / 8;
        _nnodeSize = (head + (_lptLnumBits + _lptOffsBits) * LptFanout + 7) / 8;
        _ltabSize = (LptCrcBits + LptTypeBits + _lptLebs * _lptSpcBits * 2 + 7) / 8;
        _lsaveSize = (LptCrcBits + LptTypeBits + _lnumBits * LsaveCount + 7) / 8;

        _lptSize = (long)pnodeCount * _pnodeSize + (long)nnodeCount * _nnodeSize + _ltabSize + _lsaveSize;
        // Nodes do not cross erase blocks: the end of each one may be wasted.
        long size = _lptSize;
        int perLeb = Math.Max(_pnodeSize, _nnodeSize);
        long wasted = perLeb;
        size += perLeb;
        while (size > _lebSize)
        {
            size += perLeb;
            size -= _lebSize;
            wasted += perLeb;
        }
        wasted += Align((int)size, _minIo) - size;
        _lptSize += wasted;
    }

    /// <summary>Sizes the LPT area for the largest main area the filesystem may ever have.</summary>
    private void CalcDefaultLptGeometry(int maxMainLebs)
    {
        _lptLebs = MinLptLebs;
        _bigLpt = false;
        int mainLebs = maxMainLebs - _lptLebs;
        if (mainLebs <= 0) throw new FsNoSpaceException("The maximum number of UBIFS erase blocks is too low.");
        CalcLptGeometry(mainLebs);
        if (_lptSize > _lebSize)
        {
            _bigLpt = true;
            CalcLptGeometry(mainLebs);
        }
        for (int i = 0; i < 64; i++)
        {
            int needed = (int)((_lptSize * 4 + _lebSize - 1) / _lebSize);     // four times the size, so that it can be rewritten in place
            if (needed > _lptLebs)
            {
                _lptLebs = needed;
                mainLebs = maxMainLebs - _lptLebs;
                if (mainLebs <= 0) throw new FsNoSpaceException("The maximum number of UBIFS erase blocks is too low.");
                CalcLptGeometry(mainLebs);
                continue;
            }
            if (_ltabSize > _lebSize) break;
            return;
        }
        throw new FsException("The UBIFS geometry is not usable: the table of LEB properties does not fit into an erase block.");
    }

    /// <summary>Packs bit fields one after another, least significant bit first.</summary>
    private ref struct BitPacker(Span<byte> target)
    {
        private readonly Span<byte> _target = target;
        private int _bit;

        public void Put(long value, int bits)
        {
            for (int i = 0; i < bits; i++, _bit++)
                if ((value >> i & 1) != 0) _target[_bit >> 3] |= (byte)(1 << (_bit & 7));
        }
    }

    private static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (ushort)((crc & 1) != 0 ? crc >> 1 ^ 0xA001 : crc >> 1);
        }
        return crc;
    }

    private static void SealLptNode(Span<byte> node)
    {
        ushort crc = Crc16(node[2..]);
        node[0] = (byte)crc;
        node[1] = (byte)(crc >> 8);
    }

    private static int NnodeNumber(int row, int col)
    {
        int num = 1;
        while (row-- > 0)
        {
            int bits = col & (LptFanout - 1);
            col >>= LptFanoutShift;
            num = num << LptFanoutShift | bits;
        }
        return num;
    }

    /// <summary>
    /// Writes the tree that records the free and dirty space of every erase block of the main area:
    /// the leaves first, then each level above them, then the table of the LPT area itself.
    /// </summary>
    private void CreateLpt()
    {
        var ltab = new (int Free, int Dirty)[_lptLebs];
        Array.Fill(ltab, (_lebSize, 0));
        byte[] buf = _lebBuf;
        int lnum = _lptFirst, len = 0;

        void NextLebIfFull(int nodeSize)
        {
            if (len + nodeSize <= _lebSize) return;
            int aligned = Align(len, _minIo);
            ltab[lnum - _lptFirst] = (_lebSize - aligned, aligned - len);
            if (lnum > _lptLast) throw new FsException("The UBIFS LEB properties tree does not fit into its area.");
            WriteLeb(lnum++, len);
            len = 0;
        }

        int count = (_mainLebs + LptFanout - 1) >> LptFanoutShift;
        int belowLnum = lnum, belowOffs = 0, belowCount = count, belowSize = _pnodeSize;
        for (int i = 0; i < count; i++)
        {
            NextLebIfFull(_pnodeSize);
            Span<byte> node = buf.AsSpan(len, _pnodeSize);
            node.Clear();
            var bits = new BitPacker(node[2..]);
            bits.Put(LptPnode, LptTypeBits);
            if (_bigLpt) bits.Put(i, _pcntBits);
            for (int j = 0; j < LptFanout; j++)
            {
                int k = (i << LptFanoutShift) + j;
                (int free, int dirty, int flags) = k < _mainLebs && k < _lprops.Count ? _lprops[k] : (_lebSize, 0, 0);
                bits.Put(free >> 3, _spaceBits);
                bits.Put(dirty >> 3, _spaceBits);
                bits.Put((flags & LpropsIndex) != 0 ? 1 : 0, 1);
            }
            SealLptNode(node);
            len += _pnodeSize;
        }

        int row = _lptHeight - 1;
        while (true)
        {
            count = Math.Max(1, (count + LptFanout - 1) / LptFanout);
            for (int i = 0; i < count; i++)
            {
                NextLebIfFull(_nnodeSize);
                if (row == 0)
                {
                    _lptLnum = lnum;
                    _lptOffs = len;
                }
                Span<byte> node = buf.AsSpan(len, _nnodeSize);
                node.Clear();
                var bits = new BitPacker(node[2..]);
                bits.Put(LptNnode, LptTypeBits);
                if (_bigLpt) bits.Put(NnodeNumber(row, i), _pcntBits);
                for (int j = 0; j < LptFanout; j++)
                {
                    int childLnum = 0, childOffs = 0;
                    if (belowCount > 0)
                    {
                        if (belowOffs + belowSize > _lebSize)
                        {
                            belowLnum++;
                            belowOffs = 0;
                        }
                        (childLnum, childOffs) = (belowLnum, belowOffs);
                        belowOffs += belowSize;
                        belowCount--;
                    }
                    // A branch without a child points just past the LPT area.
                    bits.Put((childLnum == 0 ? _lptLast + 1 : childLnum) - _lptFirst, _lptLnumBits);
                    bits.Put(childOffs, _lptOffsBits);
                }
                SealLptNode(node);
                len += _nnodeSize;
            }
            if (row == 0) break;
            belowCount = count;
            belowSize = _nnodeSize;
            row--;
        }

        if (_bigLpt)
        {
            NextLebIfFull(_lsaveSize);
            _lsaveLnum = lnum;
            _lsaveOffs = len;
            Span<byte> node = buf.AsSpan(len, _lsaveSize);
            node.Clear();
            var bits = new BitPacker(node[2..]);
            bits.Put(LptLsave, LptTypeBits);
            for (int i = 0; i < LsaveCount; i++) bits.Put(_mainFirst + i, _lnumBits);
            SealLptNode(node);
            len += _lsaveSize;
        }

        NextLebIfFull(_ltabSize);
        if (lnum > _lptLast) throw new FsException("The UBIFS LEB properties tree does not fit into its area.");
        _ltabLnum = lnum;
        _ltabOffs = len;
        int tableAt = len;
        len += _ltabSize;
        int end = Align(len, _minIo);
        ltab[lnum - _lptFirst] = (_lebSize - end, end - len);
        Span<byte> table = buf.AsSpan(tableAt, _ltabSize);
        table.Clear();
        var packer = new BitPacker(table[2..]);
        packer.Put(LptLtab, LptTypeBits);
        foreach ((int free, int dirty) in ltab)
        {
            packer.Put(free, _lptSpcBits);
            packer.Put(dirty, _lptSpcBits);
        }
        SealLptNode(table);
        WriteLeb(lnum, len);
        _nheadLnum = lnum;
        _nheadOffs = end;
    }

    // ---------------------------------------- master, superblock, log

    private void WriteMaster()
    {
        Span<byte> m = stackalloc byte[MstNodeSize];
        m.Clear();
        Le.W64(m, 24, _highestInum);
        Le.W64(m, 32, 0);                                   // commit number
        Le.W32(m, 40, 2);                                   // no orphans
        Le.W32(m, 44, LogLnum);
        Le.W32(m, 48, (uint)_root.Lnum);
        Le.W32(m, 52, (uint)_root.Offs);
        Le.W32(m, 56, (uint)_root.Len);
        Le.W32(m, 60, (uint)_gcLnum);
        Le.W32(m, 64, (uint)_iheadLnum);
        Le.W32(m, 68, (uint)_iheadOffs);
        Le.W64(m, 72, (ulong)_indexSize);
        Le.W64(m, 80, (ulong)_totalFree);
        Le.W64(m, 88, (ulong)_totalDirty);
        Le.W64(m, 96, (ulong)_totalUsed);
        Le.W64(m, 104, (ulong)_totalDead);
        Le.W64(m, 112, (ulong)_totalDark);
        Le.W32(m, 120, (uint)_lptLnum);
        Le.W32(m, 124, (uint)_lptOffs);
        Le.W32(m, 128, (uint)_nheadLnum);
        Le.W32(m, 132, (uint)_nheadOffs);
        Le.W32(m, 136, (uint)_ltabLnum);
        Le.W32(m, 140, (uint)_ltabOffs);
        Le.W32(m, 144, (uint)_lsaveLnum);
        Le.W32(m, 148, (uint)_lsaveOffs);
        Le.W32(m, 152, (uint)_mainFirst);                   // where the next scan for free space starts
        Le.W32(m, 156, (uint)_emptyLebs);
        Le.W32(m, 160, (uint)_idxLebs);
        Le.W32(m, 164, (uint)_lebCount);
        // The two copies are the same node, as mkfs.ubifs writes them, apart from the sequence number.
        WriteNode(m, NodeMst, 1);
        WriteNode(m, NodeMst, 2);
    }

    private void WriteSuperblock()
    {
        byte[] sb = new byte[SbNodeSize];
        sb[26] = 0;                                         // key hash: R5
        sb[27] = 0;                                         // key format: simple
        int flags = (_bigLpt ? 0x02 : 0) | (_o.SpaceFixup ? 0x04 : 0);
        Le.W32(sb, 28, (uint)flags);
        Le.W32(sb, 32, (uint)_minIo);
        Le.W32(sb, 36, (uint)_lebSize);
        Le.W32(sb, 40, (uint)_lebCount);
        Le.W32(sb, 44, (uint)_maxLebCount);
        Le.W64(sb, 48, (ulong)_maxBudBytes);
        Le.W32(sb, 56, (uint)_logLebs);
        Le.W32(sb, 60, (uint)_lptLebs);
        Le.W32(sb, 64, (uint)_orphLebs);
        Le.W32(sb, 68, JheadCount);
        Le.W32(sb, 72, (uint)_fanout);
        Le.W32(sb, 76, LsaveCount);
        Le.W32(sb, 80, 4);                                  // format version
        Le.W16(sb, 84, (int)_o.Compression);
        Le.W32(sb, 104, 1000000000);                        // time granularity, nanoseconds
        (_o.Uuid ?? Guid.NewGuid()).TryWriteBytes(sb.AsSpan(108, 16), bigEndian: true, out _);
        WriteNode(sb, NodeSb, 0);
    }

    private void WriteLog()
    {
        Span<byte> cs = stackalloc byte[CsNodeSize];
        cs.Clear();
        WriteNode(cs, NodeCs, LogLnum);
        for (int i = 1; i < _logLebs; i++) WriteLeb(LogLnum + i, 0);
    }
}
