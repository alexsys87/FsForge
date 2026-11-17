using System.Buffers.Binary;
using FsForge.Core.Compression;
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

    // ------------------------------------------- shared by the rebuilding images

    /// <summary>Erase blocks larger than this are not assumed when padding is kept aligned.</summary>
    protected const long MaxAlignment = 256 << 10;

    /// <summary>OpenWrt's metadata and signature blocks at the end of the file, when their checksums are right.</summary>
    private Fwtool? _fwtool;

    /// <summary>
    /// The file ends with OpenWrt's fwtool blocks (metadata, signature): true when their checksums are right (they
    /// are made again when the firmware is rebuilt), false when they are not, null when there are none.
    /// </summary>
    public bool? FwtoolChecksumsValid { get; private set; }

    /// <summary>The fwtool blocks hold a signature, which a rebuilt firmware no longer matches.</summary>
    public bool FwtoolSigned => _fwtool?.HasSignature == true;

    /// <summary>Where the firmware ends without OpenWrt's fwtool blocks.</summary>
    protected long ContentEnd => _fwtool?.ContentEnd ?? Source.Length;

    /// <summary>The checksums the firmware carries for its own content (header CRC, image hashes) are right.</summary>
    public abstract bool ChecksumsValid { get; }

    /// <summary>Looks for OpenWrt's fwtool blocks at the end of the file, behind <paramref name="notBefore"/>.</summary>
    protected void ReadFwtool(long notBefore, CancellationToken ct)
    {
        if (Fwtool.Read(Source, notBefore, ct) is { } fw)
        {
            FwtoolChecksumsValid = fw.CrcValid;
            if (fw.CrcValid) _fwtool = fw;
        }
    }

    protected IBlockSource Slice(long from, long to) => new SubBlockSource(Source, from, to - from, ownsInner: false);

    /// <summary>
    /// The firmware up to the end of its content with <paramref name="data"/> in place of the root filesystem behind
    /// the image: everything in front stays, and what followed the filesystem (OpenWrt's 0xdeadc0de markers, other
    /// data) is put behind the new one on the same erase block boundaries.
    /// </summary>
    protected List<IBlockSource> TrailingPieces(FirmwarePart part, IBlockSource data)
    {
        var pieces = new List<IBlockSource>();
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
            pieces.Add(Slice(tail.Rest, ContentEnd));
        }
        else if (tail.Start < ContentEnd)
        {
            long at = Shifted(tail.Start, newEnd, tail.Align);
            pieces.Add(Fill(at - newEnd, tail.Fill));
            pieces.Add(Slice(tail.Start, ContentEnd));
        }
        else if (tail.Align > 1)
            pieces.Add(Fill(RoundUp(newEnd, tail.Align) - newEnd, tail.Fill));
        return pieces;
    }

    /// <summary>
    /// Writes the new firmware: the pieces, then OpenWrt's fwtool blocks with their checksums made over them; warns
    /// when it grew and about signatures that no longer match.
    /// </summary>
    protected void WritePieces(Stream output, List<IBlockSource> pieces, List<string> warnings, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        long total = pieces.Sum(p => p.Length) + (Source.Length - ContentEnd);
        if (total > Source.Length)
            warnings.Add($"The firmware grew from {DiskText.Size(Source.Length)} to {DiskText.Size(total)}: make sure it still fits into the flash partition it is written to.");
        if (_fwtool is null && HasFwtoolTrailer())
            warnings.Add("The firmware ends with an OpenWrt metadata or signature block (fwtool) whose checksum does not match the file it came with. It was carried over unchanged: " +
                         "sysupgrade may refuse the file or need -F.");
        else if (_fwtool?.HasSignature == true)
            warnings.Add("The firmware is signed (an OpenWrt fwtool signature block). The checksums of its metadata and signature blocks were made again, " +
                         "but the signature belongs to the old image: a device that checks image signatures will refuse it.");

        byte[] buf = new byte[1 << 20];
        long done = 0;
        uint crc = ~0u;
        foreach (IBlockSource piece in pieces)
        {
            for (long o = 0; o < piece.Length;)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buf.Length, piece.Length - o);
                piece.ReadExactly(o, buf.AsSpan(0, n));
                output.Write(buf, 0, n);
                if (_fwtool is not null) crc = Fwtool.RawUpdate(crc, buf.AsSpan(0, n));
                o += n;
                done += n;
                progress?.Report(new ProgressInfo("write", done, total));
            }
        }
        // OpenWrt's metadata (and signature) behind the new image, their checksums made over it.
        _fwtool?.Write(output, Source, crc, ct);
    }

    /// <summary>
    /// What a rewrite keeps behind the image: what followed the root filesystem behind it when that is the part
    /// rewritten, and the fwtool blocks without their checksums.
    /// </summary>
    protected IEnumerable<(string Name, IBlockSource Data)> BehindSections(FirmwarePart? trailing, FirmwarePart open)
    {
        if (trailing is not null && open.Name == trailing.Name)
        {
            Tail tail = TailOf(trailing);
            // Behind a new filesystem one marker can serve more block sizes than before (an 8 KiB boundary may also be
            // the first 16 KiB one), so only what follows the markers is compared.
            if (tail.Markers.Count > 0)
                yield return ("data after the padding markers", new SubBlockSource(Source, tail.Rest, ContentEnd - tail.Rest, ownsInner: false));
            else yield return ("data after " + trailing.Name, new SubBlockSource(Source, tail.Start, ContentEnd - tail.Start, ownsInner: false));
        }
        if (_fwtool is not null)
            foreach ((string name, IBlockSource data) in _fwtool.Sections(Source)) yield return (name, data);
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
    protected sealed record Tail(long Start, long Align, byte Fill, List<long> Markers, long Rest);

    /// <summary>
    /// What follows a root filesystem behind the image. Only the end of a SquashFS, EROFS, CramFS, romfs or ext
    /// filesystem is known; any other one runs to the end of the file.
    /// </summary>
    protected Tail TailOf(FirmwarePart part)
    {
        long eof = ContentEnd;
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
    protected long FilesystemLength(long at, PartitionContent content)
    {
        long rest = ContentEnd - at;
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
    protected static long Alignment(long offset) => offset == 0 ? MaxAlignment : Math.Min(offset & -offset, MaxAlignment);

    protected static long RoundUp(long n, long align) => (n + align - 1) / align * align;

    /// <summary>The first position at or after <paramref name="min"/> that lies on the same boundary as <paramref name="old"/>.</summary>
    protected static long Shifted(long old, long min, long align)
    {
        long delta = min - old;
        long steps = delta <= 0 ? -(-delta / align) : (delta + align - 1) / align;
        return old + steps * align;
    }

    protected static IBlockSource Fill(long length, byte value)
    {
        byte[] b = new byte[length];
        if (value != 0) b.AsSpan().Fill(value);
        return new MemoryBlockSource(b);
    }

    protected static uint CrcOf(IEnumerable<IBlockSource> pieces, CancellationToken ct)
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
        Source.Length >= 16 && Source.ReadBytes(Source.Length - 16, 4).AsSpan().SequenceEqual("FWx0"u8);

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
