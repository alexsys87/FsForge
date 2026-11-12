using System.Collections.ObjectModel;
using FsForge.Core;
using FsForge.Core.Images;
using FsForge.Presentation.Mvvm;
using FsForge.Presentation.Services;
using FsForge.Presentation.Settings;

namespace FsForge.Presentation.ViewModels;

/// <summary>Base of the modal dialogs: a title, an error line and an "accept" check.</summary>
public abstract class DialogViewModel : ObservableObject
{
    private string? _error;

    public string Title { get; protected set; } = "";
    public string AcceptText { get; protected set; } = "ОК";

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

    public PromptViewModel(string title, string label, string text, Func<string, string?> validate, string acceptText = "ОК", string? hint = null)
    {
        Title = title;
        Label = label;
        AcceptText = acceptText;
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
        Title = "Создать в " + parent.Path;
        AcceptText = "Создать";
        _name = kind switch
        {
            NewItemKind.Folder => FsTree.MakeUniqueName(parent, "Новая папка"),
            NewItemKind.File => FsTree.MakeUniqueName(parent, "Новый файл"),
            _ => "",
        };
    }

    private AppSettings Settings { get; }

    public IReadOnlyList<Choice<NewItemKind>> Kinds { get; } = new[]
    {
        new Choice<NewItemKind>(NewItemKind.Folder, "Папка"),
        new Choice<NewItemKind>(NewItemKind.File, "Пустой файл"),
        new Choice<NewItemKind>(NewItemKind.Symlink, "Символическая ссылка"),
        new Choice<NewItemKind>(NewItemKind.CharDevice, "Символьное устройство"),
        new Choice<NewItemKind>(NewItemKind.BlockDevice, "Блочное устройство"),
        new Choice<NewItemKind>(NewItemKind.Fifo, "Именованный канал (FIFO)"),
        new Choice<NewItemKind>(NewItemKind.Socket, "Сокет"),
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
            Error = "Права задаются восьмеричным числом, например 0755.";
            return false;
        }
        uint major = 0, minor = 0;
        if (IsDevice && (!uint.TryParse(_major.Trim(), out major) || !uint.TryParse(_minor.Trim(), out minor)))
        {
            Error = "Старший и младший номера устройства — целые неотрицательные числа.";
            return false;
        }
        if (IsSymlink && string.IsNullOrEmpty(_target))
        {
            Error = "Укажите, на что указывает ссылка.";
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
        if (string.IsNullOrEmpty(name)) return "Введите имя.";
        if (name is "." or "..") return "Имена «.» и «..» зарезервированы.";
        if (name.Contains('/')) return "Имя не может содержать символ «/».";
        if (name.Contains('\0')) return "Имя не может содержать нулевой символ.";
        if (PosixName.Encode(name).Length > 255) return "Имя длиннее 255 байт.";
        FsNode? existing = parent.Find(name);
        if (existing is not null && !ReferenceEquals(existing, self)) return "Объект с таким именем уже существует в этой папке.";
        return null;
    }

    /// <summary>Russian wording for the most common messages of the core.</summary>
    public static string Translate(string message)
    {
        if (message.Contains("already exists")) return "Объект с таким именем уже существует.";
        if (message.Contains("into itself")) return "Нельзя переместить папку внутрь самой себя.";
        if (message.Contains("not a directory")) return "Целевой объект не является папкой.";
        if (message.Contains("longer than 4095")) return "Цель ссылки длиннее 4095 байт.";
        if (message.Contains("namespace prefix")) return "Имя атрибута должно начинаться с пространства имён: user., security., trusted. или system.";
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
        Title = IsSingle ? "Свойства: " + (first.IsRoot ? "/" : first.Name) : $"Свойства: {Fmt.Plural(nodes.Count, "объект", "объекта", "объектов")}";
        AcceptText = "Применить";

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
            LinksText = first.IsDirectory ? "" : fi.LinkCount > 1 ? $"{fi.LinkCount} (жёсткие ссылки на один файл)" : "1";
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
            TypeText = "Несколько объектов";
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
        TimeHint = settings.UtcTimes ? "ГГГГ-ММ-ДД чч:мм:сс, UTC" : "ГГГГ-ММ-ДД чч:мм:сс, местное время";

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
    public string Symbolic => _modeKnown ? UnixMode.Format(FsNodeType.File, _mode)[1..] : "смешанные";

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
        new Choice<FsTree.Targets>(FsTree.Targets.All, "права — ко всем вложенным объектам"),
        new Choice<FsTree.Targets>(FsTree.Targets.Files, "права — только к файлам"),
        new Choice<FsTree.Targets>(FsTree.Targets.Directories, "права — только к папкам"),
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
            if (octal.Length == 0) return Fail("Укажите права доступа или верните прежнее значение.");
            if (!UnixMode.TryParseOctal(octal, out int m)) return Fail("Права задаются восьмеричным числом, например 0755.");
            mode = m;
        }

        uint? uid = null, gid = null;
        if (_uid.Trim() != _initialUid || (_recursive && _uid.Trim().Length > 0))
        {
            if (!uint.TryParse(_uid.Trim(), out uint u)) return Fail("UID — целое число от 0 до 4294967295.");
            uid = u;
        }
        if (_gid.Trim() != _initialGid || (_recursive && _gid.Trim().Length > 0))
        {
            if (!uint.TryParse(_gid.Trim(), out uint g)) return Fail("GID — целое число от 0 до 4294967295.");
            gid = g;
        }

        long? mtime = null;
        if (_time.Trim() != _initialTime)
        {
            if (!Fmt.TryParseDate(_time, _settings.UtcTimes, out long t)) return Fail("Время задаётся в виде ГГГГ-ММ-ДД чч:мм:сс.");
            mtime = t;
        }

        bool retarget = IsSymlink && _target != _initialTarget;
        if (retarget && string.IsNullOrEmpty(_target)) return Fail("Укажите, на что указывает ссылка.");

        uint major = 0, minor = 0;
        bool redevice = IsDevice && (_major.Trim() != _initialMajor || _minor.Trim() != _initialMinor);
        if (redevice && (!uint.TryParse(_major.Trim(), out major) || !uint.TryParse(_minor.Trim(), out minor)))
            return Fail("Старший и младший номера устройства — целые неотрицательные числа.");

        SortedDictionary<string, byte[]>? xattrs = null;
        bool rexattr = CanEditXattrs && XattrSignature() != _initialXattrs;
        if (rexattr)
        {
            xattrs = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (XattrRow row in Xattrs)
            {
                string n = row.Name.Trim();
                if (n.Length == 0 || !n.Contains('.') || n.StartsWith('.') || n.EndsWith('.') || n.Contains('\0'))
                    return Fail($"Имя атрибута «{n}» должно начинаться с пространства имён: user., security., trusted. или system.");
                if (xattrs.ContainsKey(n)) return Fail($"Атрибут «{n}» указан дважды.");
                if (!Fmt.TryParseXattrValue(row.Value, out byte[] v)) return Fail($"Значение атрибута «{n}»: после 0x должны идти пары шестнадцатеричных цифр.");
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

/// <summary>Asks which partition of a disk image to open when several hold an editable filesystem.</summary>
public sealed class PartitionChoiceViewModel : DialogViewModel
{
    public sealed record Row(PartitionInfo Partition, string Name, string Content, string Details, bool IsEditable);

    private Row? _selected;

    public PartitionChoiceViewModel(string fileName, PartitionTable table)
    {
        Title = "Выбор раздела";
        AcceptText = "Открыть";
        Summary = $"«{fileName}» — образ диска с таблицей разделов {Fmt.PartitionScheme(table)}. " +
                  "Файловые системы, которые можно редактировать, есть в нескольких разделах. Какой из них открыть?";
        foreach (PartitionInfo p in table.Partitions)
        {
            string details = $"{Fmt.Size(p.Length)} · {p.TypeName} · смещение 0x{p.Offset:X}" + (p.Bootable ? " · загрузочный" : "");
            Rows.Add(new Row(p, $"Раздел {p.Number}", Fmt.PartitionContent(p), details, p.IsEditable));
        }
        PartitionInfo? preferred = table.PickDefault();
        _selected = Rows.FirstOrDefault(r => ReferenceEquals(r.Partition, preferred));
    }

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

    public override bool Accept()
    {
        if (_selected is null)
        {
            Error = "Выберите раздел.";
            return false;
        }
        if (!_selected.IsEditable)
        {
            Error = "В этом разделе нет SquashFS или ext2/3/4 — его нельзя открыть для редактирования.";
            return false;
        }
        Result = _selected.Partition;
        return true;
    }
}

/// <summary>Read-only facts about the opened image.</summary>
public sealed class ImageInfoViewModel : DialogViewModel
{
    public sealed record Row(string Label, string Value);

    public ImageInfoViewModel(ImageDocument doc, AppSettings settings)
    {
        Title = "Сведения об образе";
        AcceptText = "Закрыть";
        void Add(string label, string value) => Rows.Add(new Row(label, value));

        Add("Файл", doc.FilePath ?? "(новый образ, ещё не сохранён)");
        Add("Формат", doc.FormatName);
        if (doc.FilePath is not null) Add("Размер файла", Fmt.SizeWithBytes(doc.FileSize));
        if (doc.SquashFs is { } s)
        {
            Add("Версия", $"{s.VersionMajor}.{s.VersionMinor}");
            Add("Сжатие", s.Compression.ToString().ToLowerInvariant() + (s.XzFilters.Count > 0 ? " + BCJ " + string.Join(", ", s.XzFilters) : ""));
            Add("Размер блока", Fmt.Size(s.BlockSize));
            Add("Инодов", Fmt.Bytes(s.InodeCount));
            Add("Фрагментов", s.NoFragments ? "не используются" : Fmt.Bytes(s.FragmentCount));
            Add("Особенности", string.Join(", ", new[]
            {
                s.Exportable ? "экспортируемая (NFS)" : null, s.Deduplicated ? "дедупликация" : null, s.HasXattrs ? "расширенные атрибуты" : null,
            }.Where(x => x is not null)));
            if (s.MkfsTime > 0) Add("Создан", Fmt.Date(s.MkfsTime, settings.UtcTimes));
        }
        if (doc.Ext is { } e)
        {
            Add("Размер ФС", $"{Fmt.Size(e.SizeBytes)} ({Fmt.Bytes(e.BlocksCount)} блоков по {e.BlockSize} байт)");
            Add("Свободно", $"{Fmt.Size(e.FreeBlocks * e.BlockSize)} ({Fmt.Bytes(e.FreeBlocks)} блоков)");
            Add("Иноды", $"{Fmt.Bytes(e.InodesCount)} по {e.InodeSize} байт, свободно {Fmt.Bytes(e.FreeInodes)}");
            Add("Журнал", e.HasJournal ? Fmt.Size(e.JournalBytes) : "нет");
            Add("Метка", e.Label.Length > 0 ? e.Label : "(нет)");
            Add("UUID", e.Uuid.ToString());
            Add("Возможности", string.Join(" ", e.FeatureNames));
            Add("Состояние", e.NeedsRecovery ? "журнал не воспроизведён (требуется восстановление)" : e.Clean ? "чистая" : "не была корректно размонтирована");
        }
        if (doc.Tar is { } t)
        {
            Add("Сжатие", t.Compression == Core.Tar.ArchiveCompression.None ? "нет" : t.Compression.ToString().ToLowerInvariant());
            Add("Записей", Fmt.Bytes(t.Entries));
            Add("Расширения", string.Join(", ", new[] { t.HasPaxHeaders ? "pax" : null, t.HasGnuExtensions ? "GNU" : null }.Where(x => x is not null).DefaultIfEmpty("нет")));
            Add("Префикс путей", t.PathPrefix.Length > 0 ? t.PathPrefix : "(нет)");
        }
        if (doc.Container is { } disk)
        {
            Add("Образ диска", $"{Fmt.SizeWithBytes(disk.DiskLength)}, таблица разделов {Fmt.PartitionScheme(disk.Table)}");
            foreach (PartitionInfo p in disk.Table.Partitions)
            {
                string text = $"{Fmt.PartitionContent(p)}, {Fmt.Size(p.Length)}, {p.TypeName}, смещение 0x{p.Offset:X}";
                if (p.StoredLength < p.Length) text += $", в файле {Fmt.Size(p.StoredLength)}";
                Add(ReferenceEquals(p, disk.Partition) ? $"▸ Раздел {p.Number} (открыт)" : $"Раздел {p.Number}", text);
            }
            Add("Сохранение", "образ диска целиком: заменяется только открытый раздел, таблица разделов и остальные разделы не меняются");
        }
        else if (doc.EmbeddedOffset > 0) Add("Смещение ФС в файле", $"0x{doc.EmbeddedOffset:X}");
        if (doc.AndroidSparse) Add("Контейнер", "Android sparse image");
        if (doc.OuterCompression is { } outer)
            Add("Файл сжат", outer.ToString().ToLowerInvariant() + " (распакован во временный файл; при сохранении под тем же именем сжимается снова)");

        TreeStats st = doc.Tree.GetStats();
        Add("Содержимое", $"{Fmt.Plural(st.Files, "файл", "файла", "файлов")}, {Fmt.Plural(st.Directories, "папка", "папки", "папок")}, " +
                          $"{Fmt.Plural(st.Symlinks, "ссылка", "ссылки", "ссылок")}, {Fmt.Plural(st.Specials, "специальный файл", "специальных файла", "специальных файлов")}");
        Add("Объём данных", Fmt.SizeWithBytes(st.DataBytes));
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
        AcceptText = "Закрыть";
        foreach (string m in messages) Messages.Add(m);
    }

    public string Summary { get; }
    public ObservableCollection<string> Messages { get; } = new();

    public string AllText => string.Join(Environment.NewLine, Messages);

    public override bool Accept() => true;
}
