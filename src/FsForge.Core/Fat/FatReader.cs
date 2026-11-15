using System.Text;
using static FsForge.Core.Fat.FatFormat;

namespace FsForge.Core.Fat;

/// <summary>
/// Reader for FAT12, FAT16 and FAT32 with long (VFAT) names. FAT has no owners, permissions, links or
/// devices: files are 0644 (0444 when read-only), directories 0755, everything belongs to root.
/// Times are the write times of the entries, taken as UTC.
/// </summary>
public sealed class FatReader : IDisposable
{
    private readonly IBlockSource _source;
    private readonly bool _ownsSource;
    private uint[] _fat = Array.Empty<uint>();
    private int _bps, _clusterSize;
    private long _fatStart, _rootStart, _dataStart, _clusters;
    private int _rootEntries;
    private uint _rootCluster;
    private FatType _type;

    private FatReader(IBlockSource source, bool ownsSource)
    {
        _source = source;
        _ownsSource = ownsSource;
        Info = null!;
    }

    public FatInfo Info { get; private set; }
    public List<string> Warnings { get; } = new();

    // ---------------------------------------------------------- detection

    /// <summary>A FAT boot sector with a consistent BIOS parameter block.</summary>
    public static bool IsFat(IBlockSource source)
    {
        if (source.Length < 512) return false;
        byte[] s = source.ReadBytes(0, 512);
        return Bpb(s, source.Length, out _);
    }

    private readonly record struct Layout(int Bps, int Spc, int Reserved, int Fats, int RootEntries, long TotalSectors, long FatSectors, long Clusters);

    private static bool Bpb(byte[] s, long fileLength, out Layout l)
    {
        l = default;
        if (s[510] != 0x55 || s[511] != 0xAA || s[0] is not (0xEB or 0xE9)) return false;
        int bps = Le.U16(s, 11), spc = s[13], reserved = Le.U16(s, 14), fats = s[16], rootEntries = Le.U16(s, 17);
        if (bps is not (512 or 1024 or 2048 or 4096) || spc == 0 || (spc & (spc - 1)) != 0 || reserved == 0 || fats is 0 or > 4) return false;
        if (s[21] is not (0xF0 or >= 0xF8)) return false;
        long total = Le.U16(s, 19) != 0 ? Le.U16(s, 19) : Le.U32(s, 32);
        long fatSz = Le.U16(s, 22) != 0 ? Le.U16(s, 22) : Le.U32(s, 36);
        if (total == 0 || fatSz == 0) return false;
        long rootSectors = ((long)rootEntries * 32 + bps - 1) / bps;
        long dataSectors = total - reserved - fats * fatSz - rootSectors;
        if (dataSectors <= 0) return false;
        // The filesystem must lie in the file (a truncated tail of free space is tolerated).
        if ((reserved + fats * fatSz + rootSectors) * bps > fileLength) return false;
        long clusters = dataSectors / spc;
        if (clusters == 0 || clusters > MaxFat32Clusters) return false;
        if (TypeFor(clusters) == FatType.Fat32 ? rootEntries != 0 || Le.U16(s, 22) != 0 : rootEntries == 0) return false;
        l = new Layout(bps, spc, reserved, fats, rootEntries, total, fatSz, clusters);
        return true;
    }

    public static FatReader Open(IBlockSource source, bool ownsSource = true)
    {
        var r = new FatReader(source, ownsSource);
        try
        {
            r.ReadBootSector();
            return r;
        }
        catch
        {
            if (ownsSource) source.Dispose();
            throw;
        }
    }

    private void ReadBootSector()
    {
        if (_source.Length < 512) throw new FsException("The file is too small to be a FAT filesystem.");
        byte[] s = _source.ReadBytes(0, 512);
        if (!Bpb(s, _source.Length, out Layout l)) throw new FsException("Not a FAT filesystem (invalid boot sector).");
        _bps = l.Bps;
        _clusterSize = l.Bps * l.Spc;
        _clusters = l.Clusters;
        _type = TypeFor(l.Clusters);
        _rootEntries = l.RootEntries;
        _fatStart = (long)l.Reserved * l.Bps;
        _rootStart = _fatStart + l.Fats * l.FatSectors * l.Bps;
        _dataStart = _rootStart + ((long)l.RootEntries * 32 + l.Bps - 1) / l.Bps * l.Bps;
        bool fat32 = _type == FatType.Fat32;
        _rootCluster = fat32 ? Le.U32(s, 44) : 0;
        int ext = fat32 ? 0x40 : 0x24;
        bool hasExt = s[ext + 2] is 0x29 or 0x28;
        string bpbLabel = hasExt && s[ext + 2] == 0x29 ? Encoding.ASCII.GetString(s, ext + 7, 11).TrimEnd() : "";
        if (bpbLabel == "NO NAME") bpbLabel = "";
        uint volumeId = hasExt ? Le.U32(s, ext + 3) : 0;

        if (_source.Length < _dataStart + _clusters * _clusterSize)
            Warnings.Add("The file is shorter than the FAT filesystem it holds; the missing part reads as zeros.");

        ReadFat(l);
        bool dirty = _type switch
        {
            FatType.Fat16 => (_fat[1] & 0x8000) == 0,
            FatType.Fat32 => (_fat[1] & 0x08000000) == 0,
            _ => false,
        };
        if (dirty) Warnings.Add("The FAT filesystem was not cleanly unmounted (its \"clean\" flag is not set). It is read as it is; fsck.fat would check it.");
        long free = 0;
        for (long c = 2; c < _clusters + 2; c++) if (_fat[c] == 0) free++;

        // Boot code: everything between the parameter block and the signature.
        int codeStart = fat32 ? 90 : 62;
        Info = new FatInfo
        {
            Type = _type,
            SectorSize = l.Bps,
            ClusterSize = _clusterSize,
            TotalSectors = l.TotalSectors,
            Clusters = _clusters,
            FreeClusters = free,
            Fats = l.Fats,
            FatSectors = l.FatSectors,
            ReservedSectors = l.Reserved,
            RootEntries = l.RootEntries,
            Label = RootLabel() ?? bpbLabel,
            VolumeId = volumeId,
            OemName = Encoding.ASCII.GetString(s, 3, 8).TrimEnd('\0', ' '),
            Media = s[21],
            HiddenSectors = Le.U32(s, 28),
            SectorsPerTrack = Le.U16(s, 24),
            Heads = Le.U16(s, 26),
            BootCode = s.AsSpan(codeStart, 510 - codeStart).ToArray(),
            Dirty = dirty,
        };
    }

    private void ReadFat(Layout l)
    {
        long bytes = l.FatSectors * l.Bps;
        long needed = _type switch { FatType.Fat12 => (_clusters + 2) * 3 / 2 + 2, FatType.Fat16 => (_clusters + 2) * 2, _ => (_clusters + 2) * 4 };
        if (needed > bytes) throw new FsException("Corrupt FAT filesystem: the FAT is too small for the number of clusters.");
        byte[] raw = new byte[needed];
        int n = _source.Read(_fatStart, raw);
        if (n < needed) throw new FsException("The FAT filesystem is truncated: its FAT cannot be read.");
        _fat = new uint[_clusters + 2];
        for (long c = 0; c < _clusters + 2; c++)
        {
            _fat[c] = _type switch
            {
                FatType.Fat12 => (c & 1) == 0 ? (uint)(Le.U16(raw, (int)(c * 3 / 2)) & 0xFFF) : (uint)(Le.U16(raw, (int)(c * 3 / 2)) >> 4),
                FatType.Fat16 => Le.U16(raw, (int)(c * 2)),
                _ => Le.U32(raw, (int)(c * 4)) & 0x0FFFFFFF,
            };
        }
    }

    private uint EndOfChain => _type switch { FatType.Fat12 => 0xFF8, FatType.Fat16 => 0xFFF8, _ => 0x0FFFFFF8 };

    /// <summary>The clusters of a chain; stops (with a warning) at a free, bad, out-of-range or looping link.</summary>
    private List<uint> Chain(uint first, string path, long maxClusters)
    {
        var list = new List<uint>();
        if (first == 0) return list;
        var seen = new HashSet<uint>();
        uint c = first;
        while (true)
        {
            if (c < 2 || c >= _clusters + 2)
            {
                Warnings.Add($"'{path}': its cluster chain points outside the filesystem and was cut.");
                break;
            }
            if (!seen.Add(c) || list.Count > maxClusters)
            {
                Warnings.Add($"'{path}': its cluster chain loops or is longer than the file and was cut.");
                break;
            }
            list.Add(c);
            uint next = _fat[c];
            if (next >= EndOfChain) break;
            if (next == 0 || next == EndOfChain - 1)
            {
                Warnings.Add($"'{path}': its cluster chain ends in a free or bad cluster and was cut.");
                break;
            }
            c = next;
        }
        return list;
    }

    private long ClusterOffset(uint c) => _dataStart + (long)(c - 2) * _clusterSize;

    // -------------------------------------------------------- directories

    private sealed record Entry(string Name, byte Attr, uint Cluster, uint Size, long MTime);

    private byte[] ReadDirectory(uint cluster, string path)
    {
        if (cluster == 0 && _type != FatType.Fat32)
        {
            byte[] root = new byte[_rootEntries * 32];
            _source.Read(_rootStart, root);
            return root;
        }
        List<uint> chain = Chain(cluster == 0 ? _rootCluster : cluster, path, (1L << 21) / Math.Max(1, _clusterSize / 32));
        byte[] data = new byte[(long)chain.Count * _clusterSize];
        for (int i = 0; i < chain.Count; i++) _source.Read(ClusterOffset(chain[i]), data.AsSpan(i * _clusterSize, _clusterSize));
        return data;
    }

    private string? RootLabel()
    {
        byte[] d = ReadDirectory(0, "/");
        for (int p = 0; p + 32 <= d.Length; p += 32)
        {
            if (d[p] == 0) break;
            if (d[p] == 0xE5) continue;
            byte attr = d[p + 11];
            if (attr != AttrLongName && (attr & AttrVolume) != 0 && (attr & AttrDirectory) == 0)
                return DecodeOem(d.AsSpan(p, 11)).TrimEnd();
        }
        return null;
    }

    private List<Entry> ParseDirectory(byte[] d, string path)
    {
        var entries = new List<Entry>();
        var lfn = new char[260];
        int lfnSeq = -1;
        byte lfnSum = 0;
        int lfnLength = 0;
        for (int p = 0; p + 32 <= d.Length; p += 32)
        {
            byte first = d[p];
            if (first == 0) break;
            if (first == 0xE5)
            {
                lfnSeq = -1;
                continue;
            }
            byte attr = d[p + 11];
            if (attr == AttrLongName)
            {
                int ord = first & 0x3F;
                if ((first & 0x40) != 0)
                {
                    lfnSeq = ord;
                    lfnSum = d[p + 13];
                    lfnLength = ord * 13;
                    Array.Fill(lfn, '￿');
                }
                else if (lfnSeq < 0 || ord != lfnSeq - 1 || d[p + 13] != lfnSum)
                {
                    lfnSeq = -1;
                    continue;
                }
                lfnSeq = ord;
                if (ord is < 1 or > 20) { lfnSeq = -1; continue; }
                int at = (ord - 1) * 13;
                foreach (int o in new[] { 1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30 })
                    lfn[at++] = (char)Le.U16(d, p + o);
                continue;
            }
            if ((attr & AttrVolume) != 0)
            {
                lfnSeq = -1;
                continue;
            }
            string name;
            if (lfnSeq == 1 && ShortNameChecksum(d.AsSpan(p, 11)) == lfnSum)
            {
                int end = 0;
                while (end < lfnLength && lfn[end] != '\0' && lfn[end] != '￿') end++;
                name = new string(lfn, 0, end);
            }
            else
            {
                byte[] raw = d.AsSpan(p, 11).ToArray();
                if (raw[0] == 0x05) raw[0] = 0xE5;
                string b = DecodeOem(raw.AsSpan(0, 8)).TrimEnd(), e = DecodeOem(raw.AsSpan(8, 3)).TrimEnd();
                byte nt = d[p + 12];
                if ((nt & CaseLowerBase) != 0) b = b.ToLowerInvariant();
                if ((nt & CaseLowerExt) != 0) e = e.ToLowerInvariant();
                name = e.Length > 0 ? b + "." + e : b;
            }
            lfnSeq = -1;
            if (name is "." or "..") continue;
            uint cluster = Le.U16(d, p + 26) | (_type == FatType.Fat32 ? (uint)Le.U16(d, p + 20) << 16 : 0);
            entries.Add(new Entry(name, attr, cluster, Le.U32(d, p + 28), ToUnix(Le.U16(d, p + 24), Le.U16(d, p + 22))));
        }
        return entries;
    }

    // --------------------------------------------------------------- tree

    /// <summary>Builds the tree. File data stays in the image and is read on demand.</summary>
    public FsTree ReadTree(IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var tree = new FsTree(0x1ED, 0);
        var owners = new Dictionary<uint, string>();             // first cluster -> who owns it, to find cross-links
        var stack = new Stack<(FsNode Dir, uint Cluster)>();
        stack.Push((tree.Root, 0));
        long done = 0;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            (FsNode dir, uint cluster) = stack.Pop();
            foreach (Entry e in ParseDirectory(ReadDirectory(cluster, dir.Path), dir.Path))
            {
                string path = dir.IsRoot ? "/" + e.Name : dir.Path + "/" + e.Name;
                if (e.Name.Length == 0 || e.Name.Contains('/') || dir.Find(e.Name) is not null)
                {
                    Warnings.Add($"Invalid or duplicate directory entry '{e.Name}' in '{dir.Path}' was skipped.");
                    continue;
                }
                if (e.Cluster != 0 && owners.TryGetValue(e.Cluster, out string? other))
                {
                    Warnings.Add($"'{path}' shares its clusters with '{other}' (cross-linked) and was skipped.");
                    continue;
                }
                if (e.Cluster != 0) owners[e.Cluster] = path;
                bool isDir = (e.Attr & AttrDirectory) != 0;
                var inode = new FsInode
                {
                    Type = isDir ? FsNodeType.Directory : FsNodeType.File,
                    Mode = isDir ? 0x1ED : (e.Attr & AttrReadOnly) != 0 ? 0x124 : 0x1A4,
                    MTime = e.MTime,
                };
                if (isDir)
                {
                    if (e.Cluster == 0 || e.Cluster == cluster)
                    {
                        Warnings.Add($"Directory '{path}' has no valid cluster and was skipped.");
                        continue;
                    }
                    FsNode sub = tree.Add(dir, e.Name, inode);
                    stack.Push((sub, e.Cluster));
                }
                else
                {
                    if (e.Size > 0)
                    {
                        long need = ((long)e.Size + _clusterSize - 1) / _clusterSize;
                        List<uint> chain = Chain(e.Cluster, path, need);
                        long size = Math.Min(e.Size, (long)chain.Count * _clusterSize);
                        if (size < e.Size) Warnings.Add($"'{path}' is {e.Size} bytes long but its clusters hold only {size}; the rest was cut.");
                        inode.Data = new FatFileData(this, chain, size);
                    }
                    tree.Add(dir, e.Name, inode);
                }
                if ((++done & 0x3FF) == 0) progress?.Report(new ProgressInfo("read", done, 0, path));
            }
        }
        progress?.Report(new ProgressInfo("read", done, done));
        return tree;
    }

    // ---------------------------------------------------------- file data

    private sealed class FatFileData(FatReader reader, List<uint> chain, long length) : IFileData
    {
        public long Length => length;
        public Stream OpenRead() => new FatFileStream(reader, chain, length);
    }

    private sealed class FatFileStream(FatReader r, List<uint> chain, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => _position = Math.Clamp(value, 0, length); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            long left = length - _position;
            if (left <= 0 || buffer.Length == 0) return 0;
            int cs = r._clusterSize;
            int index = (int)(_position / cs), inCluster = (int)(_position % cs);
            // Read across clusters that follow each other on the disk in one go.
            int run = 1;
            while (index + run < chain.Count && chain[index + run] == chain[index] + run && (long)run * cs < 1 << 20) run++;
            int n = (int)Math.Min(Math.Min(buffer.Length, left), (long)run * cs - inCluster);
            int got = r._source.Read(r.ClusterOffset(chain[index]) + inCluster, buffer[..n]);
            if (got < n) buffer[Math.Max(0, got)..n].Clear();          // a truncated image: the rest reads as zeros
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
