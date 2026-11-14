using System.IO.Compression;
using FsForge.Core.Compression;

namespace FsForge.Core.Ubifs;

public enum UbifsCompression
{
    None = 0,
    Lzo = 1,
    Zlib = 2,
    Zstd = 3,
}

/// <summary>Facts about a UBIFS image taken from its superblock and master node.</summary>
public sealed class UbifsInfo
{
    public int FormatVersion { get; init; }

    /// <summary>Size of a logical erase block.</summary>
    public int LebSize { get; init; }

    /// <summary>Minimal input/output unit of the flash the image was made for.</summary>
    public int MinIoSize { get; init; }

    /// <summary>Number of logical erase blocks the filesystem occupies.</summary>
    public int LebCount { get; init; }

    /// <summary>Number of logical erase blocks the filesystem may grow to.</summary>
    public int MaxLebCount { get; init; }

    public int LogLebs { get; init; }
    public int Fanout { get; init; }

    /// <summary>Compressor used for new data; every data block records its own.</summary>
    public UbifsCompression DefaultCompression { get; init; }

    /// <summary>Name hash of directory entries: "r5" or "test".</summary>
    public string KeyHash { get; init; } = "r5";

    public Guid Uuid { get; init; }
    public bool BigLpt { get; init; }

    /// <summary>The free space is fixed up on first mount (images written with "mkfs.ubifs -F").</summary>
    public bool SpaceFixup { get; init; }

    public bool Authenticated { get; init; }
    public ulong CommitNumber { get; init; }

    /// <summary>Bytes taken by nodes and free bytes, as recorded at the last commit.</summary>
    public long UsedBytes { get; init; }

    public long FreeBytes { get; init; }

    /// <summary>The filesystem was not unmounted cleanly (its master node is marked dirty).</summary>
    public bool Dirty { get; init; }

    /// <summary>Number of journal nodes applied on top of the last commit while reading; 0 for a clean image.</summary>
    public int ReplayedNodes { get; internal set; }

    /// <summary>Number of journal erase blocks (buds) the replayed nodes came from.</summary>
    public int ReplayedBuds { get; internal set; }

    public long SizeBytes => (long)LebCount * LebSize;
}

/// <summary>
/// Reader for UBIFS images: a raw image made by mkfs.ubifs, or the content of a UBI volume.
/// The index is read as of the last commit and the journal is replayed on top of it in memory,
/// which gives the state the kernel would mount.
/// </summary>
public sealed class UbifsReader : IDisposable
{
    private const uint NodeMagic = 0x06101831;
    private const int HeaderSize = 24;
    private const int SuperblockSize = 4096;
    private const int BlockSize = 4096;
    private const int BlockShift = 12;
    private const int InoNodeSize = 160, DentNodeSize = 56, DataNodeSize = 48, IdxNodeSize = 28, TrunNodeSize = 56;
    private const int MaxDataNodeSize = DataNodeSize + 2 * BlockSize;
    private const uint RootInode = 1;
    private const int MasterLeb = 1, LogLeb = 3;

    private const int NodeIno = 0, NodeData = 1, NodeDent = 2, NodeXent = 3, NodeTrun = 4, NodePad = 5,
        NodeSb = 6, NodeMst = 7, NodeRef = 8, NodeIdx = 9, NodeCs = 10;

    private const int KeyIno = 0, KeyData = 1, KeyDent = 2, KeyXent = 3;

    private const int FlagBigLpt = 0x02, FlagSpaceFixup = 0x04, FlagDoubleHash = 0x08, FlagEncryption = 0x10, FlagAuthentication = 0x20;
    private const int InodeXattr = 0x20, InodeCrypt = 0x40;

    private readonly IBlockSource _source;
    private readonly bool _ownsSource;
    private int _lebSize, _lebCount;
    private int _rootLnum, _rootOffs, _rootLen, _logLnum, _logLebs;
    private ulong _commitNumber;

    private readonly record struct Loc(int Lnum, int Offs, int Len);

    internal readonly record struct DataLoc(uint Block, int Lnum, int Offs, int Len);

    private sealed class Ino
    {
        public int Mode, Flags;
        public uint Uid, Gid, Links;
        public long MTime, Size;
        public byte[] Data = Array.Empty<byte>();
    }

    private readonly record struct Dent(uint Inum, byte Type);

    // The whole index, as collected by one pass over the tree and then changed by the journal.
    private readonly Dictionary<uint, Ino> _inodes = new();
    private readonly Dictionary<uint, List<DataLoc>> _blocks = new();
    private readonly Dictionary<uint, Dictionary<string, Dent>> _dents = new();
    private readonly Dictionary<uint, Dictionary<string, Dent>> _xents = new();

    private UbifsReader(IBlockSource source, bool ownsSource)
    {
        _source = source;
        _ownsSource = ownsSource;
        Info = null!;
    }

    public UbifsInfo Info { get; private set; }

    /// <summary>Non-fatal problems found while reading.</summary>
    public List<string> Warnings { get; } = new();

    // ------------------------------------------------------------ nodes

    /// <summary>UBIFS and UBI use CRC-32 without the final inversion.</summary>
    private static uint NodeCrc(ReadOnlySpan<byte> node) => ~Crc32.Compute(node[8..]);

    private static bool ValidNode(ReadOnlySpan<byte> node) =>
        node.Length >= HeaderSize && Le.U32(node, 0) == NodeMagic && Le.U32(node, 16) == (uint)node.Length && NodeCrc(node) == Le.U32(node, 4);

    /// <summary>Quick check: the data starts with a valid UBIFS superblock node.</summary>
    public static bool IsUbifs(IBlockSource source)
    {
        if (source.Length < SuperblockSize) return false;
        Span<byte> head = stackalloc byte[HeaderSize];
        source.ReadExactly(0, head);
        if (Le.U32(head, 0) != NodeMagic || head[20] != NodeSb || Le.U32(head, 16) != SuperblockSize) return false;
        return ValidNode(source.ReadBytes(0, SuperblockSize));
    }

    public static UbifsReader Open(IBlockSource source, bool ownsSource = true)
    {
        var reader = new UbifsReader(source, ownsSource);
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

    private static string At(int lnum, int offs) => $"erase block {lnum}, offset {offs}";

    /// <summary>Reads the node a branch of the index points to and checks its header, type and checksum.</summary>
    private byte[] ReadNode(int lnum, int offs, int len, int type)
    {
        if (lnum < 0 || lnum >= _lebCount || offs < 0 || len < HeaderSize || (long)offs + len > _lebSize)
            throw new FsException($"Corrupt UBIFS image: the index points outside the filesystem ({At(lnum, offs)}, {len} bytes).");
        long pos = (long)lnum * _lebSize + offs;
        if (pos + len > _source.Length)
            throw new FsException($"The UBIFS image is truncated: {At(lnum, offs)} is not in the file.");
        byte[] node = _source.ReadBytes(pos, len);
        if (Le.U32(node, 0) != NodeMagic)
            throw new FsException($"Corrupt UBIFS image: no node at {At(lnum, offs)}.");
        if (node[20] != type || Le.U32(node, 16) != (uint)len)
            throw new FsException($"Corrupt UBIFS image: unexpected node at {At(lnum, offs)}.");
        if (NodeCrc(node) != Le.U32(node, 4))
            throw new FsException($"Corrupt UBIFS image: bad checksum of the node at {At(lnum, offs)}.");
        return node;
    }

    private delegate void NodeVisitor(int offs, ReadOnlySpan<byte> node);

    /// <summary>
    /// Walks the nodes written one after another in an erase block, from <paramref name="offs"/> to the
    /// first empty or damaged place. Returns true when the walk ended at empty space or the end of the block.
    /// </summary>
    private bool ScanLeb(int lnum, int offs, byte[] buffer, NodeVisitor visit)
    {
        if (lnum < 0 || lnum >= _lebCount || offs < 0 || offs >= _lebSize) return true;
        long start = (long)lnum * _lebSize;
        Span<byte> leb = buffer.AsSpan(0, _lebSize);
        leb.Fill(0xFF);                                     // what is not in the file is erased flash
        if (start < _source.Length)
            _source.ReadExactly(start, leb[..(int)Math.Min(_lebSize, _source.Length - start)]);

        int pos = offs;
        while (pos + HeaderSize <= _lebSize)
        {
            uint magic = Le.U32(leb, pos);
            if (magic == 0xFFFFFFFF) return leb[pos..].IndexOfAnyExcept((byte)0xFF) < 0;
            if (leb[pos] == 0xCE)
            {
                // Padding bytes fill gaps that are too small for a padding node.
                while (pos < _lebSize && leb[pos] == 0xCE) pos++;
                pos = (pos + 7) & ~7;
                continue;
            }
            if (magic != NodeMagic) return false;
            long len = Le.U32(leb, pos + 16);
            if (len < HeaderSize || pos + len > _lebSize) return false;
            ReadOnlySpan<byte> node = leb.Slice(pos, (int)len);
            if (NodeCrc(node) != Le.U32(node, 4)) return false;
            if (node[20] == NodePad)
            {
                if (len < HeaderSize + 4) return false;
                len += Le.U32(node, HeaderSize);
                if (pos + len > _lebSize) return false;
            }
            else
            {
                visit(pos, node);
            }
            pos += (int)((len + 7) & ~7L);
        }
        return true;
    }

    // ------------------------------------------------------ superblock

    private void ReadSuperblock()
    {
        if (_source.Length < SuperblockSize) throw new FsException("The file is too small to be a UBIFS image.");
        byte[] sb = _source.ReadBytes(0, SuperblockSize);
        if (Le.U32(sb, 0) != NodeMagic || sb[20] != NodeSb) throw new FsException("Not a UBIFS image (no superblock node).");
        if (!ValidNode(sb)) throw new FsException("Corrupt UBIFS image: bad checksum of the superblock.");

        int flags = (int)Le.U32(sb, 28);
        int minIo = (int)Le.U32(sb, 32);
        _lebSize = (int)Le.U32(sb, 36);
        _lebCount = (int)Le.U32(sb, 40);
        _logLebs = (int)Le.U32(sb, 56);
        int version = (int)Le.U32(sb, 80);
        if (_lebSize < 15 * 1024 || _lebSize > 8 << 20 || (_lebSize & 7) != 0 || _lebCount < 8 || minIo < 8 || _logLebs < 1 || _logLebs >= _lebCount)
            throw new FsException("Corrupt UBIFS image: invalid geometry in the superblock.");
        if (sb[27] != 0) throw new FsException($"UBIFS key format {sb[27]} is not supported.");
        if ((flags & FlagEncryption) != 0)
            throw new FsException("The UBIFS image is encrypted (fscrypt). Names and contents of its files cannot be read without the keys, " +
                                  "so encrypted images are not supported.");
        if (version > 5)
            Warnings.Add($"The image uses UBIFS format version {version}, which is newer than this program knows (5). It may be read incorrectly.");
        if ((flags & FlagAuthentication) != 0)
            Warnings.Add("The UBIFS image is authenticated (signed). The signatures are not verified.");

        byte[] master = ReadMaster();
        _commitNumber = Le.U64(master, 32);
        _logLnum = (int)Le.U32(master, 44);
        _rootLnum = (int)Le.U32(master, 48);
        _rootOffs = (int)Le.U32(master, 52);
        _rootLen = (int)Le.U32(master, 56);

        Info = new UbifsInfo
        {
            FormatVersion = version,
            LebSize = _lebSize,
            MinIoSize = minIo,
            LebCount = _lebCount,
            MaxLebCount = (int)Le.U32(sb, 44),
            LogLebs = _logLebs,
            Fanout = (int)Le.U32(sb, 72),
            DefaultCompression = (UbifsCompression)Le.U16(sb, 84),
            KeyHash = sb[26] == 0 ? "r5" : "test",
            Uuid = new Guid(sb.AsSpan(108, 16), bigEndian: true),
            BigLpt = (flags & FlagBigLpt) != 0,
            SpaceFixup = (flags & FlagSpaceFixup) != 0,
            Authenticated = (flags & FlagAuthentication) != 0,
            CommitNumber = _commitNumber,
            FreeBytes = (long)Le.U64(master, 80),
            UsedBytes = (long)Le.U64(master, 96),
            Dirty = (Le.U32(master, 40) & 1) != 0,
        };
    }

    /// <summary>The master node is kept in two erase blocks and rewritten on every commit: the newest valid copy counts.</summary>
    private byte[] ReadMaster()
    {
        byte[]? best = null;
        ulong bestSqnum = 0;
        byte[] buffer = new byte[_lebSize];
        for (int lnum = MasterLeb; lnum <= MasterLeb + 1; lnum++)
        {
            ScanLeb(lnum, 0, buffer, (_, node) =>
            {
                if (node[20] != NodeMst || node.Length < 128) return;
                ulong sqnum = Le.U64(node, 8);
                if (best is not null && sqnum < bestSqnum) return;
                best = node.ToArray();
                bestSqnum = sqnum;
            });
        }
        return best ?? throw new FsException("Corrupt UBIFS image: no valid master node was found.");
    }

    // ----------------------------------------------------------- index

    private long _progressDone;
    private int _damaged;
    private const int MaxDamageWarnings = 20;

    /// <summary>
    /// Notes a node that cannot be read. A dump of a worn flash may have a few of them; the rest of the
    /// filesystem is still worth showing, so the damage costs the objects it describes and nothing more.
    /// </summary>
    private void Damaged(string message)
    {
        if (++_damaged <= MaxDamageWarnings) Warnings.Add(message);
    }

    private void WalkIndex(int lnum, int offs, int len, int expectedLevel, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        byte[] node = ReadNode(lnum, offs, len, NodeIdx);
        int count = Le.U16(node, 24), level = Le.U16(node, 26);
        if (count == 0 || len < IdxNodeSize + count * 20)
            throw new FsException($"Corrupt UBIFS image: invalid index node at {At(lnum, offs)}.");
        if (expectedLevel < 0 ? level > 64 : level != expectedLevel)
            throw new FsException($"Corrupt UBIFS image: the index node at {At(lnum, offs)} is at the wrong level of the tree.");
        // A branch is three numbers and a key; an authenticated image appends a hash, whose size shows in the node length.
        int branch = (len - IdxNodeSize) / count;
        _progressDone += len;
        for (int i = 0; i < count; i++)
        {
            int b = IdxNodeSize + i * branch;
            int childLnum = (int)Le.U32(node, b), childOffs = (int)Le.U32(node, b + 4), childLen = (int)Le.U32(node, b + 8);
            try
            {
                if (level > 0)
                {
                    WalkIndex(childLnum, childOffs, childLen, level - 1, progress, ct);
                    continue;
                }
                uint inum = Le.U32(node, b + 12), second = Le.U32(node, b + 16);
                Leaf(inum, (int)(second >> 29), second & 0x1FFFFFFF, new Loc(childLnum, childOffs, childLen));
                _progressDone += childLen;
            }
            catch (FsException ex)
            {
                Damaged(level > 0
                    ? $"A part of the index is damaged and was skipped; files may be missing. {ex.Message}"
                    : $"A damaged node was skipped; the file or name it describes is missing. {ex.Message}");
            }
        }
        if (level == 1) progress?.Report(new ProgressInfo("read", Math.Min(_progressDone, Info.UsedBytes), Info.UsedBytes));
    }

    private void Leaf(uint inum, int keyType, uint keyValue, Loc loc)
    {
        switch (keyType)
        {
            case KeyIno:
                _inodes[inum] = ParseInode(ReadNode(loc.Lnum, loc.Offs, loc.Len, NodeIno), loc);
                break;
            case KeyData:
                if (!_blocks.TryGetValue(inum, out List<DataLoc>? list)) _blocks[inum] = list = new List<DataLoc>();
                // Keys come in ascending order, so the list stays sorted by block number.
                if (list.Count > 0 && list[^1].Block >= keyValue)
                    throw new FsException($"Corrupt UBIFS image: the index is not sorted at {At(loc.Lnum, loc.Offs)}.");
                list.Add(new DataLoc(keyValue, loc.Lnum, loc.Offs, loc.Len));
                break;
            case KeyDent or KeyXent:
                (string name, Dent dent) = ParseDent(ReadNode(loc.Lnum, loc.Offs, loc.Len, keyType == KeyDent ? NodeDent : NodeXent), loc);
                SetEntry(keyType == KeyDent ? _dents : _xents, inum, name, dent);
                break;
        }
    }

    private static void SetEntry(Dictionary<uint, Dictionary<string, Dent>> map, uint parent, string name, Dent dent)
    {
        if (!map.TryGetValue(parent, out Dictionary<string, Dent>? entries)) map[parent] = entries = new Dictionary<string, Dent>(StringComparer.Ordinal);
        entries[name] = dent;
    }

    private static Ino ParseInode(ReadOnlySpan<byte> node, Loc loc)
    {
        if (node.Length < InoNodeSize) throw new FsException($"Corrupt UBIFS image: the inode at {At(loc.Lnum, loc.Offs)} is too short.");
        int dataLength = (int)Math.Min(Le.U32(node, 112), (uint)(node.Length - InoNodeSize));
        return new Ino
        {
            Size = (long)Le.U64(node, 48),
            MTime = (long)Le.U64(node, 72),
            Links = Le.U32(node, 92),
            Uid = Le.U32(node, 96),
            Gid = Le.U32(node, 100),
            Mode = (int)Le.U32(node, 104),
            Flags = (int)Le.U32(node, 108),
            Data = dataLength > 0 ? node.Slice(InoNodeSize, dataLength).ToArray() : Array.Empty<byte>(),
        };
    }

    private static (string Name, Dent Dent) ParseDent(ReadOnlySpan<byte> node, Loc loc)
    {
        int nameLength = node.Length >= DentNodeSize ? Le.U16(node, 50) : int.MaxValue;
        if (nameLength > node.Length - DentNodeSize)
            throw new FsException($"Corrupt UBIFS image: the directory entry at {At(loc.Lnum, loc.Offs)} is too short.");
        return (PosixName.Decode(node.Slice(DentNodeSize, nameLength)), new Dent((uint)Le.U64(node, 40), node[49]));
    }

    // --------------------------------------------------------- journal

    private enum JournalKind { Inode, Data, Entry, XattrEntry, Truncate }

    private sealed class JournalNode
    {
        public ulong Sqnum;
        public JournalKind Kind;
        public uint Inum;
        public Ino? Inode;              // null: the inode was deleted
        public DataLoc Data;
        public long DataEnd;
        public string Name = "";
        public Dent Dent;               // Inum 0: the entry was deleted
        public long OldSize, NewSize;
    }

    /// <summary>
    /// Applies what was written after the last commit. The log names the erase blocks ("buds") that
    /// hold such nodes; they are applied to the index in the order they were written.
    /// </summary>
    private void ReplayJournal(CancellationToken ct)
    {
        if (_logLnum < LogLeb || _logLnum >= LogLeb + _logLebs)
        {
            Warnings.Add("The master node of the UBIFS image points to a log outside the log area; the journal was not replayed.");
            return;
        }
        byte[] buffer = new byte[_lebSize];
        var buds = new List<(int Lnum, int Offs)>();
        ulong commitStart = 0;
        bool bad = false;
        int logLeb = _logLnum;
        for (int i = 0; i < _logLebs && !bad; i++)
        {
            bool first = true, stop = false;
            int leb = logLeb;
            ScanLeb(leb, 0, buffer, (offs, node) =>
            {
                if (stop) return;
                ulong sqnum = Le.U64(node, 8);
                if (first)
                {
                    first = false;
                    if (i == 0)
                    {
                        // The log of a commit starts with its "commit start" node.
                        if (node[20] != NodeCs || node.Length < 32 || Le.U64(node, 24) != _commitNumber) { bad = stop = true; return; }
                        commitStart = sqnum;
                        return;
                    }
                    if (sqnum < commitStart) { stop = true; return; }        // an erase block of an older log
                }
                if (node[20] == NodeRef && node.Length >= 36)
                {
                    var bud = ((int)Le.U32(node, 24), (int)Le.U32(node, 28));
                    if (!buds.Contains(bud)) buds.Add(bud);
                }
            });
            if (first || stop) break;                       // an empty erase block ends the log
            logLeb = logLeb + 1 >= LogLeb + _logLebs ? LogLeb : logLeb + 1;
        }
        if (bad || commitStart == 0)
        {
            Warnings.Add("The log of the UBIFS image does not start with the record of its last commit; the journal was not replayed. " +
                         "Changes made shortly before the image was taken may be missing.");
            return;
        }
        if (buds.Count == 0) return;

        var nodes = new List<JournalNode>();
        int damaged = 0;
        foreach ((int lnum, int offs) in buds)
        {
            ct.ThrowIfCancellationRequested();
            if (lnum < LogLeb + _logLebs || lnum >= _lebCount) { damaged++; continue; }
            bool clean = ScanLeb(lnum, offs, buffer, (at, node) =>
            {
                if (ParseJournalNode(node, new Loc(lnum, at, node.Length)) is { } j) nodes.Add(j);
            });
            if (!clean) damaged++;
        }
        // A bud that ends in garbage is what a power cut leaves behind; the kernel drops the unfinished write the same way.
        if (damaged > 0)
            Warnings.Add($"{damaged} journal erase block(s) of the UBIFS image end in damaged data (an interrupted write); the nodes before the damage were applied.");

        var sizes = new Dictionary<uint, long>();
        foreach (JournalNode j in nodes.OrderBy(n => n.Sqnum))
        {
            switch (j.Kind)
            {
                case JournalKind.Inode when j.Inode is null:
                    _inodes.Remove(j.Inum);
                    _blocks.Remove(j.Inum);
                    _dents.Remove(j.Inum);
                    _xents.Remove(j.Inum);
                    sizes.Remove(j.Inum);
                    break;
                case JournalKind.Inode:
                    _inodes[j.Inum] = j.Inode;
                    break;
                case JournalKind.Data:
                    SetBlock(j.Inum, j.Data);
                    sizes[j.Inum] = Math.Max(sizes.GetValueOrDefault(j.Inum), j.DataEnd);
                    break;
                case JournalKind.Entry or JournalKind.XattrEntry:
                    Dictionary<uint, Dictionary<string, Dent>> map = j.Kind == JournalKind.Entry ? _dents : _xents;
                    if (j.Dent.Inum != 0) SetEntry(map, j.Inum, j.Name, j.Dent);
                    else if (map.TryGetValue(j.Inum, out Dictionary<string, Dent>? entries)) entries.Remove(j.Name);
                    break;
                case JournalKind.Truncate:
                    Truncate(j.Inum, j.OldSize, j.NewSize);
                    if (sizes.ContainsKey(j.Inum)) sizes[j.Inum] = j.NewSize;
                    break;
            }
        }
        // Data written past the recorded end of a file, whose inode did not make it to the flash any more.
        foreach ((uint inum, long end) in sizes)
            if (_inodes.TryGetValue(inum, out Ino? ino) && (ino.Mode & UnixMode.S_IFMT) == UnixMode.S_IFREG && ino.Size < end) ino.Size = end;

        Info.ReplayedNodes = nodes.Count;
        Info.ReplayedBuds = buds.Count;
    }

    private static JournalNode? ParseJournalNode(ReadOnlySpan<byte> node, Loc loc)
    {
        ulong sqnum = Le.U64(node, 8);
        switch (node[20])
        {
            case NodeIno when node.Length >= InoNodeSize:
                Ino ino = ParseInode(node, loc);
                return new JournalNode { Sqnum = sqnum, Kind = JournalKind.Inode, Inum = Le.U32(node, 24), Inode = ino.Links == 0 ? null : ino };
            case NodeData when node.Length >= DataNodeSize:
                uint block = Le.U32(node, 28) & 0x1FFFFFFF;
                return new JournalNode
                {
                    Sqnum = sqnum, Kind = JournalKind.Data, Inum = Le.U32(node, 24),
                    Data = new DataLoc(block, loc.Lnum, loc.Offs, loc.Len),
                    DataEnd = ((long)block << BlockShift) + Le.U32(node, 40),
                };
            case NodeDent or NodeXent when node.Length >= DentNodeSize && Le.U16(node, 50) <= node.Length - DentNodeSize:
                (string name, Dent dent) = ParseDent(node, loc);
                return new JournalNode
                {
                    Sqnum = sqnum, Kind = node[20] == NodeDent ? JournalKind.Entry : JournalKind.XattrEntry, Inum = Le.U32(node, 24), Name = name, Dent = dent,
                };
            case NodeTrun when node.Length >= TrunNodeSize:
                return new JournalNode
                {
                    Sqnum = sqnum, Kind = JournalKind.Truncate, Inum = Le.U32(node, 24), OldSize = (long)Le.U64(node, 40), NewSize = (long)Le.U64(node, 48),
                };
            default:
                return null;
        }
    }

    private static int FindBlock(List<DataLoc> list, uint block)
    {
        int lo = 0, hi = list.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (list[mid].Block == block) return mid;
            if (list[mid].Block < block) lo = mid + 1;
            else hi = mid - 1;
        }
        return ~lo;
    }

    private void SetBlock(uint inum, DataLoc data)
    {
        if (!_blocks.TryGetValue(inum, out List<DataLoc>? list)) _blocks[inum] = list = new List<DataLoc>();
        int i = FindBlock(list, data.Block);
        if (i >= 0) list[i] = data;
        else list.Insert(~i, data);
    }

    /// <summary>Drops the blocks that a truncation from <paramref name="oldSize"/> to <paramref name="newSize"/> cut off entirely.</summary>
    private void Truncate(uint inum, long oldSize, long newSize)
    {
        if (!_blocks.TryGetValue(inum, out List<DataLoc>? list) || oldSize < 0 || newSize < 0 || newSize >= oldSize) return;
        long min = (newSize >> BlockShift) + ((newSize & (BlockSize - 1)) != 0 ? 1 : 0);
        long max = (oldSize >> BlockShift) - ((oldSize & (BlockSize - 1)) == 0 ? 1 : 0);
        list.RemoveAll(d => d.Block >= min && d.Block <= max);
    }

    // ------------------------------------------------------------ tree

    /// <summary>Reads the index, replays the journal and builds the directory tree. File contents are read on demand.</summary>
    public FsTree ReadTree(IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        _inodes.Clear();
        _blocks.Clear();
        _dents.Clear();
        _xents.Clear();
        _progressDone = 0;
        _damaged = 0;
        WalkIndex(_rootLnum, _rootOffs, _rootLen, -1, progress, ct);
        ReplayJournal(ct);

        if (!_inodes.TryGetValue(RootInode, out Ino? rootIno) || (rootIno.Mode & UnixMode.S_IFMT) != UnixMode.S_IFDIR)
            throw new FsException("Corrupt UBIFS image: the root directory is missing.");

        var tree = new FsTree();
        FsInode root = tree.Root.Inode;
        root.Mode = rootIno.Mode & 0xFFF;
        root.Uid = rootIno.Uid;
        root.Gid = rootIno.Gid;
        root.MTime = rootIno.MTime;
        root.XAttrs = ReadXattrs(RootInode, "/");

        var hardLinks = new Dictionary<uint, FsInode>();
        var seenDirs = new HashSet<uint> { RootInode };
        var stack = new Stack<(FsNode Dir, uint Inum)>();
        stack.Push((tree.Root, RootInode));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            (FsNode dir, uint dirInum) = stack.Pop();
            if (!_dents.TryGetValue(dirInum, out Dictionary<string, Dent>? entries)) continue;
            foreach ((string name, Dent dent) in entries)
            {
                string path = dir.IsRoot ? "/" + name : dir.Path + "/" + name;
                if (name.Length == 0 || name is "." or ".." || name.Contains('/') || name.Contains('\0'))
                {
                    Warnings.Add($"Invalid directory entry '{name}' in '{dir.Path}' was skipped.");
                    continue;
                }
                if (hardLinks.TryGetValue(dent.Inum, out FsInode? known))
                {
                    tree.Add(dir, name, known);
                    continue;
                }
                if (!_inodes.TryGetValue(dent.Inum, out Ino? ino))
                {
                    Warnings.Add($"'{path}' points to a missing inode and was skipped.");
                    continue;
                }
                FsInode? inode = BuildInode(dent.Inum, ino, path);
                if (inode is null) continue;
                if (inode.Type == FsNodeType.Directory)
                {
                    if (!seenDirs.Add(dent.Inum))
                    {
                        Warnings.Add($"Directory '{path}' is linked more than once and was skipped.");
                        continue;
                    }
                }
                else
                {
                    hardLinks.Add(dent.Inum, inode);
                }
                FsNode node = tree.Add(dir, name, inode);
                if (inode.Type == FsNodeType.Directory) stack.Push((node, dent.Inum));
            }
        }
        progress?.Report(new ProgressInfo("read", 1, 1));
        if (_damaged > MaxDamageWarnings)
            Warnings.Add($"... and {_damaged - MaxDamageWarnings} more damaged node(s) were skipped.");

        // The tree keeps what it needs; the rest of the index can go.
        _inodes.Clear();
        _blocks.Clear();
        _dents.Clear();
        _xents.Clear();
        return tree;
    }

    private FsInode? BuildInode(uint inum, Ino ino, string path)
    {
        if (UnixMode.TypeFromMode(ino.Mode) is not { } type || (ino.Flags & InodeXattr) != 0)
        {
            Warnings.Add($"'{path}' has an unknown file type and was skipped.");
            return null;
        }
        if ((ino.Flags & InodeCrypt) != 0)
        {
            Warnings.Add($"'{path}' is encrypted and was skipped.");
            return null;
        }
        var inode = new FsInode { Type = type, Mode = ino.Mode & 0xFFF, Uid = ino.Uid, Gid = ino.Gid, MTime = ino.MTime };
        switch (type)
        {
            case FsNodeType.File:
                if (ino.Size < 0)
                {
                    Warnings.Add($"'{path}' has an invalid size and was skipped.");
                    return null;
                }
                if (ino.Size > 0)
                {
                    DataLoc[] blocks = _blocks.TryGetValue(inum, out List<DataLoc>? list) ? list.ToArray() : Array.Empty<DataLoc>();
                    inode.Data = new UbifsFileData(this, inum, ino.Size, blocks);
                }
                break;
            case FsNodeType.Symlink:
                if (ino.Data.Length == 0)
                {
                    Warnings.Add($"The symbolic link '{path}' has no target and was skipped.");
                    return null;
                }
                inode.LinkTarget = PosixName.Decode(ino.Data);
                break;
            case FsNodeType.CharDevice or FsNodeType.BlockDevice:
                // The device number is stored the way the kernel encodes it: 32 bits, or 64 for large numbers.
                ulong dev = ino.Data.Length >= 8 ? Le.U64(ino.Data, 0) : ino.Data.Length >= 4 ? Le.U32(ino.Data, 0) : 0;
                inode.DevMajor = (uint)((dev & 0xFFF00) >> 8);
                inode.DevMinor = (uint)((dev & 0xFF) | ((dev >> 12) & 0xFFF00));
                break;
        }
        inode.XAttrs = ReadXattrs(inum, path);
        return inode;
    }

    /// <summary>Extended attributes are small hidden inodes: an entry gives the name, the inode holds the value.</summary>
    private SortedDictionary<string, byte[]>? ReadXattrs(uint inum, string path)
    {
        if (!_xents.TryGetValue(inum, out Dictionary<string, Dent>? entries) || entries.Count == 0) return null;
        var result = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach ((string name, Dent dent) in entries)
        {
            if (_inodes.TryGetValue(dent.Inum, out Ino? value) && (value.Flags & InodeCrypt) == 0) result[name] = value.Data;
            else Warnings.Add($"The extended attribute '{name}' of '{path}' cannot be read and was skipped.");
        }
        return result.Count > 0 ? result : null;
    }

    // ------------------------------------------------------------ data

    /// <summary>Content of a regular file: 4 KiB blocks, each in a data node of its own; blocks without a node are holes.</summary>
    private sealed class UbifsFileData(UbifsReader reader, uint inum, long length, DataLoc[] blocks) : ISparseFileData
    {
        public long Length => length;
        public long DataBytesHint => Math.Min(length, (long)blocks.Length * BlockSize);
        public Stream OpenRead() => new FileStream(reader, inum, length, blocks);
    }

    private sealed class FileStream(UbifsReader reader, uint inum, long length, DataLoc[] blocks) : Stream
    {
        private readonly byte[] _block = new byte[BlockSize];
        private long _position;
        private long _loaded = -1;      // number of the block held in _block
        private int _next;              // index of the first entry of "blocks" that may hold the block wanted
        private ZstdCodec? _zstd;
        private LzoCodec? _lzo;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int total = 0;
            while (buffer.Length > 0 && _position < length)
            {
                long number = _position >> BlockShift;
                int inBlock = (int)(_position & (BlockSize - 1));
                if (number != _loaded) Load(number);
                int n = (int)Math.Min(Math.Min(buffer.Length, BlockSize - inBlock), length - _position);
                _block.AsSpan(inBlock, n).CopyTo(buffer);
                buffer = buffer[n..];
                _position += n;
                total += n;
            }
            return total;
        }

        private void Load(long number)
        {
            while (_next < blocks.Length && blocks[_next].Block < number) _next++;
            _loaded = number;
            Span<byte> block = _block;
            block.Clear();
            if (_next >= blocks.Length || blocks[_next].Block != number) return;      // a hole

            DataLoc loc = blocks[_next];
            if (loc.Len is < DataNodeSize or > MaxDataNodeSize)
                throw new FsException($"Corrupt UBIFS image: invalid data node at {At(loc.Lnum, loc.Offs)}.");
            byte[] node = reader.ReadNode(loc.Lnum, loc.Offs, loc.Len, NodeData);
            int size = (int)Math.Min(Le.U32(node, 40), int.MaxValue);
            if (size > BlockSize || Le.U32(node, 24) != inum)
                throw new FsException($"Corrupt UBIFS image: the data node at {At(loc.Lnum, loc.Offs)} does not belong to the file.");
            ReadOnlySpan<byte> payload = node.AsSpan(DataNodeSize);
            Span<byte> target = block[..size];
            int produced;
            switch ((UbifsCompression)Le.U16(node, 44))
            {
                case UbifsCompression.None:
                    if (payload.Length != size) throw Corrupt(loc);
                    payload.CopyTo(target);
                    return;
                case UbifsCompression.Lzo:
                    produced = (_lzo ??= new LzoCodec()).Decompress(payload, target);
                    break;
                case UbifsCompression.Zlib:
                    produced = Inflate(node, DataNodeSize, payload.Length, target);
                    break;
                case UbifsCompression.Zstd:
                    produced = (_zstd ??= new ZstdCodec()).Decompress(payload, target);
                    break;
                default:
                    throw new FsException($"The data node at {At(loc.Lnum, loc.Offs)} uses an unknown compressor ({Le.U16(node, 44)}).");
            }
            if (produced != size) throw Corrupt(loc);
        }

        private static FsException Corrupt(DataLoc loc) =>
            new($"Corrupt UBIFS image: the data node at {At(loc.Lnum, loc.Offs)} does not unpack to its recorded size.");

        /// <summary>UBIFS stores deflate data bare, without the zlib header and checksum.</summary>
        private static int Inflate(byte[] node, int offset, int count, Span<byte> target)
        {
            try
            {
                using var input = new MemoryStream(node, offset, count, writable: false);
                using var inflate = new DeflateStream(input, CompressionMode.Decompress);
                int total = 0;
                while (total < target.Length)
                {
                    int n = inflate.Read(target[total..]);
                    if (n <= 0) break;
                    total += n;
                }
                return total;
            }
            catch (InvalidDataException ex)
            {
                throw new FsException("Corrupt zlib data in a UBIFS data node.", ex);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _zstd?.Dispose();
            base.Dispose(disposing);
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
