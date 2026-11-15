using System.Text;
using FsForge.Core.Ext;
using FsForge.Core.SquashFs;

namespace FsForge.Core.Images;

public enum PartitionScheme
{
    Mbr,
    Gpt,
}

/// <summary>What a partition holds, as far as its first sectors tell.</summary>
public enum PartitionContent
{
    Unknown,

    /// <summary>The beginning of the partition is filled with zeros.</summary>
    Empty,

    /// <summary>The partition starts beyond the end of the image file (a truncated image).</summary>
    OutsideFile,
    SquashFs,
    Ext,
    Fat,
    Ntfs,
    ExFat,
    F2fs,
    Swap,
    Btrfs,
    Xfs,
    Luks,
    Ubi,
    Ubifs,
}

/// <summary>One entry of a partition table.</summary>
public sealed class PartitionInfo
{
    /// <summary>Number as Linux counts it (mmcblk0p<b>N</b>): the table slot for primary and GPT entries, 5 and up for logical ones.</summary>
    public int Number { get; init; }

    /// <summary>Position of the partition in the disk image, in bytes.</summary>
    public long Offset { get; init; }

    /// <summary>Size according to the partition table, in bytes.</summary>
    public long Length { get; init; }

    /// <summary>
    /// How much of the partition is present in the image file. Images are often cut off right after
    /// the last filesystem, so this can be less than <see cref="Length"/>.
    /// </summary>
    public long StoredLength { get; init; }

    /// <summary>Partition type in readable form ("Linux (0x83)", "EFI System").</summary>
    public string TypeName { get; init; } = "";

    /// <summary>Partition name from a GPT entry; empty for MBR.</summary>
    public string Name { get; init; } = "";

    public bool Bootable { get; init; }
    public PartitionContent Content { get; init; }

    /// <summary>Exact kind of the content when known ("FAT16", "ext4"...).</summary>
    public string ContentName { get; init; } = "";

    /// <summary>Volume label of the filesystem, when it has one.</summary>
    public string Label { get; init; } = "";

    /// <summary>No other partition starts after this one.</summary>
    public bool IsLast { get; init; }

    /// <summary>Offset of the 32-bit sector count of this entry in the image; -1 when the size cannot be patched.</summary>
    internal long SizeFieldOffset { get; init; } = -1;

    /// <summary>The partition holds a filesystem this program can edit.</summary>
    public bool IsEditable => Content is PartitionContent.SquashFs or PartitionContent.Ext or PartitionContent.Fat;

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(Number).Append(": ").Append(ContentName.Length > 0 ? ContentName : "unknown content");
        if (Label.Length > 0) sb.Append(" \"").Append(Label).Append('"');
        else if (Name.Length > 0) sb.Append(" \"").Append(Name).Append('"');
        sb.Append(", ").Append(DiskText.Size(Length));
        sb.Append(", ").Append(TypeName);
        if (Bootable) sb.Append(", bootable");
        return sb.ToString();
    }
}

/// <summary>Partition table of a raw disk image (SD card, eMMC or disk dump).</summary>
public sealed class PartitionTable
{
    private PartitionTable(PartitionScheme scheme, int sectorSize, List<PartitionInfo> partitions)
    {
        Scheme = scheme;
        SectorSize = sectorSize;
        Partitions = partitions;
    }

    public PartitionScheme Scheme { get; }
    public int SectorSize { get; }

    /// <summary>All partitions in the order of their numbers. Extended containers are not listed.</summary>
    public IReadOnlyList<PartitionInfo> Partitions { get; }

    /// <summary>Partitions that hold SquashFS, ext2/3/4 or FAT.</summary>
    public IReadOnlyList<PartitionInfo> Editable => Partitions.Where(p => p.IsEditable).ToList();

    public PartitionInfo? Find(int number) => Partitions.FirstOrDefault(p => p.Number == number);

    /// <summary>
    /// The partition to open when nobody was asked: the root filesystem, as a rule the largest SquashFS or ext partition.
    /// A FAT boot partition is opened by default only when there is nothing else.
    /// </summary>
    public PartitionInfo? PickDefault() =>
        Partitions.Where(p => p.IsEditable && p.Content != PartitionContent.Fat).MaxBy(p => p.Length)
        ?? Partitions.Where(p => p.IsEditable).MaxBy(p => p.Length);

    // ------------------------------------------------------------------ read

    private const int MaxLogical = 60;
    private const int MaxGptEntries = 1024;

    /// <summary>Reads the partition table of a disk image; null when the data does not start with one.</summary>
    public static PartitionTable? Read(IBlockSource disk)
    {
        if (disk.Length < 1024) return null;
        byte[] mbr = disk.ReadBytes(0, 512);
        if (mbr[510] != 0x55 || mbr[511] != 0xAA) return null;

        foreach (int sectorSize in new[] { 512, 4096 })
        {
            if (TryReadGpt(disk, sectorSize) is { } gpt) return gpt;
        }
        return TryReadMbr(disk, mbr);
    }

    private readonly record struct RawEntry(int Number, long Offset, long Length, string TypeName, string Name, bool Bootable, long SizeField);

    private static PartitionTable? TryReadMbr(IBlockSource disk, byte[] mbr)
    {
        const int sector = 512;
        var entries = new List<RawEntry>();
        var starts = new List<long>();              // of every used slot, extended containers included
        var primaries = new List<(long Start, long End)>();
        for (int i = 0; i < 4; i++)
        {
            ReadOnlySpan<byte> e = mbr.AsSpan(446 + 16 * i, 16);
            byte status = e[0], type = e[4];
            // A boot sector of a filesystem carries code or text here; a real table only these two values.
            if (status != 0x00 && status != 0x80) return null;
            long lba = Le.U32(e, 8), count = Le.U32(e, 12);
            if (type == 0 || count == 0) continue;
            if (lba == 0) return null;
            long start = lba * sector, length = count * sector;
            foreach ((long s, long end) in primaries)
                if (start < end && s < start + length) return null;
            primaries.Add((start, start + length));
            starts.Add(start);

            if (IsExtendedType(type))
            {
                ReadLogical(disk, start, length, entries, starts);
                continue;
            }
            entries.Add(new RawEntry(i + 1, start, length, MbrTypeName(type), "", status == 0x80, 446 + 16 * i + 12));
        }
        // Images are often cut short, so a partition may lie beyond the end of the file - but not all of them.
        if (!entries.Exists(e => e.Offset < disk.Length)) return null;
        return Build(disk, PartitionScheme.Mbr, sector, entries, starts);
    }

    private static bool IsExtendedType(byte type) => type is 0x05 or 0x0F or 0x85;

    private static void ReadLogical(IBlockSource disk, long extStart, long extLength, List<RawEntry> entries, List<long> starts)
    {
        const int sector = 512;
        long ebr = extStart;
        int number = 5;
        var seen = new HashSet<long>();
        byte[] buf = new byte[512];
        while (number < 5 + MaxLogical && seen.Add(ebr) && ebr + 512 <= disk.Length)
        {
            disk.ReadExactly(ebr, buf);
            if (buf[510] != 0x55 || buf[511] != 0xAA) break;
            ReadOnlySpan<byte> first = buf.AsSpan(446, 16), link = buf.AsSpan(462, 16);
            long lba = Le.U32(first, 8), count = Le.U32(first, 12);
            if (first[4] != 0 && count != 0 && lba != 0)
            {
                long start = ebr + lba * sector;
                entries.Add(new RawEntry(number, start, count * sector, MbrTypeName(first[4]), "", first[0] == 0x80, -1));
                starts.Add(start);
                number++;
            }
            long next = Le.U32(link, 8);
            if (!IsExtendedType(link[4]) || next == 0) break;
            ebr = extStart + next * sector;
            if (ebr >= extStart + extLength) break;
        }
    }

    private static PartitionTable? TryReadGpt(IBlockSource disk, int sectorSize)
    {
        if (disk.Length < sectorSize * 2L + 92) return null;
        byte[] h = disk.ReadBytes(sectorSize, 92);
        if (!h.AsSpan(0, 8).SequenceEqual("EFI PART"u8)) return null;
        ulong entriesLba = Le.U64(h, 72);
        uint count = Le.U32(h, 80), entrySize = Le.U32(h, 84);
        if (entrySize < 128 || entrySize > 4096 || count == 0 || entriesLba < 2 || entriesLba >= (ulong)(disk.Length / sectorSize)) return null;
        count = Math.Min(count, MaxGptEntries);
        long tableOffset = (long)entriesLba * sectorSize;
        long tableBytes = Math.Min((long)count * entrySize, disk.Length - tableOffset);
        if (tableBytes < entrySize) return null;
        byte[] table = disk.ReadBytes(tableOffset, (int)tableBytes);

        var entries = new List<RawEntry>();
        var starts = new List<long>();
        long maxLba = long.MaxValue / sectorSize;
        for (int i = 0; (i + 1) * (long)entrySize <= table.Length; i++)
        {
            ReadOnlySpan<byte> e = table.AsSpan(i * (int)entrySize, (int)entrySize);
            if (e[..16].IndexOfAnyExcept((byte)0) < 0) continue;
            ulong first = Le.U64(e, 32), last = Le.U64(e, 40);
            if (first == 0 || last < first || last >= (ulong)maxLba) continue;
            long start = (long)first * sectorSize, length = (long)(last - first + 1) * sectorSize;
            string name = Encoding.Unicode.GetString(e.Slice(56, 72));
            int nul = name.IndexOf('\0');
            if (nul >= 0) name = name[..nul];
            bool bootable = (Le.U64(e, 48) & 4) != 0;
            entries.Add(new RawEntry(i + 1, start, length, GptTypeName(new Guid(e[..16])), name, bootable, -1));
            starts.Add(start);
        }
        if (!entries.Exists(e => e.Offset < disk.Length)) return null;
        return Build(disk, PartitionScheme.Gpt, sectorSize, entries, starts);
    }

    private static PartitionTable Build(IBlockSource disk, PartitionScheme scheme, int sectorSize, List<RawEntry> entries, List<long> starts)
    {
        long lastStart = starts.Max();
        var list = new List<PartitionInfo>();
        foreach (RawEntry e in entries.OrderBy(x => x.Number))
        {
            (PartitionContent content, string contentName, string label) = Identify(disk, e.Offset, e.Length);
            list.Add(new PartitionInfo
            {
                Number = e.Number,
                Offset = e.Offset,
                Length = e.Length,
                StoredLength = Math.Max(0, Math.Min(e.Length, disk.Length - e.Offset)),
                TypeName = e.TypeName,
                Name = e.Name,
                Bootable = e.Bootable,
                Content = content,
                ContentName = contentName,
                Label = label,
                IsLast = e.Offset >= lastStart,
                SizeFieldOffset = e.SizeField,
            });
        }
        return new PartitionTable(scheme, sectorSize, list);
    }

    // -------------------------------------------------------------- content

    /// <summary>Recognises what starts at an offset of a disk image.</summary>
    internal static (PartitionContent Content, string Name, string Label) Identify(IBlockSource disk, long offset, long length)
    {
        long available = Math.Min(length, disk.Length - offset);
        if (available <= 0) return (PartitionContent.OutsideFile, "not in the file", "");
        using var part = new SubBlockSource(disk, offset, available, ownsInner: false);
        byte[] head = new byte[(int)Math.Min(available, 4096)];
        part.ReadExactly(0, head);

        static bool At(byte[] data, int position, ReadOnlySpan<byte> text) =>
            data.Length >= position + text.Length && data.AsSpan(position, text.Length).SequenceEqual(text);

        if (At(head, 3, "NTFS    "u8)) return (PartitionContent.Ntfs, "NTFS", "");
        if (At(head, 3, "EXFAT   "u8)) return (PartitionContent.ExFat, "exFAT", "");
        if (LooksLikeFat(head, out string fatKind, out string fatLabel)) return (PartitionContent.Fat, fatKind, fatLabel);
        if (SquashFsReader.IsSquashFs(part)) return (PartitionContent.SquashFs, "SquashFS", "");
        if (ExtReader.IsExt(part))
        {
            byte[] sb = part.ReadBytes(1024, 1024);
            uint compat = Le.U32(sb, 0x5C), incompat = Le.U32(sb, 0x60), roCompat = Le.U32(sb, 0x64);
            // Extents, 64bit, flex_bg or huge files make it ext4; a journal alone makes it ext3.
            string kind = (incompat & 0x2C0) != 0 || (roCompat & 0x478) != 0 ? "ext4" : (compat & 4) != 0 ? "ext3" : "ext2";
            return (PartitionContent.Ext, kind, CleanLabel(sb.AsSpan(0x78, 16)));
        }
        if (head.Length >= 1028 && Le.U32(head, 1024) == 0xF2F52010) return (PartitionContent.F2fs, "F2FS", "");
        if (At(head, 4086, "SWAPSPACE2"u8)) return (PartitionContent.Swap, "Linux swap", "");
        if (At(head, 0, "XFSB"u8)) return (PartitionContent.Xfs, "XFS", "");
        if (At(head, 0, [0x4C, 0x55, 0x4B, 0x53, 0xBA, 0xBE])) return (PartitionContent.Luks, "LUKS", "");
        if (At(head, 0, "UBI#"u8)) return (PartitionContent.Ubi, "UBI", "");
        if (available >= 0x10048)
        {
            byte[] magic = part.ReadBytes(0x10040, 8);
            if (magic.AsSpan().SequenceEqual("_BHRfS_M"u8)) return (PartitionContent.Btrfs, "Btrfs", "");
        }
        if (head.AsSpan().IndexOfAnyExcept((byte)0) < 0) return (PartitionContent.Empty, "empty", "");
        return (PartitionContent.Unknown, "", "");
    }

    private static bool LooksLikeFat(byte[] s, out string kind, out string label)
    {
        kind = label = "";
        if (s.Length < 512 || s[510] != 0x55 || s[511] != 0xAA) return false;
        if (s[0] != 0xEB && s[0] != 0xE9) return false;
        int bytesPerSector = Le.U16(s, 11), perCluster = s[13], fats = s[16];
        if (bytesPerSector is not (512 or 1024 or 2048 or 4096)) return false;
        if (perCluster == 0 || (perCluster & (perCluster - 1)) != 0 || fats is 0 or > 2 || Le.U16(s, 14) == 0) return false;
        bool fat32 = Le.U16(s, 22) == 0;            // sectors per FAT in the old field: zero only on FAT32
        int labelAt = fat32 ? 0x47 : 0x2B, kindAt = fat32 ? 0x52 : 0x36;
        string declared = Encoding.ASCII.GetString(s, kindAt, 8).Trim();
        kind = declared is "FAT12" or "FAT16" or "FAT32" ? declared : fat32 ? "FAT32" : "FAT";
        label = CleanLabel(s.AsSpan(labelAt, 11));
        if (label == "NO NAME") label = "";
        return true;
    }

    private static string CleanLabel(ReadOnlySpan<byte> raw)
    {
        int end = raw.IndexOf((byte)0);
        if (end >= 0) raw = raw[..end];
        var sb = new StringBuilder(raw.Length);
        foreach (byte b in raw) sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '?');
        return sb.ToString().Trim();
    }

    // ---------------------------------------------------------------- names

    private static string MbrTypeName(byte type)
    {
        string name = type switch
        {
            0x01 => "FAT12",
            0x04 or 0x06 or 0x0E => "FAT16",
            0x0B or 0x0C => "FAT32",
            0x07 => "NTFS/exFAT",
            0x2E => "OpenWrt data",
            0x82 => "Linux swap",
            0x83 => "Linux",
            0x8E => "Linux LVM",
            0xDA => "Non-FS data",
            0xEE => "GPT protective",
            0xEF => "EFI System",
            0xFD => "Linux RAID",
            _ => "type",
        };
        return $"{name} (0x{type:X2})";
    }

    private static string GptTypeName(Guid type) => type.ToString("D").ToUpperInvariant() switch
    {
        "C12A7328-F81F-11D2-BA4B-00A0C93EC93B" => "EFI System",
        "21686148-6449-6E6F-744E-656564454649" => "BIOS boot",
        "0FC63DAF-8483-4772-8E79-3D69D8477DE4" => "Linux filesystem",
        "0657FD6D-A4AB-43C4-84E5-0933C84B4F4F" => "Linux swap",
        "EBD0A0A2-B9E5-4433-87C0-68B6B72699C7" => "Microsoft basic data",
        "4F68BCE3-E8CD-4DB1-96E7-FBCAF984B709" => "Linux root (x86-64)",
        "B921B045-1DF0-41C3-AF44-4C6F280D3FAE" => "Linux root (ARM64)",
        "69DAD710-2CE4-4E3C-B16C-21A1D49ABED3" => "Linux root (ARM)",
        "44479540-F297-41B2-9AF7-D131D5F0458A" => "Linux root (x86)",
        "E6D6D379-F507-44C2-A23C-238F2A3DF928" => "Linux LVM",
        "933AC7E1-2EB4-4F13-B844-0E14E2AEF915" => "Linux home",
        string other => other,
    };
}

/// <summary>The disk image a filesystem was opened from, and the partition that holds it.</summary>
public sealed class DiskContainer
{
    internal DiskContainer(IBlockSource disk, PartitionTable table, PartitionInfo partition)
    {
        Disk = disk;
        Table = table;
        Partition = partition;
    }

    public PartitionTable Table { get; }
    public PartitionInfo Partition { get; }

    /// <summary>Size of the (uncompressed) disk image in bytes.</summary>
    public long DiskLength => Disk.Length;

    /// <summary>The whole disk image; owned by the document's tree.</summary>
    internal IBlockSource Disk { get; }

    /// <summary>
    /// The partition can be enlarged when the filesystem outgrows it: it is the last one, its size is
    /// a plain field of the MBR and nothing follows it in the file.
    /// </summary>
    public bool CanGrow => Partition.SizeFieldOffset >= 0 && Partition.IsLast && Disk.Length <= Partition.Offset + Partition.Length;
}

internal static class DiskText
{
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GiB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MiB",
        >= 1L << 10 => $"{bytes / 1024.0:0.#} KiB",
        _ => $"{bytes} B",
    };
}
