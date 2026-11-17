using System.Buffers.Binary;
using System.Text;
using static FsForge.Core.ExFat.ExFatFormat;

namespace FsForge.Core.ExFat;

/// <summary>
/// Reader for exFAT (SD cards over 32 GB, USB sticks, cameras). Like FAT, exFAT has no owners, permissions,
/// links or devices: files are 0644 (0444 when read-only), directories 0755, everything belongs to root.
/// Times are the last-modified times of the entries, converted to UTC with their UTC offset.
/// </summary>
public sealed class ExFatReader : IDisposable
{
    private readonly IBlockSource _source;
    private readonly bool _ownsSource;
    private uint[] _fat = Array.Empty<uint>();
    private int _bps, _clusterSize;
    private long _heap, _clusters;
    private long _contiguous;

    private ExFatReader(IBlockSource source, bool ownsSource)
    {
        _source = source;
        _ownsSource = ownsSource;
        Info = null!;
    }

    public ExFatInfo Info { get; private set; }
    public List<string> Warnings { get; } = new();

    // ---------------------------------------------------------- detection

    private readonly record struct Layout(int BpsShift, int SpcShift, long VolumeLength, long FatOffset, long FatLength,
        long HeapOffset, long Clusters, uint RootCluster, int Fats);

    /// <summary>An exFAT main boot sector (or, when it is damaged, a backup one) with a consistent layout.</summary>
    public static bool IsExFat(IBlockSource source)
    {
        if (source.Length < 512) return false;
        byte[] s = source.ReadBytes(0, 512);
        if (HasSignature(s) && Parse(s, source.Length, out _)) return true;
        // The backup boot sector: 12 sectors further on, whatever the sector size.
        for (int shift = 9; shift <= 12; shift++)
        {
            long at = 12L << shift;
            if (source.Length < at + 512) break;
            byte[] b = source.ReadBytes(at, 512);
            if (HasSignature(b) && b[108] == shift && Parse(b, source.Length, out _)) return true;
        }
        return false;
    }

    private static bool Parse(ReadOnlySpan<byte> s, long fileLength, out Layout l)
    {
        l = default;
        if (s[510] != 0x55 || s[511] != 0xAA) return false;
        // MustBeZero: where FAT keeps its parameter block.
        if (s.Slice(11, 53).IndexOfAnyExcept((byte)0) >= 0) return false;
        int bpsShift = s[108], spcShift = s[109], fats = s[110];
        if (bpsShift is < 9 or > 12 || spcShift > 25 - bpsShift || fats is not (1 or 2)) return false;
        long volumeLength = (long)BinaryPrimitives.ReadUInt64LittleEndian(s[72..]);
        long fatOffset = BinaryPrimitives.ReadUInt32LittleEndian(s[80..]);
        long fatLength = BinaryPrimitives.ReadUInt32LittleEndian(s[84..]);
        long heapOffset = BinaryPrimitives.ReadUInt32LittleEndian(s[88..]);
        long clusters = BinaryPrimitives.ReadUInt32LittleEndian(s[92..]);
        uint root = BinaryPrimitives.ReadUInt32LittleEndian(s[96..]);
        if (fatOffset < 24 || fatLength == 0 || heapOffset < fatOffset + fatLength * fats || clusters == 0 || clusters > MaxClusters) return false;
        if (volumeLength < heapOffset + (clusters << spcShift) || volumeLength >= 1L << (63 - bpsShift)) return false;
        if (((clusters + 2) * 4 + (1L << bpsShift) - 1) >> bpsShift > fatLength) return false;
        if (root < 2 || root > clusters + 1) return false;
        // The metadata must lie in the file (a truncated tail of the cluster heap is tolerated).
        if ((heapOffset << bpsShift) > fileLength) return false;
        l = new Layout(bpsShift, spcShift, volumeLength, fatOffset, fatLength, heapOffset, clusters, root, fats);
        return true;
    }

    public static ExFatReader Open(IBlockSource source, bool ownsSource = true)
    {
        var r = new ExFatReader(source, ownsSource);
        try
        {
            r.ReadBootRegion();
            return r;
        }
        catch
        {
            if (ownsSource) source.Dispose();
            throw;
        }
    }

    /// <summary>Checks a boot region (12 sectors): signatures of the extended boot sectors and the checksum sector.</summary>
    private static bool RegionValid(byte[] region, int bps)
    {
        uint sum = BootChecksum(region.AsSpan(0, 11 * bps));
        for (int i = 11 * bps; i < 12 * bps; i += 4)
            if (BinaryPrimitives.ReadUInt32LittleEndian(region.AsSpan(i)) != sum) return false;
        return true;
    }

    private void ReadBootRegion()
    {
        if (_source.Length < 512) throw new FsException("The file is too small to be an exFAT filesystem.");
        byte[] s = _source.ReadBytes(0, 512);
        bool backup = false;
        Layout l = default;
        byte[]? region = null;
        if (HasSignature(s) && Parse(s, _source.Length, out l))
        {
            int bps = 1 << l.BpsShift;
            region = _source.ReadBytes(0, 12 * bps);
            if (!RegionValid(region, bps))
            {
                byte[] copy = _source.ReadBytes(12L * bps, 12 * bps);
                if (HasSignature(copy) && Parse(copy, _source.Length, out Layout bl) && RegionValid(copy, 1 << bl.BpsShift))
                {
                    Warnings.Add("The checksum of the main exFAT boot region does not match; the backup boot region was used.");
                    region = copy;
                    l = bl;
                    backup = true;
                }
                else
                {
                    Warnings.Add("The checksum of the exFAT boot region does not match (and the backup does not help); the boot sector was used as it is.");
                }
            }
        }
        else
        {
            for (int shift = 9; shift <= 12 && region is null; shift++)
            {
                long at = 12L << shift;
                if (_source.Length < at + (12L << shift)) break;
                byte[] copy = _source.ReadBytes(at, 12 << shift);
                if (HasSignature(copy) && copy[108] == shift && Parse(copy, _source.Length, out l))
                {
                    region = copy;
                    backup = true;
                    Warnings.Add("The main exFAT boot sector is damaged; the backup boot region was used.");
                }
            }
            if (region is null) throw new FsException("Not an exFAT filesystem (invalid boot sector).");
        }

        _bps = 1 << l.BpsShift;
        _clusterSize = 1 << (l.BpsShift + l.SpcShift);
        _clusters = l.Clusters;
        _heap = l.HeapOffset * _bps;
        int flags = BinaryPrimitives.ReadUInt16LittleEndian(region.AsSpan(106));
        bool dirty = (flags & 2) != 0;
        if (dirty) Warnings.Add("The exFAT volume was not cleanly unmounted (its VolumeDirty flag is set). It is read as it is; fsck.exfat would check it.");
        if ((flags & 4) != 0) Warnings.Add("The exFAT volume reports media failures (its MediaFailure flag is set).");
        if (region[104] > 1 && region[105] != 1) Warnings.Add($"The exFAT revision is {region[105]}.{region[104]:D2}; it is read as revision 1.00.");
        if (_source.Length < _heap + _clusters * _clusterSize)
            Warnings.Add("The file is shorter than the exFAT filesystem it holds; the missing part reads as zeros.");

        // TexFAT keeps two FATs; bit 0 of VolumeFlags tells which one is active.
        int active = l.Fats == 2 && (flags & 1) != 0 ? 1 : 0;
        ReadFat((l.FatOffset + active * l.FatLength) * _bps);

        // Root directory: the label, the allocation bitmap and the up-case table live there.
        byte[] root = ReadChainBytes(l.RootCluster, -1, false, "/");
        string label = "";
        long free = -1;
        for (int i = 0; i + EntrySize <= root.Length; i += EntrySize)
        {
            byte type = root[i];
            if (type == 0) break;
            if (type == TypeLabel)
            {
                int n = Math.Min((int)root[i + 1], MaxLabelLength);
                label = Encoding.Unicode.GetString(root, i + 2, n * 2);
            }
            else if (type == TypeBitmap && free < 0 && (root[i + 1] & 1) == active)
            {
                free = CountFree(BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(i + 20)),
                    (long)BinaryPrimitives.ReadUInt64LittleEndian(root.AsSpan(i + 24)));
            }
        }
        if (free < 0) Warnings.Add("The exFAT root directory has no allocation bitmap; the free space is unknown.");

        Info = new ExFatInfo
        {
            SectorSize = _bps,
            ClusterSize = _clusterSize,
            TotalSectors = l.VolumeLength,
            Clusters = _clusters,
            FreeClusters = Math.Max(0, free),
            Fats = l.Fats,
            FatOffset = l.FatOffset,
            FatLength = l.FatLength,
            ClusterHeapOffset = l.HeapOffset,
            PartitionOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(region.AsSpan(64)),
            Label = label,
            VolumeSerial = BinaryPrimitives.ReadUInt32LittleEndian(region.AsSpan(100)),
            Revision = $"{region[105]}.{region[104]:D2}",
            BootCode = region.AsSpan(120, 390).ToArray(),
            Dirty = dirty,
            UsedBackupBootRegion = backup,
        };
        _rootCluster = l.RootCluster;
    }

    private uint _rootCluster;

    private void ReadFat(long offset)
    {
        if (_clusters > 256L << 20) throw new FsException("The exFAT filesystem has more clusters than this program reads (268 million).");
        byte[] raw = new byte[(_clusters + 2) * 4];
        if (_source.Read(offset, raw) < raw.Length) throw new FsException("The exFAT filesystem is truncated: its FAT cannot be read.");
        _fat = new uint[_clusters + 2];
        for (long c = 0; c < _fat.Length; c++) _fat[c] = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan((int)(c * 4)));
    }

    private long CountFree(uint first, long bytes)
    {
        if (first < 2 || first > _clusters + 1 || bytes < (_clusters + 7) / 8) return -1;
        byte[] bitmap = ReadChainBytes(first, (_clusters + 7) / 8, false, "the allocation bitmap");
        long used = 0;
        for (long c = 0; c < _clusters && c / 8 < bitmap.Length; c++)
            if ((bitmap[c / 8] & (1 << (int)(c % 8))) != 0) used++;
        return _clusters - used;
    }

    // ---------------------------------------------------------- clusters

    private long ClusterOffset(uint c) => _heap + (long)(c - 2) * _clusterSize;

    /// <summary>
    /// The clusters of a stream: <paramref name="count"/> clusters from <paramref name="first"/> on when they are
    /// contiguous (NoFatChain), otherwise the FAT chain; a chain stops (with a warning) at a free, bad,
    /// out-of-range or looping link. A count of -1 follows the chain to its end (the root directory).
    /// </summary>
    private List<uint> Clusters(uint first, long count, bool contiguous, string path)
    {
        var list = new List<uint>();
        if (first == 0 || count == 0) return list;
        if (contiguous)
        {
            if (first < 2 || first + count - 2 > _clusters)
            {
                Warnings.Add($"'{path}': its clusters lie outside the filesystem; it was cut.");
                count = Math.Max(0, _clusters + 2 - first);
            }
            for (long i = 0; i < count; i++) list.Add((uint)(first + i));
            return list;
        }
        var seen = new HashSet<uint>();
        uint cur = first;
        while (true)
        {
            if (cur < 2 || cur >= _clusters + 2)
            {
                Warnings.Add($"'{path}': its cluster chain points outside the filesystem and was cut.");
                break;
            }
            if (!seen.Add(cur) || (count >= 0 && list.Count >= count) || list.Count > (256 << 20) / 4)
            {
                if (count < 0 || list.Count < count) Warnings.Add($"'{path}': its cluster chain loops and was cut.");
                break;
            }
            list.Add(cur);
            if (count >= 0 && list.Count == count) break;
            uint next = _fat[cur];
            if (next >= 0xFFFFFFF8) break;
            if (next < 2 || next == 0xFFFFFFF7)
            {
                Warnings.Add($"'{path}': its cluster chain ends in a free or bad cluster and was cut.");
                break;
            }
            cur = next;
        }
        if (count > 0 && list.Count < count && list.Count > 0 && _fat[list[^1]] >= 0xFFFFFFF8)
            Warnings.Add($"'{path}': its cluster chain is shorter than the data; the rest was cut.");
        return list;
    }

    private byte[] ReadChainBytes(uint first, long bytes, bool contiguous, string path)
    {
        long count = bytes < 0 ? -1 : (bytes + _clusterSize - 1) / _clusterSize;
        if (bytes > 256L << 20) throw new FsException($"Corrupt exFAT filesystem: {path} claims {bytes} bytes.");
        List<uint> chain = Clusters(first, count, contiguous, path);
        long length = bytes < 0 ? (long)chain.Count * _clusterSize : Math.Min(bytes, (long)chain.Count * _clusterSize);
        byte[] data = new byte[length];
        for (int i = 0; i < chain.Count && (long)i * _clusterSize < length; i++)
        {
            int n = (int)Math.Min(_clusterSize, length - (long)i * _clusterSize);
            _source.Read(ClusterOffset(chain[i]), data.AsSpan(i * _clusterSize, n));
        }
        return data;
    }

    // -------------------------------------------------------- directories

    private sealed record Entry(string Name, ushort Attr, uint Cluster, long DataLength, long ValidLength, bool Contiguous, long MTime);

    private List<Entry> ParseDirectory(byte[] d, string path)
    {
        var entries = new List<Entry>();
        for (int i = 0; i + EntrySize <= d.Length; i += EntrySize)
        {
            byte type = d[i];
            if (type == 0) break;
            if ((type & 0x80) == 0) continue;                 // unused (deleted) entry
            if (type != TypeFile)
            {
                // Benign primary entries (bitmap, up-case table, label, GUID, padding) carry no files; an unknown critical
                // primary entry means a later version of the format.
                if ((type & 0x40) == 0 && (type & 0x20) == 0 && type is not (TypeBitmap or TypeUpcase or TypeLabel))
                    Warnings.Add($"'{path}' holds a directory entry of an unknown kind (0x{type:X2}); it was skipped.");
                continue;
            }
            int secondary = d[i + 1];
            int setLength = (secondary + 1) * EntrySize;
            if (secondary < 2 || secondary > 18 || i + setLength > d.Length)
            {
                Warnings.Add($"'{path}' holds a damaged file entry; it was skipped.");
                continue;
            }
            ReadOnlySpan<byte> set = d.AsSpan(i, setLength);
            ushort stored = BinaryPrimitives.ReadUInt16LittleEndian(set[2..]);
            ReadOnlySpan<byte> stream = set.Slice(EntrySize, EntrySize);
            if (stream[0] != TypeStream)
            {
                Warnings.Add($"'{path}' holds a file entry without its stream extension; it was skipped.");
                continue;
            }
            int nameLength = stream[3];
            var name = new StringBuilder(nameLength);
            for (int k = 2; k <= secondary && name.Length < nameLength; k++)
            {
                ReadOnlySpan<byte> e = set.Slice(k * EntrySize, EntrySize);
                if (e[0] != TypeName) continue;
                for (int c = 0; c < 15 && name.Length < nameLength; c++) name.Append((char)BinaryPrimitives.ReadUInt16LittleEndian(e[(2 + c * 2)..]));
            }
            string n = name.ToString();
            if (SetChecksum(set) != stored)
            {
                Warnings.Add($"The directory entry of '{(path == "/" ? "" : path)}/{n}' has a wrong checksum; it was skipped.");
                i += setLength - EntrySize;
                continue;
            }
            ushort attr = BinaryPrimitives.ReadUInt16LittleEndian(set[4..]);
            byte flags = stream[1];
            long mtime = ToUnix(BinaryPrimitives.ReadUInt32LittleEndian(set[12..]), set[21], set[23]);
            entries.Add(new Entry(n, attr, BinaryPrimitives.ReadUInt32LittleEndian(stream[20..]),
                (long)BinaryPrimitives.ReadUInt64LittleEndian(stream[24..]), (long)BinaryPrimitives.ReadUInt64LittleEndian(stream[8..]),
                (flags & FlagNoFatChain) != 0, mtime));
            i += setLength - EntrySize;
        }
        return entries;
    }

    // --------------------------------------------------------------- tree

    /// <summary>Builds the tree. File data stays in the image and is read on demand.</summary>
    public FsTree ReadTree(IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var tree = new FsTree(0x1ED, 0);
        var owners = new Dictionary<uint, string>();             // first cluster -> who owns it, to find cross-links
        var stack = new Stack<(FsNode Dir, byte[] Data)>();
        stack.Push((tree.Root, ReadChainBytes(_rootCluster, -1, false, "/")));
        long done = 0;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            (FsNode dir, byte[] data) = stack.Pop();
            foreach (Entry e in ParseDirectory(data, dir.Path))
            {
                string path = dir.IsRoot ? "/" + e.Name : dir.Path + "/" + e.Name;
                if (e.Name.Length == 0 || e.Name.Contains('/') || e.Name is "." or ".." || dir.Find(e.Name) is not null)
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
                if (e.Contiguous) _contiguous++;
                bool isDir = (e.Attr & AttrDirectory) != 0;
                var inode = new FsInode
                {
                    Type = isDir ? FsNodeType.Directory : FsNodeType.File,
                    Mode = isDir ? 0x1ED : (e.Attr & AttrReadOnly) != 0 ? 0x124 : 0x1A4,
                    MTime = e.MTime,
                };
                if (isDir)
                {
                    if (e.Cluster == 0 || e.DataLength <= 0)
                    {
                        Warnings.Add($"Directory '{path}' has no valid cluster and was skipped.");
                        continue;
                    }
                    FsNode sub = tree.Add(dir, e.Name, inode);
                    stack.Push((sub, ReadChainBytes(e.Cluster, e.DataLength, e.Contiguous, path)));
                }
                else
                {
                    if (e.DataLength > 0)
                    {
                        long need = (e.DataLength + _clusterSize - 1) / _clusterSize;
                        List<uint> chain = e.Cluster == 0 ? new List<uint>() : Clusters(e.Cluster, need, e.Contiguous, path);
                        long size = Math.Min(e.DataLength, (long)chain.Count * _clusterSize);
                        if (size < e.DataLength) Warnings.Add($"'{path}' is {e.DataLength} bytes long but its clusters hold only {size}; the rest was cut.");
                        // Past ValidDataLength the file reads as zeros, whatever its clusters hold.
                        inode.Data = new ExFatFileData(this, chain, size, Math.Clamp(e.ValidLength, 0, size));
                    }
                    tree.Add(dir, e.Name, inode);
                }
                if ((++done & 0x3FF) == 0) progress?.Report(new ProgressInfo("read", done, 0, path));
            }
        }
        progress?.Report(new ProgressInfo("read", done, done));
        return tree;
    }

    /// <summary>Files and directories kept as one run of clusters without a FAT chain (known after <see cref="ReadTree"/>).</summary>
    public long ContiguousFiles => _contiguous;

    // ---------------------------------------------------------- file data

    private sealed class ExFatFileData(ExFatReader reader, List<uint> chain, long length, long valid) : IFileData
    {
        public long Length => length;
        public Stream OpenRead() => new ExFatFileStream(reader, chain, length, valid);
    }

    private sealed class ExFatFileStream(ExFatReader r, List<uint> chain, long length, long valid) : Stream
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
            if (_position >= valid)
            {
                int z = (int)Math.Min(buffer.Length, left);
                buffer[..z].Clear();
                _position += z;
                return z;
            }
            int cs = r._clusterSize;
            int index = (int)(_position / cs), inCluster = (int)(_position % cs);
            // Read across clusters that follow each other on the disk in one go.
            int run = 1;
            while (index + run < chain.Count && chain[index + run] == chain[index] + run && (long)run * cs < 1 << 20) run++;
            int n = (int)Math.Min(Math.Min(buffer.Length, valid - _position), (long)run * cs - inCluster);
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
