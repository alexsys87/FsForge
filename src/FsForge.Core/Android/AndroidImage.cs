using System.Buffers.Binary;
using FsForge.Core.Images;

namespace FsForge.Core.Android;

/// <summary>What kind of Android container a file is.</summary>
public enum AndroidImageKind
{
    /// <summary>super.img: logical (dynamic) partitions described by LP metadata.</summary>
    Super,

    /// <summary>boot.img, recovery.img, init_boot.img: kernel and ramdisk behind an "ANDROID!" header.</summary>
    Boot,

    /// <summary>vendor_boot.img: vendor ramdisks and DTB behind a "VNDRBOOT" header.</summary>
    VendorBoot,
}

/// <summary>Something an Android container holds that can be opened: a logical partition or a ramdisk.</summary>
public sealed class AndroidPart
{
    public int Index { get; init; }

    /// <summary>"system_a", "vendor_b", "ramdisk", "vendor_ramdisk", the name of a vendor ramdisk fragment.</summary>
    public string Name { get; init; } = "";

    /// <summary>Size of the content in bytes.</summary>
    public long Length { get; init; }

    public PartitionContent Content { get; init; }

    /// <summary>Exact kind of the content when known ("ext4", "EROFS", "cpio (lz4)").</summary>
    public string ContentName { get; init; } = "";

    /// <summary>More about the part: the group of a logical partition, the type of a vendor ramdisk.</summary>
    public string Detail { get; init; } = "";

    /// <summary>The part holds a filesystem or archive this program can open.</summary>
    public bool IsReadable => Content is PartitionContent.Ext or PartitionContent.Erofs or PartitionContent.SquashFs
        or PartitionContent.Cramfs or PartitionContent.Romfs or PartitionContent.Fat or PartitionContent.Cpio;

    public override string ToString()
    {
        string what = ContentName.Length > 0 ? ContentName : Length == 0 ? "empty" : "unknown content";
        return $"{Name}: {what}, {DiskText.Size(Length)}{(Detail.Length > 0 ? ", " + Detail : "")}";
    }
}

/// <summary>An Android container: super.img, boot.img or vendor_boot.img.</summary>
public abstract class AndroidImage
{
    public abstract AndroidImageKind Kind { get; }

    /// <summary>"super", "boot (header v4)", "vendor_boot (header v3)".</summary>
    public abstract string Description { get; }

    public IReadOnlyList<AndroidPart> Parts { get; protected set; } = Array.Empty<AndroidPart>();

    /// <summary>Non-fatal problems found while reading.</summary>
    public List<string> Warnings { get; } = new();

    public IReadOnlyList<AndroidPart> Readable => Parts.Where(p => p.IsReadable).ToList();

    /// <summary>The part opened when none is asked for: the largest readable one.</summary>
    public virtual AndroidPart? PickDefault() => Parts.Where(p => p.IsReadable).MaxBy(p => p.Length);

    /// <summary>A part by name (case-insensitive) or by number.</summary>
    public AndroidPart? Find(string nameOrIndex) =>
        Parts.FirstOrDefault(p => string.Equals(p.Name, nameOrIndex, StringComparison.OrdinalIgnoreCase))
        ?? (int.TryParse(nameOrIndex, out int i) ? Parts.FirstOrDefault(p => p.Index == i) : null);

    public abstract IBlockSource Open(AndroidPart part);

    /// <summary>The things the container keeps besides its parts (kernel, DTB...), to check they survive a rewrite.</summary>
    public virtual IEnumerable<(string Name, IBlockSource Data)> FixedSections() => Array.Empty<(string, IBlockSource)>();

    /// <summary>
    /// Writes the container again with <paramref name="data"/> as the new content of <paramref name="part"/>;
    /// everything else is kept.
    /// </summary>
    public abstract void Write(Stream output, AndroidPart part, IBlockSource data, List<string> warnings,
        IProgress<ProgressInfo>? progress, CancellationToken ct);

    /// <summary>Recognizes an Android container; null when the source is none.</summary>
    public static AndroidImage? TryRead(IBlockSource source, bool sparse)
    {
        if (SuperImage.IsSuper(source)) return SuperImage.Read(source, sparse);
        if (BootImage.IsBoot(source)) return BootImage.Read(source);
        return null;
    }

    public static bool IsAndroidImage(IBlockSource source) => SuperImage.IsSuper(source) || BootImage.IsBoot(source);

    /// <summary>What a part holds, as the partitions of a disk image are identified, plus compressed cpio archives.</summary>
    internal static (PartitionContent Content, string Name) Identify(IBlockSource data)
    {
        if (data.Length == 0) return (PartitionContent.Empty, "");
        if (Cpio.CpioReader.IsCpio(data, out Tar.ArchiveCompression c))
            return (PartitionContent.Cpio, c == Tar.ArchiveCompression.None ? "cpio" : $"cpio ({c.ToString().ToLowerInvariant()})");
        (PartitionContent content, string name, string _) = PartitionTable.Identify(data, 0, data.Length);
        return (content, name);
    }
}

/// <summary>Pieces of other sources one after another; a null piece reads as zeros.</summary>
internal sealed class ConcatSource : IBlockSource
{
    private readonly (IBlockSource? Source, long Offset, long Length)[] _pieces;
    private readonly long[] _starts;

    public ConcatSource(IEnumerable<(IBlockSource? Source, long Offset, long Length)> pieces)
    {
        _pieces = pieces.Where(p => p.Length > 0).ToArray();
        _starts = new long[_pieces.Length];
        long pos = 0;
        for (int i = 0; i < _pieces.Length; i++)
        {
            _starts[i] = pos;
            pos += _pieces[i].Length;
        }
        Length = pos;
    }

    public long Length { get; }

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= Length) return 0;
        if (buffer.Length > Length - offset) buffer = buffer[..(int)(Length - offset)];
        int total = 0;
        while (buffer.Length > 0)
        {
            int i = Array.BinarySearch(_starts, offset);
            if (i < 0) i = ~i - 1;
            (IBlockSource? src, long at, long len) = _pieces[i];
            long inPiece = offset - _starts[i];
            int n = (int)Math.Min(buffer.Length, len - inPiece);
            Span<byte> part = buffer[..n];
            if (src is null) part.Clear();
            else
            {
                // A piece that lies beyond the end of its source (a truncated image) reads as zeros there.
                long avail = Math.Max(0, Math.Min(n, src.Length - (at + inPiece)));
                if (avail > 0) src.ReadExactly(at + inPiece, part[..(int)avail]);
                part[(int)avail..].Clear();
            }
            buffer = buffer[n..];
            offset += n;
            total += n;
        }
        return total;
    }

    public void Dispose() { }
}

/// <summary>
/// An image described as pieces of data at offsets, with zeros in between: written as a plain file or as an
/// Android sparse image (pieces of zeros become FILL chunks, the gaps DONT_CARE chunks, as lpmake does).
/// </summary>
internal sealed class ImageLayout
{
    private readonly List<(long Offset, IBlockSource Data)> _pieces = new();

    public ImageLayout(long length) => Length = length;

    public long Length { get; private set; }

    public void Add(long offset, IBlockSource data)
    {
        if (data.Length == 0) return;
        _pieces.Add((offset, data));
        Length = Math.Max(Length, offset + data.Length);
    }

    public void Add(long offset, byte[] data) => Add(offset, new MemoryBlockSource(data));

    /// <summary>Makes the image at least this long (zeros at the end).</summary>
    public void Extend(long length) => Length = Math.Max(Length, length);

    private IEnumerable<(long Offset, IBlockSource Data)> Sorted()
    {
        var list = _pieces.OrderBy(p => p.Offset).ToList();
        for (int i = 1; i < list.Count; i++)
            if (list[i].Offset < list[i - 1].Offset + list[i - 1].Data.Length)
                throw new InvalidOperationException("Overlapping pieces in an image layout.");
        return list;
    }

    public void WriteRaw(Stream output, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        long start = output.Position;
        byte[] buf = new byte[1 << 20];
        long done = 0, total = _pieces.Sum(p => p.Data.Length);
        long pos = 0;
        foreach ((long offset, IBlockSource data) in Sorted())
        {
            if (offset > pos)
            {
                // The gap stays a hole where the filesystem allows it.
                output.SetLength(Math.Max(output.Length, start + offset));
                output.Position = start + offset;
            }
            for (long o = 0; o < data.Length; o += buf.Length)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buf.Length, data.Length - o);
                data.ReadExactly(o, buf.AsSpan(0, n));
                output.Write(buf, 0, n);
                done += n;
                progress?.Report(new ProgressInfo("write", done, total));
            }
            pos = offset + data.Length;
        }
        if (Length > pos) output.SetLength(Math.Max(output.Length, start + Length));
        output.Position = start + Length;
    }

    private const int SparseBlock = 4096;
    private const ushort ChunkRaw = 0xCAC1, ChunkFill = 0xCAC2, ChunkDontCare = 0xCAC3;

    public void WriteSparse(Stream output, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        long blocks = (Length + SparseBlock - 1) / SparseBlock;
        if (blocks > uint.MaxValue) throw new FsException("The image is too large for the Android sparse format.");
        long headerAt = output.Position;
        output.Write(new byte[28]);
        uint chunks = 0;
        long done = 0, total = _pieces.Sum(p => p.Data.Length);

        // Runs of blocks of one kind: 0 raw, 1 zeros (FILL 0), 2 not covered (DONT_CARE).
        int runKind = -1;
        long runStart = 0, runBlocks = 0;
        var raw = new MemoryStream();
        void Flush()
        {
            if (runBlocks == 0) return;
            Span<byte> ch = stackalloc byte[12];
            ushort type = runKind == 0 ? ChunkRaw : runKind == 1 ? ChunkFill : ChunkDontCare;
            uint size = 12 + (uint)(runKind == 0 ? raw.Length : runKind == 1 ? 4 : 0);
            BinaryPrimitives.WriteUInt16LittleEndian(ch, type);
            BinaryPrimitives.WriteUInt32LittleEndian(ch[4..], (uint)runBlocks);
            BinaryPrimitives.WriteUInt32LittleEndian(ch[8..], size);
            output.Write(ch);
            if (runKind == 0) output.Write(raw.GetBuffer(), 0, (int)raw.Length);
            else if (runKind == 1) output.Write(new byte[4]);
            raw.SetLength(0);
            chunks++;
            runBlocks = 0;
        }
        void Block(int kind, ReadOnlySpan<byte> data)
        {
            // Raw chunks are kept below 64 MiB so that no chunk has to be held in memory any longer.
            if (kind != runKind || (kind == 0 && raw.Length >= 64 << 20)) { Flush(); runKind = kind; }
            if (kind == 0) raw.Write(data);
            runBlocks++;
        }

        byte[] buf = new byte[SparseBlock];
        List<(long Offset, IBlockSource Data)> pieces = Sorted().ToList();
        int pi = 0;
        for (long b = 0; b < blocks; b++)
        {
            if ((b & 0xFF) == 0) ct.ThrowIfCancellationRequested();
            long at = b * SparseBlock, end = at + SparseBlock;
            while (pi < pieces.Count && pieces[pi].Offset + pieces[pi].Data.Length <= at) pi++;
            bool covered = false;
            Array.Clear(buf);
            for (int j = pi; j < pieces.Count && pieces[j].Offset < end; j++)
            {
                (long po, IBlockSource pd) = pieces[j];
                long from = Math.Max(at, po), to = Math.Min(end, po + pd.Length);
                if (to <= from) continue;
                covered = true;
                pd.ReadExactly(from - po, buf.AsSpan((int)(from - at), (int)(to - from)));
                done += to - from;
            }
            if (!covered) Block(2, default);
            else if (!buf.AsSpan().ContainsAnyExcept((byte)0)) Block(1, default);
            else Block(0, buf);
            if ((b & 0x3FF) == 0) progress?.Report(new ProgressInfo("write", done, total));
        }
        Flush();

        long endPos = output.Position;
        Span<byte> h = stackalloc byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(h, AndroidSparseSource.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(h[8..], 28);
        BinaryPrimitives.WriteUInt16LittleEndian(h[10..], 12);
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], SparseBlock);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], (uint)blocks);
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], chunks);
        output.Position = headerAt;
        output.Write(h);
        output.Position = endPos;
    }
}
