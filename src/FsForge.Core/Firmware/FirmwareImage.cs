using FsForge.Core.Images;
using FsForge.Core.Tar;

namespace FsForge.Core.Firmware;

/// <summary>Something a firmware file holds: a kernel, a ramdisk, a root filesystem.</summary>
public sealed class FirmwarePart
{
    public int Index { get; init; }

    /// <summary>"kernel", "ramdisk", "rootfs", "image 2".</summary>
    public string Name { get; init; } = "";

    /// <summary>Position of the part in the file, in bytes.</summary>
    public long Offset { get; init; }

    /// <summary>Size of the part as it is stored in the file, in bytes.</summary>
    public long Length { get; init; }

    public PartitionContent Content { get; init; }

    /// <summary>Exact kind of the content when known ("SquashFS", "cpio (gzip)", "ext2 (gzip)").</summary>
    public string ContentName { get; init; } = "";

    /// <summary>More about the part: its type in the header, its compression, where it was found.</summary>
    public string Detail { get; init; } = "";

    /// <summary>
    /// The compression the content has to be unpacked from before it can be read (a gzipped ext2 ramdisk);
    /// null when it is read in place. A compressed cpio archive is read as it is and has none here.
    /// </summary>
    public ArchiveCompression? Packed { get; init; }

    /// <summary>The part lies behind the image, outside what its header covers (OpenWrt's root filesystem behind the kernel).</summary>
    public bool BehindImage { get; init; }

    /// <summary>The part holds a filesystem, a UBI image or a cpio archive this program can open.</summary>
    public bool IsReadable => Content is PartitionContent.SquashFs or PartitionContent.Ext or PartitionContent.Erofs
        or PartitionContent.Cramfs or PartitionContent.Romfs or PartitionContent.Fat or PartitionContent.F2fs
        or PartitionContent.Cpio or PartitionContent.Ubi or PartitionContent.Ubifs or PartitionContent.Jffs2;

    public override string ToString()
    {
        string what = ContentName.Length > 0 ? ContentName : Length == 0 ? "empty" : "unknown content";
        return $"{Name}: {what}, {DiskText.Size(Length)} at 0x{Offset:X}{(Detail.Length > 0 ? ", " + Detail : "")}";
    }
}

/// <summary>
/// A vendor firmware container: a header in front of a kernel and a root filesystem (U-Boot uImage, ...).
/// It tells exactly where the parts lie, so the filesystem does not have to be searched for.
/// </summary>
public abstract class FirmwareImage
{
    protected FirmwareImage(IBlockSource source) => Source = source;

    /// <summary>The whole firmware file.</summary>
    public IBlockSource Source { get; }

    /// <summary>"U-Boot uImage", "U-Boot uImage (multi-file)".</summary>
    public abstract string Kind { get; }

    /// <summary>A short name of the kind for a status line: "uImage", "FIT".</summary>
    public abstract string ShortKind { get; }

    /// <summary>The kind with the name the image carries: "U-Boot uImage 'OpenWrt Linux-6.6.73'".</summary>
    public abstract string Description { get; }

    public IReadOnlyList<FirmwarePart> Parts { get; protected set; } = Array.Empty<FirmwarePart>();

    /// <summary>Non-fatal problems found while reading (wrong checksums, a truncated file).</summary>
    public List<string> Warnings { get; } = new();

    public IReadOnlyList<FirmwarePart> Readable => Parts.Where(p => p.IsReadable).ToList();

    /// <summary>The part opened when none is asked for: the largest readable one (the root filesystem).</summary>
    public FirmwarePart? PickDefault() => Parts.Where(p => p.IsReadable).MaxBy(p => p.Length);

    /// <summary>A part by name (case-insensitive) or by number.</summary>
    public FirmwarePart? Find(string nameOrIndex) =>
        Parts.FirstOrDefault(p => string.Equals(p.Name, nameOrIndex, StringComparison.OrdinalIgnoreCase))
        ?? (int.TryParse(nameOrIndex, out int i) ? Parts.FirstOrDefault(p => p.Index == i) : null);

    /// <summary>The stored bytes of a part (still compressed when <see cref="FirmwarePart.Packed"/> is set).</summary>
    public IBlockSource Open(FirmwarePart part) =>
        new SubBlockSource(Source, part.Offset, Math.Max(0, Math.Min(part.Length, Source.Length - part.Offset)), ownsInner: false);

    /// <summary>True when the firmware can be written again around new content of <paramref name="part"/>; else the reason.</summary>
    public abstract bool CanRebuild(FirmwarePart part, out string reason);

    /// <summary>Writes the firmware again with <paramref name="data"/> as the new content of <paramref name="part"/>; everything else is kept.</summary>
    public abstract void Write(Stream output, FirmwarePart part, IBlockSource data, List<string> warnings,
        IProgress<ProgressInfo>? progress, CancellationToken ct);

    /// <summary>What a rewrite of <paramref name="open"/> keeps as it was, to check that it survived.</summary>
    public abstract IEnumerable<(string Name, IBlockSource Data)> FixedSections(FirmwarePart open);

    /// <summary>True when the file starts with a firmware header this program understands.</summary>
    public static bool IsFirmware(IBlockSource source) => UImage.IsUImage(source) || FitImage.IsFit(source);

    /// <summary>Reads the firmware header and finds the parts; null when the file has none.</summary>
    public static FirmwareImage? TryRead(IBlockSource source, CancellationToken ct = default) =>
        UImage.IsUImage(source) ? UImage.Read(source, ct) : FitImage.IsFit(source) ? FitImage.Read(source, ct) : null;

    // ---------------------------------------------- filesystem behind the image

    /// <summary>How far behind the image a root filesystem is looked for.</summary>
    private const long TrailingScan = 256L << 20;

    /// <summary>
    /// The start of a root filesystem behind the image, or null. OpenWrt puts SquashFS (or UBI, JFFS2) there,
    /// often at the next erase block boundary.
    /// </summary>
    protected long? FindTrailing(long end, CancellationToken ct)
    {
        long start = SkipErased((end + 3) & ~3L, ct);
        if (start >= Source.Length) return null;
        long limit = Math.Min(Source.Length - start, TrailingScan);
        long found = Jffs2OrOtherAt(start) ? start : -1;
        if (found < 0)
        {
            long squash = SquashFs.SquashFsReader.FindEmbedded(Source, start, limit, ct);
            long ubi = Ubifs.UbiImage.FindEmbedded(Source, start, squash > 0 ? squash - start : limit, ct);
            found = ubi > 0 ? ubi : squash;
        }
        if (found < 0)
        {
            // Other filesystems are only looked for at erase block boundaries close behind the kernel.
            for (long at = (start + 0xFFFF) & ~0xFFFFL; at < start + Math.Min(limit, 4L << 20); at += 0x10000)
            {
                ct.ThrowIfCancellationRequested();
                if (Jffs2OrOtherAt(at)) { found = at; break; }
            }
        }
        return found >= 0 ? found : null;
    }

    /// <summary>The first word at or after <paramref name="at"/> that is not erased flash (0xFF), as padding to an erase block leaves it.</summary>
    protected long SkipErased(long at, CancellationToken ct)
    {
        byte[] buf = new byte[1 << 16];
        long limit = Math.Min(Source.Length, at + (16L << 20));
        while (at < limit)
        {
            ct.ThrowIfCancellationRequested();
            int n = Source.Read(at, buf.AsSpan(0, (int)Math.Min(buf.Length, limit - at))) & ~3;
            if (n <= 0) break;
            int i = buf.AsSpan(0, n).IndexOfAnyExcept((byte)0xFF);
            if (i >= 0) return at + (i & ~3);
            at += n;
        }
        return at;
    }

    /// <summary>True when a readable filesystem other than SquashFS starts here.</summary>
    protected bool Jffs2OrOtherAt(long at)
    {
        using var tail = new SubBlockSource(Source, at, Source.Length - at, ownsInner: false);
        (PartitionContent content, _, _) = Identify(tail);
        return content is PartitionContent.Ext or PartitionContent.Erofs or PartitionContent.Cramfs or PartitionContent.Romfs
            or PartitionContent.F2fs or PartitionContent.Ubi or PartitionContent.Ubifs or PartitionContent.Jffs2 or PartitionContent.SquashFs;
    }

    // ------------------------------------------------------------ content

    /// <summary>How much of a compressed part is unpacked to see what it holds.</summary>
    private const int SniffBytes = 1 << 20;

    /// <summary>
    /// What a part holds: a filesystem or UBI image, a (compressed) cpio archive, or a filesystem packed with
    /// gzip, xz, zstd, bzip2 or lz4 (an old-style initrd).
    /// </summary>
    internal static (PartitionContent Content, string Name, ArchiveCompression? Packed) Identify(IBlockSource data)
    {
        if (data.Length == 0) return (PartitionContent.Empty, "", null);
        (PartitionContent content, string name) = IdentifyPlain(data);
        if (content is not (PartitionContent.Unknown or PartitionContent.Empty)) return (content, name, null);

        byte[] head = new byte[16];
        int n = data.Read(0, head);
        if (ArchiveCodec.DetectStreamCompression(head.AsSpan(0, n)) is not { } packed) return (content, name, null);
        byte[] prefix;
        bool whole;
        try
        {
            (prefix, whole) = UnpackPrefix(data, packed);
        }
        catch (Exception ex) when (ex is InvalidDataException or ZstdSharp.ZstdException or EndOfStreamException or FsException or IOException)
        {
            return (PartitionContent.Unknown, $"{ArchiveCodec.DisplayName(packed)} data (damaged)", null);
        }
        // Only the start was unpacked: let the filesystem checks believe the rest is there.
        using var unpacked = new PrefixSource(prefix, whole ? prefix.Length : long.MaxValue / 4);
        (PartitionContent inner, string innerName) = IdentifyPlain(unpacked);
        if (inner is PartitionContent.Unknown or PartitionContent.Empty)
            return (PartitionContent.Unknown, $"{ArchiveCodec.DisplayName(packed)} data", null);
        return (inner, $"{innerName} ({ArchiveCodec.DisplayName(packed)})", packed);
    }

    private static (PartitionContent Content, string Name) IdentifyPlain(IBlockSource data)
    {
        if (Cpio.CpioReader.IsCpio(data, out ArchiveCompression c))
            return (PartitionContent.Cpio, c == ArchiveCompression.None ? "cpio" : $"cpio ({ArchiveCodec.DisplayName(c)})");
        (PartitionContent content, string name, string _) = PartitionTable.Identify(data, 0, data.Length);
        if (content is PartitionContent.Unknown or PartitionContent.Empty)
        {
            if (Ubifs.UbifsReader.IsUbifs(data)) return (PartitionContent.Ubifs, "UBIFS");
            if (Jffs2.Jffs2Reader.IsJffs2(data)) return (PartitionContent.Jffs2, "JFFS2");
        }
        return (content, name);
    }

    private static (byte[] Data, bool Whole) UnpackPrefix(IBlockSource data, ArchiveCompression packed)
    {
        using Stream input = ArchiveCodec.OpenDecompressor(new BlockSourceStream(data, 0, data.Length), packed);
        byte[] buf = new byte[SniffBytes];
        int got = 0, n;
        while (got < buf.Length && (n = input.Read(buf, got, buf.Length - got)) > 0) got += n;
        bool whole = got < buf.Length || input.ReadByte() < 0;
        return (buf[..got], whole);
    }

    /// <summary>The unpacked start of a compressed part; reads past it give zeros.</summary>
    private sealed class PrefixSource(byte[] data, long length) : IBlockSource
    {
        public long Length => length;

        public int Read(long offset, Span<byte> buffer)
        {
            if (offset < 0 || offset >= length) return 0;
            int n = (int)Math.Min(buffer.Length, length - offset);
            buffer[..n].Clear();
            if (offset < data.Length) data.AsSpan((int)offset, (int)Math.Min(n, data.Length - offset)).CopyTo(buffer);
            return n;
        }

        public void Dispose() { }
    }
}
