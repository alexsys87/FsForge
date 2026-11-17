using System.Buffers.Binary;

namespace FsForge.Core.Compression;

/// <summary>
/// The legacy LZ4 stream ("lz4 -l"): a magic number, then blocks of up to 8 MiB of data, each an LZ4 block
/// after its 32-bit compressed size. It is the only LZ4 format the Linux kernel unpacks an initramfs from, and
/// what Android compresses the ramdisks of boot images with. The magic may appear again between blocks
/// (concatenated streams).
/// </summary>
public static class Lz4Legacy
{
    public const uint Magic = 0x184C2102;
    public const int BlockSize = 8 << 20;

    /// <summary>The largest an LZ4 block of <paramref name="n"/> bytes can become.</summary>
    public static int Bound(int n) => n + n / 255 + 16;

    public static bool IsLz4Legacy(ReadOnlySpan<byte> head) => head.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(head) == Magic;
}

/// <summary>Forward-only stream that unpacks a legacy LZ4 stream.</summary>
public sealed class Lz4LegacyInputStream : Stream
{
    private readonly Stream _input;
    private readonly bool _leaveOpen;
    private readonly byte[] _block = new byte[Lz4Legacy.BlockSize];
    private byte[] _packed = new byte[1 << 16];
    private int _pos, _len;
    private bool _end;

    public Lz4LegacyInputStream(Stream input, bool leaveOpen = false)
    {
        _input = input;
        _leaveOpen = leaveOpen;
        Span<byte> m = stackalloc byte[4];
        if (!ReadFully(m) || BinaryPrimitives.ReadUInt32LittleEndian(m) != Lz4Legacy.Magic)
            throw new InvalidDataException("Not a legacy LZ4 stream.");
    }

    private bool ReadFully(Span<byte> buffer)
    {
        int got = 0;
        while (got < buffer.Length)
        {
            int n = _input.Read(buffer[got..]);
            if (n <= 0)
            {
                if (got == 0) return false;
                throw new InvalidDataException("The LZ4 data is truncated.");
            }
            got += n;
        }
        return true;
    }

    private bool NextBlock()
    {
        Span<byte> s = stackalloc byte[4];
        while (true)
        {
            if (!ReadFully(s)) return false;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(s);
            if (size == Lz4Legacy.Magic) continue;             // another stream follows
            if (size == 0) return false;
            if (size > Lz4Legacy.Bound(Lz4Legacy.BlockSize)) throw new InvalidDataException("Corrupt LZ4 data: invalid block size.");
            if (_packed.Length < size) _packed = new byte[size];
            if (!ReadFully(_packed.AsSpan(0, (int)size))) throw new InvalidDataException("The LZ4 data is truncated.");
            try
            {
                _len = new Lz4Codec().Decompress(_packed.AsSpan(0, (int)size), _block);
            }
            catch (FsException ex)
            {
                throw new InvalidDataException("Corrupt LZ4 data.", ex);
            }
            _pos = 0;
            if (_len > 0) return true;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0) return 0;
        if (_pos == _len)
        {
            if (_end || !NextBlock())
            {
                _end = true;
                return 0;
            }
        }
        int n = Math.Min(buffer.Length, _len - _pos);
        _block.AsSpan(_pos, n).CopyTo(buffer);
        _pos += n;
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen) _input.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Write-only stream that produces a legacy LZ4 stream; the 8 MiB blocks are compressed in parallel.</summary>
public sealed class Lz4LegacyOutputStream : Stream
{
    private readonly Stream _output;
    private readonly bool _leaveOpen;
    private readonly int _level, _workers;
    private readonly Queue<Task<byte[]>> _pending = new();
    private byte[] _chunk = new byte[Lz4Legacy.BlockSize];
    private int _fill;
    private bool _disposed;

    /// <param name="level">1 .. 12, as for lz4hc: higher is smaller and slower.</param>
    public Lz4LegacyOutputStream(Stream output, int level = 9, int threads = 0, bool leaveOpen = false)
    {
        _output = output;
        _leaveOpen = leaveOpen;
        _level = Math.Clamp(level, 1, 12);
        _workers = threads > 0 ? threads : Environment.ProcessorCount;
        Span<byte> m = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(m, Lz4Legacy.Magic);
        _output.Write(m);
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (buffer.Length > 0)
        {
            int n = Math.Min(buffer.Length, _chunk.Length - _fill);
            buffer[..n].CopyTo(_chunk.AsSpan(_fill));
            _fill += n;
            buffer = buffer[n..];
            if (_fill == _chunk.Length) Submit();
        }
    }

    private void Submit()
    {
        if (_fill == 0) return;
        byte[] data = _chunk;
        int length = _fill;
        _chunk = new byte[Lz4Legacy.BlockSize];
        _fill = 0;
        while (_pending.Count >= _workers) Drain(1);
        int level = _level;
        _pending.Enqueue(Task.Run(() => CompressBlock(data, length, level)));
    }

    /// <summary>The block with its size in front.</summary>
    private static byte[] CompressBlock(byte[] data, int length, int level)
    {
        byte[] dst = new byte[4 + Lz4Legacy.Bound(length)];
        int n = new Lz4FixedEncoder(level).Compress(data.AsSpan(0, length), dst.AsSpan(4), out int consumed);
        if (consumed != length) throw new InvalidOperationException("The LZ4 block does not hold all of its data.");
        BinaryPrimitives.WriteUInt32LittleEndian(dst, (uint)n);
        return dst.AsSpan(0, 4 + n).ToArray();
    }

    private void Drain(int count)
    {
        for (int i = 0; i < count && _pending.Count > 0; i++) _output.Write(_pending.Dequeue().GetAwaiter().GetResult());
    }

    public override void Flush()
    {
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
            _output.Flush();
            if (!_leaveOpen) _output.Dispose();
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
