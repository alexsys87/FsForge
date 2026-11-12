using System.Text;

namespace FsForge.Core;

/// <summary>Kind of a filesystem object.</summary>
public enum FsNodeType
{
    Directory,
    File,
    Symlink,
    CharDevice,
    BlockDevice,
    Fifo,
    Socket,
}

/// <summary>
/// Inode: attributes and content of a filesystem object. Several directory entries
/// (<see cref="FsNode"/>) may share one inode, which is how hard links are modelled.
/// </summary>
public sealed class FsInode
{
    public FsNodeType Type;

    /// <summary>Permission bits including setuid/setgid/sticky (0..07777).</summary>
    public int Mode;

    public uint Uid;
    public uint Gid;

    /// <summary>Modification time, seconds since the Unix epoch (UTC).</summary>
    public long MTime;

    /// <summary>Symbolic link target (symlinks only).</summary>
    public string? LinkTarget;

    /// <summary>Device numbers (character and block devices only).</summary>
    public uint DevMajor;
    public uint DevMinor;

    /// <summary>File content (regular files only). Null means an empty file.</summary>
    public IFileData? Data;

    /// <summary>Extended attributes: full name ("security.selinux") to raw value.</summary>
    public SortedDictionary<string, byte[]>? XAttrs;

    /// <summary>Number of directory entries that reference this inode.</summary>
    public int LinkCount { get; internal set; }

    public long Size => Type switch
    {
        FsNodeType.File => Data?.Length ?? 0,
        FsNodeType.Symlink => LinkTarget is null ? 0 : PosixName.Encode(LinkTarget).Length,
        _ => 0,
    };

    public bool HasXAttrs => XAttrs is { Count: > 0 };

    /// <summary>Creates a detached copy of the attributes (content is shared, it is immutable).</summary>
    public FsInode Clone() => new()
    {
        Type = Type,
        Mode = Mode,
        Uid = Uid,
        Gid = Gid,
        MTime = MTime,
        LinkTarget = LinkTarget,
        DevMajor = DevMajor,
        DevMinor = DevMinor,
        Data = Data,
        XAttrs = XAttrs is null ? null : new SortedDictionary<string, byte[]>(XAttrs, StringComparer.Ordinal),
    };
}

/// <summary>Directory entry: a name inside a parent directory that points to an inode.</summary>
public sealed class FsNode
{
    private Dictionary<string, FsNode>? _children;

    internal FsNode(string name, FsInode inode)
    {
        Name = name;
        Inode = inode;
        if (inode.Type == FsNodeType.Directory)
            _children = new Dictionary<string, FsNode>(StringComparer.Ordinal);
    }

    public string Name { get; internal set; }
    public FsNode? Parent { get; internal set; }
    public FsInode Inode { get; }

    public FsNodeType Type => Inode.Type;
    public bool IsDirectory => Inode.Type == FsNodeType.Directory;
    public bool IsRoot => Parent is null;

    /// <summary>Children of a directory (empty for other node types). Unordered.</summary>
    public IReadOnlyCollection<FsNode> Children =>
        _children is null ? Array.Empty<FsNode>() : _children.Values;

    public int ChildCount => _children?.Count ?? 0;

    public FsNode? Find(string name) =>
        _children is not null && _children.TryGetValue(name, out FsNode? n) ? n : null;

    internal Dictionary<string, FsNode> ChildMap =>
        _children ?? throw new InvalidOperationException($"'{Path}' is not a directory.");

    /// <summary>Absolute POSIX path of the node ("/" for the root).</summary>
    public string Path
    {
        get
        {
            if (Parent is null) return "/";
            var parts = new Stack<string>();
            for (FsNode? n = this; n is { Parent: not null }; n = n.Parent) parts.Push(n.Name);
            var sb = new StringBuilder();
            foreach (string p in parts) sb.Append('/').Append(p);
            return sb.ToString();
        }
    }

    /// <summary>Path relative to the root without the leading slash ("" for the root).</summary>
    public string RelativePath => Parent is null ? "" : Path[1..];

    public bool IsDescendantOf(FsNode other)
    {
        for (FsNode? n = Parent; n is not null; n = n.Parent)
            if (ReferenceEquals(n, other)) return true;
        return false;
    }

    /// <summary>Children sorted by name (ordinal).</summary>
    public List<FsNode> SortedChildren()
    {
        var list = new List<FsNode>(Children);
        list.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    public override string ToString() => Path;
}

/// <summary>Aggregate numbers describing a (sub)tree.</summary>
public readonly record struct TreeStats(long Files, long Directories, long Symlinks, long Specials, long DataBytes)
{
    public long Total => Files + Directories + Symlinks + Specials;
}

/// <summary>
/// Editable in-memory filesystem tree. It is format neutral: readers build it from an image,
/// writers serialise it into a new image.
/// </summary>
public sealed class FsTree : IDisposable
{
    private readonly List<IDisposable> _resources = new();

    public FsTree(int rootMode = 0x1ED /* 0755 */, long mtime = 0)
    {
        Root = new FsNode("", new FsInode { Type = FsNodeType.Directory, Mode = rootMode, MTime = mtime, LinkCount = 1 });
    }

    public FsNode Root { get; }

    /// <summary>Raised after every structural or attribute change made through the tree API.</summary>
    public event Action? Changed;

    private int _suspended;
    private bool _pendingChange;

    private void RaiseChanged()
    {
        if (_suspended > 0) _pendingChange = true;
        else Changed?.Invoke();
    }

    /// <summary>
    /// Holds back <see cref="Changed"/> until the returned object is disposed; a bulk operation then
    /// raises the event once instead of once per node.
    /// </summary>
    public IDisposable SuspendNotifications()
    {
        _suspended++;
        return new Resume(this);
    }

    private sealed class Resume(FsTree tree) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            if (--tree._suspended == 0 && tree._pendingChange)
            {
                tree._pendingChange = false;
                tree.Changed?.Invoke();
            }
        }
    }

    /// <summary>Registers an object (temp spool, source image...) that must live as long as the tree.</summary>
    public void AttachResource(IDisposable resource)
    {
        lock (_resources) _resources.Add(resource);
    }

    public void NotifyChanged() => RaiseChanged();

    // ---------------------------------------------------------------- naming

    /// <summary>Checks that a name is usable as a single path component.</summary>
    public static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name)) throw new FsException("The name is empty.");
        if (name is "." or "..") throw new FsException($"'{name}' is not allowed as a name.");
        if (name.Contains('/')) throw new FsException("A name cannot contain '/'.");
        if (name.Contains('\0')) throw new FsException("A name cannot contain a NUL character.");
        if (PosixName.Encode(name).Length > 255) throw new FsException("The name is longer than 255 bytes.");
    }

    public static string[] SplitPath(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Finds a node by absolute or root-relative path. Does not follow symlinks.</summary>
    public FsNode? Resolve(string path)
    {
        FsNode? node = Root;
        foreach (string part in SplitPath(path))
        {
            if (part == ".") continue;
            if (part == "..") { node = node.Parent ?? node; continue; }
            node = node.Find(part);
            if (node is null) return null;
        }
        return node;
    }

    /// <summary>Returns a name that does not exist in <paramref name="dir"/> yet ("name", "name (2)", ...).</summary>
    public static string MakeUniqueName(FsNode dir, string name)
    {
        if (dir.Find(name) is null) return name;
        int dot = name.LastIndexOf('.');
        string stem = dot > 0 ? name[..dot] : name;
        string ext = dot > 0 ? name[dot..] : "";
        for (int i = 2; ; i++)
        {
            string candidate = $"{stem} ({i}){ext}";
            if (dir.Find(candidate) is null) return candidate;
        }
    }

    // ------------------------------------------------------------- creation

    /// <summary>Links an inode into a directory under the given name.</summary>
    public FsNode Add(FsNode parent, string name, FsInode inode)
    {
        ValidateName(name);
        Dictionary<string, FsNode> map = parent.ChildMap;
        if (map.ContainsKey(name)) throw new FsConflictException($"'{name}' already exists in '{parent.Path}'.");
        if (inode.Type == FsNodeType.Directory && inode.LinkCount > 0)
            throw new FsException("A directory cannot be hard linked.");
        var node = new FsNode(name, inode) { Parent = parent };
        map.Add(name, node);
        inode.LinkCount++;
        RaiseChanged();
        return node;
    }

    public FsNode CreateDirectory(FsNode parent, string name, int mode = 0x1ED, uint uid = 0, uint gid = 0, long mtime = 0) =>
        Add(parent, name, new FsInode { Type = FsNodeType.Directory, Mode = mode & 0xFFF, Uid = uid, Gid = gid, MTime = mtime });

    public FsNode CreateFile(FsNode parent, string name, IFileData? data, int mode = 0x1A4, uint uid = 0, uint gid = 0, long mtime = 0) =>
        Add(parent, name, new FsInode { Type = FsNodeType.File, Mode = mode & 0xFFF, Uid = uid, Gid = gid, MTime = mtime, Data = data });

    public FsNode CreateSymlink(FsNode parent, string name, string target, uint uid = 0, uint gid = 0, long mtime = 0)
    {
        if (string.IsNullOrEmpty(target)) throw new FsException("A symbolic link target cannot be empty.");
        return Add(parent, name, new FsInode { Type = FsNodeType.Symlink, Mode = 0x1FF, Uid = uid, Gid = gid, MTime = mtime, LinkTarget = target });
    }

    public FsNode CreateSpecial(FsNode parent, string name, FsNodeType type, uint major, uint minor, int mode = 0x1A4, uint uid = 0, uint gid = 0, long mtime = 0)
    {
        if (type is FsNodeType.Directory or FsNodeType.File or FsNodeType.Symlink)
            throw new ArgumentException("Not a special file type.", nameof(type));
        return Add(parent, name, new FsInode { Type = type, Mode = mode & 0xFFF, Uid = uid, Gid = gid, MTime = mtime, DevMajor = major, DevMinor = minor });
    }

    /// <summary>Creates one more name for an existing non-directory inode.</summary>
    public FsNode CreateHardLink(FsNode parent, string name, FsNode existing)
    {
        if (existing.IsDirectory) throw new FsException("A directory cannot be hard linked.");
        return Add(parent, name, existing.Inode);
    }

    /// <summary>Returns the directory at <paramref name="path"/>, creating missing components (mkdir -p).</summary>
    public FsNode EnsureDirectory(string path, int mode = 0x1ED, uint uid = 0, uint gid = 0, long mtime = 0)
    {
        FsNode node = Root;
        foreach (string part in SplitPath(path))
        {
            if (part == ".") continue;
            FsNode? next = node.Find(part);
            if (next is null) next = CreateDirectory(node, part, mode, uid, gid, mtime);
            else if (!next.IsDirectory) throw new FsException($"'{next.Path}' exists and is not a directory.");
            node = next;
        }
        return node;
    }

    // ---------------------------------------------------------- modification

    public void Remove(FsNode node)
    {
        if (node.Parent is null) throw new FsException("The root directory cannot be removed.");
        node.Parent.ChildMap.Remove(node.Name);
        node.Parent = null;
        ReleaseLinks(node);
        RaiseChanged();
    }

    private static void ReleaseLinks(FsNode node)
    {
        node.Inode.LinkCount--;
        if (node.IsDirectory)
            foreach (FsNode child in node.Children) ReleaseLinks(child);
    }

    public void Rename(FsNode node, string newName)
    {
        if (node.Parent is null) throw new FsException("The root directory cannot be renamed.");
        if (node.Name == newName) return;
        ValidateName(newName);
        Dictionary<string, FsNode> map = node.Parent.ChildMap;
        if (map.ContainsKey(newName)) throw new FsConflictException($"'{newName}' already exists in '{node.Parent.Path}'.");
        map.Remove(node.Name);
        node.Name = newName;
        map.Add(newName, node);
        RaiseChanged();
    }

    public void Move(FsNode node, FsNode newParent, string? newName = null)
    {
        if (node.Parent is null) throw new FsException("The root directory cannot be moved.");
        newName ??= node.Name;
        ValidateName(newName);
        if (ReferenceEquals(node, newParent) || newParent.IsDescendantOf(node))
            throw new FsException("A directory cannot be moved into itself.");
        Dictionary<string, FsNode> target = newParent.ChildMap;
        if (ReferenceEquals(newParent, node.Parent) && newName == node.Name) return;
        if (target.ContainsKey(newName)) throw new FsConflictException($"'{newName}' already exists in '{newParent.Path}'.");
        node.Parent.ChildMap.Remove(node.Name);
        node.Name = newName;
        node.Parent = newParent;
        target.Add(newName, node);
        RaiseChanged();
    }

    /// <summary>Deep copy of a subtree into another directory. Hard links inside the copy are preserved.</summary>
    public FsNode Copy(FsNode node, FsNode newParent, string? newName = null)
    {
        if (ReferenceEquals(node, newParent) || newParent.IsDescendantOf(node))
            throw new FsException("A directory cannot be copied into itself.");
        var map = new Dictionary<FsInode, FsInode>(ReferenceEqualityComparer.Instance);
        return CopyInto(node, newParent, newName ?? node.Name, map);
    }

    private FsNode CopyInto(FsNode src, FsNode parent, string name, Dictionary<FsInode, FsInode> map)
    {
        if (!map.TryGetValue(src.Inode, out FsInode? inode))
        {
            inode = src.Inode.Clone();
            map.Add(src.Inode, inode);
        }
        FsNode copy = Add(parent, name, inode);
        if (src.IsDirectory)
            foreach (FsNode child in src.SortedChildren()) CopyInto(child, copy, child.Name, map);
        return copy;
    }

    // ------------------------------------------------------------ attributes

    /// <summary>Which nodes a recursive attribute change touches.</summary>
    [Flags]
    public enum Targets
    {
        Files = 1,
        Directories = 2,
        Others = 4,
        All = Files | Directories | Others,
    }

    private static bool Matches(FsNode n, Targets targets) => n.Type switch
    {
        FsNodeType.Directory => (targets & Targets.Directories) != 0,
        FsNodeType.File => (targets & Targets.Files) != 0,
        _ => (targets & Targets.Others) != 0,
    };

    private static IEnumerable<FsNode> Expand(IEnumerable<FsNode> nodes, bool recursive)
    {
        foreach (FsNode n in nodes)
        {
            if (!recursive || !n.IsDirectory) yield return n;
            else foreach (FsNode d in Walk(n)) yield return d;
        }
    }

    /// <summary>
    /// chmod: clears the bits of <paramref name="clear"/>, then sets the bits of <paramref name="set"/>.
    /// Symbolic links are never changed (their mode is always 0777). Returns the number of inodes changed.
    /// </summary>
    public int ChangeMode(IEnumerable<FsNode> nodes, int set, int clear, bool recursive = false, Targets targets = Targets.All)
    {
        int changed = 0;
        foreach (FsNode n in Expand(nodes, recursive))
        {
            if (n.Type == FsNodeType.Symlink || !Matches(n, targets)) continue;
            int mode = ((n.Inode.Mode & ~clear) | set) & 0xFFF;
            if (mode == n.Inode.Mode) continue;
            n.Inode.Mode = mode;
            changed++;
        }
        if (changed > 0) RaiseChanged();
        return changed;
    }

    /// <summary>chmod with an absolute mode.</summary>
    public int SetMode(IEnumerable<FsNode> nodes, int mode, bool recursive = false, Targets targets = Targets.All) =>
        ChangeMode(nodes, mode & 0xFFF, 0xFFF, recursive, targets);

    /// <summary>chown: null leaves the owner or the group unchanged.</summary>
    public int ChangeOwner(IEnumerable<FsNode> nodes, uint? uid, uint? gid, bool recursive = false)
    {
        int changed = 0;
        foreach (FsNode n in Expand(nodes, recursive))
        {
            FsInode i = n.Inode;
            if ((uid is null || i.Uid == uid) && (gid is null || i.Gid == gid)) continue;
            if (uid is not null) i.Uid = uid.Value;
            if (gid is not null) i.Gid = gid.Value;
            changed++;
        }
        if (changed > 0) RaiseChanged();
        return changed;
    }

    public int SetMTime(IEnumerable<FsNode> nodes, long mtime, bool recursive = false)
    {
        int changed = 0;
        foreach (FsNode n in Expand(nodes, recursive))
        {
            if (n.Inode.MTime == mtime) continue;
            n.Inode.MTime = mtime;
            changed++;
        }
        if (changed > 0) RaiseChanged();
        return changed;
    }

    /// <summary>Replaces the content of a regular file.</summary>
    public void SetData(FsNode node, IFileData? data, long? mtime = null)
    {
        if (node.Type != FsNodeType.File) throw new FsException($"'{node.Path}' is not a regular file.");
        node.Inode.Data = data is { Length: 0 } ? null : data;
        if (mtime is not null) node.Inode.MTime = mtime.Value;
        RaiseChanged();
    }

    public void SetLinkTarget(FsNode node, string target)
    {
        if (node.Type != FsNodeType.Symlink) throw new FsException($"'{node.Path}' is not a symbolic link.");
        if (string.IsNullOrEmpty(target)) throw new FsException("A symbolic link target cannot be empty.");
        if (target.Contains('\0')) throw new FsException("A symbolic link target cannot contain a NUL character.");
        if (PosixName.Encode(target).Length > 4095) throw new FsException("The symbolic link target is longer than 4095 bytes.");
        node.Inode.LinkTarget = target;
        RaiseChanged();
    }

    public void SetDevice(FsNode node, uint major, uint minor)
    {
        if (node.Type is not (FsNodeType.CharDevice or FsNodeType.BlockDevice))
            throw new FsException($"'{node.Path}' is not a device node.");
        node.Inode.DevMajor = major;
        node.Inode.DevMinor = minor;
        RaiseChanged();
    }

    /// <summary>Sets (or with a null value removes) one extended attribute.</summary>
    public void SetXAttr(FsNode node, string name, byte[]? value)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('\0')) throw new FsException("The attribute name is invalid.");
        if (value is null)
        {
            if (node.Inode.XAttrs?.Remove(name) != true) return;
            if (node.Inode.XAttrs.Count == 0) node.Inode.XAttrs = null;
        }
        else
        {
            if (!name.Contains('.') || name.StartsWith('.') || name.EndsWith('.'))
                throw new FsException("An attribute name needs a namespace prefix such as \"user.\" or \"security.\".");
            (node.Inode.XAttrs ??= new SortedDictionary<string, byte[]>(StringComparer.Ordinal))[name] = value;
        }
        RaiseChanged();
    }

    /// <summary>
    /// Gives a file that shares its inode with other names (hard links) an inode of its own,
    /// so that later attribute or content changes affect this name only.
    /// </summary>
    public void BreakHardLink(FsNode node)
    {
        if (node.IsDirectory || node.Inode.LinkCount <= 1 || node.Parent is null) return;
        FsNode parent = node.Parent;
        string name = node.Name;
        FsInode copy = node.Inode.Clone();
        using (SuspendNotifications())
        {
            Remove(node);
            Add(parent, name, copy);
        }
    }

    // -------------------------------------------------------------- queries

    /// <summary>Pre-order traversal; children are visited in ordinal name order.</summary>
    public static IEnumerable<FsNode> Walk(FsNode start, bool includeStart = true)
    {
        var stack = new Stack<FsNode>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            FsNode node = stack.Pop();
            if (includeStart || !ReferenceEquals(node, start)) yield return node;
            if (!node.IsDirectory) continue;
            List<FsNode> kids = node.SortedChildren();
            for (int i = kids.Count - 1; i >= 0; i--) stack.Push(kids[i]);
        }
    }

    public IEnumerable<FsNode> Walk() => Walk(Root);

    public static TreeStats GetStats(FsNode start)
    {
        long files = 0, dirs = 0, links = 0, specials = 0, bytes = 0;
        var seen = new HashSet<FsInode>(ReferenceEqualityComparer.Instance);
        foreach (FsNode n in Walk(start))
        {
            switch (n.Type)
            {
                case FsNodeType.Directory: dirs++; break;
                case FsNodeType.File:
                    files++;
                    if (seen.Add(n.Inode)) bytes += n.Inode.Size;
                    break;
                case FsNodeType.Symlink: links++; break;
                default: specials++; break;
            }
        }
        return new TreeStats(files, dirs, links, specials, bytes);
    }

    public TreeStats GetStats() => GetStats(Root);

    public void Dispose()
    {
        lock (_resources)
        {
            for (int i = _resources.Count - 1; i >= 0; i--)
            {
                try { _resources[i].Dispose(); } catch { /* best effort */ }
            }
            _resources.Clear();
        }
    }
}

/// <summary>Error raised by the filesystem core (corrupt image, unsupported feature, invalid operation).</summary>
public class FsException : Exception
{
    public FsException(string message) : base(message) { }
    public FsException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// A reader failed in a way it did not anticipate (almost always a damaged image). It is reported
/// like any other read error, but kept apart so that tests can tell it from a clean rejection.
/// </summary>
public sealed class FsUnexpectedException(string message, Exception inner) : FsException(message, inner);

/// <summary>The content does not fit into an image of the requested size.</summary>
public sealed class FsNoSpaceException(string message) : FsException(message);

/// <summary>An object with the requested name already exists in the directory.</summary>
public sealed class FsConflictException(string message) : FsException(message);

/// <summary>
/// Lossless conversion between POSIX names (arbitrary bytes) and .NET strings.
/// Valid UTF-8 is decoded normally; every byte that is not part of a valid sequence is mapped
/// to a lone surrogate U+DC80..U+DCFF (the "surrogateescape" scheme) and restored on encoding.
/// </summary>
public static class PosixName
{
    private static readonly UTF8Encoding Strict = new(false, true);

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return Strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return DecodeEscaped(bytes);
        }
    }

    private static string DecodeEscaped(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        int i = 0;
        while (i < bytes.Length)
        {
            System.Buffers.OperationStatus st = Rune.DecodeFromUtf8(bytes[i..], out Rune rune, out int used);
            if (st == System.Buffers.OperationStatus.Done)
            {
                sb.Append(rune.ToString());
                i += used;
            }
            else
            {
                sb.Append((char)(0xDC00 + bytes[i]));
                i++;
            }
        }
        return sb.ToString();
    }

    public static byte[] Encode(string name)
    {
        bool escaped = false;
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c >= 0xDC80 && c <= 0xDCFF && (i == 0 || !char.IsHighSurrogate(name[i - 1]))) { escaped = true; break; }
        }
        if (!escaped)
        {
            try { return Strict.GetBytes(name); }
            catch (EncoderFallbackException) { /* lone surrogate: fall through */ }
        }

        var ms = new MemoryStream(name.Length + 8);
        Span<byte> tmp = stackalloc byte[4];
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsHighSurrogate(c) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1]))
            {
                int n = new Rune(c, name[i + 1]).EncodeToUtf8(tmp);
                ms.Write(tmp[..n]);
                i++;
            }
            else if (c >= 0xDC80 && c <= 0xDCFF)
            {
                ms.WriteByte((byte)(c - 0xDC00));
            }
            else if (char.IsSurrogate(c))
            {
                ms.WriteByte((byte)'?');
            }
            else
            {
                int n = new Rune(c).EncodeToUtf8(tmp);
                ms.Write(tmp[..n]);
            }
        }
        return ms.ToArray();
    }

    /// <summary>Byte-wise comparison of the encoded names (the order used by strcmp on Linux).</summary>
    public static int CompareBytes(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a.SequenceCompareTo(b);
}

/// <summary>Helpers for Unix mode bits.</summary>
public static class UnixMode
{
    public const int S_IFMT = 0xF000;
    public const int S_IFSOCK = 0xC000;
    public const int S_IFLNK = 0xA000;
    public const int S_IFREG = 0x8000;
    public const int S_IFBLK = 0x6000;
    public const int S_IFDIR = 0x4000;
    public const int S_IFCHR = 0x2000;
    public const int S_IFIFO = 0x1000;

    public static int TypeBits(FsNodeType type) => type switch
    {
        FsNodeType.Directory => S_IFDIR,
        FsNodeType.File => S_IFREG,
        FsNodeType.Symlink => S_IFLNK,
        FsNodeType.CharDevice => S_IFCHR,
        FsNodeType.BlockDevice => S_IFBLK,
        FsNodeType.Fifo => S_IFIFO,
        FsNodeType.Socket => S_IFSOCK,
        _ => 0,
    };

    public static FsNodeType? TypeFromMode(int mode) => (mode & S_IFMT) switch
    {
        S_IFDIR => FsNodeType.Directory,
        S_IFREG => FsNodeType.File,
        S_IFLNK => FsNodeType.Symlink,
        S_IFCHR => FsNodeType.CharDevice,
        S_IFBLK => FsNodeType.BlockDevice,
        S_IFIFO => FsNodeType.Fifo,
        S_IFSOCK => FsNodeType.Socket,
        _ => null,
    };

    /// <summary>Formats mode the way "ls -l" does, e.g. "drwxr-xr-x".</summary>
    public static string Format(FsNodeType type, int mode)
    {
        Span<char> s = stackalloc char[10];
        s[0] = type switch
        {
            FsNodeType.Directory => 'd',
            FsNodeType.Symlink => 'l',
            FsNodeType.CharDevice => 'c',
            FsNodeType.BlockDevice => 'b',
            FsNodeType.Fifo => 'p',
            FsNodeType.Socket => 's',
            _ => '-',
        };
        s[1] = (mode & 0x100) != 0 ? 'r' : '-';
        s[2] = (mode & 0x080) != 0 ? 'w' : '-';
        s[3] = (mode & 0x800) != 0 ? ((mode & 0x040) != 0 ? 's' : 'S') : ((mode & 0x040) != 0 ? 'x' : '-');
        s[4] = (mode & 0x020) != 0 ? 'r' : '-';
        s[5] = (mode & 0x010) != 0 ? 'w' : '-';
        s[6] = (mode & 0x400) != 0 ? ((mode & 0x008) != 0 ? 's' : 'S') : ((mode & 0x008) != 0 ? 'x' : '-');
        s[7] = (mode & 0x004) != 0 ? 'r' : '-';
        s[8] = (mode & 0x002) != 0 ? 'w' : '-';
        s[9] = (mode & 0x200) != 0 ? ((mode & 0x001) != 0 ? 't' : 'T') : ((mode & 0x001) != 0 ? 'x' : '-');
        return new string(s);
    }

    public static string ToOctal(int mode) => Convert.ToString(mode & 0xFFF, 8).PadLeft(4, '0');

    public static bool TryParseOctal(string text, out int mode)
    {
        mode = 0;
        text = text.Trim();
        if (text.Length is 0 or > 5) return false;
        foreach (char c in text)
        {
            if (c < '0' || c > '7') return false;
            mode = (mode << 3) | (c - '0');
        }
        return mode <= 0xFFF;
    }
}
