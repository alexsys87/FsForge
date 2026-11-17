using System.Collections.ObjectModel;
using FsForge.Core;
using FsForge.Core.Images;
using FsForge.Core.Ubifs;
using FsForge.Presentation.Mvvm;
using FsForge.Presentation.Services;
using FsForge.Presentation.Settings;

namespace FsForge.Presentation.ViewModels;

/// <summary>Base of the modal dialogs: a title, an error line and an "accept" check.</summary>
public abstract class DialogViewModel : ObservableObject
{
    private string? _error;

    public string Title { get; protected set; } = "";
    public string AcceptText { get; protected set; } = L.T("ОК");

    /// <summary>Why the dialog cannot be accepted right now; null when everything is valid.</summary>
    public string? Error
    {
        get => _error;
        protected set
        {
            if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    /// <summary>Validates and applies the input. The dialog closes only when this returns true.</summary>
    public abstract bool Accept();
}

/// <summary>Asks for a single line of text (rename, new folder...).</summary>
public sealed class PromptViewModel : DialogViewModel
{
    private readonly Func<string, string?> _validate;
    private string _text;

    public PromptViewModel(string title, string label, string text, Func<string, string?> validate, string? acceptText = null, string? hint = null)
    {
        Title = title;
        Label = label;
        AcceptText = acceptText ?? L.T("ОК");
        Hint = hint ?? "";
        _text = text;
        _validate = validate;
    }

    public string Label { get; }
    public string Hint { get; }

    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value)) Error = null;
        }
    }

    public override bool Accept()
    {
        Error = _validate(_text);
        return Error is null;
    }
}

/// <summary>Kinds of objects the "Create" dialog can make.</summary>
public enum NewItemKind
{
    Folder,
    File,
    Symlink,
    CharDevice,
    BlockDevice,
    Fifo,
    Socket,
}

/// <summary>Creates a folder, an empty file, a symbolic link or a special file in a directory.</summary>
public sealed class NewItemViewModel : DialogViewModel
{
    private readonly FsTree _tree;
    private readonly FsNode _parent;
    private NewItemKind _kind;
    private string _name = "", _target = "", _mode, _major = "0", _minor = "0";

    public NewItemViewModel(FsTree tree, FsNode parent, NewItemKind kind, AppSettings settings)
    {
        _tree = tree;
        _parent = parent;
        _kind = kind;
        Settings = settings;
        _mode = DefaultMode(kind);
        Title = L.T("Создать в ") + parent.Path;
        AcceptText = L.T("Создать");
        _name = kind switch
        {
            NewItemKind.Folder => FsTree.MakeUniqueName(parent, L.T("Новая папка")),
            NewItemKind.File => FsTree.MakeUniqueName(parent, L.T("Новый файл")),
            _ => "",
        };
    }

    private AppSettings Settings { get; }

    public IReadOnlyList<Choice<NewItemKind>> Kinds { get; } = new[]
    {
        new Choice<NewItemKind>(NewItemKind.Folder, L.T("Папка")),
        new Choice<NewItemKind>(NewItemKind.File, L.T("Пустой файл")),
        new Choice<NewItemKind>(NewItemKind.Symlink, L.T("Символическая ссылка")),
        new Choice<NewItemKind>(NewItemKind.CharDevice, L.T("Символьное устройство")),
        new Choice<NewItemKind>(NewItemKind.BlockDevice, L.T("Блочное устройство")),
        new Choice<NewItemKind>(NewItemKind.Fifo, L.T("Именованный канал (FIFO)")),
        new Choice<NewItemKind>(NewItemKind.Socket, L.T("Сокет")),
    };

    private string DefaultMode(NewItemKind kind) => kind switch
    {
        NewItemKind.Folder => Settings.DefaultDirectoryMode,
        NewItemKind.File => Settings.DefaultFileMode,
        NewItemKind.Symlink => "0777",
        NewItemKind.CharDevice or NewItemKind.BlockDevice => "0600",
        _ => "0644",
    };

    public NewItemKind Kind
    {
        get => _kind;
        set
        {
            if (!SetProperty(ref _kind, value)) return;
            Mode = DefaultMode(value);
            OnPropertyChanged(nameof(IsSymlink));
            OnPropertyChanged(nameof(IsDevice));
            OnPropertyChanged(nameof(HasMode));
            Error = null;
        }
    }

    public bool IsSymlink => _kind == NewItemKind.Symlink;
    public bool IsDevice => _kind is NewItemKind.CharDevice or NewItemKind.BlockDevice;
    public bool HasMode => _kind != NewItemKind.Symlink;

    public string Name { get => _name; set { if (SetProperty(ref _name, value)) Error = null; } }
    public string Target { get => _target; set { if (SetProperty(ref _target, value)) Error = null; } }
    public string Mode { get => _mode; set { if (SetProperty(ref _mode, value)) Error = null; } }
    public string Major { get => _major; set { if (SetProperty(ref _major, value)) Error = null; } }
    public string Minor { get => _minor; set { if (SetProperty(ref _minor, value)) Error = null; } }

    /// <summary>The node that was created.</summary>
    public FsNode? Created { get; private set; }

    public override bool Accept()
    {
        string? nameError = Validation.Name(_parent, _name, null);
        if (nameError is not null)
        {
            Error = nameError;
            return false;
        }
        int mode = 0x1FF;
        if (HasMode && !UnixMode.TryParseOctal(_mode.Trim(), out mode))
        {
            Error = L.T("Права задаются восьмеричным числом, например 0755.");
            return false;
        }
        uint major = 0, minor = 0;
        if (IsDevice && (!uint.TryParse(_major.Trim(), out major) || !uint.TryParse(_minor.Trim(), out minor)))
        {
            Error = L.T("Старший и младший номера устройства — целые неотрицательные числа.");
            return false;
        }
        if (IsSymlink && string.IsNullOrEmpty(_target))
        {
            Error = L.T("Укажите, на что указывает ссылка.");
            return false;
        }

        uint uid = Settings.InheritOwner ? _parent.Inode.Uid : Settings.DefaultUid;
        uint gid = Settings.InheritOwner ? _parent.Inode.Gid : Settings.DefaultGid;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            Created = _kind switch
            {
                NewItemKind.Folder => _tree.CreateDirectory(_parent, _name, mode, uid, gid, now),
                NewItemKind.File => _tree.CreateFile(_parent, _name, null, mode, uid, gid, now),
                NewItemKind.Symlink => _tree.CreateSymlink(_parent, _name, _target, uid, gid, now),
                NewItemKind.CharDevice => _tree.CreateSpecial(_parent, _name, FsNodeType.CharDevice, major, minor, mode, uid, gid, now),
                NewItemKind.BlockDevice => _tree.CreateSpecial(_parent, _name, FsNodeType.BlockDevice, major, minor, mode, uid, gid, now),
                NewItemKind.Fifo => _tree.CreateSpecial(_parent, _name, FsNodeType.Fifo, 0, 0, mode, uid, gid, now),
                _ => _tree.CreateSpecial(_parent, _name, FsNodeType.Socket, 0, 0, mode, uid, gid, now),
            };
            return true;
        }
        catch (FsException ex)
        {
            Error = Validation.Translate(ex.Message);
            return false;
        }
    }
}

/// <summary>Input checks shared by the dialogs.</summary>
public static class Validation
{
    /// <summary>Checks a new name for an object in <paramref name="parent"/>; null when it is acceptable.</summary>
    public static string? Name(FsNode parent, string name, FsNode? self)
    {
        if (string.IsNullOrEmpty(name)) return L.T("Введите имя.");
        if (name is "." or "..") return L.T("Имена «.» и «..» зарезервированы.");
        if (name.Contains('/')) return L.T("Имя не может содержать символ «/».");
        if (name.Contains('\0')) return L.T("Имя не может содержать нулевой символ.");
        if (PosixName.Encode(name).Length > 255) return L.T("Имя длиннее 255 байт.");
        FsNode? existing = parent.Find(name);
        if (existing is not null && !ReferenceEquals(existing, self)) return L.T("Объект с таким именем уже существует в этой папке.");
        return null;
    }

    /// <summary>Russian wording for the most common messages of the core.</summary>
    public static string Translate(string message)
    {
        if (message.Contains("already exists")) return L.T("Объект с таким именем уже существует.");
        if (message.Contains("into itself")) return L.T("Нельзя переместить папку внутрь самой себя.");
        if (message.Contains("not a directory")) return L.T("Целевой объект не является папкой.");
        if (message.Contains("longer than 4095")) return L.T("Цель ссылки длиннее 4095 байт.");
        if (message.Contains("namespace prefix")) return L.T("Имя атрибута должно начинаться с пространства имён: user., security., trusted. или system.");
        return message;
    }
}

/// <summary>One extended attribute in the properties dialog.</summary>
public sealed class XattrRow : ObservableObject
{
    private string _name, _value;

    public XattrRow(string name, string value)
    {
        _name = name;
        _value = value;
    }

    public string Name { get => _name; set => SetProperty(ref _name, value); }

    /// <summary>Text, or "0x..." for binary data; a trailing "\0" stands for a NUL byte.</summary>
    public string Value { get => _value; set => SetProperty(ref _value, value); }
}

/// <summary>
/// Properties of one or several nodes: name, permissions, owner, time, link target, device numbers
/// and extended attributes. Only fields the user changed are written back.
/// </summary>
public sealed class PropertiesViewModel : DialogViewModel
{
    private readonly FsTree _tree;
    private readonly IReadOnlyList<FsNode> _nodes;
    private readonly AppSettings _settings;
    private readonly string _initialName, _initialOctal, _initialUid, _initialGid, _initialTime, _initialTarget, _initialMajor, _initialMinor;
    private readonly string _initialXattrs;
    private string _name, _octal, _uid, _gid, _time, _target, _major, _minor;
    private int _mode;
    private bool _modeKnown, _recursive, _syncing;
    private FsTree.Targets _recursiveTargets = FsTree.Targets.All;
    private XattrRow? _selectedXattr;

    public PropertiesViewModel(FsTree tree, IReadOnlyList<FsNode> nodes, AppSettings settings, IdNames names)
    {
        if (nodes.Count == 0) throw new ArgumentException("No nodes.", nameof(nodes));
        _tree = tree;
        _nodes = nodes;
        _settings = settings;
        FsNode first = nodes[0];
        FsInode fi = first.Inode;
        IsSingle = nodes.Count == 1;
        Title = IsSingle ? L.T("Свойства: ") + (first.IsRoot ? "/" : first.Name) : L.F("Свойства: {0}", Fmt.Plural(nodes.Count, "объект", "объекта", "объектов"));
        AcceptText = L.T("Применить");

        _name = _initialName = IsSingle && !first.IsRoot ? first.Name : "";
        CanRename = IsSingle && !first.IsRoot;
        PathText = IsSingle ? first.Path : (first.Parent?.Path ?? "/");
        HasDirectories = nodes.Any(n => n.IsDirectory);
        AllSymlinks = nodes.All(n => n.Type == FsNodeType.Symlink);
        IsSymlink = IsSingle && fi.Type == FsNodeType.Symlink;
        IsDevice = IsSingle && fi.Type is FsNodeType.CharDevice or FsNodeType.BlockDevice;

        // Summary lines.
        if (IsSingle)
        {
            TypeText = Fmt.TypeName(fi.Type);
            if (first.IsDirectory)
            {
                TreeStats st = FsTree.GetStats(first);
                SizeText = $"{Fmt.SizeWithBytes(st.DataBytes)}; {Fmt.Plural(st.Files, "файл", "файла", "файлов")}, {Fmt.Plural(st.Directories - 1, "папка", "папки", "папок")}";
            }
            else
            {
                SizeText = fi.Type is FsNodeType.File or FsNodeType.Symlink ? Fmt.SizeWithBytes(fi.Size) : "—";
            }
            LinksText = first.IsDirectory ? "" : fi.LinkCount > 1 ? L.F("{0} (жёсткие ссылки на один файл)", fi.LinkCount) : "1";
        }
        else
        {
            long bytes = 0, files = 0, dirs = 0;
            foreach (FsNode n in nodes)
            {
                TreeStats st = FsTree.GetStats(n);
                bytes += st.DataBytes;
                files += st.Files + st.Symlinks + st.Specials;
                dirs += st.Directories;
            }
            TypeText = L.T("Несколько объектов");
            SizeText = $"{Fmt.SizeWithBytes(bytes)}; {Fmt.Plural(files, "файл", "файла", "файлов")}, {Fmt.Plural(dirs, "папка", "папки", "папок")}";
            LinksText = "";
        }

        // Shared values: a field is pre-filled only when every selected node agrees on it.
        var modeNodes = nodes.Where(n => n.Type != FsNodeType.Symlink).ToList();
        _modeKnown = modeNodes.Count > 0 && modeNodes.All(n => (n.Inode.Mode & 0xFFF) == (modeNodes[0].Inode.Mode & 0xFFF));
        _mode = _modeKnown ? modeNodes[0].Inode.Mode & 0xFFF : 0;
        _octal = _initialOctal = _modeKnown ? UnixMode.ToOctal(_mode) : "";
        _uid = _initialUid = nodes.All(n => n.Inode.Uid == fi.Uid) ? fi.Uid.ToString() : "";
        _gid = _initialGid = nodes.All(n => n.Inode.Gid == fi.Gid) ? fi.Gid.ToString() : "";
        _time = _initialTime = nodes.All(n => n.Inode.MTime == fi.MTime) ? Fmt.Date(fi.MTime, settings.UtcTimes) : "";
        _target = _initialTarget = IsSymlink ? fi.LinkTarget ?? "" : "";
        _major = _initialMajor = IsDevice ? fi.DevMajor.ToString() : "";
        _minor = _initialMinor = IsDevice ? fi.DevMinor.ToString() : "";
        UserHint = IsSingle || _initialUid.Length > 0 ? names.User(fi.Uid) ?? "" : "";
        GroupHint = IsSingle || _initialGid.Length > 0 ? names.Group(fi.Gid) ?? "" : "";
        TimeHint = settings.UtcTimes ? L.T("ГГГГ-ММ-ДД чч:мм:сс, UTC") : L.T("ГГГГ-ММ-ДД чч:мм:сс, местное время");

        CanEditXattrs = IsSingle;
        if (IsSingle && fi.XAttrs is not null)
            foreach ((string k, byte[] v) in fi.XAttrs) Xattrs.Add(new XattrRow(k, Fmt.XattrValue(v)));
        _initialXattrs = XattrSignature();

        AddXattrCommand = new RelayCommand(() =>
        {
            var row = new XattrRow("user.", "");
            Xattrs.Add(row);
            SelectedXattr = row;
        }, () => CanEditXattrs);
        // The row to remove comes as the command parameter (a button inside the row) or from the selection.
        RemoveXattrCommand = new RelayCommand(
            p => Xattrs.Remove(p as XattrRow ?? _selectedXattr!),
            p => CanEditXattrs && (p is XattrRow || _selectedXattr is not null));
    }

    public bool IsSingle { get; }
    public bool CanRename { get; }
    public string PathText { get; }
    public string TypeText { get; }
    public string SizeText { get; }
    public string LinksText { get; }
    public bool HasLinks => LinksText.Length > 0;
    public bool HasDirectories { get; }
    public bool AllSymlinks { get; }
    public bool CanEditMode => !AllSymlinks;
    public bool IsSymlink { get; }
    public bool IsDevice { get; }
    public string UserHint { get; }
    public string GroupHint { get; }
    public string TimeHint { get; }
    public bool CanEditXattrs { get; }
    public ObservableCollection<XattrRow> Xattrs { get; } = new();
    public RelayCommand AddXattrCommand { get; }
    public RelayCommand RemoveXattrCommand { get; }

    public XattrRow? SelectedXattr
    {
        get => _selectedXattr;
        set
        {
            if (SetProperty(ref _selectedXattr, value)) RemoveXattrCommand.RaiseCanExecuteChanged();
        }
    }

    public string Name { get => _name; set { if (SetProperty(ref _name, value)) Error = null; } }
    public string Uid { get => _uid; set { if (SetProperty(ref _uid, value)) Error = null; } }
    public string Gid { get => _gid; set { if (SetProperty(ref _gid, value)) Error = null; } }
    public string Time { get => _time; set { if (SetProperty(ref _time, value)) Error = null; } }
    public string Target { get => _target; set { if (SetProperty(ref _target, value)) Error = null; } }
    public string Major { get => _major; set { if (SetProperty(ref _major, value)) Error = null; } }
    public string Minor { get => _minor; set { if (SetProperty(ref _minor, value)) Error = null; } }

    /// <summary>Octal permissions ("0755"); empty means "leave as is" for a mixed selection.</summary>
    public string Octal
    {
        get => _octal;
        set
        {
            if (!SetProperty(ref _octal, value)) return;
            Error = null;
            if (_syncing) return;
            if (UnixMode.TryParseOctal(value.Trim(), out int mode))
            {
                _mode = mode;
                _modeKnown = true;
                RaiseBits();
            }
        }
    }

    /// <summary>"rwxr-xr-x" for the current value.</summary>
    public string Symbolic => _modeKnown ? UnixMode.Format(FsNodeType.File, _mode)[1..] : L.T("смешанные");

    private void RaiseBits()
    {
        foreach (string p in BitNames) OnPropertyChanged(p);
        OnPropertyChanged(nameof(Symbolic));
    }

    private static readonly string[] BitNames =
    {
        nameof(OwnerRead), nameof(OwnerWrite), nameof(OwnerExec), nameof(GroupRead), nameof(GroupWrite), nameof(GroupExec),
        nameof(OtherRead), nameof(OtherWrite), nameof(OtherExec), nameof(SetUid), nameof(SetGid), nameof(Sticky),
    };

    private bool GetBit(int bit) => _modeKnown && (_mode & bit) != 0;

    private void SetBit(int bit, bool on, string name)
    {
        if (GetBit(bit) == on && _modeKnown) return;
        _mode = on ? _mode | bit : _mode & ~bit;
        _modeKnown = true;
        _syncing = true;
        Octal = UnixMode.ToOctal(_mode);
        _syncing = false;
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(Symbolic));
    }

    public bool OwnerRead { get => GetBit(0x100); set => SetBit(0x100, value, nameof(OwnerRead)); }
    public bool OwnerWrite { get => GetBit(0x080); set => SetBit(0x080, value, nameof(OwnerWrite)); }
    public bool OwnerExec { get => GetBit(0x040); set => SetBit(0x040, value, nameof(OwnerExec)); }
    public bool GroupRead { get => GetBit(0x020); set => SetBit(0x020, value, nameof(GroupRead)); }
    public bool GroupWrite { get => GetBit(0x010); set => SetBit(0x010, value, nameof(GroupWrite)); }
    public bool GroupExec { get => GetBit(0x008); set => SetBit(0x008, value, nameof(GroupExec)); }
    public bool OtherRead { get => GetBit(0x004); set => SetBit(0x004, value, nameof(OtherRead)); }
    public bool OtherWrite { get => GetBit(0x002); set => SetBit(0x002, value, nameof(OtherWrite)); }
    public bool OtherExec { get => GetBit(0x001); set => SetBit(0x001, value, nameof(OtherExec)); }
    public bool SetUid { get => GetBit(0x800); set => SetBit(0x800, value, nameof(SetUid)); }
    public bool SetGid { get => GetBit(0x400); set => SetBit(0x400, value, nameof(SetGid)); }
    public bool Sticky { get => GetBit(0x200); set => SetBit(0x200, value, nameof(Sticky)); }

    /// <summary>Apply permissions, owner and time to everything inside the selected folders as well.</summary>
    public bool Recursive { get => _recursive; set => SetProperty(ref _recursive, value); }

    public IReadOnlyList<Choice<FsTree.Targets>> RecursiveChoices { get; } = new[]
    {
        new Choice<FsTree.Targets>(FsTree.Targets.All, L.T("права — ко всем вложенным объектам")),
        new Choice<FsTree.Targets>(FsTree.Targets.Files, L.T("права — только к файлам")),
        new Choice<FsTree.Targets>(FsTree.Targets.Directories, L.T("права — только к папкам")),
    };

    public FsTree.Targets RecursiveTargets { get => _recursiveTargets; set => SetProperty(ref _recursiveTargets, value); }

    /// <summary>Set after a successful <see cref="Accept"/> when anything was modified.</summary>
    public bool Changed { get; private set; }

    private string XattrSignature() => string.Join("\n", Xattrs.Select(x => x.Name + "=" + x.Value));

    public override bool Accept()
    {
        // ---- validate everything first, then apply: a half-applied dialog would be confusing.
        bool rename = CanRename && _name != _initialName;
        if (rename)
        {
            string? e = Validation.Name(_nodes[0].Parent!, _name, _nodes[0]);
            if (e is not null) return Fail(e);
        }

        int? mode = null;
        string octal = _octal.Trim();
        if (CanEditMode && octal != _initialOctal || (CanEditMode && _recursive && octal.Length > 0))
        {
            if (octal.Length == 0) return Fail(L.T("Укажите права доступа или верните прежнее значение."));
            if (!UnixMode.TryParseOctal(octal, out int m)) return Fail(L.T("Права задаются восьмеричным числом, например 0755."));
            mode = m;
        }

        uint? uid = null, gid = null;
        if (_uid.Trim() != _initialUid || (_recursive && _uid.Trim().Length > 0))
        {
            if (!uint.TryParse(_uid.Trim(), out uint u)) return Fail(L.T("UID — целое число от 0 до 4294967295."));
            uid = u;
        }
        if (_gid.Trim() != _initialGid || (_recursive && _gid.Trim().Length > 0))
        {
            if (!uint.TryParse(_gid.Trim(), out uint g)) return Fail(L.T("GID — целое число от 0 до 4294967295."));
            gid = g;
        }

        long? mtime = null;
        if (_time.Trim() != _initialTime)
        {
            if (!Fmt.TryParseDate(_time, _settings.UtcTimes, out long t)) return Fail(L.T("Время задаётся в виде ГГГГ-ММ-ДД чч:мм:сс."));
            mtime = t;
        }

        bool retarget = IsSymlink && _target != _initialTarget;
        if (retarget && string.IsNullOrEmpty(_target)) return Fail(L.T("Укажите, на что указывает ссылка."));

        uint major = 0, minor = 0;
        bool redevice = IsDevice && (_major.Trim() != _initialMajor || _minor.Trim() != _initialMinor);
        if (redevice && (!uint.TryParse(_major.Trim(), out major) || !uint.TryParse(_minor.Trim(), out minor)))
            return Fail(L.T("Старший и младший номера устройства — целые неотрицательные числа."));

        SortedDictionary<string, byte[]>? xattrs = null;
        bool rexattr = CanEditXattrs && XattrSignature() != _initialXattrs;
        if (rexattr)
        {
            xattrs = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (XattrRow row in Xattrs)
            {
                string n = row.Name.Trim();
                if (n.Length == 0 || !n.Contains('.') || n.StartsWith('.') || n.EndsWith('.') || n.Contains('\0'))
                    return Fail(L.F("Имя атрибута «{0}» должно начинаться с пространства имён: user., security., trusted. или system.", n));
                if (xattrs.ContainsKey(n)) return Fail(L.F("Атрибут «{0}» указан дважды.", n));
                if (!Fmt.TryParseXattrValue(row.Value, out byte[] v)) return Fail(L.F("Значение атрибута «{0}»: после 0x должны идти пары шестнадцатеричных цифр.", n));
                xattrs[n] = v;
            }
        }

        // ---- apply
        try
        {
            using (_tree.SuspendNotifications())
            {
                if (mode is not null) Changed |= _tree.SetMode(_nodes, mode.Value, _recursive, _recursive ? _recursiveTargets : FsTree.Targets.All) > 0;
                if (uid is not null || gid is not null) Changed |= _tree.ChangeOwner(_nodes, uid, gid, _recursive) > 0;
                if (mtime is not null) Changed |= _tree.SetMTime(_nodes, mtime.Value, _recursive) > 0;
                if (retarget)
                {
                    _tree.SetLinkTarget(_nodes[0], _target);
                    Changed = true;
                }
                if (redevice)
                {
                    _tree.SetDevice(_nodes[0], major, minor);
                    Changed = true;
                }
                if (rexattr)
                {
                    _nodes[0].Inode.XAttrs = xattrs!.Count > 0 ? xattrs : null;
                    _tree.NotifyChanged();
                    Changed = true;
                }
                if (rename)
                {
                    _tree.Rename(_nodes[0], _name);
                    Changed = true;
                }
            }
            return true;
        }
        catch (FsException ex)
        {
            return Fail(Validation.Translate(ex.Message));
        }
    }

    private bool Fail(string message)
    {
        Error = message;
        return false;
    }
}

/// <summary>
/// Asks which partition of a disk image to open when several hold an editable filesystem; likewise
/// for the volumes of a UBI image.
/// </summary>
public sealed class PartitionChoiceViewModel : DialogViewModel
{
    /// <summary>A partition of a disk image, or a volume of a UBI image (then <see cref="Partition"/> is null).</summary>
    public sealed record Row(PartitionInfo? Partition, string Name, string Content, string Details, bool IsEditable, UbiVolumeInfo? Volume = null,
        Core.Android.AndroidPart? AndroidPart = null, Core.Archives.ArchiveEntry? Entry = null, Core.Firmware.FirmwarePart? FirmwarePart = null);

    private Row? _selected;

    public PartitionChoiceViewModel(string fileName, PartitionTable table)
    {
        Title = L.T("Выбор раздела");
        AcceptText = L.T("Открыть");
        Summary = L.F("«{0}» — образ диска с таблицей разделов {1}. ", fileName, Fmt.PartitionScheme(table)) +
                  L.T("Файловые системы, которые можно редактировать, есть в нескольких разделах. Какой из них открыть?");
        foreach (PartitionInfo p in table.Partitions)
        {
            string details = L.F("{0} · {1} · смещение 0x{2:X}", Fmt.Size(p.Length), p.TypeName, p.Offset) + (p.Bootable ? L.T(" · загрузочный") : "");
            Rows.Add(new Row(p, L.F("Раздел {0}", p.Number), Fmt.PartitionContent(p), details, p.IsEditable));
        }
        PartitionInfo? preferred = table.PickDefault();
        _selected = Rows.FirstOrDefault(r => ReferenceEquals(r.Partition, preferred));
    }

    public PartitionChoiceViewModel(string fileName, UbiImage ubi)
    {
        _volumes = true;
        Title = L.T("Выбор тома");
        AcceptText = L.T("Открыть");
        Summary = L.F("«{0}» — образ UBI (блоки стирания по {1}). ", fileName, Fmt.Size(ubi.PebSize)) +
                  L.T("Файловые системы, которые можно открыть, есть в нескольких томах. Какой из них открыть?");
        foreach (UbiVolumeInfo v in ubi.Volumes)
        {
            string details = L.F("{0} · {1}", Fmt.Size(v.Length), v.IsStatic ? L.T("статический") : L.T("динамический")) +
                             (v.AutoResize ? L.T(" · занимает всё свободное место (autoresize)") : "");
            Rows.Add(new Row(null, L.F("Том {0}", Fmt.VolumeName(v)), Fmt.VolumeContent(v), details, v.IsReadable, v));
        }
        UbiVolumeInfo? preferred = ubi.PickDefault();
        _selected = Rows.FirstOrDefault(r => ReferenceEquals(r.Volume, preferred));
    }

    public PartitionChoiceViewModel(string fileName, Core.Android.AndroidImage image)
    {
        _android = true;
        bool super = image.Kind == Core.Android.AndroidImageKind.Super;
        Title = super ? L.T("Выбор раздела") : L.T("Выбор ramdisk");
        AcceptText = L.T("Открыть");
        Summary = L.F("«{0}» — {1}. ", fileName, Fmt.AndroidKind(image)) +
                  (super ? L.T("Файловые системы есть в нескольких логических разделах. Какой из них открыть?")
                         : L.T("В образе несколько ramdisk. Какой из них открыть?"));
        foreach (Core.Android.AndroidPart p in image.Parts)
            Rows.Add(new Row(null, p.Name, Fmt.AndroidPartContent(p), Fmt.Size(p.Length) + (p.Detail.Length > 0 ? " · " + p.Detail : ""), p.IsReadable,
                AndroidPart: p));
        Core.Android.AndroidPart? preferred = image.PickDefault();
        _selected = Rows.FirstOrDefault(r => ReferenceEquals(r.AndroidPart, preferred));
    }

    /// <summary>Lists the parts of a firmware file (U-Boot uImage): kernel, ramdisk, root filesystem.</summary>
    public PartitionChoiceViewModel(string fileName, Core.Firmware.FirmwareImage image)
    {
        _firmware = true;
        Title = L.T("Выбор части прошивки");
        AcceptText = L.T("Открыть");
        Summary = L.F("«{0}» — {1}. ", fileName, Fmt.FirmwareKind(image)) +
                  L.T("Файловые системы или архивы есть в нескольких частях прошивки. Какую из них открыть?");
        foreach (Core.Firmware.FirmwarePart p in image.Parts)
            Rows.Add(new Row(null, p.Name, Fmt.FirmwarePartContent(p), L.F("{0} · смещение 0x{1:X}", Fmt.Size(p.Length), p.Offset), p.IsReadable,
                FirmwarePart: p));
        Core.Firmware.FirmwarePart? preferred = image.PickDefault();
        _selected = Rows.FirstOrDefault(r => ReferenceEquals(r.FirmwarePart, preferred));
    }

    /// <summary>Lists the files of a zip or 7z archive.</summary>
    public PartitionChoiceViewModel(string fileName, Core.Archives.ImageArchive archive)
    {
        _entries = true;
        Title = L.T("Выбор файла в архиве");
        AcceptText = L.T("Открыть");
        Summary = L.F("«{0}» — архив {1} с несколькими файлами. ", fileName, Core.Tar.ArchiveCodec.DisplayName(archive.Kind)) +
                  L.T("Какой из них открыть? Остальные файлы при сохранении в тот же архив переносятся без изменений.");
        foreach (Core.Archives.ArchiveEntry e in archive.Entries.Where(e => !e.IsDirectory))
        {
            string details = Fmt.Size(e.Size) + (e.CompressedSize is { } c && e.Size > 0 ? L.F(" · сжат до {0}", Fmt.Size(c)) : "");
            Rows.Add(new Row(null, e.Name, Core.Archives.ImageArchive.LooksLikeImage(e.Name) ? L.T("образ") : L.T("файл"), details, e.Size > 0, Entry: e));
        }
        Core.Archives.ArchiveEntry? preferred = archive.DefaultImage();
        _selected = Rows.FirstOrDefault(r => ReferenceEquals(r.Entry, preferred));
    }

    private readonly bool _volumes, _android, _entries, _firmware;

    /// <summary>The part of a firmware file to open; set by <see cref="Accept"/> when the dialog lists them.</summary>
    public Core.Firmware.FirmwarePart? FirmwareResult { get; private set; }

    /// <summary>The file of an archive to open; set by <see cref="Accept"/> when the dialog lists them.</summary>
    public Core.Archives.ArchiveEntry? EntryResult { get; private set; }

    /// <summary>The part of an Android image to open; set by <see cref="Accept"/> when the dialog lists them.</summary>
    public Core.Android.AndroidPart? AndroidResult { get; private set; }

    public string Summary { get; }
    public ObservableCollection<Row> Rows { get; } = new();

    public Row? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value)) Error = null;
        }
    }

    /// <summary>The partition to open; set by <see cref="Accept"/>.</summary>
    public PartitionInfo? Result { get; private set; }

    /// <summary>The UBI volume to open; set by <see cref="Accept"/> when the dialog lists volumes.</summary>
    public UbiVolumeInfo? VolumeResult { get; private set; }

    public override bool Accept()
    {
        if (_selected is null)
        {
            Error = _volumes ? L.T("Выберите том.") : _android || _entries || _firmware ? L.T("Выберите, что открыть.") : L.T("Выберите раздел.");
            return false;
        }
        if (!_selected.IsEditable)
        {
            Error = _entries
                ? L.T("Этот файл пуст — открыть его нельзя.")
                : _android || _firmware
                ? L.T("Здесь нет файловой системы или архива cpio, которые можно открыть.")
                : _volumes
                ? L.T("В этом томе нет UBIFS, SquashFS или EROFS — его нельзя открыть.")
                : L.T("В этом разделе нет SquashFS, EROFS, ext2/3/4 или FAT — его нельзя открыть для редактирования.");
            return false;
        }
        Result = _selected.Partition;
        VolumeResult = _selected.Volume;
        AndroidResult = _selected.AndroidPart;
        EntryResult = _selected.Entry;
        FirmwareResult = _selected.FirmwarePart;
        return true;
    }
}

/// <summary>Read-only facts about the opened image.</summary>
public sealed class ImageInfoViewModel : DialogViewModel
{
    public sealed record Row(string Label, string Value);

    public ImageInfoViewModel(ImageDocument doc, AppSettings settings)
    {
        Title = L.T("Сведения об образе");
        AcceptText = L.T("Закрыть");
        void Add(string label, string value) => Rows.Add(new Row(label, value));

        Add(L.T("Файл"), doc.FilePath ?? L.T("(новый образ, ещё не сохранён)"));
        Add(L.T("Формат"), doc.FormatName);
        if (doc.FilePath is not null) Add(L.T("Размер файла"), Fmt.SizeWithBytes(doc.FileSize));
        if (doc.SquashFs is { } s)
        {
            Add(L.T("Версия"), $"{s.VersionMajor}.{s.VersionMinor}");
            Add(L.T("Сжатие"), s.Compression.ToString().ToLowerInvariant() + (s.XzFilters.Count > 0 ? " + BCJ " + string.Join(", ", s.XzFilters) : ""));
            Add(L.T("Размер блока"), Fmt.Size(s.BlockSize));
            Add(L.T("Инодов"), Fmt.Bytes(s.InodeCount));
            Add(L.T("Фрагментов"), s.NoFragments ? L.T("не используются") : Fmt.Bytes(s.FragmentCount));
            Add(L.T("Особенности"), string.Join(", ", new[]
            {
                s.Exportable ? L.T("экспортируемая (NFS)") : null, s.Deduplicated ? L.T("дедупликация") : null, s.HasXattrs ? L.T("расширенные атрибуты") : null,
            }.Where(x => x is not null)));
            if (s.MkfsTime > 0) Add(L.T("Создан"), Fmt.Date(s.MkfsTime, settings.UtcTimes));
        }
        if (doc.Ext is { } e)
        {
            Add(L.T("Размер ФС"), L.F("{0} ({1} блоков по {2} байт)", Fmt.Size(e.SizeBytes), Fmt.Bytes(e.BlocksCount), e.BlockSize));
            Add(L.T("Свободно"), L.F("{0} ({1} блоков)", Fmt.Size(e.FreeBlocks * e.BlockSize), Fmt.Bytes(e.FreeBlocks)));
            Add(L.T("Иноды"), L.F("{0} по {1} байт, свободно {2}", Fmt.Bytes(e.InodesCount), e.InodeSize, Fmt.Bytes(e.FreeInodes)));
            Add(L.T("Журнал"), e.HasJournal ? Fmt.Size(e.JournalBytes) : L.T("нет"));
            Add(L.T("Метка"), e.Label.Length > 0 ? e.Label : L.T("(нет)"));
            Add("UUID", e.Uuid.ToString());
            Add(L.T("Возможности"), string.Join(" ", e.FeatureNames));
            Add(L.T("Состояние"), e.NeedsRecovery ? L.T("журнал не воспроизведён (требуется восстановление)")
                : e.JournalReplayed && e.ReplayedTransactions > 0
                    ? L.F("не была корректно размонтирована; журнал воспроизведён при чтении (транзакций: {0}, блоков: {1}), файл не изменён", e.ReplayedTransactions, Fmt.Bytes(e.ReplayedBlocks))
                : e.JournalReplayed ? L.T("не была корректно размонтирована; журнал пуст")
                : e.Clean ? L.T("чистая") : L.T("не была корректно размонтирована"));
        }
        if (doc.Ubifs is { } u)
        {
            Add(L.T("Версия формата"), u.FormatVersion.ToString());
            Add(L.T("Размер ФС"), L.F("{0} ({1} блоков стирания по {2} байт), может вырасти до {3}", Fmt.Size(u.SizeBytes), Fmt.Bytes(u.LebCount), Fmt.Bytes(u.LebSize), Fmt.Size((long)u.MaxLebCount * u.LebSize)));
            Add(L.T("Занято / свободно"), L.F("{0} / {1} (на момент последней фиксации)", Fmt.Size(u.UsedBytes), Fmt.Size(u.FreeBytes)));
            Add(L.T("Мин. единица ввода-вывода"), L.F("{0} байт", Fmt.Bytes(u.MinIoSize)));
            Add(L.T("Сжатие"), u.DefaultCompression.ToString().ToLowerInvariant() + L.T(" (по умолчанию; у каждого блока данных своё)"));
            Add("UUID", u.Uuid.ToString());
            Add(L.T("Особенности"), string.Join(", ", new[]
            {
                L.F("хеш имён {0}", u.KeyHash), L.F("ветвление индекса {0}", u.Fanout), u.BigLpt ? L.T("большая таблица LPT") : null,
                u.SpaceFixup ? L.T("свободное место исправляется при первом монтировании") : null, u.Authenticated ? L.T("подписана (подписи не проверяются)") : null,
            }.Where(x => x is not null)));
            Add(L.T("Состояние"), u.ReplayedNodes > 0
                ? L.F("не была корректно размонтирована; журнал воспроизведён при чтении (узлов: {0}, блоков стирания: {1}), файл не изменён", Fmt.Bytes(u.ReplayedNodes), u.ReplayedBuds)
                : u.Dirty ? L.T("не была корректно размонтирована; журнал пуст") : L.T("чистая"));
        }
        if (doc.Erofs is { } er)
        {
            Add(L.T("Размер ФС"), L.F("{0} ({1} блоков по {2} байт)", Fmt.Size(er.SizeBytes), Fmt.Bytes(er.Blocks), Fmt.Bytes(er.BlockSize)));
            Add(L.T("Сжатие"), er.Algorithms.Count == 0 ? L.T("нет")
                : string.Join(", ", er.Algorithms.Select(a => a.ToString().ToLowerInvariant())) +
                  (er.Compression != Core.Erofs.ErofsCompression.None ? L.F(", кластеры до {0}", Fmt.Size(er.PclusterSize)) : "") +
                  (er.LzmaDictionarySize > 0 ? L.F(", словарь LZMA {0}", Fmt.Size(er.LzmaDictionarySize)) : ""));
            Add(L.T("Инодов"), Fmt.Bytes(er.InodeCount));
            Add(L.T("Метка"), er.VolumeName.Length > 0 ? er.VolumeName : L.T("(нет)"));
            Add("UUID", er.Uuid.ToString());
            Add(L.T("Время образа"), Fmt.Date(er.BuildTime, settings.UtcTimes));
            Add(L.T("Возможности"), string.Join(" ", er.FeatureNames.DefaultIfEmpty(L.T("нет"))));
        }
        if (doc.Fat is { } fat)
        {
            Add(L.T("Тип"), fat.TypeName);
            Add(L.T("Размер ФС"), L.F("{0} ({1} кластеров по {2})", Fmt.Size(fat.SizeBytes), Fmt.Bytes(fat.Clusters), Fmt.Size(fat.ClusterSize)));
            Add(L.T("Свободно"), L.F("{0} ({1} кластеров)", Fmt.Size(fat.FreeClusters * fat.ClusterSize), Fmt.Bytes(fat.FreeClusters)));
            Add(L.T("Метка"), fat.Label.Length > 0 ? fat.Label : L.T("(нет)"));
            Add(L.T("Серийный номер"), $"{fat.VolumeId >> 16:X4}-{fat.VolumeId & 0xFFFF:X4}");
            Add(L.T("Устройство"), L.F("сектор {0} байт, копий FAT: {1}, скрытых секторов: {2}, OEM «{3}»", fat.SectorSize, fat.Fats, Fmt.Bytes(fat.HiddenSectors), fat.OemName));
            Add(L.T("Состояние"), fat.Dirty ? L.T("не была корректно размонтирована") : L.T("чистая"));
        }
        if (doc.Jffs2 is { } j)
        {
            Add(L.T("Порядок байтов"), j.BigEndian ? "big-endian" : "little-endian");
            Add(L.T("Блок стирания"), j.EraseBlockSizeKnown ? Fmt.Size(j.EraseBlockSize)
                : L.F("{0} (определён по расположению узлов)", Fmt.Size(j.EraseBlockSize)));
            Add(L.T("Маркеры очистки"), j.Cleanmarkers ? L.F("есть, по {0} байт", j.CleanmarkerSize) : L.T("нет (образ для NAND или без них)"));
            Add(L.T("Сжатие"), j.CompressionCounts.Count == 0 ? L.T("нет") : string.Join(", ", j.CompressionCounts.OrderByDescending(p => p.Value)
                .Select(p => $"{p.Key.ToString().ToLowerInvariant()}: {Fmt.Bytes(p.Value)}")));
            Add(L.T("Узлов"), L.F("{0} действующих, {1} устаревших, {2} повреждённых", Fmt.Bytes(j.Nodes), Fmt.Bytes(j.ObsoleteNodes), Fmt.Bytes(j.DamagedNodes)));
            Add(L.T("Занято"), L.F("{0} из {1}", Fmt.Size(j.UsedBytes), Fmt.Size(j.ImageBytes)));
            Add(L.T("Особенности"), string.Join(", ", new[]
            {
                j.HasSummary ? L.T("сводки блоков (summary)") : null, j.HasXattrs ? L.T("расширенные атрибуты") : null,
            }.Where(x => x is not null).DefaultIfEmpty(L.T("нет"))));
        }
        if (doc.F2fs is { } f2)
        {
            Add(L.T("Версия формата"), f2.Version);
            Add(L.T("Блоки"), L.F("{0} по {1}", Fmt.Bytes(f2.BlockCount), Fmt.Size(f2.BlockSize)));
            Add(L.T("Использовано"), L.F("{0} из {1}", Fmt.Size(f2.ValidBlocks * f2.BlockSize), Fmt.Size(f2.UserBlocks * f2.BlockSize)));
            Add(L.T("Имя тома"), f2.VolumeName.Length > 0 ? f2.VolumeName : L.T("(нет)"));
            Add("UUID", f2.Uuid.ToString());
            Add(L.T("Возможности"), f2.FeatureNames.Count > 0 ? string.Join(", ", f2.FeatureNames) : L.T("нет"));
            if (f2.CompressedFiles > 0) Add(L.T("Сжатых файлов"), Fmt.Bytes(f2.CompressedFiles));
            if (f2.EncryptedFiles > 0) Add(L.T("Зашифрованных файлов"), L.F("{0} — их содержимое не читается", Fmt.Bytes(f2.EncryptedFiles)));
            Add(L.T("Состояние"), f2.NotCleanlyUnmounted ? L.T("не отмонтирована штатно") : L.T("отмонтирована штатно"));
        }
        if (doc.Cramfs is { } cr)
        {
            Add(L.T("Порядок байтов"), cr.BigEndian ? "big-endian" : "little-endian");
            Add(L.T("Размер блока"), Fmt.Size(cr.BlockSize));
            Add(L.T("Имя тома"), cr.VolumeName.Length > 0 ? cr.VolumeName : L.T("(нет)"));
            if (cr.Files > 0) Add(L.T("Объектов"), Fmt.Bytes(cr.Files));
            if (cr.Edition != 0) Add(L.T("Издание"), cr.Edition.ToString());
            Add(L.T("Контрольная сумма"), cr.CrcOk switch { true => L.T("верна"), false => L.T("НЕ СОВПАДАЕТ — образ повреждён"), _ => L.T("нет (старый формат)") });
            Add(L.T("Возможности"), string.Join(", ", new[]
            {
                cr.Holes ? L.T("«дыры»") : null, cr.Padded ? L.T("512 байт для загрузчика") : null,
                cr.ExtBlockPointers ? L.T("несжатые блоки и прямые указатели") : null,
            }.Where(x => x is not null).DefaultIfEmpty(L.T("нет"))));
        }
        if (doc.Romfs is { } ro)
        {
            Add(L.T("Имя тома"), ro.VolumeName.Length > 0 ? ro.VolumeName : L.T("(нет)"));
            Add(L.T("Выравнивание данных"), Fmt.Size(ro.DataAlignment));
            Add(L.T("Контрольная сумма"), ro.ChecksumOk ? L.T("верна") : L.T("НЕ СОВПАДАЕТ — ядро не смонтирует образ"));
        }
        if (doc.Cpio is { } cp)
        {
            Add(L.T("Сжатие"), cp.Compression == Core.Tar.ArchiveCompression.None ? L.T("нет") : cp.Compression.ToString().ToLowerInvariant());
            Add(L.T("Записей"), Fmt.Bytes(cp.Entries));
            Add(L.T("Заголовки"), cp.Header.ToString().ToLowerInvariant());
            Add(L.T("Префикс путей"), cp.PathPrefix.Length > 0 ? cp.PathPrefix : L.T("(нет)"));
            if (cp.Parts > 1) Add(L.T("Архивов подряд"), cp.Parts.ToString());
            if (cp.Truncated) Add(L.T("Состояние"), L.T("нет завершающей записи — архив, возможно, обрезан"));
        }
        if (doc.Tar is { } t)
        {
            Add(L.T("Сжатие"), t.Compression == Core.Tar.ArchiveCompression.None ? L.T("нет") : t.Compression.ToString().ToLowerInvariant());
            Add(L.T("Записей"), Fmt.Bytes(t.Entries));
            Add(L.T("Расширения"), string.Join(", ", new[] { t.HasPaxHeaders ? "pax" : null, t.HasGnuExtensions ? "GNU" : null }.Where(x => x is not null).DefaultIfEmpty(L.T("нет"))));
            Add(L.T("Префикс путей"), t.PathPrefix.Length > 0 ? t.PathPrefix : L.T("(нет)"));
        }
        if (doc.Container is { } disk)
        {
            Add(L.T("Образ диска"), L.F("{0}, таблица разделов {1}", Fmt.SizeWithBytes(disk.DiskLength), Fmt.PartitionScheme(disk.Table)));
            foreach (PartitionInfo p in disk.Table.Partitions)
            {
                string text = L.F("{0}, {1}, {2}, смещение 0x{3:X}", Fmt.PartitionContent(p), Fmt.Size(p.Length), p.TypeName, p.Offset);
                if (p.StoredLength < p.Length) text += L.F(", в файле {0}", Fmt.Size(p.StoredLength));
                Add(ReferenceEquals(p, disk.Partition) ? L.F("▸ Раздел {0} (открыт)", p.Number) : L.F("Раздел {0}", p.Number), text);
            }
            Add(L.T("Сохранение"), L.T("образ диска целиком: заменяется только открытый раздел, таблица разделов и остальные разделы не меняются"));
        }
        else if (doc.Ubi is { } ubi)
        {
            Add(L.T("Образ UBI"), L.F("{0} блоков стирания по {1}, логический блок {2} байт", Fmt.Bytes(ubi.Image.PebCount), Fmt.Size(ubi.Image.PebSize), Fmt.Bytes(ubi.Image.LebSize)));
            foreach (UbiVolumeInfo v in ubi.Image.Volumes)
            {
                string text = L.F("{0}, {1}, {2}", Fmt.VolumeContent(v), Fmt.Size(v.Length), v.IsStatic ? L.T("статический") : L.T("динамический"));
                if (v.AutoResize) text += L.T(" · занимает всё свободное место (autoresize)");
                Add(ReferenceEquals(v, ubi.Volume) ? L.F("▸ Том {0} (открыт)", Fmt.VolumeName(v)) : L.F("Том {0}", Fmt.VolumeName(v)), text);
            }
            if (doc.EmbeddedOffset > 0) Add(L.T("Смещение образа UBI в файле"), $"0x{doc.EmbeddedOffset:X}");
            Add(L.T("Сохранение"), L.T("образ UBI целиком: заменяется только открытый том, остальные тома переносятся без изменений"));
        }
        else if (doc.Android is { } android)
        {
            Add(L.T("Образ Android"), Fmt.AndroidKind(android.Image));
            if (android.Image is Core.Android.SuperImage sup)
            {
                Add(L.T("Устройство super"), L.F("«{0}», {1}, свободно {2}; метаданные по {3}, слотов: {4}{5}", sup.BlockDevices[0].Name, Fmt.Size(sup.DeviceSize), Fmt.Size(sup.FreeBytes),
                    Fmt.Size(sup.MetadataMaxSize), sup.SlotCount, sup.Sparse ? L.T(", файл sparse") : ""));
                foreach (Core.Android.SuperImage.LpGroup g in sup.Groups)
                    if (g.MaximumSize > 0) Add(L.F("Группа {0}", g.Name), L.F("не больше {0}", Fmt.Size((long)g.MaximumSize)));
            }
            if (android.Image is Core.Android.BootImage boot)
            {
                if (boot.Cmdline.Length > 0) Add(L.T("Командная строка ядра"), boot.Cmdline);
                Add(L.T("Подпись AVB"), boot.HasAvbFooter ? L.T("есть (после изменения перестанет совпадать)") : L.T("нет"));
            }
            foreach (Core.Android.AndroidPart p in android.Image.Parts)
            {
                string text = L.F("{0}, {1}", Fmt.AndroidPartContent(p), Fmt.Size(p.Length)) + (p.Detail.Length > 0 ? " · " + p.Detail : "");
                Add(ReferenceEquals(p, android.Part) ? L.F("▸ {0} (открыт)", p.Name) : p.Name, text);
            }
            Add(L.T("Сохранение"), android.Image.Kind == Core.Android.AndroidImageKind.Super
                ? L.T("super.img целиком: заменяется только открытый раздел, остальные остаются на своих местах")
                : L.T("загрузочный образ целиком: заменяется только открытый ramdisk"));
        }
        else if (doc.Firmware is { } fw)
        {
            Add(L.T("Прошивка"), Fmt.FirmwareKind(fw.Image));
            if (fw.Image is Core.Firmware.UImage ui)
            {
                Add(L.T("Заголовок uImage"), L.F("{0} {1}, тип {2}, сжатие {3}, адрес загрузки 0x{4:X8}, точка входа 0x{5:X8}",
                    ui.OsName, ui.ArchitectureName, ui.TypeName, ui.CompressionName, ui.LoadAddress, ui.EntryPoint));
                Add(L.T("Контрольная сумма данных"), ui.DataCrcMatches ? L.T("совпадает") : L.T("не совпадает (образ изменён или повреждён)"));
                if (ui.FwtoolChecksumsValid is { } fwOk)
                    Add(L.T("Метаданные OpenWrt (fwtool)"), (fwOk ? L.T("контрольные суммы верны, при сохранении пересчитываются") : L.T("контрольные суммы неверны, переносятся без изменений")) +
                                                            (ui.FwtoolSigned ? L.T("; есть подпись — после изменения она не совпадёт") : ""));
            }
            if (fw.Image is Core.Firmware.TrxImage trx)
            {
                Add(L.T("Заголовок TRX"), L.F("версия {0}, флаги 0x{1:X4}, длина {2}", trx.Version, trx.Flags, Fmt.SizeWithBytes(trx.Length)));
                Add(L.T("Контрольная сумма данных"), trx.CrcMatches ? L.T("совпадает") : L.T("не совпадает (образ изменён или повреждён)"));
                if (trx.ChkBoard is not null)
                {
                    Add(L.T("Плата (Netgear CHK)"), trx.ChkBoard.Length > 0 ? trx.ChkBoard : L.T("не указана"));
                    Add(L.T("Контрольные суммы CHK"), trx.ChkChecksumsValid == true ? L.T("совпадают, при сохранении пересчитываются") : L.T("не совпадают с расчётом программы — образ только читается"));
                }
            }
            if (fw.Image is Core.Firmware.FitImage fit)
            {
                if (fit.DefaultConfiguration.Length > 0)
                    Add(L.T("Конфигурация по умолчанию"), fit.DefaultConfiguration + (fit.DefaultConfigurationText.Length > 0 ? $" ({fit.DefaultConfigurationText})" : ""));
                Add(L.T("Хеши образов"), fit.HashesChecked == 0 ? L.T("нет") : fit.HashesWrong == 0
                    ? L.F("проверено {0}, все совпадают", fit.HashesChecked)
                    : L.F("проверено {0}, не совпадают {1} (образ изменён или повреждён)", fit.HashesChecked, fit.HashesWrong));
                Add(L.T("Данные образов"), fit.ExternalData ? L.T("вне дерева устройств (mkimage -E)") : L.T("внутри дерева устройств"));
                if (fit.Signed) Add(L.T("Подпись"), L.T("есть (проверенная загрузка U-Boot)"));
            }
            foreach (Core.Firmware.FirmwarePart p in fw.Image.Parts)
            {
                string text = L.F("{0}, {1}, смещение 0x{2:X}", Fmt.FirmwarePartContent(p), Fmt.Size(p.Length), p.Offset);
                Add(ReferenceEquals(p, fw.Part) ? L.F("▸ {0} (открыт)", p.Name) : p.Name, text);
            }
            Add(L.T("Сохранение"), doc.SavesFilesystemOnly
                ? L.T("только сама файловая система, в новый файл: эту прошивку пересобрать нельзя, файл прошивки не меняется")
                : fw.Part.BehindImage
                ? L.F("прошивка целиком: заменяется только корневая файловая система, образ {0} с ядром не меняется", fw.Image.ShortKind)
                : fw.Image is Core.Firmware.FitImage
                ? L.T("прошивка целиком: заменяется только открытый образ, его хеши и размер в дереве FIT пересчитываются")
                : fw.Image is Core.Firmware.TrxImage
                ? L.T("прошивка целиком: заменяется только открытая часть, длина, смещения и CRC-32 в заголовке TRX (и суммы CHK) пересчитываются")
                : L.T("прошивка целиком: заменяется только открытая часть, размер и CRC-32 в заголовке uImage пересчитываются"));
        }
        else if (doc.EmbeddedOffset > 0) Add(L.T("Смещение ФС в файле"), $"0x{doc.EmbeddedOffset:X}");
        if (doc.AndroidSparse) Add(L.T("Контейнер"), "Android sparse image");
        if (doc.Archive is { } arc)
        {
            Add(L.T("Архив"), L.F("{0} ({1}); образ «{2}» распакован во временный файл, при сохранении под тем же именем упаковывается снова",
                Core.Tar.ArchiveCodec.DisplayName(arc.Kind), arc.Methods, arc.EntryName));
            if (arc.OtherFiles.Count > 0)
                Add(L.T("Другие файлы архива"), string.Join(", ", arc.OtherFiles.Take(20)) + (arc.OtherFiles.Count > 20 ? L.F(" и ещё {0}", arc.OtherFiles.Count - 20) : "") +
                                                 L.T(" — переносятся при сохранении"));
        }
        else if (doc.OuterCompression is { } outer)
            Add(L.T("Файл сжат"), Core.Tar.ArchiveCodec.DisplayName(outer) + L.T(" (распакован во временный файл; при сохранении под тем же именем сжимается снова)"));

        TreeStats st = doc.Tree.GetStats();
        Add(L.T("Содержимое"), $"{Fmt.Plural(st.Files, "файл", "файла", "файлов")}, {Fmt.Plural(st.Directories, "папка", "папки", "папок")}, " +
                          $"{Fmt.Plural(st.Symlinks, "ссылка", "ссылки", "ссылок")}, {Fmt.Plural(st.Specials, "специальный файл", "специальных файла", "специальных файлов")}");
        Add(L.T("Объём данных"), Fmt.SizeWithBytes(st.DataBytes));
        foreach (string w in doc.Warnings) Warnings.Add(w);
    }

    public ObservableCollection<Row> Rows { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();
    public bool HasWarnings => Warnings.Count > 0;

    public override bool Accept() => true;
}

/// <summary>A list of messages shown after an operation (warnings of a reader or writer).</summary>
public sealed class MessagesViewModel : DialogViewModel
{
    public MessagesViewModel(string title, string summary, IEnumerable<string> messages)
    {
        Title = title;
        Summary = summary;
        AcceptText = L.T("Закрыть");
        foreach (string m in messages) Messages.Add(m);
    }

    public string Summary { get; }
    public ObservableCollection<string> Messages { get; } = new();

    public string AllText => string.Join(Environment.NewLine, Messages);

    public override bool Accept() => true;
}
