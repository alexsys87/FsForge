using System.Buffers.Binary;
using System.IO.Compression;
using FsForge.Core;
using FsForge.Core.Compression;
using FsForge.Core.Ubifs;

namespace FsForge.Cli;

/// <summary>
/// Test-only writer of UBIFS and UBI images. FsForge itself reads these formats but does not write
/// them, so the built-in tests make their own images here: a small counterpart of mkfs.ubifs and
/// ubinize that can also leave nodes in the journal, the way an unclean unmount does.
/// It writes what the reader needs (superblock, master nodes, log, index and leaf nodes), not
/// everything the kernel does - there is no LEB properties tree.
/// </summary>
internal sealed class UbifsImageBuilder
{
    public const int LebSize = 126976, MinIo = 2048;

    private const uint Magic = 0x06101831;
    private const int LogLeb = 3, LogLebs = 4, MainFirst = LogLeb + LogLebs + 2 /* LPT */ + 1 /* orphans */;
    private const int TypeIno = 0, TypeData = 1, TypeDent = 2, TypeXent = 3, TypeTrun = 4, TypePad = 5, TypeSb = 6, TypeMst = 7, TypeRef = 8, TypeIdx = 9, TypeCs = 10;

    private readonly List<byte[]> _lebs = new();
    private readonly List<(ulong Key, int Lnum, int Offs, int Len)> _leaves = new();
    private readonly Dictionary<FsInode, uint> _inums = new(ReferenceEqualityComparer.Instance);
    private readonly UbifsCompression _compression;
    private readonly int _fanout;
    private int _lnum = MainFirst, _offs;
    private ulong _sqnum;
    private uint _nextInum = 65;
    private (int Lnum, int Offs, int Len) _root;
    private int _logOffs, _budLnum = -1, _budOffs;
    private bool _dirty;

    /// <summary>Commit number written to the log; changing it makes the log look like one of another commit.</summary>
    public ulong LogCommitNumber { get; set; } = 7;

    private UbifsImageBuilder(UbifsCompression compression, int fanout)
    {
        _compression = compression;
        _fanout = fanout;
    }

    /// <summary>Lays a tree out as a committed filesystem with an empty journal.</summary>
    public static UbifsImageBuilder FromTree(FsTree tree, UbifsCompression compression = UbifsCompression.Zlib, int fanout = 8)
    {
        var b = new UbifsImageBuilder(compression, fanout);
        b._inums[tree.Root.Inode] = 1;
        b.WriteInode(tree.Root);
        foreach (FsNode node in tree.Walk().Where(n => !n.IsRoot))
        {
            if (!b._inums.ContainsKey(node.Inode))
            {
                b._inums[node.Inode] = b._nextInum++;
                b.WriteInode(node);
            }
            b.Leaf(b.Entry(TypeDent, b._inums[node.Parent!.Inode], node.Name, b._inums[node.Inode], DentType(node.Type)));
        }
        b.WriteIndex();
        // The journal starts in a block of its own, as it does on a real flash.
        b._lnum++;
        b._offs = 0;
        return b;
    }

    public uint InumOf(FsNode node) => _inums[node.Inode];

    /// <summary>Position of the inode node of a tree node in the image, for tests that damage it.</summary>
    public long InodeOffset(FsNode node)
    {
        var leaf = _leaves.First(l => l.Key == (ulong)InumOf(node) << 32);
        return (long)leaf.Lnum * LebSize + leaf.Offs;
    }

    public uint NewInum() => _nextInum++;

    // ------------------------------------------------------------ nodes

    private static uint Crc(ReadOnlySpan<byte> data) => ~Crc32.Compute(data);

    private byte[] Node(int type, int length, ulong? sqnum = null)
    {
        byte[] n = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(n, Magic);
        BinaryPrimitives.WriteUInt64LittleEndian(n.AsSpan(8), sqnum ?? ++_sqnum);
        BinaryPrimitives.WriteUInt32LittleEndian(n.AsSpan(16), (uint)length);
        n[20] = (byte)type;
        return n;
    }

    private static byte[] Seal(byte[] node)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(4), Crc(node.AsSpan(8)));
        return node;
    }

    private static void Key(byte[] node, uint inum, int type, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(24), inum);
        BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(28), ((uint)type << 29) | value);
    }

    private byte[] Leb(int lnum)
    {
        while (_lebs.Count <= lnum)
        {
            byte[] leb = new byte[LebSize];
            leb.AsSpan().Fill(0xFF);
            _lebs.Add(leb);
        }
        return _lebs[lnum];
    }

    /// <summary>Writes a node at the head of the main area; nodes never cross an erase block.</summary>
    private (int Lnum, int Offs, int Len) Place(byte[] node)
    {
        if (_offs + node.Length > LebSize)
        {
            _lnum++;
            _offs = 0;
        }
        node.CopyTo(Leb(_lnum), _offs);
        var loc = (_lnum, _offs, node.Length);
        _offs += (node.Length + 7) & ~7;
        return loc;
    }

    private void Leaf(byte[] node)
    {
        (int lnum, int offs, int len) = Place(node);
        _leaves.Add((((ulong)BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(24)) << 32) | BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(28)), lnum, offs, len));
    }

    /// <summary>The R5 name hash of UBIFS directory entry keys.</summary>
    private static uint R5(byte[] name)
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
        return a <= 2 ? a + 3 : a;
    }

    private static int DentType(FsNodeType t) => t switch
    {
        FsNodeType.File => 0, FsNodeType.Directory => 1, FsNodeType.Symlink => 2, FsNodeType.BlockDevice => 3,
        FsNodeType.CharDevice => 4, FsNodeType.Fifo => 5, _ => 6,
    };

    private byte[] Inode(uint inum, int mode, long size, uint links, byte[]? data = null, uint uid = 0, uint gid = 0, long mtime = 0,
        int flags = 0, int xattrCount = 0)
    {
        data ??= Array.Empty<byte>();
        byte[] n = Node(TypeIno, 160 + data.Length);
        Key(n, inum, 0, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(n.AsSpan(48), (ulong)size);
        foreach (int at in new[] { 56, 64, 72 }) BinaryPrimitives.WriteInt64LittleEndian(n.AsSpan(at), mtime);
        BinaryPrimitives.WriteUInt32LittleEndian(n.AsSpan(92), links);
        BinaryPrimitives.WriteUInt32LittleEndian(n.AsSpan(96), uid);
        BinaryPrimitives.WriteUInt32LittleEndian(n.AsSpan(100), gid);
        BinaryPrimitives.WriteUInt32LittleEndian(n.AsSpan(104), (uint)mode);
        BinaryPrimitives.WriteUInt32LittleEndian(n.AsSpan(108), (uint)flags);
        BinaryPrimitives.WriteUInt32LittleEndian(n.AsSpan(112), (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(n.AsSpan(116), (uint)xattrCount);
        BinaryPrimitives.WriteUInt16LittleEndian(n.AsSpan(132), (ushort)_compression);
        data.CopyTo(n, 160);
        return Seal(n);
    }

    private byte[] Entry(int type, uint parent, string name, uint inum, int dentType)
    {
        byte[] raw = PosixName.Encode(name);
        byte[] n = Node(type, 56 + raw.Length + 1);
        Key(n, parent, type == TypeDent ? 2 : 3, R5(raw));
        BinaryPrimitives.WriteUInt64LittleEndian(n.AsSpan(40), inum);
        n[49] = (byte)dentType;
        BinaryPrimitives.WriteUInt16LittleEndian(n.AsSpan(50), (ushort)raw.Length);
        raw.CopyTo(n, 56);
        return Seal(n);
    }

    private byte[] Data(uint inum, uint block, ReadOnlySpan<byte> content)
    {
        byte[] payload = content.ToArray();
        UbifsCompression used = UbifsCompression.None;
        if (_compression == UbifsCompression.Zlib)
        {
            using var ms = new MemoryStream();
            using (var z = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(content);
            if (ms.Length < content.Length) (payload, used) = (ms.ToArray(), UbifsCompression.Zlib);
        }
        else if (_compression == UbifsCompression.Zstd)
        {
            using var zstd = new ZstdSharp.Compressor(3);
            byte[] packed = zstd.Wrap(content).ToArray();
            if (packed.Length < content.Length) (payload, used) = (packed, UbifsCompression.Zstd);
        }
        byte[] n = Node(TypeData, 48 + payload.Length);
        Key(n, inum, 1, block);
        BinaryPrimitives.WriteUInt32LittleEndian(n.AsSpan(40), (uint)content.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(n.AsSpan(44), (ushort)used);
        payload.CopyTo(n, 48);
        return Seal(n);
    }

    private void WriteInode(FsNode node)
    {
        FsInode i = node.Inode;
        uint inum = _inums[i];
        byte[]? data = null;
        long size = 160;
        uint links = (uint)Math.Max(1, i.LinkCount);
        switch (i.Type)
        {
            case FsNodeType.Directory:
                links = 2 + (uint)node.Children.Count(c => c.IsDirectory);
                break;
            case FsNodeType.Symlink:
                data = PosixName.Encode(i.LinkTarget ?? "");
                size = data.Length;
                break;
            case FsNodeType.CharDevice or FsNodeType.BlockDevice:
                data = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(data, (i.DevMinor & 0xFF) | (i.DevMajor << 8) | ((i.DevMinor & ~0xFFu) << 12));
                size = 0;
                break;
            case FsNodeType.File:
                size = i.Size;
                break;
            default:
                size = 0;
                break;
        }
        Leaf(Inode(inum, UnixMode.TypeBits(i.Type) | (i.Mode & 0xFFF), size, links, data, i.Uid, i.Gid, i.MTime, 0, i.XAttrs?.Count ?? 0));

        if (i.Type == FsNodeType.File && i.Data is not null)
        {
            using Stream s = i.Data.OpenRead();
            byte[] block = new byte[4096];
            for (uint number = 0; ; number++)
            {
                int n = s.ReadAtLeast(block, block.Length, throwOnEndOfStream: false);
                if (n == 0) break;
                // Blocks of zeros are left out: they are the holes of a sparse file.
                if (block.AsSpan(0, n).IndexOfAnyExcept((byte)0) >= 0) Leaf(Data(inum, number, block.AsSpan(0, n)));
            }
        }
        if (i.XAttrs is null) return;
        foreach ((string name, byte[] value) in i.XAttrs)
        {
            uint holder = _nextInum++;
            Leaf(Entry(TypeXent, inum, name, holder, 0));
            Leaf(Inode(holder, UnixMode.S_IFREG | 0x1FF, value.Length, 1, value, flags: 0x20));
        }
    }

    private void WriteIndex()
    {
        // Keys of one directory can collide; the order among equal keys does not matter to a reader that looks at the names.
        List<(ulong Key, int Lnum, int Offs, int Len)> level = _leaves.OrderBy(l => l.Key).ToList();
        for (int height = 0; ; height++)
        {
            var next = new List<(ulong Key, int Lnum, int Offs, int Len)>();
            for (int i = 0; i < level.Count; i += _fanout)
            {
                int count = Math.Min(_fanout, level.Count - i);
                byte[] n = Node(TypeIdx, 28 + count * 20);
                BinaryPrimitives.WriteUInt16LittleEndian(n.AsSpan(24), (ushort)count);
                BinaryPrimitives.WriteUInt16LittleEndian(n.AsSpan(26), (ushort)height);
                for (int k = 0; k < count; k++)
                {
                    Span<byte> br = n.AsSpan(28 + k * 20, 20);
                    BinaryPrimitives.WriteUInt32LittleEndian(br, (uint)level[i + k].Lnum);
                    BinaryPrimitives.WriteUInt32LittleEndian(br[4..], (uint)level[i + k].Offs);
                    BinaryPrimitives.WriteUInt32LittleEndian(br[8..], (uint)level[i + k].Len);
                    BinaryPrimitives.WriteUInt32LittleEndian(br[12..], (uint)(level[i + k].Key >> 32));
                    BinaryPrimitives.WriteUInt32LittleEndian(br[16..], (uint)level[i + k].Key);
                }
                (int lnum, int offs, int len) = Place(Seal(n));
                next.Add((level[i].Key, lnum, offs, len));
            }
            level = next;
            if (level.Count == 1) break;
        }
        _root = (level[0].Lnum, level[0].Offs, level[0].Len);
    }

    // ---------------------------------------------------------- journal

    /// <summary>Fills the gap up to the next I/O unit the way UBIFS does: a padding node, or padding bytes when it does not fit.</summary>
    private int Pad(byte[] leb, int offs)
    {
        int end = (offs + MinIo - 1) / MinIo * MinIo, gap = end - offs;
        if (gap >= 28)
        {
            byte[] pad = Node(TypePad, 28);
            BinaryPrimitives.WriteUInt32LittleEndian(pad.AsSpan(24), (uint)(gap - 28));
            Seal(pad).CopyTo(leb, offs);
            leb.AsSpan(offs + 28, gap - 28).Clear();
        }
        else
        {
            leb.AsSpan(offs, gap).Fill(0xCE);
        }
        return end;
    }

    private void WriteLogStart()
    {
        if (_logOffs > 0) return;
        byte[] cs = Node(TypeCs, 32);
        BinaryPrimitives.WriteUInt64LittleEndian(cs.AsSpan(24), LogCommitNumber);
        Seal(cs).CopyTo(Leb(LogLeb), 0);
        _logOffs = Pad(Leb(LogLeb), 32);
    }

    private byte[] Reference(int lnum, int offs, ulong? sqnum = null)
    {
        byte[] r = Node(TypeRef, 64, sqnum);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(24), (uint)lnum);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(28), (uint)offs);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(32), 1);
        return Seal(r);
    }

    /// <summary>Starts a new journal erase block; the next journal node goes there.</summary>
    public void NewBud()
    {
        WriteLogStart();
        _budLnum = Math.Max(_lnum, _lebs.Count - 1) + 1;
        _budOffs = 0;
        Reference(_budLnum, 0).CopyTo(Leb(LogLeb), _logOffs);
        _logOffs = Pad(Leb(LogLeb), _logOffs + 64);
        Leb(_budLnum);
        _dirty = true;
    }

    private void Journal(byte[] node)
    {
        if (_budLnum < 0 || _budOffs + node.Length > LebSize) NewBud();
        node.CopyTo(Leb(_budLnum), _budOffs);
        _budOffs += (node.Length + 7) & ~7;
    }

    public void JournalInode(uint inum, int mode, long size, uint links, byte[]? data = null, uint uid = 0, uint gid = 0, long mtime = 0, int flags = 0) =>
        Journal(Inode(inum, mode, size, links, data, uid, gid, mtime, flags));

    /// <summary>An inode node with a link count of zero deletes the inode and everything that belongs to it.</summary>
    public void JournalDeleteInode(uint inum) => Journal(Inode(inum, UnixMode.S_IFREG, 0, 0));

    public void JournalData(uint inum, uint block, ReadOnlySpan<byte> content) => Journal(Data(inum, block, content));

    /// <summary>Adds a directory entry; an entry that points to inode 0 deletes the name.</summary>
    public void JournalEntry(uint parent, string name, uint inum, FsNodeType type = FsNodeType.File) =>
        Journal(Entry(TypeDent, parent, name, inum, DentType(type)));

    public void JournalXattr(uint host, string name, byte[] value)
    {
        uint holder = NewInum();
        Journal(Entry(TypeXent, host, name, holder, 0));
        Journal(Inode(holder, UnixMode.S_IFREG | 0x1FF, value.Length, 1, value, flags: 0x20));
    }

    public void JournalTruncate(uint inum, long oldSize, long newSize)
    {
        byte[] n = Node(TypeTrun, 56);
        BinaryPrimitives.WriteUInt32LittleEndian(n.AsSpan(24), inum);
        BinaryPrimitives.WriteUInt64LittleEndian(n.AsSpan(40), (ulong)oldSize);
        BinaryPrimitives.WriteUInt64LittleEndian(n.AsSpan(48), (ulong)newSize);
        Journal(Seal(n));
    }

    /// <summary>Leaves half a node at the journal head: what a power cut in the middle of a write looks like.</summary>
    public void JournalTornWrite()
    {
        byte[] n = Inode(NewInum(), UnixMode.S_IFREG | 0x1A4, 0, 1, new byte[600]);
        if (_budLnum < 0 || _budOffs + n.Length > LebSize) NewBud();
        n.AsSpan(0, 300).CopyTo(Leb(_budLnum).AsSpan(_budOffs));
        _budOffs += 304;
    }

    /// <summary>
    /// Puts a reference with an old sequence number into the second erase block of the log: a leftover
    /// of an earlier commit, which a reader must not follow. It points to a block that deletes a name.
    /// </summary>
    public void StaleLogBlock(uint parent, string name)
    {
        WriteLogStart();
        int lnum = Math.Max(_lnum, _lebs.Count - 1) + 1;
        byte[] dent = Entry(TypeDent, parent, name, 0, 0);
        dent.CopyTo(Leb(lnum), 0);
        Reference(lnum, 0, sqnum: 1).CopyTo(Leb(LogLeb + 1), 0);
    }

    // ----------------------------------------------------------- output

    /// <summary>The finished image: a file of whole erase blocks, as mkfs.ubifs writes it.</summary>
    public byte[] ToArray()
    {
        WriteLogStart();
        int lebCount = Math.Max(_lnum + 1, _lebs.Count) + 4;

        byte[] sb = Node(TypeSb, 4096);
        sb[26] = 0;                                                     // key hash: R5
        BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(32), MinIo);
        BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(36), LebSize);
        BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(40), (uint)lebCount);
        BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(44), (uint)lebCount * 4);
        BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(48), 4 * LebSize);
        BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(56), LogLebs);
        BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(60), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(64), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(68), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(72), (uint)_fanout);
        BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(80), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(sb.AsSpan(84), (ushort)_compression);
        BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(104), 1000000000);
        for (int i = 0; i < 16; i++) sb[108 + i] = (byte)(0xA0 + i);
        Seal(sb).CopyTo(Leb(0), 0);

        byte[] Master(ulong commit, (int Lnum, int Offs, int Len) root)
        {
            byte[] m = Node(TypeMst, 512);
            BinaryPrimitives.WriteUInt64LittleEndian(m.AsSpan(24), _nextInum);
            BinaryPrimitives.WriteUInt64LittleEndian(m.AsSpan(32), commit);
            BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(40), _dirty ? 1u : 2u);
            BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(44), LogLeb);
            BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(48), (uint)root.Lnum);
            BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(52), (uint)root.Offs);
            BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(56), (uint)root.Len);
            BinaryPrimitives.WriteUInt64LittleEndian(m.AsSpan(80), 4UL * LebSize);
            BinaryPrimitives.WriteUInt64LittleEndian(m.AsSpan(96), (ulong)(_lnum - MainFirst) * LebSize + (ulong)_offs);
            return Seal(m);
        }
        // An older master node in front of the current one, as left by the previous commit: it points nowhere useful.
        byte[] old = Master(6, (MainFirst, 0, 160)), current = Master(7, _root);
        for (int lnum = 1; lnum <= 2; lnum++)
        {
            byte[] leb = Leb(lnum);
            old.CopyTo(leb, 0);
            int at = Pad(leb, 512);
            current.CopyTo(leb, at);
            Pad(leb, at + 512);
        }

        var image = new MemoryStream();
        for (int i = 0; i < _lebs.Count; i++) image.Write(_lebs[i]);
        return image.ToArray();
    }

    // -------------------------------------------------------------- UBI

    public sealed record Volume(string Name, byte[] Data, bool Static = false, int Id = -1);

    /// <summary>
    /// Wraps volumes into a UBI image the way ubinize does. With <paramref name="shuffle"/> the erase
    /// blocks are stored in a scrambled order, as they are on a flash that has been in use.
    /// </summary>
    public static byte[] WrapUbi(IReadOnlyList<Volume> volumes, int pebSize = LebSize + 2 * MinIo, Random? shuffle = null, uint imageSeq = 0x1234ABCD)
    {
        const int vidOffset = MinIo, dataOffset = 2 * MinIo;
        int lebSize = pebSize - dataOffset;
        var pebs = new List<byte[]>();
        ulong sqnum = 0;

        void Peb(int volId, int lnum, ReadOnlySpan<byte> data, bool isStatic, int usedBlocks)
        {
            byte[] p = new byte[pebSize];
            p.AsSpan().Fill(0xFF);
            Span<byte> ec = p.AsSpan(0, 64);
            ec.Clear();
            "UBI#"u8.CopyTo(ec);
            ec[4] = 1;
            BinaryPrimitives.WriteUInt32BigEndian(ec[16..], vidOffset);
            BinaryPrimitives.WriteUInt32BigEndian(ec[20..], dataOffset);
            BinaryPrimitives.WriteUInt32BigEndian(ec[24..], imageSeq);
            BinaryPrimitives.WriteUInt32BigEndian(ec[60..], Crc(ec[..60]));
            if (volId >= 0)
            {
                Span<byte> vid = p.AsSpan(vidOffset, 64);
                vid.Clear();
                "UBI!"u8.CopyTo(vid);
                vid[4] = 1;
                vid[5] = (byte)(isStatic ? 2 : 1);
                if (volId >= 0x7FFFEFFF) vid[7] = 5;                    // compatibility: reject
                BinaryPrimitives.WriteUInt32BigEndian(vid[8..], (uint)volId);
                BinaryPrimitives.WriteUInt32BigEndian(vid[12..], (uint)lnum);
                if (isStatic)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(vid[20..], (uint)data.Length);
                    BinaryPrimitives.WriteUInt32BigEndian(vid[24..], (uint)usedBlocks);
                    BinaryPrimitives.WriteUInt32BigEndian(vid[32..], Crc(data));
                }
                BinaryPrimitives.WriteUInt64BigEndian(vid[40..], sqnum++);
                BinaryPrimitives.WriteUInt32BigEndian(vid[60..], Crc(vid[..60]));
                data.CopyTo(p.AsSpan(dataOffset));
            }
            pebs.Add(p);
        }

        // The volume table: one record per possible volume, in two copies.
        int slots = Math.Min(128, lebSize / 172);
        byte[] table = new byte[slots * 172];
        for (int i = 0; i < slots; i++)
        {
            Span<byte> r = table.AsSpan(i * 172, 172);
            Volume? v = volumes.Select((vol, n) => (vol, id: vol.Id >= 0 ? vol.Id : n)).Where(x => x.id == i).Select(x => x.vol).FirstOrDefault();
            if (v is not null)
            {
                byte[] name = System.Text.Encoding.UTF8.GetBytes(v.Name);
                BinaryPrimitives.WriteUInt32BigEndian(r, (uint)((v.Data.Length + lebSize - 1) / lebSize + 2));
                BinaryPrimitives.WriteUInt32BigEndian(r[4..], 1);
                r[12] = (byte)(v.Static ? 2 : 1);
                BinaryPrimitives.WriteUInt16BigEndian(r[14..], (ushort)name.Length);
                name.CopyTo(r[16..]);
            }
            BinaryPrimitives.WriteUInt32BigEndian(r[168..], Crc(r[..168]));
        }
        Peb(0x7FFFEFFF, 0, table, false, 0);
        Peb(0x7FFFEFFF, 1, table, false, 0);

        for (int n = 0; n < volumes.Count; n++)
        {
            Volume v = volumes[n];
            int blocks = (v.Data.Length + lebSize - 1) / lebSize;
            for (int lnum = 0; lnum < blocks; lnum++)
            {
                ReadOnlySpan<byte> data = v.Data.AsSpan(lnum * lebSize, Math.Min(lebSize, v.Data.Length - lnum * lebSize));
                // An erased block of a dynamic volume is not stored at all.
                if (!v.Static && data.IndexOfAnyExcept((byte)0xFF) < 0) continue;
                Peb(v.Id >= 0 ? v.Id : n, lnum, data, v.Static, blocks);
            }
        }
        Peb(-1, 0, default, false, 0);                                  // free blocks
        Peb(-1, 0, default, false, 0);

        if (shuffle is not null)
        {
            // Everything but the first block, whose erase-counter header identifies the image.
            for (int i = pebs.Count - 1; i > 1; i--)
            {
                int k = shuffle.Next(1, i + 1);
                (pebs[i], pebs[k]) = (pebs[k], pebs[i]);
            }
        }
        var image = new MemoryStream();
        foreach (byte[] p in pebs) image.Write(p);
        return image.ToArray();
    }
}
