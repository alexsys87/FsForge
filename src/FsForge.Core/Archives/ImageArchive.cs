using System.IO.Compression;
using FsForge.Core.Tar;

namespace FsForge.Core.Archives;

/// <summary>
/// A zip or 7z archive that holds an image, possibly next to other files (a README, checksums). Lists the
/// files, unpacks one of them and writes a new archive in which one file is replaced and the others are
/// carried over.
/// </summary>
public sealed class ImageArchive : IDisposable
{
    private readonly Stream _file;
    private readonly SevenZipArchive? _sevenZip;
    private readonly ZipArchive? _zip;
    private readonly List<ZipArchiveEntry> _zipEntries = new();

    public ArchiveCompression Kind { get; }
    public IReadOnlyList<ArchiveEntry> Entries { get; }

    /// <summary>The compression methods used, for display ("LZMA2", "Deflate").</summary>
    public string Methods { get; }

    private ImageArchive(Stream file, ArchiveCompression kind)
    {
        _file = file;
        Kind = kind;
        if (kind == ArchiveCompression.SevenZip)
        {
            _sevenZip = SevenZipArchive.Open(file, leaveOpen: true);
            Entries = _sevenZip.Entries;
            Methods = _sevenZip.Methods;
            return;
        }
        try
        {
            _zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            throw new FsException($"Corrupt zip archive: {ex.Message}", ex);
        }
        var list = new List<ArchiveEntry>();
        var methods = new HashSet<string>();
        int i = 0;
        foreach (ZipArchiveEntry e in _zip.Entries)
        {
            _zipEntries.Add(e);
            bool dir = e.FullName.EndsWith('/') || e.FullName.EndsWith('\\');
            if (!dir) methods.Add(e.CompressedLength == e.Length && e.Length > 0 ? "Stored" : "Deflate");
            list.Add(new ArchiveEntry
            {
                Index = i++,
                Name = e.FullName.Replace('\\', '/').TrimEnd('/'),
                Size = e.Length,
                CompressedSize = e.CompressedLength,
                IsDirectory = dir,
                LastWriteTimeUtc = e.LastWriteTime.UtcDateTime,
                Attributes = e.ExternalAttributes == 0 ? null : (uint)e.ExternalAttributes,
                Crc = e.Crc32,
            });
        }
        Entries = list;
        Methods = string.Join(", ", methods.Order());
    }

    public static ImageArchive Open(string path, ArchiveCompression kind)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        try
        {
            return new ImageArchive(fs, kind);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _zip?.Dispose();
        _sevenZip?.Dispose();
        _file.Dispose();
    }

    /// <summary>The files that may be images: not folders and not empty.</summary>
    public IEnumerable<ArchiveEntry> Candidates => Entries.Where(e => !e.IsDirectory && e.Size > 0);

    /// <summary>
    /// The entry to open when the caller does not choose: the only file, else the largest file that has the
    /// name of an image, else the largest file.
    /// </summary>
    public ArchiveEntry? DefaultImage()
    {
        var files = Candidates.ToList();
        if (files.Count <= 1) return files.FirstOrDefault();
        return files.Where(e => LooksLikeImage(e.Name)).MaxBy(e => e.Size) ?? files.MaxBy(e => e.Size);
    }

    private static readonly string[] ImageExtensions =
    {
        ".img", ".iso", ".bin", ".raw", ".dd", ".image", ".hdd", ".sdcard", ".wic", ".rootfs", ".squashfs", ".sqfs", ".erofs",
        ".ext2", ".ext3", ".ext4", ".ubi", ".ubifs", ".jffs2", ".vfat", ".fat", ".f2fs", ".cramfs", ".romfs", ".cpio", ".tar",
    };

    public static bool LooksLikeImage(string name) =>
        ImageExtensions.Contains(Path.GetExtension(ArchiveCodec.RemoveCompressionExtension(name)), StringComparer.OrdinalIgnoreCase);

    public ArchiveEntry? Find(string name) =>
        Entries.FirstOrDefault(e => !e.IsDirectory && e.Name == name)
        ?? Entries.FirstOrDefault(e => !e.IsDirectory && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? Entries.FirstOrDefault(e => !e.IsDirectory && string.Equals(Path.GetFileName(e.Name), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Opens the contents of an entry; its CRC is checked when the end is read.</summary>
    public Stream OpenEntry(ArchiveEntry entry)
    {
        if (_sevenZip is not null) return _sevenZip.OpenEntry(entry);
        Stream data;
        try
        {
            data = _zipEntries[entry.Index].Open();
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            throw new FsException($"'{entry.Name}' in the zip archive cannot be unpacked: {ex.Message} " +
                                  "Only the Deflate and Stored methods without encryption are supported.", ex);
        }
        return new CheckedStream(data, entry.Size, entry.Crc, entry.Name, ownsInner: true);
    }

    /// <summary>Every entry with its contents, in archive order (each 7z folder is decoded once).</summary>
    public void ReadAll(Action<ArchiveEntry, Stream> visit, CancellationToken ct)
    {
        if (_sevenZip is not null)
        {
            _sevenZip.ReadAll(visit, ct);
            return;
        }
        foreach (ArchiveEntry e in Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (e.IsDirectory)
            {
                using var none = new MemoryStream(Array.Empty<byte>(), false);
                visit(e, none);
                continue;
            }
            using var data = (CheckedStream)OpenEntry(e);
            visit(e, data);
            data.Drain();
        }
    }

    // ------------------------------------------------------------ writing

    /// <summary>
    /// Writes a new archive of the given kind with one file, the image. The other files of
    /// <paramref name="source"/> (when given) are carried over in their order, and the image takes the place of
    /// <paramref name="replaced"/> among them.
    /// </summary>
    /// <param name="image">Writes the image data (exactly <paramref name="imageLength"/> bytes) into a stream.</param>
    public static void Write(string target, ArchiveCompression kind, string imageName, long imageLength, Action<Stream> image,
        ImageArchive? source, ArchiveEntry? replaced, TarOptions levels, string? tempDirectory, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var plan = new List<(ArchiveEntry? Old, bool IsImage)>();
        bool placed = false;
        if (source is not null)
            foreach (ArchiveEntry e in source.Entries)
            {
                if (replaced is not null && e.Index == replaced.Index)
                {
                    plan.Add((e, true));
                    placed = true;
                }
                else if (!e.IsDirectory && string.Equals(e.Name, imageName, StringComparison.OrdinalIgnoreCase)) continue;
                else plan.Add((e, false));
            }
        if (!placed) plan.Insert(0, (null, true));

        // The other files are taken from a copy unpacked to memory or, when large, a temporary file; a 7z
        // folder can only be read from its start, so this keeps the source read in a single pass.
        var others = new Dictionary<int, Func<Stream>>();
        var temps = new List<string>();
        try
        {
            if (source is not null && plan.Any(p => p.Old is { IsDirectory: false } && !p.IsImage))
            {
                source.ReadAll((e, data) =>
                {
                    if (e.IsDirectory || (replaced is not null && e.Index == replaced.Index) || !plan.Any(p => p.Old?.Index == e.Index)) return;
                    if (e.Size <= 64 << 20)
                    {
                        var ms = new MemoryStream();
                        data.CopyTo(ms);
                        byte[] bytes = ms.ToArray();
                        others[e.Index] = () => new MemoryStream(bytes, false);
                    }
                    else
                    {
                        string dir = tempDirectory ?? Path.GetTempPath();
                        Directory.CreateDirectory(dir);
                        string tmp = Path.Combine(dir, $"fsforge-{Guid.NewGuid():N}.part");
                        temps.Add(tmp);
                        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write)) data.CopyTo(fs);
                        others[e.Index] = () => new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                    }
                }, ct);
            }

            var entries = new List<ArchiveWriteEntry>();
            foreach (var (old, isImage) in plan)
            {
                if (isImage)
                {
                    entries.Add(new ArchiveWriteEntry
                    {
                        Name = imageName, Length = imageLength, LastWriteTimeUtc = DateTime.UtcNow,
                        Attributes = old?.Attributes, WriteData = image,
                    });
                }
                else
                {
                    ArchiveEntry e = old!;
                    Func<Stream>? open = others.GetValueOrDefault(e.Index);
                    entries.Add(new ArchiveWriteEntry
                    {
                        Name = e.Name, IsDirectory = e.IsDirectory, Length = e.IsDirectory ? 0 : e.Size,
                        LastWriteTimeUtc = e.LastWriteTimeUtc, Attributes = e.Attributes,
                        WriteData = open is null ? null : s => { using Stream d = open(); d.CopyTo(s, 1 << 20); },
                    });
                }
            }

            using var output = new FileStream(target, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
            if (kind == ArchiveCompression.SevenZip) SevenZipWriter.Write(output, entries, levels.XzEffort, levels.Threads, progress, ct);
            else WriteZip(output, entries, levels.GzipLevel, progress, ct);
            output.Flush(flushToDisk: true);
        }
        finally
        {
            foreach (string t in temps)
                try { File.Delete(t); } catch (IOException) { }
        }
    }

    private static void WriteZip(Stream output, List<ArchiveWriteEntry> entries, int gzipLevel, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        CompressionLevel level = gzipLevel <= 3 ? CompressionLevel.Fastest : gzipLevel >= 9 ? CompressionLevel.SmallestSize : CompressionLevel.Optimal;
        long total = entries.Sum(e => e.Length), done = 0;
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (ArchiveWriteEntry e in entries)
        {
            ct.ThrowIfCancellationRequested();
            ZipArchiveEntry z = zip.CreateEntry(e.IsDirectory ? e.Name.TrimEnd('/') + "/" : e.Name, e.IsDirectory || e.Length == 0 ? CompressionLevel.NoCompression : level);
            DateTime t = e.LastWriteTimeUtc ?? DateTime.UtcNow;
            if (t.Year < 1980) t = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            if (t.Year > 2107) t = new DateTime(2107, 12, 31, 0, 0, 0, DateTimeKind.Utc);
            z.LastWriteTime = new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToLocalTime();
            if (e.Attributes is { } a) z.ExternalAttributes = unchecked((int)a);
            if (e.IsDirectory || e.WriteData is null) continue;
            using Stream s = z.Open();
            var counted = new ProgressStream(s, n => progress?.Report(new ProgressInfo("compress", done += n, total)), ct);
            e.WriteData(counted);
            if (counted.Count != e.Length)
                throw new FsException($"'{e.Name}' changed its size while it was being packed ({counted.Count} instead of {e.Length} bytes).");
        }
    }

    private sealed class ProgressStream(Stream inner, Action<int> report, CancellationToken ct) : Stream
    {
        public long Count;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ct.ThrowIfCancellationRequested();
            inner.Write(buffer);
            Count += buffer.Length;
            report(buffer.Length);
        }
    }
}

/// <summary>The zip or 7z archive an image was taken from.</summary>
/// <param name="Kind">Zip or SevenZip.</param>
/// <param name="EntryName">Name of the image file inside the archive; saving writes the image under this name again.</param>
/// <param name="Methods">Compression methods of the archive, for display.</param>
/// <param name="OtherFiles">The other files and folders of the archive, which saving carries over.</param>
public sealed record ArchiveSource(ArchiveCompression Kind, string EntryName, string Methods, IReadOnlyList<string> OtherFiles);

/// <summary>Recognises zip archives.</summary>
public static class ZipImage
{
    /// <summary>A local file header ("PK\x03\x04"), or the end record of an empty archive ("PK\x05\x06").</summary>
    public static bool IsZip(ReadOnlySpan<byte> head) =>
        head.Length >= 4 && head[0] == 'P' && head[1] == 'K' && ((head[2] == 3 && head[3] == 4) || (head[2] == 5 && head[3] == 6));
}
