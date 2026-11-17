using System.Buffers.Binary;
using System.Text;
using FsForge.Core.Compression;
using FsForge.Core.Images;

namespace FsForge.Core.Firmware;

/// <summary>
/// The Broadcom TRX image (OpenWrt trx.h) of routers with Broadcom chips (Linksys, ASUS, Netgear, Buffalo...):
/// a little-endian header "HDR0" with the length of the image, a CRC-32 from the flags to the end, the version
/// (1 or 2) and three (four in version 2) offsets of the parts: LZMA loader, kernel, root filesystem. Netgear wraps
/// it in a CHK header ("*#$^", big-endian) with the board name and checksums of its own.
/// </summary>
public sealed class TrxImage : FirmwareImage
{
    public const uint Magic = 0x30524448;          // "HDR0", little-endian
    public const uint ChkMagic = 0x2A23245E;       // "*#$^", big-endian

    private TrxImage(IBlockSource source) : base(source) { }

    /// <summary>Where the TRX header starts: 0, or behind a Netgear CHK header.</summary>
    public long TrxOffset { get; private init; }

    /// <summary>Length of the TRX image (header included) as its header gives it.</summary>
    public uint Length { get; private init; }

    public ushort Version { get; private init; }
    public ushort Flags { get; private init; }
    public uint Crc { get; private init; }

    /// <summary>
    /// How the CRC-32 matches: "fwtool" style (started at ~0, not inverted, as OpenWrt's trx writes it), "standard"
    /// (inverted, as zlib), or empty when neither matches.
    /// </summary>
    public string CrcScheme { get; private set; } = "";

    public bool CrcMatches => CrcScheme.Length > 0;

    /// <summary>The board name of a Netgear CHK header, or null without one.</summary>
    public string? ChkBoard { get; private init; }

    public override string Kind => ChkBoard is null ? "Broadcom TRX image" : "Netgear CHK image with a Broadcom TRX";
    public override string ShortKind => ChkBoard is null ? "TRX" : "CHK/TRX";
    public override string Description => ChkBoard is { Length: > 0 } b ? $"{Kind} for '{b}'" : $"{Kind}, version {Version}";
    public override bool ChecksumsValid => CrcMatches;

    /// <summary>The file is a TRX image, or a Netgear CHK image holding one.</summary>
    public static bool IsTrx(IBlockSource source) => TrxAt(source) is not null;

    /// <summary>Where a valid TRX header starts: at 0, or behind a CHK header; null when there is none.</summary>
    private static long? TrxAt(IBlockSource source)
    {
        if (source.Length < 32) return null;
        byte[] head = source.ReadBytes(0, 8);
        if (BinaryPrimitives.ReadUInt32BigEndian(head) == ChkMagic)
        {
            uint headerLength = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4));
            return headerLength is >= 40 and <= 4096 && HeaderLooksValid(source, headerLength) ? headerLength : null;
        }
        return HeaderLooksValid(source, 0) ? 0 : null;
    }

    private static int HeaderSize(ushort version) => version == 2 ? 32 : 28;

    private static bool HeaderLooksValid(IBlockSource source, long at)
    {
        if (source.Length - at < 32) return false;
        byte[] h = source.ReadBytes(at, 32);
        if (BinaryPrimitives.ReadUInt32LittleEndian(h) != Magic) return false;
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4));
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(14));
        if (version is not (1 or 2)) return false;
        int size = HeaderSize(version);
        if (length < size || length > source.Length - at) return false;
        bool any = false;
        for (int i = 0; i < (size - 16) / 4; i++)
        {
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(16 + 4 * i));
            if (offset == 0) continue;
            if (offset < size || offset >= length) return false;
            any = true;
        }
        return any;
    }

    public static TrxImage Read(IBlockSource source, CancellationToken ct = default)
    {
        long at = TrxAt(source) ?? throw new FsException("The file is not a TRX image.");
        string? board = null;
        if (at > 0)
        {
            // CHK: magic, header length, 8 reserved bytes, checksums and lengths (6 × 4 bytes), then the board name.
            byte[] chk = source.ReadBytes(0, (int)at);
            board = CString(chk.AsSpan(40));
        }
        byte[] h = source.ReadBytes(at, 32);
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(14));
        var trx = new TrxImage(source)
        {
            TrxOffset = at,
            Length = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4)),
            Crc = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(8)),
            Flags = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(12)),
            Version = version,
            ChkBoard = board,
        };
        trx.ReadParts(h, ct);
        return trx;
    }

    private void ReadParts(byte[] h, CancellationToken ct)
    {
        // The CRC covers the image from the flags to its end.
        uint raw = ~0u;
        byte[] buf = new byte[1 << 20];
        for (long o = TrxOffset + 12, end = TrxOffset + Length; o < end;)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(buf.Length, end - o);
            Source.ReadExactly(o, buf.AsSpan(0, n));
            raw = ~Crc32.Update(~raw, buf.AsSpan(0, n));
            o += n;
        }
        CrcScheme = raw == Crc ? "fwtool" : ~raw == Crc ? "standard" : "";
        if (!CrcMatches)
            Warnings.Add("The CRC-32 of the TRX image does not match its header: the image was changed or damaged, and the bootloader would refuse it.");

        int slots = (HeaderSize(Version) - 16) / 4;
        var offsets = Enumerable.Range(0, slots)
            .Select(i => (Slot: i, Offset: (long)BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(16 + 4 * i))))
            .Where(o => o.Offset != 0).OrderBy(o => o.Offset).ToList();
        var parts = new List<FirmwarePart>();
        for (int i = 0; i < offsets.Count; i++)
        {
            long start = TrxOffset + offsets[i].Offset;
            long end = TrxOffset + (i + 1 < offsets.Count ? offsets[i + 1].Offset : Length);
            using var data = new SubBlockSource(Source, start, end - start, ownsInner: false);
            (PartitionContent content, string contentName, var packed) = Identify(data);
            bool readable = new FirmwarePart { Content = content }.IsReadable;
            if (!readable && contentName.Length == 0)
            {
                byte[] head = data.Length >= 4 ? data.ReadBytes(0, 4) : Array.Empty<byte>();
                if (head is [0x5D, 0, 0, _] or [0x6D, 0, 0, _]) contentName = "LZMA data";
            }
            // Version 1 lists loader, kernel and root filesystem; with two offsets they are kernel and root filesystem.
            string name = readable
                ? (parts.Any(p => p.Name == "rootfs") ? $"rootfs{i}" : "rootfs")
                : offsets.Count >= 3 && i == 0 ? "loader"
                : (offsets.Count >= 3 && i == 1) || (offsets.Count < 3 && i == 0) ? "kernel"
                : $"part{offsets[i].Slot}";
            parts.Add(new FirmwarePart
            {
                Index = parts.Count, Name = name, Offset = start, Length = end - start,
                Content = content, ContentName = contentName, Packed = packed,
                Detail = $"offset {offsets[i].Slot} of the TRX header",
            });
        }
        Parts = parts;
    }

    private static string CString(ReadOnlySpan<byte> field)
    {
        int n = field.IndexOf((byte)0);
        if (n >= 0) field = field[..n];
        var sb = new StringBuilder(field.Length);
        foreach (byte b in field) sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '?');
        return sb.ToString().Trim();
    }

    // ---------------------------------------------------------- writing

    public override bool CanRebuild(FirmwarePart part, out string reason)
    {
        reason = "Rebuilding a TRX image is not supported yet.";
        return false;
    }

    public override void Write(Stream output, FirmwarePart part, IBlockSource data, List<string> warnings,
        IProgress<ProgressInfo>? progress, CancellationToken ct) =>
        throw new FsException("Rebuilding a TRX image is not supported yet.");

    public override IEnumerable<(string Name, IBlockSource Data)> FixedSections(FirmwarePart open) => Array.Empty<(string, IBlockSource)>();
}
