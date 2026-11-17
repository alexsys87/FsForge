using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows.Input;
using FsForge.Core;
using FsForge.Core.Host;
using FsForge.Core.Images;
using FsForge.Core.Ubifs;
using FsForge.Core.Tar;
using FsForge.Presentation.Mvvm;
using FsForge.Presentation.Services;
using FsForge.Presentation.Settings;

namespace FsForge.Presentation.ViewModels;

/// <summary>
/// The main window: one opened image, the folder being shown, the selection and every command.
/// Long operations run on a worker thread while the window shows progress and stays responsive.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int SearchLimit = 20000;

    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly IUiDispatcher _ui;
    private readonly SettingsStore _store;
    private readonly List<ICommand> _commands = new();
    private readonly Stack<string> _back = new(), _forward = new();
    private readonly List<EditSession> _edits = new();
    private List<FsNode> _clipboard = new();
    private List<NodeItem> _selection = new();
    private bool _clipboardCut;
    private AppSettings _settings;
    private ImageDocument? _doc;
    private FsNode? _current;
    private IdNames _names = IdNames.Empty;
    private TempSpool? _spool;
    private CancellationTokenSource? _cts;
    private string? _editDirectory;
    private bool _checkingEdits;
    private TreeItem? _selectedTreeItem;

    private string _title = "FsForge", _currentPath = "", _searchText = "", _statusText = "", _documentInfo = "";
    private string _busyTitle = "", _busyDetail = "", _infoTitle = "", _infoMessage = "";
    private bool _isBusy, _busyIndeterminate, _searchEverywhere, _isInfoOpen, _isModified;
    private double _busyPercent;
    private MessageKind _infoKind;
    private IReadOnlyList<string> _pendingWarnings = Array.Empty<string>();
    private string _pendingWarningsTitle = "";

    private sealed record EditSession(string ImagePath, string HostPath, long Length, DateTime LastWriteUtc);

    public MainViewModel(IDialogService dialogs, IShellService shell, IUiDispatcher ui, SettingsStore store, AppSettings settings)
    {
        _dialogs = dialogs;
        _shell = shell;
        _ui = ui;
        _store = store;
        _settings = settings;

        NewCommand = Async(NewImageAsync, () => !IsBusy);
        NewFromFolderCommand = Async(NewFromFolderAsync, () => !IsBusy);
        OpenCommand = Async(OpenAsync, () => !IsBusy);
        OpenRecentCommand = AsyncP(p => p is string s ? OpenPathAsync(s) : p is RecentItem r ? OpenPathAsync(r.Path) : Task.CompletedTask, _ => !IsBusy);
        SaveCommand = Async(() => SaveAsync(), () => Ready && (IsModified || _doc!.FilePath is null));
        SaveAsCommand = Async(() => SaveAsAsync(), () => Ready);
        CloseCommand = Async(() => CloseAsync(), () => Ready);
        AddFilesCommand = Async(AddFilesAsync, () => Ready);
        AddFolderCommand = Async(AddFolderAsync, () => Ready);
        ImportArchiveCommand = Async(ImportArchiveAsync, () => Ready);
        ExportArchiveCommand = Async(ExportArchiveAsync, () => Ready);
        ExtractCommand = Async(ExtractSelectedAsync, () => Ready);
        ExtractAllCommand = Async(ExtractAllAsync, () => Ready);
        NewFolderCommand = Sync(() => CreateItem(NewItemKind.Folder), () => Ready);
        NewFileCommand = Sync(() => CreateItem(NewItemKind.File), () => Ready);
        NewSymlinkCommand = Sync(() => CreateItem(NewItemKind.Symlink), () => Ready);
        NewSpecialCommand = Sync(() => CreateItem(NewItemKind.CharDevice), () => Ready);
        DeleteCommand = Async(DeleteAsync, () => Ready && _selection.Count > 0);
        RenameCommand = Sync(Rename, () => Ready && _selection.Count == 1);
        PropertiesCommand = Sync(ShowProperties, () => Ready);
        CopyCommand = Sync(() => SetClipboard(cut: false), () => Ready && _selection.Count > 0);
        CutCommand = Sync(() => SetClipboard(cut: true), () => Ready && _selection.Count > 0);
        PasteCommand = Async(PasteAsync, () => Ready && _clipboard.Count > 0);
        OpenItemCommand = AsyncP(p => OpenItemAsync(p as NodeItem ?? (_selection.Count == 1 ? _selection[0] : null)), _ => Ready);
        OpenExternallyCommand = Async(() => OpenExternallyAsync(_selection.Count == 1 ? _selection[0].Node : null), () => Ready && _selection.Count == 1 && _selection[0].Type == FsNodeType.File);
        ReplaceContentCommand = Async(ReplaceContentAsync, () => Ready && _selection.Count == 1 && _selection[0].Type == FsNodeType.File);
        CopyPathCommand = Sync(CopyPath, () => Ready);
        UpCommand = Sync(GoUp, () => Ready && _current?.Parent is not null);
        BackCommand = Sync(GoBack, () => Ready && _back.Count > 0);
        ForwardCommand = Sync(GoForward, () => Ready && _forward.Count > 0);
        RefreshCommand = Sync(() => AfterTreeChanged(), () => Ready);
        NavigateCommand = new RelayCommand(p => { if (p is Crumb c) NavigateTo(c.Node); else if (p is TreeItem t) NavigateTo(t.Node); }, _ => Ready);
        _commands.Add(NavigateCommand);
        ImageInfoCommand = Sync(() => _dialogs.ShowDialog(new ImageInfoViewModel(_doc!, _settings)), () => Ready);
        SettingsCommand = Sync(ShowSettings, () => !IsBusy);
        ShowWarningsCommand = Sync(ShowPendingWarnings, () => _pendingWarnings.Count > 0);
        CancelCommand = Sync(() => _cts?.Cancel(), () => IsBusy);
        ClearSearchCommand = Sync(() => SearchText = "", () => true);
        AboutCommand = Async(ShowAboutAsync, () => true);

        RefreshRecent();
        UpdateStatus();
    }

    private RelayCommand Sync(Action a, Func<bool> can)
    {
        var c = new RelayCommand(a, can);
        _commands.Add(c);
        return c;
    }

    private AsyncRelayCommand Async(Func<Task> a, Func<bool> can)
    {
        var c = new AsyncRelayCommand(a, can);
        _commands.Add(c);
        return c;
    }

    private AsyncRelayCommand AsyncP(Func<object?, Task> a, Func<object?, bool> can)
    {
        var c = new AsyncRelayCommand(a, can);
        _commands.Add(c);
        return c;
    }

    private void UpdateCommands()
    {
        foreach (ICommand c in _commands)
        {
            if (c is RelayCommand r) r.RaiseCanExecuteChanged();
            else if (c is AsyncRelayCommand a) a.RaiseCanExecuteChanged();
        }
    }

    // ------------------------------------------------------------- commands

    public AsyncRelayCommand NewCommand { get; }
    public AsyncRelayCommand NewFromFolderCommand { get; }
    public AsyncRelayCommand OpenCommand { get; }
    public AsyncRelayCommand OpenRecentCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand SaveAsCommand { get; }
    public AsyncRelayCommand CloseCommand { get; }
    public AsyncRelayCommand AddFilesCommand { get; }
    public AsyncRelayCommand AddFolderCommand { get; }
    public AsyncRelayCommand ImportArchiveCommand { get; }
    public AsyncRelayCommand ExportArchiveCommand { get; }
    public AsyncRelayCommand ExtractCommand { get; }
    public AsyncRelayCommand ExtractAllCommand { get; }
    public RelayCommand NewFolderCommand { get; }
    public RelayCommand NewFileCommand { get; }
    public RelayCommand NewSymlinkCommand { get; }
    public RelayCommand NewSpecialCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public RelayCommand RenameCommand { get; }
    public RelayCommand PropertiesCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand CutCommand { get; }
    public AsyncRelayCommand PasteCommand { get; }
    public AsyncRelayCommand OpenItemCommand { get; }
    public AsyncRelayCommand OpenExternallyCommand { get; }
    public AsyncRelayCommand ReplaceContentCommand { get; }
    public RelayCommand CopyPathCommand { get; }
    public RelayCommand UpCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand ForwardCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand NavigateCommand { get; }
    public RelayCommand ImageInfoCommand { get; }
    public RelayCommand SettingsCommand { get; }
    public RelayCommand ShowWarningsCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    public AsyncRelayCommand AboutCommand { get; }

    // ---------------------------------------------------------------- state

    public AppSettings Settings => _settings;
    public ImageDocument? Document => _doc;
    public FsNode? CurrentDirectory => _current;
    public bool HasDocument => _doc is not null;
    public bool NoDocument => _doc is null;
    private bool Ready => _doc is not null && !IsBusy;

    public ObservableCollection<NodeItem> Items { get; } = new();
    public ObservableCollection<TreeItem> TreeRoots { get; } = new();
    public ObservableCollection<Crumb> Breadcrumbs { get; } = new();
    public ObservableCollection<RecentItem> RecentFiles { get; } = new();
    public bool HasRecentFiles => RecentFiles.Count > 0;

    /// <summary>The view should select these rows (after a refresh, a paste or an import).</summary>
    public event Action<IReadOnlyList<NodeItem>>? SelectionRequested;

    /// <summary>Raised when the theme or backdrop settings changed.</summary>
    public event Action? AppearanceChanged;

    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string DocumentInfo { get => _documentInfo; private set => SetProperty(ref _documentInfo, value); }
    public bool IsModified { get => _isModified; private set => SetProperty(ref _isModified, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            UpdateCommands();
        }
    }

    public bool IsIdle => !_isBusy;
    public string BusyTitle { get => _busyTitle; private set => SetProperty(ref _busyTitle, value); }
    public string BusyDetail { get => _busyDetail; private set => SetProperty(ref _busyDetail, value); }
    public double BusyPercent { get => _busyPercent; private set => SetProperty(ref _busyPercent, value); }
    public bool BusyIndeterminate { get => _busyIndeterminate; private set => SetProperty(ref _busyIndeterminate, value); }

    public bool IsInfoOpen { get => _isInfoOpen; set => SetProperty(ref _isInfoOpen, value); }
    public string InfoTitle { get => _infoTitle; private set => SetProperty(ref _infoTitle, value); }
    public string InfoMessage { get => _infoMessage; private set => SetProperty(ref _infoMessage, value); }
    public MessageKind InfoKind { get => _infoKind; private set => SetProperty(ref _infoKind, value); }

    public bool ShowTreePane
    {
        get => _settings.ShowTreePane;
        set
        {
            if (_settings.ShowTreePane == value) return;
            _settings.ShowTreePane = value;
            OnPropertyChanged();
        }
    }

    public string SortColumn => _settings.SortColumn;
    public bool SortDescending => _settings.SortDescending;

    /// <summary>Path in the address box. Setting it navigates when the path exists.</summary>
    public string CurrentPath
    {
        get => _currentPath;
        set
        {
            if (_doc is null || value == _currentPath) return;
            FsNode? node = _doc.Tree.Resolve(value.Trim());
            if (node is null)
            {
                _dialogs.Notify(L.T("Путь не найден"), value, MessageKind.Warning);
                OnPropertyChanged();
                return;
            }
            if (node.IsDirectory) NavigateTo(node);
            else
            {
                NavigateTo(node.Parent!);
                SelectNodes(new[] { node });
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? "")) return;
            OnPropertyChanged(nameof(IsSearching));
            RefreshItems(keepSelection: false);
        }
    }

    /// <summary>Search in the current folder and everything below it instead of the folder alone.</summary>
    public bool SearchEverywhere
    {
        get => _searchEverywhere;
        set
        {
            if (!SetProperty(ref _searchEverywhere, value)) return;
            OnPropertyChanged(nameof(ShowLocationColumn));
            if (_searchText.Length > 0) RefreshItems(keepSelection: false);
        }
    }

    public bool IsSearching => _searchText.Length > 0;
    public bool ShowLocationColumn => _searchEverywhere && _searchText.Length > 0;

    public IReadOnlyList<NodeItem> Selection => _selection;

    /// <summary>Called by the view whenever the selected rows change.</summary>
    public void SetSelection(IEnumerable<NodeItem> items)
    {
        _selection = items.ToList();
        UpdateStatus();
        UpdateCommands();
    }

    // -------------------------------------------------------- busy handling

    private sealed class ThrottledProgress : IProgress<ProgressInfo>, IDisposable
    {
        private readonly IUiDispatcher _ui;
        private readonly Action<ProgressInfo> _apply;
        private readonly Timer _timer;
        private readonly object _gate = new();
        private ProgressInfo _latest;
        private bool _dirty, _disposed;

        public ThrottledProgress(IUiDispatcher ui, Action<ProgressInfo> apply)
        {
            _ui = ui;
            _apply = apply;
            _timer = new Timer(_ => Tick(), null, 100, 100);
        }

        public void Report(ProgressInfo value)
        {
            lock (_gate)
            {
                _latest = value;
                _dirty = true;
            }
        }

        private void Tick()
        {
            ProgressInfo info;
            lock (_gate)
            {
                if (!_dirty || _disposed) return;
                _dirty = false;
                info = _latest;
            }
            _ui.Post(() =>
            {
                if (!_disposed) _apply(info);
            });
        }

        public void Dispose()
        {
            lock (_gate) _disposed = true;
            _timer.Dispose();
        }
    }

    private void ApplyProgress(ProgressInfo p)
    {
        string stage = p.Stage switch
        {
            "read" => L.T("Чтение структуры"),
            "data" => L.T("Запись данных"),
            "verify" => L.T("Проверка записанного образа"),
            "extract" => L.T("Извлечение"),
            "import" => L.T("Добавление"),
            "scan" => L.T("Подготовка"),
            "unpack" => L.T("Распаковка сжатого образа"),
            "copy" => L.T("Копирование остальных разделов"),
            "compress" => L.T("Сжатие образа"),
            "done" => L.T("Завершение"),
            _ => p.Stage,
        };
        if (p.Total > 0)
        {
            BusyIndeterminate = false;
            BusyPercent = 100.0 * p.Fraction;
            string amount = p.Stage is "data" or "verify" or "extract" or "unpack" or "copy" or "compress" ? L.F("{0} из {1}", Fmt.Size(p.Done), Fmt.Size(p.Total)) : L.F("{0} из {1}", p.Done, p.Total);
            BusyDetail = $"{stage}: {amount}" + (string.IsNullOrEmpty(p.Item) ? "" : "  ·  " + Shorten(p.Item!));
        }
        else
        {
            BusyIndeterminate = true;
            BusyDetail = stage + (string.IsNullOrEmpty(p.Item) ? "" : "  ·  " + Shorten(p.Item!));
        }
    }

    private static string Shorten(string path) => path.Length <= 70 ? path : "…" + path[^69..];

    /// <summary>Runs work on a worker thread with progress and cancellation; false when it failed or was cancelled.</summary>
    private async Task<bool> RunBusyAsync(string title, Action<IProgress<ProgressInfo>, CancellationToken> work)
    {
        if (IsBusy) return false;
        var cts = new CancellationTokenSource();
        _cts = cts;
        BusyTitle = title;
        BusyDetail = "";
        BusyPercent = 0;
        BusyIndeterminate = true;
        IsBusy = true;
        var progress = new ThrottledProgress(_ui, ApplyProgress);
        string? error = null;
        bool cancelled = false;
        try
        {
            await Task.Run(() => work(progress, cts.Token));
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (FsException ex)
        {
            error = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
        }
        catch (Exception ex)
        {
            // Never let a defect in an operation take the window down: report it instead.
            error = L.F("Внутренняя ошибка ({0}): {1}", ex.GetType().Name, ex.Message);
        }
        finally
        {
            progress.Dispose();
            _cts = null;
            cts.Dispose();
            IsBusy = false;
        }
        if (cancelled)
        {
            _dialogs.Notify(L.T("Отменено"), title, MessageKind.Warning);
            return false;
        }
        if (error is not null)
        {
            await _dialogs.ShowMessageAsync(title + L.T(" — ошибка"), error, MessageKind.Error);
            return false;
        }
        return true;
    }

    // ------------------------------------------------------------- document

    private void OnDocumentChanged()
    {
        _ui.Post(() =>
        {
            if (_doc is null) return;
            IsModified = _doc.IsModified;
            UpdateTitle();
            UpdateCommands();
        });
    }

    private void UpdateTitle()
    {
        if (_doc is null)
        {
            Title = "FsForge";
            DocumentInfo = "";
            return;
        }
        string name = _doc.FilePath is null ? L.T("Новый образ") : Path.GetFileName(_doc.FilePath);
        Title = $"{name}{(_doc.IsModified ? " •" : "")} — FsForge";
        string info = _doc.FormatName;
        if (_doc.SquashFs is { } s) info += L.F(" · {0} · блок {1}", s.Compression.ToString().ToLowerInvariant(), Fmt.Size(s.BlockSize));
        if (_doc.Ext is { } e) info += L.F(" · {0} · свободно {1}", Fmt.Size(e.SizeBytes), Fmt.Size(e.FreeBlocks * e.BlockSize));
        if (_doc.Ubifs is { } u) info += L.F(" · {0} · блок стирания {1}", u.DefaultCompression.ToString().ToLowerInvariant(), Fmt.Size(u.LebSize));
        if (_doc.Erofs is { } er)
            info += er.Compression == FsForge.Core.Erofs.ErofsCompression.None
                ? L.T(" · без сжатия")
                : L.F(" · {0} · кластер {1}", er.Compression.ToString().ToLowerInvariant(), Fmt.Size(er.PclusterSize));
        if (_doc.F2fs is { } f2)
            info += L.F(" · {0} · свободно {1}", Fmt.Size(f2.BlockCount * f2.BlockSize), Fmt.Size(Math.Max(0, f2.UserBlocks - f2.ValidBlocks) * f2.BlockSize));
        if (_doc.Cramfs is { } cr)
            info += L.F(" · {0} · блок {1}", cr.BigEndian ? "big-endian" : "little-endian", Fmt.Size(cr.BlockSize));
        if (_doc.Romfs is { } ro && ro.VolumeName.Length > 0) info += $" · {ro.VolumeName}";
        if (_doc.Fat is { } fat)
            info += L.F(" · кластер {0} · свободно {1}", Fmt.Size(fat.ClusterSize), Fmt.Size(fat.FreeClusters * fat.ClusterSize));
        if (_doc.ExFat is { } exfat)
            info += L.F(" · {0} · кластер {1} · свободно {2}", Fmt.Size(exfat.SizeBytes), Fmt.Size(exfat.ClusterSize), Fmt.Size(exfat.FreeClusters * exfat.ClusterSize));
        if (_doc.Jffs2 is { } j)
            info += L.F(" · {0} · {1} · блок стирания {2}", j.BigEndian ? "big-endian" : "little-endian", j.MainCompression.ToString().ToLowerInvariant(), Fmt.Size(j.EraseBlockSize));
        if (_doc.FilePath is not null && _doc.Ext is null) info += $" · {Fmt.Size(_doc.FileSize)}";
        if (_doc.Ubi is { } ubi)
            info = L.F("образ UBI · том {0} из {1} · ", Fmt.VolumeName(ubi.Volume), ubi.Image.Volumes.Count) + info;
        if (_doc.Android is { } android)
            info = (android.Image.Kind == FsForge.Core.Android.AndroidImageKind.Super
                ? L.F("super.img · {0} из {1} · ", android.Part.Name, android.Image.Parts.Count)
                : L.F("{0} · ramdisk {1} · ", android.Image.Kind == FsForge.Core.Android.AndroidImageKind.VendorBoot ? "vendor_boot" : "boot", android.Part.Name)) + info;
        if (_doc.Firmware is { } fw)
            info = L.F("{0} · {1} из {2} · ", fw.Image.ShortKind, fw.Part.Name, fw.Image.Parts.Count) + info;
        if (_doc.Container is { } disk)
            info = L.F("образ диска {0} · раздел {1} из {2} · ", Fmt.PartitionScheme(disk.Table), disk.Partition.Number, disk.Table.Partitions.Count) + info;
        if (_doc.Archive is { } arc)
            info = L.F("{0} в архиве {1} · ", arc.EntryName, ArchiveCodec.DisplayName(arc.Kind)) + info;
        else if (_doc.OuterCompression is { } packed)
            info += L.F(" · файл сжат {0}", ArchiveCodec.DisplayName(packed));
        DocumentInfo = info;
    }

    private void SetDocument(ImageDocument? doc, string? restorePath = null, HashSet<string>? expanded = null)
    {
        if (_doc is not null)
        {
            _doc.Changed -= OnDocumentChanged;
            if (!_doc.IsDisposed) _doc.Dispose();
        }
        _spool = null;                              // owned by the tree of the previous document
        _clipboard = new List<FsNode>();
        _selection = new List<NodeItem>();
        _back.Clear();
        _forward.Clear();
        _doc = doc;
        _current = null;
        _selectedTreeItem = null;
        TreeRoots.Clear();
        Items.Clear();
        Breadcrumbs.Clear();
        _searchText = "";
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(ShowLocationColumn));
        IsInfoOpen = false;
        _pendingWarnings = Array.Empty<string>();

        if (doc is not null)
        {
            doc.Changed += OnDocumentChanged;
            _names = IdNames.Load(doc.Tree);
            var root = new TreeItem(doc.Tree.Root, null) { IsExpanded = true };
            root.Sync();
            TreeRoots.Add(root);
            if (expanded is not null)
                foreach (TreeItem t in root.Flatten())
                    if (expanded.Contains(t.Node.Path)) t.IsExpanded = true;
            FsNode start = restorePath is null ? doc.Tree.Root : NearestDirectory(doc.Tree, restorePath);
            NavigateTo(start, history: false);
        }
        else
        {
            _names = IdNames.Empty;
            _currentPath = "";
            OnPropertyChanged(nameof(CurrentPath));
        }
        IsModified = doc?.IsModified ?? false;
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(NoDocument));
        OnPropertyChanged(nameof(Document));
        UpdateTitle();
        UpdateStatus();
        UpdateCommands();
    }

    private static FsNode NearestDirectory(FsTree tree, string path)
    {
        FsNode node = tree.Root;
        foreach (string part in FsTree.SplitPath(path))
        {
            FsNode? next = node.Find(part);
            if (next is not { IsDirectory: true }) break;
            node = next;
        }
        return node;
    }

    /// <summary>Asks what to do with unsaved changes; false means "stay".</summary>
    public async Task<bool> ConfirmDiscardAsync()
    {
        if (_doc is null || !_doc.IsModified || !_settings.ConfirmDiscardChanges) return true;
        DialogAnswer a = await _dialogs.AskAsync(L.T("Несохранённые изменения"),
            L.T("В образе есть несохранённые изменения. Сохранить их?"), L.T("Сохранить"), L.T("Не сохранять"));
        if (a == DialogAnswer.Cancel) return false;
        if (a == DialogAnswer.Secondary) return true;
        return await SaveAsync();
    }

    private async Task OpenAsync()
    {
        string? path = _dialogs.PickImageToOpen(NullIfEmpty(_settings.LastOpenDirectory));
        if (path is not null) await OpenPathAsync(path);
    }

    /// <summary>Opens an image or archive, replacing the current document.</summary>
    public async Task<bool> OpenPathAsync(string path)
    {
        if (IsBusy || !await ConfirmDiscardAsync()) return false;
        if (!File.Exists(path))
        {
            await _dialogs.ShowMessageAsync(L.T("Файл не найден"), path, MessageKind.Error);
            if (_settings.RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                RefreshRecent();
                SaveSettings();
            }
            return false;
        }
        ImageDocument? doc = null;
        bool ok = await RunBusyAsync(L.T("Открытие ") + Path.GetFileName(path), (p, ct) =>
            doc = ImageDocument.Open(path, _settings.EffectiveTempDirectory, _settings.SearchEmbedded, p, ct,
                choosePartition: table => AskPartition(path, table), chooseVolume: ubi => AskVolume(path, ubi),
                chooseAndroidPart: image => AskAndroidPart(path, image), chooseArchiveEntry: archive => AskArchiveEntry(path, archive),
                chooseFirmwarePart: image => AskFirmwarePart(path, image)));
        if (!ok || doc is null) return false;

        SetDocument(doc);
        _settings.LastOpenDirectory = Path.GetDirectoryName(doc.FilePath!) ?? "";
        _settings.AddRecent(doc.FilePath!);
        RefreshRecent();
        SaveSettings();
        if (doc.Warnings.Count > 0 && _settings.ShowWarningsAfterOpen)
            ShowInfo(L.F("Образ открыт с предупреждениями: {0}", doc.Warnings.Count), doc.Warnings[0], MessageKind.Warning, doc.Warnings, L.T("Предупреждения при открытии"));
        return true;
    }

    /// <summary>
    /// Lets the user pick a partition of a disk image. Called on the worker thread that is opening the
    /// image; the dialog itself runs on the interface thread. Null means "cancel".
    /// </summary>
    private PartitionInfo? AskPartition(string path, PartitionTable table)
    {
        var done = new TaskCompletionSource<PartitionInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(() =>
        {
            try
            {
                var vm = new PartitionChoiceViewModel(Path.GetFileName(path), table);
                done.SetResult(_dialogs.ShowDialog(vm) ? vm.Result : null);
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task.GetAwaiter().GetResult();
    }

    /// <summary>Lets the user pick a volume of a UBI image; works like <see cref="AskPartition"/>.</summary>
    private UbiVolumeInfo? AskVolume(string path, UbiImage ubi)
    {
        var done = new TaskCompletionSource<UbiVolumeInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(() =>
        {
            try
            {
                var vm = new PartitionChoiceViewModel(Path.GetFileName(path), ubi);
                done.SetResult(_dialogs.ShowDialog(vm) ? vm.VolumeResult : null);
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task.GetAwaiter().GetResult();
    }

    /// <summary>Lets the user pick a logical partition or ramdisk of an Android image; works like <see cref="AskPartition"/>.</summary>
    private FsForge.Core.Android.AndroidPart? AskAndroidPart(string path, FsForge.Core.Android.AndroidImage image)
    {
        var done = new TaskCompletionSource<FsForge.Core.Android.AndroidPart?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(() =>
        {
            try
            {
                var vm = new PartitionChoiceViewModel(Path.GetFileName(path), image);
                done.SetResult(_dialogs.ShowDialog(vm) ? vm.AndroidResult : null);
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task.GetAwaiter().GetResult();
    }

    /// <summary>Lets the user pick the part of a firmware file (uImage) to open; works like <see cref="AskPartition"/>.</summary>
    private FsForge.Core.Firmware.FirmwarePart? AskFirmwarePart(string path, FsForge.Core.Firmware.FirmwareImage image)
    {
        var done = new TaskCompletionSource<FsForge.Core.Firmware.FirmwarePart?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(() =>
        {
            try
            {
                var vm = new PartitionChoiceViewModel(Path.GetFileName(path), image);
                done.SetResult(_dialogs.ShowDialog(vm) ? vm.FirmwareResult : null);
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task.GetAwaiter().GetResult();
    }

    /// <summary>Lets the user pick the file of a zip or 7z archive to open; works like <see cref="AskPartition"/>.</summary>
    private FsForge.Core.Archives.ArchiveEntry? AskArchiveEntry(string path, FsForge.Core.Archives.ImageArchive archive)
    {
        var done = new TaskCompletionSource<FsForge.Core.Archives.ArchiveEntry?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(() =>
        {
            try
            {
                var vm = new PartitionChoiceViewModel(Path.GetFileName(path), archive);
                done.SetResult(_dialogs.ShowDialog(vm) ? vm.EntryResult : null);
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task.GetAwaiter().GetResult();
    }

    private async Task NewImageAsync()
    {
        if (!await ConfirmDiscardAsync()) return;
        SetDocument(ImageDocument.CreateNew(_settings.NewImageFormat));
    }

    private async Task NewFromFolderAsync()
    {
        string? folder = _dialogs.PickFolder(L.T("Папка, из которой будет собран образ"), NullIfEmpty(_settings.LastHostDirectory));
        if (folder is not null) await NewFromFolderAsync(folder);
    }

    /// <summary>Creates a new image whose root holds the contents of a host folder.</summary>
    public async Task<bool> NewFromFolderAsync(string folder)
    {
        if (IsBusy || !await ConfirmDiscardAsync()) return false;
        ImageDocument doc = ImageDocument.CreateNew(_settings.NewImageFormat);
        ImportOptions options = _settings.ToImportOptions(ConflictPolicy.Overwrite);
        ImportResult? result = null;
        bool ok = await RunBusyAsync(L.T("Чтение папки ") + Path.GetFileName(folder), (p, ct) =>
            result = HostImporter.ImportContents(doc.Tree, doc.Tree.Root, folder, options, p, ct));
        if (!ok || result is null)
        {
            doc.Dispose();
            return false;
        }
        SetDocument(doc);
        _settings.LastHostDirectory = folder;
        SaveSettings();
        ReportImport(result, L.T("Образ собран из папки"));
        return true;
    }

    private async Task<bool> CloseAsync()
    {
        if (!await ConfirmDiscardAsync()) return false;
        SetDocument(null);
        return true;
    }

    // ----------------------------------------------------------------- save

    /// <summary>Saves to the file the image was opened from (or asks for one).</summary>
    public async Task<bool> SaveAsync()
    {
        if (_doc is null || IsBusy) return false;
        // A filesystem taken out of a firmware container is never written back over the container.
        // (A partition of a disk image is: the disk is rebuilt around it.)
        if (_doc.FilePath is null || _settings.AlwaysAskSaveOptions || _doc.SavesFilesystemOnly)
            return await SaveAsAsync();
        return await SaveCoreAsync(_doc.FilePath, _settings.DefaultSaveOptions(_doc));
    }

    public async Task<bool> SaveAsAsync()
    {
        if (_doc is null || IsBusy) return false;
        var vm = new SaveOptionsViewModel(_doc, _settings.DefaultSaveOptions(_doc));
        if (!_dialogs.ShowDialog(vm) || vm.Result is null) return false;

        string baseName = _doc.FilePath is null ? "rootfs" : StripExtensions(Path.GetFileName(_doc.FilePath));
        // The file keeps its name when it stays what it was: the same disk image, or the same kind of plain image.
        // "disk" stands for either container: a disk image with partitions, or a UBI image with volumes.
        bool disk = _doc.WritesContainer(vm.Result);
        bool ubi = _doc.WritesUbi(vm.Result);
        bool sameFormat = _doc.FilePath is not null && vm.Result.Format == _doc.Format && !_doc.SavesFilesystemOnly && _doc.Container is null
                          && _doc.Ubi is null && _doc.Android is null && _doc.Firmware is null && !ubi
                          && (vm.Result.Format != ImageFormat.Tar || vm.Result.Tar.Compression == _doc.Tar?.Compression);
        string suggested = disk || sameFormat ? Path.GetFileName(_doc.FilePath!) : baseName + vm.Extension;
        string? dir = NullIfEmpty(_settings.LastSaveDirectory) ?? (_doc.FilePath is null ? null : Path.GetDirectoryName(_doc.FilePath));
        bool firmware = _doc.WritesFirmware(vm.Result);
        string title = firmware ? L.T("Сохранить прошивку") : ubi ? L.T("Сохранить образ UBI") : disk && _doc.Android is not null ? L.T("Сохранить образ Android")
            : disk ? L.T("Сохранить образ диска") : L.T("Сохранить образ");
        string? path = _dialogs.PickSaveFile(title, suggested, ubi ? ImageFormat.Ubifs : vm.Result.Format, vm.Extension, dir, disk && (!ubi || firmware));
        if (path is null) return false;
        return await SaveCoreAsync(path, vm.Result);
    }

    private static string StripExtensions(string fileName)
    {
        // "rootfs.ext4.gz" and "rootfs.tar.gz" both become "rootfs".
        foreach (string outer in new[] { ".gz", ".xz", ".zst", ".zstd", ".bz2", ".zip", ".7z" })
            if (fileName.EndsWith(outer, StringComparison.OrdinalIgnoreCase)) fileName = fileName[..^outer.Length];
        string stem = Path.GetFileNameWithoutExtension(fileName);
        return stem.Length > 0 ? stem : fileName;
    }

    private async Task<bool> SaveCoreAsync(string path, SaveOptions options)
    {
        ImageDocument doc = _doc!;
        string viewPath = _current?.Path ?? "/";
        var expanded = new HashSet<string>(TreeRoots.SelectMany(r => r.Flatten()).Where(t => t.IsExpanded).Select(t => t.Node.Path));
        _settings.ApplyCommon(options);

        ImageDocument.SaveResult? result = null;
        bool ok = await RunBusyAsync(L.T("Сохранение ") + Path.GetFileName(path), (p, ct) => result = doc.Save(path, options, p, ct));
        if (!ok || result is null)
        {
            // A failure after the source file was released leaves nothing to show.
            if (doc.IsDisposed) SetDocument(null);
            return false;
        }

        // Editor sessions refer to paths, which stay valid in the reopened image.
        SetDocument(result.Document, viewPath, expanded);
        _settings.LastSaveDirectory = Path.GetDirectoryName(path) ?? "";
        _settings.AddRecent(path);
        RefreshRecent();
        SaveSettings();
        string done = $"{Path.GetFileName(path)} — {Fmt.Size(result.BytesWritten)}";
        if (result.UpdatedFiles is { Count: > 0 } updated)
            done += L.F(" · обновлены файлы рядом с ним: {0}", string.Join(", ", updated));
        if (result.Warnings.Count > 0)
            ShowInfo(L.F("Образ сохранён с предупреждениями: {0}", result.Warnings.Count), result.Warnings[0], MessageKind.Warning, result.Warnings, L.T("Предупреждения при сохранении"));
        else
            _dialogs.Notify(L.T("Образ сохранён"), done, MessageKind.Success);
        return true;
    }

    // ----------------------------------------------------------- navigation

    public void NavigateTo(FsNode directory, bool history = true)
    {
        if (_doc is null || !directory.IsDirectory) return;
        if (history && _current is not null && !ReferenceEquals(_current, directory))
        {
            _back.Push(_current.Path);
            _forward.Clear();
        }
        _current = directory;
        _currentPath = directory.Path;
        OnPropertyChanged(nameof(CurrentPath));
        OnPropertyChanged(nameof(CurrentDirectory));

        Breadcrumbs.Clear();
        var chain = new Stack<FsNode>();
        for (FsNode? n = directory; n is not null; n = n.Parent) chain.Push(n);
        foreach (FsNode n in chain) Breadcrumbs.Add(new Crumb(n.IsRoot ? "/" : n.Name, n));

        if (_searchText.Length > 0)
        {
            _searchText = "";
            OnPropertyChanged(nameof(SearchText));
            OnPropertyChanged(nameof(IsSearching));
            OnPropertyChanged(nameof(ShowLocationColumn));
        }
        RefreshItems(keepSelection: false);
        SyncTreeSelection();
        UpdateCommands();
    }

    private void SyncTreeSelection()
    {
        if (_current is null || TreeRoots.Count == 0) return;
        TreeItem? item = TreeRoots[0].Find(_current);
        if (item is null) return;
        for (TreeItem? p = item.Parent; p is not null; p = p.Parent) p.IsExpanded = true;
        // Exactly one item carries the flag, also among rows the tree control has not created yet.
        if (_selectedTreeItem is not null && !ReferenceEquals(_selectedTreeItem, item)) _selectedTreeItem.IsSelected = false;
        _selectedTreeItem = item;
        item.IsSelected = true;
    }

    private void GoUp()
    {
        if (_current?.Parent is { } parent)
        {
            FsNode child = _current;
            NavigateTo(parent);
            SelectNodes(new[] { child });
        }
    }

    private void GoBack() => Jump(_back, _forward);
    private void GoForward() => Jump(_forward, _back);

    private void Jump(Stack<string> from, Stack<string> to)
    {
        if (_doc is null) return;
        while (from.Count > 0)
        {
            string path = from.Pop();
            if (_doc.Tree.Resolve(path) is not { IsDirectory: true } node) continue;   // removed meanwhile
            if (_current is not null) to.Push(_current.Path);
            NavigateTo(node, history: false);
            return;
        }
        UpdateCommands();
    }

    // ----------------------------------------------------------------- list

    public void SortBy(string column)
    {
        if (_settings.SortColumn == column) _settings.SortDescending = !_settings.SortDescending;
        else
        {
            _settings.SortColumn = column;
            _settings.SortDescending = false;
        }
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortDescending));
        RefreshItems(keepSelection: true);
    }

    private Comparison<NodeItem> Comparer()
    {
        static int ByName(NodeItem a, NodeItem b)
        {
            int c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
        }
        Comparison<NodeItem> key = _settings.SortColumn switch
        {
            "Size" => (a, b) => a.Size.CompareTo(b.Size),
            "Type" => (a, b) => string.CompareOrdinal(a.TypeText, b.TypeText),
            "Mode" => (a, b) => string.CompareOrdinal(a.Octal, b.Octal),
            "Owner" => (a, b) => a.Uid != b.Uid ? a.Uid.CompareTo(b.Uid) : a.Gid.CompareTo(b.Gid),
            "Modified" => (a, b) => a.MTime.CompareTo(b.MTime),
            "Location" => (a, b) => string.CompareOrdinal(a.Location, b.Location),
            _ => ByName,
        };
        bool desc = _settings.SortDescending, foldersFirst = _settings.FoldersFirst;
        return (a, b) =>
        {
            if (foldersFirst && a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            int c = key(a, b);
            if (c == 0) c = ByName(a, b);
            return desc ? -c : c;
        };
    }

    private static Func<string, bool> Matcher(string pattern)
    {
        if (pattern.Contains('*') || pattern.Contains('?'))
        {
            var rx = new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            return name =>
            {
                try { return rx.IsMatch(name); }
                catch (RegexMatchTimeoutException) { return false; }
            };
        }
        return name => name.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshItems(bool keepSelection)
    {
        var selected = new HashSet<FsNode>(ReferenceEqualityComparer.Instance);
        if (keepSelection)
            foreach (NodeItem i in _selection) selected.Add(i.Node);

        var list = new List<NodeItem>();
        bool truncated = false;
        if (_doc is not null && _current is not null)
        {
            if (_searchText.Length == 0)
            {
                foreach (FsNode n in _current.Children) list.Add(new NodeItem(n, _settings, _names));
            }
            else
            {
                Func<string, bool> match = Matcher(_searchText.Trim());
                IEnumerable<FsNode> source = _searchEverywhere ? FsTree.Walk(_current, includeStart: false) : _current.Children;
                foreach (FsNode n in source)
                {
                    if (!match(n.Name)) continue;
                    if (list.Count >= SearchLimit)
                    {
                        truncated = true;
                        break;
                    }
                    list.Add(new NodeItem(n, _settings, _names, showLocation: _searchEverywhere));
                }
            }
        }
        list.Sort(Comparer());

        Items.Clear();
        foreach (NodeItem i in list) Items.Add(i);
        _selection = new List<NodeItem>();
        if (selected.Count > 0)
        {
            List<NodeItem> again = list.Where(i => selected.Contains(i.Node)).ToList();
            if (again.Count > 0) SelectionRequested?.Invoke(again);
        }
        UpdateStatus(truncated);
        UpdateCommands();
    }

    private void SelectNodes(IEnumerable<FsNode> nodes)
    {
        var set = new HashSet<FsNode>(nodes, ReferenceEqualityComparer.Instance);
        List<NodeItem> items = Items.Where(i => set.Contains(i.Node)).ToList();
        if (items.Count > 0) SelectionRequested?.Invoke(items);
    }

    private void UpdateStatus(bool truncated = false)
    {
        if (_doc is null)
        {
            StatusText = L.T("Откройте образ или перетащите его в окно");
            return;
        }
        string text;
        if (_selection.Count > 0)
        {
            long bytes = 0;
            foreach (NodeItem i in _selection)
                if (i.Type == FsNodeType.File) bytes += i.Size;
            text = L.F("Выбрано: {0} из {1}", _selection.Count, Items.Count) + (bytes > 0 ? $" · {Fmt.Size(bytes)}" : "");
        }
        else
        {
            text = Fmt.Plural(Items.Count, "объект", "объекта", "объектов");
            if (_searchText.Length > 0) text = L.T("Найдено: ") + text;
        }
        if (truncated) text += L.F(" · показаны первые {0}", SearchLimit);
        if (_clipboard.Count > 0) text += L.F(" · в буфере: {0} ({1})", _clipboard.Count, (_clipboardCut ? L.T("вырезано") : L.T("скопировано")));
        StatusText = text;
    }

    /// <summary>Rebuilds the list and the folder tree after the model changed.</summary>
    private void AfterTreeChanged(IEnumerable<FsNode>? select = null)
    {
        if (_doc is null) return;
        // The folder being shown may have been removed or moved.
        if (_current is null || !IsAttached(_current)) _current = NearestDirectory(_doc.Tree, _currentPath);
        if (TreeRoots.Count > 0) TreeRoots[0].Sync();
        _clipboard.RemoveAll(n => !IsAttached(n));
        FsNode dir = _current;
        _current = null;                            // force NavigateTo to rebuild without a history entry
        string search = _searchText;
        NavigateTo(dir, history: false);
        if (search.Length > 0) SearchText = search;
        if (select is not null) SelectNodes(select);
        IsModified = _doc.IsModified;
        UpdateTitle();
    }

    private bool IsAttached(FsNode node)
    {
        FsNode n = node;
        while (n.Parent is not null) n = n.Parent;
        return ReferenceEquals(n, _doc?.Tree.Root) && (node.IsRoot || ReferenceEquals(node.Parent!.Find(node.Name), node));
    }

    // --------------------------------------------------------------- adding

    private TempSpool EnsureSpool()
    {
        if (_spool is null)
        {
            _spool = new TempSpool(_settings.EffectiveTempDirectory);
            _doc!.Tree.AttachResource(_spool);
        }
        return _spool;
    }

    private async Task AddFilesAsync()
    {
        IReadOnlyList<string> files = _dialogs.PickFilesToAdd(NullIfEmpty(_settings.LastHostDirectory));
        if (files.Count == 0) return;
        _settings.LastHostDirectory = Path.GetDirectoryName(files[0]) ?? "";
        await AddHostPathsAsync(files, _current!);
    }

    private async Task AddFolderAsync()
    {
        string? folder = _dialogs.PickFolder(L.T("Папка, которую нужно добавить в образ"), NullIfEmpty(_settings.LastHostDirectory));
        if (folder is null) return;
        _settings.LastHostDirectory = Path.GetDirectoryName(folder) ?? folder;
        await AddHostPathsAsync(new[] { folder }, _current!);
    }

    /// <summary>Picks the conflict policy, asking the user when the settings say so. Null means "cancel".</summary>
    private async Task<ConflictPolicy?> ChooseConflictPolicyAsync(int conflicts, string firstName)
    {
        switch (_settings.Conflicts)
        {
            case ConflictMode.Overwrite: return ConflictPolicy.Overwrite;
            case ConflictMode.Skip: return ConflictPolicy.Skip;
            case ConflictMode.Rename: return ConflictPolicy.Rename;
        }
        if (conflicts == 0) return ConflictPolicy.Overwrite;
        string what = conflicts == 1 ? L.F("«{0}» уже есть в этой папке.", firstName) : L.F("«{0}» и ещё {1} уже есть в этой папке.", firstName, conflicts - 1);
        DialogAnswer a = await _dialogs.AskAsync(L.T("Объекты с такими именами уже существуют"),
            what + L.T(" Папки будут объединены, файлы — заменены."), L.T("Заменить"), L.T("Сохранить оба"));
        return a switch
        {
            DialogAnswer.Primary => ConflictPolicy.Overwrite,
            DialogAnswer.Secondary => ConflictPolicy.Rename,
            _ => null,
        };
    }

    /// <summary>Adds host files and folders to a directory of the image.</summary>
    public async Task<bool> AddHostPathsAsync(IReadOnlyList<string> paths, FsNode target)
    {
        if (_doc is null || IsBusy || paths.Count == 0) return false;
        var clashing = paths.Select(p => Path.GetFileName(Path.TrimEndingDirectorySeparator(p))).Where(n => target.Find(n) is not null).ToList();
        ConflictPolicy? policy = await ChooseConflictPolicyAsync(clashing.Count, clashing.FirstOrDefault() ?? "");
        if (policy is null) return false;

        ImportOptions options = _settings.ToImportOptions(policy.Value);
        if (_settings.SnapshotAddedFiles) options.Snapshot = EnsureSpool();
        FsTree tree = _doc.Tree;
        ImportResult? result = null;
        bool ok = await RunBusyAsync(L.T("Добавление в ") + target.Path, (p, ct) => result = HostImporter.Import(tree, target, paths, options, p, ct));
        AfterTreeChanged(result?.Added);
        SaveSettings();
        if (ok && result is not null) ReportImport(result, L.T("Добавлено в образ"));
        return ok;
    }

    private void ReportImport(ImportResult r, string title)
    {
        string summary = $"{Fmt.Plural(r.Files, "файл", "файла", "файлов")}, {Fmt.Plural(r.Directories, "папка", "папки", "папок")}" +
                         (r.Symlinks > 0 ? $", {Fmt.Plural(r.Symlinks, "ссылка", "ссылки", "ссылок")}" : "") +
                         (r.Specials > 0 ? L.F(", специальных: {0}", r.Specials) : "") + $" · {Fmt.Size(r.Bytes)}" +
                         (r.Skipped > 0 ? L.F(" · пропущено: {0}", r.Skipped) : "");
        if (r.Warnings.Count > 0) ShowInfo(title + L.F(" (предупреждений: {0})", r.Warnings.Count), summary, MessageKind.Warning, r.Warnings, L.T("Предупреждения при добавлении"));
        else _dialogs.Notify(title, summary, MessageKind.Success);
    }

    private void ShowInfo(string title, string message, MessageKind kind, IReadOnlyList<string> details, string detailsTitle)
    {
        _pendingWarnings = details.ToList();
        _pendingWarningsTitle = detailsTitle;
        InfoTitle = title;
        InfoMessage = message.Length > 300 ? message[..300] + "…" : message;
        InfoKind = kind;
        IsInfoOpen = true;
        ShowWarningsCommand.RaiseCanExecuteChanged();
    }

    private void ShowPendingWarnings() =>
        _dialogs.ShowDialog(new MessagesViewModel(_pendingWarningsTitle, Fmt.Plural(_pendingWarnings.Count, "сообщение", "сообщения", "сообщений"), _pendingWarnings));

    /// <summary>
    /// Handles files dropped on the window. An image dropped on an empty window is opened; anything
    /// dropped on an opened image is added to <paramref name="target"/> (the folder being shown by default).
    /// </summary>
    public async Task HandleDropAsync(IReadOnlyList<string> paths, FsNode? target = null)
    {
        if (IsBusy || paths.Count == 0) return;
        bool single = paths.Count == 1 && File.Exists(paths[0]);
        ImageFormat? format = null;
        if (single)
        {
            try { format = ImageDocument.Detect(paths[0]); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FsException) { format = null; }
        }

        if (_doc is null)
        {
            if (single && format is not null)
            {
                await OpenPathAsync(paths[0]);
                return;
            }
            if (paths.Count == 1 && Directory.Exists(paths[0]))
            {
                DialogAnswer a = await _dialogs.AskAsync(L.T("Создать образ из папки?"),
                    L.F("Содержимое папки «{0}» станет корнем нового образа. Формат выбирается при сохранении.", Path.GetFileName(paths[0])),
                    L.T("Создать образ"));
                if (a == DialogAnswer.Primary) await NewFromFolderAsync(paths[0]);
                return;
            }
            if (single)
            {
                // Not a known image: let the opener explain why (or find an embedded filesystem).
                await OpenPathAsync(paths[0]);
                return;
            }
            DialogAnswer b = await _dialogs.AskAsync(L.T("Создать новый образ?"),
                L.F("Перетащено объектов: {0}. Создать новый образ и добавить их в его корень?", paths.Count), L.T("Создать образ"));
            if (b != DialogAnswer.Primary) return;
            SetDocument(ImageDocument.CreateNew(_settings.NewImageFormat));
            await AddHostPathsAsync(paths, _doc!.Tree.Root);
            return;
        }

        target ??= _current!;
        if (single && format is not null)
        {
            DialogAnswer a = await _dialogs.AskAsync(L.T("Что сделать с файлом?"),
                L.F("«{0}» — это образ или архив. Его можно открыть вместо текущего образа или добавить в папку {1} как обычный файл.", Path.GetFileName(paths[0]), target.Path),
                L.T("Открыть"), L.T("Добавить как файл"));
            if (a == DialogAnswer.Cancel) return;
            if (a == DialogAnswer.Primary)
            {
                await OpenPathAsync(paths[0]);
                return;
            }
        }
        await AddHostPathsAsync(paths, target);
    }

    // ------------------------------------------------------------ archives

    private async Task ImportArchiveAsync()
    {
        string? path = _dialogs.PickImageToOpen(NullIfEmpty(_settings.LastOpenDirectory));
        if (path is not null) await ImportArchiveAsync(path, _current!);
    }

    /// <summary>Unpacks an archive (or any supported image) into a directory of the current image.</summary>
    public async Task<bool> ImportArchiveAsync(string path, FsNode target)
    {
        if (_doc is null || IsBusy) return false;
        ImageDocument? source = null;
        bool ok = await RunBusyAsync(L.T("Чтение ") + Path.GetFileName(path), (p, ct) =>
            source = ImageDocument.Open(path, _settings.EffectiveTempDirectory, _settings.SearchEmbedded, p, ct,
                choosePartition: table => AskPartition(path, table), chooseVolume: ubi => AskVolume(path, ubi),
                chooseAndroidPart: image => AskAndroidPart(path, image), chooseArchiveEntry: archive => AskArchiveEntry(path, archive),
                chooseFirmwarePart: image => AskFirmwarePart(path, image)));
        if (!ok || source is null) return false;

        var clashing = source.Tree.Root.Children.Where(c => target.Find(c.Name) is not null).Select(c => c.Name).ToList();
        ConflictPolicy? policy = await ChooseConflictPolicyAsync(clashing.Count, clashing.FirstOrDefault() ?? "");
        if (policy is null)
        {
            source.Dispose();
            return false;
        }

        FsTree tree = _doc.Tree;
        IReadOnlyList<string> warnings = source.Warnings.ToList();
        TreeMerger.Result? result = null;
        ok = await RunBusyAsync(L.T("Импорт в ") + target.Path, (_, ct) => result = TreeMerger.Merge(tree, target, source.Tree, policy.Value, ct));
        if (result is null) source.Dispose();       // nothing was handed over
        AfterTreeChanged(result?.TopLevel);
        if (!ok || result is null) return false;

        string summary = L.F("Добавлено объектов: {0}", result.Added) + (result.Replaced > 0 ? L.F(", заменено: {0}", result.Replaced) : "") +
                         (result.Merged > 0 ? L.F(", объединено папок: {0}", result.Merged) : "") + (result.Skipped > 0 ? L.F(", пропущено: {0}", result.Skipped) : "");
        if (warnings.Count > 0) ShowInfo(L.F("Архив импортирован (предупреждений: {0})", warnings.Count), summary, MessageKind.Warning, warnings, L.T("Предупреждения при чтении архива"));
        else _dialogs.Notify(L.T("Архив импортирован"), summary, MessageKind.Success);
        return true;
    }

    private async Task ExportArchiveAsync()
    {
        // A single selected folder is exported; otherwise the folder being shown.
        FsNode start = _selection.Count == 1 && _selection[0].IsDirectory ? _selection[0].Node : _current!;
        SaveOptions defaults = _settings.DefaultSaveOptions(_doc!, ImageFormat.Tar);
        defaults.Tar = _doc!.Tar is null ? _settings.Tar.Clone() : defaults.Tar;
        var vm = new SaveOptionsViewModel(_doc, defaults, L.F("Экспорт {0} в архив", start.Path), lockFormat: true);
        if (!_dialogs.ShowDialog(vm) || vm.Result is null) return;
        string name = (start.IsRoot ? (_doc.FilePath is null ? "rootfs" : StripExtensions(Path.GetFileName(_doc.FilePath))) : start.Name) + vm.Extension;
        string? path = _dialogs.PickSaveFile(L.T("Экспорт в архив"), name, ImageFormat.Tar, vm.Extension, NullIfEmpty(_settings.LastSaveDirectory));
        if (path is not null) await ExportArchiveAsync(start, path, vm.Result.Tar);
    }

    /// <summary>Writes a directory of the image as a tar archive without touching the document.</summary>
    public async Task<bool> ExportArchiveAsync(FsNode start, string path, TarOptions options)
    {
        if (_doc is null || IsBusy) return false;
        FsTree tree = _doc.Tree;
        options.Threads = _settings.Threads;
        IReadOnlyList<string> warnings = Array.Empty<string>();
        string tmp = path + ".fsforge-tmp";
        bool ok = await RunBusyAsync(L.T("Экспорт ") + Path.GetFileName(path), (p, ct) =>
        {
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                    warnings = TarWriter.Write(tree, fs, options, start, p, ct);
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                try { File.Delete(tmp); } catch (IOException) { /* best effort */ }
                throw;
            }
        });
        if (!ok) return false;
        _settings.LastSaveDirectory = Path.GetDirectoryName(path) ?? "";
        SaveSettings();
        string done = $"{Path.GetFileName(path)} — {Fmt.Size(new FileInfo(path).Length)}";
        if (warnings.Count > 0) ShowInfo(L.F("Архив создан с предупреждениями: {0}", warnings.Count), done, MessageKind.Warning, warnings, L.T("Предупреждения при экспорте"));
        else _dialogs.Notify(L.T("Архив создан"), done, MessageKind.Success);
        return true;
    }

    // ----------------------------------------------------------- extracting

    private async Task ExtractSelectedAsync()
    {
        List<FsNode> nodes = _selection.Count > 0 ? _selection.Select(i => i.Node).ToList() : new List<FsNode> { _current! };
        string? folder = _dialogs.PickFolder(L.T("Папка, в которую нужно извлечь"), NullIfEmpty(_settings.LastHostDirectory));
        if (folder is not null) await ExtractAsync(nodes, folder);
    }

    private async Task ExtractAllAsync()
    {
        string? folder = _dialogs.PickFolder(L.T("Папка, в которую нужно распаковать весь образ"), NullIfEmpty(_settings.LastHostDirectory));
        if (folder is not null) await ExtractAsync(new[] { _doc!.Tree.Root }, folder);
    }

    /// <summary>Unpacks nodes into a host folder.</summary>
    public async Task<bool> ExtractAsync(IReadOnlyList<FsNode> nodes, string folder)
    {
        if (_doc is null || IsBusy || nodes.Count == 0) return false;
        ExtractOptions options = _settings.ToExtractOptions();
        ExtractResult? r = null;
        bool ok = await RunBusyAsync(L.T("Извлечение в ") + folder, (p, ct) => r = HostExtractor.Extract(nodes, folder, options, p, ct));
        if (!ok || r is null) return false;

        _settings.LastHostDirectory = folder;
        SaveSettings();
        string summary = $"{Fmt.Plural(r.Files, "файл", "файла", "файлов")}, {Fmt.Plural(r.Directories, "папка", "папки", "папок")} · {Fmt.Size(r.Bytes)}" +
                         (r.Renamed > 0 ? L.F(" · переименовано для Windows: {0}", r.Renamed) : "") + (r.Skipped > 0 ? L.F(" · пропущено: {0}", r.Skipped) : "");
        if (r.Warnings.Count > 0) ShowInfo(L.F("Извлечено с предупреждениями: {0}", r.Warnings.Count), summary, MessageKind.Warning, r.Warnings, L.T("Предупреждения при извлечении"));
        else _dialogs.Notify(L.T("Извлечено"), summary, MessageKind.Success);
        if (_settings.OpenFolderAfterExtract) TryShell(() => _shell.OpenFolder(folder));
        return true;
    }

    /// <summary>Largest selection that may be dragged out of the window; bigger ones use "Extract".</summary>
    public const long DragLimitBytes = 256L << 20;

    /// <summary>
    /// Extracts nodes to a temporary folder (no metadata file) and returns the host paths. It runs on
    /// the calling thread because a drag has to start while the mouse button is still down.
    /// </summary>
    public IReadOnlyList<string> ExtractForDrag(IReadOnlyList<FsNode> nodes)
    {
        if (_doc is null || IsBusy || nodes.Count == 0) return Array.Empty<string>();
        long bytes = 0;
        foreach (FsNode n in nodes) bytes += FsTree.GetStats(n).DataBytes;
        if (bytes > DragLimitBytes)
        {
            _dialogs.Notify(L.T("Слишком много данных для перетаскивания"),
                L.F("Выбрано {0}. Используйте команду «Извлечь».", Fmt.Size(bytes)), MessageKind.Warning);
            return Array.Empty<string>();
        }
        string folder = Path.Combine(EditDirectory(), "drag-" + Guid.NewGuid().ToString("N")[..8]);
        ExtractOptions options = _settings.ToExtractOptions();
        options.WriteManifest = false;
        try
        {
            HostExtractor.Extract(nodes, folder, options);
            return Directory.GetFileSystemEntries(folder);
        }
        catch (Exception ex) when (ex is FsException or IOException or UnauthorizedAccessException)
        {
            _dialogs.Notify(L.T("Не удалось подготовить файлы"), ex.Message, MessageKind.Error);
            return Array.Empty<string>();
        }
    }

    private Task ShowAboutAsync()
    {
        string version = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "";
        return _dialogs.ShowMessageAsync(L.T("О программе FsForge"),
            $"FsForge {version}" + Environment.NewLine +
            L.T("Редактор образов SquashFS, EROFS и ext2/ext3/ext4, архивов rootfs.tar (gz, xz, zst).") + Environment.NewLine + Environment.NewLine +
            L.T("Чтение и запись файловых систем выполняются собственным кодом на C#: без WSL и без утилит Linux.") + Environment.NewLine +
            L.T("Интерфейс: WPF и библиотека WPF-UI (Fluent).") + Environment.NewLine + Environment.NewLine +
            L.T("Настройки: ") + _store.FilePath);
    }

    // -------------------------------------------------------------- editing

    private void CreateItem(NewItemKind kind)
    {
        var vm = new NewItemViewModel(_doc!.Tree, _current!, kind, _settings);
        if (_dialogs.ShowDialog(vm) && vm.Created is not null) AfterTreeChanged(new[] { vm.Created });
    }

    private async Task DeleteAsync()
    {
        List<FsNode> nodes = _selection.Select(i => i.Node).Where(n => !n.IsRoot).ToList();
        if (nodes.Count == 0) return;
        if (_settings.ConfirmDelete)
        {
            long inside = nodes.Where(n => n.IsDirectory).Sum(n => FsTree.GetStats(n).Total - 1);
            string what = nodes.Count == 1 ? $"«{nodes[0].Name}»" : Fmt.Plural(nodes.Count, "объект", "объекта", "объектов");
            string extra = inside > 0 ? L.F(" Вместе с вложенными: {0}.", Fmt.Plural(inside, "объект", "объекта", "объектов")) : "";
            DialogAnswer a = await _dialogs.AskAsync(L.T("Удаление"), L.F("Удалить {0} из образа?{1}", what, extra), L.T("Удалить"));
            if (a != DialogAnswer.Primary) return;
        }
        FsTree tree = _doc!.Tree;
        using (tree.SuspendNotifications())
        {
            foreach (FsNode n in nodes)
                if (IsAttached(n)) tree.Remove(n);
        }
        AfterTreeChanged();
    }

    private void Rename()
    {
        FsNode node = _selection[0].Node;
        if (node.IsRoot) return;
        var vm = new PromptViewModel(L.T("Переименование"), L.T("Новое имя"), node.Name, text => Validation.Name(node.Parent!, text, node), L.T("Переименовать"));
        if (!_dialogs.ShowDialog(vm) || vm.Text == node.Name) return;
        try
        {
            _doc!.Tree.Rename(node, vm.Text);
        }
        catch (FsException ex)
        {
            _dialogs.Notify(L.T("Не удалось переименовать"), Validation.Translate(ex.Message), MessageKind.Error);
            return;
        }
        AfterTreeChanged(new[] { node });
    }

    private void ShowProperties()
    {
        List<FsNode> nodes = _selection.Count > 0 ? _selection.Select(i => i.Node).ToList() : new List<FsNode> { _current! };
        var vm = new PropertiesViewModel(_doc!.Tree, nodes, _settings, _names);
        if (!_dialogs.ShowDialog(vm) || !vm.Changed) return;
        _doc.Touch();
        // Owner names shown in the list come from /etc/passwd, which may just have been renamed.
        AfterTreeChanged(nodes);
    }

    private void SetClipboard(bool cut)
    {
        _clipboard = _selection.Select(i => i.Node).Where(n => !n.IsRoot).ToList();
        _clipboardCut = cut;
        UpdateStatus();
        UpdateCommands();
    }

    private async Task PasteAsync()
    {
        FsTree tree = _doc!.Tree;
        FsNode target = _current!;
        var added = new List<FsNode>();
        var errors = new List<string>();
        using (tree.SuspendNotifications())
        {
            foreach (FsNode node in _clipboard.ToList())
            {
                if (!IsAttached(node)) continue;
                try
                {
                    if (_clipboardCut)
                    {
                        if (ReferenceEquals(node.Parent, target)) continue;
                        string name = target.Find(node.Name) is null ? node.Name : FsTree.MakeUniqueName(target, node.Name);
                        tree.Move(node, target, name);
                        added.Add(node);
                    }
                    else
                    {
                        if (target.IsDescendantOf(node) || ReferenceEquals(target, node))
                        {
                            errors.Add(L.F("«{0}»: нельзя скопировать папку внутрь самой себя.", node.Name));
                            continue;
                        }
                        added.Add(tree.Copy(node, target, FsTree.MakeUniqueName(target, node.Name)));
                    }
                }
                catch (FsException ex)
                {
                    errors.Add($"«{node.Name}»: {Validation.Translate(ex.Message)}");
                }
            }
        }
        if (_clipboardCut) _clipboard = new List<FsNode>();
        AfterTreeChanged(added);
        if (errors.Count > 0) await _dialogs.ShowMessageAsync(L.T("Вставка"), string.Join(Environment.NewLine, errors), MessageKind.Warning);
    }

    private void CopyPath()
    {
        string text = _selection.Count > 0 ? string.Join(Environment.NewLine, _selection.Select(i => i.Node.Path)) : _current!.Path;
        TryShell(() => _shell.SetClipboardText(text));
    }

    private async Task OpenItemAsync(NodeItem? item)
    {
        if (item is null || _doc is null) return;
        if (item.IsDirectory)
        {
            NavigateTo(item.Node);
            return;
        }
        // A link to a directory inside the image behaves like the directory.
        if (item.Type == FsNodeType.Symlink && ResolveLink(item.Node) is { } linked)
        {
            if (linked.IsDirectory) NavigateTo(linked);
            else
            {
                NavigateTo(linked.Parent!);
                SelectNodes(new[] { linked });
            }
            return;
        }
        if (item.Type == FsNodeType.File && _settings.DoubleClickFile == FileOpenAction.OpenExternally)
        {
            await OpenExternallyAsync(item.Node);
            return;
        }
        SelectionRequested?.Invoke(new[] { item });
        _selection = new List<NodeItem> { item };
        ShowProperties();
    }

    private FsNode? ResolveLink(FsNode link)
    {
        string? target = link.Inode.LinkTarget;
        if (string.IsNullOrEmpty(target) || _doc is null) return null;
        FsNode? node = target.StartsWith('/') ? _doc.Tree.Root : link.Parent;
        foreach (string part in target.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node is null) return null;
            node = part switch
            {
                "." => node,
                ".." => node.Parent ?? node,
                _ => node.Find(part),
            };
        }
        return node is null || ReferenceEquals(node, link) ? null : node;
    }

    private string EditDirectory()
    {
        if (_editDirectory is null)
        {
            string root = _settings.EffectiveTempDirectory ?? Path.GetTempPath();
            _editDirectory = Path.Combine(root, "FsForge", "session-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_editDirectory);
        }
        return _editDirectory;
    }

    private async Task OpenExternallyAsync(FsNode? node)
    {
        if (node is null || node.Type != FsNodeType.File || _doc is null) return;
        string dir = Path.Combine(EditDirectory(), "edit-" + Guid.NewGuid().ToString("N")[..8]);
        string hostPath = Path.Combine(dir, HostNames.MakeSafe(node.Name));
        var options = new ExtractOptions { WriteManifest = false, RestoreTimestamps = true };
        bool ok = await RunBusyAsync(L.T("Открытие ") + node.Name, (p, ct) => HostExtractor.Extract(new[] { node }, dir, options, p, ct));
        if (!ok || !File.Exists(hostPath)) return;

        var info = new FileInfo(hostPath);
        _edits.RemoveAll(e => e.ImagePath == node.Path);
        _edits.Add(new EditSession(node.Path, hostPath, info.Length, info.LastWriteTimeUtc));
        // Files without an extension have no association on Windows: use the configured editor for them.
        string? program = Path.GetExtension(hostPath).Length == 0 || IsTextLike(hostPath) ? NullIfEmpty(_settings.TextEditor) : null;
        try
        {
            _shell.OpenFile(hostPath, program);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            await _dialogs.ShowMessageAsync(L.T("Не удалось открыть файл"), ex.Message, MessageKind.Error);
        }
    }

    private static bool IsTextLike(string path) => Path.GetExtension(path).ToLowerInvariant()
        is ".conf" or ".cfg" or ".ini" or ".sh" or ".rc" or ".lua" or ".json" or ".txt" or ".list" or ".rules" or ".service" or ".mount" or ".env" or ".yaml" or ".yml";

    /// <summary>
    /// Looks for files that were changed in an external editor since they were opened and offers to
    /// put the new content into the image. The view calls this when the window is activated.
    /// </summary>
    public async Task CheckExternalEditsAsync()
    {
        if (_doc is null || IsBusy || _checkingEdits || _edits.Count == 0) return;
        _checkingEdits = true;
        try
        {
            foreach (EditSession e in _edits.ToList())
            {
                if (_doc is null) return;           // the image was closed while a question was open
                var info = new FileInfo(e.HostPath);
                if (!info.Exists)
                {
                    _edits.Remove(e);
                    continue;
                }
                if (info.Length == e.Length && info.LastWriteTimeUtc == e.LastWriteUtc) continue;

                // Whatever the answer, do not ask again for the same version of the file.
                _edits.Remove(e);
                _edits.Add(e with { Length = info.Length, LastWriteUtc = info.LastWriteTimeUtc });
                if (_doc.Tree.Resolve(e.ImagePath) is not { Type: FsNodeType.File } node) continue;
                DialogAnswer a = await _dialogs.AskAsync(L.T("Файл изменён во внешней программе"),
                    L.F("{0} был изменён. Записать новое содержимое в образ?", e.ImagePath), L.T("Обновить в образе"));
                if (a != DialogAnswer.Primary || _doc is null || !IsAttached(node)) continue;
                try
                {
                    IFileData? data = null;
                    if (info.Length > 0)
                    {
                        using var fs = new FileStream(e.HostPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        data = EnsureSpool().Append(fs, fs.Length);
                    }
                    _doc.Tree.SetData(node, data, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    AfterTreeChanged(new[] { node });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FsException)
                {
                    await _dialogs.ShowMessageAsync(L.T("Не удалось прочитать изменённый файл"), ex.Message, MessageKind.Error);
                }
            }
        }
        finally
        {
            _checkingEdits = false;
        }
    }

    private async Task ReplaceContentAsync()
    {
        FsNode node = _selection[0].Node;
        IReadOnlyList<string> files = _dialogs.PickFilesToAdd(NullIfEmpty(_settings.LastHostDirectory));
        if (files.Count != 1)
        {
            if (files.Count > 1) await _dialogs.ShowMessageAsync(L.T("Замена содержимого"), L.T("Выберите один файл."), MessageKind.Warning);
            return;
        }
        try
        {
            var info = new FileInfo(files[0]);
            IFileData? data = null;
            if (info.Length > 0)
            {
                if (_settings.SnapshotAddedFiles)
                {
                    using var fs = new FileStream(files[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    data = EnsureSpool().Append(fs, fs.Length);
                }
                else
                {
                    data = new HostFileData(files[0]);
                }
            }
            long mtime = _settings.UseHostTimestamps
                ? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeSeconds()
                : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _doc!.Tree.SetData(node, data, mtime);
            AfterTreeChanged(new[] { node });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FsException)
        {
            await _dialogs.ShowMessageAsync(L.T("Замена содержимого"), ex.Message, MessageKind.Error);
        }
    }

    // ------------------------------------------------------------- settings

    private void ShowSettings()
    {
        var vm = new SettingsViewModel(_settings, _dialogs, _store.FilePath);
        if (!_dialogs.ShowDialog(vm) || vm.Result is null) return;
        ApplySettings(vm.Result);
    }

    /// <summary>Replaces the settings, saves them and refreshes everything that depends on them.</summary>
    public void ApplySettings(AppSettings settings)
    {
        bool appearance = settings.Theme != _settings.Theme || settings.Backdrop != _settings.Backdrop;
        AppLanguage language = L.Resolve(settings.Language);
        bool languageChanged = language != L.Resolve(_settings.Language);
        _settings = settings;
        SaveSettings();
        RefreshRecent();
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(ShowTreePane));
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortDescending));
        if (_doc is not null) RefreshItems(keepSelection: true);
        if (appearance) AppearanceChanged?.Invoke();
        // The interface is built once in one language: say so in the language that was chosen.
        if (languageChanged)
            _dialogs.Notify(L.T("Язык интерфейса", language), L.T("Новый язык будет использован после перезапуска программы.", language), MessageKind.Info);
    }

    public void SaveSettings() => _store.Save(_settings);

    /// <summary>Removes one entry from the recent files list (the file itself is not touched).</summary>
    public void RemoveRecent(RecentItem item)
    {
        if (_settings.RecentFiles.RemoveAll(p => string.Equals(p, item.Path, StringComparison.OrdinalIgnoreCase)) == 0) return;
        RefreshRecent();
        SaveSettings();
    }

    private void RefreshRecent()
    {
        RecentFiles.Clear();
        foreach (string p in _settings.RecentFiles) RecentFiles.Add(new RecentItem(p));
        OnPropertyChanged(nameof(HasRecentFiles));
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private void TryShell(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception
                                       or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            _dialogs.Notify(L.T("Не удалось выполнить действие"), ex.Message, MessageKind.Warning);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        if (_doc is not null)
        {
            _doc.Changed -= OnDocumentChanged;
            if (!_doc.IsDisposed) _doc.Dispose();
            _doc = null;
        }
        if (_editDirectory is not null)
        {
            try { Directory.Delete(_editDirectory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a file is still open in an editor */ }
        }
    }
}
