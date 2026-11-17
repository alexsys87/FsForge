using System.Buffers.Binary;

namespace FsForge.Core.ExFat;

/// <summary>Parameters of a new exFAT filesystem (the options of mkfs.exfat and of Windows' format).</summary>
public sealed class ExFatOptions
{
    /// <summary>Size of the filesystem; 0 makes it as small as the content allows (plus <see cref="ExtraFreeBytes"/>).</summary>
    public long SizeBytes { get; set; }

    /// <summary>Free space added to a filesystem whose size is chosen automatically.</summary>
    public long ExtraFreeBytes { get; set; }

    /// <summary>Bytes per cluster; 0 picks the size Windows uses for the filesystem size.</summary>
    public int ClusterSize { get; set; }

    public int SectorSize { get; set; } = 512;

    /// <summary>Volume label, up to 11 characters; empty for none.</summary>
    public string Label { get; set; } = "";

    /// <summary>Volume serial number; null makes a new one.</summary>
    public uint? VolumeSerial { get; set; }

    /// <summary>Sectors in front of the filesystem on the disk: the start of its partition (the PartitionOffset field).</summary>
    public long PartitionOffset { get; set; }

    /// <summary>Boot code of the source's boot sector (390 bytes), kept when an image is saved again.</summary>
    public byte[]? BootCode { get; set; }

    public ExFatOptions Clone() => (ExFatOptions)MemberwiseClone();

    /// <summary>Options that reproduce the layout of an existing filesystem.</summary>
    public static ExFatOptions FromInfo(ExFatInfo i) => new()
    {
        SizeBytes = i.SizeBytes,
        ClusterSize = i.ClusterSize,
        SectorSize = i.SectorSize,
        Label = i.Label,
        VolumeSerial = i.VolumeSerial,
        PartitionOffset = i.PartitionOffset,
        BootCode = i.BootCode,
    };

    public void Validate()
    {
        if (SectorSize is not (512 or 1024 or 2048 or 4096)) throw new FsException("The exFAT sector size must be 512, 1024, 2048 or 4096 bytes.");
        if (ClusterSize != 0 && (ClusterSize < SectorSize || ClusterSize > 32 << 20 || (ClusterSize & (ClusterSize - 1)) != 0))
            throw new FsException("The exFAT cluster size must be a power of two between the sector size and 32 MiB.");
        if (SizeBytes < 0 || ExtraFreeBytes < 0 || PartitionOffset < 0) throw new FsException("The exFAT size cannot be negative.");
        if (ExFatFormat.LabelProblem(Label) is { } why) throw new FsException("Invalid exFAT volume label: " + why + ".");
    }
}

/// <summary>Facts about an opened exFAT filesystem.</summary>
public sealed class ExFatInfo
{
    public int SectorSize { get; init; }
    public int ClusterSize { get; init; }
    public long TotalSectors { get; init; }
    public long SizeBytes => TotalSectors * SectorSize;
    public long Clusters { get; init; }
    public long FreeClusters { get; init; }
    public int Fats { get; init; }
    public long FatOffset { get; init; }
    public long FatLength { get; init; }
    public long ClusterHeapOffset { get; init; }
    public long PartitionOffset { get; init; }
    public string Label { get; init; } = "";
    public uint VolumeSerial { get; init; }
    public string Revision { get; init; } = "";
    public byte[]? BootCode { get; init; }

    /// <summary>The VolumeDirty flag is set: the volume was not unmounted properly.</summary>
    public bool Dirty { get; init; }

    /// <summary>The main boot region was damaged and the backup copy was used.</summary>
    public bool UsedBackupBootRegion { get; init; }
}

/// <summary>On-disk constants, the up-case table and the checksums of exFAT (Microsoft's specification).</summary>
public static class ExFatFormat
{
    public const int BootRegionSectors = 12, EntrySize = 32, MaxNameLength = 255, MaxLabelLength = 11;
    public const long MaxClusters = 0xFFFFFFF5;

    public const byte TypeBitmap = 0x81, TypeUpcase = 0x82, TypeLabel = 0x83, TypeFile = 0x85, TypeGuid = 0xA0,
        TypeStream = 0xC0, TypeName = 0xC1;

    public const ushort AttrReadOnly = 0x01, AttrHidden = 0x02, AttrSystem = 0x04, AttrDirectory = 0x10, AttrArchive = 0x20;
    public const byte FlagAllocationPossible = 0x01, FlagNoFatChain = 0x02;

    public static readonly byte[] Signature = "EXFAT   "u8.ToArray();

    /// <summary>Recognises the main boot sector by its jump instruction and file system name.</summary>
    public static bool HasSignature(ReadOnlySpan<byte> sector) =>
        sector.Length >= 512 && sector[0] == 0xEB && sector[1] == 0x76 && sector[2] == 0x90 && sector.Slice(3, 8).SequenceEqual(Signature);

    /// <summary>The checksum of the boot region: sectors 0-10, without VolumeFlags and PercentInUse.</summary>
    public static uint BootChecksum(ReadOnlySpan<byte> sectors0To10)
    {
        uint sum = 0;
        for (int i = 0; i < sectors0To10.Length; i++)
        {
            if (i is 106 or 107 or 112) continue;
            sum = ((sum & 1) != 0 ? 0x80000000u : 0) + (sum >> 1) + sectors0To10[i];
        }
        return sum;
    }

    /// <summary>The 32-bit checksum of the up-case table.</summary>
    public static uint TableChecksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        foreach (byte b in data) sum = ((sum & 1) != 0 ? 0x80000000u : 0) + (sum >> 1) + b;
        return sum;
    }

    /// <summary>The 16-bit checksum of a directory entry set, without bytes 2 and 3 of the first entry (the checksum itself).</summary>
    public static ushort SetChecksum(ReadOnlySpan<byte> set)
    {
        ushort sum = 0;
        for (int i = 0; i < set.Length; i++)
        {
            if (i is 2 or 3) continue;
            sum = (ushort)(((sum & 1) != 0 ? 0x8000 : 0) + (sum >> 1) + set[i]);
        }
        return sum;
    }

    /// <summary>The hash of an up-cased name, as the stream extension entry keeps it.</summary>
    public static ushort NameHash(string name)
    {
        ushort hash = 0;
        foreach (char c in name)
        {
            ushort u = Upcase(c);
            hash = (ushort)(((hash & 1) != 0 ? 0x8000 : 0) + (hash >> 1) + (u & 0xFF));
            hash = (ushort)(((hash & 1) != 0 ? 0x8000 : 0) + (hash >> 1) + (u >> 8));
        }
        return hash;
    }

    /// <summary>The up-case mapping of the table this program writes: Unicode's simple upper case of the BMP.</summary>
    public static ushort Upcase(int c) => c is >= 0xD800 and <= 0xDFFF ? (ushort)c : char.ToUpperInvariant((char)c);

    private static readonly Lazy<byte[]> Table = new(BuildUpcaseTable);

    /// <summary>The up-case table in the compressed form of the specification (identity runs as 0xFFFF and a length).</summary>
    public static byte[] UpcaseTable => Table.Value;

    private static byte[] BuildUpcaseTable()
    {
        var table = new List<ushort>();
        int c = 0;
        while (c <= 0xFFFF)
        {
            int run = 0;
            while (c + run <= 0xFFFF && Upcase(c + run) == c + run) run++;
            // A literal 0xFFFF would read as a run marker, so the last run is always compressed.
            if (run > 1 || (run == 1 && c == 0xFFFF))
            {
                table.Add(0xFFFF);
                table.Add((ushort)run);
                c += run;
            }
            else
            {
                table.Add(Upcase(c));
                c++;
            }
        }
        byte[] bytes = new byte[table.Count * 2];
        for (int i = 0; i < table.Count; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), table[i]);
        return bytes;
    }

    /// <summary>Characters never allowed in a file name.</summary>
    private const string Forbidden = "\"*/:<>?\\|";

    /// <summary>Why a name cannot be stored in exFAT, or null when it can.</summary>
    public static string? NameProblem(string name)
    {
        if (name.Length == 0) return "it is empty";
        if (name.Length > MaxNameLength) return $"it is longer than {MaxNameLength} characters";
        foreach (char c in name)
            if (c < 0x20 || Forbidden.Contains(c)) return $"it contains '{(c < 0x20 ? $"\\x{(int)c:X2}" : c.ToString())}'";
        if (name is "." or "..") return "it is reserved";
        return null;
    }

    public static string? LabelProblem(string label)
    {
        if (label.Length > MaxLabelLength) return $"it has more than {MaxLabelLength} characters";
        foreach (char c in label)
            if (c < 0x20 || Forbidden.Contains(c)) return $"it contains '{(c < 0x20 ? $"\\x{(int)c:X2}" : c.ToString())}'";
        return null;
    }

    /// <summary>Cluster size Windows picks for exFAT volumes of this size.</summary>
    public static int DefaultClusterSize(long bytes) => bytes <= 256L << 20 ? 4096 : bytes <= 32L << 30 ? 32768 : 131072;

    // ------------------------------------------------------------ times

    public const long MinTime = 315532800;            // 1980-01-01 00:00:00 UTC
    public const long MaxTime = 4354819199;           // 2107-12-31 23:59:59 UTC

    /// <summary>
    /// A timestamp with its 10 ms increment and UTC offset as Unix time. Without a valid offset the time is
    /// taken as UTC, as FAT times are in this program.
    /// </summary>
    public static long ToUnix(uint stamp, byte tenMs, byte utcOffset)
    {
        int year = 1980 + (int)(stamp >> 25), month = (int)(stamp >> 21) & 15, day = (int)(stamp >> 16) & 31;
        int hour = (int)(stamp >> 11) & 31, minute = (int)(stamp >> 5) & 63, second = (int)(stamp & 31) * 2;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
            return MinTime;
        long t = new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero).ToUnixTimeSeconds() + Math.Min((int)tenMs, 199) / 100;
        if ((utcOffset & 0x80) != 0)
        {
            int quarters = (sbyte)(byte)(utcOffset << 1) >> 1;      // 7-bit signed, in 15-minute units
            t -= quarters * 15 * 60L;
        }
        return t;
    }

    /// <summary>The timestamp, its 10 ms increment and the UTC offset (UTC itself) of a Unix time.</summary>
    public static (uint Stamp, byte TenMs, byte UtcOffset) FromUnix(long unix)
    {
        DateTime t = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(unix, MinTime, MaxTime)).UtcDateTime;
        uint stamp = (uint)((t.Year - 1980) << 25 | t.Month << 21 | t.Day << 16 | t.Hour << 11 | t.Minute << 5 | t.Second / 2);
        return (stamp, (byte)(t.Second % 2 * 100), 0x80);
    }
}
