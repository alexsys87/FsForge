using System.Text.Json;
using System.Text.Json.Serialization;
using FsForge.Core.Ext;
using FsForge.Core.Host;
using FsForge.Core.Images;
using FsForge.Core.SquashFs;
using FsForge.Core.Tar;
using FsForge.Presentation.Services;

namespace FsForge.Presentation.Settings;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public enum BackdropKind
{
    Mica,
    Acrylic,
    Tabbed,
    None,
}

/// <summary>What to do when an added file has the name of an existing one.</summary>
public enum ConflictMode
{
    Ask,
    Overwrite,
    Skip,
    Rename,
}

public enum SizeDisplay
{
    /// <summary>"12,3 МБ"</summary>
    Units,

    /// <summary>"12 345 678"</summary>
    Bytes,
}

/// <summary>What a double click on a file does.</summary>
public enum FileOpenAction
{
    Properties,
    OpenExternally,
}

/// <summary>All user preferences. Stored as JSON in %AppData%\FsForge\settings.json.</summary>
public sealed class AppSettings
{
    // ---- appearance
    /// <summary>Interface language; applied at the next start of the program.</summary>
    public AppLanguage Language { get; set; } = AppLanguage.System;
    public AppTheme Theme { get; set; } = AppTheme.System;
    public BackdropKind Backdrop { get; set; } = BackdropKind.Mica;
    public bool ShowTreePane { get; set; } = true;
    public double TreePaneWidth { get; set; } = 270;
    public bool FoldersFirst { get; set; } = true;
    public SizeDisplay Sizes { get; set; } = SizeDisplay.Units;
    public bool UtcTimes { get; set; }
    public bool ShowOwnerNames { get; set; } = true;
    public string SortColumn { get; set; } = "Name";
    public bool SortDescending { get; set; }

    // ---- window
    public bool RememberWindow { get; set; } = true;
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 740;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool WindowMaximized { get; set; }

    // ---- behaviour
    public bool ConfirmDelete { get; set; } = true;
    public bool ConfirmDiscardChanges { get; set; } = true;
    public FileOpenAction DoubleClickFile { get; set; } = FileOpenAction.OpenExternally;
    public string TextEditor { get; set; } = "notepad.exe";
    public List<string> RecentFiles { get; set; } = new();
    public int RecentLimit { get; set; } = 12;
    public string TempDirectory { get; set; } = "";
    public string LastOpenDirectory { get; set; } = "";
    public string LastSaveDirectory { get; set; } = "";
    public string LastHostDirectory { get; set; } = "";

    // ---- opening
    public bool SearchEmbedded { get; set; } = true;
    public bool ShowWarningsAfterOpen { get; set; } = true;

    // ---- saving
    public VerifyLevel Verify { get; set; } = VerifyLevel.Structure;
    public bool KeepBackup { get; set; } = true;
    public bool AlwaysAskSaveOptions { get; set; }

    /// <summary>Worker threads for compression; 0 = one per processor.</summary>
    public int Threads { get; set; }

    public ImageFormat NewImageFormat { get; set; } = ImageFormat.SquashFs;
    public SquashFsOptions SquashFs { get; set; } = new();
    public ExtOptions Ext { get; set; } = new();
    public TarOptions Tar { get; set; } = new();

    /// <summary>Defaults of a new UBIFS image: the geometry of the flash it is made for, and the compressor.</summary>
    public FsForge.Core.Ubifs.UbifsOptions Ubifs { get; set; } = new();

    /// <summary>Defaults of a new JFFS2 image: byte order and erase block size of the target, compressor.</summary>
    public FsForge.Core.Jffs2.Jffs2Options Jffs2 { get; set; } = new();

    /// <summary>Defaults of a new FAT image: type, cluster size, label.</summary>
    public FsForge.Core.Fat.FatOptions Fat { get; set; } = new();

    /// <summary>Defaults of a new UBI image.</summary>
    public FsForge.Core.Ubifs.UbiOptions Ubi { get; set; } = new();

    /// <summary>Free space added to an ext image whose size is chosen automatically, in MiB.</summary>
    public int ExtExtraFreeMiB { get; set; }

    // ---- extracting
    public bool WriteManifest { get; set; } = true;
    public SymlinkExtractMode Symlinks { get; set; } = SymlinkExtractMode.Placeholder;
    public bool SpecialPlaceholders { get; set; } = true;
    public bool RestoreTimestamps { get; set; } = true;
    public bool OverwriteOnExtract { get; set; } = true;
    public bool OpenFolderAfterExtract { get; set; } = true;

    // ---- adding
    public bool UseManifest { get; set; } = true;
    public string DefaultFileMode { get; set; } = "0644";
    public string DefaultDirectoryMode { get; set; } = "0755";
    public string ExecutableMode { get; set; } = "0755";
    public uint DefaultUid { get; set; }
    public uint DefaultGid { get; set; }
    public bool InheritOwner { get; set; } = true;
    public bool DetectExecutables { get; set; } = true;
    public bool UseHostTimestamps { get; set; } = true;
    public ConflictMode Conflicts { get; set; } = ConflictMode.Ask;
    public bool SkipHiddenAndSystem { get; set; }
    public bool SnapshotAddedFiles { get; set; }

    // ------------------------------------------------------------ helpers

    public ExtractOptions ToExtractOptions() => new()
    {
        WriteManifest = WriteManifest,
        Symlinks = Symlinks,
        SpecialFilePlaceholders = SpecialPlaceholders,
        RestoreTimestamps = RestoreTimestamps,
        OverwriteExisting = OverwriteOnExtract,
    };

    public ImportOptions ToImportOptions(ConflictPolicy conflicts) => new()
    {
        UseManifest = UseManifest,
        DefaultFileMode = ParseMode(DefaultFileMode, 0x1A4),
        DefaultDirectoryMode = ParseMode(DefaultDirectoryMode, 0x1ED),
        ExecutableMode = ParseMode(ExecutableMode, 0x1ED),
        DefaultUid = DefaultUid,
        DefaultGid = DefaultGid,
        InheritOwner = InheritOwner,
        DetectExecutables = DetectExecutables,
        UseHostTimestamps = UseHostTimestamps,
        Conflicts = conflicts,
        SkipHiddenAndSystem = SkipHiddenAndSystem,
    };

    private static int ParseMode(string text, int fallback) =>
        FsForge.Core.UnixMode.TryParseOctal(text, out int mode) ? mode : fallback;

    /// <summary>Save parameters for a document: the source's own where the format is kept, the defaults otherwise.</summary>
    public SaveOptions DefaultSaveOptions(ImageDocument doc, ImageFormat? format = null)
    {
        var defaults = new SaveOptions
        {
            SquashFs = SquashFs.Clone(),
            Ext = Ext.Clone(),
            Tar = Tar.Clone(),
            Ubifs = Ubifs.Clone(),
            Jffs2 = Jffs2.Clone(),
            Fat = Fat.Clone(),
            Ubi = Ubi.Clone(),
        };
        defaults.Ext.ExtraFreeBytes = (long)ExtExtraFreeMiB << 20;
        SaveOptions o = doc.CreateSaveOptions(format, defaults);
        ApplyCommon(o);
        return o;
    }

    public void ApplyCommon(SaveOptions o)
    {
        o.Verify = Verify;
        o.KeepBackup = KeepBackup;
        o.TempDirectory = string.IsNullOrWhiteSpace(TempDirectory) ? null : TempDirectory;
        o.SquashFs.Threads = Threads;
        o.Tar.Threads = Threads;
    }

    public string? EffectiveTempDirectory => string.IsNullOrWhiteSpace(TempDirectory) ? null : TempDirectory;

    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > Math.Max(0, RecentLimit)) RecentFiles.RemoveRange(RecentLimit, RecentFiles.Count - RecentLimit);
    }

    public AppSettings Clone() => SettingsStore.Deserialize(SettingsStore.Serialize(this)) ?? new AppSettings();
}

/// <summary>Loads and saves <see cref="AppSettings"/>.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public SettingsStore(string? path = null)
    {
        FilePath = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FsForge", "settings.json");
    }

    public string FilePath { get; }

    public static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, Json);

    public static AppSettings? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads the settings; a missing or damaged file gives the defaults.</summary>
    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                AppSettings? s = Deserialize(File.ReadAllText(FilePath));
                if (s is not null) return Sanitize(s);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall through to the defaults.
        }
        return new AppSettings();
    }

    public bool Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, Serialize(settings));
            File.Move(tmp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Repairs values a hand-edited file may have broken.</summary>
    public static AppSettings Sanitize(AppSettings s)
    {
        s.RecentFiles ??= new();
        s.SquashFs ??= new();
        s.Ext ??= new();
        s.Tar ??= new();
        s.SquashFs.XzFilters ??= new();
        s.RecentLimit = Math.Clamp(s.RecentLimit, 0, 50);
        s.Threads = Math.Clamp(s.Threads, 0, 256);
        s.TreePaneWidth = Math.Clamp(s.TreePaneWidth, 120, 900);
        s.WindowWidth = Math.Clamp(s.WindowWidth, 640, 10000);
        s.WindowHeight = Math.Clamp(s.WindowHeight, 420, 10000);
        s.ExtExtraFreeMiB = Math.Clamp(s.ExtExtraFreeMiB, 0, 1 << 20);
        if (!Enum.IsDefined(s.NewImageFormat)) s.NewImageFormat = ImageFormat.SquashFs;
        s.Ubifs ??= new();
        s.Jffs2 ??= new();
        s.Fat ??= new();
        try { s.Fat.Validate(); } catch (FsForge.Core.FsException) { s.Fat = new(); }
        // Defaults never carry the size, serial, position or boot code of a particular volume.
        s.Fat.SizeBytes = 0;
        s.Fat.VolumeId = null;
        s.Fat.HiddenSectors = 0;
        s.Fat.BootCode = null;
        s.Ubi ??= new();
        try { s.Jffs2.Validate(); } catch (FsForge.Core.FsException) { s.Jffs2 = new(); }
        try { s.Ubifs.Validate(); } catch (FsForge.Core.FsException) { s.Ubifs = new(); }
        try { s.Ubi.Validate(); } catch (FsForge.Core.FsException) { s.Ubi = new(); }
        // Like the other defaults: nothing of a particular image, or of the volume it lived in.
        s.Ubifs.Uuid = null;
        s.Ubifs.VolumeLebCount = 0;
        s.Ubifs.JournalBytes = 0;
        s.Ubifs.LogLebs = 0;
        s.TextEditor ??= "";
        s.TempDirectory ??= "";
        try { s.SquashFs.Validate(); } catch (FsForge.Core.FsException) { s.SquashFs = new SquashFsOptions(); }
        try { s.Ext.Validate(); } catch (FsForge.Core.FsException) { s.Ext = new ExtOptions(); }
        try { s.Tar.Validate(); } catch (FsForge.Core.FsException) { s.Tar = new TarOptions(); }
        // Defaults describe new images: they never carry the identity or size of a particular one.
        s.Ext.SizeBytes = 0;
        s.Ext.InodeCount = 0;
        s.Ext.JournalBytes = 0;
        s.Ext.Uuid = null;
        s.Ext.Timestamp = null;
        s.SquashFs.MkfsTime = null;
        if (!FsForge.Core.UnixMode.TryParseOctal(s.DefaultFileMode ?? "", out _)) s.DefaultFileMode = "0644";
        if (!FsForge.Core.UnixMode.TryParseOctal(s.DefaultDirectoryMode ?? "", out _)) s.DefaultDirectoryMode = "0755";
        if (!FsForge.Core.UnixMode.TryParseOctal(s.ExecutableMode ?? "", out _)) s.ExecutableMode = "0755";
        return s;
    }
}
