using System.Buffers.Binary;

namespace FsForge.Core.Compression;

/// <summary>
/// Write-only stream that produces an .xz file. The input is cut into independent chunks that are
/// compressed in parallel and becomes one block of a single XZ stream (as "xz -T" writes it). A single
/// stream matters: the Linux kernel unpacks only the first stream of a compressed initramfs.
/// </summary>
public sealed class XzOutputStream : Stream
{
    private readonly Stream _output;
    private readonly bool _leaveOpen;
    private readonly int _chunkSize;
    private readonly int _workers;
    private readonly LzmaEncoderSettings _settings;
    private readonly Queue<Task<byte[]>> _pending = new();
    private readonly Stack<XzCodec> _codecs = new();
    private byte[] _chunk;
    private int _fill;
    private readonly List<(ulong Unpadded, ulong Uncompressed)> _blocks = new();
    private byte[]? _streamFlags;
    private bool _disposed;

    /// <param name="effort">0 = fast, 1 = normal, 2 = maximum.</param>
    /// <param name="chunkSize">Uncompressed size of one chunk (also the dictionary size).</param>
    /// <param name="threads">Worker threads; 0 means one per processor.</param>
    public XzOutputStream(Stream output, int effort = 1, int chunkSize = 1 << 21, int threads = 0, bool leaveOpen = false)
    {
        _output = output;
        _leaveOpen = leaveOpen;
        _chunkSize = Math.Clamp(chunkSize, 1 << 16, 1 << 26);
        _workers = threads > 0 ? threads : Environment.ProcessorCount;
        _settings = LzmaEncoderSettings.FromEffort(effort);
        _chunk = new byte[_chunkSize];
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (buffer.Length > 0)
        {
            int n = Math.Min(buffer.Length, _chunkSize - _fill);
            buffer[..n].CopyTo(_chunk.AsSpan(_fill));
            _fill += n;
            buffer = buffer[n..];
            if (_fill == _chunkSize) Submit();
        }
    }

    private void Submit()
    {
        if (_fill == 0) return;
        byte[] data = _chunk;
        int length = _fill;
        _chunk = new byte[_chunkSize];
        _fill = 0;
        while (_pending.Count >= _workers) Drain(1);
        _pending.Enqueue(Task.Run(() => CompressChunk(data, length)));
    }

    private void Drain(int count)
    {
        for (int i = 0; i < count && _pending.Count > 0; i++)
        {
            byte[] stream = _pending.Dequeue().GetAwaiter().GetResult();
            WriteBlockOf(stream);
        }
    }

    /// <summary>
    /// Takes the block out of a complete single-block stream and appends it to the output stream; the
    /// stream header goes out once, before the first block.
    /// </summary>
    private void WriteBlockOf(byte[] stream)
    {
        ReadOnlySpan<byte> s = stream;
        int footer = s.Length - 12;
        int indexSize = (int)(BinaryPrimitives.ReadUInt32LittleEndian(s[(footer + 4)..]) + 1) * 4;
        int pos = footer - indexSize + 1;                    // past the index indicator
        if (XzFormat.ReadVli(s, ref pos) != 1) throw new InvalidOperationException("Expected a single-block XZ stream.");
        ulong unpadded = XzFormat.ReadVli(s, ref pos);
        ulong uncompressed = XzFormat.ReadVli(s, ref pos);
        if (_streamFlags is null)
        {
            _streamFlags = s.Slice(6, 2).ToArray();
            _output.Write(s[..12]);
        }
        _output.Write(s.Slice(12, (int)((unpadded + 3) & ~3UL)));
        _blocks.Add((unpadded, uncompressed));
    }

    /// <summary>The index of all blocks and the stream footer.</summary>
    private void WriteTrailer()
    {
        var buf = new List<byte> { 0 };
        Span<byte> vli = stackalloc byte[10];
        void Vli(ulong v, Span<byte> tmp)
        {
            int n = XzFormat.WriteVli(tmp, v);
            for (int i = 0; i < n; i++) buf.Add(tmp[i]);
        }
        Vli((ulong)_blocks.Count, vli);
        foreach (var (unpadded, uncompressed) in _blocks)
        {
            Vli(unpadded, vli);
            Vli(uncompressed, vli);
        }
        while ((buf.Count & 3) != 0) buf.Add(0);
        byte[] index = new byte[buf.Count + 4];
        buf.CopyTo(index);
        BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(buf.Count), Crc32.Compute(index.AsSpan(0, buf.Count)));
        _output.Write(index);

        Span<byte> foot = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(foot[4..], (uint)(index.Length / 4 - 1));
        _streamFlags.AsSpan().CopyTo(foot[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(foot, Crc32.Compute(foot.Slice(4, 6)));
        foot[10] = (byte)'Y';
        foot[11] = (byte)'Z';
        _output.Write(foot);
    }

    private byte[] CompressChunk(byte[] data, int length)
    {
        XzCodec? codec;
        lock (_codecs) _codecs.TryPop(out codec);
        codec ??= new XzCodec(_chunkSize, null, _settings);
        try
        {
            byte[] dst = new byte[length];
            int n = codec.Compress(data.AsSpan(0, length), dst);
            if (n > 0) return dst.AsSpan(0, n).ToArray();
            return Stored(data.AsSpan(0, length), _chunkSize);
        }
        finally
        {
            lock (_codecs) _codecs.Push(codec);
        }
    }

    /// <summary>Builds an XZ stream whose LZMA2 payload consists of uncompressed chunks.</summary>
    internal static byte[] Stored(ReadOnlySpan<byte> data, int dictionarySize)
    {
        const int Piece = 1 << 16;
        int pieces = (data.Length + Piece - 1) / Piece;
        int head = XzFormat.HeaderSizeFor(0);
        byte[] buf = new byte[head + data.Length + pieces * 3 + 1 + XzFormat.TrailerMax + 16];
        int p = head;
        for (int off = 0; off < data.Length; off += Piece)
        {
            int n = Math.Min(Piece, data.Length - off);
            buf[p++] = (byte)(off == 0 ? 1 : 2);        // 1: uncompressed with dictionary reset, 2: without
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(p), (ushort)(n - 1));
            p += 2;
            data.Slice(off, n).CopyTo(buf.AsSpan(p));
            p += n;
        }
        buf[p++] = 0;
        (int start, int len) = XzFormat.Wrap(buf, head, p - head, data.Length, Crc32.Compute(data),
            XzFormat.DictCode(Math.Max(dictionarySize, 4096)), ReadOnlySpan<BcjFilter>.Empty);
        return buf.AsSpan(start, len).ToArray();
    }

    public override void Flush()
    {
        // Chunk boundaries are not forced here: flushing only pushes finished chunks downstream.
        Drain(_pending.Count);
        _output.Flush();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            Submit();
            Drain(_pending.Count);
            if (_streamFlags is null) WriteBlockOf(Stored(ReadOnlySpan<byte>.Empty, _chunkSize));
            WriteTrailer();
            _output.Flush();
            if (!_leaveOpen) _output.Dispose();
        }
        base.Dispose(disposing);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
