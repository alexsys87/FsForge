using System.Security.Cryptography;
using FsForge.Core.Erofs;
using FsForge.Core.Ext;
using FsForge.Core.SquashFs;
using FsForge.Core.Tar;
using FsForge.Core.Fat;
using FsForge.Core.Jffs2;
using FsForge.Core.Ubifs;

namespace FsForge.Core.Images;

public enum ImageFormat
{
    SquashFs,
    Ext,
    Tar,

    /// <summary>UBIFS, the filesystem of raw NAND and NOR flash; alone or in a volume of a UBI image.</summary>
    Ubifs,

    /// <summary>JFFS2, the log-structured filesystem of NOR (and older NAND) flash; little- or big-endian.</summary>
    Jffs2,

    /// <summary>FAT12/16/32 with long names: boot partitions of SD cards, EFI system partitions, USB sticks.</summary>
    Fat,

    /// <summary>EROFS, the read-only filesystem with fixed-size compressed clusters (Android, container images, live systems).</summary>
    Erofs,
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
    public UbifsOptions Ubifs { get; set; } = new();
    public Jffs2Options Jffs2 { get; set; } = new();
    public FatOptions Fat { get; set; } = new();
    public ErofsOptions Erofs { get; set; } = new();

    /// <summary>Geometry and volume of the UBI image that <see cref="NewUbi"/> asks for.</summary>
    public UbiOptions Ubi { get; set; } = new();

    /// <summary>
    /// Wrap the filesystem (UBIFS or SquashFS) into a new UBI image with one volume, as ubinize does.
    /// Has no effect when the document came from a UBI image that is being kept (<see cref="KeepContainer"/>).
    /// </summary>
    public bool NewUbi { get; set; }

    public VerifyLevel Verify { get; set; } = VerifyLevel.Structure;

    /// <summary>
    /// When the filesystem was opened from a partition of a disk image, write the whole disk image
    /// again - partition table and all other partitions untouched - instead of the filesystem alone.
    /// Likewise for a volume of a UBI image: the UBI image is written again with the other volumes as they were.
    /// Has no effect on other documents and on tar archives.
    /// </summary>
    public bool KeepContainer { get; set; } = true;

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
        Ubifs = Ubifs.Clone(),
        Jffs2 = Jffs2.Clone(),
        Fat = Fat.Clone(),
        Erofs = Erofs.Clone(),
        Ubi = Ubi.Clone(),
        NewUbi = NewUbi,
        Verify = Verify,
        KeepContainer = KeepContainer,
        KeepBackup = KeepBackup,
        TempDirectory = TempDirectory,
    };
}

/// <summary>The UBI image and the volume a filesystem was opened from.</summary>
public sealed record UbiContainer(UbiImage Image, UbiVolumeInfo Volume);

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
    public UbifsInfo? Ubifs { get; private init; }
    public Jffs2Info? Jffs2 { get; private init; }
    public FatInfo? Fat { get; private init; }
    public ErofsInfo? Erofs { get; private init; }

    /// <summary>
    /// The UBI image and the volume the filesystem was opened from; null when the file is not a UBI image.
    /// Such a document is saved as the whole UBI image again, with the other volumes as they were.
    /// </summary>
    public UbiContainer? Ubi { get; private init; }

    /// <summary>
    /// Offset of the filesystem inside the (unpacked) file: the start of its partition, or the place
    /// where it was found in a firmware container. 0 for plain images.
    /// </summary>
    public long EmbeddedOffset { get; private init; }

    /// <summary>
    /// The disk image and the partition the filesystem was opened from; null when the file is not a
    /// partitioned disk image. Such a document is saved as the whole disk again.
    /// </summary>
    public DiskContainer? Container { get; private init; }

    /// <summary>
    /// Compression the image file is wrapped in (sdcard.img.gz, rootfs.ext4.xz); null for a plain file.
    /// Saving under a name with the same extension compresses the image again.
    /// </summary>
    public ArchiveCompression? OuterCompression { get; private init; }

    /// <summary>The file is an Android sparse image (simg).</summary>
    public bool AndroidSparse { get; private init; }

    /// <summary>
    /// The file holds more than the filesystem and cannot be rebuilt around a changed one (a firmware
    /// container with its own headers and checksums, an Android sparse image). Saving writes the
    /// filesystem alone (or the UBI image alone), so it must go to a new file.
    /// </summary>
    public bool SavesFilesystemOnly => AndroidSparse || (EmbeddedOffset > 0 && Container is null);

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
        ImageFormat.Ubifs => "UBIFS",
        ImageFormat.Jffs2 => "JFFS2",
        ImageFormat.Fat => Fat?.TypeName ?? "FAT",
        ImageFormat.Erofs => "EROFS",
        _ => Tar is null ? "tar" : "tar" + ArchiveCodec.DefaultExtension(Tar.Compression)[4..],
    };

    // ----------------------------------------------------------------- open

    /// <summary>How much of a compressed file <see cref="Detect"/> unpacks at most to look inside.</summary>
    private const int SniffLimit = 96 << 20;

    /// <summary>
    /// Recognises the format of a file without reading its content. For a disk image this is the
    /// format of the partition that <see cref="Open"/> picks by default; for a compressed disk image
    /// whose partitions start beyond the first megabytes it is a guess. Null when the file is not
    /// something <see cref="Open"/> is likely to accept.
    /// </summary>
    public static ImageFormat? Detect(string path)
    {
        using var file = new FileBlockSource(path, FileShare.ReadWrite | FileShare.Delete);
        IBlockSource src = file;
        if (IsAndroidSparse(file)) src = new AndroidSparseSource(file);
        if (FilesystemAt(src) is { } direct) return direct;
        if (UbiImage.IsUbi(src))
        {
            try
            {
                return UbiImage.Read(src).PickDefault() is { } volume ? FormatOf(volume) : null;
            }
            catch (FsException)
            {
                return null;
            }
        }
        if (ReferenceEquals(src, file) && TarReader.IsTar(src, out _)) return ImageFormat.Tar;
        if (PartitionTable.Read(src)?.PickDefault() is { } part) return FormatOf(part);
        if (!ReferenceEquals(src, file)) return null;

        byte[] magic = new byte[8];
        int n = file.Read(0, magic);
        if (ArchiveCodec.DetectCompression(magic.AsSpan(0, n)) is not { } packed) return null;
        try
        {
            return SniffCompressed(file, packed);
        }
        catch (Exception ex) when (ex is InvalidDataException or ZstdSharp.ZstdException or EndOfStreamException or FsException or IOException)
        {
            return null;
        }
    }

    private static ImageFormat? FilesystemAt(IBlockSource src) =>
        SquashFsReader.IsSquashFs(src) ? ImageFormat.SquashFs
        : UbifsReader.IsUbifs(src) ? ImageFormat.Ubifs
        : ErofsReader.IsErofs(src) ? ImageFormat.Erofs
        : ExtReader.IsExt(src) ? ImageFormat.Ext
        : FatReader.IsFat(src) ? ImageFormat.Fat
        : Jffs2Reader.IsJffs2(src) ? ImageFormat.Jffs2 : null;

    private static ImageFormat FormatOf(UbiVolumeInfo volume) => volume.Content switch
    {
        PartitionContent.SquashFs => ImageFormat.SquashFs,
        PartitionContent.Erofs => ImageFormat.Erofs,
        PartitionContent.Ext => ImageFormat.Ext,
        _ => ImageFormat.Ubifs,
    };

    private static ImageFormat FormatOf(PartitionInfo part) => part.Content switch
    {
        PartitionContent.SquashFs => ImageFormat.SquashFs,
        PartitionContent.Erofs => ImageFormat.Erofs,
        PartitionContent.Fat => ImageFormat.Fat,
        _ => ImageFormat.Ext,
    };

    /// <summary>Unpacks only as much of a compressed image as it takes to see what it is.</summary>
    private static ImageFormat? SniffCompressed(FileBlockSource file, ArchiveCompression compression)
    {
        using Stream input = ArchiveCodec.OpenDecompressor(new BlockSourceStream(file, 0, file.Length), compression);
        var head = new MemoryStream();
        byte[] buffer = new byte[1 << 16];

        bool Fill(long target)
        {
            while (head.Length < target)
            {
                int n = input.Read(buffer, 0, buffer.Length);
                if (n <= 0) return false;
                head.Write(buffer, 0, n);
            }
            return true;
        }

        Fill(1 << 20);
        var first = new DiskHead(head.ToArray());
        if (FilesystemAt(first) is { } direct) return direct;
        // The volumes of a UBI image are spread over the whole of it; UBIFS is the usual content.
        if (UbiImage.IsUbi(first)) return ImageFormat.Ubifs;
        if (PartitionTable.Read(first) is not { } table) return null;
        // Unpack up to the beginning of the last partition that is within reach.
        Fill(table.Partitions.Where(p => p.Offset < SniffLimit).Select(p => p.Offset + 4096).DefaultIfEmpty(0).Max());
        if (PartitionTable.Read(new DiskHead(head.ToArray()))?.PickDefault() is { } part) return FormatOf(part);
        // A root filesystem that starts further on (behind a large boot partition) is out of reach here.
        // Such a disk is still most likely an image to open; ext4 is the usual content.
        return table.Partitions.Any(p => p.Offset >= SniffLimit) ? ImageFormat.Ext : null;
    }

    /// <summary>The beginning of a disk standing in for the whole of it: everything further on reads as zeros.</summary>
    private sealed class DiskHead(byte[] data) : IBlockSource
    {
        public long Length => 1L << 46;

        public int Read(long offset, Span<byte> buffer)
        {
            if (offset < 0 || offset >= Length) return 0;
            int n = (int)Math.Min(buffer.Length, Length - offset);
            buffer = buffer[..n];
            buffer.Clear();
            if (offset < data.Length)
                data.AsSpan((int)offset, (int)Math.Min(n, data.Length - offset)).CopyTo(buffer);
            return n;
        }

        public void Dispose() { }
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
    /// <param name="partition">Number of the partition to open in a disk image; 0 lets the image or <paramref name="choosePartition"/> decide.</param>
    /// <param name="choosePartition">
    /// Called when a disk image has several partitions with an editable filesystem. Returning null cancels the
    /// operation. Without it the largest such partition is opened.
    /// </param>
    /// <param name="volume">Name or number of the volume to open in a UBI image; null lets the image or <paramref name="chooseVolume"/> decide.</param>
    /// <param name="chooseVolume">
    /// Called when a UBI image has several volumes with a readable filesystem. Returning null cancels the
    /// operation. Without it the volume that holds the most data is opened.
    /// </param>
    public static ImageDocument Open(string path, string? tempDirectory = null, bool searchEmbedded = true,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default,
        int partition = 0, Func<PartitionTable, PartitionInfo?>? choosePartition = null,
        string? volume = null, Func<UbiImage, UbiVolumeInfo?>? chooseVolume = null)
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

        // "src" is the whole (unpacked) file and is what gets closed on failure; "fs" is the filesystem in it.
        IBlockSource src = file;
        try
        {
            // A compressed file that is not a tar archive is taken for a compressed image
            // (sdcard.img.gz, rootfs.ext4.gz): unpack it to a temporary file and go on with that.
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
            IBlockSource fs = src;
            DiskContainer? container = null;
            PartitionTable? table = null;
            UbiContainer? ubi = null;

            if (FilesystemAt(src) is null && UbiImage.IsUbi(src))
            {
                if (partition > 0) throw new FsException($"The file is a UBI image and has no partition table, so there is no partition {partition} to open.");
                (fs, ubi) = OpenUbi(src, volume, chooseVolume, ct);
            }
            else if (FilesystemAt(src) is null)
            {
                if (!sparse && outer is null && TarReader.IsTar(src, out _))
                {
                    TarReader.Result res = TarReader.Read(src, tempDirectory, progress, ct);
                    var tarDoc = new ImageDocument(res.Tree, ImageFormat.Tar, path) { Tar = res.Info, FileSize = file.Length };
                    tarDoc._warnings.AddRange(res.Warnings);
                    return tarDoc;
                }

                table = PartitionTable.Read(src);
                PartitionInfo? chosen = table is null ? null : ChoosePartition(table, partition, choosePartition);
                if (chosen is not null)
                {
                    offset = chosen.Offset;
                    fs = new SubBlockSource(src, offset, chosen.StoredLength, ownsInner: false);
                    // An Android sparse file cannot be written back, so there it is just an offset.
                    if (!sparse) container = new DiskContainer(src, table!, chosen);
                }
                else
                {
                    if (partition > 0) throw new FsException($"The file has no partition table, so there is no partition {partition} to open.");
                    long found = searchEmbedded ? SquashFsReader.FindEmbedded(src, 0, 256L << 20, ct) : -1;
                    // A SquashFS that lies inside a UBI image is cut into erase blocks there: the UBI image has to be opened instead.
                    long foundUbi = searchEmbedded ? UbiImage.FindEmbedded(src, 0, found > 0 ? found : 256L << 20, ct) : -1;
                    if (foundUbi > 0)
                    {
                        offset = foundUbi;
                        (fs, ubi) = OpenUbi(new SubBlockSource(src, offset, src.Length - offset, ownsInner: false), volume, chooseVolume, ct);
                    }
                    else
                    {
                        if (found <= 0) throw new FsException(DescribeUnknown(src, outer, table));
                        offset = found;
                        fs = new SubBlockSource(src, offset, src.Length - offset, ownsInner: false);
                    }
                }
            }
            if (volume is not null && ubi is null)
                throw new FsException($"The file is not a UBI image, so there is no volume '{volume}' to open.");

            ImageDocument doc;
            if (UbifsReader.IsUbifs(fs))
            {
                UbifsReader r = UbifsReader.Open(fs);
                FsTree tree = r.ReadTree(progress, ct);
                tree.AttachResource(r);
                doc = new ImageDocument(tree, ImageFormat.Ubifs, path)
                {
                    Ubifs = r.Info, EmbeddedOffset = offset, AndroidSparse = sparse, FileSize = file.Length,
                    OuterCompression = outer, Container = container, Ubi = ubi,
                };
                doc._warnings.AddRange(r.Warnings);
                if (r.Info.ReplayedNodes > 0)
                    doc._warnings.Add($"The filesystem was not cleanly unmounted: {r.Info.ReplayedNodes} journal node(s) were applied while reading. " +
                                      "The file itself was not changed.");
            }
            else if (SquashFsReader.IsSquashFs(fs))
            {
                SquashFsReader r = SquashFsReader.Open(fs);
                FsTree tree = r.ReadTree(progress, ct);
                tree.AttachResource(r);
                doc = new ImageDocument(tree, ImageFormat.SquashFs, path)
                {
                    SquashFs = r.Info, EmbeddedOffset = offset, AndroidSparse = sparse, FileSize = file.Length,
                    OuterCompression = outer, Container = container, Ubi = ubi,
                };
                doc._warnings.AddRange(r.Warnings);
                if (r.Info.Compression is SquashCompression.Lzo or SquashCompression.Lzma)
                    doc._warnings.Add($"The image is compressed with {r.Info.Compression.ToString().ToUpperInvariant()}, which can be read but not written. " +
                                      $"Saving uses {SquashFsOptions.FromInfo(r.Info).Compression.ToString().ToLowerInvariant()} unless another method is chosen.");
            }
            else if (ErofsReader.IsErofs(fs))
            {
                ErofsReader r = ErofsReader.Open(fs);
                FsTree tree = r.ReadTree(progress, ct);
                tree.AttachResource(r);
                doc = new ImageDocument(tree, ImageFormat.Erofs, path)
                {
                    Erofs = r.Info, EmbeddedOffset = offset, AndroidSparse = sparse, FileSize = file.Length,
                    OuterCompression = outer, Container = container, Ubi = ubi,
                };
                doc._warnings.AddRange(r.Warnings);
            }
            else if (!ExtReader.IsExt(fs) && FatReader.IsFat(fs))
            {
                FatReader r = FatReader.Open(fs);
                FsTree tree = r.ReadTree(progress, ct);
                tree.AttachResource(r);
                doc = new ImageDocument(tree, ImageFormat.Fat, path)
                {
                    Fat = r.Info, EmbeddedOffset = offset, AndroidSparse = sparse, FileSize = file.Length,
                    OuterCompression = outer, Container = container, Ubi = ubi,
                };
                doc._warnings.AddRange(r.Warnings);
            }
            else if (!ExtReader.IsExt(fs) && Jffs2Reader.IsJffs2(fs))
            {
                Jffs2Reader r = Jffs2Reader.Open(fs, ownsSource: true, progress, ct);
                FsTree tree = r.ReadTree(progress, ct);
                tree.AttachResource(r);
                doc = new ImageDocument(tree, ImageFormat.Jffs2, path)
                {
                    Jffs2 = r.Info, EmbeddedOffset = offset, AndroidSparse = sparse, FileSize = file.Length,
                    OuterCompression = outer, Container = container, Ubi = ubi,
                };
                doc._warnings.AddRange(r.Warnings);
            }
            else
            {
                ExtReader r = ExtReader.Open(fs);
                FsTree tree = r.ReadTree(progress, ct);
                tree.AttachResource(r);
                doc = new ImageDocument(tree, ImageFormat.Ext, path)
                {
                    Ext = r.Info, EmbeddedOffset = offset, AndroidSparse = sparse, FileSize = file.Length,
                    OuterCompression = outer, Container = container, Ubi = ubi,
                };
                doc._warnings.AddRange(r.Warnings);
            }
            // The readers look through a window that does not own the file: the tree keeps it open.
            if (!ReferenceEquals(fs, src)) doc.Tree.AttachResource(src);

            if (ubi is not null) doc._warnings.AddRange(ubi.Image.Warnings);
            if (sparse)
                doc._warnings.Add("The file is an Android sparse image. Saving writes an ordinary (raw) image of the filesystem.");
            else if (offset > 0 && container is null && ubi is not null)
                doc._warnings.Add($"The UBI image was found inside the file at offset 0x{offset:X}. " +
                                  "Saving writes the UBI image alone, without the data in front of it.");
            else if (offset > 0 && container is null)
                doc._warnings.Add($"The {doc.FormatName} filesystem was found inside the file at offset 0x{offset:X}, and the file has no " +
                                  "partition table that would tell how to rebuild it. Saving writes the filesystem alone, without the surrounding container.");
            return doc;
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

    /// <summary>Selects the partition of a disk image to open; null when none of them can be edited.</summary>
    private static PartitionInfo? ChoosePartition(PartitionTable table, int wanted, Func<PartitionTable, PartitionInfo?>? ask)
    {
        if (wanted > 0)
        {
            PartitionInfo p = table.Find(wanted)
                ?? throw new FsException($"The disk image has no partition {wanted}. Its partitions: {ListPartitions(table)}.");
            if (!p.IsEditable)
                throw new FsException($"Partition {wanted} holds {(p.ContentName.Length > 0 ? p.ContentName : "an unknown format")}, " +
                                      "not SquashFS, EROFS, ext2/3/4 or FAT, and cannot be edited.");
            return p;
        }
        IReadOnlyList<PartitionInfo> editable = table.Editable;
        if (editable.Count == 0) return null;
        if (editable.Count == 1 || ask is null) return table.PickDefault();
        PartitionInfo? answer = ask(table) ?? throw new OperationCanceledException();
        if (!answer.IsEditable || !table.Partitions.Contains(answer))
            throw new FsException("The selected partition does not hold SquashFS, EROFS, ext2/3/4 or FAT.");
        return answer;
    }

    /// <summary>Reads the volumes of a UBI image and selects the one to open.</summary>
    private static (IBlockSource Fs, UbiContainer Container) OpenUbi(IBlockSource source, string? wanted,
        Func<UbiImage, UbiVolumeInfo?>? ask, CancellationToken ct)
    {
        UbiImage image = UbiImage.Read(source, ct);
        UbiVolumeInfo chosen;
        if (wanted is not null)
        {
            chosen = image.Find(wanted)
                ?? throw new FsException($"The UBI image has no volume '{wanted}'. Its volumes: {ListVolumes(image)}.");
            if (!chosen.IsReadable)
                throw new FsException($"Volume {VolumeText(chosen)} holds {(chosen.ContentName.Length > 0 ? chosen.ContentName : "an unknown format")}, " +
                                      "not UBIFS, SquashFS or EROFS, and cannot be opened.");
        }
        else
        {
            IReadOnlyList<UbiVolumeInfo> readable = image.Readable;
            if (readable.Count == 0)
                throw new FsException(image.Volumes.Count == 0
                    ? "The UBI image holds no volumes."
                    : "None of the volumes of this UBI image holds UBIFS, SquashFS or EROFS. Its volumes: " + ListVolumes(image) + ".");
            if (readable.Count == 1 || ask is null) chosen = image.PickDefault()!;
            else
            {
                chosen = ask(image) ?? throw new OperationCanceledException();
                if (!chosen.IsReadable || !image.Volumes.Contains(chosen))
                    throw new FsException("The selected volume does not hold UBIFS, SquashFS or EROFS.");
            }
        }
        return (image.OpenVolume(chosen), new UbiContainer(image, chosen));
    }

    private static string VolumeText(UbiVolumeInfo v) => v.Name.Length > 0 ? $"'{v.Name}' ({v.Id})" : v.Id.ToString();

    private static string ListVolumes(UbiImage image) => string.Join("; ", image.Volumes.Select(v => v.ToString()));

    private static string ListPartitions(PartitionTable table) => string.Join("; ", table.Partitions.Select(p => p.ToString()));

    private static string DescribeUnknown(IBlockSource src, ArchiveCompression? outer, PartitionTable? table)
    {
        if (table is not null)
            return "None of the partitions of this disk image holds SquashFS, EROFS, ext2/3/4 or FAT. Its partitions: " + ListPartitions(table) + ".";
        byte[] head = new byte[8];
        int n = src.Read(0, head);
        string hint = ArchiveCodec.DetectUnsupported(head.AsSpan(0, n)) is { } c ? $" It looks like {c} compressed data." : "";
        if (outer is not null) hint = $" It was unpacked from a {outer.ToString()!.ToLowerInvariant()} file, but the content is not a known image.";
        return "The file is not a SquashFS, EROFS, ext2/3/4, FAT, UBIFS or JFFS2 image, UBI image, disk image with such a partition, or tar archive." + hint;
    }

    /// <summary>Decompresses a whole file into a temporary one that is deleted when the source is disposed.</summary>
    private static IBlockSource Unpack(FileBlockSource file, ArchiveCompression compression, string? tempDirectory,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        string tmp = NewTempFile(tempDirectory);
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

    private static string NewTempFile(string? tempDirectory)
    {
        string dir = tempDirectory ?? Path.GetTempPath();
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "fsforge-" + Guid.NewGuid().ToString("N") + ".img");
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
        if (Ubifs is not null) o.Ubifs = UbifsOptions.FromInfo(Ubifs);
        if (Jffs2 is not null) o.Jffs2 = Jffs2Options.FromInfo(Jffs2);
        if (Fat is not null) o.Fat = FatOptions.FromInfo(Fat);
        if (Erofs is not null)
        {
            // The method, the cluster size and the identity of the image are kept; the effort stays as configured.
            ErofsOptions e = ErofsOptions.FromInfo(Erofs);
            (e.Lz4Level, e.LzmaEffort, e.ZstdLevel, e.DeflateLevel, e.Threads) = (o.Erofs.Lz4Level, o.Erofs.LzmaEffort, o.Erofs.ZstdLevel, o.Erofs.DeflateLevel, o.Erofs.Threads);
            o.Erofs = e;
        }
        // A filesystem that goes into a UBI image is made for the erase blocks of that image.
        if (Ubifs is null && Ubi is not null)
        {
            o.Ubifs.LebSize = Ubi.Volume.LebSize;
            o.Ubifs.MinIoSize = Ubi.Image.GuessMinIoSize();
        }
        if (SquashFs is not null) o.SquashFs = SquashFsOptions.FromInfo(SquashFs);
        if (Ext is not null) o.Ext = ExtOptions.FromInfo(Ext);
        if (Tar is not null) o.Tar = TarOptions.FromInfo(Tar);
        return o;
    }

    /// <summary>True when saving with these options writes the whole disk image or UBI image and not the filesystem alone.</summary>
    public bool WritesContainer(SaveOptions options) =>
        (Container is not null || Ubi is not null) && options.KeepContainer && options.Format is not (ImageFormat.Tar or ImageFormat.Jffs2)
        && !(Ubi is not null && options.Format == ImageFormat.Fat);

    /// <summary>True when saving with these options writes a UBI image: the one the document came from, or a new one.</summary>
    public bool WritesUbi(SaveOptions options) =>
        Ubi is not null && WritesContainer(options) ||
        Container is null && options.NewUbi && options.Format is ImageFormat.Ubifs or ImageFormat.SquashFs;

    /// <summary>Writes a tree to a stream in the selected format; returns the writer's warnings.</summary>
    public static IReadOnlyList<string> WriteTo(FsTree tree, Stream output, SaveOptions options,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default) => options.Format switch
    {
        ImageFormat.SquashFs => SquashFsWriter.Write(tree, output, options.SquashFs, progress, ct),
        ImageFormat.Ext => ExtWriter.Write(tree, output, options.Ext, progress, ct),
        ImageFormat.Ubifs => UbifsWriter.Write(tree, output, options.Ubifs, progress, ct),
        ImageFormat.Jffs2 => Jffs2Writer.Write(tree, output, options.Jffs2, progress, ct),
        ImageFormat.Fat => FatWriter.Write(tree, output, options.Fat, progress, ct),
        ImageFormat.Erofs => ErofsWriter.Write(tree, output, options.Erofs, progress, ct, options.TempDirectory),
        _ => TarWriter.Write(tree, output, options.Tar, null, progress, ct),
    };

    /// <summary>Outcome of <see cref="Save"/>.</summary>
    public sealed record SaveResult(ImageDocument Document, IReadOnlyList<string> Warnings, long BytesWritten);

    /// <summary>
    /// Writes the image to <paramref name="path"/> and reopens it. The data goes to a temporary file
    /// first and is verified there, so a failure never damages an existing file. On success this
    /// document is disposed and the returned one takes its place.
    /// <para>
    /// A filesystem opened from a partition of a disk image is written back as the whole disk image
    /// (see <see cref="SaveOptions.KeepContainer"/>). A name ending in .gz, .xz or .zst makes the
    /// image compressed.
    /// </para>
    /// </summary>
    public SaveResult Save(string path, SaveOptions options, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        path = Path.GetFullPath(path);
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

        DiskContainer? container = WritesContainer(options) ? Container : null;
        int partition = container?.Partition.Number ?? 0;
        bool ubiOut = WritesUbi(options);
        if (options.NewUbi && !ubiOut && Ubi is null && options.Format is ImageFormat.Ext or ImageFormat.Tar or ImageFormat.Jffs2 or ImageFormat.Fat or ImageFormat.Erofs)
            throw new FsException("Only UBIFS and SquashFS can be wrapped into a UBI image.");
        // The volume to look into when the written UBI image is read back.
        string? volume = !ubiOut ? null : Ubi is not null && WritesContainer(options) ? Ubi.Volume.Id.ToString() : options.Ubi.VolumeId.ToString();
        // A tar archive carries its compression in its own options; an image gets it from the file name.
        ArchiveCompression outer = options.Format == ImageFormat.Tar ? ArchiveCompression.None : ArchiveCodec.ImageCompressionFromFileName(path);

        // "tmp" becomes the target file. A compressed image is first written raw to "raw" and verified there.
        string tmp = path + ".fsforge-tmp";
        string raw = tmp;
        try
        {
            if (outer != ArchiveCompression.None) raw = NewTempFile(options.TempDirectory);
            using (var fs = new FileStream(raw, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20))
            {
                if (container is not null) WriteDisk(fs, container, options, warnings, progress, ct);
                else if (ubiOut) WriteUbi(fs, options, warnings, progress, ct);
                else warnings.AddRange(WriteTo(Tree, fs, options, progress, ct));
                fs.Flush(flushToDisk: true);
            }

            if (options.Verify != VerifyLevel.None)
            {
                ct.ThrowIfCancellationRequested();
                using ImageDocument check = Open(raw, options.TempDirectory, searchEmbedded: false, null, ct, partition, volume: volume);
                List<string> problems = TreeComparer.Compare(Tree, check.Tree, TreeComparer.RulesFor(options),
                    options.Verify == VerifyLevel.Content, progress, ct);
                if (container is not null) problems.AddRange(CompareSurroundings(container, check.Container, ct));
                if (ubiOut && Ubi is not null && WritesContainer(options)) problems.AddRange(CompareVolumes(Ubi, check.Ubi, ct));
                if (problems.Count > 0)
                {
                    string list = string.Join(Environment.NewLine, problems.Take(10));
                    throw new FsException(
                        "The written image does not match the source and was discarded; the original file is untouched." +
                        Environment.NewLine + list + (problems.Count > 10 ? Environment.NewLine + $"... and {problems.Count - 10} more." : ""));
                }
            }

            if (outer != ArchiveCompression.None)
                Compress(raw, tmp, outer, options, progress, ct);
        }
        catch (Exception ex)
        {
            TryDelete(tmp);
            if (ex is IOException or UnauthorizedAccessException)
                throw new FsException($"The image could not be written: {ex.Message}", ex);
            throw;
        }
        finally
        {
            if (raw != tmp) TryDelete(raw);
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

        ImageDocument reopened = Open(path, options.TempDirectory, searchEmbedded: false, null, CancellationToken.None, partition, volume: volume);
        return new SaveResult(reopened, warnings, written);
    }

    // ---------------------------------------------------------- UBI image

    /// <summary>
    /// Writes a UBI image around the filesystem: the image the document was opened from, with the
    /// other volumes copied as they are, or a new image with a single volume.
    /// </summary>
    private void WriteUbi(FileStream output, SaveOptions options, List<string> warnings, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        UbiContainer? source = Ubi is not null && WritesContainer(options) ? Ubi : null;
        int pebSize, vidOffset, dataOffset, lebSize, reserved;
        uint sequence;
        if (source is not null)
        {
            (pebSize, vidOffset, dataOffset, sequence) = (source.Image.PebSize, source.Image.VidHeaderOffset, source.Image.DataOffset, source.Image.ImageSequence);
            lebSize = source.Volume.LebSize;
            reserved = source.Volume.ReservedLebs;
        }
        else
        {
            UbiOptions u = options.Ubi;
            u.Validate();
            (pebSize, vidOffset, dataOffset, lebSize) = (u.PebSize, u.VidHeaderOffset, u.DataOffset, u.LebSize);
            sequence = (uint)Random.Shared.NextInt64(1, uint.MaxValue);
            reserved = (int)Math.Min(int.MaxValue, u.VolumeBytes / lebSize);
        }

        SaveOptions o = options;
        if (options.Format == ImageFormat.Ubifs)
        {
            o = options.Clone();
            // The kernel refuses a filesystem whose erase block or I/O unit differs from that of its volume.
            if (o.Ubifs.LebSize != lebSize)
            {
                if (source is not null && Format == ImageFormat.Ubifs)
                    throw new FsException($"The UBIFS erase block size ({o.Ubifs.LebSize}) must stay the erase block size of the UBI volume ({lebSize}).");
                o.Ubifs.LebSize = lebSize;
            }
            if (source is null) o.Ubifs.MinIoSize = options.Ubi.MinIoSize;
            if (o.Ubifs.VolumeLebCount == 0) o.Ubifs.VolumeLebCount = reserved;
        }

        string tmp = NewTempFile(options.TempDirectory);
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20))
                warnings.AddRange(WriteTo(Tree, fs, o, progress, ct));
            using var data = new FileBlockSource(tmp);

            // What the volume table has to reserve: the image, and for UBIFS a main area that holds its journal.
            int needed = Math.Max((int)((data.Length + lebSize - 1) / lebSize), UbifsReader.MinimumVolumeLebs(data));
            var volumes = new List<UbiVolumeSpec>();
            if (source is null)
            {
                volumes.Add(new UbiVolumeSpec
                {
                    Id = options.Ubi.VolumeId, Name = options.Ubi.VolumeName, Data = data,
                    ReservedLebs = Math.Max(reserved, needed), AutoResize = options.Ubi.AutoResize,
                });
            }
            else
            {
                UbiVolumeInfo opened = source.Volume;
                if (reserved > 0 && needed > reserved)
                    warnings.Add($"The filesystem no longer fits into volume {VolumeText(opened)} ({reserved} erase blocks): the volume was enlarged to " +
                                 $"{needed} erase blocks. The flash must have room for it.");
                foreach (UbiVolumeInfo v in source.Image.Volumes)
                {
                    bool replaced = ReferenceEquals(v, opened);
                    volumes.Add(new UbiVolumeSpec
                    {
                        Id = v.Id, Name = v.Name, IsStatic = v.IsStatic, AutoResize = v.AutoResize, Alignment = v.Alignment, ExtraFlags = v.Flags,
                        Data = replaced ? data : source.Image.OpenVolume(v),
                        ReservedLebs = replaced ? Math.Max(reserved, needed) : v.ReservedLebs,
                        IsStored = replaced ? null : lnum => source.Image.IsStored(v, lnum),
                    });
                }
            }
            UbiWriter.Write(output, pebSize, vidOffset, dataOffset, sequence, volumes, progress, ct);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    /// <summary>Checks that the volumes that were not rewritten read the same in the written UBI image.</summary>
    private static List<string> CompareVolumes(UbiContainer source, UbiContainer? written, CancellationToken ct)
    {
        var problems = new List<string>();
        if (written is null)
        {
            problems.Add("The written file is not a UBI image.");
            return problems;
        }
        byte[] x = new byte[1 << 20], y = new byte[1 << 20];
        foreach (UbiVolumeInfo a in source.Image.Volumes)
        {
            if (ReferenceEquals(a, source.Volume)) continue;
            UbiVolumeInfo? b = written.Image.Volumes.FirstOrDefault(v => v.Id == a.Id);
            if (b is null || b.Name != a.Name || b.IsStatic != a.IsStatic || b.Length != a.Length)
            {
                problems.Add($"Volume {VolumeText(a)} of the UBI image is missing or differs in the written image.");
                continue;
            }
            IBlockSource sa = source.Image.OpenVolume(a), sb = written.Image.OpenVolume(b);
            for (long offset = 0; offset < a.Length; offset += x.Length)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(x.Length, a.Length - offset);
                sa.ReadExactly(offset, x.AsSpan(0, n));
                sb.ReadExactly(offset, y.AsSpan(0, n));
                if (x.AsSpan(0, n).SequenceEqual(y.AsSpan(0, n))) continue;
                problems.Add($"The content of volume {VolumeText(a)} differs from the source UBI image.");
                break;
            }
        }
        return problems;
    }

    // ------------------------------------------------------ disk container

    /// <summary>libfstools puts the writable overlay of an OpenWrt image at the next 64 KiB boundary after the SquashFS.</summary>
    private const long OverlayAlign = 0x10000;

    private const long Mebibyte = 1L << 20;

    private static long AlignUp(long value, long step) => (value + step - 1) / step * step;

    /// <summary>
    /// Writes the disk image with a new filesystem in the partition the document was opened from.
    /// Everything outside that partition is copied as it is.
    /// </summary>
    private void WriteDisk(FileStream fs, DiskContainer c, SaveOptions options, List<string> warnings,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        PartitionInfo part = c.Partition;
        IBlockSource disk = c.Disk;
        long slot = part.Length;                    // room according to the partition table
        long stored = part.StoredLength;            // how much of it the file actually had
        bool canGrow = c.CanGrow;
        bool squash = options.Format == ImageFormat.SquashFs;
        // Like SquashFS, EROFS is as large as its content: it is written to the start of the partition.
        bool packed = squash || options.Format == ImageFormat.Erofs;
        string partName = $"partition {part.Number}";

        // Everything in front of the partition: the partition table, the boot loader, the boot partition.
        CopyRange(disk, 0, part.Offset, fs, progress, ct);

        SaveOptions o = options;
        bool fat = options.Format == ImageFormat.Fat;
        if (fat)
        {
            o = options.Clone();
            // FAT fills its partition and knows where the partition starts (the "hidden sectors" of its boot sector).
            if (o.Fat.SizeBytes == 0) o.Fat.SizeBytes = slot / o.Fat.SectorSize * o.Fat.SectorSize;
            else if (o.Fat.SizeBytes > slot && !canGrow)
                throw new FsException($"The requested filesystem size of {DiskText.Size(o.Fat.SizeBytes)} exceeds {partName} " +
                                      $"({DiskText.Size(slot)}), which cannot be enlarged{WhyNoGrow(c)}.");
            o.Fat.HiddenSectors = (uint)Math.Min(uint.MaxValue, part.Offset / o.Fat.SectorSize);
        }
        else if (!packed)
        {
            o = options.Clone();
            long bs = o.Ext.BlockSize;
            if (o.Ext.SizeBytes == 0) o.Ext.SizeBytes = slot / bs * bs;         // "automatic" means: fill the partition
            else if (o.Ext.SizeBytes > slot && !canGrow)
                throw new FsException($"The requested filesystem size of {DiskText.Size(o.Ext.SizeBytes)} exceeds {partName} " +
                                      $"({DiskText.Size(slot)}), which cannot be enlarged{WhyNoGrow(c)}.");
        }

        fs.Position = part.Offset;
        try
        {
            warnings.AddRange(WriteTo(Tree, fs, o, progress, ct));
        }
        catch (FsNoSpaceException) when (fat && canGrow)
        {
            o.Fat.SizeBytes = 0;
            o.Fat.ExtraFreeBytes = Math.Max(o.Fat.ExtraFreeBytes, slot / 10);
            fs.SetLength(part.Offset);
            fs.Position = part.Offset;
            warnings.AddRange(WriteTo(Tree, fs, o, progress, ct));
        }
        catch (FsNoSpaceException) when (!packed && canGrow)
        {
            // Let the writer find the size the content needs; the partition follows below.
            o.Ext.SizeBytes = 0;
            o.Ext.InodeCount = 0;
            o.Ext.ExtraFreeBytes = Math.Max(o.Ext.ExtraFreeBytes, slot / 10);
            fs.SetLength(part.Offset);
            fs.Position = part.Offset;
            warnings.AddRange(WriteTo(Tree, fs, o, progress, ct));
        }
        catch (FsNoSpaceException)
        {
            throw new FsException($"The content does not fit into {partName} ({DiskText.Size(slot)}), which cannot be enlarged{WhyNoGrow(c)}. " +
                                  "Remove some files or save the filesystem alone.");
        }
        long fsLength = fs.Length - part.Offset;

        // Where the overlay of an OpenWrt system starts, before and after.
        long oldOverlay = Format == ImageFormat.SquashFs ? AlignUp(SquashFs!.BytesUsed, OverlayAlign) : -1;
        long newOverlay = AlignUp(fsLength, OverlayAlign);

        long newSlot = slot;
        if (fsLength > slot)
        {
            if (!canGrow)
                throw new FsException($"The filesystem takes {DiskText.Size(fsLength)}, but {partName} holds only {DiskText.Size(slot)} " +
                                      $"and cannot be enlarged{WhyNoGrow(c)}. Remove some files, choose a stronger compression " +
                                      "or save the filesystem alone.");
            // Keep as much room behind the filesystem as there was (the overlay lives there).
            long room = squash && oldOverlay >= 0 ? Math.Max(0, slot - oldOverlay) : 0;
            newSlot = AlignUp((squash ? newOverlay : fsLength) + room, Mebibyte);
            long sectors = newSlot / c.Table.SectorSize;
            if (part.Offset / c.Table.SectorSize + sectors > uint.MaxValue)
                throw new FsException($"The filesystem takes {DiskText.Size(fsLength)}; {partName} cannot be made that large in an MBR partition table.");
            Span<byte> field = stackalloc byte[4];
            Le.W32(field, 0, (uint)sectors);
            fs.Position = part.SizeFieldOffset;
            fs.Write(field);
            warnings.Add($"The filesystem no longer fits into {partName} ({DiskText.Size(slot)}): the partition was enlarged to " +
                         $"{DiskText.Size(newSlot)}. The card must hold at least {DiskText.Size(part.Offset + newSlot)}.");
        }

        // What follows the filesystem inside the partition.
        long end = Math.Max(fsLength, stored);      // the file never gets shorter: stale data on a card would show through
        if (squash)
        {
            bool overlayStays = oldOverlay == newOverlay && newSlot == slot;
            if (overlayStays)
            {
                if (stored > newOverlay) CopyRange(disk, part.Offset + newOverlay, stored - newOverlay, fs, progress, ct);
            }
            else
            {
                if (oldOverlay >= 0 && oldOverlay < stored && HoldsData(disk, part.Offset + oldOverlay, stored - oldOverlay))
                    warnings.Add("The data that followed the old filesystem in the partition (the writable overlay of the system) " +
                                 "was cleared, because the size of the filesystem changed. The device creates a new, empty overlay on first boot.");
                // Zeros where the overlay starts now make the device format it instead of trusting leftovers on the card.
                end = Math.Max(end, Math.Min(newSlot, newOverlay + OverlayAlign));
            }
            if (oldOverlay >= 0 && slot - oldOverlay >= Mebibyte && newSlot - newOverlay < Mebibyte)
                warnings.Add($"Less than 1 MiB is left in {partName} behind the filesystem. An OpenWrt system keeps its settings there; " +
                             "without that room it runs with a temporary overlay in memory.");
        }

        // An image that covered the whole partition keeps doing so after the partition has grown.
        if (newSlot != slot && stored == slot) end = newSlot;

        // Everything behind the partition: further partitions, a backup GPT.
        long after = part.Offset + slot;
        if (disk.Length > after) CopyRange(disk, after, disk.Length - after, fs, progress, ct);
        long total = Math.Max(part.Offset + end, newSlot == slot ? disk.Length : 0);
        if (fs.Length < total) fs.SetLength(total);
    }

    private static string WhyNoGrow(DiskContainer c)
    {
        PartitionInfo p = c.Partition;
        if (!p.IsLast) return " because other partitions follow it";
        if (c.Table.Scheme == PartitionScheme.Gpt) return " in a GPT disk image";
        if (p.SizeFieldOffset < 0) return " because it is a logical partition";
        return " because other data follows it in the file";
    }

    /// <summary>True when a region of the disk holds anything but zeros (only its first megabyte is examined).</summary>
    private static bool HoldsData(IBlockSource disk, long offset, long length)
    {
        byte[] buffer = new byte[1 << 16];
        long left = Math.Min(length, Mebibyte);
        while (left > 0)
        {
            int n = disk.Read(offset, buffer.AsSpan(0, (int)Math.Min(buffer.Length, left)));
            if (n <= 0) break;
            if (buffer.AsSpan(0, n).IndexOfAnyExcept((byte)0) >= 0) return true;
            offset += n;
            left -= n;
        }
        return false;
    }

    /// <summary>Copies a range of the disk to the same position of the output. Runs of zeros are skipped, which keeps the file sparse.</summary>
    private static void CopyRange(IBlockSource disk, long offset, long length, FileStream output,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        byte[] buffer = new byte[1 << 20];
        long end = offset + length, total = length;
        while (offset < end)
        {
            ct.ThrowIfCancellationRequested();
            int n = disk.Read(offset, buffer.AsSpan(0, (int)Math.Min(buffer.Length, end - offset)));
            if (n <= 0) throw new FsException("The disk image is truncated: its end cannot be read.");
            if (buffer.AsSpan(0, n).IndexOfAnyExcept((byte)0) >= 0)
            {
                output.Position = offset;
                output.Write(buffer, 0, n);
            }
            offset += n;
            progress?.Report(new ProgressInfo("copy", total - (end - offset), total));
        }
        if (output.Length < end) output.SetLength(end);
    }

    /// <summary>Checks that nothing outside the rewritten partition differs from the source disk.</summary>
    private static List<string> CompareSurroundings(DiskContainer source, DiskContainer? written, CancellationToken ct)
    {
        var problems = new List<string>();
        if (written is null)
        {
            problems.Add("The written file is not a disk image with a partition table.");
            return problems;
        }
        PartitionInfo a = source.Partition, b = written.Partition;
        if (a.Offset != b.Offset) problems.Add($"Partition {a.Number} moved from offset {a.Offset} to {b.Offset}.");
        if (written.Table.Partitions.Count != source.Table.Partitions.Count)
            problems.Add("The number of partitions changed.");
        bool grown = b.Length != a.Length;

        byte[] x = new byte[1 << 20], y = new byte[1 << 20];
        void Range(long offset, long length, string what)
        {
            long end = offset + length;
            while (offset < end)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(x.Length, end - offset);
                source.Disk.ReadExactly(offset, x.AsSpan(0, n));
                written.Disk.ReadExactly(offset, y.AsSpan(0, n));
                // The size of an enlarged partition is the one field of the table that may change.
                if (grown && a.SizeFieldOffset >= offset && a.SizeFieldOffset + 4 <= offset + n)
                    x.AsSpan((int)(a.SizeFieldOffset - offset), 4).CopyTo(y.AsSpan((int)(a.SizeFieldOffset - offset), 4));
                if (!x.AsSpan(0, n).SequenceEqual(y.AsSpan(0, n)))
                {
                    problems.Add($"The data {what} partition {a.Number} differs from the source disk image.");
                    return;
                }
                offset += n;
            }
        }

        try
        {
            Range(0, a.Offset, "in front of");
            long after = a.Offset + a.Length;
            if (source.Disk.Length > after) Range(after, source.Disk.Length - after, "behind");
        }
        catch (FsException ex)
        {
            problems.Add("The written disk image is shorter than expected: " + ex.Message);
        }
        return problems;
    }

    /// <summary>Compresses a verified raw image into the file that replaces the target, then reads it back.</summary>
    private static void Compress(string raw, string target, ArchiveCompression compression, SaveOptions options,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        TarOptions codec = options.Tar.Clone();
        codec.Compression = compression;
        byte[] buffer = new byte[1 << 20];
        byte[] expected;
        long length;

        using (var input = new FileStream(raw, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
        using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20))
        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            length = input.Length;
            using (Stream packer = ArchiveCodec.OpenCompressor(output, codec))
            {
                int n;
                long done = 0;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    sha.AppendData(buffer, 0, n);
                    packer.Write(buffer, 0, n);
                    done += n;
                    progress?.Report(new ProgressInfo("compress", done, length));
                }
            }
            output.Flush(flushToDisk: true);
            expected = sha.GetHashAndReset();
        }
        if (options.Verify == VerifyLevel.None) return;

        // The compressors are our own code paths too: make sure the file unpacks to exactly what went in.
        using (var packed = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
        using (Stream unpacker = ArchiveCodec.OpenDecompressor(packed, compression))
        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            long done = 0;
            int n;
            try
            {
                while ((n = unpacker.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    sha.AppendData(buffer, 0, n);
                    done += n;
                    progress?.Report(new ProgressInfo("verify", done, length));
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or ZstdSharp.ZstdException or EndOfStreamException)
            {
                throw new FsException($"The compressed image cannot be unpacked again ({ex.Message}) and was discarded; the original file is untouched.");
            }
            if (done != length || !sha.GetHashAndReset().AsSpan().SequenceEqual(expected))
                throw new FsException("The compressed image does not unpack to the data that was written and was discarded; the original file is untouched.");
        }
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

        /// <summary>Permissions are not stored (FAT): only the type is compared.</summary>
        public bool IgnoreModes;

        /// <summary>Only files and directories exist in the target (FAT): other objects are not looked for, hard links become copies.</summary>
        public bool FilesAndDirectoriesOnly;

        /// <summary>Times are stored in steps of this many seconds (2 in FAT) and compared rounded down to them.</summary>
        public long TimeStep = 1;

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
        ImageFormat.Ubifs => new Rules
        {
            IgnoreOwners = o.Ubifs.AllRoot,
            IgnoreXattrs = true,                    // only the user/trusted/security namespaces exist in UBIFS
        },
        ImageFormat.Erofs => new Rules
        {
            IgnoreOwners = o.Erofs.AllRoot,
            IgnoreXattrs = true,                    // only the user/trusted/security namespaces and ACLs exist in EROFS
        },
        ImageFormat.Fat => new Rules
        {
            // Only files and directories, without owners, permissions or attributes; times in 2-second steps.
            IgnoreOwners = true,
            IgnoreModes = true,
            IgnoreXattrs = true,
            IgnoreRootAttributes = true,
            FilesAndDirectoriesOnly = true,
            TimeStep = 2,
            MinTime = FatFormat.MinTime,
            MaxTime = FatFormat.MaxTime,
        },
        ImageFormat.Jffs2 => new Rules
        {
            IgnoreOwners = o.Jffs2.AllRoot,
            IgnoreXattrs = true,                    // written only on request, and only the user/security/trusted/ACL ones
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
                if (!rules.IgnoreModes && x.Type != FsNodeType.Symlink && (x.Mode & 0xFFF) != (y.Mode & 0xFFF))
                    problems.Add($"{path}: mode is {UnixMode.ToOctal(y.Mode)}, expected {UnixMode.ToOctal(x.Mode)}.");
                if (!rules.IgnoreOwners && (x.Uid != y.Uid || x.Gid != y.Gid))
                    problems.Add($"{path}: owner is {y.Uid}:{y.Gid}, expected {x.Uid}:{x.Gid}.");
                if (x.MTime - (x.MTime % rules.TimeStep + rules.TimeStep) % rules.TimeStep != y.MTime && x.MTime >= rules.MinTime && x.MTime <= rules.MaxTime)
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
                    if (x.LinkCount > 1 && !rules.FilesAndDirectoriesOnly)
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
                        if (rules.FilesAndDirectoriesOnly && ca.Type is not (FsNodeType.File or FsNodeType.Directory)) continue;
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
