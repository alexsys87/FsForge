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

    /// <summary>Erase blocks larger than this are not assumed when padding is kept aligned.</summary>
    private const long MaxAlignment = 256 << 10;

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
        {
            pieces.Add(Slice(0, part.Offset));
            pieces.Add(data);
            long newEnd = part.Offset + data.Length;
            Tail tail = TailOf(part);
            if (tail.Markers.Count > 0)
            {
                // A marker on the first boundary of each of these sizes behind the new filesystem (one boundary can serve
                // several sizes), padding in between.
                long pos = newEnd;
                foreach (long at in tail.Markers.Select(size => RoundUp(newEnd, size)).Distinct().Order())
                {
                    pieces.Add(Fill(at - pos, tail.Fill));
                    pieces.Add(new MemoryBlockSource(new byte[] { 0xDE, 0xAD, 0xC0, 0xDE }));
                    pos = at + 4;
                }
                pieces.Add(Slice(tail.Rest, Source.Length));
            }
            else if (tail.Start < Source.Length)
            {
                long at = Shifted(tail.Start, newEnd, tail.Align);
                pieces.Add(Fill(at - newEnd, tail.Fill));
                pieces.Add(Slice(tail.Start, Source.Length));
            }
            else if (tail.Align > 1)
                pieces.Add(Fill(RoundUp(newEnd, tail.Align) - newEnd, tail.Fill));
        }
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
                pieces.Add(Slice(_trailing.Offset, Source.Length));
            }
            else pieces.Add(Slice(_end, Source.Length));
        }

        long total = pieces.Sum(p => p.Length);
        if (total > Source.Length)
            warnings.Add($"The firmware grew from {DiskText.Size(Source.Length)} to {DiskText.Size(total)}: make sure it still fits into the flash partition it is written to.");
        if (HasFwtoolTrailer())
            warnings.Add("The firmware ends with an OpenWrt metadata or signature block (fwtool). It was carried over unchanged, but it was made for the old image: " +
                         "its checksum or signature may no longer match, and sysupgrade may then refuse the file or need -F.");

        byte[] buf = new byte[1 << 20];
        long done = 0;
        foreach (IBlockSource piece in pieces)
        {
            for (long o = 0; o < piece.Length;)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buf.Length, piece.Length - o);
                piece.ReadExactly(o, buf.AsSpan(0, n));
                output.Write(buf, 0, n);
                o += n;
                done += n;
                progress?.Report(new ProgressInfo("write", done, total));
            }
        }
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
            if (p.Name != open.Name) yield return (p.Name, Open(p));
        if (_trailing is not null && open.Name == _trailing.Name)
        {
            Tail tail = TailOf(_trailing);
            // Behind a new filesystem one marker can serve more block sizes than before (an 8 KiB boundary may also be
            // the first 16 KiB one), so only what follows the markers is compared.
            if (tail.Markers.Count > 0)
                yield return ("data after the padding markers", new SubBlockSource(Source, tail.Rest, Source.Length - tail.Rest, ownsInner: false));
            else yield return ("data after " + _trailing.Name, new SubBlockSource(Source, tail.Start, Source.Length - tail.Start, ownsInner: false));
        }
    }

    /// <summary>What follows a root filesystem behind the image.</summary>
    /// <param name="Start">Where it starts, the padding behind the filesystem skipped (the end of the file when nothing follows).</param>
    /// <param name="Align">The boundary it has to stay on when it is moved as a whole.</param>
    /// <param name="Fill">The byte the padding is made of.</param>
    /// <param name="Markers">
    /// When it starts with OpenWrt's 0xdeadc0de markers: the erase block sizes (4 KiB to 256 KiB) whose first boundary
    /// behind the filesystem holds one, smallest first. Empty when it does not start so.
    /// </param>
    /// <param name="Rest">Where the data behind the markers starts.</param>
    private sealed record Tail(long Start, long Align, byte Fill, List<long> Markers, long Rest);

    /// <summary>
    /// What follows a root filesystem behind the image. Only the end of a SquashFS, EROFS, CramFS, romfs or ext
    /// filesystem is known; any other one runs to the end of the file.
    /// </summary>
    private Tail TailOf(FirmwarePart part)
    {
        long eof = Source.Length;
        long fsEnd = part.Packed is null ? part.Offset + FilesystemLength(part.Offset, part.Content) : eof;
        long start = fsEnd;
        byte[] buf = new byte[1 << 16];
        while (start < eof)
        {
            int n = Source.Read(start, buf.AsSpan(0, (int)Math.Min(buf.Length, eof - start)));
            if (n <= 0) break;
            int i = buf.AsSpan(0, n).IndexOfAnyExcept((byte)0, (byte)0xFF);
            if (i >= 0) { start += i; break; }
            start += n;
        }
        byte fill = start > fsEnd ? Source.ReadBytes(start - 1, 1)[0] : (byte)0xFF;
        if (start >= eof) return new Tail(eof, eof > fsEnd ? Alignment(eof) : 1, fill, new List<long>(), eof);

        // OpenWrt's padjffs2 puts 0xdeadc0de at the first 4, 8 ... 256 KiB boundaries behind the filesystem, so that the
        // kernel finds one at the start of the next erase block, whatever size the flash has. What matters is which
        // of these boundaries hold a marker.
        var found = new HashSet<long>();
        long rest = start;
        byte[] word = new byte[4];
        for (long at = start; (at & 3) == 0 && at + 4 <= eof && at < fsEnd + 2 * MaxAlignment; at += 4)
        {
            Source.ReadExactly(at, word);
            if (BinaryPrimitives.ReadUInt32BigEndian(word) == 0xDEADC0DE) { found.Add(at); rest = at + 4; }
            else if (word.AsSpan().IndexOfAnyExcept((byte)0, (byte)0xFF) >= 0) break;
        }
        var markers = new List<long>();
        for (long size = 4096; size <= MaxAlignment; size <<= 1)
            if (found.Contains(RoundUp(fsEnd, size))) markers.Add(size);
        if (markers.Count == 0) return new Tail(start, Alignment(start), fill, markers, start);
        return new Tail(start, Math.Max(Alignment(start), markers.Max()), fill, markers, rest);
    }

    /// <summary>
    /// How long the filesystem at <paramref name="at"/> is, as its superblock gives it (SquashFS, EROFS, CramFS, romfs,
    /// ext2/3/4); anything else, or a size that makes no sense, runs to the end of the file.
    /// </summary>
    private long FilesystemLength(long at, PartitionContent content)
    {
        long rest = Source.Length - at;
        long length = 0;
        try
        {
            switch (content)
            {
                case PartitionContent.SquashFs:
                    length = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(Source.ReadBytes(at + 40, 8)), (ulong)long.MaxValue);
                    break;
                case PartitionContent.Erofs:
                {
                    byte[] sb = Source.ReadBytes(at + 1024, 40);
                    length = sb[12] is >= 9 and <= 16 ? (long)BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(36)) << sb[12] : 0;
                    break;
                }
                case PartitionContent.Cramfs:
                {
                    byte[] sb = Source.ReadBytes(at, 12);
                    bool big = BinaryPrimitives.ReadUInt32BigEndian(sb) == 0x28CD3D45;
                    uint flags = big ? BinaryPrimitives.ReadUInt32BigEndian(sb.AsSpan(8)) : BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(8));
                    // Only the version 2 superblock (flag 1) records the size.
                    if ((flags & 1) != 0) length = big ? BinaryPrimitives.ReadUInt32BigEndian(sb.AsSpan(4)) : BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(4));
                    break;
                }
                case PartitionContent.Romfs:
                    length = BinaryPrimitives.ReadUInt32BigEndian(Source.ReadBytes(at + 8, 4));
                    break;
                case PartitionContent.Ext:
                {
                    byte[] sb = Source.ReadBytes(at + 1024, 28);
                    uint log = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(24));
                    if (log <= 6) length = (long)BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(4)) << (int)(10 + log);
                    break;
                }
            }
        }
        catch (FsException)
        {
            length = 0;
        }
        return length > 0 && length <= rest ? length : rest;
    }

    /// <summary>The largest power of two (up to <see cref="MaxAlignment"/>) the offset is a multiple of.</summary>
    private static long Alignment(long offset) => offset == 0 ? MaxAlignment : Math.Min(offset & -offset, MaxAlignment);

    private static long RoundUp(long n, long align) => (n + align - 1) / align * align;

    /// <summary>The first position at or after <paramref name="min"/> that lies on the same boundary as <paramref name="old"/>.</summary>
    private static long Shifted(long old, long min, long align)
    {
        long delta = min - old;
        long steps = delta <= 0 ? -(-delta / align) : (delta + align - 1) / align;
        return old + steps * align;
    }

    private static IBlockSource Fill(long length, byte value)
    {
        byte[] b = new byte[length];
        if (value != 0) b.AsSpan().Fill(value);
        return new MemoryBlockSource(b);
    }

    private static uint CrcOf(IEnumerable<IBlockSource> pieces, CancellationToken ct)
    {
        byte[] buf = new byte[1 << 20];
        uint crc = 0;
        foreach (IBlockSource p in pieces)
            for (long o = 0; o < p.Length;)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buf.Length, p.Length - o);
                p.ReadExactly(o, buf.AsSpan(0, n));
                crc = Crc32.Update(crc, buf.AsSpan(0, n));
                o += n;
            }
        return crc;
    }

    /// <summary>The file ends with a block of OpenWrt's fwtool (metadata or signature): the trailer magic "FWx0".</summary>
    private bool HasFwtoolTrailer() =>
        Source.Length >= _end + 16 && Source.ReadBytes(Source.Length - 16, 4).AsSpan().SequenceEqual("FWx0"u8);

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
