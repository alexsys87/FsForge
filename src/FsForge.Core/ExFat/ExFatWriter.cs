using System.Buffers.Binary;
using System.Text;
using static FsForge.Core.ExFat.ExFatFormat;

namespace FsForge.Core.ExFat;

/// <summary>
/// Writes a tree as an exFAT filesystem, laid out as mkfs.exfat and Windows lay it out: boot region and its
/// backup, one FAT, the cluster heap starting with the allocation bitmap, the up-case table and the root
/// directory. Every file and directory is one contiguous run of clusters, also chained in the FAT so that every
/// driver reads it. Only files and directories can be stored; owners and permissions are not part of exFAT (a file
/// without write permission becomes read-only), symbolic links, devices and FIFOs are left out with a warning,
/// and hard links become separate copies.
/// </summary>
public static class ExFatWriter
{
    public static IReadOnlyList<string> Write(FsTree tree, Stream output, ExFatOptions options,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        options.Validate();
        var w = new Writer(options);
        w.Plan(tree);
        w.Layout();
        w.Emit(output, progress, ct);
        return w.Warnings;
    }

    private sealed class Child
    {
        public required FsNode Node;
        public uint Cluster;
        public long Clusters;
        public long Length;
    }

    private sealed class Dir
    {
        public required FsNode Node;
        public Dir? Parent;
        public readonly List<Child> Children = new();
        public readonly List<Dir> Subdirs = new();
        public int EntryCount;
        public uint Cluster;
        public long Clusters;
    }

    private sealed class Writer(ExFatOptions o)
    {
        private readonly int _bps = o.SectorSize;
        private Dir _root = null!;
        private readonly List<string> _skipped = new();
        private int _extraLinks;
        private readonly HashSet<FsInode> _seenFiles = new(ReferenceEqualityComparer.Instance);
        private readonly byte[] _upcase = UpcaseTable;

        // Geometry, in sectors.
        private int _spc;
        private long _total, _fatOffset, _fatLength, _heap, _clusters;
        private long _bitmapBytes, _bitmapClusters, _upcaseClusters;

        public List<string> Warnings { get; } = new();

        private int ClusterBytes => _spc * _bps;

        // ---------------------------------------------------------- plan

        public void Plan(FsTree tree)
        {
            _root = PlanDir(tree.Root, null);
            if (_skipped.Count > 0)
                Warnings.Add($"{_skipped.Count} object(s) cannot be stored in exFAT (symbolic links, devices, FIFOs, sockets) and were left out: " +
                             string.Join(", ", _skipped.Take(5)) + (_skipped.Count > 5 ? ", ..." : "") + ".");
            if (_extraLinks > 0)
                Warnings.Add($"exFAT has no hard links: {_extraLinks} additional name(s) of hard-linked files were stored as separate copies.");
        }

        private static int SetEntries(string name) => 2 + (name.Length + 14) / 15;

        private Dir PlanDir(FsNode node, Dir? parent)
        {
            var dir = new Dir { Node = node, Parent = parent };
            var names = new Dictionary<string, string>(StringComparer.Ordinal);       // up-cased -> name: exFAT ignores case
            foreach (FsNode c in node.SortedChildren())
            {
                if (c.Type is not (FsNodeType.File or FsNodeType.Directory))
                {
                    _skipped.Add(c.Path);
                    continue;
                }
                if (NameProblem(c.Name) is { } why)
                    throw new FsException($"'{c.Path}' cannot be stored in exFAT: {why}. Rename it first.");
                string upper = new(c.Name.Select(ch => (char)Upcase(ch)).ToArray());
                if (names.TryGetValue(upper, out string? other))
                    throw new FsException($"'{c.Path}' and '{other}' differ only in case, which exFAT does not distinguish. Rename one of them first.");
                names[upper] = c.Name;
                if (c.Type == FsNodeType.File && !_seenFiles.Add(c.Inode)) _extraLinks++;
                dir.Children.Add(new Child { Node = c });
            }
            dir.EntryCount = dir.Children.Sum(c => SetEntries(c.Node.Name))
                             + (parent is null ? 2 + (o.Label.Length > 0 ? 1 : 0) : 0);     // bitmap and up-case table in the root
            foreach (Child c in dir.Children)
                if (c.Node.IsDirectory) dir.Subdirs.Add(PlanDir(c.Node, dir));
            return dir;
        }

        private IEnumerable<Dir> AllDirs()
        {
            var stack = new Stack<Dir>();
            stack.Push(_root);
            while (stack.Count > 0)
            {
                Dir d = stack.Pop();
                yield return d;
                foreach (Dir s in d.Subdirs) stack.Push(s);
            }
        }

        // -------------------------------------------------------- layout

        private static long CeilDiv(long a, long b) => (a + b - 1) / b;

        /// <summary>Clusters the content takes with clusters of <paramref name="bytes"/> bytes (directories and files).</summary>
        private long ContentClusters(int bytes)
        {
            long n = 0;
            foreach (Dir d in AllDirs())
            {
                n += Math.Max(1, CeilDiv((long)d.EntryCount * EntrySize, bytes));
                foreach (Child c in d.Children)
                    if (!c.Node.IsDirectory) n += CeilDiv(c.Node.Inode.Size, bytes);
            }
            return n;
        }

        /// <summary>
        /// The layout of a volume of <paramref name="total"/> sectors: the FAT halfway to the alignment boundary and the
        /// cluster heap on it, as Windows and the SD card formatter place them; null when it does not fit.
        /// </summary>
        private bool TryGeometry(long total, int spc)
        {
            long bytes = total * _bps;
            long align = Math.Max(spc, (bytes >= 64L << 20 ? 1L << 20 : 32L << 10) / _bps);
            long fatOffset = Math.Max(BootRegionSectors * 2, align / 2);
            for (int round = 0; round < 2; round++)
            {
                long clustersGuess = (total - fatOffset) / spc;
                long fatLength = CeilDiv((clustersGuess + 2) * 4, _bps);
                long heap = (fatOffset + fatLength + align - 1) / align * align;
                if (heap >= total)
                {
                    if (round == 0)
                    {
                        // Small volume: give up the alignment rather than the filesystem.
                        align = spc;
                        fatOffset = BootRegionSectors * 2;
                        continue;
                    }
                    return false;
                }
                long clusters = (total - heap) / spc;
                if (clusters <= 0 || clusters > MaxClusters || heap > uint.MaxValue) return false;
                _spc = spc;
                _total = total;
                _fatOffset = fatOffset;
                _fatLength = fatLength;
                _heap = heap;
                _clusters = clusters;
                _bitmapBytes = CeilDiv(clusters, 8);
                _bitmapClusters = CeilDiv(_bitmapBytes, (long)spc * _bps);
                _upcaseClusters = CeilDiv(_upcase.Length, (long)spc * _bps);
                return true;
            }
            return false;
        }

        private long SystemClusters => _bitmapClusters + _upcaseClusters;

        private IEnumerable<int> ClusterCandidates(long bytes)
        {
            if (o.ClusterSize > 0)
            {
                yield return o.ClusterSize;
                yield break;
            }
            int d = Math.Max(_bps, DefaultClusterSize(bytes));
            yield return d;
            for (int c = d * 2; c <= 32 << 20; c *= 2) yield return c;
        }

        public void Layout()
        {
            if (o.SizeBytes > 0)
            {
                long total = o.SizeBytes / _bps;
                foreach (int cluster in ClusterCandidates(o.SizeBytes))
                {
                    if (!TryGeometry(total, cluster / _bps)) continue;
                    long need = SystemClusters + ContentClusters(cluster);
                    if (need > _clusters)
                        throw new FsNoSpaceException($"The content does not fit into a {SizeText(o.SizeBytes)} exFAT filesystem: it needs {need} clusters of {cluster} bytes, there are {_clusters}.");
                    return;
                }
                throw new FsException($"An exFAT filesystem of {SizeText(o.SizeBytes)} cannot be made{(o.ClusterSize > 0 ? $" with {o.ClusterSize}-byte clusters" : "")}.");
            }

            // As small as the content allows, plus the requested free space, rounded up to whole MiB.
            int cs = o.ClusterSize > 0 ? o.ClusterSize : Math.Max(_bps, DefaultClusterSize((ContentClusters(4096) * 4096L) + o.ExtraFreeBytes));
            int spc = cs / _bps;
            long content = ContentClusters(cs) + CeilDiv(o.ExtraFreeBytes, cs);
            long guess = (content + CeilDiv(_upcase.Length, cs) + 2) * spc + 2048;
            long mib = (1L << 20) / _bps;
            for (long total = (guess + mib - 1) / mib * mib; total < long.MaxValue / 2; total += Math.Max(mib, total / 64))
            {
                if (TryGeometry(total, spc) && SystemClusters + content <= _clusters) return;
            }
            throw new FsException("No exFAT layout can hold the content.");
        }

        private static string SizeText(long bytes) => bytes >= 1L << 20 && bytes % (1L << 20) == 0 ? $"{bytes >> 20} MiB" : $"{bytes} bytes";

        // ---------------------------------------------------------- emit

        private uint _next = 2;

        private uint Allocate(long count)
        {
            if (count == 0) return 0;
            if (_next - 2 + count > _clusters)
                throw new FsNoSpaceException($"The content does not fit into the exFAT filesystem ({_clusters} clusters of {ClusterBytes} bytes).");
            uint first = _next;
            _next += (uint)count;
            return first;
        }

        public void Emit(Stream output, IProgress<ProgressInfo>? progress, CancellationToken ct)
        {
            int cs = ClusterBytes;
            uint bitmapCluster = Allocate(_bitmapClusters), upcaseCluster = Allocate(_upcaseClusters);
            // Allocation in the order the data is written: each directory, then its files, then its subdirectories.
            var order = new List<(Dir? Dir, Child? File)>();
            void Assign(Dir d)
            {
                d.Clusters = Math.Max(1, CeilDiv((long)d.EntryCount * EntrySize, cs));
                d.Cluster = Allocate(d.Clusters);
                order.Add((d, null));
                foreach (Child c in d.Children)
                {
                    if (c.Node.IsDirectory) continue;
                    c.Length = c.Node.Inode.Size;
                    c.Clusters = CeilDiv(c.Length, cs);
                    c.Cluster = Allocate(c.Clusters);
                    if (c.Clusters > 0) order.Add((null, c));
                }
                foreach (Dir s in d.Subdirs)
                {
                    Assign(s);
                    Child c = d.Children.First(x => ReferenceEquals(x.Node, s.Node));
                    c.Cluster = s.Cluster;
                    c.Clusters = s.Clusters;
                    c.Length = s.Clusters * cs;
                }
            }
            Assign(_root);
            long used = _next - 2;

            // The FAT: every allocation is one contiguous chain.
            byte[] fat = new byte[_fatLength * _bps];
            BinaryPrimitives.WriteUInt32LittleEndian(fat, 0xFFFFFFF8);
            BinaryPrimitives.WriteUInt32LittleEndian(fat.AsSpan(4), 0xFFFFFFFF);
            void Chain(uint first, long count)
            {
                for (long i = 0; i < count; i++)
                    BinaryPrimitives.WriteUInt32LittleEndian(fat.AsSpan((int)((first + i) * 4)), i == count - 1 ? 0xFFFFFFFF : (uint)(first + i + 1));
            }
            Chain(bitmapCluster, _bitmapClusters);
            Chain(upcaseCluster, _upcaseClusters);
            foreach ((Dir? d, Child? f) in order) Chain(d?.Cluster ?? f!.Cluster, d?.Clusters ?? f!.Clusters);

            // Boot region and its backup, then the FAT.
            byte[] boot = BootRegion(used);
            output.Write(boot);
            output.Write(boot);
            WriteZeros(output, (_fatOffset - 2 * BootRegionSectors) * _bps);
            output.Write(fat);
            WriteZeros(output, (_heap - _fatOffset - _fatLength) * _bps);

            // Cluster heap: the allocation bitmap, the up-case table, then directories and files in allocation order.
            byte[] bitmap = new byte[_bitmapClusters * cs];
            for (long c = 0; c < used; c++) bitmap[c / 8] |= (byte)(1 << (int)(c % 8));
            output.Write(bitmap);
            byte[] up = new byte[_upcaseClusters * cs];
            _upcase.CopyTo(up, 0);
            output.Write(up);

            long done = 0, totalBytes = Math.Max(1, order.Sum(x => x.File?.Length ?? 0));
            byte[] buffer = new byte[1 << 20];
            foreach ((Dir? d, Child? f) in order)
            {
                ct.ThrowIfCancellationRequested();
                if (d is not null)
                {
                    byte[] padded = new byte[d.Clusters * cs];
                    DirectoryEntries(d, bitmapCluster, upcaseCluster).CopyTo(padded, 0);
                    output.Write(padded);
                    continue;
                }
                FsInode inode = f!.Node.Inode;
                using Stream s = inode.Data!.OpenRead();
                long left = f.Length;
                while (left > 0)
                {
                    int n = (int)Math.Min(buffer.Length, left);
                    s.ReadExactly(buffer, 0, n);
                    output.Write(buffer, 0, n);
                    left -= n;
                    done += n;
                }
                if (s.ReadByte() >= 0) throw new FsException($"'{f.Node.Path}' changed while it was being written.");
                long pad = f.Clusters * cs - f.Length;
                if (pad > 0) WriteZeros(output, pad);
                progress?.Report(new ProgressInfo("data", done, totalBytes, f.Node.Path));
            }
            WriteZeros(output, (_total - _heap) * _bps - used * cs);
            progress?.Report(new ProgressInfo("done", totalBytes, totalBytes));
        }

        private static void WriteZeros(Stream output, long count)
        {
            byte[] zeros = new byte[1 << 20];
            while (count > 0)
            {
                int n = (int)Math.Min(count, zeros.Length);
                output.Write(zeros, 0, n);
                count -= n;
            }
        }

        // ------------------------------------------------ boot region

        private byte[] BootRegion(long used)
        {
            byte[] r = new byte[BootRegionSectors * _bps];
            Span<byte> s = r.AsSpan(0, _bps);
            s[0] = 0xEB;
            s[1] = 0x76;
            s[2] = 0x90;
            Signature.CopyTo(s[3..]);
            BinaryPrimitives.WriteUInt64LittleEndian(s[64..], (ulong)o.PartitionOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(s[72..], (ulong)_total);
            BinaryPrimitives.WriteUInt32LittleEndian(s[80..], (uint)_fatOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(s[84..], (uint)_fatLength);
            BinaryPrimitives.WriteUInt32LittleEndian(s[88..], (uint)_heap);
            BinaryPrimitives.WriteUInt32LittleEndian(s[92..], (uint)_clusters);
            BinaryPrimitives.WriteUInt32LittleEndian(s[96..], _root.Cluster);
            BinaryPrimitives.WriteUInt32LittleEndian(s[100..], o.VolumeSerial ?? (uint)Random.Shared.NextInt64(1, uint.MaxValue));
            BinaryPrimitives.WriteUInt16LittleEndian(s[104..], 0x0100);         // revision 1.00
            s[108] = (byte)System.Numerics.BitOperations.Log2((uint)_bps);
            s[109] = (byte)System.Numerics.BitOperations.Log2((uint)_spc);
            s[110] = 1;                                                         // one FAT
            s[111] = 0x80;                                                      // drive select
            s[112] = (byte)Math.Min(100, used * 100 / _clusters);               // percent in use
            if (o.BootCode is { Length: 390 } code) code.CopyTo(s[120..]);
            else s[120..510].Fill(0xF4);                                        // boot code: hlt
            s[510] = 0x55;
            s[511] = 0xAA;
            // Extended boot sectors 1-8 carry only their signature; 9 and 10 (OEM parameters, reserved) stay empty.
            for (int i = 1; i <= 8; i++) BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan((i + 1) * _bps - 4), 0xAA550000);
            uint sum = BootChecksum(r.AsSpan(0, 11 * _bps));
            for (int i = 11 * _bps; i < 12 * _bps; i += 4) BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(i), sum);
            return r;
        }

        // ------------------------------------------------ directories

        private byte[] DirectoryEntries(Dir d, uint bitmapCluster, uint upcaseCluster)
        {
            var e = new MemoryStream(d.EntryCount * EntrySize);
            if (d.Parent is null)
            {
                if (o.Label.Length > 0)
                {
                    byte[] label = new byte[EntrySize];
                    label[0] = TypeLabel;
                    label[1] = (byte)o.Label.Length;
                    Encoding.Unicode.GetBytes(o.Label).CopyTo(label, 2);
                    e.Write(label);
                }
                byte[] bitmap = new byte[EntrySize];
                bitmap[0] = TypeBitmap;
                BinaryPrimitives.WriteUInt32LittleEndian(bitmap.AsSpan(20), bitmapCluster);
                BinaryPrimitives.WriteUInt64LittleEndian(bitmap.AsSpan(24), (ulong)_bitmapBytes);
                e.Write(bitmap);
                byte[] upcase = new byte[EntrySize];
                upcase[0] = TypeUpcase;
                BinaryPrimitives.WriteUInt32LittleEndian(upcase.AsSpan(4), TableChecksum(_upcase));
                BinaryPrimitives.WriteUInt32LittleEndian(upcase.AsSpan(20), upcaseCluster);
                BinaryPrimitives.WriteUInt64LittleEndian(upcase.AsSpan(24), (ulong)_upcase.Length);
                e.Write(upcase);
            }
            foreach (Child c in d.Children) e.Write(EntrySet(c));
            return e.ToArray();
        }

        private static byte[] EntrySet(Child c)
        {
            string name = c.Node.Name;
            int count = SetEntries(name);
            byte[] r = new byte[count * EntrySize];
            FsInode i = c.Node.Inode;
            bool dir = c.Node.IsDirectory;
            ushort attr = dir ? AttrDirectory : (ushort)(AttrArchive | ((i.Mode & 0x92) == 0 ? AttrReadOnly : 0));
            (uint stamp, byte tenMs, byte offset) = FromUnix(i.MTime);

            r[0] = TypeFile;
            r[1] = (byte)(count - 1);
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(4), attr);
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(8), stamp);      // created
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(12), stamp);     // last modified
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(16), stamp);     // last accessed
            r[20] = tenMs;
            r[21] = tenMs;
            r[22] = offset;
            r[23] = offset;
            r[24] = offset;

            Span<byte> s = r.AsSpan(EntrySize, EntrySize);
            s[0] = TypeStream;
            s[1] = FlagAllocationPossible;
            s[3] = (byte)name.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(s[4..], NameHash(name));
            BinaryPrimitives.WriteUInt64LittleEndian(s[8..], (ulong)c.Length);     // valid data length
            BinaryPrimitives.WriteUInt32LittleEndian(s[20..], c.Cluster);
            BinaryPrimitives.WriteUInt64LittleEndian(s[24..], (ulong)c.Length);

            for (int k = 0; k * 15 < name.Length; k++)
            {
                Span<byte> n = r.AsSpan((2 + k) * EntrySize, EntrySize);
                n[0] = TypeName;
                for (int j = 0; j < 15 && k * 15 + j < name.Length; j++) BinaryPrimitives.WriteUInt16LittleEndian(n[(2 + j * 2)..], name[k * 15 + j]);
            }
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(2), SetChecksum(r));
            return r;
        }
    }
}
