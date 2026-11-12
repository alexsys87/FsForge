using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using FsForge.Core.Compression;
using static FsForge.Core.SquashFs.SquashFsFormat;

namespace FsForge.Core.SquashFs;

/// <summary>Builds a SquashFS 4.0 image from a tree.</summary>
public sealed class SquashFsWriter
{
    private const int InMemoryFileLimit = 32 << 20;

    private readonly FsTree _tree;
    private readonly Stream _out;
    private readonly SquashFsOptions _opt;
    private readonly IProgress<ProgressInfo>? _progress;
    private readonly CancellationToken _ct;
    private readonly long _base;
    private readonly int _blockSize;
    private readonly ConcurrentBag<IBlockCodec> _codecs = new();
    private readonly IBlockCodec _metaCodec;

    private long _pos;                              // current write offset relative to the image start
    private long _bytesDone, _bytesTotal;

    // Data phase.
    private readonly Queue<Pending> _queue = new();
    private readonly int _maxPending;
    private readonly Dictionary<FsInode, FileLayout> _layouts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ContentKey, FileLayout> _dedup = new();
    private readonly List<(long Start, uint Size)> _fragments = new();
    private byte[] _fragBuf;
    private int _fragFill;

    public List<string> Warnings { get; } = new();

    private sealed class FileLayout
    {
        public long StartBlock;
        public uint[] BlockSizes = Array.Empty<uint>();
        public uint Fragment = InvalidFragment;
        public uint FragOffset;
        public long Sparse;
        public long Size;
    }

    private readonly record struct ContentKey(long Length, UInt128 Hash);

    private sealed class Pending
    {
        public Task<Compressed>? Task;
        public FileLayout? Layout;
        public int BlockIndex = -1;         // >= 0: data block of Layout
        public int FragmentIndex = -1;      // >= 0: fragment block
        public bool FileStart;              // marker: record the start position of Layout
    }

    private readonly record struct Compressed(byte[] Buffer, int Length, bool IsCompressed);

    private SquashFsWriter(FsTree tree, Stream output, SquashFsOptions options, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        options.Validate();
        _tree = tree;
        _out = output;
        _opt = options;
        _progress = progress;
        _ct = ct;
        _base = output.Position;
        _blockSize = options.BlockSize;
        _fragBuf = new byte[_blockSize];
        int threads = options.Threads > 0 ? options.Threads : Environment.ProcessorCount;
        _maxPending = Math.Max(2, threads * 2);
        _metaCodec = CreateMetadataEncoder(options);
    }

    /// <summary>Writes the image at the current position of <paramref name="output"/> (must be seekable).</summary>
    public static IReadOnlyList<string> Write(FsTree tree, Stream output, SquashFsOptions options,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        if (!output.CanSeek) throw new ArgumentException("The output stream must be seekable.", nameof(output));
        var w = new SquashFsWriter(tree, output, options, progress, ct);
        try
        {
            w.Run();
        }
        finally
        {
            while (w._codecs.TryTake(out IBlockCodec? c)) (c as IDisposable)?.Dispose();
            (w._metaCodec as IDisposable)?.Dispose();
        }
        return w.Warnings;
    }

    // ---------------------------------------------------------------- driver

    private void Run()
    {
        // Reserve room for the superblock and the optional compressor options.
        byte[]? compOptions = BuildCompressorOptions();
        _pos = SuperblockSize;
        _out.Position = _base;
        _out.Write(new byte[SuperblockSize]);
        if (compOptions is not null)
        {
            Span<byte> h = stackalloc byte[2];
            Le.W16(h, 0, compOptions.Length | 0x8000);
            WriteRaw(h);
            WriteRaw(compOptions);
        }

        // ---- data blocks and fragments
        var files = new List<FsInode>();
        var seen = new HashSet<FsInode>(ReferenceEqualityComparer.Instance);
        foreach (FsNode n in FsTree.Walk(_tree.Root))
        {
            if (n.Type != FsNodeType.File || !seen.Add(n.Inode)) continue;
            files.Add(n.Inode);
            _bytesTotal += n.Inode.Size;
        }
        foreach (FsInode inode in files)
        {
            _ct.ThrowIfCancellationRequested();
            _layouts[inode] = WriteFile(inode);
        }
        FlushFragment();
        while (_queue.Count > 0) Drain();
        Report("data", null);

        // ---- metadata
        var meta = new MetadataBuilder(this);
        meta.Build();

        long inodeTableStart = _pos;
        WriteRaw(meta.InodeTable);
        long dirTableStart = _pos;
        WriteRaw(meta.DirTable);

        long fragTableStart = WriteFragmentTable();
        long exportTableStart = _opt.Exportable ? WriteIndexedTable(meta.ExportTable) : unchecked((long)InvalidBlock);
        long idTableStart = WriteIndexedTable(meta.IdTable);
        long xattrTableStart = meta.WriteXattrTables();
        long bytesUsed = _pos;

        if (_opt.Pad4K && (_pos & 4095) != 0) WriteRaw(new byte[4096 - (_pos & 4095)]);

        // ---- superblock
        int flags = 0;
        if (_opt.NoFragments) flags |= FlagNoFragments;
        if (_opt.Deduplicate) flags |= FlagDuplicates;
        if (_opt.Exportable) flags |= FlagExportable;
        if (!_opt.StoreXattrs) flags |= FlagNoXattrs;
        if (compOptions is not null) flags |= FlagCompressorOptions;

        byte[] sb = new byte[SuperblockSize];
        Le.W32(sb, 0, Magic);
        Le.W32(sb, 4, (uint)meta.InodeCount);
        Le.W32(sb, 8, (uint)(_opt.MkfsTime ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        Le.W32(sb, 12, (uint)_blockSize);
        Le.W32(sb, 16, (uint)_fragments.Count);
        Le.W16(sb, 20, (int)_opt.Compression);
        Le.W16(sb, 22, System.Numerics.BitOperations.Log2((uint)_blockSize));
        Le.W16(sb, 24, flags);
        Le.W16(sb, 26, meta.IdCount);
        Le.W16(sb, 28, 4);
        Le.W16(sb, 30, 0);
        Le.W64(sb, 32, (ulong)meta.RootRef);
        Le.W64(sb, 40, (ulong)bytesUsed);
        Le.W64(sb, 48, (ulong)idTableStart);
        Le.W64(sb, 56, (ulong)xattrTableStart);
        Le.W64(sb, 64, (ulong)inodeTableStart);
        Le.W64(sb, 72, (ulong)dirTableStart);
        Le.W64(sb, 80, (ulong)fragTableStart);
        Le.W64(sb, 88, (ulong)exportTableStart);
        long end = _out.Position;
        _out.Position = _base;
        _out.Write(sb);
        _out.Position = end;
        _out.Flush();
        Report("done", null);
    }

    private byte[]? BuildCompressorOptions()
    {
        var w = new ByteWriter(16);
        switch (_opt.Compression)
        {
            case SquashCompression.Gzip:
                if (_opt.GzipLevel == 9) return null;
                w.U32((uint)_opt.GzipLevel);
                w.U16(15);
                w.U16(0);
                break;
            case SquashCompression.Xz:
            {
                int dict = _opt.XzDictionarySize > 0 ? _opt.XzDictionarySize : _blockSize;
                int filters = 0;
                foreach (BcjFilter f in _opt.XzFilters)
                    if (Bcj.IsSupported(f)) filters |= XzFilterFlag(f);
                if (dict == _blockSize && filters == 0) return null;
                w.U32((uint)dict);
                w.U32((uint)filters);
                break;
            }
            case SquashCompression.Lz4:
                w.U32(1);
                w.U32(0);
                break;
            case SquashCompression.Zstd:
                if (_opt.ZstdLevel == 15) return null;
                w.U32((uint)_opt.ZstdLevel);
                break;
            default:
                return null;
        }
        return w.ToArray();
    }

    private void WriteRaw(ReadOnlySpan<byte> data)
    {
        _out.Write(data);
        _pos += data.Length;
    }

    private void Report(string stage, string? item) =>
        _progress?.Report(new ProgressInfo(stage, _bytesDone, _bytesTotal, item));

    // --------------------------------------------------------------- codecs

    private IBlockCodec RentCodec() => _codecs.TryTake(out IBlockCodec? c) ? c : CreateEncoder(_opt);

    /// <summary>Compresses a block that the caller rented from the shared pool; ownership moves to the task.</summary>
    private Task<Compressed> CompressAsync(byte[] block, int length)
    {
        return Task.Run(() =>
        {
            IBlockCodec codec = RentCodec();
            byte[] dst = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                // A block is stored compressed only when that actually saves space.
                int n = length > 1 ? codec.Compress(block.AsSpan(0, length), dst.AsSpan(0, length - 1)) : -1;
                if (n > 0 && n < length)
                {
                    ArrayPool<byte>.Shared.Return(block);
                    return new Compressed(dst, n, true);
                }
                ArrayPool<byte>.Shared.Return(dst);
                return new Compressed(block, length, false);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(dst);
                throw;
            }
            finally
            {
                _codecs.Add(codec);
            }
        });
    }

    private void Enqueue(Pending p)
    {
        while (_queue.Count >= _maxPending) Drain();
        _queue.Enqueue(p);
    }

    /// <summary>Completes the oldest queued item and writes its block, keeping the on-disk order deterministic.</summary>
    private void Drain()
    {
        Pending p = _queue.Dequeue();
        if (p.FileStart)
        {
            p.Layout!.StartBlock = _pos;
            return;
        }
        Compressed c;
        try
        {
            c = p.Task!.GetAwaiter().GetResult();
        }
        catch
        {
            // Let the remaining workers finish before the error propagates.
            foreach (Pending rest in _queue)
            {
                try { rest.Task?.Wait(); } catch { /* ignored */ }
            }
            _queue.Clear();
            throw;
        }
        uint word = (uint)c.Length | (c.IsCompressed ? 0 : BlockUncompressed);
        if (p.FragmentIndex >= 0) _fragments[p.FragmentIndex] = (_pos, word);
        else p.Layout!.BlockSizes[p.BlockIndex] = word;
        WriteRaw(c.Buffer.AsSpan(0, c.Length));
        ArrayPool<byte>.Shared.Return(c.Buffer);
    }

    // ----------------------------------------------------------- file data

    private static bool IsAllZero(ReadOnlySpan<byte> data) => data.IndexOfAnyExcept((byte)0) < 0;

    private FileLayout WriteFile(FsInode inode)
    {
        long size = inode.Size;
        var layout = new FileLayout { Size = size };
        if (size == 0 || inode.Data is null) return layout;
        Report("data", null);

        bool useFragment = !_opt.NoFragments && size < _blockSize;
        if (size <= InMemoryFileLimit)
        {
            byte[] content = ArrayPool<byte>.Shared.Rent((int)size);
            try
            {
                using (Stream s = inode.Data.OpenRead()) s.ReadExactly(content, 0, (int)size);
                ReadOnlySpan<byte> data = content.AsSpan(0, (int)size);
                ContentKey key = default;
                if (_opt.Deduplicate)
                {
                    key = new ContentKey(size, Hash(data));
                    if (_dedup.TryGetValue(key, out FileLayout? same))
                    {
                        _bytesDone += size;
                        return same;
                    }
                }
                if (useFragment)
                {
                    AddToFragment(data, layout);
                }
                else
                {
                    int blocks = (int)((size + _blockSize - 1) / _blockSize);
                    layout.BlockSizes = new uint[blocks];
                    Enqueue(new Pending { Layout = layout, FileStart = true });
                    for (int i = 0; i < blocks; i++)
                    {
                        int off = i * _blockSize;
                        int len = (int)Math.Min(_blockSize, size - off);
                        SubmitBlock(layout, i, data.Slice(off, len));
                    }
                }
                if (_opt.Deduplicate) _dedup[key] = layout;
                _bytesDone += size;
                return layout;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(content);
            }
        }

        // Large file: hash first (for duplicate detection), then stream the blocks.
        if (_opt.Deduplicate)
        {
            ContentKey key;
            using (Stream s = inode.Data.OpenRead()) key = new ContentKey(size, Hash(s));
            if (_dedup.TryGetValue(key, out FileLayout? same))
            {
                _bytesDone += size;
                return same;
            }
            _dedup[key] = layout;
        }
        {
            long blocks = (size + _blockSize - 1) / _blockSize;
            layout.BlockSizes = new uint[blocks];
            Enqueue(new Pending { Layout = layout, FileStart = true });
            byte[] buf = ArrayPool<byte>.Shared.Rent(_blockSize);
            try
            {
                using Stream s = inode.Data.OpenRead();
                long left = size;
                for (int i = 0; i < blocks; i++)
                {
                    _ct.ThrowIfCancellationRequested();
                    int len = (int)Math.Min(_blockSize, left);
                    s.ReadExactly(buf, 0, len);
                    SubmitBlock(layout, i, buf.AsSpan(0, len));
                    left -= len;
                    _bytesDone += len;
                    if ((i & 15) == 0) Report("data", null);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }
        return layout;
    }

    private void SubmitBlock(FileLayout layout, int index, ReadOnlySpan<byte> data)
    {
        if (IsAllZero(data))
        {
            // Sparse block: size word 0, nothing stored.
            layout.BlockSizes[index] = 0;
            layout.Sparse += data.Length;
            return;
        }
        byte[] block = ArrayPool<byte>.Shared.Rent(data.Length);
        data.CopyTo(block);
        Enqueue(new Pending { Layout = layout, BlockIndex = index, Task = CompressAsync(block, data.Length) });
    }

    private void AddToFragment(ReadOnlySpan<byte> data, FileLayout layout)
    {
        if (_fragFill + data.Length > _blockSize) FlushFragment();
        if (_fragFill == 0) _fragments.Add((0, 0));     // reserve the index, filled in when written
        layout.Fragment = (uint)(_fragments.Count - 1);
        layout.FragOffset = (uint)_fragFill;
        data.CopyTo(_fragBuf.AsSpan(_fragFill));
        _fragFill += data.Length;
    }

    private void FlushFragment()
    {
        if (_fragFill == 0) return;
        byte[] block = ArrayPool<byte>.Shared.Rent(_fragFill);
        _fragBuf.AsSpan(0, _fragFill).CopyTo(block);
        Enqueue(new Pending { FragmentIndex = _fragments.Count - 1, Task = CompressAsync(block, _fragFill) });
        _fragFill = 0;
    }

    private static UInt128 Hash(ReadOnlySpan<byte> data)
    {
        Span<byte> h = stackalloc byte[32];
        SHA256.HashData(data, h);
        return new UInt128(BitConverter.ToUInt64(h), BitConverter.ToUInt64(h[8..]));
    }

    private static UInt128 Hash(Stream data)
    {
        byte[] h = SHA256.HashData(data);
        return new UInt128(BitConverter.ToUInt64(h), BitConverter.ToUInt64(h.AsSpan(8)));
    }

    // -------------------------------------------------------- lookup tables

    /// <summary>Compresses one metadata block and appends it (with its 16-bit header) to <paramref name="target"/>.</summary>
    private void AppendMetaBlock(ReadOnlySpan<byte> block, ByteWriter target)
    {
        byte[] tmp = ArrayPool<byte>.Shared.Rent(MetadataSize);
        try
        {
            int n = block.Length > 1 ? _metaCodec.Compress(block, tmp.AsSpan(0, block.Length - 1)) : -1;
            if (n > 0 && n < block.Length)
            {
                target.U16(n);
                target.Bytes(tmp.AsSpan(0, n));
            }
            else
            {
                target.U16(block.Length | 0x8000);
                target.Bytes(block);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(tmp);
        }
    }

    /// <summary>
    /// Writes a table as metadata blocks followed by the index of their positions.
    /// Returns the position of the index.
    /// </summary>
    private long WriteIndexedTable(ReadOnlySpan<byte> table)
    {
        var index = new ByteWriter();
        var block = new ByteWriter(MetadataSize + 2);
        for (int off = 0; off < table.Length; off += MetadataSize)
        {
            int len = Math.Min(MetadataSize, table.Length - off);
            index.U64((ulong)_pos);
            block.Clear();
            AppendMetaBlock(table.Slice(off, len), block);
            WriteRaw(block.Span);
        }
        long start = _pos;
        WriteRaw(index.Span);
        return start;
    }

    private long WriteFragmentTable()
    {
        var t = new ByteWriter(_fragments.Count * 16 + 16);
        foreach ((long start, uint size) in _fragments)
        {
            t.U64((ulong)start);
            t.U32(size);
            t.U32(0);
        }
        return WriteIndexedTable(t.Span);
    }

    // ------------------------------------------------------------- metadata

    /// <summary>Stream of metadata blocks with addressing by (compressed block start, offset in block).</summary>
    private sealed class MetaStream(SquashFsWriter owner)
    {
        private readonly ByteWriter _done = new(1 << 16);
        private readonly byte[] _cur = new byte[MetadataSize];
        private int _fill;

        public long BlockStart => _done.Length;
        public int Offset => _fill;
        public long Ref => ((long)_done.Length << 16) | (uint)_fill;

        public void Write(ReadOnlySpan<byte> data)
        {
            while (data.Length > 0)
            {
                int n = Math.Min(data.Length, MetadataSize - _fill);
                data[..n].CopyTo(_cur.AsSpan(_fill));
                _fill += n;
                data = data[n..];
                if (_fill == MetadataSize) FlushBlock();
            }
        }

        private void FlushBlock()
        {
            if (_fill == 0) return;
            owner.AppendMetaBlock(_cur.AsSpan(0, _fill), _done);
            _fill = 0;
        }

        public ReadOnlySpan<byte> Finish()
        {
            FlushBlock();
            return _done.Span;
        }
    }

    private sealed class InodeInfo
    {
        public uint Number;
        public long Ref = -1;
        public int Links;
        public uint Xattr = InvalidXattr;
    }

    private sealed class MetadataBuilder
    {
        private readonly SquashFsWriter _w;
        private readonly MetaStream _inodes, _dirs;
        private readonly Dictionary<FsInode, InodeInfo> _info = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<uint, int> _idIndex = new();
        private readonly List<uint> _idList = new();
        private readonly List<long> _export = new();
        private readonly MetaStream _xattrKv;
        private readonly List<(long Ref, uint Count, uint Size)> _xattrIds = new();
        private readonly Dictionary<string, uint> _xattrDedup = new(StringComparer.Ordinal);
        private byte[] _inodeTable = Array.Empty<byte>(), _dirTable = Array.Empty<byte>();
        private uint _nextNumber = 1;

        public MetadataBuilder(SquashFsWriter w)
        {
            _w = w;
            _inodes = new MetaStream(w);
            _dirs = new MetaStream(w);
            _xattrKv = new MetaStream(w);
        }

        public ReadOnlySpan<byte> InodeTable => _inodeTable;
        public ReadOnlySpan<byte> DirTable => _dirTable;
        public int InodeCount => _info.Count;
        public int IdCount => _idList.Count;
        public long RootRef { get; private set; }

        public ReadOnlySpan<byte> IdTable
        {
            get
            {
                var t = new ByteWriter(_idList.Count * 4);
                foreach (uint id in _idList) t.U32(id);
                return t.ToArray();
            }
        }

        public ReadOnlySpan<byte> ExportTable
        {
            get
            {
                var t = new ByteWriter(_export.Count * 8);
                foreach (long r in _export) t.U64((ulong)r);
                return t.ToArray();
            }
        }

        private int IdIndex(uint id)
        {
            if (_w._opt.AllRoot) id = 0;
            if (_idIndex.TryGetValue(id, out int idx)) return idx;
            if (_idList.Count >= 65536) throw new FsException("SquashFS supports at most 65536 distinct user and group ids.");
            idx = _idList.Count;
            _idList.Add(id);
            _idIndex.Add(id, idx);
            return idx;
        }

        private sealed class Frame
        {
            public required FsNode Dir;
            public required List<FsNode> Children;      // sorted by encoded name
            public required List<byte[]> Names;
            public int NextSubdir;
        }

        private static Frame MakeFrame(FsNode dir)
        {
            var pairs = new List<(byte[] Name, FsNode Node)>(dir.ChildCount);
            foreach (FsNode c in dir.Children) pairs.Add((PosixName.Encode(c.Name), c));
            pairs.Sort(static (a, b) => a.Name.AsSpan().SequenceCompareTo(b.Name));
            var f = new Frame { Dir = dir, Children = new List<FsNode>(pairs.Count), Names = new List<byte[]>(pairs.Count) };
            foreach ((byte[] name, FsNode node) in pairs)
            {
                f.Children.Add(node);
                f.Names.Add(name);
            }
            return f;
        }

        public void Build()
        {
            // Pass 1: link counts and inode numbers. Numbers follow the order in which inodes are
            // written (files of a directory, then its subdirectories depth-first, then the directory).
            foreach (FsNode n in FsTree.Walk(_w._tree.Root))
            {
                if (!_info.TryGetValue(n.Inode, out InodeInfo? ii)) _info.Add(n.Inode, ii = new InodeInfo());
                ii.Links++;
            }
            Traverse(assignNumbers: true);
            _export.Clear();
            for (int i = 0; i < _info.Count; i++) _export.Add(0);

            // Pass 2: serialise.
            Traverse(assignNumbers: false);
            _inodeTable = _inodes.Finish().ToArray();
            _dirTable = _dirs.Finish().ToArray();
        }

        private void Traverse(bool assignNumbers)
        {
            var stack = new Stack<Frame>();
            stack.Push(MakeFrame(_w._tree.Root));
            bool entered = true;
            while (stack.Count > 0)
            {
                _w._ct.ThrowIfCancellationRequested();
                Frame f = stack.Peek();
                if (entered)
                {
                    // Non-directory children first so their inodes sit next to each other.
                    foreach (FsNode c in f.Children)
                    {
                        if (c.IsDirectory) continue;
                        InodeInfo ii = _info[c.Inode];
                        if (assignNumbers)
                        {
                            if (ii.Number == 0) ii.Number = _nextNumber++;
                        }
                        else if (ii.Ref < 0)
                        {
                            WriteInode(c, ii);
                        }
                    }
                    entered = false;
                }

                bool descended = false;
                while (f.NextSubdir < f.Children.Count)
                {
                    FsNode c = f.Children[f.NextSubdir++];
                    if (!c.IsDirectory) continue;
                    stack.Push(MakeFrame(c));
                    entered = true;
                    descended = true;
                    break;
                }
                if (descended) continue;

                // All children are done: the directory itself.
                InodeInfo di = _info[f.Dir.Inode];
                if (assignNumbers)
                {
                    di.Number = _nextNumber++;
                }
                else
                {
                    WriteDirectory(f, di);
                }
                stack.Pop();
            }
        }

        private void InodeHeader(ByteWriter b, int type, FsInode inode, InodeInfo ii)
        {
            b.U16(type);
            b.U16(inode.Mode & 0xFFF);
            b.U16(IdIndex(inode.Uid));
            b.U16(IdIndex(inode.Gid));
            b.U32((uint)Math.Clamp(inode.MTime, 0, uint.MaxValue));
            b.U32(ii.Number);
        }

        private void Commit(ByteWriter b, InodeInfo ii)
        {
            ii.Ref = _inodes.Ref;
            _inodes.Write(b.Span);
            _export[(int)ii.Number - 1] = ii.Ref;
        }

        private void WriteInode(FsNode node, InodeInfo ii)
        {
            FsInode inode = node.Inode;
            ii.Xattr = XattrId(inode, node);
            bool hasXattr = ii.Xattr != InvalidXattr;
            var b = new ByteWriter(64);
            switch (inode.Type)
            {
                case FsNodeType.File:
                {
                    FileLayout l = _w._layouts[inode];
                    bool ext = ii.Links > 1 || hasXattr || l.Sparse != 0 || l.Size > uint.MaxValue || l.StartBlock > uint.MaxValue;
                    if (ext)
                    {
                        InodeHeader(b, TypeFile + TypeExtOffset, inode, ii);
                        b.U64((ulong)l.StartBlock);
                        b.U64((ulong)l.Size);
                        b.U64((ulong)l.Sparse);
                        b.U32((uint)ii.Links);
                        b.U32(l.Fragment);
                        b.U32(l.FragOffset);
                        b.U32(ii.Xattr);
                    }
                    else
                    {
                        InodeHeader(b, TypeFile, inode, ii);
                        b.U32((uint)l.StartBlock);
                        b.U32(l.Fragment);
                        b.U32(l.FragOffset);
                        b.U32((uint)l.Size);
                    }
                    foreach (uint s in l.BlockSizes) b.U32(s);
                    break;
                }
                case FsNodeType.Symlink:
                {
                    byte[] target = PosixName.Encode(inode.LinkTarget ?? "");
                    InodeHeader(b, hasXattr ? TypeSymlink + TypeExtOffset : TypeSymlink, inode, ii);
                    b.U32((uint)ii.Links);
                    b.U32((uint)target.Length);
                    b.Bytes(target);
                    if (hasXattr) b.U32(ii.Xattr);
                    break;
                }
                case FsNodeType.BlockDevice:
                case FsNodeType.CharDevice:
                {
                    int t = inode.Type == FsNodeType.BlockDevice ? TypeBlock : TypeChar;
                    InodeHeader(b, hasXattr ? t + TypeExtOffset : t, inode, ii);
                    b.U32((uint)ii.Links);
                    b.U32(EncodeDev(inode.DevMajor, inode.DevMinor));
                    if (hasXattr) b.U32(ii.Xattr);
                    break;
                }
                case FsNodeType.Fifo:
                case FsNodeType.Socket:
                {
                    int t = inode.Type == FsNodeType.Fifo ? TypeFifo : TypeSocket;
                    InodeHeader(b, hasXattr ? t + TypeExtOffset : t, inode, ii);
                    b.U32((uint)ii.Links);
                    if (hasXattr) b.U32(ii.Xattr);
                    break;
                }
                default:
                    throw new InvalidOperationException();
            }
            Commit(b, ii);
        }

        private void WriteDirectory(Frame f, InodeInfo di)
        {
            // ---- listing
            var listing = new ByteWriter(Math.Max(64, f.Children.Count * 24));
            var indexes = new List<(int Offset, byte[] Name)>();
            int hdrPos = -1, count = 0, indexBase = 0, subdirs = 0;
            uint hdrStart = 0, hdrNumber = 0;
            for (int i = 0; i < f.Children.Count; i++)
            {
                FsNode c = f.Children[i];
                byte[] name = f.Names[i];
                InodeInfo ci = _info[c.Inode];
                if (c.IsDirectory) subdirs++;
                uint startBlock = (uint)(ci.Ref >> 16);
                int offset = (int)(ci.Ref & 0xFFFF);
                if (ci.Ref >> 16 > uint.MaxValue) throw new FsException("The inode table is too large for SquashFS.");
                long delta = (long)ci.Number - hdrNumber;
                int entrySize = 8 + name.Length;
                bool overflow = hdrPos >= 0 && listing.Length + entrySize - indexBase > MetadataSize;
                if (hdrPos < 0 || count == 256 || startBlock != hdrStart || delta is > 32767 or < -32768 || overflow)
                {
                    if (hdrPos >= 0)
                    {
                        PatchCount(listing, hdrPos, count);
                        if (overflow)
                        {
                            indexes.Add((listing.Length, name));
                            indexBase = listing.Length;
                        }
                    }
                    hdrPos = listing.Length;
                    listing.U32(0);
                    listing.U32(startBlock);
                    listing.U32(ci.Number);
                    hdrStart = startBlock;
                    hdrNumber = ci.Number;
                    count = 0;
                    delta = 0;
                }
                listing.U16(offset);
                listing.U16((ushort)(short)delta);
                listing.U16(BasicType(c.Type));
                listing.U16(name.Length - 1);
                listing.Bytes(name);
                count++;
            }
            if (hdrPos >= 0) PatchCount(listing, hdrPos, count);

            // ---- store the listing, resolving the block each index entry falls into
            long dirBlock = _dirs.BlockStart;
            int dirOffset = _dirs.Offset;
            var indexBlocks = new long[indexes.Count];
            int written = 0;
            ReadOnlySpan<byte> bytes = listing.Span;
            for (int i = 0; i < indexes.Count; i++)
            {
                _dirs.Write(bytes.Slice(written, indexes[i].Offset - written));
                written = indexes[i].Offset;
                indexBlocks[i] = _dirs.BlockStart;
            }
            _dirs.Write(bytes[written..]);
            if (dirBlock > uint.MaxValue) throw new FsException("The directory table is too large for SquashFS.");

            // ---- inode
            FsInode inode = f.Dir.Inode;
            di.Xattr = XattrId(inode, f.Dir);
            bool hasXattr = di.Xattr != InvalidXattr;
            uint parent = f.Dir.Parent is null ? (uint)_info.Count + 1 : _info[f.Dir.Parent.Inode].Number;
            int nlink = 2 + subdirs;
            var b = new ByteWriter(64);
            bool ext = hasXattr || f.Children.Count > 256 || listing.Length >= MetadataSize || indexes.Count > 0;
            if (ext)
            {
                InodeHeader(b, TypeDir + TypeExtOffset, inode, di);
                b.U32((uint)nlink);
                b.U32((uint)listing.Length + 3);
                b.U32((uint)dirBlock);
                b.U32(parent);
                b.U16(indexes.Count);
                b.U16(dirOffset);
                b.U32(di.Xattr);
                for (int i = 0; i < indexes.Count; i++)
                {
                    b.U32((uint)indexes[i].Offset);
                    b.U32((uint)indexBlocks[i]);
                    b.U32((uint)indexes[i].Name.Length - 1);
                    b.Bytes(indexes[i].Name);
                }
            }
            else
            {
                InodeHeader(b, TypeDir, inode, di);
                b.U32((uint)dirBlock);
                b.U32((uint)nlink);
                b.U16(listing.Length + 3);
                b.U16(dirOffset);
                b.U32(parent);
            }
            Commit(b, di);
            if (f.Dir.Parent is null) RootRef = di.Ref;
        }

        private static void PatchCount(ByteWriter listing, int hdrPos, int count) =>
            listing.PatchU32(hdrPos, (uint)(count - 1));

        // ------------------------------------------------------------ xattrs

        private uint XattrId(FsInode inode, FsNode node)
        {
            if (!_w._opt.StoreXattrs || inode.XAttrs is not { Count: > 0 }) return InvalidXattr;
            var entries = new List<(int Type, byte[] Name, byte[] Value)>();
            foreach ((string fullName, byte[] value) in inode.XAttrs)
            {
                int type = -1;
                for (int i = 0; i < XattrPrefixes.Length; i++)
                    if (fullName.StartsWith(XattrPrefixes[i], StringComparison.Ordinal)) type = i;
                if (type < 0)
                {
                    _w.Warnings.Add($"{node.Path}: extended attribute '{fullName}' cannot be stored in SquashFS and was skipped.");
                    continue;
                }
                entries.Add((type, PosixName.Encode(fullName[XattrPrefixes[type].Length..]), value));
            }
            if (entries.Count == 0) return InvalidXattr;

            var kv = new ByteWriter(128);
            uint size = 0;
            foreach ((int type, byte[] name, byte[] value) in entries)
            {
                kv.U16(type);
                kv.U16(name.Length);
                kv.Bytes(name);
                kv.U32((uint)value.Length);
                kv.Bytes(value);
                size += (uint)(8 + name.Length + value.Length);
            }
            string key = Convert.ToBase64String(kv.Span);
            if (_xattrDedup.TryGetValue(key, out uint id)) return id;
            id = (uint)_xattrIds.Count;
            _xattrIds.Add((_xattrKv.Ref, (uint)entries.Count, size));
            _xattrKv.Write(kv.Span);
            _xattrDedup.Add(key, id);
            return id;
        }

        /// <summary>Writes the xattr key/value blocks, the id table and its header. Returns the header position.</summary>
        public long WriteXattrTables()
        {
            if (_xattrIds.Count == 0) return unchecked((long)InvalidBlock);
            long kvStart = _w._pos;
            _w.WriteRaw(_xattrKv.Finish());

            var ids = new ByteWriter(_xattrIds.Count * 16);
            foreach ((long r, uint count, uint size) in _xattrIds)
            {
                ids.U64((ulong)r);
                ids.U32(count);
                ids.U32(size);
            }
            // Id blocks first, then the header that is immediately followed by the block index.
            var index = new ByteWriter();
            var block = new ByteWriter(MetadataSize + 2);
            ReadOnlySpan<byte> table = ids.Span;
            for (int off = 0; off < table.Length; off += MetadataSize)
            {
                int len = Math.Min(MetadataSize, table.Length - off);
                index.U64((ulong)_w._pos);
                block.Clear();
                _w.AppendMetaBlock(table.Slice(off, len), block);
                _w.WriteRaw(block.Span);
            }
            long start = _w._pos;
            var hdr = new ByteWriter(16);
            hdr.U64((ulong)kvStart);
            hdr.U32((uint)_xattrIds.Count);
            hdr.U32(0);
            _w.WriteRaw(hdr.Span);
            _w.WriteRaw(index.Span);
            return start;
        }
    }
}
