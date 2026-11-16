using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using FsForge.Core.Compression;
using FsForge.Core.Ext;
using static FsForge.Core.Erofs.ErofsFormat;

namespace FsForge.Core.Erofs;

/// <summary>
/// Builds an EROFS image from a tree, the way mkfs.erofs does: 4 KiB blocks, file data compressed into
/// clusters of a fixed size (LZ4 or MicroLZMA) with compact cluster indexes, tails of uncompressed files
/// and small directories next to their inodes, 32-byte inodes where the attributes allow it, extended
/// attributes shared between inodes.
/// <para>
/// Layout: block 0 with the superblock; the file data; the shared extended attributes; the inodes;
/// the blocks of the directories.
/// </para>
/// </summary>
public sealed class ErofsWriter
{
    private const int Blk = BlockSize, BlkBits = 12;
    private const int InMemoryFileLimit = 32 << 20;
    private const long PendingBytesLimit = 192L << 20;
    private const int TailMemoryLimit = 32 << 20;

    private readonly FsTree _tree;
    private readonly Stream _out;
    private readonly ErofsOptions _opt;
    private readonly string? _tempDirectory;
    private readonly IProgress<ProgressInfo>? _progress;
    private readonly CancellationToken _ct;
    private readonly long _base;
    private readonly bool _compress, _big;
    private readonly int _pclusterSize, _segmentSize, _algorithm, _dictionary;
    private readonly ConcurrentBag<Codec> _codecs = new();
    private readonly Queue<Pending> _queue = new();
    private readonly int _maxTasks;
    private readonly SemaphoreSlim _workers;        // at most this many pieces are compressed at a time
    private int _tasks;                             // compression tasks in the queue
    private readonly Dictionary<ContentKey, FileLayout> _dedup = new();
    private static readonly byte[] Zeros = new byte[Blk];

    private long _block;                            // next block to write, counted from the start of the image
    private long _pendingBytes;
    private long _bytesDone, _bytesTotal;
    private long _epoch;
    private long _tailMemory;
    private TempSpool? _spool;

    public List<string> Warnings { get; } = new();

    // ------------------------------------------------------------------ model

    /// <summary>Where the content of a regular file went.</summary>
    private sealed class FileLayout
    {
        public long Size;
        public bool Compressed;
        public long StartBlock;
        public long Blocks;                         // blocks of file data in the image
        public List<ExtentRecord>? Extents;         // of a compressed file, in order
        public bool TailInline;                     // the last, partial block lies next to the inode
        public IFileData? Tail;                     // those bytes, unless the file is nothing but its tail
    }

    /// <summary>A compressed or plain cluster: the bytes of the file it stands for and its size in blocks.</summary>
    private readonly record struct ExtentRecord(int Length, int Blocks, bool Raw);

    private readonly record struct ContentKey(long Length, UInt128 Hash);

    /// <summary>An inode of the image.</summary>
    private sealed class Item
    {
        public required FsInode Inode;
        public required FsNode Node;                // one of its names
        public Item? Parent;                        // of a directory
        public List<(byte[] Name, Item Target)>? Entries;   // of a directory, sorted, without "." and ".."
        public bool Extended;
        public byte[] Xattrs = Array.Empty<byte>();
        public FileLayout? File;
        public byte[]? Index;                       // cluster index of a compressed file, map header included
        public long Size;
        public int Layout;
        public uint Union;
        public int TailSize;                        // bytes that follow the inode
        public long LateBlocks;                     // blocks written behind the inodes (directories, long links)
        public long MetaOffset;                     // in the metadata area
        public uint Number;

        public int InodeSize => Extended ? InodeExtendedSize : InodeCompactSize;
        public int HeadSize => InodeSize + Xattrs.Length + (Index is null ? 0 : ((InodeSize + Xattrs.Length + 7) & ~7) - InodeSize - Xattrs.Length + Index.Length);
        public ulong Nid => (ulong)MetaOffset >> SlotBits;
    }

    private sealed class Pending
    {
        public Task<SegmentResult>? Task;
        public FileLayout? Layout;
        public bool Start, End;
        public long Bytes;
        public byte[]? Content;                     // the whole file, when it was read into memory
        public byte[]? Buffer;                      // the segment, when the file is streamed
        public Item? Item;
    }

    private readonly record struct SegmentResult(byte[] Data, int Length, ExtentRecord[] Extents);

    /// <summary>The compressor of one worker.</summary>
    private sealed class Codec : IDisposable
    {
        private readonly Lz4FixedEncoder? _lz4;
        private readonly Lzma2Encoder? _lzma;
        private readonly int _deflateLevel = -1;
        private readonly MemoryStream _deflated = new();
        private readonly ZstdSharp.Compressor? _zstd;
        private readonly int _dictionary;
        private byte[] _fit = Array.Empty<byte>();
        private long _lastFit;              // how much input the previous cluster took: neighbours compress alike

        public Codec(ErofsOptions o, int dictionary)
        {
            _dictionary = dictionary;
            Scratch = new byte[o.PclusterSize];
            switch (o.Compression)
            {
                case ErofsCompression.Lzma:
                    _lzma = new Lzma2Encoder(LzmaEncoderSettings.FromEffort(o.LzmaEffort));
                    break;
                case ErofsCompression.Deflate:
                    _deflateLevel = o.DeflateLevel;
                    break;
                case ErofsCompression.Zstd:
                    _zstd = new ZstdSharp.Compressor(o.ZstdLevel);
                    // The kernel refuses a frame whose window is larger than the one the superblock announces.
                    _zstd.SetParameter(ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_windowLog, System.Numerics.BitOperations.Log2((uint)dictionary));
                    break;
                default:
                    _lz4 = new Lz4FixedEncoder(o.Compression == ErofsCompression.Lz4 ? 1 : o.Lz4Level);
                    break;
            }
        }

        public byte[] Scratch { get; }

        /// <summary>DEFLATE and Zstandard compress a whole input: the input is searched for, see <see cref="Fit"/>.</summary>
        public bool Searches => _deflateLevel >= 0 || _zstd is not null;

        /// <summary>Fills <see cref="Scratch"/> with as much of the input as fits; returns the compressed size.</summary>
        public int Compress(ReadOnlySpan<byte> src, out int consumed)
        {
            if (_lz4 is not null) return _lz4.Compress(src, Scratch, out consumed);
            if (_lzma is not null) return Math.Max(_lzma.EncodeMicroLzma(src, Scratch, _dictionary, out consumed), 0);
            int size = Fit(src, out consumed);
            // A DEFLATE stream that starts with a stored block may start with a zero byte, which would be taken
            // for padding; its unused bits are set instead (as mkfs.erofs does).
            if (size > 0 && _deflateLevel >= 0 && Scratch[0] == 0) Scratch[0] = 1 << 3;
            return size;
        }

        /// <summary>
        /// A long beginning of the input whose compressed form fits into the cluster (the search of mkfs.erofs):
        /// the length the previous cluster took, a guess from the ratio, then halving between what fits and
        /// what does not, until the two are less than 1 % apart.
        /// </summary>
        private int Fit(ReadOnlySpan<byte> src, out int consumed)
        {
            int cap = Scratch.Length;
            if (_fit.Length < cap + 64) _fit = new byte[cap + 64];
            long fits = 0, tooLong = (long)src.Length + 1;
            int best = 0;
            long m = _lastFit > 0 ? _lastFit * 15 / 16 : cap * 4L;
            while (true)
            {
                m = Math.Min(Math.Max(m, fits + 1), tooLong - 1);
                int size = Once(src[..(int)m]);
                if (size > 0 && size <= cap)
                {
                    _fit.AsSpan(0, size).CopyTo(Scratch);
                    fits = m;
                    best = size;
                    if (tooLong - fits <= Math.Max(1, fits >> 7) || size + 32 >= cap) break;
                    // The guess from the ratio stays below what does not fit.
                    m = Math.Min(cap * m / size, fits + (tooLong - fits) * 3 / 4);
                }
                else
                {
                    tooLong = m;
                    if (tooLong - fits <= Math.Max(1, fits >> 7)) break;
                    m = (fits + tooLong) / 2;
                }
            }
            if (fits > 0) _lastFit = fits;
            consumed = (int)fits;
            return best;
        }

        private int Once(ReadOnlySpan<byte> src)
        {
            if (_zstd is not null) return _zstd.TryWrap(src, _fit, out int written) ? written : -1;
            // zlib of the runtime: a bare stream with the 32 KiB window the kernel inflates with.
            _deflated.SetLength(0);
            using (DeflateStream d = ZLibLevels.OpenDeflate(_deflated, _deflateLevel, leaveOpen: true)) d.Write(src);
            if (_deflated.Length > _fit.Length) return -1;
            _deflated.GetBuffer().AsSpan(0, (int)_deflated.Length).CopyTo(_fit);
            return (int)_deflated.Length;
        }

        public void Dispose() => _zstd?.Dispose();
    }

    // ------------------------------------------------------------------ setup

    private ErofsWriter(FsTree tree, Stream output, ErofsOptions options, string? tempDirectory, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        options.Validate();
        _tree = tree;
        _out = output;
        _opt = options;
        _tempDirectory = tempDirectory;
        _progress = progress;
        _ct = ct;
        _base = output.Position;
        _compress = options.Compression != ErofsCompression.None;
        _pclusterSize = options.PclusterSize;
        _big = _compress && _pclusterSize > Blk;
        _algorithm = options.Compression switch
        {
            ErofsCompression.Lzma => AlgLzma,
            ErofsCompression.Deflate => AlgDeflate,
            ErofsCompression.Zstd => AlgZstd,
            _ => AlgLz4,
        };
        _dictionary = options.Compression == ErofsCompression.Zstd ? options.EffectiveZstdWindow : options.EffectiveLzmaDictionary;
        // Clusters never cross a segment, so that the segments of a file can be compressed side by side.
        _segmentSize = Math.Clamp(_pclusterSize * 32, 4 << 20, 16 << 20);
        int threads = options.Threads > 0 ? options.Threads : Environment.ProcessorCount;
        // Every worker gets a few pieces ahead; the markers between them (start and end of a file) cost nothing.
        _maxTasks = Math.Max(2, threads * 4);
        _workers = new SemaphoreSlim(threads, threads);
    }

    /// <summary>Writes the image at the current position of <paramref name="output"/> (must be seekable).</summary>
    /// <param name="tempDirectory">Folder for temporary data; null means the system temporary folder.</param>
    public static IReadOnlyList<string> Write(FsTree tree, Stream output, ErofsOptions options,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default, string? tempDirectory = null)
    {
        if (!output.CanSeek) throw new ArgumentException("The output stream must be seekable.", nameof(output));
        var w = new ErofsWriter(tree, output, options, tempDirectory, progress, ct);
        try
        {
            w.Run();
        }
        finally
        {
            while (w._codecs.TryTake(out Codec? c)) c.Dispose();
            foreach (Pending p in w._queue)
            {
                try { p.Task?.Wait(); } catch { /* the first error is the one reported */ }
            }
            w._spool?.Dispose();
            w._workers.Dispose();
        }
        return w.Warnings;
    }

    private void Report(string stage, string? item) =>
        _progress?.Report(new ProgressInfo(stage, _bytesDone, _bytesTotal, item));

    // ----------------------------------------------------------------- driver

    private void Run()
    {
        // ---- the inodes, in the order they get in the image: a directory, then what it holds
        var items = new List<Item>();
        var byInode = new Dictionary<FsInode, Item>(ReferenceEqualityComparer.Instance);
        var times = new Dictionary<long, int>();
        foreach (FsNode n in FsTree.Walk(_tree.Root))
        {
            if (!byInode.TryGetValue(n.Inode, out Item? item))
            {
                item = new Item { Inode = n.Inode, Node = n };
                byInode.Add(n.Inode, item);
                items.Add(item);
                times[n.Inode.MTime] = times.GetValueOrDefault(n.Inode.MTime) + 1;
                if (n.Type == FsNodeType.File) _bytesTotal += n.Inode.Size;
            }
            if (n.Parent is null) continue;
            Item parent = byInode[n.Parent.Inode];
            (parent.Entries ??= new()).Add((PosixName.Encode(n.Name), item));
            if (n.IsDirectory) item.Parent = parent;
        }

        // The time of the image is the time most inodes have: they can use the short inode form.
        _epoch = Math.Max(0, _opt.BuildTime ?? times.MaxBy(p => (p.Value, -p.Key)).Key);

        Dictionary<XattrKey, int> sharedIds = PlanXattrs(items, out byte[] sharedArea);
        foreach (Item item in items)
        {
            FsInode i = item.Inode;
            long links = i.Type == FsNodeType.Directory ? 2 + (item.Entries?.Count(e => e.Target.Inode.Type == FsNodeType.Directory) ?? 0) : i.LinkCount;
            uint uid = _opt.AllRoot ? 0 : i.Uid, gid = _opt.AllRoot ? 0 : i.Gid;
            item.Extended = uid > 0xFFFF || gid > 0xFFFF || links > 0xFFFF || i.MTime != _epoch || (i.Type == FsNodeType.File && i.Size > uint.MaxValue);
            item.Xattrs = BuildXattrs(item, sharedIds);
        }

        // ---- block 0 is written last; the file data starts in block 1
        _out.Position = _base;
        _out.Write(Zeros);
        _block = 1;
        Report("data", null);
        foreach (Item item in items)
        {
            if (item.Inode.Type != FsNodeType.File) continue;
            _ct.ThrowIfCancellationRequested();
            WriteFile(item);
        }
        DrainAll();
        Report("data", null);

        // ---- shared extended attributes
        long xattrBlock = 0;
        if (sharedArea.Length > 0)
        {
            xattrBlock = _block;
            WriteBlocks(sharedArea);
        }

        // ---- inodes
        long metaBlock = _block;
        foreach (Item item in items) PrepareInode(item);
        long metaBytes = PlaceInodes(items);
        long metaBlocks = (metaBytes + Blk - 1) >> BlkBits;
        long late = metaBlock + metaBlocks;
        uint number = 1;
        foreach (Item item in items)
        {
            item.Number = number++;
            if (item.LateBlocks == 0) continue;
            item.Union = Address(late);
            late += item.LateBlocks;
        }
        long totalBlocks = late;
        if (totalBlocks > uint.MaxValue) throw new FsException("The image would be larger than 16 TiB, the limit of this EROFS layout.");
        WriteInodes(items, metaBytes);
        _block += metaBlocks;

        // ---- directories and links that did not fit next to their inode
        foreach (Item item in items)
        {
            if (item.LateBlocks == 0) continue;
            _ct.ThrowIfCancellationRequested();
            byte[] content = item.Inode.Type == FsNodeType.Directory ? DirectoryData(item) : PosixName.Encode(item.Inode.LinkTarget ?? "");
            // What follows the inode as its tail is not written a second time.
            WriteBlocks(content.AsSpan(0, (int)Math.Min(content.Length, item.LateBlocks << BlkBits)));
        }
        if (_block != totalBlocks) throw new InvalidOperationException("EROFS writer: the layout does not match what was written.");

        // ---- superblock
        byte[] first = new byte[Blk];
        Span<byte> sb = first.AsSpan(SuperOffset);
        uint incompat = 0;
        if (_compress) incompat |= IncompatZeroPadding;
        bool configs = _compress && (_big || _algorithm != AlgLz4);
        if (configs) incompat |= IncompatComprCfgs;
        Le.W32(sb, 0, Magic);
        Le.W32(sb, 8, CompatSbChecksum | CompatMtime);
        sb[12] = BlkBits;
        Le.W16(sb, 14, (int)items[0].Nid);
        Le.W64(sb, 16, (ulong)items.Count);
        Le.W64(sb, 24, (ulong)_epoch);
        Le.W32(sb, 36, (uint)totalBlocks);
        Le.W32(sb, 40, (uint)metaBlock);
        Le.W32(sb, 44, (uint)xattrBlock);
        (_opt.Uuid ?? Guid.NewGuid()).TryWriteBytes(sb.Slice(48, 16), bigEndian: true, out _);
        PosixName.Encode(_opt.VolumeName).CopyTo(sb.Slice(64, 16));
        Le.W32(sb, 80, incompat);
        Le.W16(sb, 84, configs ? 1 << _algorithm : _compress ? 0xFFFF : 0);
        if (configs)
        {
            // A record per compression method: its length, then its parameters.
            Span<byte> cfg = sb[SuperSize..];
            switch (_algorithm)
            {
                case AlgLz4:
                    Le.W16(cfg, 0, 14);
                    Le.W16(cfg, 2, 0xFFFF);                     // the longest match distance
                    Le.W16(cfg, 4, _pclusterSize >> BlkBits);
                    break;
                case AlgLzma:
                    Le.W16(cfg, 0, 14);
                    Le.W32(cfg, 2, (uint)_dictionary);
                    break;
                case AlgDeflate:
                    Le.W16(cfg, 0, 6);
                    cfg[2] = 15;                                // window bits
                    break;
                default:
                    Le.W16(cfg, 0, 6);
                    cfg[2] = 0;                                 // format
                    cfg[3] = (byte)(System.Numerics.BitOperations.Log2((uint)_dictionary) - 10);   // window log - 10
                    break;
            }
        }
        Le.W32(sb, 4, ExtJournal.Crc32c(~0u, sb));
        long end = _base + (totalBlocks << BlkBits);
        _out.Position = _base;
        _out.Write(first);
        // A file that was first compressed and then stored as it is may have left data behind the end.
        if (_out.Length > end) _out.SetLength(end);
        _out.Position = end;
        _out.Flush();
        Report("done", null);
    }

    private static uint Address(long block) => (uint)block;

    /// <summary>Writes data as whole blocks, the last one padded with zeros.</summary>
    private void WriteBlocks(ReadOnlySpan<byte> data)
    {
        _out.Write(data);
        int rest = data.Length & (Blk - 1);
        if (rest != 0) _out.Write(Zeros.AsSpan(0, Blk - rest));
        _block += (data.Length + Blk - 1) >> BlkBits;
    }

    // ------------------------------------------------------ extended attributes

    private readonly record struct XattrKey(int Index, string Name, UInt128 ValueHash, int ValueLength);

    private static byte[] XattrEntry(int index, byte[] name, byte[] value)
    {
        byte[] e = new byte[(4 + name.Length + value.Length + 3) & ~3];
        e[0] = (byte)name.Length;
        e[1] = (byte)index;
        Le.W16(e, 2, value.Length);
        name.CopyTo(e, 4);
        value.CopyTo(e, 4 + name.Length);
        return e;
    }

    private static UInt128 Hash(ReadOnlySpan<byte> data)
    {
        Span<byte> h = stackalloc byte[32];
        SHA256.HashData(data, h);
        return new UInt128(BitConverter.ToUInt64(h), BitConverter.ToUInt64(h[8..]));
    }

    /// <summary>The attributes of an inode that the format can hold, as (key, entry) pairs.</summary>
    private IEnumerable<(XattrKey Key, byte[] Entry)> XattrsOf(Item item, bool warn)
    {
        if (!_opt.StoreXattrs || item.Inode.XAttrs is not { Count: > 0 } all) yield break;
        foreach ((string name, byte[] value) in all)
        {
            (int index, string suffix) = SplitXattrName(name);
            byte[] encoded = PosixName.Encode(suffix);
            if (index == 0 || encoded.Length > 255 || value.Length > 65535)
            {
                if (warn)
                    Warnings.Add(index == 0
                        ? $"'{item.Node.Path}': the extended attribute '{name}' was dropped; EROFS holds only user.*, trusted.*, security.* attributes and POSIX ACLs."
                        : $"'{item.Node.Path}': the extended attribute '{name}' was dropped; its name or value is too long for EROFS.");
                continue;
            }
            yield return (new XattrKey(index, suffix, Hash(value), value.Length), XattrEntry(index, encoded, value));
        }
    }

    /// <summary>Decides which attributes are stored once for all inodes that carry them; returns their offsets in units of 4 bytes.</summary>
    private Dictionary<XattrKey, int> PlanXattrs(List<Item> items, out byte[] area)
    {
        var uses = new Dictionary<XattrKey, (int Count, byte[] Entry)>();
        var order = new List<XattrKey>();
        foreach (Item item in items)
        {
            foreach ((XattrKey key, byte[] entry) in XattrsOf(item, warn: true))
            {
                if (uses.TryGetValue(key, out var u)) uses[key] = (u.Count + 1, u.Entry);
                else
                {
                    uses.Add(key, (1, entry));
                    order.Add(key);
                }
            }
        }
        var ids = new Dictionary<XattrKey, int>();
        var w = new ByteWriter(1024);
        if (_opt.ShareXattrs)
        {
            foreach (XattrKey key in order)
            {
                (int count, byte[] entry) = uses[key];
                // Sharing costs the entry once and four bytes per inode.
                if (count < 2 || (long)entry.Length * (count - 1) <= 4L * count) continue;
                ids.Add(key, w.Length / 4);
                w.Bytes(entry);
            }
        }
        area = w.ToArray();
        return ids;
    }

    private byte[] BuildXattrs(Item item, Dictionary<XattrKey, int> sharedIds)
    {
        var shared = new List<int>();
        var inline = new ByteWriter(64);
        foreach ((XattrKey key, byte[] entry) in XattrsOf(item, warn: false))
        {
            if (shared.Count < 255 && sharedIds.TryGetValue(key, out int id)) shared.Add(id);
            else inline.Bytes(entry);
        }
        if (shared.Count == 0 && inline.Length == 0) return Array.Empty<byte>();
        int size = XattrHeaderSize + 4 * shared.Count + inline.Length;
        // The size is stored as a 16-bit count of 4-byte units.
        if ((size - XattrHeaderSize) / 4 + 1 > 0xFFFF)
        {
            Warnings.Add($"'{item.Node.Path}': the extended attributes take more than 256 KiB and were dropped.");
            return Array.Empty<byte>();
        }
        var w = new ByteWriter(size);
        w.U32(0);                                   // name filter: not used
        w.U8(shared.Count);
        w.Zeros(7);
        foreach (int id in shared) w.U32((uint)id);
        w.Bytes(inline.Span);
        return w.ToArray();
    }

    // -------------------------------------------------------------- file data

    private void Enqueue(Pending p)
    {
        while (_queue.Count > 0 && ((p.Task is not null && _tasks >= _maxTasks) || _pendingBytes > PendingBytesLimit)) Drain();
        _queue.Enqueue(p);
        _pendingBytes += p.Bytes;
        if (p.Task is not null) _tasks++;
    }

    private void DrainAll()
    {
        while (_queue.Count > 0) Drain();
    }

    /// <summary>Completes the oldest queued item and writes its clusters: the order in the image is the order of submission.</summary>
    private void Drain()
    {
        Pending p = _queue.Dequeue();
        _pendingBytes -= p.Bytes;
        if (p.Task is not null) _tasks--;
        FileLayout layout = p.Layout!;
        if (p.Start)
        {
            layout.StartBlock = _block;
            return;
        }
        if (p.End)
        {
            FinishCompressed(p.Item!, layout, p.Content);
            if (p.Content is not null) ArrayPool<byte>.Shared.Return(p.Content);
            return;
        }
        SegmentResult r;
        try
        {
            r = p.Task!.GetAwaiter().GetResult();
        }
        finally
        {
            if (p.Buffer is not null) ArrayPool<byte>.Shared.Return(p.Buffer);
        }
        _out.Write(r.Data, 0, r.Length);
        ArrayPool<byte>.Shared.Return(r.Data);
        _block += r.Length >> BlkBits;
        layout.Blocks += r.Length >> BlkBits;
        layout.Extents!.AddRange(r.Extents);
    }

    private void WriteFile(Item item)
    {
        FsInode inode = item.Inode;
        long size = inode.Size;
        var layout = new FileLayout { Size = size };
        item.File = layout;
        if (size == 0 || inode.Data is null) return;
        Report("data", null);

        // A file smaller than a block is nothing but a tail; it is read again when its inode is written.
        if (size < Blk && CanInline(item, (int)size))
        {
            layout.TailInline = true;
            _bytesDone += size;
            return;
        }

        bool compress = _compress && size > Blk;
        if (size <= InMemoryFileLimit)
        {
            byte[] content = ArrayPool<byte>.Shared.Rent((int)size);
            bool queued = false;
            try
            {
                using (Stream s = inode.Data.OpenRead()) s.ReadExactly(content, 0, (int)size);
                if (_opt.Deduplicate)
                {
                    var key = new ContentKey(size, Hash(content.AsSpan(0, (int)size)));
                    if (_dedup.TryGetValue(key, out FileLayout? same))
                    {
                        if (CanShare(item, size))
                        {
                            item.File = same;
                            _bytesDone += size;
                            return;
                        }
                    }
                    else _dedup[key] = layout;
                }
                if (!compress)
                {
                    DrainAll();
                    WriteFlat(item, layout, content.AsSpan(0, (int)size));
                    _bytesDone += size;
                    return;
                }
                layout.Compressed = true;
                layout.Extents = new List<ExtentRecord>();
                Enqueue(new Pending { Layout = layout, Start = true });
                for (int offset = 0; offset < size; offset += _segmentSize)
                {
                    int start = offset, length = (int)Math.Min(_segmentSize, size - offset);
                    Enqueue(new Pending { Layout = layout, Task = StartCompression(content, start, length) });
                }
                // The content stays with the queue: the file may still turn out not to be worth compressing.
                Enqueue(new Pending { Layout = layout, End = true, Item = item, Content = content, Bytes = size });
                queued = true;
                _bytesDone += size;
            }
            finally
            {
                if (!queued) ArrayPool<byte>.Shared.Return(content);
            }
            return;
        }

        // A large file: hashed first (to find duplicates), then streamed.
        if (_opt.Deduplicate)
        {
            ContentKey key;
            using (Stream s = inode.Data.OpenRead())
            {
                byte[] h = SHA256.HashData(s);
                key = new ContentKey(size, new UInt128(BitConverter.ToUInt64(h), BitConverter.ToUInt64(h.AsSpan(8))));
            }
            if (_dedup.TryGetValue(key, out FileLayout? same))
            {
                if (CanShare(item, size))
                {
                    item.File = same;
                    _bytesDone += size;
                    return;
                }
            }
            else _dedup[key] = layout;
        }
        using Stream data = inode.Data.OpenRead();
        if (!compress)
        {
            DrainAll();
            WriteFlatStream(item, layout, data);
            return;
        }
        layout.Compressed = true;
        layout.Extents = new List<ExtentRecord>();
        Enqueue(new Pending { Layout = layout, Start = true });
        for (long left = size; left > 0;)
        {
            _ct.ThrowIfCancellationRequested();
            int length = (int)Math.Min(_segmentSize, left);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                data.ReadExactly(buffer, 0, length);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw;
            }
            Enqueue(new Pending { Layout = layout, Buffer = buffer, Bytes = length, Task = StartCompression(buffer, 0, length) });
            left -= length;
            _bytesDone += length;
            Report("data", null);
        }
        Enqueue(new Pending { Layout = layout, End = true, Item = item });
    }

    /// <summary>The tail of a file may follow its inode when both fit into one block.</summary>
    private bool CanInline(Item item, int tail) =>
        _opt.InlineData && tail > 0 && item.InodeSize + item.Xattrs.Length + tail <= Blk;

    /// <summary>
    /// A duplicate uses the blocks of the first copy. When that copy keeps its tail next to its inode, so must
    /// the duplicate - which it can only when the tail fits next to its own inode as well.
    /// </summary>
    private bool CanShare(Item item, long size) =>
        !_opt.InlineData || (size & (Blk - 1)) == 0 || CanInline(item, (int)(size & (Blk - 1)));

    private void WriteFlat(Item item, FileLayout layout, ReadOnlySpan<byte> content)
    {
        layout.Compressed = false;
        layout.Extents = null;
        layout.StartBlock = _block;
        int tail = content.Length & (Blk - 1);
        if (CanInline(item, tail))
        {
            layout.TailInline = true;
            ReadOnlySpan<byte> rest = content[^tail..];
            content = content[..^tail];
            if (content.Length > 0) layout.Tail = KeepTail(rest);
        }
        WriteBlocks(content);
        layout.Blocks = _block - layout.StartBlock;
    }

    private void WriteFlatStream(Item item, FileLayout layout, Stream data)
    {
        layout.StartBlock = _block;
        long left = layout.Size;
        int tail = (int)(left & (Blk - 1));
        bool inline = CanInline(item, tail);
        if (inline) left -= tail;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            while (left > 0)
            {
                _ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(1 << 20, left);
                data.ReadExactly(buffer, 0, n);
                WriteBlocks(buffer.AsSpan(0, n));
                left -= n;
                _bytesDone += n;
                Report("data", null);
            }
            if (inline)
            {
                data.ReadExactly(buffer, 0, tail);
                layout.TailInline = true;
                layout.Tail = KeepTail(buffer.AsSpan(0, tail));
                _bytesDone += tail;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        layout.Blocks = _block - layout.StartBlock;
    }

    /// <summary>Keeps the tail of a file until its inode is written: in memory, or in a temporary file once that gets large.</summary>
    private IFileData KeepTail(ReadOnlySpan<byte> tail)
    {
        byte[] copy = tail.ToArray();
        if (_tailMemory + copy.Length <= TailMemoryLimit)
        {
            _tailMemory += copy.Length;
            return new MemoryFileData(copy);
        }
        _spool ??= new TempSpool(_tempDirectory);
        return _spool.Append(new MemoryStream(copy, writable: false), copy.Length);
    }

    /// <summary>Compresses a piece of a file on a worker as soon as one of the allowed workers is free.</summary>
    private Task<SegmentResult> StartCompression(byte[] buffer, int offset, int length) => Task.Run(async () =>
    {
        await _workers.WaitAsync(_ct).ConfigureAwait(false);
        try
        {
            return CompressSegment(buffer, offset, length);
        }
        finally
        {
            _workers.Release();
        }
    });

    /// <summary>
    /// Compresses a piece of a file into clusters. A cluster takes as much data as fits into it; data that
    /// does not shrink is stored as plain blocks.
    /// </summary>
    private SegmentResult CompressSegment(byte[] buffer, int offset, int length)
    {
        _ct.ThrowIfCancellationRequested();
        if (!_codecs.TryTake(out Codec? codec)) codec = new Codec(_opt, _dictionary);
        byte[] output = ArrayPool<byte>.Shared.Rent(length + Blk);
        var extents = new List<ExtentRecord>(length / _pclusterSize / 2 + 4);
        try
        {
            int pos = 0, op = 0;
            // How much input a cluster is offered at first; more follows when all of it fitted.
            // DEFLATE and Zstandard search the length themselves and get everything at once.
            int firstWindow = codec.Searches ? MaxExtentBytes : _pclusterSize * (_algorithm == AlgLzma ? 8 : 4);
            while (pos < length)
            {
                int remaining = length - pos;
                int rawBlocks = 1;
                if (remaining > Blk)
                {
                    int limit = Math.Min(remaining, MaxExtentBytes);
                    int window = Math.Min(limit, firstWindow);
                    int size, consumed;
                    while (true)
                    {
                        size = codec.Compress(buffer.AsSpan(offset + pos, window), out consumed);
                        if (size <= 0 || consumed < window || window == limit) break;
                        // Everything fitted: offer as much as would fill the cluster at this ratio, and some more.
                        long wanted = (long)window * _pclusterSize / size * 5 / 4;
                        window = (int)Math.Min(limit, Math.Clamp(wanted, window * 2L, window * 16L));
                    }
                    int blocks = (size + Blk - 1) >> BlkBits;
                    if (size > 0 && blocks << BlkBits < consumed)
                    {
                        // The data sits at the end of the cluster, behind zeros.
                        int area = blocks << BlkBits, pad = area - size;
                        output.AsSpan(op, pad).Clear();
                        codec.Scratch.AsSpan(0, size).CopyTo(output.AsSpan(op + pad));
                        op += area;
                        extents.Add(new ExtentRecord(consumed, blocks, false));
                        pos += consumed;
                        continue;
                    }
                    // What was tried does not shrink: all of it is stored as it is.
                    rawBlocks = Math.Max(1, Math.Min(consumed, remaining) >> BlkBits);
                }
                for (int i = 0; i < rawBlocks && pos < length; i++)
                {
                    int n = Math.Min(Blk, length - pos);
                    buffer.AsSpan(offset + pos, n).CopyTo(output.AsSpan(op));
                    output.AsSpan(op + n, Blk - n).Clear();
                    op += Blk;
                    extents.Add(new ExtentRecord(n, 1, true));
                    pos += n;
                }
            }
            return new SegmentResult(output, op, extents.ToArray());
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(output);
            throw;
        }
        finally
        {
            _codecs.Add(codec);
        }
    }

    /// <summary>All clusters of a file are written: keep them, or store the file as it is when they saved nothing.</summary>
    private void FinishCompressed(Item item, FileLayout layout, byte[]? content)
    {
        long clusters = (layout.Size + Blk - 1) >> BlkBits;
        bool gain = (layout.Blocks << BlkBits) + 16 + clusters * 8 < layout.Size;
        if (gain || content is null) return;
        // Go back to where the file starts and write it uncompressed.
        _block = layout.StartBlock;
        _out.Position = _base + (_block << BlkBits);
        WriteFlat(item, layout, content.AsSpan(0, (int)layout.Size));
    }

    // ---------------------------------------------------------------- indexes

    private struct ClusterEntry
    {
        public int Type;
        public int Offset;                          // of a head: where its data starts in the cluster
        public uint Block;                          // of a head
        public int Delta0, Delta1;                  // of the others: distance to the head, and to the next head
    }

    /// <summary>One index entry per 4 KiB of the file, as mkfs.erofs derives them from the clusters.</summary>
    private ClusterEntry[] ClusterEntries(FileLayout layout)
    {
        var entries = new ClusterEntry[(layout.Size + Blk - 1) >> BlkBits];
        int at = 0, offset = 0;
        long block = layout.StartBlock;
        foreach (ExtentRecord e in layout.Extents!)
        {
            int type = e.Raw ? ClusterPlain : ClusterHead1;
            int count = e.Length;
            int d0 = 0, d1 = (offset + count) >> BlkBits;
            if (d1 == 0)
            {
                // The last piece of the file, inside one cluster.
                entries[at++] = new ClusterEntry { Type = type, Offset = offset, Block = Address(block) };
                offset = 0;
            }
            else
            {
                do
                {
                    if (d0 == 0) entries[at++] = new ClusterEntry { Type = type, Offset = offset, Block = Address(block) };
                    // The first cluster behind the head holds the size of the compressed cluster instead of the distance.
                    else if (d0 == 1 && _big) entries[at++] = new ClusterEntry { Type = ClusterNonHead, Delta0 = e.Blocks | D0BlockCount, Delta1 = d1 };
                    else entries[at++] = new ClusterEntry { Type = ClusterNonHead, Delta0 = Math.Min(d0, D0BlockCount - 1), Delta1 = d1 };
                    count -= Blk - offset;
                    offset = 0;
                    d0++;
                    d1--;
                }
                while (count >= Blk);
                offset = count;
            }
            block += e.Blocks;
        }
        // The file ends inside a cluster that has no entry yet: a head without data marks the end.
        if (offset != 0) entries[at++] = new ClusterEntry { Type = ClusterPlain, Offset = offset };
        if (at != entries.Length) throw new InvalidOperationException("EROFS writer: the cluster index does not match the file size.");
        return entries;
    }

    /// <summary>The map header and the compact cluster index of a compressed file.</summary>
    /// <param name="headSize">Size of the inode and its extended attributes: the index is aligned to its position.</param>
    private byte[] BuildIndex(FileLayout layout, int headSize)
    {
        ClusterEntry[] entries = ClusterEntries(layout);
        int total = entries.Length;
        int indexStart = ((headSize + 7) & ~7) + 8;
        // Packs of two entries in eight bytes at both ends, packs of sixteen in 32 bytes where those are aligned.
        int initial = (32 - indexStart % 32) / 4 % 8;
        int twoByte = 0;
        if (initial > total) initial = total;
        else twoByte = (total - initial) / 16 * 16;
        int last = total - initial - twoByte;
        byte[] index = new byte[8 + (initial + 1) / 2 * 8 + twoByte * 2 + (last + 1) / 2 * 8];

        int advise = AdviseCompacted2B | (_big ? AdviseBigPcluster1 | AdviseBigPcluster2 : 0);
        Le.W16(index, 4, advise);
        index[6] = (byte)_algorithm;
        index[7] = 0;                               // a cluster of the index is one block

        // The pack keeps the block of its first head; the blocks of the others follow from the sizes before them.
        uint block = Address(layout.StartBlock);
        bool pendingHead = false;                   // a head whose cluster has not been counted yet
        if (!_big)
        {
            block--;
            pendingHead = true;
        }
        int at = 0, pos = 8;
        void Pack(int perPack, int packBytes, int count)
        {
            int bits = (packBytes * 8 - 32) / perPack;
            for (; count > 0; count -= perPack, pos += packBytes)
            {
                uint packBlock = block;
                bool update = _big;
                int bit = 0;
                for (int i = 0; i < perPack; i++, bit += bits)
                {
                    ClusterEntry e = i < count ? entries[at++] : default;
                    int value;
                    if (e.Type == ClusterNonHead)
                    {
                        if ((e.Delta0 & D0BlockCount) != 0)
                        {
                            value = e.Delta0;
                            block += (uint)(e.Delta0 & ~D0BlockCount);
                            pendingHead = false;
                        }
                        // The last entry of a pack holds the distance to the next head.
                        else value = i + 1 == perPack ? Math.Min(e.Delta1, Blk - 1) : e.Delta0;
                    }
                    else
                    {
                        value = e.Offset;
                        if (pendingHead)
                        {
                            block++;
                            if (update) packBlock = block;
                        }
                        pendingHead = true;
                        update = false;
                    }
                    uint v = (uint)((e.Type << BlkBits) | value);
                    int b = pos + bit / 8, shift = bit & 7;
                    index[b] |= (byte)(v << shift);
                    index[b + 1] = (byte)(v >> (8 - shift));
                    index[b + 2] = (byte)(v >> (16 - shift));
                }
                Le.W32(index, pos + packBytes - 4, packBlock);
            }
        }
        Pack(2, 8, initial);
        Pack(16, 32, twoByte);
        Pack(2, 8, last);
        return index;
    }

    // ------------------------------------------------------------- directories

    /// <summary>
    /// The size of a directory: its entries, "." and ".." included, sorted by name and packed into blocks -
    /// a table of 12-byte entries followed by the names. The last block is as long as its content.
    /// </summary>
    private static long DirectorySize(Item dir, out int tail)
    {
        long blocks = 0;
        int used = 0;
        foreach (byte[] name in DirectoryNames(dir))
        {
            int need = DirentSize + name.Length;
            if (used + need > Blk)
            {
                blocks++;
                used = 0;
            }
            used += need;
        }
        if (used == Blk)
        {
            blocks++;
            used = 0;
        }
        tail = used;
        return (blocks << BlkBits) + used;
    }

    private static readonly byte[] Dot = { (byte)'.' }, DotDot = { (byte)'.', (byte)'.' };

    private static IEnumerable<byte[]> DirectoryNames(Item dir) => DirectoryEntries(dir).Select(e => e.Name);

    private static List<(byte[] Name, Item Target)> DirectoryEntries(Item dir)
    {
        var list = new List<(byte[] Name, Item Target)>((dir.Entries?.Count ?? 0) + 2)
        {
            (Dot, dir),
            (DotDot, dir.Parent ?? dir),
        };
        if (dir.Entries is not null) list.AddRange(dir.Entries);
        list.Sort(static (a, b) => PosixName.CompareBytes(a.Name, b.Name));
        return list;
    }

    private static byte[] DirectoryData(Item dir)
    {
        var output = new MemoryStream();
        byte[] block = new byte[Blk];
        List<(byte[] Name, Item Target)> entries = DirectoryEntries(dir);
        for (int first = 0; first < entries.Count;)
        {
            int count = 0, used = 0;
            while (first + count < entries.Count && used + DirentSize + entries[first + count].Name.Length <= Blk)
            {
                used += DirentSize + entries[first + count].Name.Length;
                count++;
            }
            Array.Clear(block);
            int nameAt = count * DirentSize;
            for (int i = 0; i < count; i++)
            {
                (byte[] name, Item target) = entries[first + i];
                Le.W64(block, i * DirentSize, target.Nid);
                Le.W16(block, i * DirentSize + 8, nameAt);
                block[i * DirentSize + 10] = (byte)FileType(target.Inode.Type);
                name.CopyTo(block, nameAt);
                nameAt += name.Length;
            }
            first += count;
            // Every block but the last is a whole one.
            output.Write(block, 0, first < entries.Count ? Blk : used);
        }
        return output.ToArray();
    }

    // ------------------------------------------------------------------ inodes

    /// <summary>Decides the data layout of an inode and what follows it in the metadata area.</summary>
    private void PrepareInode(Item item)
    {
        FsInode inode = item.Inode;
        int head = item.InodeSize + item.Xattrs.Length;
        switch (inode.Type)
        {
            case FsNodeType.File:
            {
                FileLayout f = item.File!;
                item.Size = f.Size;
                if (f.Compressed)
                {
                    item.Layout = LayoutCompressedCompact;
                    item.Union = Address(f.Blocks);
                    item.Index = BuildIndex(f, head);
                }
                else
                {
                    item.Layout = f.TailInline ? LayoutFlatInline : LayoutFlatPlain;
                    item.Union = f.Blocks > 0 ? Address(f.StartBlock) : NullAddr;
                    item.TailSize = f.TailInline ? (int)(f.Size & (Blk - 1)) : 0;
                    if (f.TailInline && head + item.TailSize > Blk)
                        throw new InvalidOperationException("EROFS writer: an inline tail does not fit next to its inode.");
                }
                break;
            }
            case FsNodeType.Directory:
            case FsNodeType.Symlink:
            {
                int tail;
                if (inode.Type == FsNodeType.Directory) item.Size = DirectorySize(item, out tail);
                else
                {
                    item.Size = PosixName.Encode(inode.LinkTarget ?? "").Length;
                    tail = (int)(item.Size & (Blk - 1));
                }
                bool inline = tail > 0 && head + tail <= Blk;
                item.Layout = inline ? LayoutFlatInline : LayoutFlatPlain;
                item.TailSize = inline ? tail : 0;
                item.LateBlocks = inline ? item.Size >> BlkBits : (item.Size + Blk - 1) >> BlkBits;
                item.Union = NullAddr;              // replaced by the address of the blocks, when there are any
                break;
            }
            case FsNodeType.CharDevice or FsNodeType.BlockDevice:
                item.Union = EncodeDev(inode.DevMajor, inode.DevMinor);
                break;
        }
    }

    /// <summary>
    /// Gives every inode its place in the metadata area. An inode starts at a multiple of 32 bytes; it and its
    /// tail stay inside one block when they fit into one, and space left at the end of a block is offered to
    /// the inodes that follow. Returns the size of the area.
    /// </summary>
    private static long PlaceInodes(List<Item> items)
    {
        // Inode number 0 is avoided: tools take a directory entry with inode 0 for an empty one.
        long cursor = 1 << SlotBits;
        var gaps = new List<(long Offset, int Length)>();
        foreach (Item item in items)
        {
            int size = item.HeadSize + item.TailSize;
            int slots = (size + 31) & ~31;
            bool placed = false;
            if (!ReferenceEquals(item, items[0]) && slots <= Blk)
            {
                for (int g = 0; g < gaps.Count; g++)
                {
                    (long offset, int length) = gaps[g];
                    if (length < size) continue;
                    item.MetaOffset = offset;
                    if (length - slots >= 32) gaps[g] = (offset + slots, length - slots);
                    else gaps.RemoveAt(g);
                    placed = true;
                    break;
                }
            }
            if (placed) continue;
            int room = Blk - (int)(cursor & (Blk - 1));
            // The head of a large inode (a long cluster index) may run over several blocks; a tail must not leave its block.
            bool fits = size <= room || (item.TailSize == 0 && room >= item.InodeSize && size > Blk);
            if (!fits)
            {
                if (room >= 32)
                {
                    gaps.Add((cursor, room));
                    if (gaps.Count > 64) gaps.RemoveAt(0);
                }
                cursor += room;
            }
            item.MetaOffset = cursor;
            cursor += slots;
        }
        return cursor;
    }

    private void WriteInodes(List<Item> items, long metaBytes)
    {
        var order = new List<Item>(items);
        order.Sort(static (a, b) => a.MetaOffset.CompareTo(b.MetaOffset));
        long written = 0;
        var w = new ByteWriter(4096);

        void Pad(long upTo)
        {
            for (long gap = upTo - written; gap > 0;)
            {
                int n = (int)Math.Min(gap, Blk);
                _out.Write(Zeros, 0, n);
                gap -= n;
            }
            written = upTo;
        }

        foreach (Item item in order)
        {
            _ct.ThrowIfCancellationRequested();
            Pad(item.MetaOffset);
            FsInode inode = item.Inode;
            int mode = UnixMode.TypeBits(inode.Type) | (inode.Type == FsNodeType.Symlink ? 0x1FF : inode.Mode & 0xFFF);
            uint uid = _opt.AllRoot ? 0 : inode.Uid, gid = _opt.AllRoot ? 0 : inode.Gid;
            long links = inode.Type == FsNodeType.Directory ? 2 + (item.Entries?.Count(e => e.Target.Inode.Type == FsNodeType.Directory) ?? 0) : inode.LinkCount;
            int xattrCount = item.Xattrs.Length == 0 ? 0 : (item.Xattrs.Length - XattrHeaderSize) / 4 + 1;
            w.Clear();
            w.U16((item.Extended ? 1 : 0) | (item.Layout << 1));
            w.U16(xattrCount);
            w.U16(mode);
            if (item.Extended)
            {
                w.U16(0);
                w.U64((ulong)item.Size);
                w.U32(item.Union);
                w.U32(item.Number);
                w.U32(uid);
                w.U32(gid);
                w.U64((ulong)inode.MTime);
                w.U32(0);
                w.U32((uint)Math.Min(links, uint.MaxValue));
                w.Zeros(16);
            }
            else
            {
                w.U16((int)links);
                w.U32((uint)item.Size);
                w.U32(0);
                w.U32(item.Union);
                w.U32(item.Number);
                w.U16((int)uid);
                w.U16((int)gid);
                w.U32(0);
            }
            w.Bytes(item.Xattrs);
            if (item.Index is not null)
            {
                w.Zeros(((w.Length + 7) & ~7) - w.Length);
                w.Bytes(item.Index);
            }
            if (w.Length != item.HeadSize) throw new InvalidOperationException("EROFS writer: an inode has an unexpected size.");
            _out.Write(w.Span);
            written += w.Length;

            if (item.TailSize > 0)
            {
                byte[] tail = TailOf(item);
                if (tail.Length != item.TailSize) throw new InvalidOperationException("EROFS writer: an inline tail has an unexpected size.");
                _out.Write(tail);
                written += tail.Length;
            }
        }
        Pad((metaBytes + Blk - 1) & ~(long)(Blk - 1));
    }

    /// <summary>The bytes that follow an inode: the end of a file, of a directory or the target of a link.</summary>
    private static byte[] TailOf(Item item)
    {
        switch (item.Inode.Type)
        {
            case FsNodeType.Directory:
                return DirectoryData(item)[^item.TailSize..];
            case FsNodeType.Symlink:
                return PosixName.Encode(item.Inode.LinkTarget ?? "")[^item.TailSize..];
            default:
            {
                FileLayout f = item.File!;
                byte[] tail = new byte[item.TailSize];
                using Stream s = (f.Tail ?? item.Inode.Data!).OpenRead();
                s.ReadExactly(tail);
                return tail;
            }
        }
    }
}
