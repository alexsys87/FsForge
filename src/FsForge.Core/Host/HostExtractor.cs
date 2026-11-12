namespace FsForge.Core.Host;

/// <summary>What to do with symbolic links when a tree is unpacked into a Windows folder.</summary>
public enum SymlinkExtractMode
{
    /// <summary>Write a small text file that holds the link target; the manifest remembers it is a link.</summary>
    Placeholder,

    /// <summary>Create a real symbolic link where the system allows it, otherwise fall back to a placeholder.</summary>
    Native,

    /// <summary>Do not create anything on disk (the manifest still remembers the link).</summary>
    Skip,
}

public sealed class ExtractOptions
{
    /// <summary>Record Unix metadata in ".fsforge-meta.json" so that the folder can be packed again without loss.</summary>
    public bool WriteManifest { get; set; } = true;

    public SymlinkExtractMode Symlinks { get; set; } = SymlinkExtractMode.Placeholder;

    /// <summary>Create empty stand-in files for device nodes, FIFOs and sockets.</summary>
    public bool SpecialFilePlaceholders { get; set; } = true;

    /// <summary>Set the last-write time of extracted files from the image.</summary>
    public bool RestoreTimestamps { get; set; } = true;

    /// <summary>Replace files that already exist in the target folder.</summary>
    public bool OverwriteExisting { get; set; } = true;

    public ExtractOptions Clone() => (ExtractOptions)MemberwiseClone();
}

public sealed class ExtractResult
{
    public long Files, Directories, Symlinks, Specials, Bytes, Renamed, Skipped;
    public List<string> Warnings { get; } = new();
}

/// <summary>Unpacks nodes of a tree into a folder of the host filesystem.</summary>
public static class HostExtractor
{
    private static readonly DateTime MinTime = new(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MaxTime = new(9000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Context
    {
        public required ExtractOptions Options;
        public required ExtractResult Result;
        public required HostManifest Manifest;
        public required string Root;
        public IProgress<ProgressInfo>? Progress;
        public CancellationToken Ct;
        public long Total, Done;
        public readonly Dictionary<FsInode, int> LinkGroups = new(ReferenceEqualityComparer.Instance);
        public readonly byte[] Buffer = new byte[1 << 17];
        public bool NativeLinksFailed;
    }

    /// <summary>
    /// Extracts every node into <paramref name="targetDirectory"/> under its own name. Passing the root
    /// node extracts its children directly into the folder.
    /// </summary>
    public static ExtractResult Extract(IReadOnlyList<FsNode> nodes, string targetDirectory, ExtractOptions options,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        targetDirectory = Path.GetFullPath(targetDirectory);
        Directory.CreateDirectory(targetDirectory);
        var ctx = new Context
        {
            Options = options,
            Result = new ExtractResult(),
            Manifest = (options.WriteManifest ? HostManifest.TryLoad(targetDirectory) : null) ?? new HostManifest(),
            Root = targetDirectory,
            Progress = progress,
            Ct = ct,
        };

        var counted = new HashSet<FsInode>(ReferenceEqualityComparer.Instance);
        foreach (FsNode start in nodes)
            foreach (FsNode n in FsTree.Walk(start))
                if (n.Type == FsNodeType.File && counted.Add(n.Inode)) ctx.Total += n.Inode.Size;

        // Names already present in the folder take part in collision checks.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { HostManifest.FileName };
        var known = new Dictionary<string, string>(StringComparer.Ordinal);    // real name -> host name from an earlier extraction
        foreach ((string key, ManifestEntry e) in ctx.Manifest.Entries)
        {
            if (key.Length == 0 || key.Contains('/')) continue;
            used.Add(key);
            known[e.Name ?? key] = key;
        }

        try
        {
            foreach (FsNode node in nodes)
            {
                if (node.IsRoot)
                {
                    ctx.Manifest.Entries[""] = ManifestEntry.FromInode(node.Inode);
                    ExtractChildren(ctx, node, targetDirectory, "", used, known);
                    SetDirectoryTime(ctx, targetDirectory, node.Inode.MTime);
                }
                else
                {
                    ExtractNode(ctx, node, targetDirectory, "", used, known);
                }
            }
        }
        finally
        {
            // Keep the manifest consistent with whatever reached the disk, even after a cancellation.
            if (options.WriteManifest)
            {
                try { ctx.Manifest.Save(targetDirectory); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ctx.Result.Warnings.Add("The metadata file could not be written: " + ex.Message);
                }
            }
        }
        progress?.Report(new ProgressInfo("done", ctx.Total, ctx.Total));
        return ctx.Result;
    }

    private static void ExtractChildren(Context ctx, FsNode dir, string hostDir, string relDir,
        HashSet<string>? used = null, Dictionary<string, string>? known = null)
    {
        // The manifest name is reserved in every folder so that a real file of that name never shadows one.
        used ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase) { HostManifest.FileName };
        if (known is null)
        {
            // Reuse the host names chosen by an earlier extraction into the same folder.
            known = new Dictionary<string, string>(StringComparer.Ordinal);
            string prefix = relDir + "/";
            foreach ((string key, ManifestEntry e) in ctx.Manifest.Entries)
            {
                if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string rest = key[prefix.Length..];
                if (rest.Length == 0 || rest.Contains('/')) continue;
                used.Add(rest);
                known[e.Name ?? rest] = rest;
            }
        }
        foreach (FsNode child in dir.SortedChildren())
            ExtractNode(ctx, child, hostDir, relDir, used, known);
    }

    private static void ExtractNode(Context ctx, FsNode node, string hostDir, string relDir,
        HashSet<string> used, Dictionary<string, string> known)
    {
        ctx.Ct.ThrowIfCancellationRequested();
        if (!known.TryGetValue(node.Name, out string? hostName))
        {
            hostName = HostNames.MakeUnique(node.Name, used);
            known[node.Name] = hostName;
        }
        if (hostName != node.Name) ctx.Result.Renamed++;
        string hostPath = Path.Combine(hostDir, hostName);
        string rel = relDir.Length == 0 ? hostName : relDir + "/" + hostName;
        FsInode inode = node.Inode;
        ManifestEntry entry = ManifestEntry.FromInode(inode);
        if (hostName != node.Name) entry.Name = node.Name;

        try
        {
            switch (inode.Type)
            {
                case FsNodeType.Directory:
                    if (File.Exists(hostPath))
                    {
                        Skip(ctx, node, "a file with the same name is in the way");
                        return;
                    }
                    Directory.CreateDirectory(hostPath);
                    ctx.Manifest.Entries[rel] = entry;
                    ctx.Result.Directories++;
                    ExtractChildren(ctx, node, hostPath, rel);
                    SetDirectoryTime(ctx, hostPath, inode.MTime);
                    return;

                case FsNodeType.File:
                    if (!PrepareFile(ctx, node, hostPath)) return;
                    if (inode.LinkCount > 1)
                    {
                        if (!ctx.LinkGroups.TryGetValue(inode, out int group))
                            ctx.LinkGroups[inode] = group = ++ctx.Manifest.LastHardLinkGroup;
                        entry.HardLinkGroup = group;
                    }
                    WriteFile(ctx, node, hostPath);
                    Stamp(ctx, hostPath, inode.MTime, entry);
                    ctx.Result.Files++;
                    break;

                case FsNodeType.Symlink:
                    ctx.Result.Symlinks++;
                    if (ctx.Options.Symlinks == SymlinkExtractMode.Skip) break;
                    if (!PrepareFile(ctx, node, hostPath)) return;
                    if (ctx.Options.Symlinks == SymlinkExtractMode.Native && !ctx.NativeLinksFailed && TryNativeLink(ctx, hostPath, inode.LinkTarget ?? ""))
                        break;
                    File.WriteAllBytes(hostPath, PosixName.Encode(inode.LinkTarget ?? ""));
                    Stamp(ctx, hostPath, inode.MTime, entry);
                    break;

                default:
                    ctx.Result.Specials++;
                    if (!ctx.Options.SpecialFilePlaceholders) break;
                    if (!PrepareFile(ctx, node, hostPath)) return;
                    File.WriteAllBytes(hostPath, Array.Empty<byte>());
                    Stamp(ctx, hostPath, inode.MTime, entry);
                    break;
            }
            ctx.Manifest.Entries[rel] = entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            Skip(ctx, node, ex.Message);
        }
    }

    private static void Skip(Context ctx, FsNode node, string reason)
    {
        ctx.Result.Skipped++;
        ctx.Result.Warnings.Add($"'{node.Path}' was not extracted: {reason}");
    }

    /// <summary>Makes room for a file at <paramref name="hostPath"/>; false when the node has to be skipped.</summary>
    private static bool PrepareFile(Context ctx, FsNode node, string hostPath)
    {
        if (Directory.Exists(hostPath) && new DirectoryInfo(hostPath).LinkTarget is null)
        {
            Skip(ctx, node, "a folder with the same name is in the way");
            return false;
        }
        var info = new FileInfo(hostPath);
        if (!info.Exists && info.LinkTarget is null) return true;
        if (!ctx.Options.OverwriteExisting)
        {
            Skip(ctx, node, "the file already exists");
            return false;
        }
        if (info.Exists && (info.Attributes & FileAttributes.ReadOnly) != 0) info.Attributes &= ~FileAttributes.ReadOnly;
        info.Delete();
        return true;
    }

    private static void WriteFile(Context ctx, FsNode node, string hostPath)
    {
        FsInode inode = node.Inode;
        using var fs = new FileStream(hostPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        if (inode.Data is null || inode.Size == 0) return;
        using Stream s = inode.Data.OpenRead();
        long left = inode.Size;
        while (left > 0)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            int n = s.Read(ctx.Buffer, 0, (int)Math.Min(ctx.Buffer.Length, left));
            if (n <= 0) throw new FsException($"'{node.Path}': unexpected end of the file data.");
            fs.Write(ctx.Buffer, 0, n);
            left -= n;
            ctx.Done += n;
            ctx.Result.Bytes += n;
            ctx.Progress?.Report(new ProgressInfo("extract", ctx.Done, ctx.Total, node.Path));
        }
    }

    private static DateTime ToHostTime(long unixSeconds)
    {
        DateTime t;
        try
        {
            t = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            t = unixSeconds < 0 ? MinTime : MaxTime;
        }
        return t < MinTime ? MinTime : t > MaxTime ? MaxTime : t;
    }

    /// <summary>Applies the timestamp and records what the host file looks like right now.</summary>
    private static void Stamp(Context ctx, string hostPath, long mtime, ManifestEntry entry)
    {
        var info = new FileInfo(hostPath);
        if (ctx.Options.RestoreTimestamps)
        {
            try { info.LastWriteTimeUtc = ToHostTime(mtime); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException) { /* keep the current time */ }
        }
        info.Refresh();
        entry.HostSize = info.Length;
        entry.HostTicks = info.LastWriteTimeUtc.Ticks;
    }

    private static void SetDirectoryTime(Context ctx, string hostPath, long mtime)
    {
        if (!ctx.Options.RestoreTimestamps) return;
        try { Directory.SetLastWriteTimeUtc(hostPath, ToHostTime(mtime)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException) { /* cosmetic only */ }
    }

    private static bool TryNativeLink(Context ctx, string hostPath, string target)
    {
        try
        {
            string hostTarget = OperatingSystem.IsWindows() ? target.Replace('/', '\\') : target;
            File.CreateSymbolicLink(hostPath, hostTarget);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Usually a missing privilege (Windows needs Developer Mode or elevation): stop trying.
            ctx.NativeLinksFailed = true;
            ctx.Result.Warnings.Add("Symbolic links could not be created on this system (" + ex.Message.TrimEnd('.') +
                                    "); text placeholders were written instead.");
            return false;
        }
    }
}
