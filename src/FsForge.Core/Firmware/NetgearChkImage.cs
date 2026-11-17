using System.Buffers.Binary;
using System.Text;
using FsForge.Core.Images;

namespace FsForge.Core.Firmware;

/// <summary>
/// The Netgear CHK image (OpenWrt's mkchkimg) around something other than a TRX image: a big-endian header "*#$^"
/// with its length, the region and version bytes, Netgear checksums of the kernel, the root filesystem, both and the
/// header, the two lengths and the board name; then the kernel (mkchkimg -k) and an optional root filesystem of its own
/// (-f). OpenWrt's ramips and lantiq Netgear images give mkchkimg the whole sysupgrade image as the kernel: a uImage
/// with the root filesystem behind it, inside the kernel part. A CHK holding a TRX is read by <see cref="TrxImage"/>.
/// </summary>
public sealed class NetgearChkImage : FirmwareImage
{
    private const int KernelSum = 16, RootfsSum = 20, KernelLength = 24, RootfsLength = 28, ImageSum = 32, HeaderSum = 36, BoardAt = 40;

    /// <summary>The longest board name taken (mkchkimg allows 64 characters; vendor images have longer headers, but not much).</summary>
    private const int MaxHeader = BoardAt + 1024;

    private NetgearChkImage(IBlockSource source) : base(source) { }

    private byte[] _header = Array.Empty<byte>();

    /// <summary>The board name of the header.</summary>
    public string Board { get; private init; } = "";

    /// <summary>The region byte (1 world wide, 2 North America).</summary>
    public byte Region { get; private init; }

    /// <summary>The four checksums are what Netgear's checksum gives (mkchkimg): only then is the image rebuilt.</summary>
    public bool ChkChecksumsValid { get; private set; }

    /// <summary>Where the kernel part starts (the header length).</summary>
    public long KernelOffset => _header.Length;

    public long KernelPartLength => Field(KernelLength);
    public long RootfsPartLength => Field(RootfsLength);

    /// <summary>The root filesystem lies inside the kernel part (behind a uImage), not in a part of its own.</summary>
    public bool RootfsInKernel { get; private set; }

    public override string Kind => "Netgear CHK image";
    public override string ShortKind => "CHK";
    public override string Description => Board.Length > 0 ? $"{Kind} for '{Board}'" : Kind;
    public override bool ChecksumsValid => ChkChecksumsValid;

    private uint Field(int at) => BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(at));

    /// <summary>A CHK header whose lengths fit the file, with a printable board name; a TRX inside is left to <see cref="TrxImage"/>.</summary>
    public static bool IsChk(IBlockSource source) => HeaderLength(source) is not null && !TrxImage.IsTrx(source);

    private static int? HeaderLength(IBlockSource source)
    {
        if (source.Length < BoardAt + 4) return null;
        byte[] h = source.ReadBytes(0, BoardAt);
        if (BinaryPrimitives.ReadUInt32BigEndian(h) != TrxImage.ChkMagic) return null;
        uint length = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(4));
        if (length is < BoardAt or > MaxHeader || length >= source.Length) return null;
        long kernel = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(KernelLength)), rootfs = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(RootfsLength));
        if (kernel == 0 || length + kernel + rootfs > source.Length) return null;
        byte[] board = source.ReadBytes(BoardAt, (int)length - BoardAt);
        if (board.Any(b => b is < 0x20 or >= 0x7F && b != 0)) return null;
        return (int)length;
    }

    public static NetgearChkImage Read(IBlockSource source, CancellationToken ct = default)
    {
        int length = HeaderLength(source) ?? throw new FsException("The file is not a Netgear CHK image.");
        byte[] h = source.ReadBytes(0, length);
        string board = Encoding.ASCII.GetString(h, BoardAt, length - BoardAt);
        int nul = board.IndexOf('\0');
        var image = new NetgearChkImage(source)
        {
            _header = h,
            Board = (nul >= 0 ? board[..nul] : board).Trim(),
            Region = h[8],
        };
        image.ReadParts(ct);
        image.ChkChecksumsValid = image.CheckSums(ct);
        if (!image.ChkChecksumsValid)
            image.Warnings.Add("The checksums of the Netgear CHK header do not match the way mkchkimg computes them: the image was changed or damaged, " +
                               "or it was made by a tool this program does not know.");
        return image;
    }

    private void ReadParts(CancellationToken ct)
    {
        long kernelStart = KernelOffset, kernelEnd = kernelStart + KernelPartLength, rootfsEnd = kernelEnd + RootfsPartLength;
        var parts = new List<FirmwarePart>();
        void Add(string name, long offset, long length, string detail)
        {
            using var data = new SubBlockSource(Source, offset, length, ownsInner: false);
            (PartitionContent content, string contentName, var packed) = Identify(data);
            if (content is PartitionContent.Unknown && contentName.Length == 0 && UImage.IsUImage(data)) contentName = "uImage";
            if (content is PartitionContent.Unknown && contentName.Length == 0 && length >= 4 && data.ReadBytes(0, 4) is [0x5D, 0, 0, _] or [0x6D, 0, 0, _])
                contentName = "LZMA data";
            parts.Add(new FirmwarePart
            {
                Index = parts.Count, Name = name, Offset = offset, Length = length,
                Content = content, ContentName = contentName, Packed = packed, Detail = detail,
            });
        }

        // Inside the kernel part: a root filesystem behind a uImage (OpenWrt's sysupgrade image given to mkchkimg -k).
        long? inside = null;
        if (RootfsPartLength == 0)
        {
            using var kernel = new SubBlockSource(Source, kernelStart, KernelPartLength, ownsInner: false);
            long behind = UImage.IsUImage(kernel) && KernelPartLength >= 64
                ? kernelStart + 64 + BinaryPrimitives.ReadUInt32BigEndian(kernel.ReadBytes(12, 4))
                : kernelStart;
            if (behind > kernelStart && behind < kernelEnd && FindTrailing(behind, ct) is { } at && at < kernelEnd) inside = at;
        }
        if (inside is { } fs)
        {
            RootfsInKernel = true;
            Add("kernel", kernelStart, fs - kernelStart, "the kernel part of the CHK header, up to the root filesystem");
            Add("rootfs", fs, kernelEnd - fs, "inside the kernel part, behind the uImage");
        }
        else
        {
            Add("kernel", kernelStart, KernelPartLength, "the kernel part of the CHK header");
            if (RootfsPartLength > 0) Add("rootfs", kernelEnd, RootfsPartLength, "the root filesystem part of the CHK header");
        }
        if (rootfsEnd < Source.Length)
            Warnings.Add($"{Source.Length - rootfsEnd} bytes follow the parts the Netgear CHK header lists; they are carried over unchanged.");
        Parts = parts;
    }

    private bool CheckSums(CancellationToken ct)
    {
        long kernelEnd = KernelOffset + KernelPartLength, rootfsEnd = kernelEnd + RootfsPartLength;
        var kernel = new TrxImage.NetgearSum();
        kernel.Add(Source, KernelOffset, kernelEnd, ct);
        var rootfs = new TrxImage.NetgearSum();
        rootfs.Add(Source, kernelEnd, rootfsEnd, ct);
        var whole = new TrxImage.NetgearSum();
        whole.Add(Source, KernelOffset, rootfsEnd, ct);
        byte[] header = (byte[])_header.Clone();
        header.AsSpan(HeaderSum, 4).Clear();
        var head = new TrxImage.NetgearSum();
        head.Add(header);
        return kernel.Finish() == Field(KernelSum) && rootfs.Finish() == Field(RootfsSum)
               && whole.Finish() == Field(ImageSum) && head.Finish() == Field(HeaderSum);
    }

    // ---------------------------------------------------------- writing

    public override bool CanRebuild(FirmwarePart part, out string reason)
    {
        reason = !Parts.Contains(part) ? "The part does not belong to this image."
            : part.Name != "rootfs" ? $"Only the root filesystem of a Netgear CHK image is rebuilt, not '{part.Name}'."
            : part.Content == PartitionContent.Unknown ? $"'{part.Name}' holds nothing this program writes."
            : !ChkChecksumsValid ? "The checksums of the Netgear CHK header do not match the way this program computes them, so they cannot be made again."
            : "";
        return reason.Length == 0;
    }

    /// <summary>
    /// Writes the image as mkchkimg makes it from the new parts: the root filesystem replaced in its part (or inside the
    /// kernel part, the uImage in front unchanged), what followed it inside that part kept behind it (OpenWrt's
    /// 0xdeadc0de markers on the first erase block boundaries behind the new one), the header with the new length and
    /// the four checksums; region, version bytes and board stay.
    /// </summary>
    public override void Write(Stream output, FirmwarePart part, IBlockSource data, List<string> warnings,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        if (!CanRebuild(part, out string reason)) throw new FsException(reason);
        long kernelStart = KernelOffset, kernelEnd = kernelStart + KernelPartLength, rootfsEnd = kernelEnd + RootfsPartLength;
        // The region the root filesystem lies in, from its start (the boundaries of the markers count from there: the
        // bootloader writes each part to flash without the header).
        (long regionStart, long regionEnd) = RootfsInKernel ? (kernelStart, kernelEnd) : (kernelEnd, rootfsEnd);
        var region = new List<IBlockSource> { Slice(regionStart, part.Offset), data };
        Tail tail = TailOf(part, regionEnd, regionStart);
        if (tail.Start < regionEnd || tail.Markers.Count > 0)
            region.AddRange(TailPieces(part, part.Offset + data.Length, regionEnd, regionStart));

        List<IBlockSource> kernel = RootfsInKernel ? region : new List<IBlockSource> { Slice(kernelStart, kernelEnd) };
        List<IBlockSource> rootfs = RootfsInKernel ? new List<IBlockSource>() : region;
        long kernelLength = kernel.Sum(p => p.Length), rootfsLength = rootfs.Sum(p => p.Length);
        if (kernelLength > uint.MaxValue || rootfsLength > uint.MaxValue) throw new FsException("The new image is too large for a Netgear CHK image.");

        var kernelSum = new TrxImage.NetgearSum();
        var rootfsSum = new TrxImage.NetgearSum();
        var wholeSum = new TrxImage.NetgearSum();
        byte[] buf = new byte[1 << 20];
        void Sum(List<IBlockSource> pieces, ref TrxImage.NetgearSum one)
        {
            foreach (IBlockSource p in pieces)
                for (long o = 0; o < p.Length;)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = (int)Math.Min(buf.Length, p.Length - o);
                    p.ReadExactly(o, buf.AsSpan(0, n));
                    one.Add(buf.AsSpan(0, n));
                    wholeSum.Add(buf.AsSpan(0, n));
                    o += n;
                }
        }
        Sum(kernel, ref kernelSum);
        Sum(rootfs, ref rootfsSum);

        byte[] header = (byte[])_header.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(KernelLength), (uint)kernelLength);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(RootfsLength), (uint)rootfsLength);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(KernelSum), kernelSum.Finish());
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(RootfsSum), rootfsSum.Finish());
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(ImageSum), wholeSum.Finish());
        header.AsSpan(HeaderSum, 4).Clear();
        var head = new TrxImage.NetgearSum();
        head.Add(header);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(HeaderSum), head.Finish());

        var pieces = new List<IBlockSource> { new MemoryBlockSource(header) };
        pieces.AddRange(kernel);
        pieces.AddRange(rootfs);
        if (rootfsEnd < Source.Length) pieces.Add(Slice(rootfsEnd, Source.Length));
        WritePieces(output, pieces, warnings, progress, ct);
    }

    /// <summary>What a rewrite keeps: the header without lengths and sums, the kernel, and what followed the root filesystem.</summary>
    public override IEnumerable<(string Name, IBlockSource Data)> FixedSections(FirmwarePart open)
    {
        yield return ("CHK header", new MemoryBlockSource(_header[..16].Concat(_header[BoardAt..]).ToArray()));
        foreach (FirmwarePart p in Parts)
            if (p.Name != open.Name) yield return (p.Name, Open(p));
        if (Parts.FirstOrDefault(p => p.Name == "rootfs") is { } rootfs && open.Name == "rootfs")
        {
            long regionEnd = RootfsInKernel ? KernelOffset + KernelPartLength : KernelOffset + KernelPartLength + RootfsPartLength;
            long regionStart = RootfsInKernel ? KernelOffset : KernelOffset + KernelPartLength;
            Tail tail = TailOf(rootfs, regionEnd, regionStart);
            if (tail.Start < regionEnd || tail.Markers.Count > 0)
                foreach ((string name, IBlockSource data) in TailSections(rootfs, open, regionEnd, regionStart)) yield return (name, data);
        }
        long after = KernelOffset + KernelPartLength + RootfsPartLength;
        if (after < Source.Length) yield return ("data behind the image", Slice(after, Source.Length));
    }
}
