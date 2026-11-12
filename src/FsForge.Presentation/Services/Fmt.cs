using System.Globalization;
using FsForge.Core;
using FsForge.Core.Images;

namespace FsForge.Presentation.Services;

/// <summary>Text formatting shared by the view models.</summary>
public static class Fmt
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly string[] Units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };

    /// <summary>"0 Б", "12,3 КБ", "1,05 ГБ" (binary units).</summary>
    public static string Size(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(Ru) + " Б";
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < Units.Length - 1)
        {
            v /= 1024;
            u++;
        }
        string fmt = v >= 100 ? "0" : v >= 10 ? "0.#" : "0.##";
        return v.ToString(fmt, Ru) + " " + Units[u];
    }

    /// <summary>"12 345 678" with thin grouping.</summary>
    public static string Bytes(long bytes) => bytes.ToString("#,0", Ru);

    public static string SizeWithBytes(long bytes) => bytes < 1024 ? Size(bytes) : $"{Size(bytes)} ({Bytes(bytes)} байт)";

    /// <summary>What a partition holds: "SquashFS", "FAT32 «BOOT»", "неизвестное содержимое".</summary>
    public static string PartitionContent(PartitionInfo p)
    {
        string kind = p.Content switch
        {
            Core.Images.PartitionContent.Unknown => "неизвестное содержимое",
            Core.Images.PartitionContent.Empty => "пусто",
            Core.Images.PartitionContent.OutsideFile => "за пределами файла",
            _ => p.ContentName,
        };
        string label = p.Label.Length > 0 ? p.Label : p.Name;
        return label.Length > 0 ? $"{kind} «{label}»" : kind;
    }

    /// <summary>"Раздел 2 — SquashFS, 104 МБ".</summary>
    public static string Partition(PartitionInfo p) => $"Раздел {p.Number} — {PartitionContent(p)}, {Size(p.Length)}";

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
        FsNodeType.Directory => "Папка",
        FsNodeType.File => "Файл",
        FsNodeType.Symlink => "Символическая ссылка",
        FsNodeType.CharDevice => "Символьное устройство",
        FsNodeType.BlockDevice => "Блочное устройство",
        FsNodeType.Fifo => "Именованный канал (FIFO)",
        FsNodeType.Socket => "Сокет",
        _ => type.ToString(),
    };

    /// <summary>Russian plural: Plural(5, "файл", "файла", "файлов").</summary>
    public static string Plural(long n, string one, string few, string many)
    {
        long a = Math.Abs(n) % 100, b = a % 10;
        string word = a is >= 11 and <= 14 ? many : b == 1 ? one : b is >= 2 and <= 4 ? few : many;
        return $"{n.ToString("#,0", Ru)} {word}";
    }

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
