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

    /// <summary>
    /// The four checksums of the CHK header (kernel, root filesystem, image, header) are what Netgear's checksum gives:
    /// the CHK can be written again. False when one does not match, null without a CHK header.
    /// </summary>
    public bool? ChkChecksumsValid { get; private set; }

    /// <summary>The CHK header as it is in the file (empty without one).</summary>
    private byte[] _chk = Array.Empty<byte>();

    /// <summary>The TRX header as it is in the file.</summary>
    private byte[] _header = Array.Empty<byte>();

    /// <summary>The parts with the header slot each comes from, in the order of the image.</summary>
    private readonly List<(FirmwarePart Part, int Slot)> _slots = new();

    public override string Kind => ChkBoard is null ? "Broadcom TRX image" : "Netgear CHK image with a Broadcom TRX";
    public override string ShortKind => ChkBoard is null ? "TRX" : "CHK/TRX";
    public override string Description => ChkBoard is { Length: > 0 } b ? $"{Kind} for '{b}'" : $"{Kind}, version {Version}";
    public override bool ChecksumsValid => CrcMatches && ChkChecksumsValid != false;

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
        trx._header = source.ReadBytes(at, HeaderSize(version));
        if (at > 0) trx._chk = source.ReadBytes(0, (int)at);
        trx.ReadParts(h, ct);
        if (at > 0) trx.ChkChecksumsValid = trx.CheckChk(ct);
        trx.ReadFwtool(at + trx.Length, ct);
        return trx;
    }

    /// <summary>The root filesystem behind the TRX image (trx -F: outside its length and CRC), or null.</summary>
    private FirmwarePart? _trailing;

    /// <summary>
    /// fwtool-form CRC (started at ~0, not inverted) of a TRX image from its flags on, given as pieces from offset 12.
    /// In version 2, the 8 bytes from 22 into the bin-header (offset slot 3) count as 0xFF, as CFE and trx compute it.
    /// </summary>
    private static uint TrxCrc(IEnumerable<IBlockSource> pieces, long binHeader, CancellationToken ct)
    {
        long maskFrom = binHeader > 0 ? binHeader + 22 : -1, maskTo = maskFrom + 8;
        uint raw = ~0u;
        long pos = 12;
        byte[] buf = new byte[1 << 20];
        foreach (IBlockSource piece in pieces)
            for (long o = 0; o < piece.Length;)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buf.Length, piece.Length - o);
                piece.ReadExactly(o, buf.AsSpan(0, n));
                long from = Math.Max(maskFrom, pos), to = Math.Min(maskTo, pos + n);
                if (maskFrom >= 0 && from < to) buf.AsSpan((int)(from - pos), (int)(to - from)).Fill(0xFF);
                raw = ~Crc32.Update(~raw, buf.AsSpan(0, n));
                o += n;
                pos += n;
            }
        return raw;
    }

    private long BinHeaderOffset(byte[] header) =>
        Version == 2 ? BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28)) : 0;

    private void ReadParts(byte[] h, CancellationToken ct)
    {
        // The CRC covers the image from the flags to its end.
        uint raw = TrxCrc(new[] { Slice(TrxOffset + 12, TrxOffset + Length) }, BinHeaderOffset(h), ct);
        CrcScheme = raw == Crc ? "fwtool" : ~raw == Crc ? "standard" : "";
        if (!CrcMatches)
            Warnings.Add("The CRC-32 of the TRX image does not match its header: the image was changed or damaged, and the bootloader would refuse it.");

        int slots = (HeaderSize(Version) - 16) / 4;
        var offsets = Enumerable.Range(0, slots)
            .Select(i => (Slot: i, Offset: (long)BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(16 + 4 * i))))
            .Where(o => o.Offset != 0).OrderBy(o => o.Offset).ToList();
        var found = new List<(FirmwarePart Part, int Slot, bool Readable)>();
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
            found.Add((new FirmwarePart
            {
                Offset = start, Length = end - start, Content = content, ContentName = contentName, Packed = packed,
                Detail = $"offset {offsets[i].Slot} of the TRX header",
            }, offsets[i].Slot, readable));
        }

        // Names: filesystems are root filesystems; of the rest, the largest is the kernel and one in front of it the
        // LZMA loader of bcm47xx; slot 3 of version 2 is the bin-header CFE reads.
        int kernel = found.Select((f, i) => (f, i)).Where(x => !x.f.Readable && !(Version == 2 && x.f.Slot == 3))
            .OrderByDescending(x => x.f.Part.Length).Select(x => x.i).DefaultIfEmpty(-1).First();
        var parts = new List<FirmwarePart>();
        for (int i = 0; i < found.Count; i++)
        {
            (FirmwarePart f, int slot, bool readable) = found[i];
            string name = readable ? (parts.Any(p => p.Name == "rootfs") ? $"rootfs{i}" : "rootfs")
                : Version == 2 && slot == 3 ? "bin-header"
                : i == kernel ? "kernel"
                : i < kernel ? "loader"
                : $"part{slot}";
            var part = new FirmwarePart
            {
                Index = parts.Count, Name = name, Offset = f.Offset, Length = f.Length,
                Content = f.Content, ContentName = f.ContentName, Packed = f.Packed, Detail = f.Detail,
            };
            parts.Add(part);
            _slots.Add((part, slot));
        }

        // trx -F appends the root filesystem behind the length the header gives, outside the CRC.
        long trxEnd = TrxOffset + Length;
        if (trxEnd < Source.Length && FindTrailing(trxEnd, ct) is { } rootfs)
        {
            (PartitionContent content, string contentName, var packed) = Identify(Slice(rootfs, Source.Length));
            if (new FirmwarePart { Content = content }.IsReadable)
                parts.Add(_trailing = new FirmwarePart
                {
                    Index = parts.Count, Name = parts.Any(p => p.Name == "rootfs") ? "trailing" : "rootfs", Offset = rootfs, Length = Source.Length - rootfs,
                    Content = content, ContentName = contentName, Packed = packed, BehindImage = true,
                    Detail = "behind the TRX image, outside its length and CRC (trx -F)",
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

    // ------------------------------------------------------ Netgear CHK

    private const int ChkKernelSum = 16, ChkRootfsSum = 20, ChkKernelLength = 24, ChkRootfsLength = 28, ChkImageSum = 32, ChkHeaderSum = 36;

    /// <summary>Netgear's checksum (mkchkimg): two running byte sums, each folded to 16 bits.</summary>
    internal struct NetgearSum
    {
        private uint _c0, _c1;

        public void Add(ReadOnlySpan<byte> data)
        {
            foreach (byte b in data)
            {
                _c0 += b;
                _c1 += _c0;
            }
        }

        public void Add(IBlockSource source, long from, long to, CancellationToken ct)
        {
            byte[] buf = new byte[1 << 20];
            for (long o = from; o < to;)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buf.Length, to - o);
                source.ReadExactly(o, buf.AsSpan(0, n));
                Add(buf.AsSpan(0, n));
                o += n;
            }
        }

        public readonly uint Finish()
        {
            uint b = (_c0 & 0xFFFF) + ((_c0 >> 16) & 0xFFFF);
            uint c0 = ((b >> 16) + b) & 0xFFFF;
            b = (_c1 & 0xFFFF) + ((_c1 >> 16) & 0xFFFF);
            uint c1 = ((b >> 16) + b) & 0xFFFF;
            return (c1 << 16) | c0;
        }
    }

    private uint ChkField(int at) => BinaryPrimitives.ReadUInt32BigEndian(_chk.AsSpan(at));

    /// <summary>Checks the CHK checksums the way mkchkimg makes them: kernel (the TRX), root filesystem, both, and the header.</summary>
    private bool CheckChk(CancellationToken ct)
    {
        long kernelEnd = TrxOffset + ChkField(ChkKernelLength), rootfsEnd = kernelEnd + ChkField(ChkRootfsLength);
        if (rootfsEnd > Source.Length) return false;
        var kernel = new NetgearSum();
        kernel.Add(Source, TrxOffset, kernelEnd, ct);
        var rootfs = new NetgearSum();
        rootfs.Add(Source, kernelEnd, rootfsEnd, ct);
        var image = new NetgearSum();
        image.Add(Source, TrxOffset, rootfsEnd, ct);
        byte[] header = (byte[])_chk.Clone();
        header.AsSpan(ChkHeaderSum, 4).Clear();
        var head = new NetgearSum();
        head.Add(header);
        return kernel.Finish() == ChkField(ChkKernelSum) && rootfs.Finish() == ChkField(ChkRootfsSum)
               && image.Finish() == ChkField(ChkImageSum) && head.Finish() == ChkField(ChkHeaderSum);
    }

    // ---------------------------------------------------------- writing

    public override bool CanRebuild(FirmwarePart part, out string reason)
    {
        long kernelEnd = _chk.Length > 0 ? TrxOffset + ChkField(ChkKernelLength) : 0;
        reason = !Parts.Contains(part) ? "The part does not belong to this image."
            : part.Content == PartitionContent.Unknown ? $"'{part.Name}' holds nothing this program writes."
            : !CrcMatches ? "The CRC-32 of the TRX image matches neither form it is written in, so it is not known which one the bootloader expects."
            : ChkChecksumsValid == false ? "The checksums of the Netgear CHK header do not match the way this program computes them, so they cannot be made again."
            : _chk.Length > 0 && part.BehindImage && part.Offset >= kernelEnd ? "The root filesystem lies in a part of the Netgear CHK image of its own, which is not rebuilt."
            : "";
        return reason.Length == 0;
    }

    /// <summary>
    /// Writes the image again with <paramref name="data"/> in place of <paramref name="part"/>, as OpenWrt's trx lays it
    /// out: each part padded to 4 bytes, the image to 4 KiB (when it was), the parts behind the changed one moved on
    /// their kind of boundary, the header with the new length, offsets and CRC-32 (in the form the old one had; in
    /// version 2 with the bin-header flags counted as 0xFF). OpenWrt's 0xdeadc0de markers behind a root filesystem stay
    /// on the first erase block boundaries behind the new one. A root filesystem behind the image (trx -F) leaves the
    /// TRX image as it is. A Netgear CHK header gets the new length and checksums.
    /// </summary>
    public override void Write(Stream output, FirmwarePart part, IBlockSource data, List<string> warnings,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        if (!CanRebuild(part, out string reason)) throw new FsException(reason);
        long start = TrxOffset, trxEnd = TrxOffset + Length;
        long kernelEnd = _chk.Length > 0 ? start + ChkField(ChkKernelLength) : -1;
        long rootfsEnd = _chk.Length > 0 ? kernelEnd + ChkField(ChkRootfsLength) : -1;

        // The image from the TRX header on, up to "regionEnd" of the old file.
        var image = new List<IBlockSource>();
        long regionEnd;
        if (ReferenceEquals(part, _trailing))
        {
            regionEnd = _chk.Length > 0 ? Math.Max(kernelEnd, trxEnd) : ContentEnd;
            image.Add(Slice(start, part.Offset));
            image.Add(data);
            Tail tail = TailOf(part, regionEnd, start);
            if (tail.Start < regionEnd || tail.Markers.Count > 0)
                image.AddRange(TailPieces(part, part.Offset + data.Length, regionEnd, start));
            else
            {
                // Padding alone, as trx makes it: the file to 4 bytes, then to 4 KiB.
                long length = part.Offset - start + data.Length;
                long padded = (((length + 3) & ~3L) + 4095) & ~4095L;
                image.Add(Fill(padded - length, 0));
            }
        }
        else
        {
            image.AddRange(RebuildTrx(part, data, ct));
            regionEnd = trxEnd;
            if (_chk.Length > 0 && kernelEnd > trxEnd)
            {
                image.Add(Slice(trxEnd, kernelEnd));
                regionEnd = kernelEnd;
            }
        }

        var pieces = new List<IBlockSource>();
        long after = regionEnd;
        if (_chk.Length > 0)
        {
            // CHK: its kernel is the TRX image (with what the old one carried behind it); a root filesystem of its own stays.
            long kernelLength = image.Sum(t => t.Length);
            if (kernelLength > uint.MaxValue) throw new FsException("The new image is too large for a Netgear CHK image.");
            var kernel = new NetgearSum();
            var whole = new NetgearSum();
            byte[] buf = new byte[1 << 20];
            foreach (IBlockSource t in image)
                for (long o = 0; o < t.Length;)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = (int)Math.Min(buf.Length, t.Length - o);
                    t.ReadExactly(o, buf.AsSpan(0, n));
                    kernel.Add(buf.AsSpan(0, n));
                    whole.Add(buf.AsSpan(0, n));
                    o += n;
                }
            whole.Add(Source, kernelEnd, rootfsEnd, ct);
            byte[] chk = (byte[])_chk.Clone();
            BinaryPrimitives.WriteUInt32BigEndian(chk.AsSpan(ChkKernelLength), (uint)kernelLength);
            BinaryPrimitives.WriteUInt32BigEndian(chk.AsSpan(ChkKernelSum), kernel.Finish());
            BinaryPrimitives.WriteUInt32BigEndian(chk.AsSpan(ChkImageSum), whole.Finish());
            chk.AsSpan(ChkHeaderSum, 4).Clear();
            var head = new NetgearSum();
            head.Add(chk);
            BinaryPrimitives.WriteUInt32BigEndian(chk.AsSpan(ChkHeaderSum), head.Finish());
            pieces.Add(new MemoryBlockSource(chk));
            pieces.AddRange(image);
            pieces.Add(Slice(kernelEnd, rootfsEnd));
            after = Math.Max(regionEnd, rootfsEnd);
        }
        else
        {
            if (start > 0) pieces.Add(Slice(0, start));
            pieces.AddRange(image);
        }
        if (after < ContentEnd) pieces.Add(Slice(after, ContentEnd));
        WritePieces(output, pieces, warnings, progress, ct);
    }

    /// <summary>The TRX image (header first) with <paramref name="data"/> in place of a part inside it.</summary>
    private List<IBlockSource> RebuildTrx(FirmwarePart part, IBlockSource data, CancellationToken ct)
    {
        long start = TrxOffset, end = TrxOffset + Length;
        int index = _slots.FindIndex(s => ReferenceEquals(s.Part, part));
        var offsets = _slots.ToDictionary(s => s.Slot, s => s.Part.Offset - start);
        // trx pads each part to 4 bytes.
        var body = new List<IBlockSource> { Slice(start + _header.Length, part.Offset), data, Fill((4 - data.Length % 4) % 4, 0) };
        long newEnd = part.Offset + ((data.Length + 3) & ~3L);
        if (index == _slots.Count - 1)
        {
            // What followed the last part: OpenWrt's markers or other data on their boundaries; padding alone is made anew.
            Tail tail = TailOf(part, end, start);
            if (tail.Start < end || tail.Markers.Count > 0) body.AddRange(TailPieces(part, newEnd, end, start));
        }
        else
        {
            // The parts behind move by a step that keeps each of them on its kind of boundary. A part that followed the old
            // filesystem right behind it (trx -a 4; the image file padded to 4 KiB, as mksquashfs pads it) follows the new
            // one right behind it too.
            long next = _slots[index + 1].Part.Offset - start;
            long fsEnd = part.Offset + FilesystemLength(part.Offset, part.Content, end) - start;
            long align = next <= ((fsEnd + 4095) & ~4095L) ? 4 : Math.Max(4, _slots.Skip(index + 1).Max(s => Alignment(s.Part.Offset - start)));
            long moved = Shifted(next, newEnd - start, align);
            body.Add(Fill(moved - (newEnd - start), 0));
            body.Add(Slice(start + next, end));
            foreach ((FirmwarePart _, int slot) in _slots.Skip(index + 1)) offsets[slot] += moved - next;
        }
        long length = _header.Length + body.Sum(b => b.Length);
        long round = Length % 4096 == 0 ? 4096 : 4;
        if (length % round != 0)
        {
            body.Add(Fill(round - length % round, 0));
            length += round - length % round;
        }
        if (length > uint.MaxValue) throw new FsException("The new image is too large for a TRX image (4 GiB at most).");

        byte[] header = (byte[])_header.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)length);
        foreach ((int slot, long offset) in offsets) BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16 + 4 * slot), (uint)offset);
        var covered = new List<IBlockSource> { new MemoryBlockSource(header[12..]) };
        covered.AddRange(body);
        uint raw = TrxCrc(covered, BinHeaderOffset(header), ct);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), CrcScheme == "fwtool" ? raw : ~raw);
        var trx = new List<IBlockSource> { new MemoryBlockSource(header) };
        trx.AddRange(body);
        return trx;
    }

    /// <summary>
    /// What a rewrite keeps: the header fields that do not depend on the content, the CHK board, the other parts, what
    /// followed a root filesystem, and what follows the image.
    /// </summary>
    public override IEnumerable<(string Name, IBlockSource Data)> FixedSections(FirmwarePart open)
    {
        yield return ("TRX flags and version", new MemoryBlockSource(_header[12..16]));
        if (_chk.Length > 0)
            yield return ("CHK header", new MemoryBlockSource(_chk[..16].Concat(_chk[40..]).ToArray()));
        foreach (FirmwarePart p in Parts)
            if (p.Name != open.Name)
                yield return (p.Name, ReferenceEquals(p, _trailing) ? Slice(p.Offset, ContentEnd) : Open(p));
        long trxEnd = TrxOffset + Length;
        long kernelEnd = _chk.Length > 0 ? TrxOffset + ChkField(ChkKernelLength) : trxEnd;
        if (_trailing is not null && open.Name == _trailing.Name)
        {
            yield return ("TRX image", Slice(TrxOffset, trxEnd));
            foreach ((string name, IBlockSource data) in TailSections(_trailing, open, _chk.Length > 0 ? Math.Max(kernelEnd, trxEnd) : ContentEnd, TrxOffset))
                yield return (name, data);
        }
        else if (_slots.Count > 0)
        {
            // Padding alone behind the last part is made anew; anything else must follow it.
            Tail tail = TailOf(_slots[^1].Part, trxEnd, TrxOffset);
            if (tail.Start < trxEnd || tail.Markers.Count > 0)
                foreach ((string name, IBlockSource data) in TailSections(_slots[^1].Part, open, trxEnd, TrxOffset)) yield return (name, data);
            if (_trailing is not null) yield return ("behind the TRX image", Slice(_trailing.Offset, ContentEnd));
        }
        long after = _chk.Length > 0 ? Math.Max(trxEnd, kernelEnd + ChkField(ChkRootfsLength)) : _trailing is null ? trxEnd : ContentEnd;
        if (after < ContentEnd) yield return ("data behind the image", Slice(after, ContentEnd));
        foreach ((string name, IBlockSource data) in FwtoolSections()) yield return (name, data);
    }
}
