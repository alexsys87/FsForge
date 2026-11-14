using System.Globalization;
using FsForge.Core;
using FsForge.Core.Images;

namespace FsForge.Presentation.Services;

/// <summary>Text formatting shared by the view models.</summary>
public static class Fmt
{
    private static readonly string[] Units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };

    private static CultureInfo Culture => L.Culture;

    /// <summary>"0 Б", "12,3 КБ", "1,05 ГБ" (binary units); "12.3 KB" in English.</summary>
    public static string Size(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(Culture) + " " + L.T("Б");
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < Units.Length - 1)
        {
            v /= 1024;
            u++;
        }
        string fmt = v >= 100 ? "0" : v >= 10 ? "0.#" : "0.##";
        return v.ToString(fmt, Culture) + " " + L.T(Units[u]);
    }

    /// <summary>"12 345 678" with thin grouping.</summary>
    public static string Bytes(long bytes) => bytes.ToString("#,0", Culture);

    public static string SizeWithBytes(long bytes) => bytes < 1024 ? Size(bytes) : L.F("{0} ({1} байт)", Size(bytes), Bytes(bytes));

    /// <summary>What a partition holds: "SquashFS", "FAT32 «BOOT»", "неизвестное содержимое".</summary>
    public static string PartitionContent(PartitionInfo p)
    {
        string kind = p.Content switch
        {
            Core.Images.PartitionContent.Unknown => L.T("неизвестное содержимое"),
            Core.Images.PartitionContent.Empty => L.T("пусто"),
            Core.Images.PartitionContent.OutsideFile => L.T("за пределами файла"),
            _ => p.ContentName,
        };
        string label = p.Label.Length > 0 ? p.Label : p.Name;
        return label.Length > 0 ? L.F("{0} «{1}»", kind, label) : kind;
    }

    /// <summary>"Раздел 2 — SquashFS, 104 МБ".</summary>
    public static string Partition(PartitionInfo p) => L.F("Раздел {0} — {1}, {2}", p.Number, PartitionContent(p), Size(p.Length));

    /// <summary>"«rootfs» (1)", or the number alone for a volume without a name.</summary>
    public static string VolumeName(Core.Ubifs.UbiVolumeInfo v) => v.Name.Length > 0 ? L.F("«{0}» ({1})", v.Name, v.Id) : v.Id.ToString();

    /// <summary>What a UBI volume holds: "UBIFS", "SquashFS", "неизвестное содержимое".</summary>
    public static string VolumeContent(Core.Ubifs.UbiVolumeInfo v) => v.Content switch
    {
        Core.Images.PartitionContent.Empty => L.T("пусто"),
        _ when v.ContentName.Length > 0 => v.ContentName,
        _ => L.T("неизвестное содержимое"),
    };

    public static string PartitionScheme(PartitionTable t) => t.Scheme == Core.Images.PartitionScheme.Gpt ? "GPT" : "MBR";

    public const string DateFormat = "yyyy-MM-dd HH:mm:ss";

    public static DateTime? ToDateTime(long unixSeconds, bool utc)
    {
        try
        {
            DateTimeOffset t = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            return utc ? t.UtcDateTime : t.LocalDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static string Date(long unixSeconds, bool utc) =>
        ToDateTime(unixSeconds, utc)?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? unixSeconds.ToString(CultureInfo.InvariantCulture);

    /// <summary>Parses "yyyy-MM-dd HH:mm[:ss]" or a raw number of seconds since 1970.</summary>
    public static bool TryParseDate(string text, bool utc, out long unixSeconds)
    {
        text = text.Trim();
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out unixSeconds)) return true;
        string[] formats = { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd", "dd.MM.yyyy HH:mm:ss", "dd.MM.yyyy HH:mm", "dd.MM.yyyy" };
        DateTimeStyles style = utc ? DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal : DateTimeStyles.AssumeLocal;
        if (!DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, style, out DateTime dt)) return false;
        unixSeconds = new DateTimeOffset(dt.ToUniversalTime(), TimeSpan.Zero).ToUnixTimeSeconds();
        return true;
    }

    public static string TypeName(FsNodeType type) => type switch
    {
        FsNodeType.Directory => L.T("Папка"),
        FsNodeType.File => L.T("Файл"),
        FsNodeType.Symlink => L.T("Символическая ссылка"),
        FsNodeType.CharDevice => L.T("Символьное устройство"),
        FsNodeType.BlockDevice => L.T("Блочное устройство"),
        FsNodeType.Fifo => L.T("Именованный канал (FIFO)"),
        FsNodeType.Socket => L.T("Сокет"),
        _ => type.ToString(),
    };

    /// <summary>"5 файлов" in Russian, "5 files" in English: Plural(5, "файл", "файла", "файлов").</summary>
    public static string Plural(long n, string one, string few, string many) => L.Plural(n, one, few, many);

    /// <summary>Shows an attribute value as text when it is printable, otherwise as "0x..." hex.</summary>
    public static string XattrValue(byte[] value)
    {
        if (value.Length == 0) return "";
        ReadOnlySpan<byte> v = value;
        // A single trailing NUL is common (SELinux contexts) and is shown as "\0".
        bool trailingNul = v[^1] == 0;
        ReadOnlySpan<byte> body = trailingNul ? v[..^1] : v;
        bool printable = body.Length > 0;
        foreach (byte b in body)
        {
            if (b < 0x20 || b >= 0x7F || b == (byte)'\\')
            {
                printable = false;
                break;
            }
        }
        if (printable && !System.Text.Encoding.ASCII.GetString(body).StartsWith("0x", StringComparison.Ordinal))
            return System.Text.Encoding.ASCII.GetString(body) + (trailingNul ? "\\0" : "");
        return "0x" + Convert.ToHexString(value).ToLowerInvariant();
    }

    /// <summary>Inverse of <see cref="XattrValue"/>.</summary>
    public static bool TryParseXattrValue(string text, out byte[] value)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            string hex = text[2..].Replace(" ", "");
            try
            {
                value = hex.Length == 0 ? Array.Empty<byte>() : Convert.FromHexString(hex);
                return true;
            }
            catch (FormatException)
            {
                value = Array.Empty<byte>();
                return false;
            }
        }
        bool nul = text.EndsWith("\\0", StringComparison.Ordinal);
        if (nul) text = text[..^2];
        byte[] body = System.Text.Encoding.UTF8.GetBytes(text);
        value = nul ? body.Append((byte)0).ToArray() : body;
        return true;
    }
}

/// <summary>User and group names from the image's own /etc/passwd and /etc/group.</summary>
public sealed class IdNames
{
    private readonly Dictionary<uint, string> _users = new(), _groups = new();

    public static IdNames Empty { get; } = new();

    public static IdNames Load(FsTree tree)
    {
        var names = new IdNames();
        Read(tree.Resolve("/etc/passwd"), names._users);
        Read(tree.Resolve("/etc/group"), names._groups);
        return names;
    }

    private static void Read(FsNode? node, Dictionary<uint, string> map)
    {
        // Follow one level of symlink inside /etc (OpenWrt links passwd to /tmp only rarely, but be lenient).
        if (node is { Type: FsNodeType.File, Inode.Data: { Length: > 0 and < 1 << 20 } data })
        {
            try
            {
                using var reader = new StreamReader(data.OpenRead());
                while (reader.ReadLine() is { } line)
                {
                    string[] f = line.Split(':');
                    if (f.Length >= 3 && f[0].Length > 0 && uint.TryParse(f[2], out uint id)) map.TryAdd(id, f[0]);
                }
            }
            catch (Exception ex) when (ex is FsException or IOException)
            {
                // Names are a convenience only.
            }
        }
    }

    public string? User(uint uid) => _users.GetValueOrDefault(uid);
    public string? Group(uint gid) => _groups.GetValueOrDefault(gid);

    public string Owner(uint uid, uint gid, bool withNames)
    {
        if (!withNames) return $"{uid}:{gid}";
        string u = User(uid) is { } un ? $"{un} ({uid})" : uid.ToString();
        string g = Group(gid) is { } gn ? $"{gn} ({gid})" : gid.ToString();
        return u + " : " + g;
    }
}
