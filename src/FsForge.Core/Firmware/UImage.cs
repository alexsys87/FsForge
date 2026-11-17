using System.Buffers.Binary;
using System.Text;
using FsForge.Core.Compression;
using FsForge.Core.Images;

namespace FsForge.Core.Firmware;

/// <summary>
/// The legacy U-Boot image ("uImage", made by <c>mkimage -f legacy</c>): a 64-byte big-endian header with
/// CRC-32 checksums of itself and of the data, in front of a kernel, a ramdisk (uInitrd), a filesystem or
/// several images at once (type "multi").
/// </summary>
/// <remarks>
/// Many router firmwares (OpenWrt sysupgrade images for ath79, ramips, lantiq...) put a uImage with the kernel
/// alone at the start of the file and the root filesystem right behind it, outside what the header covers.
/// That filesystem is looked for after the image and listed as the part "rootfs".
/// </remarks>
public sealed class UImage : FirmwareImage
{
    public const uint Magic = 0x27051956;
    public const int HeaderSize = 64;

    private const byte TypeMulti = 4;

    /// <summary>How far behind the image a root filesystem is looked for.</summary>
    private const long TrailingScan = 256L << 20;

    private UImage(IBlockSource source) : base(source) { }

    public string Name { get; private init; } = "";
    public byte Os { get; private init; }
    public byte Architecture { get; private init; }
    public byte Type { get; private init; }
    public byte Compression { get; private init; }
    public uint LoadAddress { get; private init; }
    public uint EntryPoint { get; private init; }

    /// <summary>Creation time from the header, in seconds since 1970.</summary>
    public uint Timestamp { get; private init; }

    /// <summary>Size of the data behind the header as the header gives it.</summary>
    public uint DataSize { get; private init; }

    public uint DataCrc { get; private init; }

    /// <summary>The CRC-32 of the data matches the header (false also when the file is too short to hold the data).</summary>
    public bool DataCrcMatches { get; private set; }

    public string TypeName => TypeText(Type);
    public string OsName => Os switch { 5 => "Linux", 17 => "U-Boot", 0 => "none", _ => $"OS {Os}" };
    public string ArchitectureName => ArchText(Architecture);
    public string CompressionName => CompressionText(Compression);

    public override string Kind => Type == TypeMulti ? "U-Boot uImage (multi-file)" : "U-Boot uImage";

    public override string Description => Name.Length > 0 ? $"{Kind} '{Name}'" : Kind;

    /// <summary>The file starts with a uImage header whose own checksum is right.</summary>
    public static bool IsUImage(IBlockSource source)
    {
        if (source.Length < HeaderSize) return false;
        Span<byte> h = stackalloc byte[HeaderSize];
        if (source.Read(0, h) < HeaderSize) return false;
        return BinaryPrimitives.ReadUInt32BigEndian(h) == Magic && HeaderCrcMatches(h);
    }

    private static bool HeaderCrcMatches(ReadOnlySpan<byte> h)
    {
        Span<byte> copy = stackalloc byte[HeaderSize];
        h.CopyTo(copy);
        copy[4..8].Clear();
        return Crc32.Compute(copy) == BinaryPrimitives.ReadUInt32BigEndian(h[4..]);
    }

    public static UImage Read(IBlockSource source, CancellationToken ct = default)
    {
        byte[] h = source.ReadBytes(0, HeaderSize);
        if (BinaryPrimitives.ReadUInt32BigEndian(h) != Magic || !HeaderCrcMatches(h))
            throw new FsException("The file does not start with a valid U-Boot uImage header.");
        var image = new UImage(source)
        {
            Timestamp = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(8)),
            DataSize = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(12)),
            LoadAddress = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(16)),
            EntryPoint = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(20)),
            DataCrc = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(24)),
            Os = h[28],
            Architecture = h[29],
            Type = h[30],
            Compression = h[31],
            Name = CString(h.AsSpan(32, 32)),
        };
        image.ReadParts(ct);
        return image;
    }

    private void ReadParts(CancellationToken ct)
    {
        long end = HeaderSize + (long)DataSize;
        if (end > Source.Length)
            Warnings.Add($"The uImage header gives {DataSize} bytes of data, but the file ends {end - Source.Length} bytes before that: it is truncated.");
        else
        {
            DataCrcMatches = DataCrcOf(HeaderSize, DataSize, ct) == DataCrc;
            if (!DataCrcMatches)
                Warnings.Add("The CRC-32 of the uImage data does not match its header: the image was changed or damaged, and U-Boot would refuse to boot it.");
        }

        var parts = new List<FirmwarePart>();
        if (Type == TypeMulti) ReadMulti(parts, end);
        else parts.Add(MakePart(parts.Count, PartName(Type), HeaderSize, Math.Min(DataSize, Math.Max(0, Source.Length - HeaderSize)), Type));

        if (FindTrailing(end, ct) is { } rootfs)
        {
            (PartitionContent content, string contentName, var packed) = Identify(new SubBlockSource(Source, rootfs, Source.Length - rootfs, ownsInner: false));
            long gap = rootfs - end;
            parts.Add(new FirmwarePart
            {
                Index = parts.Count, Name = parts.Any(p => p.Name == "rootfs") ? "trailing" : "rootfs", Offset = rootfs, Length = Source.Length - rootfs,
                Content = content, ContentName = contentName, Packed = packed,
                Detail = gap == 0 ? "right after the uImage, outside its header" : $"{gap} bytes after the end of the uImage, outside its header",
            });
        }
        Parts = parts;
    }

    /// <summary>A multi-file image: a zero-terminated list of sizes, then the images, each but the last padded to 4 bytes.</summary>
    private void ReadMulti(List<FirmwarePart> parts, long end)
    {
        var sizes = new List<uint>();
        long pos = HeaderSize;
        while (true)
        {
            if (pos + 4 > Math.Min(end, Source.Length) || sizes.Count > 64)
                throw new FsException("The list of images of the multi-file uImage is damaged: it has no end.");
            uint size = BinaryPrimitives.ReadUInt32BigEndian(Source.ReadBytes(pos, 4));
            pos += 4;
            if (size == 0) break;
            sizes.Add(size);
        }
        for (int i = 0; i < sizes.Count; i++)
        {
            long available = Math.Max(0, Math.Min(sizes[i], Source.Length - pos));
            if (pos + sizes[i] > end)
                Warnings.Add($"Image {i} of the multi-file uImage reaches beyond the data the header covers.");
            // The type of each image is not stored. U-Boot's bootm takes the first for the kernel; the others
            // are named after what they hold.
            FirmwarePart part = MakePart(parts.Count, null, pos, available, i == 0 ? (byte)2 : (byte)0);
            if (parts.Any(p => p.Name == part.Name)) part = Renamed(part, $"{part.Name}{i}");
            parts.Add(part);
            pos += (sizes[i] + 3) & ~3L;
        }
    }

    /// <param name="name">Name of the part; null names an image of a multi-file uImage after its content.</param>
    private FirmwarePart MakePart(int index, string? name, long offset, long length, byte type)
    {
        using var data = new SubBlockSource(Source, offset, length, ownsInner: false);
        (PartitionContent content, string contentName, var packed) = Identify(data);
        // A kernel is named as such; its compressed data says nothing to the filesystem checks.
        if (type is 2 or 14 && content is PartitionContent.Unknown)
            (content, contentName, packed) = (PartitionContent.Unknown, Compression == 0 || Type == TypeMulti ? "kernel" : $"kernel ({CompressionName})", null);
        else if (content is PartitionContent.Unknown && length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(data.ReadBytes(0, 4)) == 0xD00DFEED)
            contentName = "device tree";
        bool readable = new FirmwarePart { Content = content }.IsReadable;
        name ??= type == 2 && !readable ? "kernel"
            : content == PartitionContent.Cpio || packed is not null ? "ramdisk"
            : readable ? "rootfs"
            : contentName == "device tree" ? "fdt"
            : $"image{index}";
        string detail = Type == TypeMulti
            ? $"image {index} of the multi-file uImage"
            : $"type {TypeName}, compression {CompressionName}";
        return new FirmwarePart
        {
            Index = index, Name = name, Offset = offset, Length = length,
            Content = content, ContentName = contentName, Packed = packed, Detail = detail,
        };
    }

    private static FirmwarePart Renamed(FirmwarePart p, string name) => new()
    {
        Index = p.Index, Name = name, Offset = p.Offset, Length = p.Length,
        Content = p.Content, ContentName = p.ContentName, Packed = p.Packed, Detail = p.Detail,
    };

    /// <summary>Name of the only image of a uImage, after its type.</summary>
    private static string PartName(byte type) => type switch
    {
        2 or 14 => "kernel",
        3 => "ramdisk",
        7 => "filesystem",
        8 => "fdt",
        5 => "firmware",
        6 => "script",
        _ => TypeText(type),
    };

    /// <summary>
    /// The start of a root filesystem behind the image, or null. OpenWrt puts SquashFS (or UBI, JFFS2) there,
    /// often at the next erase block boundary.
    /// </summary>
    private long? FindTrailing(long end, CancellationToken ct)
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
    private long SkipErased(long at, CancellationToken ct)
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
    private bool Jffs2OrOtherAt(long at)
    {
        using var tail = new SubBlockSource(Source, at, Source.Length - at, ownsInner: false);
        (PartitionContent content, _, _) = Identify(tail);
        return content is PartitionContent.Ext or PartitionContent.Erofs or PartitionContent.Cramfs or PartitionContent.Romfs
            or PartitionContent.F2fs or PartitionContent.Ubi or PartitionContent.Ubifs or PartitionContent.Jffs2 or PartitionContent.SquashFs;
    }

    private uint DataCrcOf(long offset, long length, CancellationToken ct)
    {
        byte[] buf = new byte[1 << 20];
        uint crc = 0;
        for (long done = 0; done < length;)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(buf.Length, length - done);
            Source.ReadExactly(offset + done, buf.AsSpan(0, n));
            crc = Crc32.Update(crc, buf.AsSpan(0, n));
            done += n;
        }
        return crc;
    }

    private static string CString(ReadOnlySpan<byte> field)
    {
        int n = field.IndexOf((byte)0);
        if (n >= 0) field = field[..n];
        var sb = new StringBuilder(field.Length);
        foreach (byte b in field) sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '?');
        return sb.ToString().Trim();
    }

    public static string TypeText(byte type) => type switch
    {
        1 => "standalone",
        2 => "kernel",
        3 => "ramdisk",
        4 => "multi",
        5 => "firmware",
        6 => "script",
        7 => "filesystem",
        8 => "flat_dt",
        14 => "kernel_noload",
        _ => $"type {type}",
    };

    public static string CompressionText(byte c) => c switch
    {
        0 => "none",
        1 => "gzip",
        2 => "bzip2",
        3 => "lzma",
        4 => "lzo",
        5 => "lz4",
        6 => "zstd",
        _ => $"compression {c}",
    };

    public static string ArchText(byte a) => a switch
    {
        2 => "ARM",
        3 => "x86",
        5 => "MIPS",
        6 => "MIPS64",
        7 => "PowerPC",
        9 => "SuperH",
        14 => "MicroBlaze",
        15 => "Nios II",
        21 => "OpenRISC",
        22 => "AArch64",
        23 => "ARC",
        24 => "x86_64",
        25 => "Xtensa",
        26 => "RISC-V",
        _ => $"architecture {a}",
    };
}
