namespace FsForge.Core.Host;

/// <summary>What to do when an added object has the name of an existing one.</summary>
public enum ConflictPolicy
{
    /// <summary>Replace the existing object (directories are merged).</summary>
    Overwrite,

    /// <summary>Keep the existing object and do not add the new one.</summary>
    Skip,

    /// <summary>Add the new object under a free name ("name (2)").</summary>
    Rename,

    /// <summary>Stop with an error.</summary>
    Fail,
}

public sealed class ImportOptions
{
    /// <summary>Restore owners, modes, links and device nodes from ".fsforge-meta.json" when it is present.</summary>
    public bool UseManifest { get; set; } = true;

    public int DefaultFileMode { get; set; } = 0x1A4;        // 0644
    public int DefaultDirectoryMode { get; set; } = 0x1ED;   // 0755
    public int ExecutableMode { get; set; } = 0x1ED;         // 0755
    public uint DefaultUid { get; set; }
    public uint DefaultGid { get; set; }

    /// <summary>New objects get the owner of the directory they are added to.</summary>
    public bool InheritOwner { get; set; } = true;

    /// <summary>Give ELF binaries and scripts that start with "#!" the executable mode.</summary>
    public bool DetectExecutables { get; set; } = true;

    /// <summary>Use the last-write time of the host file; otherwise the time of the import.</summary>
    public bool UseHostTimestamps { get; set; } = true;

    public ConflictPolicy Conflicts { get; set; } = ConflictPolicy.Overwrite;

    /// <summary>Skip files and folders with the Hidden or System attribute (desktop.ini, Thumbs.db...).</summary>
    public bool SkipHiddenAndSystem { get; set; }

    /// <summary>
    /// Copy file contents into this spool at import time. Without it the tree references the host
    /// files, which must then stay unchanged until the image is saved.
    /// </summary>
    public TempSpool? Snapshot { get; set; }

    public ImportOptions Clone() => (ImportOptions)MemberwiseClone();
}

public sealed class ImportResult
{
    public long Files, Directories, Symlinks, Specials, Bytes, Skipped, Replaced;
    public List<string> Warnings { get; } = new();

    /// <summary>Top-level nodes that were added or merged into.</summary>
    public List<FsNode> Added { get; } = new();
}

/// <summary>Adds files and folders of the host filesystem to a tree.</summary>
public static class HostImporter
{
    private sealed class Context
    {
        public required FsTree Tree;
        public required ImportOptions Options;
        public required ImportResult Result;
        public IProgress<ProgressInfo>? Progress;
        public CancellationToken Ct;
        public long Now;
        public long Counter;
        public readonly Dictionary<string, HostManifest?> Manifests = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<(HostManifest, int), FsNode> LinkGroups = new();
    }

    /// <summary>Adds every path (file or folder, recursively) to <paramref name="target"/>.</summary>
    public static ImportResult Import(FsTree tree, FsNode target, IReadOnlyList<string> hostPaths, ImportOptions options,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        if (!target.IsDirectory) throw new FsException($"'{target.Path}' is not a directory.");
        Context ctx = NewContext(tree, options, progress, ct);
        using (tree.SuspendNotifications())
        {
            foreach (string raw in hostPaths)
            {
                string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
                FsNode? node = ImportEntry(ctx, target, path, topLevel: true);
                if (node is not null && !ctx.Result.Added.Contains(node)) ctx.Result.Added.Add(node);
            }
            tree.NotifyChanged();
        }
        progress?.Report(new ProgressInfo("done", ctx.Counter, ctx.Counter));
        return ctx.Result;
    }

    /// <summary>
    /// Adds the contents of <paramref name="hostDirectory"/> (not the folder itself) to <paramref name="target"/>.
    /// When the folder carries a manifest entry for itself, its attributes are applied to the target.
    /// </summary>
    public static ImportResult ImportContents(FsTree tree, FsNode target, string hostDirectory, ImportOptions options,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        if (!target.IsDirectory) throw new FsException($"'{target.Path}' is not a directory.");
        hostDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(hostDirectory));
        if (!Directory.Exists(hostDirectory)) throw new FsException($"The folder '{hostDirectory}' does not exist.");
        Context ctx = NewContext(tree, options, progress, ct);

        (HostManifest? manifest, string rel) = FindManifest(ctx, hostDirectory, isDirectory: true);
        if (manifest is not null && manifest.Entries.TryGetValue(rel, out ManifestEntry? self) && self.Type == FsNodeType.Directory)
            self.ApplyTo(target.Inode);

        using (tree.SuspendNotifications())
        {
            ImportChildren(ctx, target, hostDirectory);
            tree.NotifyChanged();
        }
        ctx.Result.Added.Add(target);
        progress?.Report(new ProgressInfo("done", ctx.Counter, ctx.Counter));
        return ctx.Result;
    }

    private static Context NewContext(FsTree tree, ImportOptions options, IProgress<ProgressInfo>? progress, CancellationToken ct) => new()
    {
        Tree = tree,
        Options = options,
        Result = new ImportResult(),
        Progress = progress,
        Ct = ct,
        Now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    };

    // ------------------------------------------------------------- manifest

    /// <summary>
    /// Finds the manifest that covers a host path by walking up the folder chain, and the key of the
    /// path inside it.
    /// </summary>
    private static (HostManifest? Manifest, string Key) FindManifest(Context ctx, string hostPath, bool isDirectory)
    {
        if (!ctx.Options.UseManifest) return (null, "");
        string? dir = isDirectory ? hostPath : Path.GetDirectoryName(hostPath);
        var parts = new Stack<string>();
        if (!isDirectory) parts.Push(Path.GetFileName(hostPath));
        for (int depth = 0; dir is not null && depth < 64; depth++)
        {
            if (!ctx.Manifests.TryGetValue(dir, out HostManifest? m))
                ctx.Manifests[dir] = m = HostManifest.TryLoad(dir);
            if (m is not null) return (m, string.Join('/', parts));
            parts.Push(Path.GetFileName(dir));
            dir = Path.GetDirectoryName(dir);
        }
        return (null, "");
    }

    // --------------------------------------------------------------- import

    private static void ImportChildren(Context ctx, FsNode target, string hostDirectory)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(hostDirectory).OrderBy(p => p, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ctx.Result.Warnings.Add($"'{hostDirectory}' could not be read: {ex.Message}");
            return;
        }
        foreach (string path in entries)
        {
            if (string.Equals(Path.GetFileName(path), HostManifest.FileName, StringComparison.OrdinalIgnoreCase)) continue;
            ImportEntry(ctx, target, path, topLevel: false);
        }
    }

    private static FsNode? ImportEntry(Context ctx, FsNode parent, string hostPath, bool topLevel)
    {
        ctx.Ct.ThrowIfCancellationRequested();
        try
        {
            FileSystemInfo info = Directory.Exists(hostPath) ? new DirectoryInfo(hostPath) : new FileInfo(hostPath);
            if (!info.Exists && info.LinkTarget is null)
            {
                ctx.Result.Skipped++;
                ctx.Result.Warnings.Add($"'{hostPath}' does not exist.");
                return null;
            }
            if (!topLevel && ctx.Options.SkipHiddenAndSystem && (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
            {
                ctx.Result.Skipped++;
                return null;
            }

            bool isDir = info is DirectoryInfo;
            string? hostLink = info.LinkTarget;
            (HostManifest? manifest, string key) = FindManifest(ctx, hostPath, isDirectory: false);
            ManifestEntry? entry = null;
            if (manifest is not null && manifest.Entries.TryGetValue(key, out ManifestEntry? found))
            {
                // The manifest entry is only trusted when the object on disk still has the matching kind.
                bool hostIsDir = isDir && hostLink is null;
                if ((found.Type == FsNodeType.Directory) == hostIsDir) entry = found;
            }

            string name = entry?.Name ?? info.Name;
            FsTree.ValidateName(name);
            if ((++ctx.Counter & 0x7F) == 0) ctx.Progress?.Report(new ProgressInfo("import", ctx.Counter, 0, hostPath));

            // ---- directories ----
            if (isDir && hostLink is null)
            {
                FsNode? existingDir = parent.Find(name);
                FsNode dirNode;
                if (existingDir is { IsDirectory: true })
                {
                    dirNode = existingDir;                      // merge into the existing directory
                    entry?.ApplyTo(dirNode.Inode);
                }
                else
                {
                    if (existingDir is not null && !ResolveConflict(ctx, parent, existingDir, ref name)) return null;
                    FsInode di = NewInode(ctx, parent, FsNodeType.Directory, entry, info);
                    dirNode = ctx.Tree.Add(parent, name, di);
                }
                ctx.Result.Directories++;
                ImportChildren(ctx, dirNode, hostPath);
                return dirNode;
            }

            // ---- everything else ----
            FsNodeType type = entry?.Type ?? (hostLink is not null ? FsNodeType.Symlink : FsNodeType.File);
            FsNode? existing = parent.Find(name);
            if (existing is not null && !ResolveConflict(ctx, parent, existing, ref name)) return null;

            var file = info as FileInfo;
            bool unmodified = entry is not null && file is not null && hostLink is null &&
                              entry.HostSize == file.Length && entry.HostTicks == file.LastWriteTimeUtc.Ticks;

            // Hard links are restored while every member of the group is still untouched.
            if (entry is { HardLinkGroup: > 0 } && type == FsNodeType.File && unmodified &&
                ctx.LinkGroups.TryGetValue((manifest!, entry.HardLinkGroup), out FsNode? first) && first.Parent is not null)
            {
                ctx.Result.Files++;
                return ctx.Tree.CreateHardLink(parent, name, first);
            }

            FsInode inode = NewInode(ctx, parent, type, entry, info);
            if (entry is not null && !unmodified && file is not null && hostLink is null && ctx.Options.UseHostTimestamps)
            {
                // The file was edited after extraction: its new time replaces the recorded one.
                try { inode.MTime = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeSeconds(); }
                catch (ArgumentOutOfRangeException) { /* keep the recorded time */ }
            }

            switch (type)
            {
                case FsNodeType.File:
                    if (file is null || hostLink is not null)
                    {
                        ctx.Result.Skipped++;
                        ctx.Result.Warnings.Add($"'{hostPath}' is a link on the host and was skipped.");
                        return null;
                    }
                    if (file.Length > 0)
                    {
                        if (ctx.Options.Snapshot is { } spool)
                        {
                            using FileStream fs = OpenShared(hostPath);
                            inode.Data = spool.Append(fs, file.Length);
                        }
                        else
                        {
                            inode.Data = new HostFileData(hostPath);
                        }
                    }
                    if (entry is null && ctx.Options.DetectExecutables && LooksExecutable(hostPath, file.Length))
                        inode.Mode = ctx.Options.ExecutableMode & 0xFFF;
                    ctx.Result.Files++;
                    ctx.Result.Bytes += file.Length;
                    break;

                case FsNodeType.Symlink:
                    inode.Mode = 0x1FF;
                    inode.LinkTarget = SymlinkTarget(entry, file, hostLink, unmodified);
                    if (string.IsNullOrEmpty(inode.LinkTarget))
                    {
                        ctx.Result.Skipped++;
                        ctx.Result.Warnings.Add($"'{hostPath}': the symbolic link has no target and was skipped.");
                        return null;
                    }
                    ctx.Result.Symlinks++;
                    break;

                default:
                    ctx.Result.Specials++;
                    break;
            }

            FsNode node = ctx.Tree.Add(parent, name, inode);
            if (entry is { HardLinkGroup: > 0 } && type == FsNodeType.File && unmodified)
                ctx.LinkGroups[(manifest!, entry.HardLinkGroup)] = node;
            return node;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Result.Skipped++;
            ctx.Result.Warnings.Add($"'{hostPath}' was skipped: {ex.Message}");
            return null;
        }
        catch (FsException ex) when (ex is not FsConflictException)
        {
            ctx.Result.Skipped++;
            ctx.Result.Warnings.Add($"'{hostPath}' was skipped: {ex.Message}");
            return null;
        }
    }

    private static string? SymlinkTarget(ManifestEntry? entry, FileInfo? file, string? hostLink, bool unmodified)
    {
        if (entry is { Type: FsNodeType.Symlink })
        {
            // An edited placeholder file carries the new target; otherwise the recorded one is exact.
            if (!unmodified && file is not null && hostLink is null && file.Length is > 0 and < 4096)
            {
                byte[] raw = File.ReadAllBytes(file.FullName);
                int end = raw.Length;
                while (end > 0 && raw[end - 1] is (byte)'\n' or (byte)'\r') end--;
                if (end > 0 && raw.AsSpan(0, end).IndexOfAny((byte)0, (byte)'\n') < 0)
                    return PosixName.Decode(raw.AsSpan(0, end));
            }
            return entry.LinkTarget;
        }
        // A native link on the host: keep the target, with POSIX separators.
        return hostLink?.Replace('\\', '/');
    }

    private static FsInode NewInode(Context ctx, FsNode parent, FsNodeType type, ManifestEntry? entry, FileSystemInfo info)
    {
        var inode = new FsInode { Type = type };
        if (entry is not null)
        {
            entry.ApplyTo(inode);
            inode.DevMajor = entry.DevMajor;
            inode.DevMinor = entry.DevMinor;
            return inode;
        }
        ImportOptions o = ctx.Options;
        inode.Mode = (type == FsNodeType.Directory ? o.DefaultDirectoryMode : o.DefaultFileMode) & 0xFFF;
        inode.Uid = o.InheritOwner ? parent.Inode.Uid : o.DefaultUid;
        inode.Gid = o.InheritOwner ? parent.Inode.Gid : o.DefaultGid;
        inode.MTime = ctx.Now;
        if (o.UseHostTimestamps)
        {
            try { inode.MTime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeSeconds(); }
            catch (ArgumentOutOfRangeException) { /* keep "now" */ }
        }
        return inode;
    }

    /// <summary>Applies the conflict policy; false means "do not add". May change the name.</summary>
    private static bool ResolveConflict(Context ctx, FsNode parent, FsNode existing, ref string name)
    {
        switch (ctx.Options.Conflicts)
        {
            case ConflictPolicy.Overwrite:
                ctx.Tree.Remove(existing);
                ctx.Result.Replaced++;
                return true;
            case ConflictPolicy.Rename:
                name = FsTree.MakeUniqueName(parent, name);
                return true;
            case ConflictPolicy.Skip:
                ctx.Result.Skipped++;
                return false;
            default:
                throw new FsConflictException($"'{name}' already exists in '{parent.Path}'.");
        }
    }

    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);

    private static bool LooksExecutable(string path, long length)
    {
        if (length < 4) return false;
        try
        {
            using FileStream fs = OpenShared(path);
            Span<byte> head = stackalloc byte[4];
            if (fs.ReadAtLeast(head, 4, throwOnEndOfStream: false) < 4) return false;
            return (head[0] == 0x7F && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F')
                   || (head[0] == (byte)'#' && head[1] == (byte)'!');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
