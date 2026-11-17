using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FsForge.Core.Images;

namespace FsForge.Core.Firmware;

/// <summary>
/// TP-Link firmware images. Two kinds:
/// <list type="bullet">
/// <item>the classic header of OpenWrt's mktplinkfw (version 1) and mktplinkfw2 (versions 2 and 3): 512 big-endian
/// bytes with the hardware id, the offsets and lengths of the bootloader, kernel and root filesystem, and an MD5 of
/// the whole image computed with a fixed key in place of the sum;</item>
/// <item>the "safeloader" factory image of newer devices (Archer, Deco, EAP...): the length, an MD5 with a key,
/// vendor information and a text partition table ("fwup-ptn os-image base 0x... size 0x...") in front of the
/// partitions it lists (os-image, file-system, soft-version, support-list...).</item>
/// </list>
/// The layouts follow OpenWrt's firmware-utils; the MD5 is checked with the keys those tools use, and an image whose
/// sum does not match that way is only reported, not refused.
/// </summary>
public sealed partial class TpLinkImage : FirmwareImage
{
    private const int ClassicHeaderSize = 0x200;

    /// <summary>
    /// The keys mktplinkfw puts in place of the sum when it computes the MD5 (normal images, images with a bootloader).
    /// mktplinkfw2 (header versions 2 and 3) has a normal key of its own, two bytes apart from mktplinkfw's.
    /// </summary>
    private static readonly byte[] SaltNormal = { 0xDC, 0xD7, 0x3A, 0xA5, 0xC3, 0x95, 0x98, 0xFB, 0xDD, 0xF9, 0xE7, 0xF4, 0x0E, 0xAE, 0x47, 0x38 };
    private static readonly byte[] SaltNormal2 = { 0xDC, 0xD7, 0x3A, 0xA5, 0xC3, 0x95, 0x98, 0xFB, 0xDC, 0xF9, 0xE7, 0xF4, 0x0E, 0xAE, 0x47, 0x37 };
    private static readonly byte[] SaltBoot = { 0x8C, 0xEF, 0x33, 0x5B, 0xD5, 0xC5, 0xCE, 0xFA, 0xA7, 0x9C, 0x28, 0xDA, 0xB2, 0xE9, 0x0F, 0x42 };

    /// <summary>The key tplink-safeloader puts in front of the image when it computes the MD5.</summary>
    private static readonly byte[] SaltSafeloader = { 0x7A, 0x2B, 0x15, 0xED, 0x9B, 0x98, 0x59, 0x6D, 0xE5, 0x04, 0xAB, 0x44, 0xAC, 0x2A, 0x9F, 0x4E };

    private TpLinkImage(IBlockSource source) : base(source) { }

    /// <summary>The classic header: its field places and its bytes as they are in the file.</summary>
    private Classic? _classic;
    private byte[] _header = Array.Empty<byte>();

    /// <summary>The safeloader partition table as read (in the order of its lines).</summary>
    private Table? _table;

    /// <summary>True for the safeloader image, false for the classic header.</summary>
    public bool Safeloader { get; private init; }

    /// <summary>Version of the classic header (1, 2 or 3); 0 for a safeloader image.</summary>
    public uint HeaderVersion { get; private init; }

    public string Vendor { get; private set; } = "";
    public string FirmwareVersion { get; private set; } = "";
    public uint HardwareId { get; private set; }
    public uint HardwareRevision { get; private set; }

    /// <summary>The devices a safeloader image says it fits (its support-list), one per line.</summary>
    public string SupportList { get; private set; } = "";

    /// <summary>The MD5 matches the way OpenWrt's tools compute it (with their key); null when there is none to check.</summary>
    public bool? Md5Matches { get; private set; }

    public override string Kind => Safeloader ? "TP-Link safeloader image" : $"TP-Link firmware image (header version {HeaderVersion})";
    public override string ShortKind => "TP-Link";
    public override string Description => HardwareId != 0 ? $"{Kind} for hardware 0x{HardwareId:X8}" : Kind;
    public override bool ChecksumsValid => Md5Matches != false;

    public static bool IsTpLink(IBlockSource source)
    {
        try
        {
            return ClassicLayout(source) is not null || SafeloaderTable(source) is not null;
        }
        catch (FsException)
        {
            return false;
        }
    }

    public static TpLinkImage Read(IBlockSource source, CancellationToken ct = default)
    {
        if (ClassicLayout(source) is { } classic) return ReadClassic(source, classic, ct);
        if (SafeloaderTable(source) is { } table) return ReadSafeloader(source, table, ct);
        throw new FsException("The file is not a TP-Link firmware image.");
    }

    // ---------------------------------------------------------------- classic

    /// <summary>Where the fields of the classic header lie in each version.</summary>
    private sealed record Classic(uint Version, int HwId, int Md5, int Lengths, string Vendor, string FwVersion)
    {
        /// <summary>
        /// Where the root filesystem starts: rootfs_ofs, or right behind the kernel for an image made with -a (mktplinkfw
        /// then pads kernel_len and leaves the layout's rootfs_ofs in the header); 0 for a combined image.
        /// </summary>
        public long RootfsAt { get; init; }

        /// <summary>The key of the MD5 of an image without a bootloader.</summary>
        public byte[] Salt => Version == 1 ? SaltNormal : SaltNormal2;
    }

    /// <summary>Reads and checks the classic header: the fields must fit the file and the root filesystem must be there.</summary>
    private static Classic? ClassicLayout(IBlockSource source)
    {
        if (source.Length < ClassicHeaderSize + 64) return null;
        byte[] h = source.ReadBytes(0, ClassicHeaderSize);
        uint version = BinaryPrimitives.ReadUInt32BigEndian(h);
        // Version 1: vendor (24) and firmware version (36) strings; versions 2 and 3: a firmware version of 48 bytes.
        Classic layout = version switch
        {
            0x01000000 => new Classic(1, 64, 76, 116, Text(h.AsSpan(4, 24)), Text(h.AsSpan(28, 36))),
            0x02000000 or 0x03000000 => new Classic(version >> 24, 52, 64, 104, "", Text(h.AsSpan(4, 48))),
            _ => null!,
        };
        if (layout is null) return null;
        // kernel_la, kernel_ep, fw_length, kernel_ofs, kernel_len, rootfs_ofs, rootfs_len, boot_ofs, boot_len
        uint U(int i) => BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(layout.Lengths + 4 * i));
        long fwLength = U(2), kernelOfs = U(3), kernelLen = U(4), rootfsOfs = U(5), rootfsLen = U(6), bootOfs = U(7), bootLen = U(8);
        // fw_length is the size of the flash layout (mktplinkfw's fw_max_len): the file is shorter when it was made
        // without padding (-s). Everything the header lists must be in the file.
        long limit = source.Length;
        if (fwLength < ClassicHeaderSize || fwLength < kernelOfs + kernelLen) return null;
        if (kernelLen == 0 || kernelOfs != ClassicHeaderSize || kernelOfs + kernelLen > limit) return null;
        if (bootLen > 0 && bootOfs + bootLen > limit) return null;
        bool Readable(long at)
        {
            using var data = new SubBlockSource(source, at, limit - at, ownsInner: false);
            return new FirmwarePart { Content = Identify(data).Content }.IsReadable;
        }
        // A combined image carries the root filesystem inside the kernel blob: mktplinkfw leaves rootfs_ofs and
        // rootfs_len 0, mktplinkfw2 leaves them 0xFFFFFFFF.
        long rootfsAt = 0;
        if (rootfsOfs != 0 && !(rootfsOfs == uint.MaxValue && rootfsLen == uint.MaxValue))
        {
            bool listed = rootfsOfs >= kernelOfs + kernelLen && rootfsOfs < limit && rootfsOfs + rootfsLen <= limit;
            long behind = kernelOfs + kernelLen;
            if (listed && (rootfsOfs == behind || Readable(rootfsOfs))) rootfsAt = rootfsOfs;
            // -a: the root filesystem follows the (padded) kernel whatever rootfs_ofs says.
            else if (behind + rootfsLen <= limit && rootfsLen > 0 && Readable(behind)) rootfsAt = behind;
            else if (listed) rootfsAt = rootfsOfs;
            else return null;
        }
        // Many byte strings start with 01 00 00 00: the vendor or a filesystem where the header says must confirm it.
        // Versions 2 and 3 have no vendor string, but fixed bytes: 0x55AA at 0x8C and 0xA5 at 0x90 (mktplinkfw2's unk4, unk5).
        bool vendor = layout.Vendor.StartsWith("TP-LINK", StringComparison.OrdinalIgnoreCase) || layout.Vendor.StartsWith("OpenWrt", StringComparison.Ordinal)
                      || (layout.Version > 1 && h[0x8C] == 0x55 && h[0x8D] == 0xAA && h[0x90] == 0xA5);
        if (!vendor && (rootfsAt == 0 || !Readable(rootfsAt))) return null;
        return layout with { RootfsAt = rootfsAt };
    }

    private static TpLinkImage ReadClassic(IBlockSource source, Classic layout, CancellationToken ct)
    {
        byte[] h = source.ReadBytes(0, ClassicHeaderSize);
        uint U(int i) => BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(layout.Lengths + 4 * i));
        long fwLength = U(2), kernelOfs = U(3), kernelLen = U(4), rootfsOfs = U(5), rootfsLen = U(6), bootOfs = U(7), bootLen = U(8);
        long limit = source.Length;
        var image = new TpLinkImage(source)
        {
            _classic = layout,
            _header = h,
            HeaderVersion = layout.Version,
            Vendor = layout.Vendor,
            FirmwareVersion = layout.FwVersion,
            HardwareId = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(layout.HwId)),
            HardwareRevision = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(layout.HwId + 4)),
        };
        {
            // The MD5 of the whole file with the key in place of the sum (mktplinkfw: md5salt_boot when the image carries
            // a bootloader, md5salt_normal otherwise).
            byte[] stored = h.AsSpan(layout.Md5, 16).ToArray();
            image.Md5Matches = Md5Classic(source, source.Length, layout.Md5, bootLen > 0 ? SaltBoot : layout.Salt, ct).AsSpan().SequenceEqual(stored);
            if (image.Md5Matches == false)
                image.Warnings.Add("The MD5 sum in the TP-Link header does not match the way this program computes it: the image was changed or damaged, " +
                                   "or it was made with a key this program does not know.");
        }

        var parts = new List<FirmwarePart>();
        void Add(string name, long offset, long length, string detail)
        {
            using var data = new SubBlockSource(source, offset, length, ownsInner: false);
            (PartitionContent content, string contentName, var packed) = Identify(data);
            if (content is PartitionContent.Unknown && contentName.Length == 0 && length >= 4 && data.ReadBytes(0, 4) is [0x5D, 0, 0, _] or [0x6D, 0, 0, _])
                contentName = "LZMA data";
            if (content is PartitionContent.Unknown && contentName.Length == 0 && UImage.IsUImage(data)) contentName = "uImage";
            parts.Add(new FirmwarePart
            {
                Index = parts.Count, Name = name, Offset = offset, Length = length,
                Content = content, ContentName = contentName, Packed = packed, Detail = detail,
            });
        }
        if (bootLen > 0) Add("bootloader", bootOfs, bootLen, "the bootloader the header lists");
        Add("kernel", kernelOfs, kernelLen, "the kernel the header lists");
        if (layout.RootfsAt != 0 && layout.RootfsAt == rootfsOfs)
            // The root filesystem runs to the end of the file (OpenWrt's 0xdeadc0de padding may follow it).
            Add("rootfs", rootfsOfs, Math.Max(rootfsLen, limit - rootfsOfs), "the root filesystem the header lists");
        else if (layout.RootfsAt != 0)
            Add("rootfs", layout.RootfsAt, limit - layout.RootfsAt, $"behind the kernel (made with -a; the header says 0x{rootfsOfs:X})");
        else if (layout.RootfsAt == 0 && image.FindTrailing(kernelOfs, ct) is { } inside)
            // A combined image: the root filesystem behind the kernel inside the kernel blob.
            Add("rootfs", inside, limit - inside, "inside the combined kernel image (no rootfs_ofs)");
        image.Parts = parts.OrderBy(p => p.Offset).Select((p, i) => new FirmwarePart
        {
            Index = i, Name = p.Name, Offset = p.Offset, Length = p.Length,
            Content = p.Content, ContentName = p.ContentName, Packed = p.Packed, Detail = p.Detail,
        }).ToList();
        image.ReadFwtool(limit, ct);
        return image;
    }

    private static byte[] Md5Classic(IBlockSource source, long length, int sumAt, byte[] salt, CancellationToken ct)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        byte[] head = source.ReadBytes(0, ClassicHeaderSize);
        salt.CopyTo(head, sumAt);
        md5.AppendData(head);
        Append(md5, source, ClassicHeaderSize, length, ct);
        return md5.GetHashAndReset();
    }

    private static void Append(IncrementalHash hash, IBlockSource source, long from, long to, CancellationToken ct)
    {
        byte[] buf = new byte[1 << 20];
        for (long o = from; o < to;)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(buf.Length, to - o);
            source.ReadExactly(o, buf.AsSpan(0, n));
            hash.AppendData(buf, 0, n);
            o += n;
        }
    }

    // ------------------------------------------------------------- safeloader

    /// <summary>A line of the safeloader partition table.</summary>
    private sealed record Entry(string Name, long Base, long Size);

    /// <summary>
    /// The image partition table of a safeloader image: where the payload starts (the table is its first 0x800 bytes,
    /// the bases count from its start), whether it is the "?NEW" kind, and the entries.
    /// </summary>
    private sealed record Table(long Payload, bool QNew, List<Entry> Entries);

    private const int SafeloaderPreamble = 0x14, SafeloaderHeader = 0x1000, SafeloaderTableSize = 0x800, QNewHeader = 0x3C;

    [GeneratedRegex(@"fwup-ptn\s+(\S+)\s+base\s+0x([0-9A-Fa-f]+)\s+size\s+0x([0-9A-Fa-f]+)")]
    private static partial Regex PartitionLine();

    /// <summary>
    /// The partition table as tplink-safeloader writes it: 0x14 bytes of preamble (size and MD5), 0x1000 bytes of
    /// header (vendor information), then the payload at 0x1014, which starts with the table: lines
    /// "fwup-ptn NAME base 0x... size 0x...\t\r\n" ended by a zero byte, in 0x800 bytes. The "?NEW" kind has 0x3C more
    /// bytes of header (payload at 0x1050).
    /// </summary>
    private static Table? SafeloaderTable(IBlockSource source)
    {
        if (source.Length < SafeloaderPreamble + QNewHeader + SafeloaderHeader + SafeloaderTableSize) return null;
        bool qnew = source.ReadBytes(SafeloaderPreamble, 4).AsSpan().SequenceEqual("?NEW"u8);
        long payload = SafeloaderPreamble + (qnew ? QNewHeader : 0) + SafeloaderHeader;
        byte[] table = source.ReadBytes(payload, SafeloaderTableSize);
        if (!table.AsSpan().StartsWith("fwup-ptn"u8)) return null;
        int end = Array.IndexOf(table, (byte)0);
        string text = Encoding.ASCII.GetString(table, 0, end < 0 ? table.Length : end);
        var entries = new List<Entry>();
        foreach (Match m in PartitionLine().Matches(text))
        {
            if (!long.TryParse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long b)
                || !long.TryParse(m.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long size)) return null;
            if (payload + b + size > source.Length) return null;
            entries.Add(new Entry(m.Groups[1].Value, b, size));
        }
        return entries.Count == 0 ? null : new Table(payload, qnew, entries);
    }

    private static TpLinkImage ReadSafeloader(IBlockSource source, Table table, CancellationToken ct)
    {
        var image = new TpLinkImage(source) { Safeloader = true, _table = table };
        if (!table.QNew)
        {
            // The preamble: the image size (big-endian, with the preamble), then the MD5 of the key and everything from 0x14 on.
            long length = BinaryPrimitives.ReadUInt32BigEndian(source.ReadBytes(0, 4));
            if (length > SafeloaderPreamble && length <= source.Length)
            {
                using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                md5.AppendData(SaltSafeloader);
                Append(md5, source, SafeloaderPreamble, length, ct);
                image.Md5Matches = md5.GetHashAndReset().AsSpan().SequenceEqual(source.ReadBytes(4, 16));
                if (image.Md5Matches == false)
                    image.Warnings.Add("The MD5 sum of the TP-Link safeloader image does not match the way this program computes it: the image was changed or damaged, " +
                                       "or it was made with a key this program does not know.");
            }
            // The vendor header: a length (at most 0x1000) and that much text, e.g. "vendor_name:TP-Link\nvendor_id:...".
            uint vendorLength = BinaryPrimitives.ReadUInt32BigEndian(source.ReadBytes(SafeloaderPreamble, 4));
            if (vendorLength is > 0 and <= SafeloaderHeader - 4)
                image.Vendor = Text(source.ReadBytes(SafeloaderPreamble + 4, (int)vendorLength), lines: true).Replace('\n', ' ');
            else if (source.ReadBytes(SafeloaderPreamble, 13).AsSpan().SequenceEqual("fw-type:Cloud"u8))
                image.Vendor = "fw-type:Cloud";
        }

        var parts = new List<FirmwarePart>();
        foreach (Entry e in table.Entries.OrderBy(e => e.Base))
        {
            long offset = table.Payload + e.Base;
            using var data = new SubBlockSource(source, offset, e.Size, ownsInner: false);
            (PartitionContent content, string contentName, var packed) = Identify(data);
            if (content is PartitionContent.Unknown && contentName.Length == 0 && UImage.IsUImage(data)) contentName = "uImage";
            if (content is PartitionContent.Unknown && contentName.Length == 0 && FitImage.IsFit(data)) contentName = "FIT image";
            if (e.Name == "partition-table") contentName = "flash partition table";
            // soft-version, support-list, extra-para: a meta header (length, zero), then text or the soft_version structure.
            if (e.Size > 8 && e.Name is "support-list" or "soft-version")
            {
                byte[] meta = source.ReadBytes(offset, (int)Math.Min(e.Size, 8192));
                int length = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(meta), (uint)(meta.Length - 8));
                ReadOnlySpan<byte> body = meta.AsSpan(8, length);
                if (e.Name == "support-list") image.SupportList = Text(body, lines: true);
                else image.FirmwareVersion = SoftVersion(body);
                contentName = e.Name == "support-list" ? "support list" : "software version";
            }
            parts.Add(new FirmwarePart
            {
                Index = parts.Count, Name = e.Name, Offset = offset, Length = e.Size,
                Content = content, ContentName = contentName, Packed = packed,
                Detail = $"partition table: base 0x{e.Base:X}, size 0x{e.Size:X}",
            });
        }
        image.Parts = parts;
        image.ReadFwtool(table.Payload + table.Entries.Max(e => e.Base + e.Size), ct);
        return image;
    }

    /// <summary>
    /// The soft-version partition: text, or the soft_version structure (0xFF, major, minor, patch, the date in BCD,
    /// the revision and the compatibility level, big-endian).
    /// </summary>
    private static string SoftVersion(ReadOnlySpan<byte> body)
    {
        if (body.Length >= 12 && body[0] == 0xFF)
        {
            static int Bcd(byte b) => (b >> 4) * 10 + (b & 15);
            string text = $"{body[1]}.{body[2]}.{body[3]} Build {Bcd(body[4]):00}{Bcd(body[5]):00}{Bcd(body[6]):00}{Bcd(body[7]):00} Rel. {BinaryPrimitives.ReadUInt32BigEndian(body[8..])}";
            if (body.Length >= 16) text += $", compatibility level {BinaryPrimitives.ReadUInt32BigEndian(body[12..])}";
            return text;
        }
        return Text(body, lines: true).Split('\n')[0];
    }

    private static string Text(ReadOnlySpan<byte> field, bool lines = false)
    {
        int n = field.IndexOf((byte)0);
        if (n >= 0) field = field[..n];
        var sb = new StringBuilder(field.Length);
        foreach (byte b in field) sb.Append(b is >= 0x20 and < 0x7F || lines && b == '\n' ? (char)b : lines && b == '\r' ? '\0' : '?');
        return sb.ToString().Replace("\0", "").Trim();
    }

    // ---------------------------------------------------------- writing

    private uint Field(int i) => BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(_classic!.Lengths + 4 * i));

    public override bool CanRebuild(FirmwarePart part, out string reason)
    {
        reason = !Parts.Contains(part) ? "The part does not belong to this image."
            : part.Content == PartitionContent.Unknown ? $"'{part.Name}' holds nothing this program writes."
            : Md5Matches != true ? "The MD5 sum of the TP-Link image could not be checked the way this program computes it, so it cannot be made again."
            : Safeloader ? SafeloaderObstacle(part) : ClassicObstacle(part);
        return reason.Length == 0;
    }

    public override void Write(Stream output, FirmwarePart part, IBlockSource data, List<string> warnings,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        if (!CanRebuild(part, out string reason)) throw new FsException(reason);
        List<IBlockSource> pieces = Safeloader ? SafeloaderPieces(part, data, ct) : ClassicPieces(part, data, ct);
        WritePieces(output, pieces, warnings, progress, ct);
    }

    public override IEnumerable<(string Name, IBlockSource Data)> FixedSections(FirmwarePart open)
    {
        if (_classic is not null)
        {
            // The header without what depends on the content: kernel_len, rootfs_len and the sum.
            byte[] h = (byte[])_header.Clone();
            h.AsSpan(_classic.Md5, 16).Clear();
            h.AsSpan(_classic.Lengths + 16, 4).Clear();
            h.AsSpan(_classic.Lengths + 24, 4).Clear();
            yield return ("TP-Link header", new MemoryBlockSource(h));
        }
        if (_table is not null)
        {
            yield return ("safeloader header", Slice(SafeloaderPreamble, _table.Payload));
            yield return ("partition names", new MemoryBlockSource(Encoding.ASCII.GetBytes(string.Join(",", _table.Entries.Select(e => e.Name)))));
        }
        foreach (FirmwarePart p in Parts)
            if (p.Name != open.Name && !(open.Name == "rootfs" && p.Name == "kernel" && _classic?.RootfsAt == 0)) yield return (p.Name, Open(p));
        foreach ((string name, IBlockSource data) in FwtoolSections()) yield return (name, data);
    }

    // ---------------------------------------------------------------- classic

    /// <summary>The end of the content the header describes, and whether mktplinkfw's JFFS2 marker and its padding follow it.</summary>
    private (long End, bool Marker, bool Padded) ClassicTail()
    {
        long fwLength = Field(2), end = _classic!.RootfsAt == 0 ? ClassicHeaderSize + Field(4) : _classic.RootfsAt + Field(6);
        long marker = (end + 0xFFFF) & ~0xFFFFL;
        bool hasMarker = marker + 4 <= ContentEnd && BinaryPrimitives.ReadUInt32BigEndian(Source.ReadBytes(marker, 4)) == 0xDEADC0DE;
        return (end, hasMarker, ContentEnd == fwLength);
    }

    private string ClassicObstacle(FirmwarePart part)
    {
        if (part.Name != "rootfs") return $"Only the root filesystem of a TP-Link image is rebuilt, not '{part.Name}'.";
        if (Field(8) > 0) return "The TP-Link image carries a bootloader, which is not rebuilt.";
        // Behind the content only what mktplinkfw puts there: 0xFF padding and its JFFS2 marker.
        (long end, bool marker, _) = ClassicTail();
        long from = marker ? ((end + 0xFFFF) & ~0xFFFFL) + 4 : end;
        byte[] buf = new byte[1 << 16];
        for (long o = end; o < ContentEnd;)
        {
            int n = Source.Read(o, buf.AsSpan(0, (int)Math.Min(buf.Length, ContentEnd - o)));
            if (n <= 0) break;
            for (int i = 0; i < n; i++)
            {
                long at = o + i;
                if (marker && at >= from - 4 && at < from) continue;
                if (buf[i] != 0xFF) return "There is data behind the root filesystem of the TP-Link image that is not mktplinkfw's padding.";
            }
            o += n;
        }
        return "";
    }

    /// <summary>
    /// The image as mktplinkfw builds it with the new root filesystem: the header (new rootfs_len, or kernel_len for a
    /// combined image, and the MD5 with the key), the kernel, the root filesystem at its offset, the JFFS2 marker at the
    /// next 64 KiB (-j) and the 0xFF padding to the flash layout when the old image had it.
    /// </summary>
    private List<IBlockSource> ClassicPieces(FirmwarePart part, IBlockSource data, CancellationToken ct)
    {
        long fwLength = Field(2);
        bool combined = _classic!.RootfsAt == 0;
        (long _, bool marker, bool padded) = ClassicTail();
        var body = new List<IBlockSource> { Slice(ClassicHeaderSize, part.Offset), data };
        long end = part.Offset + data.Length;
        if (marker)
        {
            long at = (end + 0xFFFF) & ~0xFFFFL;
            body.Add(Fill(at - end, 0xFF));
            body.Add(new MemoryBlockSource(new byte[] { 0xDE, 0xAD, 0xC0, 0xDE }));
            end = at + 4;
        }
        if (end > fwLength)
            throw new FsException($"The new root filesystem does not fit into the flash layout of the TP-Link image: the firmware would be {end} bytes, the layout holds {fwLength}.");
        if (padded) body.Add(Fill(fwLength - end, 0xFF));

        byte[] header = (byte[])_header.Clone();
        if (combined) BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(_classic!.Lengths + 16), (uint)(part.Offset + data.Length - ClassicHeaderSize));
        else BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(_classic!.Lengths + 24), (uint)data.Length);
        _classic.Salt.CopyTo(header, _classic.Md5);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(header);
        byte[] buf = new byte[1 << 20];
        foreach (IBlockSource b in body)
            for (long o = 0; o < b.Length;)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buf.Length, b.Length - o);
                b.ReadExactly(o, buf.AsSpan(0, n));
                md5.AppendData(buf, 0, n);
                o += n;
            }
        md5.GetHashAndReset().CopyTo(header, _classic.Md5);
        var pieces = new List<IBlockSource> { new MemoryBlockSource(header) };
        pieces.AddRange(body);
        return pieces;
    }

    // ------------------------------------------------------------- safeloader

    /// <summary>The flash partition of a name in the partition-table partition ("partition NAME base 0x... size 0x..."), or null.</summary>
    private (long Base, long Size)? FlashPartition(string name)
    {
        FirmwarePart? table = Parts.FirstOrDefault(p => p.Name == "partition-table");
        if (table is null) return null;
        string text = Encoding.ASCII.GetString(Source.ReadBytes(table.Offset, (int)Math.Min(table.Length, 0x10000)));
        Match m = Regex.Match(text, $@"partition\s+{Regex.Escape(name)}\s+base\s+0x([0-9A-Fa-f]+)\s+size\s+0x([0-9A-Fa-f]+)");
        return m.Success ? (long.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture), long.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)) : null;
    }

    private string SafeloaderObstacle(FirmwarePart part)
    {
        if (_table is not { QNew: false } table) return "The \"?NEW\" kind of safeloader image is not rebuilt.";
        // tplink-safeloader puts the partitions one after another from base 0x800 on.
        long next = SafeloaderTableSize;
        foreach (Entry e in table.Entries)
        {
            if (e.Base != next) return "The partitions of the safeloader image do not follow one another as tplink-safeloader puts them.";
            next += e.Size;
        }
        if (table.Payload + next != ContentEnd) return "There is data behind the partitions of the safeloader image.";
        return "";
    }

    /// <summary>
    /// The image as tplink-safeloader builds it with the new partition: the preamble (size, MD5 of the key and the image
    /// from 0x14), the header as it was, the partition table written anew (bases one after another from 0x800, a zero
    /// byte, 0xFF), the partitions. A file-system that ended with the JFFS2 marker (-j) gets it again at the next 64 KiB
    /// of its flash address.
    /// </summary>
    private List<IBlockSource> SafeloaderPieces(FirmwarePart part, IBlockSource data, CancellationToken ct)
    {
        Table table = _table!;
        var content = new List<IBlockSource> { data };
        long size = data.Length;
        bool marker = part.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(Source.ReadBytes(part.Offset + part.Length - 4, 4)) == 0xDEADC0DE;
        (long Base, long Size)? flash = FlashPartition(part.Name);
        if (marker && flash is { } f && (f.Base + part.Length - 4) % 0x10000 == 0)
        {
            long padded = ((data.Length + f.Base + 0xFFFF) & ~0xFFFFL) - f.Base;
            content.Add(Fill(padded - data.Length, 0xFF));
            content.Add(new MemoryBlockSource(new byte[] { 0xDE, 0xAD, 0xC0, 0xDE }));
            size = padded + 4;
        }
        if (flash is { } limit && size > limit.Size)
            throw new FsException($"The new {part.Name} ({size} bytes) is too big for its flash partition ({limit.Size} bytes).");

        var lines = new StringBuilder();
        var partitions = new List<IBlockSource>();
        long position = SafeloaderTableSize;
        foreach (Entry e in table.Entries)
        {
            bool changed = table.Payload + e.Base == part.Offset;
            long length = changed ? size : e.Size;
            lines.Append(CultureInfo.InvariantCulture, $"fwup-ptn {e.Name} base 0x{position:x5} size 0x{length:x5}\t\r\n");
            if (changed) partitions.AddRange(content);
            else partitions.Add(Slice(table.Payload + e.Base, table.Payload + e.Base + e.Size));
            position += length;
        }
        byte[] tableBytes = new byte[SafeloaderTableSize];
        tableBytes.AsSpan().Fill(0xFF);
        byte[] text = Encoding.ASCII.GetBytes(lines.ToString());
        if (text.Length + 1 > SafeloaderTableSize) throw new FsException("The partition table of the safeloader image would overflow.");
        text.CopyTo(tableBytes, 0);
        tableBytes[text.Length] = 0;

        long total = table.Payload + position;
        if (total > uint.MaxValue) throw new FsException("The new image is too large for a safeloader image.");
        var body = new List<IBlockSource> { Slice(SafeloaderPreamble, table.Payload), new MemoryBlockSource(tableBytes) };
        body.AddRange(partitions);
        byte[] preamble = new byte[SafeloaderPreamble];
        BinaryPrimitives.WriteUInt32BigEndian(preamble, (uint)total);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(SaltSafeloader);
        byte[] buf = new byte[1 << 20];
        foreach (IBlockSource b in body)
            for (long o = 0; o < b.Length;)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buf.Length, b.Length - o);
                b.ReadExactly(o, buf.AsSpan(0, n));
                md5.AppendData(buf, 0, n);
                o += n;
            }
        md5.GetHashAndReset().CopyTo(preamble, 4);
        var pieces = new List<IBlockSource> { new MemoryBlockSource(preamble) };
        pieces.AddRange(body);
        return pieces;
    }
}
