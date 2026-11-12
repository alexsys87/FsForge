using System.Security.Cryptography;
using FsForge.Core.Ext;
using FsForge.Core.SquashFs;
using FsForge.Core.Tar;

namespace FsForge.Core.Images;

public enum ImageFormat
{
    SquashFs,
    Ext,
    Tar,
}

/// <summary>How thoroughly a freshly written image is checked before it replaces the old file.</summary>
public enum VerifyLevel
{
    None,

    /// <summary>Read the new image back and compare names, types, sizes, modes and owners.</summary>
    Structure,

    /// <summary>Additionally compare the content of every file.</summary>
    Content,
}

/// <summary>Everything needed to write a tree as an image file.</summary>
public sealed class SaveOptions
{
    public ImageFormat Format { get; set; } = ImageFormat.SquashFs;
    public SquashFsOptions SquashFs { get; set; } = new();
    public ExtOptions Ext { get; set; } = new();
    public TarOptions Tar { get; set; } = new();
    public VerifyLevel Verify { get; set; } = VerifyLevel.Structure;

    /// <summary>Keep the previous version of an overwritten file as "name.bak".</summary>
    public bool KeepBackup { get; set; }

    /// <summary>Folder for temporary data; null means the system temporary folder.</summary>
    public string? TempDirectory { get; set; }

    public SaveOptions Clone() => new()
    {
        Format = Format,
        SquashFs = SquashFs.Clone(),
        Ext = Ext.Clone(),
        Tar = Tar.Clone(),
        Verify = Verify,
        KeepBackup = KeepBackup,
        TempDirectory = TempDirectory,
    };
}

/// <summary>
/// An opened image: the editable tree plus what is known about the file it came from.
/// </summary>
public sealed class ImageDocument : IDisposable
{
    private readonly List<string> _warnings = new();
    private bool _disposed;

    private ImageDocument(FsTree tree, ImageFormat format, string? path)
    {
        Tree = tree;
        Format = format;
        FilePath = path;
        tree.Changed += OnTreeChanged;
    }

    public FsTree Tree { get; }
    public ImageFormat Format { get; }

    /// <summary>Path of the file the document was opened from; null for a new image.</summary>
    public string? FilePath { get; }

    public SquashFsInfo? SquashFs { get; private init; }
    public ExtInfo? Ext { get; private init; }
    public TarInfo? Tar { get; private init; }

    /// <summary>Offset of the filesystem inside the file (firmware containers); 0 for plain images.</summary>
    public long EmbeddedOffset { get; private init; }

    /// <summary>Compression the image file was wrapped in (image.ext4.gz); null for a plain image.</summary>
    public ArchiveCompression? OuterCompression { get; private init; }

    /// <summary>
    /// The file holds more than the filesystem (a firmware container, a sparse or compressed wrapper),
    /// so saving must go to a new file instead of overwriting it.
    /// </summary>
    public bool IsWrapped => EmbeddedOffset > 0 || AndroidSparse || OuterCompression is not null;

    /// <summary>The file is an Android sparse image (simg).</summary>
    public bool AndroidSparse { get; private init; }

    public long FileSize { get; private init; }

    /// <summary>Non-fatal problems found while reading.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    public bool IsModified { get; private set; }

    /// <summary>True once the document has been closed (also after a successful <see cref="Save"/>).</summary>
    public bool IsDisposed => _disposed;

    /// <summary>Raised when <see cref="IsModified"/> changes and after every change of the tree.</summary>
    public event Action? Changed;

    private void OnTreeChanged()
    {
        IsModified = true;
        Changed?.Invoke();
    }

    /// <summary>Marks the document as changed for edits made directly on inodes.</summary>
    public void Touch() => OnTreeChanged();

    public string FormatName => Format switch
    {
        ImageFormat.SquashFs => "SquashFS",
        ImageFormat.Ext => Ext?.Kind.ToString().ToLowerInvariant() ?? "ext4",
        _ => Tar is null ? "tar" : "tar" + ArchiveCodec.DefaultExtension(Tar.Compression)[4..],
    };

    // ----------------------------------------------------------------- open

    /// <summary>Recognises the format of a file without reading its content. Null when it is unknown.</summary>
    public static ImageFormat? Detect(string path)
    {
        using var file = new FileBlockSource(path, FileShare.ReadWrite | FileShare.Delete);
        IBlockSource src = file;
        if (IsAndroidSparse(file)) src = new AndroidSparseSource(file);
        if (SquashFsReader.IsSquashFs(src)) return ImageFormat.SquashFs;
        if (ExtReader.IsExt(src)) return ImageFormat.Ext;
        if (ReferenceEquals(src, file) && TarReader.IsTar(src, out _)) return ImageFormat.Tar;
        return null;
    }

    private static bool IsAndroidSparse(IBlockSource s)
    {
        if (s.Length < 28) return false;
        Span<byte> m = stackalloc byte[4];
        s.ReadExactly(0, m);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(m) == AndroidSparseSource.Magic;
    }

    /// <summary>Opens an image or archive and reads its directory tree. File contents are read on demand.</summary>
    /// <param name="searchEmbedded">Look for a SquashFS inside a firmware container when the file itself is not an image.</param>
    public static ImageDocument Open(string path, string? tempDirectory = null, bool searchEmbedded = true,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        path = Path.GetFullPath(path);
        FileBlockSource file;
        try
        {
            file = new FileBlockSource(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FsException($"The file cannot be opened: {ex.Message}", ex);
        }

        IBlockSource src = file;
        try
        {
            // A compressed file that is not a tar archive is taken for a compressed image
            // (rootfs.ext4.gz, image.squashfs.xz): unpack it to a temporary file and go on with that.
            ArchiveCompression? outer = null;
            byte[] magic = new byte[8];
            int magicLength = file.Read(0, magic);
            if (ArchiveCodec.DetectCompression(magic.AsSpan(0, magicLength)) is { } packed && !TarReader.IsTar(file, out _))
            {
                src = Unpack(file, packed, tempDirectory, progress, ct);
                outer = packed;
            }

            bool sparse = IsAndroidSparse(src);
            if (sparse) src = new AndroidSparseSource(src);
            long offset = 0;

            if (!SquashFsReader.IsSquashFs(src) && !ExtReader.IsExt(src))
            {
                if (!sparse && outer is null && TarReader.IsTar(src, out _))
                {
                    TarReader.Result res = TarReader.Read(src, tempDirectory, progress, ct);
                    var tarDoc = new ImageDocument(res.Tree, ImageFormat.Tar, path) { Tar = res.Info, FileSize = file.Length };
                    tarDoc._warnings.AddRange(res.Warnings);
                    return tarDoc;
                }
                long found = searchEmbedded ? SquashFsReader.FindEmbedded(src, 0, 256L << 20, ct) : -1;
                if (found <= 0)
                {
                    byte[] head = new byte[8];
                    int n = src.Read(0, head);
                    string hint = ArchiveCodec.DetectUnsupported(head.AsSpan(0, n)) is { } c ? $" It looks like {c} compressed data." : "";
                    if (outer is not null) hint = $" It was unpacked from a {outer.ToString()!.ToLowerInvariant()} file, but the content is not a known image.";
                    throw new FsException("The file is not a SquashFS, ext2/3/4 image or tar archive." + hint);
                }
                offset = found;
                src = new SubBlockSource(src, offset, src.Length - offset);
            }

            if (SquashFsReader.IsSquashFs(src))
            {
                SquashFsReader r = SquashFsReader.Open(src);
                FsTree tree = r.ReadTree(progress, ct);
                tree.AttachResource(r);
                var doc = new ImageDocument(tree, ImageFormat.SquashFs, path)
                {
                    SquashFs = r.Info, EmbeddedOffset = offset, AndroidSparse = sparse, FileSize = file.Length, OuterCompression = outer,
                };
                doc._warnings.AddRange(r.Warnings);
                doc.NoteOuterCompression();
                if (r.Info.Compression is SquashCompression.Lzo or SquashCompression.Lzma)
                    doc._warnings.Add($"The image is compressed with {r.Info.Compression.ToString().ToUpperInvariant()}, which can be read but not written. " +
                                      $"Saving uses {SquashFsOptions.FromInfo(r.Info).Compression.ToString().ToLowerInvariant()} unless another method is chosen.");
                if (offset > 0)
                    doc._warnings.Add($"The SquashFS filesystem was found inside the file at offset 0x{offset:X}. " +
                                      "Saving writes the filesystem alone, without the surrounding container.");
                return doc;
            }
            else
            {
                ExtReader r = ExtReader.Open(src);
                FsTree tree = r.ReadTree(progress, ct);
                tree.AttachResource(r);
                var doc = new ImageDocument(tree, ImageFormat.Ext, path)
                {
                    Ext = r.Info, AndroidSparse = sparse, FileSize = file.Length, OuterCompression = outer,
                };
                doc._warnings.AddRange(r.Warnings);
                doc.NoteOuterCompression();
                if (sparse)
                    doc._warnings.Add("The file is an Android sparse image. Saving writes an ordinary (raw) image.");
                return doc;
            }
        }
        catch (Exception ex)
        {
            src.Dispose();
            // Safety net: a damaged image must never take the application down with it.
            if (ex is IndexOutOfRangeException or ArgumentException or OverflowException or InvalidCastException
                or NullReferenceException or OutOfMemoryException or InvalidDataException or EndOfStreamException
                or KeyNotFoundException or InvalidOperationException or NotSupportedException or DivideByZeroException)
                throw new FsUnexpectedException("The image is damaged or uses a layout that is not supported (" + ex.Message + ").", ex);
            throw;
        }
    }

    private void NoteOuterCompression()
    {
        if (OuterCompression is { } c)
            _warnings.Add($"The image was unpacked from a {c.ToString().ToLowerInvariant()} file. Saving writes the image itself, uncompressed.");
    }

    /// <summary>Decompresses a whole file into a temporary one that is deleted when the source is disposed.</summary>
    private static IBlockSource Unpack(FileBlockSource file, ArchiveCompression compression, string? tempDirectory,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        string dir = tempDirectory ?? Path.GetTempPath();
        Directory.CreateDirectory(dir);
        string tmp = Path.Combine(dir, "fsforge-" + Guid.NewGuid().ToString("N") + ".img");
        try
        {
            var raw = new BlockSourceStream(file, 0, file.Length);
            using (Stream input = ArchiveCodec.OpenDecompressor(raw, compression))
            using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
            {
                byte[] buffer = new byte[1 << 17];
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, n);
                    progress?.Report(new ProgressInfo("unpack", raw.Position, file.Length));
                }
            }
            var unpacked = new TempFileSource(tmp);
            file.Dispose();
            return unpacked;
        }
        catch (Exception ex)
        {
            TryDelete(tmp);
            if (ex is InvalidDataException or ZstdSharp.ZstdException or EndOfStreamException)
                throw new FsException($"The {compression.ToString().ToLowerInvariant()} data is damaged: {ex.Message}", ex);
            throw;
        }
    }

    /// <summary>A temporary file that disappears together with the source reading it.</summary>
    private sealed class TempFileSource(string path) : IBlockSource
    {
        private readonly FileBlockSource _inner = new(path);

        public long Length => _inner.Length;
        public int Read(long offset, Span<byte> buffer) => _inner.Read(offset, buffer);

        public void Dispose()
        {
            _inner.Dispose();
            TryDelete(path);
        }
    }

    /// <summary>Creates an empty image that exists only in memory until it is saved.</summary>
    public static ImageDocument CreateNew(ImageFormat format)
    {
        var tree = new FsTree(0x1ED, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return new ImageDocument(tree, format, null);
    }

    // ----------------------------------------------------------------- save

    /// <summary>
    /// Save parameters that reproduce the source image as closely as possible when the format is kept,
    /// and sensible defaults when it is converted.
    /// </summary>
    public SaveOptions CreateSaveOptions(ImageFormat? format = null, SaveOptions? defaults = null)
    {
        SaveOptions o = defaults?.Clone() ?? new SaveOptions();
        o.Format = format ?? Format;
        if (SquashFs is not null) o.SquashFs = SquashFsOptions.FromInfo(SquashFs);
        if (Ext is not null) o.Ext = ExtOptions.FromInfo(Ext);
        if (Tar is not null) o.Tar = TarOptions.FromInfo(Tar);
        return o;
    }

    /// <summary>Writes a tree to a stream in the selected format; returns the writer's warnings.</summary>
    public static IReadOnlyList<string> WriteTo(FsTree tree, Stream output, SaveOptions options,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default) => options.Format switch
    {
        ImageFormat.SquashFs => SquashFsWriter.Write(tree, output, options.SquashFs, progress, ct),
        ImageFormat.Ext => ExtWriter.Write(tree, output, options.Ext, progress, ct),
        _ => TarWriter.Write(tree, output, options.Tar, null, progress, ct),
    };

    /// <summary>Outcome of <see cref="Save"/>.</summary>
    public sealed record SaveResult(ImageDocument Document, IReadOnlyList<string> Warnings, long BytesWritten);

    /// <summary>
    /// Writes the image to <paramref name="path"/> and reopens it. The data goes to a temporary file
    /// first and is verified there, so a failure never damages an existing file. On success this
    /// document is disposed and the returned one takes its place.
    /// </summary>
    public SaveResult Save(string path, SaveOptions options, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        path = Path.GetFullPath(path);
        string tmp = path + ".fsforge-tmp";
        var warnings = new List<string>();
        bool samePath = FilePath is not null && string.Equals(FilePath, path,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        // Fail early, before the long write, when the target obviously cannot be replaced.
        if (Directory.Exists(path)) throw new FsException($"'{path}' is a folder.");
        if (File.Exists(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                throw new FsException($"'{path}' is read-only. Remove the attribute or choose another name.");
            if (!samePath) EnsureNotLocked(path);
        }

        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20))
            {
                warnings.AddRange(WriteTo(Tree, fs, options, progress, ct));
                fs.Flush(flushToDisk: true);
            }

            if (options.Verify != VerifyLevel.None)
            {
                ct.ThrowIfCancellationRequested();
                using ImageDocument check = Open(tmp, options.TempDirectory, searchEmbedded: false, null, ct);
                List<string> problems = TreeComparer.Compare(Tree, check.Tree, TreeComparer.RulesFor(options),
                    options.Verify == VerifyLevel.Content, progress, ct);
                if (problems.Count > 0)
                {
                    string list = string.Join(Environment.NewLine, problems.Take(10));
                    throw new FsException(
                        "The written image does not match the source and was discarded; the original file is untouched." +
                        Environment.NewLine + list + (problems.Count > 10 ? Environment.NewLine + $"... and {problems.Count - 10} more." : ""));
                }
            }
        }
        catch (Exception ex)
        {
            TryDelete(tmp);
            if (ex is IOException or UnauthorizedAccessException)
                throw new FsException($"The image could not be written: {ex.Message}", ex);
            throw;
        }

        long written = new FileInfo(tmp).Length;
        if (samePath)
        {
            // The file being replaced is our own source: it has to be closed first. Everything the new
            // image needs is already in the verified temporary file.
            Dispose();
            try
            {
                Swap(tmp, path, options.KeepBackup);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new FsException(
                    $"The new image was written to '{tmp}' but could not replace '{path}': {ex.Message} " +
                    "Rename the temporary file manually.", ex);
            }
        }
        else
        {
            try
            {
                Swap(tmp, path, options.KeepBackup);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(tmp);
                throw new FsException($"The image could not be saved as '{path}': {ex.Message}", ex);
            }
            Dispose();
        }

        ImageDocument reopened = Open(path, options.TempDirectory, searchEmbedded: false, null, CancellationToken.None);
        return new SaveResult(reopened, warnings, written);
    }

    private static void Swap(string tmp, string path, bool keepBackup)
    {
        if (keepBackup && File.Exists(path)) File.Move(path, path + ".bak", overwrite: true);
        File.Move(tmp, path, overwrite: true);
    }

    private static void EnsureNotLocked(string path)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FsException($"'{path}' cannot be replaced: {ex.Message}", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* nothing else to do */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Tree.Changed -= OnTreeChanged;
        Tree.Dispose();
    }
}

/// <summary>Compares two trees; used to verify a written image against its source.</summary>
public static class TreeComparer
{
    /// <summary>Differences that are expected for a given target format.</summary>
    public sealed class Rules
    {
        public bool IgnoreOwners, IgnoreXattrs, IgnoreSockets, IgnoreLostFound, IgnoreRootAttributes;

        /// <summary>Timestamps outside this range cannot be represented and are not compared.</summary>
        public long MinTime = long.MinValue, MaxTime = long.MaxValue;
    }

    public static Rules RulesFor(SaveOptions o) => o.Format switch
    {
        ImageFormat.SquashFs => new Rules
        {
            IgnoreOwners = o.SquashFs.AllRoot,
            IgnoreXattrs = true,                    // only the user/trusted/security namespaces exist in SquashFS
            MinTime = 0,
            MaxTime = uint.MaxValue,
        },
        ImageFormat.Ext => new Rules
        {
            IgnoreOwners = o.Ext.AllRoot,
            IgnoreXattrs = true,                    // attributes that do not fit a block are dropped with a warning
            IgnoreLostFound = true,
            MinTime = o.Ext.InodeSize >= 256 ? -0x80000000L : int.MinValue,
            MaxTime = o.Ext.InodeSize >= 256 ? 0x37FFFFFFFL : int.MaxValue,
        },
        _ => new Rules
        {
            IgnoreOwners = o.Tar.AllRoot,
            IgnoreXattrs = true,
            IgnoreSockets = true,
            IgnoreRootAttributes = !o.Tar.IncludeRootEntry,
        },
    };

    /// <summary>Returns a description of every difference (empty when the trees match).</summary>
    public static List<string> Compare(FsTree expected, FsTree actual, Rules rules, bool content,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var problems = new List<string>();
        long total = 0, done = 0;
        if (content) total = expected.GetStats().DataBytes;
        var compared = new HashSet<FsInode>(ReferenceEqualityComparer.Instance);
        var linkMap = new Dictionary<FsInode, FsInode>(ReferenceEqualityComparer.Instance);
        byte[] bufA = new byte[1 << 16], bufB = new byte[1 << 16];

        void Visit(FsNode a, FsNode b)
        {
            ct.ThrowIfCancellationRequested();
            if (problems.Count > 200) return;
            FsInode x = a.Inode, y = b.Inode;
            string path = a.Path;
            bool root = a.IsRoot;
            if (x.Type != y.Type)
            {
                problems.Add($"{path}: type is {y.Type}, expected {x.Type}.");
                return;
            }
            if (!(root && rules.IgnoreRootAttributes))
            {
                if (x.Type != FsNodeType.Symlink && (x.Mode & 0xFFF) != (y.Mode & 0xFFF))
                    problems.Add($"{path}: mode is {UnixMode.ToOctal(y.Mode)}, expected {UnixMode.ToOctal(x.Mode)}.");
                if (!rules.IgnoreOwners && (x.Uid != y.Uid || x.Gid != y.Gid))
                    problems.Add($"{path}: owner is {y.Uid}:{y.Gid}, expected {x.Uid}:{x.Gid}.");
                if (x.MTime != y.MTime && x.MTime >= rules.MinTime && x.MTime <= rules.MaxTime)
                    problems.Add($"{path}: modification time is {y.MTime}, expected {x.MTime}.");
            }
            if (!rules.IgnoreXattrs && !XattrsEqual(x.XAttrs, y.XAttrs)) problems.Add($"{path}: extended attributes differ.");

            switch (x.Type)
            {
                case FsNodeType.Symlink:
                    if (x.LinkTarget != y.LinkTarget) problems.Add($"{path}: link target differs.");
                    break;
                case FsNodeType.CharDevice or FsNodeType.BlockDevice:
                    if (x.DevMajor != y.DevMajor || x.DevMinor != y.DevMinor)
                        problems.Add($"{path}: device is {y.DevMajor}:{y.DevMinor}, expected {x.DevMajor}:{x.DevMinor}.");
                    break;
                case FsNodeType.File:
                    if (x.Size != y.Size)
                    {
                        problems.Add($"{path}: size is {y.Size}, expected {x.Size}.");
                        break;
                    }
                    // Hard links must stay hard links of the same group.
                    if (x.LinkCount > 1)
                    {
                        if (linkMap.TryGetValue(x, out FsInode? mapped))
                        {
                            if (!ReferenceEquals(mapped, y)) problems.Add($"{path}: the hard link was not preserved.");
                        }
                        else
                        {
                            linkMap[x] = y;
                        }
                    }
                    if (content && compared.Add(x) && x.Size > 0)
                    {
                        if (!SameContent(x, y, bufA, bufB, ref done, total, path, progress, ct))
                            problems.Add($"{path}: the file content differs.");
                    }
                    break;
                case FsNodeType.Directory:
                    foreach (FsNode ca in a.SortedChildren())
                    {
                        if (rules.IgnoreSockets && ca.Type == FsNodeType.Socket) continue;
                        FsNode? cb = b.Find(ca.Name);
                        if (cb is null) problems.Add($"{ca.Path}: missing in the written image.");
                        else Visit(ca, cb);
                    }
                    foreach (FsNode cb in b.Children)
                    {
                        if (a.Find(cb.Name) is not null) continue;
                        if (rules.IgnoreLostFound && root && cb.Name == "lost+found" && cb.IsDirectory) continue;
                        problems.Add($"{cb.Path}: unexpected entry in the written image.");
                    }
                    break;
            }
        }

        Visit(expected.Root, actual.Root);
        return problems;
    }

    private static bool XattrsEqual(SortedDictionary<string, byte[]>? a, SortedDictionary<string, byte[]>? b)
    {
        int ca = a?.Count ?? 0, cb = b?.Count ?? 0;
        if (ca != cb) return false;
        if (ca == 0) return true;
        foreach ((string k, byte[] v) in a!)
            if (!b!.TryGetValue(k, out byte[]? w) || !v.AsSpan().SequenceEqual(w)) return false;
        return true;
    }

    private static bool SameContent(FsInode x, FsInode y, byte[] bufA, byte[] bufB, ref long done, long total, string path,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        using Stream sa = x.Data!.OpenRead();
        using Stream sb = y.Data!.OpenRead();
        long left = x.Size;
        while (left > 0)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(bufA.Length, left);
            sa.ReadExactly(bufA, 0, n);
            sb.ReadExactly(bufB, 0, n);
            if (!bufA.AsSpan(0, n).SequenceEqual(bufB.AsSpan(0, n))) return false;
            left -= n;
            done += n;
            progress?.Report(new ProgressInfo("verify", done, total, path));
        }
        return true;
    }

    /// <summary>SHA-256 of a file's content as lower-case hex.</summary>
    public static string Hash(FsInode inode)
    {
        if (inode.Data is null) return Convert.ToHexString(SHA256.HashData(ReadOnlySpan<byte>.Empty)).ToLowerInvariant();
        using Stream s = inode.Data.OpenRead();
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }
}
