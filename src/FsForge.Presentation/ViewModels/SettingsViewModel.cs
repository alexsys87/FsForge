using FsForge.Core;
using FsForge.Core.Compression;
using FsForge.Core.Ext;
using FsForge.Core.Host;
using FsForge.Core.Images;
using FsForge.Core.SquashFs;
using FsForge.Core.Tar;
using FsForge.Presentation.Mvvm;
using FsForge.Presentation.Services;
using FsForge.Presentation.Settings;

namespace FsForge.Presentation.ViewModels;

/// <summary>
/// The settings dialog. It edits a copy of the settings; the caller takes <see cref="Result"/>
/// when the dialog is accepted.
/// </summary>
public sealed class SettingsViewModel : DialogViewModel
{
    private readonly IDialogService _dialogs;
    private AppSettings _settings;

    public SettingsViewModel(AppSettings current, IDialogService dialogs, string settingsPath)
    {
        _dialogs = dialogs;
        _settings = current.Clone();
        SettingsPath = settingsPath;
        Title = L.T("Настройки");
        AcceptText = L.T("Сохранить");
        ResetCommand = new RelayCommand(Reset);
        BrowseTempCommand = new RelayCommand(() =>
        {
            string? dir = _dialogs.PickFolder(L.T("Папка для временных файлов"), _settings.TempDirectory);
            if (dir is null) return;
            _settings.TempDirectory = dir;
            OnPropertyChanged(nameof(Settings));
        });
        ClearRecentCommand = new RelayCommand(() =>
        {
            _settings.RecentFiles.Clear();
            OnPropertyChanged(nameof(Settings));
            OnPropertyChanged(nameof(RecentCountText));
        });
    }

    /// <summary>The copy being edited. Controls bind to its properties directly.</summary>
    public AppSettings Settings => _settings;

    public string SettingsPath { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand BrowseTempCommand { get; }
    public RelayCommand ClearRecentCommand { get; }
    public AppSettings? Result { get; private set; }

    public string RecentCountText => L.F("В списке недавних: {0}", _settings.RecentFiles.Count);

    public string ThreadsHint => L.F("0 — по числу ядер процессора ({0})", Environment.ProcessorCount);

    /// <summary>The single BCJ filter of the SquashFS defaults (-1 = none).</summary>
    public int DefaultBcjFilter
    {
        get => _settings.SquashFs.XzFilters.Count > 0 ? (int)_settings.SquashFs.XzFilters[0] : -1;
        set
        {
            _settings.SquashFs.XzFilters.Clear();
            if (value >= 0) _settings.SquashFs.XzFilters.Add((BcjFilter)value);
            OnPropertyChanged();
        }
    }

    public bool DefaultFragments
    {
        get => !_settings.SquashFs.NoFragments;
        set { _settings.SquashFs.NoFragments = !value; OnPropertyChanged(); }
    }

    // ---- choices
    /// <summary>The languages are named in their own language, so that each can be found whatever the current one is.</summary>
    public IReadOnlyList<Choice<AppLanguage>> Languages { get; } = new[]
    {
        new Choice<AppLanguage>(AppLanguage.System, L.T("как в системе")),
        new Choice<AppLanguage>(AppLanguage.Russian, L.T("Русский")),
        new Choice<AppLanguage>(AppLanguage.English, "English"),
    };

    public IReadOnlyList<Choice<AppTheme>> Themes { get; } = new[]
    {
        new Choice<AppTheme>(AppTheme.System, L.T("как в системе")), new Choice<AppTheme>(AppTheme.Light, L.T("светлая")), new Choice<AppTheme>(AppTheme.Dark, L.T("тёмная")),
    };

    public IReadOnlyList<Choice<BackdropKind>> Backdrops { get; } = new[]
    {
        new Choice<BackdropKind>(BackdropKind.Mica, "Mica"), new Choice<BackdropKind>(BackdropKind.Tabbed, "Mica Alt"),
        new Choice<BackdropKind>(BackdropKind.Acrylic, "Acrylic"), new Choice<BackdropKind>(BackdropKind.None, L.T("без эффекта")),
    };

    public IReadOnlyList<Choice<SizeDisplay>> SizeDisplays { get; } = new[]
    {
        new Choice<SizeDisplay>(SizeDisplay.Units, L.T("в КБ / МБ / ГБ")), new Choice<SizeDisplay>(SizeDisplay.Bytes, L.T("в байтах")),
    };

    public IReadOnlyList<Choice<FileOpenAction>> FileOpenActions { get; } = new[]
    {
        new Choice<FileOpenAction>(FileOpenAction.OpenExternally, L.T("открыть во внешней программе")),
        new Choice<FileOpenAction>(FileOpenAction.Properties, L.T("показать свойства")),
    };

    public IReadOnlyList<Choice<ConflictMode>> ConflictModes { get; } = new[]
    {
        new Choice<ConflictMode>(ConflictMode.Ask, L.T("спрашивать")), new Choice<ConflictMode>(ConflictMode.Overwrite, L.T("заменять")),
        new Choice<ConflictMode>(ConflictMode.Skip, L.T("пропускать")), new Choice<ConflictMode>(ConflictMode.Rename, L.T("добавлять под новым именем")),
    };

    public IReadOnlyList<Choice<SymlinkExtractMode>> SymlinkModes { get; } = new[]
    {
        new Choice<SymlinkExtractMode>(SymlinkExtractMode.Placeholder, L.T("текстовый файл с целью ссылки")),
        new Choice<SymlinkExtractMode>(SymlinkExtractMode.Native, L.T("настоящая ссылка Windows (нужен режим разработчика)")),
        new Choice<SymlinkExtractMode>(SymlinkExtractMode.Skip, L.T("не создавать")),
    };

    public IReadOnlyList<Choice<ImageFormat>> NewFormats { get; } = new[]
    {
        new Choice<ImageFormat>(ImageFormat.SquashFs, "SquashFS"), new Choice<ImageFormat>(ImageFormat.Erofs, "EROFS"), new Choice<ImageFormat>(ImageFormat.Ext, "ext2/3/4"), new Choice<ImageFormat>(ImageFormat.Ubifs, "UBIFS"), new Choice<ImageFormat>(ImageFormat.Jffs2, "JFFS2"), new Choice<ImageFormat>(ImageFormat.Fat, "FAT"),
        new Choice<ImageFormat>(ImageFormat.Tar, L.T("архив tar")),
        new Choice<ImageFormat>(ImageFormat.Cpio, L.T("архив cpio (initramfs)")),
    };

    public IReadOnlyList<Choice<ExtKind>> ExtKinds { get; } = new[]
    {
        new Choice<ExtKind>(ExtKind.Ext4, "ext4"), new Choice<ExtKind>(ExtKind.Ext3, "ext3"), new Choice<ExtKind>(ExtKind.Ext2, "ext2"),
    };

    public IReadOnlyList<Choice<VerifyLevel>> VerifyLevels => OptionLists.VerifyLevels;
    public IReadOnlyList<Choice<SquashCompression>> SquashCompressions => OptionLists.SquashCompressions;
    public IReadOnlyList<Choice<int>> SquashBlockSizes => OptionLists.SquashBlockSizes;
    public IReadOnlyList<Choice<int>> XzEfforts => OptionLists.XzEfforts;
    public IReadOnlyList<Choice<FsForge.Core.Erofs.ErofsCompression>> ErofsCompressions => OptionLists.ErofsCompressions;
    public IReadOnlyList<Choice<int>> ErofsPclusterSizes => OptionLists.ErofsPclusterSizes;
    public IReadOnlyList<Choice<int>> ErofsLz4Levels => OptionLists.ErofsLz4Levels;
    public IReadOnlyList<Choice<int>> GzipLevels => OptionLists.GzipLevels;
    public IReadOnlyList<Choice<int>> ZstdLevels => OptionLists.ZstdLevels;
    public IReadOnlyList<Choice<int>> BcjFilters => OptionLists.BcjFilters;
    public IReadOnlyList<Choice<int>> ExtBlockSizes => OptionLists.ExtBlockSizes;
    public IReadOnlyList<Choice<int>> ExtInodeSizes => OptionLists.ExtInodeSizes;
    public IReadOnlyList<Choice<ArchiveCompression>> TarCompressions => OptionLists.TarCompressions;
    public IReadOnlyList<Choice<TarDialect>> TarDialects => OptionLists.TarDialects;
    public IReadOnlyList<Choice<string>> TarPrefixes => OptionLists.TarPrefixes;

    private void Reset()
    {
        // Window placement and history are not preferences: keep them.
        var fresh = new AppSettings
        {
            RecentFiles = _settings.RecentFiles,
            WindowWidth = _settings.WindowWidth,
            WindowHeight = _settings.WindowHeight,
            WindowLeft = _settings.WindowLeft,
            WindowTop = _settings.WindowTop,
            WindowMaximized = _settings.WindowMaximized,
            LastOpenDirectory = _settings.LastOpenDirectory,
            LastSaveDirectory = _settings.LastSaveDirectory,
            LastHostDirectory = _settings.LastHostDirectory,
        };
        _settings = fresh;
        Error = null;
        // Every control is bound through "Settings.", so one notification refreshes them all.
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(DefaultBcjFilter));
        OnPropertyChanged(nameof(DefaultFragments));
    }

    public override bool Accept()
    {
        AppSettings s = _settings;
        foreach ((string text, string what) in new[]
                 {
                     (s.DefaultFileMode, L.T("файлов")), (s.DefaultDirectoryMode, L.T("папок")), (s.ExecutableMode, L.T("исполняемых файлов")),
                 })
        {
            if (!UnixMode.TryParseOctal((text ?? "").Trim(), out _))
            {
                Error = L.F("Права для {0} задаются восьмеричным числом, например 0644.", what);
                return false;
            }
        }
        if (!string.IsNullOrWhiteSpace(s.TempDirectory) && !Directory.Exists(s.TempDirectory))
        {
            Error = L.T("Папка для временных файлов не существует.");
            return false;
        }
        if (s.ReservedPercentInvalid())
        {
            Error = L.T("Резерв для root в ext — число от 0 до 50 процентов.");
            return false;
        }
        s.DefaultFileMode = s.DefaultFileMode.Trim();
        s.DefaultDirectoryMode = s.DefaultDirectoryMode.Trim();
        s.ExecutableMode = s.ExecutableMode.Trim();
        if (s.Ext.Kind == ExtKind.Ext2) s.Ext.Journal = false;
        if (s.SquashFs.XzDictionarySize > s.SquashFs.BlockSize) s.SquashFs.XzDictionarySize = 0;
        Result = SettingsStore.Sanitize(s);
        return true;
    }
}

internal static class SettingsChecks
{
    public static bool ReservedPercentInvalid(this AppSettings s) => s.Ext.ReservedPercent is < 0 or > 50 || double.IsNaN(s.Ext.ReservedPercent);
}
