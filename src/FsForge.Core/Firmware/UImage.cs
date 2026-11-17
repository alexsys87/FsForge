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


    private UImage(IBlockSource source) : base(source) { }

    /// <summary>The 64 bytes of the header as they are in the file.</summary>
    private byte[] _header = Array.Empty<byte>();

    /// <summary>End of the data the header covers.</summary>
    private long _end;

    /// <summary>The images inside the data (one, or those of a multi-file image), in their order.</summary>
    private List<FirmwarePart> _inner = new();

    /// <summary>The root filesystem found behind the image, outside what the header covers.</summary>
    private FirmwarePart? _trailing;

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

    public override string ShortKind => "uImage";

    public override bool ChecksumsValid => DataCrcMatches;

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
        image._header = h;
        image.ReadParts(ct);
        return image;
    }

    private void ReadParts(CancellationToken ct)
    {
        long end = HeaderSize + (long)DataSize;
        ReadFwtool(Math.Min(end, Source.Length), ct);
        if (end > Source.Length)
            Warnings.Add($"The uImage header gives {DataSize} bytes of data, but the file ends {end - Source.Length} bytes before that: it is truncated.");
        else
        {
            DataCrcMatches = DataCrcOf(HeaderSize, DataSize, ct) == DataCrc;
            if (!DataCrcMatches)
                Warnings.Add("The CRC-32 of the uImage data does not match its header: the image was changed or damaged, and U-Boot would refuse to boot it.");
        }

        _end = end;
        var parts = new List<FirmwarePart>();
        if (Type == TypeMulti) ReadMulti(parts, end);
        else parts.Add(MakePart(parts.Count, PartName(Type), HeaderSize, Math.Min(DataSize, Math.Max(0, Source.Length - HeaderSize)), Type));
        _inner = parts.ToList();

        if (FindTrailing(end, ct) is { } rootfs)
        {
            (PartitionContent content, string contentName, var packed) = Identify(new SubBlockSource(Source, rootfs, Source.Length - rootfs, ownsInner: false));
            long gap = rootfs - end;
            _trailing = new FirmwarePart
            {
                Index = parts.Count, Name = parts.Any(p => p.Name == "rootfs") ? "trailing" : "rootfs", Offset = rootfs, Length = Source.Length - rootfs,
                Content = content, ContentName = contentName, Packed = packed, BehindImage = true,
                Detail = gap == 0 ? "right after the uImage, outside its header" : $"{gap} bytes after the end of the uImage, outside its header",
            };
            parts.Add(_trailing);
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

    // ------------------------------------------------------------- writing

    public override bool CanRebuild(FirmwarePart part, out string reason)
    {
        reason = "";
        if (!Parts.Contains(part)) reason = "The part does not belong to this image.";
        else if (_end > Source.Length) reason = "The firmware file is truncated: the uImage data reaches beyond its end.";
        else if (part.Content == PartitionContent.Unknown) reason = $"'{part.Name}' holds nothing this program writes.";
        return reason.Length == 0;
    }

    /// <summary>
    /// Writes the firmware again with <paramref name="data"/> in place of <paramref name="part"/>. An image inside the
    /// uImage gets a new size in the header (and in the list of a multi-file image), a new data CRC and a new header
    /// CRC; whatever follows the data keeps its alignment. A root filesystem behind the image leaves the uImage as it
    /// is; what follows the filesystem (OpenWrt's 0xdeadc0de markers, metadata) is moved so that it stays on the
    /// same erase block boundaries.
    /// </summary>
    public override void Write(Stream output, FirmwarePart part, IBlockSource data, List<string> warnings,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        if (!CanRebuild(part, out string reason)) throw new FsException(reason);
        var pieces = new List<IBlockSource>();
        IBlockSource Slice(long from, long to) => new SubBlockSource(Source, from, to - from, ownsInner: false);

        if (ReferenceEquals(part, _trailing))
            pieces.AddRange(TrailingPieces(part, data));
        else
        {
            // The data of the image: the new part in place of the old one, the others as they are.
            var body = new List<IBlockSource>();
            if (Type == TypeMulti)
            {
                byte[] table = new byte[4 * (_inner.Count + 1)];
                for (int i = 0; i < _inner.Count; i++)
                {
                    long size = ReferenceEquals(_inner[i], part) ? data.Length : _inner[i].Length;
                    if (size > uint.MaxValue) throw new FsException("The new content is too large for a uImage (4 GiB at most).");
                    BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(4 * i), (uint)size);
                }
                body.Add(new MemoryBlockSource(table));
                for (int i = 0; i < _inner.Count; i++)
                {
                    IBlockSource piece = ReferenceEquals(_inner[i], part) ? data : Open(_inner[i]);
                    body.Add(piece);
                    if (i < _inner.Count - 1) body.Add(Fill((4 - piece.Length % 4) % 4, 0));
                }
            }
            else body.Add(data);
            long dataLength = body.Sum(b => b.Length);
            if (dataLength > uint.MaxValue) throw new FsException("The new content is too large for a uImage (4 GiB at most).");

            byte[] header = (byte[])_header.Clone();
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), (uint)dataLength);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), CrcOf(body, ct));
            header.AsSpan(4, 4).Clear();
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), Crc32.Compute(header));
            pieces.Add(new MemoryBlockSource(header));
            pieces.AddRange(body);

            long newEnd = HeaderSize + dataLength;
            if (_trailing is not null)
            {
                // The root filesystem behind the image stays on the same kind of boundary (an erase block).
                long at = Shifted(_trailing.Offset, newEnd, Alignment(_trailing.Offset));
                byte fill = _trailing.Offset > _end ? Source.ReadBytes(_end, 1)[0] : (byte)0xFF;
                pieces.Add(Fill(at - newEnd, fill));
                pieces.Add(Slice(_trailing.Offset, ContentEnd));
            }
            else pieces.Add(Slice(_end, ContentEnd));
        }

        WritePieces(output, pieces, warnings, progress, ct);
    }

    /// <summary>
    /// What the rewrite keeps as it was, to check the written image: the header fields that do not depend on the data,
    /// the other parts, and what follows a root filesystem behind the image.
    /// </summary>
    public override IEnumerable<(string Name, IBlockSource Data)> FixedSections(FirmwarePart open)
    {
        byte[] fields = new byte[4 + 8 + 36];
        _header.AsSpan(8, 4).CopyTo(fields);
        _header.AsSpan(16, 8).CopyTo(fields.AsSpan(4));
        _header.AsSpan(28, 36).CopyTo(fields.AsSpan(12));
        yield return ("header", new MemoryBlockSource(fields));
        foreach (FirmwarePart p in Parts)
            if (p.Name != open.Name)
                yield return (p.Name, ReferenceEquals(p, _trailing) ? new SubBlockSource(Source, p.Offset, ContentEnd - p.Offset, ownsInner: false) : Open(p));
        foreach ((string name, IBlockSource data) in BehindSections(_trailing, open)) yield return (name, data);
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
