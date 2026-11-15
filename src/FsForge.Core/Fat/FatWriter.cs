using System.Text;
using static FsForge.Core.Fat.FatFormat;

namespace FsForge.Core.Fat;

/// <summary>
/// Writes a tree as a FAT12/16/32 filesystem with long (VFAT) names, the way mkfs.fat followed by copying
/// the files would. Only files and directories can be stored; owners and permissions are not part of FAT
/// (a file without write permission becomes read-only), symbolic links, devices and FIFOs are left out
/// with a warning, and hard links become separate copies.
/// </summary>
public static class FatWriter
{
    public static IReadOnlyList<string> Write(FsTree tree, Stream output, FatOptions options,
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
        public required string Long;
        public string Short = "";
        public bool NeedLfn;
        public uint Cluster;
        public long Clusters;
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
        public byte[] Entries = Array.Empty<byte>();
    }

    private sealed class Writer(FatOptions o)
    {
        private readonly int _bps = o.SectorSize;
        private Dir _root = null!;
        private readonly List<string> _skipped = new();
        private int _extraLinks;
        private readonly HashSet<FsInode> _seenFiles = new(ReferenceEqualityComparer.Instance);

        // Geometry.
        private FatType _type;
        private int _spc, _reserved, _rootEntries;
        private long _total, _fatSectors, _clusters;

        public List<string> Warnings { get; } = new();

        private int ClusterBytes => _spc * _bps;

        // ---------------------------------------------------------- plan

        public void Plan(FsTree tree)
        {
            _root = PlanDir(tree.Root, null);
            if (_skipped.Count > 0)
                Warnings.Add($"{_skipped.Count} object(s) cannot be stored in FAT (symbolic links, devices, FIFOs, sockets) and were left out: " +
                             string.Join(", ", _skipped.Take(5)) + (_skipped.Count > 5 ? ", ..." : "") + ".");
            if (_extraLinks > 0)
                Warnings.Add($"FAT has no hard links: {_extraLinks} additional name(s) of hard-linked files were stored as separate copies.");
        }

        private Dir PlanDir(FsNode node, Dir? parent)
        {
            var dir = new Dir { Node = node, Parent = parent };
            var names = new Dictionary<string, string>(StringComparer.Ordinal);       // upper case -> name, to catch case-only differences
            var children = new List<Child>();
            foreach (FsNode c in node.SortedChildren())
            {
                if (c.Type is not (FsNodeType.File or FsNodeType.Directory))
                {
                    _skipped.Add(c.Path);
                    continue;
                }
                if (LongNameProblem(c.Name) is { } why)
                    throw new FsException($"'{c.Path}' cannot be stored in FAT: {why}. Rename it first.");
                string upper = c.Name.ToUpperInvariant();
                if (names.TryGetValue(upper, out string? other))
                    throw new FsException($"'{c.Path}' and '{other}' differ only in case, which FAT does not distinguish. Rename one of them first.");
                names[upper] = c.Name;
                if (c.Type == FsNodeType.File && c.Inode.Size > uint.MaxValue)
                    throw new FsException($"'{c.Path}' is larger than 4 GiB, the limit of a file in FAT.");
                if (c.Type == FsNodeType.File && !_seenFiles.Add(c.Inode)) _extraLinks++;
                children.Add(new Child { Node = c, Long = c.Name });
            }

            // Short names: names that already have the 8.3 shape keep it; the others get NAME~N.EXT.
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (Child c in children)
            {
                if (AsShortName(c.Long) is { } s && used.Add(s))
                {
                    c.Short = s;
                    c.NeedLfn = c.Long != s;
                }
            }
            foreach (Child c in children)
            {
                if (c.Short.Length > 0) continue;
                (string b, string e) = ShortBasis(c.Long);
                for (int n = 1; ; n++)
                {
                    string tail = "~" + n;
                    string cand = b[..Math.Min(b.Length, 8 - tail.Length)] + tail + (e.Length > 0 ? "." + e : "");
                    if (used.Add(cand))
                    {
                        c.Short = cand;
                        break;
                    }
                }
                c.NeedLfn = true;
            }
            dir.Children.AddRange(children);
            dir.EntryCount = (parent is null ? 0 : 2) + (parent is null && o.Label.Length > 0 ? 1 : 0)
                             + children.Sum(c => 1 + (c.NeedLfn ? (c.Long.Length + 12) / 13 : 0));
            foreach (Child c in children)
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

        private static int Bits(FatType t) => t switch { FatType.Fat12 => 12, FatType.Fat16 => 16, _ => 32 };

        private int ReservedFor(FatType t) => o.ReservedSectors > 0 ? o.ReservedSectors : t == FatType.Fat32 ? 32 : 1;

        private int RootEntriesFor(FatType t)
        {
            if (t == FatType.Fat32) return 0;
            int perSector = _bps / 32;
            int wanted = Math.Max(o.RootEntries > 0 ? o.RootEntries : 512, _root.EntryCount);
            return (wanted + perSector - 1) / perSector * perSector;
        }

        /// <summary>Clusters and FAT size of a filesystem of <paramref name="total"/> sectors, or null when the type does not fit.</summary>
        private (long Clusters, long FatSectors)? Geometry(long total, FatType t, int spc)
        {
            int reserved = ReservedFor(t), rootEntries = RootEntriesFor(t);
            if (rootEntries > 65535) return null;
            long rootSectors = (long)rootEntries * 32 / _bps;
            long fatSz = 1;
            for (int i = 0; i < 64; i++)
            {
                long data = total - reserved - o.Fats * fatSz - rootSectors;
                if (data < spc) return null;
                long clusters = data / spc;
                long need = ((clusters + 2) * Bits(t) / 8 + _bps - 1 + (Bits(t) == 12 ? 1 : 0)) / _bps;
                if (need <= fatSz)
                {
                    // The type is decided by the cluster count alone: the layout must land in the range of the type.
                    if (TypeFor(clusters) != t || clusters > MaxFat32Clusters) return null;
                    return (clusters, fatSz);
                }
                fatSz = need;
            }
            return null;
        }

        /// <summary>The usual cluster size for a type and a filesystem size (Microsoft's tables).</summary>
        private static int DefaultCluster(FatType t, long bytes) => t switch
        {
            FatType.Fat12 => bytes <= 4L << 20 ? 512 : bytes <= 8L << 20 ? 1024 : bytes <= 16L << 20 ? 2048 : 4096,
            FatType.Fat16 => bytes <= 32L << 20 ? 1024 : bytes <= 64L << 20 ? 2048 : bytes <= 128L << 20 ? 4096
                : bytes <= 256L << 20 ? 8192 : bytes <= 512L << 20 ? 16384 : 32768,
            _ => bytes <= 260L << 20 ? 512 : bytes <= 8L << 30 ? 4096 : bytes <= 16L << 30 ? 8192 : bytes <= 32L << 30 ? 16384 : 32768,
        };

        /// <summary>Cluster sizes to try for a type: the requested one, or the usual one and then its neighbours.</summary>
        private IEnumerable<int> ClusterCandidates(FatType t, long bytes)
        {
            if (o.ClusterSize > 0)
            {
                yield return o.ClusterSize;
                yield break;
            }
            // Large FAT12 clusters only when FAT12 was asked for: otherwise FAT16 is the better choice.
            int max = t == FatType.Fat12 && o.Type != FatType.Fat12 ? 4096 : 32768;
            int d = Math.Max(_bps, DefaultCluster(t, bytes));
            yield return d;
            for (int c = d * 2; c <= max; c *= 2) yield return c;
            for (int c = d / 2; c >= _bps; c /= 2) yield return c;
        }

        private IEnumerable<FatType> TypeCandidates(long bytes)
        {
            if (o.Type != FatType.Auto)
            {
                yield return o.Type;
                yield break;
            }
            FatType first = bytes >= 512L << 20 ? FatType.Fat32 : bytes >= 16L << 20 ? FatType.Fat16 : FatType.Fat12;
            yield return first;
            foreach (FatType t in new[] { FatType.Fat16, FatType.Fat32, FatType.Fat12 })
                if (t != first) yield return t;
        }

        /// <summary>Clusters the content takes with clusters of <paramref name="bytes"/> bytes.</summary>
        private long ContentClusters(FatType t, int bytes)
        {
            long n = 0;
            foreach (Dir d in AllDirs())
            {
                if (d.Parent is not null || t == FatType.Fat32) n += Math.Max(1, ((long)d.EntryCount * 32 + bytes - 1) / bytes);
                foreach (Child c in d.Children)
                    if (!c.Node.IsDirectory) n += (c.Node.Inode.Size + bytes - 1) / bytes;
            }
            return n;
        }

        public void Layout()
        {
            if (o.SizeBytes > 0)
            {
                long total = o.SizeBytes / _bps;
                foreach (FatType t in TypeCandidates(o.SizeBytes))
                    foreach (int cluster in ClusterCandidates(t, o.SizeBytes))
                    {
                        if (Geometry(total, t, cluster / _bps) is not { } g) continue;
                        Use(t, cluster / _bps, total, g);
                        long need = ContentClusters(t, cluster);
                        if (need > _clusters)
                            throw new FsNoSpaceException($"The content does not fit into a {DiskTextSize(o.SizeBytes)} FAT filesystem: it needs {need} clusters of {cluster} bytes, there are {_clusters}.");
                        return;
                    }
                throw new FsException(o.Type == FatType.Auto
                    ? $"No FAT layout fits {DiskTextSize(o.SizeBytes)}{(o.ClusterSize > 0 ? $" with {o.ClusterSize}-byte clusters" : "")}."
                    : $"{TypeName(o.Type)} cannot be made {DiskTextSize(o.SizeBytes)} large{(o.ClusterSize > 0 ? $" with {o.ClusterSize}-byte clusters" : "")}: " +
                      $"it needs between {(o.Type == FatType.Fat12 ? 1 : o.Type == FatType.Fat16 ? MinFat16Clusters : MinFat32Clusters)} and " +
                      $"{(o.Type == FatType.Fat12 ? MinFat16Clusters - 1 : o.Type == FatType.Fat16 ? MinFat32Clusters - 1 : MaxFat32Clusters)} clusters. " +
                      "Choose another type, cluster size or size.");
            }

            // As small as the content allows: the smallest layout of the first type that can hold it.
            long estimate = Math.Max(1, ContentClusters(FatType.Fat16, 4096)) * 4096L + o.ExtraFreeBytes;
            foreach (FatType t in o.Type != FatType.Auto ? new[] { o.Type } : new[] { FatType.Fat12, FatType.Fat16, FatType.Fat32 })
            {
                foreach (int cluster in ClusterCandidates(t, estimate))
                {
                    int spc = cluster / _bps;
                    long need = ContentClusters(t, cluster) + (o.ExtraFreeBytes + cluster - 1) / cluster;
                    long clusters = Math.Max(need, t switch { FatType.Fat12 => 1, FatType.Fat16 => MinFat16Clusters, _ => MinFat32Clusters });
                    if (TypeFor(clusters) != t) continue;
                    long fatSz = ((clusters + 2) * Bits(t) / 8 + _bps) / _bps;
                    long total = ReservedFor(t) + o.Fats * fatSz + (long)RootEntriesFor(t) * 32 / _bps + clusters * spc;
                    // Round up to whole MiB when that is cheap, like partitioning tools do; grow until the content fits.
                    for (int i = 0; i < 64; i++, total += spc)
                    {
                        if (Geometry(total, t, spc) is { } g && g.Clusters >= need)
                        {
                            Use(t, spc, total, g);
                            return;
                        }
                    }
                }
            }
            throw new FsException("No FAT layout can hold the content.");
        }

        private void Use(FatType t, int spc, long total, (long Clusters, long FatSectors) g)
        {
            _type = t;
            _spc = spc;
            _total = total;
            _clusters = g.Clusters;
            _fatSectors = g.FatSectors;
            _reserved = ReservedFor(t);
            _rootEntries = RootEntriesFor(t);
        }

        private static string TypeName(FatType t) => t switch { FatType.Fat12 => "FAT12", FatType.Fat16 => "FAT16", _ => "FAT32" };

        private static string DiskTextSize(long bytes) => bytes >= 1L << 20 && bytes % (1L << 20) == 0 ? $"{bytes >> 20} MiB" : $"{bytes} bytes";

        // ---------------------------------------------------------- emit

        private uint _next = 2;

        private uint Allocate(long count)
        {
            if (count == 0) return 0;
            if (_next - 2 + count > _clusters)
                throw new FsNoSpaceException($"The content does not fit into the FAT filesystem ({_clusters} clusters of {ClusterBytes} bytes).");
            uint first = _next;
            _next += (uint)count;
            return first;
        }

        public void Emit(Stream output, IProgress<ProgressInfo>? progress, CancellationToken ct)
        {
            int cs = ClusterBytes;
            // Allocation in the order the data is written: each directory, then its files, then its subdirectories.
            var order = new List<(Dir? Dir, Child? File)>();
            void Assign(Dir d)
            {
                if (d.Parent is not null || _type == FatType.Fat32)
                {
                    d.Clusters = Math.Max(1, ((long)d.EntryCount * 32 + cs - 1) / cs);
                    d.Cluster = Allocate(d.Clusters);
                    order.Add((d, null));
                }
                foreach (Child c in d.Children)
                {
                    if (c.Node.IsDirectory) continue;
                    c.Clusters = (c.Node.Inode.Size + cs - 1) / cs;
                    c.Cluster = Allocate(c.Clusters);
                    if (c.Clusters > 0) order.Add((null, c));
                }
                foreach (Dir s in d.Subdirs)
                {
                    Assign(s);
                    d.Children.First(c => ReferenceEquals(c.Node, s.Node)).Cluster = s.Cluster;
                }
            }
            Assign(_root);
            foreach (Dir d in AllDirs()) d.Entries = DirectoryEntries(d);

            // The FAT: every allocation is one contiguous chain.
            uint eoc = _type switch { FatType.Fat12 => 0xFFF, FatType.Fat16 => 0xFFFF, _ => 0x0FFFFFFF };
            var fat = new uint[_clusters + 2];
            fat[0] = (_type switch { FatType.Fat12 => 0xF00u, FatType.Fat16 => 0xFF00u, _ => 0x0FFFFF00u }) | o.Media;
            fat[1] = eoc;
            foreach ((Dir? d, Child? f) in order)
            {
                uint first = d?.Cluster ?? f!.Cluster;
                long count = d?.Clusters ?? f!.Clusters;
                for (long i = 0; i < count; i++) fat[first + i] = i == count - 1 ? eoc : (uint)(first + i + 1);
            }
            byte[] fatBytes = new byte[_fatSectors * _bps];
            for (long c = 0; c < fat.Length; c++)
            {
                switch (_type)
                {
                    case FatType.Fat12:
                    {
                        int at = (int)(c * 3 / 2);
                        ushort v = Le.U16(fatBytes, at);
                        v = (c & 1) == 0 ? (ushort)((v & 0xF000) | (fat[c] & 0xFFF)) : (ushort)((v & 0x000F) | ((fat[c] & 0xFFF) << 4));
                        Le.W16(fatBytes, at, v);
                        break;
                    }
                    case FatType.Fat16: Le.W16(fatBytes, (int)(c * 2), (int)fat[c]); break;
                    default: Le.W32(fatBytes, (int)(c * 4), fat[c]); break;
                }
            }

            long free = _clusters - (_next - 2);
            byte[] boot = BootSector();
            output.Write(boot);
            for (int s = 1; s < _reserved; s++)
            {
                if (_type == FatType.Fat32 && s is 1 or 7) output.Write(FsInfo(free));
                else if (_type == FatType.Fat32 && s == 6) output.Write(boot);
                else output.Write(new byte[_bps]);
            }
            for (int i = 0; i < o.Fats; i++) output.Write(fatBytes);
            if (_type != FatType.Fat32)
            {
                byte[] root = new byte[(long)_rootEntries * 32];
                _root.Entries.CopyTo(root, 0);
                output.Write(root);
            }

            // The data area, cluster by cluster in allocation order, then the free space.
            long done = 0, totalBytes = Math.Max(1, order.Sum(x => x.File?.Node.Inode.Size ?? 0));
            byte[] buffer = new byte[cs];
            foreach ((Dir? d, Child? f) in order)
            {
                ct.ThrowIfCancellationRequested();
                if (d is not null)
                {
                    byte[] padded = new byte[d.Clusters * cs];
                    d.Entries.CopyTo(padded, 0);
                    output.Write(padded);
                    continue;
                }
                FsInode inode = f!.Node.Inode;
                using Stream s = inode.Data!.OpenRead();
                long left = inode.Size;
                for (long i = 0; i < f.Clusters; i++)
                {
                    int n = (int)Math.Min(cs, left);
                    s.ReadExactly(buffer, 0, n);
                    if (n < cs) Array.Clear(buffer, n, cs - n);
                    output.Write(buffer);
                    left -= n;
                    done += n;
                }
                if (s.ReadByte() >= 0) throw new FsException($"'{f.Node.Path}' changed while it was being written.");
                progress?.Report(new ProgressInfo("data", done, totalBytes, f.Node.Path));
            }
            long written = (_reserved + o.Fats * _fatSectors + (long)_rootEntries * 32 / _bps) * _bps + (long)(_next - 2) * cs;
            WriteZeros(output, _total * _bps - written);
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

        // ------------------------------------------------ boot sector

        private byte[] BootSector()
        {
            bool fat32 = _type == FatType.Fat32;
            byte[] b = new byte[_bps];
            b[0] = 0xEB;
            b[1] = (byte)(fat32 ? 0x58 : 0x3C);
            b[2] = 0x90;
            byte[] oem = Encoding.ASCII.GetBytes((o.OemName + "        ")[..8]);
            oem.CopyTo(b, 3);
            Le.W16(b, 11, _bps);
            b[13] = (byte)_spc;
            Le.W16(b, 14, _reserved);
            b[16] = (byte)o.Fats;
            Le.W16(b, 17, _rootEntries);
            if (!fat32 && _total < 65536) Le.W16(b, 19, (int)_total);
            else Le.W32(b, 32, (uint)_total);
            b[21] = o.Media;
            if (!fat32) Le.W16(b, 22, (int)_fatSectors);
            Le.W16(b, 24, o.SectorsPerTrack);
            Le.W16(b, 26, o.Heads);
            Le.W32(b, 28, o.HiddenSectors);
            int ext = 0x24;
            if (fat32)
            {
                Le.W32(b, 36, (uint)_fatSectors);
                Le.W32(b, 44, _root.Cluster);
                Le.W16(b, 48, 1);                  // FSInfo sector
                Le.W16(b, 50, 6);                  // backup boot sector
                ext = 0x40;
            }
            b[ext] = 0x80;                          // drive number
            b[ext + 2] = 0x29;                      // extended boot signature
            Le.W32(b, ext + 3, o.VolumeId ?? (uint)Random.Shared.NextInt64(1, uint.MaxValue));
            Encoding.ASCII.GetBytes((o.Label.Length > 0 ? o.Label : "NO NAME") .PadRight(11)).CopyTo(b, ext + 7);
            Encoding.ASCII.GetBytes((_type == FatType.Fat12 ? "FAT12" : _type == FatType.Fat16 ? "FAT16" : "FAT32").PadRight(8)).CopyTo(b, ext + 18);
            int code = ext + 26;
            if (o.BootCode is { } src && src.Length == 510 - code)
            {
                src.CopyTo(b, code);
            }
            else
            {
                // Not bootable: "int 18h" asks the BIOS for the next boot device.
                b[code] = 0xCD;
                b[code + 1] = 0x18;
                b[code + 2] = 0xEB;
                b[code + 3] = 0xFE;
            }
            b[510] = 0x55;
            b[511] = 0xAA;
            return b;
        }

        private byte[] FsInfo(long free)
        {
            byte[] s = new byte[_bps];
            Le.W32(s, 0, 0x41615252);
            Le.W32(s, 484, 0x61417272);
            Le.W32(s, 488, (uint)free);
            Le.W32(s, 492, free > 0 ? _next : 0xFFFFFFFF);
            Le.W32(s, 508, 0xAA550000);
            return s;
        }

        // ------------------------------------------------ directories

        private byte[] DirectoryEntries(Dir d)
        {
            var e = new MemoryStream(d.EntryCount * 32);
            (ushort date, ushort time) = FromUnix(d.Node.Inode.MTime);
            if (d.Parent is null)
            {
                if (o.Label.Length > 0) e.Write(Short(Encoding.ASCII.GetBytes(o.Label.PadRight(11)), AttrVolume, 0, 0, date, time));
            }
            else
            {
                e.Write(Short(Encoding.ASCII.GetBytes(".          "), AttrDirectory, d.Cluster, 0, date, time));
                uint parent = d.Parent.Parent is null ? 0 : d.Parent.Cluster;
                (ushort pd, ushort pt) = FromUnix(d.Parent.Node.Inode.MTime);
                e.Write(Short(Encoding.ASCII.GetBytes("..         "), AttrDirectory, parent, 0, pd, pt));
            }
            foreach (Child c in d.Children)
            {
                byte[] name = Pack83(c.Short);
                if (c.NeedLfn) e.Write(LongEntries(c.Long, ShortNameChecksum(name)));
                FsInode i = c.Node.Inode;
                (ushort cd, ushort ctm) = FromUnix(i.MTime);
                byte attr = c.Node.IsDirectory ? AttrDirectory : (byte)(AttrArchive | ((i.Mode & 0x92) == 0 ? AttrReadOnly : 0));
                e.Write(Short(name, attr, c.Cluster, c.Node.IsDirectory ? 0 : (uint)i.Size, cd, ctm));
            }
            return e.ToArray();
        }

        private byte[] Short(byte[] name11, byte attr, uint cluster, uint size, ushort date, ushort time)
        {
            byte[] r = new byte[32];
            name11.AsSpan(0, 11).CopyTo(r);
            r[11] = attr;
            Le.W16(r, 14, time);                    // creation
            Le.W16(r, 16, date);
            Le.W16(r, 18, date);                    // last access
            if (_type == FatType.Fat32) Le.W16(r, 20, (int)(cluster >> 16));
            Le.W16(r, 22, time);
            Le.W16(r, 24, date);
            Le.W16(r, 26, (int)(cluster & 0xFFFF));
            Le.W32(r, 28, size);
            return r;
        }

        private static byte[] LongEntries(string name, byte checksum)
        {
            int count = (name.Length + 12) / 13;
            byte[] r = new byte[count * 32];
            int[] offsets = { 1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30 };
            for (int k = count; k >= 1; k--)
            {
                int p = (count - k) * 32;
                r[p] = (byte)(k | (k == count ? 0x40 : 0));
                r[p + 11] = AttrLongName;
                r[p + 13] = checksum;
                for (int j = 0; j < 13; j++)
                {
                    int at = (k - 1) * 13 + j;
                    int ch = at < name.Length ? name[at] : at == name.Length ? 0 : 0xFFFF;
                    Le.W16(r, p + offsets[j], ch);
                }
            }
            return r;
        }
    }
}
