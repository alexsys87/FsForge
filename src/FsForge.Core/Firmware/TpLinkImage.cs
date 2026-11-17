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

    /// <summary>The keys mktplinkfw puts in place of the sum when it computes the MD5 (normal images, images with a bootloader).</summary>
    private static readonly byte[] SaltNormal = { 0xDC, 0xD7, 0x3A, 0xA5, 0xC3, 0x95, 0x98, 0xFB, 0xDD, 0xF9, 0xE7, 0xF4, 0x0E, 0xAE, 0x47, 0x38 };
    private static readonly byte[] SaltBoot = { 0x8C, 0xEF, 0x33, 0x5B, 0xD5, 0xC5, 0xCE, 0xFA, 0xA7, 0x9C, 0x28, 0xDA, 0xB2, 0xE9, 0x0F, 0x42 };

    /// <summary>The key tplink-safeloader puts in front of the image when it computes the MD5.</summary>
    private static readonly byte[] SaltSafeloader = { 0x7A, 0x2B, 0x15, 0xED, 0x9B, 0x98, 0x59, 0x6D, 0xE5, 0x04, 0xAB, 0x44, 0xAC, 0x2A, 0x9F, 0x4E };

    private TpLinkImage(IBlockSource source) : base(source) { }

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
    private sealed record Classic(uint Version, int HwId, int Md5, int Lengths, string Vendor, string FwVersion);

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
        // A combined image (rootfs_ofs 0) carries the root filesystem inside the kernel blob.
        if (rootfsOfs != 0 && (rootfsOfs < kernelOfs + kernelLen || rootfsOfs >= limit || rootfsOfs + rootfsLen > limit)) return null;
        if (bootLen > 0 && bootOfs + bootLen > limit) return null;
        // Many byte strings start with 01 00 00 00: the vendor or a filesystem where the header says must confirm it.
        bool vendor = layout.Vendor.StartsWith("TP-LINK", StringComparison.OrdinalIgnoreCase) || layout.Vendor.StartsWith("OpenWrt", StringComparison.Ordinal);
        if (!vendor)
        {
            if (rootfsOfs == 0) return null;
            (PartitionContent content, _, _) = Identify(new SubBlockSource(source, rootfsOfs, limit - rootfsOfs, ownsInner: false));
            if (!new FirmwarePart { Content = content }.IsReadable) return null;
        }
        return layout;
    }

    private static TpLinkImage ReadClassic(IBlockSource source, Classic layout, CancellationToken ct)
    {
        byte[] h = source.ReadBytes(0, ClassicHeaderSize);
        uint U(int i) => BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(layout.Lengths + 4 * i));
        long fwLength = U(2), kernelOfs = U(3), kernelLen = U(4), rootfsOfs = U(5), rootfsLen = U(6), bootOfs = U(7), bootLen = U(8);
        long limit = source.Length;
        var image = new TpLinkImage(source)
        {
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
            image.Md5Matches = Md5Classic(source, source.Length, layout.Md5, bootLen > 0 ? SaltBoot : SaltNormal, ct).AsSpan().SequenceEqual(stored);
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
        if (rootfsOfs != 0)
            // The root filesystem runs to the end of the file (OpenWrt's 0xdeadc0de padding may follow it).
            Add("rootfs", rootfsOfs, Math.Max(rootfsLen, limit - rootfsOfs), "the root filesystem the header lists");
        else if (image.FindTrailing(kernelOfs, ct) is { } inside)
            // A combined image: the root filesystem behind the kernel inside the kernel blob.
            Add("rootfs", inside, limit - inside, "inside the combined kernel image (rootfs_ofs 0)");
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
        var image = new TpLinkImage(source) { Safeloader = true };
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

    public override bool CanRebuild(FirmwarePart part, out string reason)
    {
        reason = "Rebuilding a TP-Link firmware image is not supported yet.";
        return false;
    }

    public override void Write(Stream output, FirmwarePart part, IBlockSource data, List<string> warnings,
        IProgress<ProgressInfo>? progress, CancellationToken ct) =>
        throw new FsException("Rebuilding a TP-Link firmware image is not supported yet.");

    public override IEnumerable<(string Name, IBlockSource Data)> FixedSections(FirmwarePart open) => Array.Empty<(string, IBlockSource)>();
}
