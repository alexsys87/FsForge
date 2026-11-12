using System.Text;
using System.Text.Json;

namespace FsForge.Core.Host;

/// <summary>
/// Metadata of one extracted object. Windows folders cannot hold Unix owners, modes, device
/// nodes or arbitrary names, so the extractor records them here and the importer puts them back.
/// </summary>
public sealed class ManifestEntry
{
    /// <summary>Real name inside the image when it differs from the name used on the host.</summary>
    public string? Name;

    public FsNodeType Type;
    public int Mode;
    public uint Uid, Gid;
    public long MTime;
    public string? LinkTarget;
    public uint DevMajor, DevMinor;
    public SortedDictionary<string, byte[]>? XAttrs;

    /// <summary>Objects with the same non-zero number were hard links of one file.</summary>
    public int HardLinkGroup;

    /// <summary>Size and last-write time of the host file right after extraction; used to notice later edits.</summary>
    public long HostSize = -1;
    public long HostTicks;

    public static ManifestEntry FromInode(FsInode inode) => new()
    {
        Type = inode.Type,
        Mode = inode.Mode & 0xFFF,
        Uid = inode.Uid,
        Gid = inode.Gid,
        MTime = inode.MTime,
        LinkTarget = inode.LinkTarget,
        DevMajor = inode.DevMajor,
        DevMinor = inode.DevMinor,
        XAttrs = inode.XAttrs is { Count: > 0 } ? new SortedDictionary<string, byte[]>(inode.XAttrs, StringComparer.Ordinal) : null,
    };

    public void ApplyTo(FsInode inode)
    {
        inode.Mode = Mode & 0xFFF;
        inode.Uid = Uid;
        inode.Gid = Gid;
        inode.MTime = MTime;
        inode.XAttrs = XAttrs is { Count: > 0 } ? new SortedDictionary<string, byte[]>(XAttrs, StringComparer.Ordinal) : null;
    }
}

/// <summary>
/// The ".fsforge-meta.json" file stored in the top folder of an extracted tree.
/// Keys are host paths relative to that folder with '/' separators; "" is the folder itself.
/// </summary>
public sealed class HostManifest
{
    public const string FileName = ".fsforge-meta.json";
    private const int CurrentVersion = 1;

    public Dictionary<string, ManifestEntry> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Highest hard link group number in use.</summary>
    public int LastHardLinkGroup { get; set; }

    public static string PathIn(string directory) => Path.Combine(directory, FileName);

    private static char TypeChar(FsNodeType t) => t switch
    {
        FsNodeType.Directory => 'd',
        FsNodeType.File => 'f',
        FsNodeType.Symlink => 'l',
        FsNodeType.CharDevice => 'c',
        FsNodeType.BlockDevice => 'b',
        FsNodeType.Fifo => 'p',
        _ => 's',
    };

    private static FsNodeType? TypeFromChar(string? s) => s switch
    {
        "d" => FsNodeType.Directory,
        "f" => FsNodeType.File,
        "l" => FsNodeType.Symlink,
        "c" => FsNodeType.CharDevice,
        "b" => FsNodeType.BlockDevice,
        "p" => FsNodeType.Fifo,
        "s" => FsNodeType.Socket,
        _ => null,
    };

    /// <summary>True when the string survives a JSON round trip (no unpaired surrogates).</summary>
    private static bool IsCleanText(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
            else if (char.IsSurrogate(s[i])) return false;
        }
        return true;
    }

    private static void WriteText(Utf8JsonWriter w, string property, string value)
    {
        // Names and link targets may hold bytes that are not valid UTF-8; those go out as base64.
        if (IsCleanText(value)) w.WriteString(property, value);
        else w.WriteString(property + "64", Convert.ToBase64String(PosixName.Encode(value)));
    }

    private static string? ReadText(JsonElement e, string property)
    {
        if (e.TryGetProperty(property, out JsonElement v) && v.ValueKind == JsonValueKind.String) return v.GetString();
        if (e.TryGetProperty(property + "64", out JsonElement b) && b.ValueKind == JsonValueKind.String)
        {
            try { return PosixName.Decode(Convert.FromBase64String(b.GetString()!)); }
            catch (FormatException) { return null; }
        }
        return null;
    }

    public void Save(string directory)
    {
        string path = PathIn(directory);
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("format", "fsforge-meta");
            w.WriteNumber("version", CurrentVersion);
            w.WriteString("note", "Unix metadata of the extracted files. Keep this file next to them to repack the folder without losing owners, modes, links and device nodes.");
            w.WriteStartObject("entries");
            foreach ((string key, ManifestEntry e) in Entries.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                w.WriteStartObject(key);
                if (e.Name is not null) WriteText(w, "name", e.Name);
                w.WriteString("type", TypeChar(e.Type).ToString());
                w.WriteString("mode", UnixMode.ToOctal(e.Mode));
                w.WriteNumber("uid", e.Uid);
                w.WriteNumber("gid", e.Gid);
                w.WriteNumber("mtime", e.MTime);
                if (e.Type == FsNodeType.Symlink) WriteText(w, "link", e.LinkTarget ?? "");
                if (e.Type is FsNodeType.CharDevice or FsNodeType.BlockDevice)
                {
                    w.WriteNumber("major", e.DevMajor);
                    w.WriteNumber("minor", e.DevMinor);
                }
                if (e.HardLinkGroup != 0) w.WriteNumber("hardlink", e.HardLinkGroup);
                if (e.HostSize >= 0)
                {
                    w.WriteNumber("hostSize", e.HostSize);
                    w.WriteNumber("hostTime", e.HostTicks);
                }
                if (e.XAttrs is { Count: > 0 })
                {
                    w.WriteStartObject("xattrs");
                    foreach ((string name, byte[] value) in e.XAttrs)
                        if (IsCleanText(name)) w.WriteString(name, Convert.ToBase64String(value));
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Loads the manifest of a folder; null when there is none or it cannot be parsed.</summary>
    public static HostManifest? TryLoad(string directory)
    {
        string path = PathIn(directory);
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using JsonDocument doc = JsonDocument.Parse(fs, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("entries", out JsonElement entries) || entries.ValueKind != JsonValueKind.Object)
                return null;
            var m = new HostManifest();
            foreach (JsonProperty p in entries.EnumerateObject())
            {
                JsonElement e = p.Value;
                if (e.ValueKind != JsonValueKind.Object) continue;
                FsNodeType? type = TypeFromChar(e.TryGetProperty("type", out JsonElement t) ? t.GetString() : null);
                if (type is null) continue;
                var entry = new ManifestEntry { Type = type.Value, Name = ReadText(e, "name") };
                if (e.TryGetProperty("mode", out JsonElement mode) && mode.ValueKind == JsonValueKind.String &&
                    UnixMode.TryParseOctal(mode.GetString()!, out int m8)) entry.Mode = m8;
                else entry.Mode = type == FsNodeType.Directory ? 0x1ED : 0x1A4;
                entry.Uid = GetUInt(e, "uid");
                entry.Gid = GetUInt(e, "gid");
                entry.MTime = GetLong(e, "mtime", 0);
                entry.LinkTarget = ReadText(e, "link");
                entry.DevMajor = GetUInt(e, "major");
                entry.DevMinor = GetUInt(e, "minor");
                entry.HardLinkGroup = (int)GetLong(e, "hardlink", 0);
                entry.HostSize = GetLong(e, "hostSize", -1);
                entry.HostTicks = GetLong(e, "hostTime", 0);
                if (e.TryGetProperty("xattrs", out JsonElement xa) && xa.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty x in xa.EnumerateObject())
                    {
                        if (x.Value.ValueKind != JsonValueKind.String) continue;
                        try
                        {
                            (entry.XAttrs ??= new(StringComparer.Ordinal))[x.Name] = Convert.FromBase64String(x.Value.GetString()!);
                        }
                        catch (FormatException)
                        {
                            // A hand-edited value that is not base64: drop this attribute only.
                        }
                    }
                }
                m.Entries[p.Name] = entry;
                m.LastHardLinkGroup = Math.Max(m.LastHardLinkGroup, entry.HardLinkGroup);
            }
            return m;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private static long GetLong(JsonElement e, string name, long fallback) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long r) ? r : fallback;

    private static uint GetUInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out uint r) ? r : 0;
}

/// <summary>Turns POSIX names into names that Windows accepts and that do not collide in a folder.</summary>
public static class HostNames
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>True when the name can be used on Windows exactly as it is.</summary>
    public static bool IsSafe(string name) => MakeSafe(name) == name;

    /// <summary>Replaces what Windows forbids: reserved characters, device names, trailing dots and spaces.</summary>
    public static string MakeSafe(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsHighSurrogate(c) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1]))
            {
                sb.Append(c).Append(name[++i]);
            }
            else if (char.IsSurrogate(c))
            {
                // A byte that was not valid UTF-8 (see PosixName) or a stray surrogate.
                sb.Append('%').Append(((c >= 0xDC80 && c <= 0xDCFF ? c - 0xDC00 : '?') & 0xFF).ToString("X2"));
            }
            else if (c < 0x20 || c == 0x7F || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
            {
                sb.Append('%').Append(((int)c).ToString("X2"));
            }
            else
            {
                sb.Append(c);
            }
        }
        if (sb.Length > 0 && sb[^1] is '.' or ' ')
        {
            char last = sb[^1];
            sb.Length--;
            sb.Append('%').Append(((int)last).ToString("X2"));
        }
        if (sb.Length == 0) sb.Append('_');

        string result = sb.ToString();
        int dot = result.IndexOf('.');
        string stem = dot < 0 ? result : result[..dot];
        if (Reserved.Contains(stem.TrimEnd(' '))) result = "_" + result;
        if (result.Length > 240) result = result[..240];
        return result;
    }

    /// <summary>
    /// Returns a safe name that is not in <paramref name="used"/> yet (compared without case, as
    /// Windows does) and adds it to the set.
    /// </summary>
    public static string MakeUnique(string name, HashSet<string> used)
    {
        string safe = MakeSafe(name);
        if (used.Add(safe)) return safe;
        int dot = safe.LastIndexOf('.');
        string stem = dot > 0 ? safe[..dot] : safe;
        string ext = dot > 0 ? safe[dot..] : "";
        for (int i = 2; ; i++)
        {
            string candidate = $"{stem}~{i}{ext}";
            if (used.Add(candidate)) return candidate;
        }
    }
}
