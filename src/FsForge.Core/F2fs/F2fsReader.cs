using System.Text;
using FsForge.Core.Compression;
using FsForge.Core.Ext;
using static FsForge.Core.F2fs.F2fsFormat;

namespace FsForge.Core.F2fs;

/// <summary>
/// Reads F2FS images (the Flash-Friendly File System: Android userdata and system, some routers and single-board
/// computers) of any block size: the valid checkpoint, the NAT with its journal, inodes with extra attributes,
/// inline data, inline dentries and inline xattrs, the node tree of large files, compressed files (LZ4, ZSTD,
/// LZO), extended attributes and POSIX ACLs. Encrypted files (file-based encryption) cannot be read: they are
/// shown without content.
/// </summary>
public sealed class F2fsReader : IDisposable
{
    private readonly IBlockSource _source;
    private readonly bool _ownsSource;
    private int _bs, _logBps;
    private uint _feature, _natBlk, _mainBlk, _rootIno;
    private long _blockCount;
    private byte[] _natBitmap = Array.Empty<byte>();
    private readonly Dictionary<uint, (uint Ino, uint Addr)> _natJournal = new();
    private int _encrypted, _compressed;

    private F2fsReader(IBlockSource source, bool ownsSource)
    {
        _source = source;
        _ownsSource = ownsSource;
        Info = null!;
    }

    public F2fsInfo Info { get; private set; }

    /// <summary>Non-fatal problems found while reading.</summary>
    public List<string> Warnings { get; } = new();

    // ---------------------------------------------------------- detection

    private static bool ValidSuper(ReadOnlySpan<byte> sb)
    {
        if (U32(sb, 0) != Magic) return false;
        uint logBs = U32(sb, 16), logBps = U32(sb, 20), logSector = U32(sb, 8), logSpb = U32(sb, 12);
        if (logBs is < 12 or > 16 || logBps != 9 || logSector is < 9 or > 16 || logSector + logSpb != logBs) return false;
        uint segs = U32(sb, 48), main = U32(sb, 68);
        return segs > 0 && main > 0 && main <= segs && U32(sb, 92) > U32(sb, 72);
    }

    public static bool IsF2fs(IBlockSource source)
    {
        if (source.Length < 8192) return false;
        Span<byte> sb = stackalloc byte[SuperSize];
        source.ReadExactly(SuperOffset, sb);
        if (ValidSuper(sb)) return true;
        // The backup copy is in the second block (of 4 KiB, the usual size).
        source.ReadExactly(4096 + SuperOffset, sb);
        return ValidSuper(sb);
    }

    public static F2fsReader Open(IBlockSource source, bool ownsSource = true)
    {
        var r = new F2fsReader(source, ownsSource);
        byte[] sb = source.ReadBytes(SuperOffset, SuperSize);
        if (!ValidSuper(sb) || (U32(sb, 2180) & FeatSbChksum) != 0 && Crc(sb.AsSpan(0, SuperCrcOffset)) != U32(sb, SuperCrcOffset))
        {
            int bs = ValidSuper(sb) ? 1 << (int)U32(sb, 16) : 4096;
            byte[] backup = source.ReadBytes(bs + SuperOffset, SuperSize);
            if (!ValidSuper(backup)) throw new FsException("Corrupt F2FS image: neither copy of the superblock is valid.");
            sb = backup;
            r.Warnings.Add("The primary superblock of the F2FS image is damaged; the backup copy was used.");
        }
        r._feature = U32(sb, 2180);
        r._bs = 1 << (int)U32(sb, 16);
        r._logBps = (int)U32(sb, 20);
        r._blockCount = (long)U64(sb, 36);
        r._natBlk = U32(sb, 84);
        r._mainBlk = U32(sb, 92);
        r._rootIno = U32(sb, 96);
        if (sb[2201] != 0) throw new FsException("The F2FS filesystem spans several devices, which is not supported.");
        if ((r._feature & FeatBlkzoned) != 0) r.Warnings.Add("The F2FS image is made for a zoned device.");
        if ((long)r._blockCount * r._bs > source.Length)
            r.Warnings.Add($"The F2FS image is shorter than its superblock says ({r._blockCount * r._bs} bytes): it may be truncated.");
        r.ReadCheckpoint(sb);
        return r;
    }

    private byte[] Block(long addr)
    {
        if (addr < 0 || (addr + 1) * _bs > _source.Length) throw new FsException($"The F2FS image is truncated: block {addr} lies outside the file.");
        return _source.ReadBytes(addr * _bs, _bs);
    }

    private void ReadCheckpoint(byte[] sb)
    {
        uint cpBlk = U32(sb, 76);
        int bps = 1 << _logBps;
        (byte[] Header, long Start, ulong Version)? best = null;
        for (int pack = 0; pack < 2; pack++)
        {
            long start = cpBlk + (long)pack * bps;
            byte[] h;
            try
            {
                h = Block(start);
            }
            catch (FsException)
            {
                continue;
            }
            if (!ValidCp(h)) continue;
            uint total = U32(h, 136);
            if (total < 2 || total > bps) continue;
            byte[] tail;
            try
            {
                tail = Block(start + total - 1);
            }
            catch (FsException)
            {
                continue;
            }
            if (!ValidCp(tail) || U64(tail, 0) != U64(h, 0)) continue;
            if (best is null || U64(h, 0) > best.Value.Version) best = (h, start, U64(h, 0));
        }
        if (best is null) throw new FsException("Corrupt F2FS image: no valid checkpoint.");
        (byte[] cp, long cpStart, ulong ver) = best.Value;
        uint flags = U32(cp, 132);
        if ((flags & CpUmount) == 0)
            Warnings.Add("The F2FS filesystem was not cleanly unmounted: what was written after its last checkpoint (fsync without a checkpoint) is not shown.");
        if ((flags & CpError) != 0) Warnings.Add("The checkpoint of the F2FS image records an error: the filesystem should be checked with fsck.f2fs.");

        int sitSize = (int)U32(cp, 156), natSize = (int)U32(cp, 160);
        uint payload = U32(sb, 1664);
        int natAt = (flags & CpLargeNatBitmap) != 0 ? 192 + 4 : payload > 0 ? 192 : 192 + sitSize;
        if (natSize <= 0 || natAt + natSize > _bs) throw new FsException("Corrupt F2FS checkpoint: invalid NAT bitmap size.");
        _natBitmap = cp.AsSpan(natAt, natSize).ToArray();

        // The NAT journal: entries newer than the NAT blocks, in the summary of the hot data log.
        int startSum = (int)U32(cp, 140);
        if (startSum < 1 || startSum >= bps) throw new FsException("Corrupt F2FS checkpoint: invalid summary position.");
        byte[] sum = Block(cpStart + startSum);
        int journalAt = (flags & CpCompactSum) != 0 ? 0 : SummarySize * EntriesInSum(_bs);
        int n = U16(sum, journalAt), max = (SumJournalSize(_bs) - 2) / 13;
        if (n > max) throw new FsException("Corrupt F2FS checkpoint: invalid NAT journal.");
        for (int i = 0; i < n; i++)
        {
            int e = journalAt + 2 + i * 13;
            _natJournal[U32(sum, e)] = (U32(sum, e + 5), U32(sum, e + 9));
        }

        Guid uuid = new(sb.AsSpan(108, 16));
        Info = new F2fsInfo
        {
            BlockSize = _bs, BlockCount = _blockCount, SegmentCount = (int)U32(sb, 48), MainSegments = (int)U32(sb, 68),
            Features = _feature, VolumeName = VolumeNameOf(sb.AsSpan(124, MaxVolumeName * 2)), Uuid = uuid,
            Version = $"{U16(sb, 4)}.{U16(sb, 6)}", CheckpointVersion = ver, CheckpointFlags = flags,
            ValidBlocks = (long)U64(cp, 16), UserBlocks = (long)U64(cp, 8), FreeSegments = U32(cp, 32), ValidInodes = U32(cp, 148),
        };
    }

    private bool ValidCp(byte[] h)
    {
        int crcOff = (int)U32(h, 164);
        return crcOff >= 192 && crcOff <= _bs - 4 && Crc(h.AsSpan(0, crcOff)) == U32(h, crcOff);
    }

    // ------------------------------------------------------------- nodes

    private (uint Ino, uint Addr) Nat(uint nid)
    {
        if (_natJournal.TryGetValue(nid, out var j)) return j;
        int epb = NatEntriesPerBlock(_bs), bps = 1 << _logBps;
        long blockOff = nid / epb;
        if (blockOff >= (long)_natBitmap.Length * 8) throw new FsException($"Corrupt F2FS image: node {nid} is beyond the NAT.");
        long addr = _natBlk + (blockOff << 1) - (blockOff & (bps - 1));
        if (TestBitMsb(_natBitmap, blockOff)) addr += bps;
        byte[] b = Block(addr);
        int at = (int)(nid % epb) * NatEntrySize;
        return (U32(b, at + 1), U32(b, at + 5));
    }

    private byte[] Node(uint nid, uint ino)
    {
        (uint natIno, uint addr) = Nat(nid);
        if (addr < _mainBlk || addr >= _blockCount || natIno != ino)
            throw new FsException($"Corrupt F2FS image: node {nid} of inode {ino} has an invalid address.");
        byte[] b = Block(addr);
        int f = _bs - NodeFooterSize;
        if (U32(b, f) != nid || U32(b, f + 4) != ino) throw new FsException($"Corrupt F2FS image: node {nid} of inode {ino} does not belong there.");
        return b;
    }

    private sealed class Ino
    {
        public uint Nid;
        public byte[] Raw = null!;
        public int Mode, Extra, InlineXattrAddrs, AddrsPerInode;
        public byte Advise, Inline;
        public uint Uid, Gid, Links, XattrNid, Flags;
        public long Size, MTime;
        public int Algorithm, LogCluster;
        public bool Encrypted => (Advise & AdviseEncrypt) != 0;
        public bool Compressed => (Flags & ComprFl) != 0;
        public int Base => Extra / 4;
    }

    private Ino ReadInode(uint ino)
    {
        byte[] b = Node(ino, ino);
        var i = new Ino
        {
            Nid = ino, Raw = b, Mode = U16(b, 0), Advise = b[2], Inline = b[3], Uid = U32(b, 4), Gid = U32(b, 8), Links = U32(b, 12),
            Size = (long)U64(b, 16), MTime = (long)U64(b, 48), XattrNid = U32(b, 76), Flags = U32(b, 80),
        };
        if ((i.Inline & ExtraAttr) != 0)
        {
            i.Extra = U16(b, OffsetOfIAddr);
            if (i.Extra % 4 != 0 || i.Extra < 4 || i.Extra > 200) throw new FsException($"Corrupt F2FS image: inode {ino} has an invalid extra size.");
        }
        if ((i.Inline & InlineXattr) != 0)
            i.InlineXattrAddrs = (_feature & FeatFlexibleInlineXattr) != 0 && i.Extra >= 4 ? U16(b, OffsetOfIAddr + 2) : InlineXattrAddrs;
        i.AddrsPerInode = AddrsPerInode(_bs) - i.Base - i.InlineXattrAddrs;
        if (i.AddrsPerInode <= 0 || i.Size < 0) throw new FsException($"Corrupt F2FS image: inode {ino} has an invalid layout.");
        if (i.Compressed && i.Extra >= 36)
        {
            i.Algorithm = b[OffsetOfIAddr + 32];
            i.LogCluster = b[OffsetOfIAddr + 33];
            if (i.LogCluster is < 2 or > 8) throw new FsException($"Corrupt F2FS image: inode {ino} has an invalid compression cluster size.");
        }
        return i;
    }

    /// <summary>The block address of every block of a file or directory, in order.</summary>
    private uint[] BlockMap(Ino ino)
    {
        long blocks = (ino.Size + _bs - 1) / _bs;
        if (blocks > int.MaxValue / 2) throw new FsException($"Corrupt F2FS image: inode {ino.Nid} is too large.");
        uint[] map = new uint[blocks];
        int pos = 0;
        byte[] r = ino.Raw;
        for (int i = 0; i < ino.AddrsPerInode && pos < blocks; i++) map[pos++] = U32(r, OffsetOfIAddr + (ino.Base + i) * 4);
        int nidAt = OffsetOfIAddr + AddrsPerInode(_bs) * 4;
        int apb = AddrsPerBlock(_bs);

        void Direct(uint nid)
        {
            if (nid == 0)
            {
                pos = (int)Math.Min(blocks, pos + (long)apb);          // a hole as large as a direct node
                return;
            }
            byte[] d = Node(nid, ino.Nid);
            for (int i = 0; i < apb && pos < blocks; i++) map[pos++] = U32(d, i * 4);
        }
        void Indirect(uint nid, int depth)
        {
            long span = depth == 1 ? apb : (long)apb * apb;
            if (nid == 0)
            {
                pos = (int)Math.Min(blocks, pos + span * apb);
                return;
            }
            byte[] d = Node(nid, ino.Nid);
            for (int i = 0; i < apb && pos < blocks; i++)
            {
                uint child = U32(d, i * 4);
                if (depth == 1) Direct(child);
                else Indirect(child, 1);
            }
        }
        if (pos < blocks) Direct(U32(r, nidAt));
        if (pos < blocks) Direct(U32(r, nidAt + 4));
        if (pos < blocks) Indirect(U32(r, nidAt + 8), 1);
        if (pos < blocks) Indirect(U32(r, nidAt + 12), 1);
        if (pos < blocks) Indirect(U32(r, nidAt + 16), 2);
        return map;
    }

    private int InlineOffset(Ino ino) => OffsetOfIAddr + (ino.Base + 1) * 4;
    private int MaxInline(Ino ino) => 4 * (AddrsPerInode(_bs) - ino.Base - ino.InlineXattrAddrs - 1);

    private bool ValidData(uint addr) => addr >= _mainBlk && addr < _blockCount;

    // -------------------------------------------------------------- tree

    public FsTree ReadTree(IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var tree = new FsTree();
        Ino root = ReadInode(_rootIno);
        if ((root.Mode & UnixMode.S_IFMT) != UnixMode.S_IFDIR) throw new FsException("Corrupt F2FS image: the root inode is not a directory.");
        FsInode r = tree.Root.Inode;
        (r.Mode, r.Uid, r.Gid, r.MTime) = (root.Mode & 0xFFF, root.Uid, root.Gid, root.MTime);
        r.XAttrs = ReadXattrs(root);

        var known = new Dictionary<uint, FsInode>();
        var seenDirs = new HashSet<uint> { _rootIno };
        var stack = new Stack<(FsNode Node, Ino Ino)>();
        stack.Push((tree.Root, root));
        long done = 0;
        int encryptedNames = 0;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            (FsNode dir, Ino dirIno) = stack.Pop();
            if (dirIno.Encrypted) encryptedNames++;
            foreach ((string name, uint childIno) in ReadDirectory(dirIno))
            {
                if (name is "." or "..") continue;
                string path = dir.IsRoot ? "/" + name : dir.Path + "/" + name;
                if (name.Length == 0 || name.Contains('/') || dir.Find(name) is not null)
                {
                    Warnings.Add($"Invalid or duplicate directory entry '{name}' in '{dir.Path}' was skipped.");
                    continue;
                }
                if (known.TryGetValue(childIno, out FsInode? linked))
                {
                    tree.Add(dir, name, linked);
                    continue;
                }
                Ino ino;
                try
                {
                    ino = ReadInode(childIno);
                }
                catch (FsException ex)
                {
                    Warnings.Add($"'{path}' was skipped: {ex.Message}");
                    continue;
                }
                FsNodeType? type = UnixMode.TypeFromMode(ino.Mode);
                if (type is null)
                {
                    Warnings.Add($"'{path}' has an unknown file type (mode {Convert.ToString(ino.Mode, 8)}) and was skipped.");
                    continue;
                }
                var inode = new FsInode { Type = type.Value, Mode = ino.Mode & 0xFFF, Uid = ino.Uid, Gid = ino.Gid, MTime = ino.MTime };
                switch (type.Value)
                {
                    case FsNodeType.Directory:
                        if (!seenDirs.Add(childIno)) throw new FsException("Corrupt F2FS image: a directory is referenced more than once.");
                        break;
                    case FsNodeType.File:
                        if (ino.Encrypted)
                        {
                            _encrypted++;
                            break;
                        }
                        if (ino.Compressed) _compressed++;
                        if (ino.Size > 0) inode.Data = new F2fsFileData(this, ino);
                        break;
                    case FsNodeType.Symlink:
                        inode.Mode = 0x1FF;
                        if (ino.Size is <= 0 or > 65536) throw new FsException($"Corrupt F2FS image: the symbolic link '{path}' has an invalid length.");
                        byte[] target = new byte[ino.Size];
                        using (Stream s = new F2fsFileData(this, ino).OpenRead()) s.ReadExactly(target);
                        inode.LinkTarget = PosixName.Decode(target);
                        if (ino.Encrypted) _encrypted++;
                        break;
                    case FsNodeType.CharDevice or FsNodeType.BlockDevice:
                        uint old = U32(ino.Raw, OffsetOfIAddr + ino.Base * 4), dev = U32(ino.Raw, OffsetOfIAddr + (ino.Base + 1) * 4);
                        if (old != 0) (inode.DevMajor, inode.DevMinor) = ((old >> 8) & 0xFF, old & 0xFF);
                        else (inode.DevMajor, inode.DevMinor) = ((dev & 0xFFF00) >> 8, (dev & 0xFF) | ((dev >> 12) & 0xFFF00));
                        break;
                }
                inode.XAttrs = ReadXattrs(ino);
                FsNode node = tree.Add(dir, name, inode);
                if (type == FsNodeType.Directory) stack.Push((node, ino));
                else if (ino.Links > 1) known[childIno] = inode;
                if ((++done & 0x3FF) == 0) progress?.Report(new ProgressInfo("read", done, Info.ValidInodes, path));
            }
        }
        progress?.Report(new ProgressInfo("read", done, done));
        if (_encrypted > 0)
            Warnings.Add($"{_encrypted} file(s) are encrypted (file-based encryption, as on the userdata partition of Android): " +
                         "their content cannot be read and is shown empty.");
        if (encryptedNames > 0)
            Warnings.Add($"{encryptedNames} director(y/ies) have encrypted names: they are shown as they are stored.");
        Info = new F2fsInfo
        {
            BlockSize = Info.BlockSize, BlockCount = Info.BlockCount, SegmentCount = Info.SegmentCount, MainSegments = Info.MainSegments,
            Features = Info.Features, VolumeName = Info.VolumeName, Uuid = Info.Uuid, Version = Info.Version,
            CheckpointVersion = Info.CheckpointVersion, CheckpointFlags = Info.CheckpointFlags, ValidBlocks = Info.ValidBlocks,
            UserBlocks = Info.UserBlocks, FreeSegments = Info.FreeSegments, ValidInodes = Info.ValidInodes,
            EncryptedFiles = _encrypted, CompressedFiles = _compressed,
        };
        return tree;
    }

    private IEnumerable<(string Name, uint Ino)> ReadDirectory(Ino dir)
    {
        var list = new List<(string, uint)>();
        if ((dir.Inline & InlineDentry) != 0)
        {
            int max = MaxInline(dir);
            int nr = max * 8 / ((DirEntrySize + SlotLen) * 8 + 1);
            int bitmap = (nr + 7) / 8, reserved = max - ((DirEntrySize + SlotLen) * nr + bitmap);
            if (nr <= 0 || reserved < 0) throw new FsException($"Corrupt F2FS image: directory {dir.Nid} has an invalid inline area.");
            ReadOnlySpan<byte> area = dir.Raw.AsSpan(InlineOffset(dir), max);
            ParseDentries(area, nr, bitmap + reserved, list, dir.Nid);
            return list;
        }
        int perBlock = DentriesInBlock(_bs), bm = DentryBitmapSize(_bs);
        int dentAt = _bs - (DirEntrySize + SlotLen) * perBlock;
        foreach (uint addr in BlockMap(dir))
        {
            if (addr is NullAddr or NewAddr) continue;
            if (!ValidData(addr)) throw new FsException($"Corrupt F2FS image: a block of directory {dir.Nid} lies outside the main area.");
            ParseDentries(Block(addr), perBlock, dentAt, list, dir.Nid);
        }
        return list;
    }

    private static void ParseDentries(ReadOnlySpan<byte> area, int nr, int dentAt, List<(string, uint)> list, uint dirIno)
    {
        int namesAt = dentAt + nr * DirEntrySize;
        for (int i = 0; i < nr;)
        {
            if (!TestBitLe(area, i))
            {
                i++;
                continue;
            }
            int e = dentAt + i * DirEntrySize;
            uint ino = U32(area, e + 4);
            int len = U16(area, e + 8);
            int slots = Math.Max(1, (len + SlotLen - 1) / SlotLen);
            if (len == 0 || len > NameLen || i + slots > nr)
                throw new FsException($"Corrupt F2FS image: directory {dirIno} has an invalid entry.");
            list.Add((PosixName.Decode(area.Slice(namesAt + i * SlotLen, len)), ino));
            i += slots;
        }
    }

    private SortedDictionary<string, byte[]>? ReadXattrs(Ino ino)
    {
        var buf = new MemoryStream();
        if (ino.InlineXattrAddrs > 0)
            buf.Write(ino.Raw, OffsetOfIAddr + (AddrsPerInode(_bs) - ino.InlineXattrAddrs) * 4, ino.InlineXattrAddrs * 4);
        if (ino.XattrNid != 0)
        {
            try
            {
                buf.Write(Node(ino.XattrNid, ino.Nid), 0, _bs - NodeFooterSize);
            }
            catch (FsException ex)
            {
                Warnings.Add($"The extended attributes of inode {ino.Nid} cannot be read: {ex.Message}");
            }
        }
        byte[] b = buf.ToArray();
        if (b.Length < 28 || U32(b, 0) != XattrMagic) return null;
        SortedDictionary<string, byte[]>? result = null;
        for (int pos = 24; pos + 4 <= b.Length && U32(b, pos) != 0;)
        {
            int index = b[pos], nameLen = b[pos + 1], valueLen = U16(b, pos + 2);
            if (pos + 4 + nameLen + valueLen > b.Length) break;
            string? prefix = XattrPrefix(index);
            if (prefix is not null)
            {
                string name = prefix + Encoding.UTF8.GetString(b, pos + 4, nameLen);
                byte[] value = b.AsSpan(pos + 4 + nameLen, valueLen).ToArray();
                if (index is 2 or 3) value = ExtFormat.AclDiskToXattr(value) ?? value;
                (result ??= new SortedDictionary<string, byte[]>(StringComparer.Ordinal))[name] = value;
            }
            pos += (4 + nameLen + valueLen + 3) & ~3;
        }
        return result;
    }

    // --------------------------------------------------------------- data

    private sealed class F2fsFileData(F2fsReader reader, Ino ino) : IFileData
    {
        private uint[]? _map;
        public long Length => ino.Size;
        public Stream OpenRead() => new F2fsFileStream(reader, ino, _map ??= reader.MapForData(ino));
    }

    private uint[] MapForData(Ino ino) => (ino.Inline & InlineData) != 0 ? Array.Empty<uint>() : BlockMap(ino);

    /// <summary>Reads the file block by block; a compressed file cluster by cluster.</summary>
    private sealed class F2fsFileStream : Stream
    {
        private readonly F2fsReader _r;
        private readonly Ino _ino;
        private readonly uint[] _map;
        private readonly byte[] _chunk;
        private readonly int _chunkBlocks;
        private long _position, _chunkIndex = -1;

        public F2fsFileStream(F2fsReader r, Ino ino, uint[] map)
        {
            (_r, _ino, _map) = (r, ino, map);
            _chunkBlocks = ino.Compressed ? 1 << ino.LogCluster : 1;
            _chunk = new byte[_chunkBlocks * r._bs];
        }

        private void Load(long index)
        {
            Array.Clear(_chunk);
            if ((_ino.Inline & InlineData) != 0)
            {
                int max = _r.MaxInline(_ino);
                _ino.Raw.AsSpan(_r.InlineOffset(_ino), (int)Math.Min(max, Math.Min(_ino.Size, _chunk.Length))).CopyTo(_chunk);
                return;
            }
            int bs = _r._bs;
            long first = index * _chunkBlocks;
            if (_ino.Compressed && first < _map.Length && _map[first] == CompressAddr)
            {
                var packed = new MemoryStream();
                for (long i = first + 1; i < Math.Min(_map.Length, first + _chunkBlocks); i++)
                {
                    uint a = _map[i];
                    if (a is NullAddr or NewAddr) break;
                    if (!_r.ValidData(a)) throw new FsException("Corrupt F2FS image: a compressed block lies outside the main area.");
                    packed.Write(_r.Block(a));
                }
                byte[] p = packed.ToArray();
                if (p.Length < 24) throw new FsException("Corrupt F2FS image: an empty compressed cluster.");
                uint clen = U32(p, 0);
                if (clen > p.Length - 24) throw new FsException("Corrupt F2FS image: invalid length of a compressed cluster.");
                Decompress(_ino.Algorithm, p.AsSpan(24, (int)clen), _chunk);
                return;
            }
            for (int i = 0; i < _chunkBlocks; i++)
            {
                long b = first + i;
                if (b >= _map.Length) break;
                uint a = _map[b];
                if (a is NullAddr or NewAddr or CompressAddr) continue;
                if (!_r.ValidData(a)) throw new FsException($"Corrupt F2FS image: block {b} of inode {_ino.Nid} lies outside the main area.");
                _r._source.ReadExactly((long)a * bs, _chunk.AsSpan(i * bs, bs));
            }
        }

        private static void Decompress(int algorithm, ReadOnlySpan<byte> src, Span<byte> dst)
        {
            switch (algorithm)
            {
                case CompressLz4:
                    new Lz4Codec().Decompress(src, dst);
                    break;
                case CompressZstd:
                    using (var z = new ZstdSharp.Decompressor())
                        z.Unwrap(src, dst);
                    break;
                case CompressLzo:
                    new LzoCodec().Decompress(src, dst);
                    break;
                default:
                    throw new FsException(algorithm == CompressLzoRle
                        ? "The F2FS file is compressed with LZO-RLE, which is not supported."
                        : $"The F2FS file is compressed with an unknown method ({algorithm}).");
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int total = 0;
            while (buffer.Length > 0 && _position < _ino.Size)
            {
                long index = _position / _chunk.Length;
                if (index != _chunkIndex)
                {
                    try
                    {
                        Load(index);
                    }
                    catch (Exception ex) when (ex is ZstdSharp.ZstdException or ArgumentException or IndexOutOfRangeException)
                    {
                        throw new FsException($"Corrupt compressed data in inode {_ino.Nid}.", ex);
                    }
                    _chunkIndex = index;
                }
                int at = (int)(_position - index * _chunk.Length);
                int n = (int)Math.Min(Math.Min(buffer.Length, _chunk.Length - at), _ino.Size - _position);
                _chunk.AsSpan(at, n).CopyTo(buffer);
                buffer = buffer[n..];
                _position += n;
                total += n;
            }
            return total;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _ino.Size;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
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
