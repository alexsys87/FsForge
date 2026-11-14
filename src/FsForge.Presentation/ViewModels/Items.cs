using System.Collections.ObjectModel;
using FsForge.Core;
using FsForge.Presentation.Mvvm;
using FsForge.Presentation.Services;
using FsForge.Presentation.Settings;

namespace FsForge.Presentation.ViewModels;

/// <summary>Picture shown next to an entry.</summary>
public enum NodeIcon
{
    Folder,
    File,
    Executable,
    Symlink,
    CharDevice,
    BlockDevice,
    Fifo,
    Socket,
}

/// <summary>A value with the text shown for it in a list of choices.</summary>
public sealed record Choice<T>(T Value, string Title)
{
    public override string ToString() => Title;
}

/// <summary>One row of the file list: a snapshot of a node formatted for display.</summary>
public sealed class NodeItem
{
    public NodeItem(FsNode node, AppSettings settings, IdNames names, bool showLocation = false)
    {
        Node = node;
        FsInode i = node.Inode;
        Name = node.IsRoot ? "/" : node.Name;
        Location = showLocation ? node.Parent?.Path ?? "" : "";
        Type = i.Type;
        IsDirectory = node.IsDirectory;
        Size = IsDirectory ? -1 : i.Size;
        SizeText = i.Type switch
        {
            FsNodeType.Directory => "",
            FsNodeType.CharDevice or FsNodeType.BlockDevice => $"{i.DevMajor}, {i.DevMinor}",
            FsNodeType.File or FsNodeType.Symlink => settings.Sizes == SizeDisplay.Bytes ? Fmt.Bytes(i.Size) : Fmt.Size(i.Size),
            _ => "",
        };
        Mode = UnixMode.Format(i.Type, i.Type == FsNodeType.Symlink ? 0x1FF : i.Mode);
        Octal = i.Type == FsNodeType.Symlink ? "0777" : UnixMode.ToOctal(i.Mode);
        Uid = i.Uid;
        Gid = i.Gid;
        Owner = names.Owner(i.Uid, i.Gid, settings.ShowOwnerNames);
        MTime = i.MTime;
        Modified = Fmt.Date(i.MTime, settings.UtcTimes);
        TypeText = i.Type switch
        {
            FsNodeType.File when (i.Mode & 0x49) != 0 => L.T("Исполняемый файл"),
            _ => Fmt.TypeName(i.Type),
        };
        Icon = i.Type switch
        {
            FsNodeType.Directory => NodeIcon.Folder,
            FsNodeType.Symlink => NodeIcon.Symlink,
            FsNodeType.CharDevice => NodeIcon.CharDevice,
            FsNodeType.BlockDevice => NodeIcon.BlockDevice,
            FsNodeType.Fifo => NodeIcon.Fifo,
            FsNodeType.Socket => NodeIcon.Socket,
            _ => (i.Mode & 0x49) != 0 ? NodeIcon.Executable : NodeIcon.File,
        };
        Details = i.Type switch
        {
            FsNodeType.Symlink => "→ " + i.LinkTarget,
            FsNodeType.File when i.LinkCount > 1 => L.F("жёсткая ссылка ×{0}", i.LinkCount),
            _ => i.HasXAttrs ? $"xattr: {i.XAttrs!.Count}" : "",
        };
    }

    public FsNode Node { get; }
    public string Name { get; }
    public string Location { get; }
    public FsNodeType Type { get; }
    public bool IsDirectory { get; }
    public long Size { get; }
    public string SizeText { get; }
    public string Mode { get; }
    public string Octal { get; }
    public uint Uid { get; }
    public uint Gid { get; }
    public string Owner { get; }
    public long MTime { get; }
    public string Modified { get; }
    public string TypeText { get; }
    public NodeIcon Icon { get; }
    public string Details { get; }

    public override string ToString() => Name;
}

/// <summary>A directory in the folder tree on the left.</summary>
public sealed class TreeItem : ObservableObject
{
    private bool _isExpanded, _isSelected;
    private string _name;

    public TreeItem(FsNode node, TreeItem? parent)
    {
        Node = node;
        Parent = parent;
        _name = node.IsRoot ? "/" : node.Name;
    }

    public FsNode Node { get; }
    public TreeItem? Parent { get; }
    public ObservableCollection<TreeItem> Children { get; } = new();

    public string Name
    {
        get => _name;
        private set => SetProperty(ref _name, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>Brings this item and everything below it in line with the model.</summary>
    public void Sync()
    {
        Name = Node.IsRoot ? "/" : Node.Name;
        var wanted = new List<FsNode>();
        foreach (FsNode c in Node.SortedChildren())
            if (c.IsDirectory) wanted.Add(c);

        // Fast path: nothing was added, removed or reordered.
        bool same = wanted.Count == Children.Count;
        for (int i = 0; same && i < wanted.Count; i++) same = ReferenceEquals(Children[i].Node, wanted[i]);
        if (!same)
        {
            var existing = new Dictionary<FsNode, TreeItem>(ReferenceEqualityComparer.Instance);
            foreach (TreeItem t in Children) existing[t.Node] = t;
            for (int i = 0; i < wanted.Count; i++)
            {
                if (i < Children.Count && ReferenceEquals(Children[i].Node, wanted[i])) continue;
                if (existing.TryGetValue(wanted[i], out TreeItem? item))
                {
                    int at = Children.IndexOf(item);
                    if (at >= 0) Children.Move(at, i);
                    else Children.Insert(i, item);
                }
                else
                {
                    Children.Insert(i, new TreeItem(wanted[i], this));
                }
            }
            while (Children.Count > wanted.Count) Children.RemoveAt(Children.Count - 1);
        }
        foreach (TreeItem c in Children) c.Sync();
    }

    /// <summary>Finds the item of a directory node below this one.</summary>
    public TreeItem? Find(FsNode node)
    {
        var chain = new Stack<FsNode>();
        for (FsNode? n = node; n is not null && !ReferenceEquals(n, Node); n = n.Parent) chain.Push(n);
        TreeItem current = this;
        while (chain.Count > 0)
        {
            FsNode next = chain.Pop();
            TreeItem? child = null;
            foreach (TreeItem c in current.Children)
            {
                if (ReferenceEquals(c.Node, next))
                {
                    child = c;
                    break;
                }
            }
            if (child is null) return null;
            current = child;
        }
        return ReferenceEquals(current.Node, node) ? current : null;
    }

    public IEnumerable<TreeItem> Flatten()
    {
        yield return this;
        foreach (TreeItem c in Children)
            foreach (TreeItem d in c.Flatten()) yield return d;
    }
}

/// <summary>One segment of the address bar.</summary>
public sealed record Crumb(string Title, FsNode Node)
{
    public override string ToString() => Title;
}

/// <summary>Entry of the "recent files" list.</summary>
public sealed record RecentItem(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";

    /// <summary>The path as menu text: underscores are doubled so that they are not taken for access keys.</summary>
    public string MenuText => Path.Replace("_", "__");
    public override string ToString() => Path;
}
