using System.Text;

namespace FsForge.Core.Fat;

public enum FatType
{
    /// <summary>Chosen from the size: FAT12 for floppies, FAT16 up to 512 MiB, FAT32 beyond.</summary>
    Auto,
    Fat12,
    Fat16,
    Fat32,
}

/// <summary>Parameters of a new FAT filesystem (the options of mkfs.fat).</summary>
public sealed class FatOptions
{
    public FatType Type { get; set; } = FatType.Auto;

    /// <summary>Size of the filesystem; 0 makes it as small as the content allows (plus <see cref="ExtraFreeBytes"/>).</summary>
    public long SizeBytes { get; set; }

    /// <summary>Free space added to a filesystem whose size is chosen automatically.</summary>
    public long ExtraFreeBytes { get; set; }

    /// <summary>Bytes per cluster; 0 picks the usual size for the filesystem size.</summary>
    public int ClusterSize { get; set; }

    public int SectorSize { get; set; } = 512;

    /// <summary>Volume label, up to 11 characters; empty for none.</summary>
    public string Label { get; set; } = "";

    /// <summary>Volume serial number; null makes a new one.</summary>
    public uint? VolumeId { get; set; }

    /// <summary>Entries of the fixed root directory of FAT12/16; 0 picks 512 (or more when the root needs it).</summary>
    public int RootEntries { get; set; }

    /// <summary>Reserved sectors in front of the first FAT; 0 picks 1 for FAT12/16 and 32 for FAT32.</summary>
    public int ReservedSectors { get; set; }

    public int Fats { get; set; } = 2;
    public byte Media { get; set; } = 0xF8;

    /// <summary>Sectors in front of the filesystem on the disk: the start of its partition.</summary>
    public uint HiddenSectors { get; set; }

    public int SectorsPerTrack { get; set; } = 63;
    public int Heads { get; set; } = 255;
    public string OemName { get; set; } = "MSWIN4.1";

    /// <summary>Boot code of the source's boot sector, kept when an image is saved again.</summary>
    public byte[]? BootCode { get; set; }

    public FatOptions Clone() => (FatOptions)MemberwiseClone();

    /// <summary>Options that reproduce the layout of an existing filesystem.</summary>
    public static FatOptions FromInfo(FatInfo i) => new()
    {
        Type = i.Type,
        SizeBytes = i.SizeBytes,
        ClusterSize = i.ClusterSize,
        SectorSize = i.SectorSize,
        Label = i.Label,
        VolumeId = i.VolumeId,
        RootEntries = i.RootEntries,
        ReservedSectors = i.ReservedSectors,
        Fats = i.Fats,
        Media = i.Media,
        HiddenSectors = i.HiddenSectors,
        SectorsPerTrack = i.SectorsPerTrack > 0 ? i.SectorsPerTrack : 63,
        Heads = i.Heads > 0 ? i.Heads : 255,
        OemName = i.OemName.Length > 0 ? i.OemName : "MSWIN4.1",
        BootCode = i.BootCode,
    };

    public void Validate()
    {
        if (SectorSize is not (512 or 1024 or 2048 or 4096)) throw new FsException("The FAT sector size must be 512, 1024, 2048 or 4096 bytes.");
        if (ClusterSize != 0 && (ClusterSize < SectorSize || ClusterSize > 64 * 1024 || (ClusterSize & (ClusterSize - 1)) != 0))
            throw new FsException("The FAT cluster size must be a power of two between the sector size and 64 KiB.");
        if (SizeBytes < 0 || ExtraFreeBytes < 0) throw new FsException("The FAT size cannot be negative.");
        if (Fats is < 1 or > 2) throw new FsException("A FAT filesystem has one or two FATs.");
        if (RootEntries is < 0 or > 65535 || ReservedSectors is < 0 or > 65535) throw new FsException("Invalid FAT layout parameters.");
        if (Label.Length > 11 || Label.Any(c => c < 0x20 || c > 0x7E || "\"*+,./:;<=>?[\\]|".Contains(c)))
            throw new FsException("A FAT volume label has at most 11 characters: letters, digits, spaces and !#$%&'()-@^_`{}~.");
    }
}

/// <summary>Facts about an opened FAT filesystem.</summary>
public sealed class FatInfo
{
    public FatType Type { get; init; }
    public int SectorSize { get; init; }
    public int ClusterSize { get; init; }
    public long TotalSectors { get; init; }
    public long SizeBytes => TotalSectors * SectorSize;
    public long Clusters { get; init; }
    public long FreeClusters { get; init; }
    public int Fats { get; init; }
    public long FatSectors { get; init; }
    public int ReservedSectors { get; init; }
    public int RootEntries { get; init; }
    public string Label { get; init; } = "";
    public uint VolumeId { get; init; }
    public string OemName { get; init; } = "";
    public byte Media { get; init; }
    public uint HiddenSectors { get; init; }
    public int SectorsPerTrack { get; init; }
    public int Heads { get; init; }
    public byte[]? BootCode { get; init; }

    /// <summary>The "clean shutdown" bit of FAT16/32 is not set: the volume was not unmounted properly.</summary>
    public bool Dirty { get; init; }

    public string TypeName => Type switch { FatType.Fat12 => "FAT12", FatType.Fat16 => "FAT16", _ => "FAT32" };
}

/// <summary>On-disk constants and name rules shared by the FAT reader and writer.</summary>
internal static class FatFormat
{
    public const byte AttrReadOnly = 0x01, AttrHidden = 0x02, AttrSystem = 0x04, AttrVolume = 0x08,
        AttrDirectory = 0x10, AttrArchive = 0x20, AttrLongName = 0x0F;

    public const byte CaseLowerBase = 0x08, CaseLowerExt = 0x10;

    public const int MinFat16Clusters = 4085, MinFat32Clusters = 65525;
    public const long MaxFat32Clusters = 0x0FFFFFF5;

    /// <summary>The type of a FAT is decided by its number of clusters alone (Microsoft's rule, which every driver follows).</summary>
    public static FatType TypeFor(long clusters) =>
        clusters < MinFat16Clusters ? FatType.Fat12 : clusters < MinFat32Clusters ? FatType.Fat16 : FatType.Fat32;

    private static readonly Encoding Oem = CreateOem();

    private static Encoding CreateOem()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(437);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1;
        }
    }

    /// <summary>Short names keep characters beyond ASCII in the OEM code page; code page 437 is assumed.</summary>
    public static string DecodeOem(ReadOnlySpan<byte> b) => Oem.GetString(b);

    /// <summary>The checksum of a short name that ties long-name entries to it.</summary>
    public static byte ShortNameChecksum(ReadOnlySpan<byte> name11)
    {
        byte sum = 0;
        foreach (byte c in name11) sum = (byte)(((sum & 1) << 7) + (sum >> 1) + c);
        return sum;
    }

    /// <summary>Characters allowed in a short name besides letters and digits.</summary>
    private const string ShortSpecial = "$%'-_@~`!(){}^#&";

    /// <summary>Characters never allowed in a long name.</summary>
    private const string LongForbidden = "\"*/:<>?\\|";

    public static bool IsShortChar(char c) => c is >= 'A' and <= 'Z' or >= '0' and <= '9' || ShortSpecial.Contains(c);

    /// <summary>Why a name cannot be stored in FAT, or null when it can.</summary>
    public static string? LongNameProblem(string name)
    {
        if (name.Length > 255) return "it is longer than 255 characters";
        foreach (char c in name)
            if (c < 0x20 || LongForbidden.Contains(c)) return $"it contains '{(c < 0x20 ? $"\\x{(int)c:X2}" : c.ToString())}'";
        if (name.EndsWith(' ') || name.EndsWith('.')) return "it ends with a space or a dot";
        return null;
    }

    /// <summary>
    /// The name as an exact upper-case 8.3 name ("CONFIG.TXT"), or null when it does not have that shape.
    /// </summary>
    public static string? AsShortName(string name)
    {
        string upper = name.ToUpperInvariant();
        int dot = upper.IndexOf('.');
        string b = dot < 0 ? upper : upper[..dot], e = dot < 0 ? "" : upper[(dot + 1)..];
        if (b.Length is < 1 or > 8 || e.Length > 3 || e.Contains('.') || (dot >= 0 && e.Length == 0)) return null;
        return b.All(IsShortChar) && e.All(IsShortChar) ? upper : null;
    }

    /// <summary>"CONFIG.TXT" as the 11 bytes of a directory entry ("CONFIG  TXT").</summary>
    public static byte[] Pack83(string shortName)
    {
        byte[] r = Enumerable.Repeat((byte)' ', 11).ToArray();
        int dot = shortName.IndexOf('.');
        string b = dot < 0 ? shortName : shortName[..dot], e = dot < 0 ? "" : shortName[(dot + 1)..];
        for (int i = 0; i < b.Length; i++) r[i] = (byte)b[i];
        for (int i = 0; i < e.Length; i++) r[8 + i] = (byte)e[i];
        if (r[0] == 0xE5) r[0] = 0x05;
        return r;
    }

    /// <summary>
    /// The basis of a generated short name (Windows' algorithm): upper case, without spaces and leading dots,
    /// other characters that are not allowed replaced by '_'.
    /// </summary>
    public static (string Base, string Ext) ShortBasis(string name)
    {
        string u = name.ToUpperInvariant().Replace(" ", "").TrimStart('.');
        int dot = u.LastIndexOf('.');
        string b = dot < 0 ? u : u[..dot].Replace(".", ""), e = dot < 0 ? "" : u[(dot + 1)..];
        static string Clean(string s) => new(s.Select(c => IsShortChar(c) ? c : '_').ToArray());
        b = Clean(b);
        e = Clean(e);
        if (b.Length == 0) b = "_";
        return (b.Length > 8 ? b[..8] : b, e.Length > 3 ? e[..3] : e);
    }

    // ------------------------------------------------------------ times

    /// <summary>FAT keeps local time without a zone; it is taken as UTC (as Linux does with tz=UTC and in containers).</summary>
    public const long MinTime = 315532800;            // 1980-01-01 00:00:00
    public const long MaxTime = 4354819198;           // 2107-12-31 23:59:58

    public static long ToUnix(ushort date, ushort time)
    {
        int year = 1980 + (date >> 9), month = (date >> 5) & 15, day = date & 31;
        int hour = time >> 11, minute = (time >> 5) & 63, second = (time & 31) * 2;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
            return MinTime;
        return new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero).ToUnixTimeSeconds();
    }

    public static (ushort Date, ushort Time) FromUnix(long unix)
    {
        DateTime t = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(unix, MinTime, MaxTime)).UtcDateTime;
        ushort date = (ushort)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day);
        ushort time = (ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2));
        return (date, time);
    }
}
