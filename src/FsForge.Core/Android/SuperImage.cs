using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FsForge.Core.Images;

namespace FsForge.Core.Android;

/// <summary>
/// super.img: the "super" partition of Android 10 and later, holding the logical (dynamic) partitions system,
/// vendor, product... The LP metadata (system/core/fs_mgr/liblp) lies at the start: 4 KiB reserved, the
/// geometry and its backup, then a primary and a backup copy of the metadata for every slot. The metadata
/// lists the partitions, each made of extents of 512-byte sectors on the block device, their groups and the
/// block devices.
/// <para>
/// A partition is replaced in place when its new content fits into the room it had; otherwise its old extents
/// are freed and it gets new ones in the free space (aligned as the block device asks). The other partitions
/// stay where they are, byte for byte. The same metadata is written to every slot, as lpmake does.
/// </para>
/// </summary>
public sealed class SuperImage : AndroidImage
{
    public const uint GeometryMagic = 0x616C4467, HeaderMagic = 0x414C5030;
    public const int ReservedBytes = 4096, GeometrySize = 4096, GeometryStructSize = 52, SectorSize = 512;
    private const int PartitionEntry = 52, ExtentEntry = 24, GroupEntry = 48, DeviceEntry = 64;
    public const uint TargetLinear = 0, TargetZero = 1;

    private IBlockSource _source = null!;
    private bool _sparse;
    private byte[] _reserved = Array.Empty<byte>(), _geometry = Array.Empty<byte>();

    public override AndroidImageKind Kind => AndroidImageKind.Super;
    public override string Description => $"super (LP metadata {MajorVersion}.{MinorVersion}, {SlotCount} slot(s))";

    public uint MetadataMaxSize { get; private set; }
    public uint SlotCount { get; private set; }
    public uint LogicalBlockSize { get; private set; }
    public ushort MajorVersion { get; private set; }
    public ushort MinorVersion { get; private set; }
    public uint HeaderFlags { get; private set; }

    /// <summary>The image came as an Android sparse file and is written back as one.</summary>
    public bool Sparse => _sparse;

    public List<LpPartition> Partitions { get; } = new();
    public List<LpGroup> Groups { get; } = new();
    public List<LpBlockDevice> BlockDevices { get; } = new();

    public sealed class LpPartition
    {
        public string Name = "";
        public uint Attributes;
        public int Group;
        public List<LpExtent> Extents = new();
        public long Sectors => Extents.Sum(e => (long)e.Sectors);
    }

    public record struct LpExtent(ulong Sectors, uint TargetType, ulong TargetData, uint TargetSource);

    public sealed class LpGroup
    {
        public string Name = "";
        public uint Flags;
        public ulong MaximumSize;
    }

    public sealed class LpBlockDevice
    {
        public ulong FirstLogicalSector;
        public uint Alignment, AlignmentOffset;
        public ulong Size;
        public string Name = "";
        public uint Flags;
    }

    /// <summary>Size of the block device the partitions live on, in bytes.</summary>
    public long DeviceSize => BlockDevices.Count > 0 ? (long)BlockDevices[0].Size : 0;

    /// <summary>Bytes of the device not used by any partition.</summary>
    public long FreeBytes => DeviceSize - (long)(BlockDevices.Count > 0 ? BlockDevices[0].FirstLogicalSector * SectorSize : 0)
                             - Partitions.Sum(p => p.Extents.Where(e => e.TargetType == TargetLinear && e.TargetSource == 0).Sum(e => (long)e.Sectors * SectorSize));

    // ---------------------------------------------------------- reading

    private static bool ValidGeometry(ReadOnlySpan<byte> g)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(g) != GeometryMagic || BinaryPrimitives.ReadUInt32LittleEndian(g[4..]) != GeometryStructSize) return false;
        byte[] copy = g[..GeometryStructSize].ToArray();
        copy.AsSpan(8, 32).Clear();
        return SHA256.HashData(copy).AsSpan().SequenceEqual(g.Slice(8, 32));
    }

    public static bool IsSuper(IBlockSource source)
    {
        if (source.Length < ReservedBytes + 2 * GeometrySize) return false;
        Span<byte> g = stackalloc byte[GeometryStructSize];
        foreach (long at in new long[] { ReservedBytes, ReservedBytes + GeometrySize })
        {
            source.ReadExactly(at, g);
            if (ValidGeometry(g)) return true;
        }
        return false;
    }

    public static SuperImage Read(IBlockSource source, bool sparse)
    {
        var s = new SuperImage { _source = source, _sparse = sparse };
        s._reserved = source.ReadBytes(0, ReservedBytes);
        byte[] g = source.ReadBytes(ReservedBytes, GeometrySize);
        if (!ValidGeometry(g))
        {
            g = source.ReadBytes(ReservedBytes + GeometrySize, GeometrySize);
            if (!ValidGeometry(g)) throw new FsException("Corrupt super image: neither copy of the geometry is valid.");
            s.Warnings.Add("The primary geometry of the super image is damaged; the backup copy was used.");
        }
        s._geometry = g;
        s.MetadataMaxSize = BinaryPrimitives.ReadUInt32LittleEndian(g.AsSpan(40));
        s.SlotCount = BinaryPrimitives.ReadUInt32LittleEndian(g.AsSpan(44));
        s.LogicalBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(g.AsSpan(48));
        if (s.MetadataMaxSize is < 512 or > 64 << 20 || s.MetadataMaxSize % SectorSize != 0 || s.SlotCount is 0 or > 8
            || s.LogicalBlockSize is 0 || s.LogicalBlockSize % SectorSize != 0)
            throw new FsException("Corrupt super image: invalid geometry.");

        byte[]? blob = null;
        byte[]? first = null;
        for (uint slot = 0; slot < s.SlotCount && blob is null; slot++)
            foreach (long at in new[] { s.PrimaryOffset(slot), s.BackupOffset(slot) })
            {
                byte[]? m = s.ReadMetadataBlob(at);
                if (m is null) continue;
                blob = m;
                if (slot > 0 || at != s.PrimaryOffset(0))
                    s.Warnings.Add("The first copy of the super metadata is damaged; another copy was used.");
                break;
            }
        if (blob is null) throw new FsException("Corrupt super image: no copy of the partition metadata is valid.");
        s.Parse(blob);
        // Slots that hold something else (after an update on an A/B device) are noted: one metadata is written to all.
        for (uint slot = 1; slot < s.SlotCount; slot++)
        {
            first = s.ReadMetadataBlob(s.PrimaryOffset(slot));
            if (first is not null && !first.AsSpan().SequenceEqual(blob))
            {
                s.Warnings.Add($"The metadata of slot {slot} differs from slot 0 (the device was updated). The partitions of slot 0 are shown; saving writes them to every slot.");
                break;
            }
        }
        s.BuildParts();
        return s;
    }

    private long PrimaryOffset(uint slot) => ReservedBytes + 2L * GeometrySize + (long)MetadataMaxSize * slot;
    private long BackupOffset(uint slot) => ReservedBytes + 2L * GeometrySize + (long)MetadataMaxSize * (SlotCount + slot);

    /// <summary>The header and tables at an offset when their checksums are right; null otherwise.</summary>
    private byte[]? ReadMetadataBlob(long at)
    {
        if (at + 256 > _source.Length) return null;
        byte[] h = _source.ReadBytes(at, 256);
        if (BinaryPrimitives.ReadUInt32LittleEndian(h) != HeaderMagic || BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(4)) != 10) return null;
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(8));
        uint tablesSize = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(44));
        if (headerSize is < 128 or > 256 || (long)headerSize + tablesSize > MetadataMaxSize) return null;
        byte[] header = h.AsSpan(0, (int)headerSize).ToArray();
        header.AsSpan(12, 32).Clear();
        if (!SHA256.HashData(header).AsSpan().SequenceEqual(h.AsSpan(12, 32))) return null;
        if (at + headerSize + tablesSize > _source.Length) return null;
        byte[] tables = _source.ReadBytes(at + headerSize, (int)tablesSize);
        if (!SHA256.HashData(tables).AsSpan().SequenceEqual(h.AsSpan(48, 32))) return null;
        byte[] blob = new byte[headerSize + tablesSize];
        h.AsSpan(0, (int)headerSize).CopyTo(blob);
        tables.CopyTo(blob, headerSize);
        return blob;
    }

    private static string Name(ReadOnlySpan<byte> b)
    {
        int n = b.IndexOf((byte)0);
        return Encoding.ASCII.GetString(n >= 0 ? b[..n] : b);
    }

    private void Parse(byte[] blob)
    {
        ReadOnlySpan<byte> m = blob;
        MajorVersion = BinaryPrimitives.ReadUInt16LittleEndian(m[4..]);
        MinorVersion = BinaryPrimitives.ReadUInt16LittleEndian(m[6..]);
        int headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(m[8..]);
        if (MinorVersion > 2) Warnings.Add($"The super metadata has version 10.{MinorVersion}, newer than this program knows (10.2).");
        HeaderFlags = headerSize >= 132 ? BinaryPrimitives.ReadUInt32LittleEndian(m[128..]) : 0;
        ReadOnlySpan<byte> tables = m[headerSize..];

        int tablesLength = tables.Length;
        (int Offset, int Count, int Size) Table(int at, int minSize)
        {
            int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(at));
            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(at + 4));
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(at + 8));
            if (count < 0 || size < minSize || offset < 0 || (long)offset + (long)count * size > tablesLength)
                throw new FsException("Corrupt super image: a metadata table lies outside the metadata.");
            return (offset, count, size);
        }
        var pt = Table(80, PartitionEntry);
        var et = Table(92, ExtentEntry);
        var gt = Table(104, GroupEntry);
        var bt = Table(116, DeviceEntry);

        var extents = new List<LpExtent>();
        for (int i = 0; i < et.Count; i++)
        {
            ReadOnlySpan<byte> e = tables.Slice(et.Offset + i * et.Size, ExtentEntry);
            extents.Add(new LpExtent(BinaryPrimitives.ReadUInt64LittleEndian(e), BinaryPrimitives.ReadUInt32LittleEndian(e[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(e[12..]), BinaryPrimitives.ReadUInt32LittleEndian(e[20..])));
        }
        for (int i = 0; i < gt.Count; i++)
        {
            ReadOnlySpan<byte> e = tables.Slice(gt.Offset + i * gt.Size, GroupEntry);
            Groups.Add(new LpGroup { Name = Name(e[..36]), Flags = BinaryPrimitives.ReadUInt32LittleEndian(e[36..]), MaximumSize = BinaryPrimitives.ReadUInt64LittleEndian(e[40..]) });
        }
        for (int i = 0; i < bt.Count; i++)
        {
            ReadOnlySpan<byte> e = tables.Slice(bt.Offset + i * bt.Size, DeviceEntry);
            BlockDevices.Add(new LpBlockDevice
            {
                FirstLogicalSector = BinaryPrimitives.ReadUInt64LittleEndian(e), Alignment = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]),
                AlignmentOffset = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]), Size = BinaryPrimitives.ReadUInt64LittleEndian(e[16..]),
                Name = Name(e.Slice(24, 36)), Flags = BinaryPrimitives.ReadUInt32LittleEndian(e[60..]),
            });
        }
        if (BlockDevices.Count == 0) throw new FsException("Corrupt super image: the metadata lists no block device.");
        for (int i = 0; i < pt.Count; i++)
        {
            ReadOnlySpan<byte> e = tables.Slice(pt.Offset + i * pt.Size, PartitionEntry);
            uint firstExtent = BinaryPrimitives.ReadUInt32LittleEndian(e[40..]), numExtents = BinaryPrimitives.ReadUInt32LittleEndian(e[44..]);
            uint group = BinaryPrimitives.ReadUInt32LittleEndian(e[48..]);
            if ((long)firstExtent + numExtents > extents.Count || group >= Groups.Count)
                throw new FsException("Corrupt super image: a partition refers to extents or a group that do not exist.");
            Partitions.Add(new LpPartition
            {
                Name = Name(e[..36]), Attributes = BinaryPrimitives.ReadUInt32LittleEndian(e[36..]), Group = (int)group,
                Extents = extents.GetRange((int)firstExtent, (int)numExtents),
            });
        }
    }

    private void BuildParts()
    {
        var parts = new List<AndroidPart>();
        for (int i = 0; i < Partitions.Count; i++)
        {
            LpPartition p = Partitions[i];
            bool elsewhere = p.Extents.Any(e => e.TargetType == TargetLinear && e.TargetSource != 0);
            PartitionContent content = PartitionContent.Unknown;
            string name = "";
            if (elsewhere) name = $"on block device '{BlockDevices[(int)p.Extents.First(e => e.TargetSource != 0).TargetSource % BlockDevices.Count].Name}'";
            else if (p.Extents.Count > 0 && p.Extents.Any(e => e.TargetType == TargetLinear && (long)e.TargetData * SectorSize >= _source.Length))
                (content, name) = (PartitionContent.OutsideFile, "not in the file");
            else
            {
                IBlockSource data = OpenPartition(p);
                (content, name) = Identify(data);
            }
            parts.Add(new AndroidPart
            {
                Index = i, Name = p.Name, Length = p.Sectors * SectorSize, Content = content, ContentName = name,
                Detail = "group " + Groups[p.Group].Name + ((p.Attributes & 1) != 0 ? ", read-only" : ""),
            });
        }
        Parts = parts;
    }

    private IBlockSource OpenPartition(LpPartition p) => new ConcatSource(p.Extents.Select(e =>
        (e.TargetType == TargetLinear ? _source : (IBlockSource?)null, (long)e.TargetData * SectorSize, (long)e.Sectors * SectorSize)));

    public override IBlockSource Open(AndroidPart part)
    {
        LpPartition p = Partitions[part.Index];
        if (p.Extents.Any(e => e.TargetType == TargetLinear && e.TargetSource != 0))
            throw new FsException($"The partition '{p.Name}' lies on another block device than this super image.");
        return OpenPartition(p);
    }

    // ---------------------------------------------------------- writing

    public override void Write(Stream output, AndroidPart part, IBlockSource data, List<string> warnings,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        LpBlockDevice dev = BlockDevices[0];
        long block = LogicalBlockSize;
        long size = (data.Length + block - 1) / block * block;
        ulong sectors = (ulong)(size / SectorSize);
        LpPartition target = Partitions[part.Index];

        // Room the partition had: when the new content fits, it keeps its extents (cut to the new length).
        var extents = new List<LpExtent>();
        if (sectors <= (ulong)target.Sectors && target.Extents.All(e => e.TargetType == TargetLinear && e.TargetSource == 0))
        {
            ulong left = sectors;
            foreach (LpExtent e in target.Extents)
            {
                if (left == 0) break;
                ulong n = Math.Min(left, e.Sectors);
                extents.Add(e with { Sectors = n });
                left -= n;
            }
        }
        else
        {
            // New extents in the free space, aligned as liblp aligns them.
            ulong alignment = Math.Max(dev.Alignment, (uint)LogicalBlockSize) / SectorSize;
            ulong alignOffset = dev.AlignmentOffset / SectorSize;
            ulong start = dev.FirstLogicalSector, end = dev.Size / SectorSize;
            var used = Partitions.Where(p => p != target)
                .SelectMany(p => p.Extents).Where(e => e.TargetType == TargetLinear && e.TargetSource == 0)
                .Select(e => (From: e.TargetData, To: e.TargetData + e.Sectors)).OrderBy(r => r.From).ToList();
            ulong left = sectors, pos = start;
            foreach ((ulong from, ulong to) in used.Append((end, end)))
            {
                if (left == 0) break;
                ulong a = pos;
                if (alignment > 1)
                {
                    ulong r = (a + alignment - alignOffset % alignment) % alignment;
                    if (r != 0) a += alignment - r;
                }
                if (a < from)
                {
                    ulong n = Math.Min(left, from - a);
                    extents.Add(new LpExtent(n, TargetLinear, a, 0));
                    left -= n;
                }
                pos = Math.Max(pos, to);
            }
            if (left > 0)
                throw new FsException($"The new content of '{target.Name}' ({DiskText.Size(size)}) does not fit into the super image: " +
                                      $"{DiskText.Size(FreeBytes + target.Sectors * SectorSize)} is free for it.");
            if (extents.Count > 1) warnings.Add($"The partition '{target.Name}' no longer fits where it was: it was given {extents.Count} extents in the free space of the super image.");
        }
        if (sectors == 0) extents.Clear();

        // Groups with a maximum size must still hold their partitions.
        LpGroup group = Groups[target.Group];
        long groupBytes = Partitions.Where(p => p.Group == target.Group && p != target).Sum(p => p.Sectors * SectorSize) + size;
        if (group.MaximumSize > 0 && (ulong)groupBytes > group.MaximumSize)
            throw new FsException($"The partitions of group '{group.Name}' would take {DiskText.Size(groupBytes)}, more than its maximum of {DiskText.Size((long)group.MaximumSize)}.");

        byte[] metadata = Serialize(target, extents);
        if (metadata.Length > MetadataMaxSize) throw new FsException("The partition metadata does not fit into the room the super image has for it.");

        // Content: the metadata area, the other partitions as they are, the new one.
        var layout = new ImageLayout(Math.Max(_source.Length, (long)dev.Size));
        layout.Add(0, _reserved);
        layout.Add(ReservedBytes, _geometry);
        layout.Add(ReservedBytes + GeometrySize, _geometry);
        byte[] slot = new byte[MetadataMaxSize];
        metadata.CopyTo(slot, 0);
        for (uint i = 0; i < SlotCount; i++)
        {
            layout.Add(PrimaryOffset(i), slot);
            layout.Add(BackupOffset(i), slot);
        }
        foreach (LpPartition p in Partitions)
        {
            if (p == target) continue;
            foreach (LpExtent e in p.Extents)
                if (e.TargetType == TargetLinear && e.TargetSource == 0)
                    layout.Add((long)e.TargetData * SectorSize, new SubBlockSource(_source, (long)e.TargetData * SectorSize,
                        Math.Max(0, Math.Min((long)e.Sectors * SectorSize, _source.Length - (long)e.TargetData * SectorSize)), ownsInner: false));
        }
        long done = 0;
        foreach (LpExtent e in extents)
        {
            long n = (long)e.Sectors * SectorSize;
            layout.Add((long)e.TargetData * SectorSize, new ConcatSource(new[] { ((IBlockSource?)data, done, Math.Min(n, Math.Max(0, data.Length - done))), (null, 0, n - Math.Min(n, Math.Max(0, data.Length - done))) }));
            done += n;
        }
        if (_sparse) layout.WriteSparse(output, progress, ct);
        else layout.WriteRaw(output, progress, ct);
    }

    /// <summary>The header and tables with the extents of one partition replaced.</summary>
    private byte[] Serialize(LpPartition target, List<LpExtent> newExtents)
    {
        var partitions = new MemoryStream();
        var extents = new MemoryStream();
        int extentCount = 0;
        Span<byte> e = stackalloc byte[ExtentEntry];
        Span<byte> pe = stackalloc byte[PartitionEntry];
        foreach (LpPartition p in Partitions)
        {
            List<LpExtent> list = p == target ? newExtents : p.Extents;
            pe.Clear();
            Encoding.ASCII.GetBytes(p.Name).AsSpan(0, Math.Min(36, p.Name.Length)).CopyTo(pe);
            BinaryPrimitives.WriteUInt32LittleEndian(pe[36..], p.Attributes);
            BinaryPrimitives.WriteUInt32LittleEndian(pe[40..], (uint)extentCount);
            BinaryPrimitives.WriteUInt32LittleEndian(pe[44..], (uint)list.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(pe[48..], (uint)p.Group);
            partitions.Write(pe);
            foreach (LpExtent x in list)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(e, x.Sectors);
                BinaryPrimitives.WriteUInt32LittleEndian(e[8..], x.TargetType);
                BinaryPrimitives.WriteUInt64LittleEndian(e[12..], x.TargetData);
                BinaryPrimitives.WriteUInt32LittleEndian(e[20..], x.TargetSource);
                extents.Write(e);
                extentCount++;
            }
        }
        var groups = new MemoryStream();
        Span<byte> ge = stackalloc byte[GroupEntry];
        foreach (LpGroup g in Groups)
        {
            ge.Clear();
            Encoding.ASCII.GetBytes(g.Name).AsSpan(0, Math.Min(36, g.Name.Length)).CopyTo(ge);
            BinaryPrimitives.WriteUInt32LittleEndian(ge[36..], g.Flags);
            BinaryPrimitives.WriteUInt64LittleEndian(ge[40..], g.MaximumSize);
            groups.Write(ge);
        }
        var devices = new MemoryStream();
        Span<byte> de = stackalloc byte[DeviceEntry];
        foreach (LpBlockDevice d in BlockDevices)
        {
            de.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(de, d.FirstLogicalSector);
            BinaryPrimitives.WriteUInt32LittleEndian(de[8..], d.Alignment);
            BinaryPrimitives.WriteUInt32LittleEndian(de[12..], d.AlignmentOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(de[16..], d.Size);
            Encoding.ASCII.GetBytes(d.Name).AsSpan(0, Math.Min(36, d.Name.Length)).CopyTo(de[24..]);
            BinaryPrimitives.WriteUInt32LittleEndian(de[60..], d.Flags);
            devices.Write(de);
        }
        byte[] tables = [.. partitions.ToArray(), .. extents.ToArray(), .. groups.ToArray(), .. devices.ToArray()];

        int headerSize = MinorVersion >= 2 ? 256 : 128;
        byte[] h = new byte[headerSize];
        BinaryPrimitives.WriteUInt32LittleEndian(h, HeaderMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(4), MajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(6), MinorVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), (uint)headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(44), (uint)tables.Length);
        SHA256.HashData(tables).CopyTo(h, 48);
        void Desc(int at, long offset, int count, int size)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(at), (uint)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(at + 4), (uint)count);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(at + 8), (uint)size);
        }
        Desc(80, 0, Partitions.Count, PartitionEntry);
        Desc(92, partitions.Length, extentCount, ExtentEntry);
        Desc(104, partitions.Length + extents.Length, Groups.Count, GroupEntry);
        Desc(116, partitions.Length + extents.Length + groups.Length, BlockDevices.Count, DeviceEntry);
        if (headerSize >= 132) BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(128), HeaderFlags);
        SHA256.HashData(h).CopyTo(h, 12);
        return [.. h, .. tables];
    }

    public override IEnumerable<(string Name, IBlockSource Data)> FixedSections()
    {
        foreach (AndroidPart p in Parts)
            if (p.Content != PartitionContent.OutsideFile && !Partitions[p.Index].Extents.Any(e => e.TargetSource != 0))
                yield return (p.Name, OpenPartition(Partitions[p.Index]));
    }
}
