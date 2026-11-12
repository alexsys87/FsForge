using FsForge.Core.Host;

namespace FsForge.Core;

/// <summary>Moves the content of one tree into a directory of another (importing an archive into an image).</summary>
public static class TreeMerger
{
    public sealed class Result
    {
        public long Added, Merged, Replaced, Skipped;
        public List<FsNode> TopLevel { get; } = new();
    }

    /// <summary>
    /// Transfers every child of the source root into <paramref name="targetDir"/>. Directories that
    /// exist on both sides are merged; other name clashes follow <paramref name="policy"/>. The source
    /// tree must not be used afterwards: its resources (temporary spool, source file) are handed
    /// over to <paramref name="target"/> and live as long as it does.
    /// </summary>
    public static Result Merge(FsTree target, FsNode targetDir, FsTree source, ConflictPolicy policy, CancellationToken ct = default)
    {
        if (!targetDir.IsDirectory) throw new FsException($"'{targetDir.Path}' is not a directory.");
        var result = new Result();
        // Inodes are moved, not copied, so hard links inside the source stay hard links.
        var linkCounts = new HashSet<FsInode>(ReferenceEqualityComparer.Instance);
        using (target.SuspendNotifications())
        {
            foreach (FsNode child in source.Root.SortedChildren())
            {
                FsNode? added = MoveInto(target, targetDir, child, policy, result, linkCounts, ct);
                if (added is not null) result.TopLevel.Add(added);
            }
            target.NotifyChanged();
        }
        target.AttachResource(source);
        return result;
    }

    private static FsNode? MoveInto(FsTree target, FsNode parent, FsNode src, ConflictPolicy policy, Result result,
        HashSet<FsInode> seen, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string name = src.Name;
        FsNode? existing = parent.Find(name);

        if (existing is { IsDirectory: true } && src.IsDirectory)
        {
            // Merge: the incoming directory's attributes win, as when an archive is unpacked over a tree.
            FsInode e = existing.Inode, s = src.Inode;
            e.Mode = s.Mode;
            e.Uid = s.Uid;
            e.Gid = s.Gid;
            e.MTime = s.MTime;
            if (s.XAttrs is not null) e.XAttrs = s.XAttrs;
            result.Merged++;
            foreach (FsNode child in src.SortedChildren()) MoveInto(target, existing, child, policy, result, seen, ct);
            return existing;
        }

        if (existing is not null)
        {
            switch (policy)
            {
                case ConflictPolicy.Skip:
                    result.Skipped++;
                    return null;
                case ConflictPolicy.Rename:
                    name = FsTree.MakeUniqueName(parent, name);
                    break;
                case ConflictPolicy.Fail:
                    throw new FsConflictException($"'{name}' already exists in '{parent.Path}'.");
                default:
                    target.Remove(existing);
                    result.Replaced++;
                    break;
            }
        }

        // The inode keeps its identity; its link count restarts in the new tree.
        if (seen.Add(src.Inode)) src.Inode.LinkCount = 0;
        FsNode node = target.Add(parent, name, src.Inode);
        result.Added++;
        if (src.IsDirectory)
            foreach (FsNode child in src.SortedChildren()) MoveInto(target, node, child, policy, result, seen, ct);
        return node;
    }
}
