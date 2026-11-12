using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace FsForge.Core;

/// <summary>Thread-safe random access to an image.</summary>
public interface IBlockSource : IDisposable
{
    long Length { get; }

    /// <summary>Reads up to buffer.Length bytes at the offset; returns the number of bytes read.</summary>
    int Read(long offset, Span<byte> buffer);
}

public static class BlockSourceExtensions
{
    public static void ReadExactly(this IBlockSource source, long offset, Span<byte> buffer)
    {
        if (offset < 0) throw new FsException("The image is corrupt: it refers to data before its own start.");
        int done = 0;
        while (done < buffer.Length)
        {
            int n = source.Read(offset + done, buffer[done..]);
            if (n <= 0)
                throw new FsException($"The image is truncated: cannot read {buffer.Length} bytes at offset {offset}.");
            done += n;
        }
    }

    public static byte[] ReadBytes(this IBlockSource source, long offset, int count)
    {
        byte[] data = new byte[count];
        source.ReadExactly(offset, data);
        return data;
    }
}

/// <summary>Image stored in a host file.</summary>
public sealed class FileBlockSource : IBlockSource
{
    private readonly SafeFileHandle _handle;

    public FileBlockSource(string path, FileShare share = FileShare.Read | FileShare.Delete)
    {
        Path = path;
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, share, FileOptions.RandomAccess);
        Length = RandomAccess.GetLength(_handle);
    }

    public string Path { get; }
    public long Length { get; }

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= Length) return 0;
        return RandomAccess.Read(_handle, buffer, offset);
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>Image held in memory.</summary>
public sealed class MemoryBlockSource : IBlockSource
{
    private readonly byte[] _data;

    public MemoryBlockSource(byte[] data) => _data = data;

    public long Length => _data.Length;

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= _data.Length) return 0;
        int n = (int)Math.Min(buffer.Length, _data.Length - offset);
        _data.AsSpan((int)offset, n).CopyTo(buffer);
        return n;
    }

    public void Dispose() { }
}

/// <summary>A window into another source (filesystem embedded at an offset of a firmware file).</summary>
public sealed class SubBlockSource : IBlockSource
{
    private readonly IBlockSource _inner;
    private readonly long _offset;
    private readonly bool _ownsInner;

    public SubBlockSource(IBlockSource inner, long offset, long length, bool ownsInner = true)
    {
        _inner = inner;
        _offset = offset;
        Length = Math.Max(0, Math.Min(length, inner.Length - offset));
        _ownsInner = ownsInner;
    }

    public long Length { get; }

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= Length) return 0;
        if (buffer.Length > Length - offset) buffer = buffer[..(int)(Length - offset)];
        return _inner.Read(_offset + offset, buffer);
    }

    public void Dispose()
    {
        if (_ownsInner) _inner.Dispose();
    }
}

/// <summary>
/// Android sparse image (simg) presented as the raw image it expands to.
/// </summary>
public sealed class AndroidSparseSource : IBlockSource
{
    public const uint Magic = 0xED26FF3A;

    private const ushort ChunkRaw = 0xCAC1, ChunkFill = 0xCAC2, ChunkDontCare = 0xCAC3, ChunkCrc = 0xCAC4;

    private readonly IBlockSource _inner;
    private readonly long[] _outStart;      // start offset of every chunk in the expanded image
    private readonly long[] _outLength;
    private readonly long[] _fileOffset;    // -1: zeros, -2: fill pattern
    private readonly uint[] _fill;

    public AndroidSparseSource(IBlockSource inner)
    {
        _inner = inner;
        Span<byte> hdr = stackalloc byte[28];
        inner.ReadExactly(0, hdr);
        if (BinaryPrimitives.ReadUInt32LittleEndian(hdr) != Magic) throw new FsException("Not an Android sparse image.");
        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(hdr[4..]);
        ushort fileHdrSize = BinaryPrimitives.ReadUInt16LittleEndian(hdr[8..]);
        ushort chunkHdrSize = BinaryPrimitives.ReadUInt16LittleEndian(hdr[10..]);
        uint blockSize = BinaryPrimitives.ReadUInt32LittleEndian(hdr[12..]);
        uint totalBlocks = BinaryPrimitives.ReadUInt32LittleEndian(hdr[16..]);
        uint totalChunks = BinaryPrimitives.ReadUInt32LittleEndian(hdr[20..]);
        if (major != 1 || fileHdrSize < 28 || chunkHdrSize < 12 || blockSize == 0 || blockSize % 4 != 0)
            throw new FsException("Unsupported Android sparse image header.");

        _outStart = new long[totalChunks];
        _outLength = new long[totalChunks];
        _fileOffset = new long[totalChunks];
        _fill = new uint[totalChunks];

        long pos = fileHdrSize, outPos = 0;
        Span<byte> ch = stackalloc byte[12];
        Span<byte> four = stackalloc byte[4];
        int count = 0;
        for (uint i = 0; i < totalChunks; i++)
        {
            inner.ReadExactly(pos, ch);
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(ch);
            uint blocks = BinaryPrimitives.ReadUInt32LittleEndian(ch[4..]);
            uint totalSize = BinaryPrimitives.ReadUInt32LittleEndian(ch[8..]);
            long dataPos = pos + chunkHdrSize;
            long outLen = (long)blocks * blockSize;
            switch (type)
            {
                case ChunkRaw:
                    if (totalSize - chunkHdrSize != outLen) throw new FsException("Corrupt sparse image: bad raw chunk size.");
                    _fileOffset[count] = dataPos;
                    break;
                case ChunkFill:
                    inner.ReadExactly(dataPos, four);
                    _fill[count] = BinaryPrimitives.ReadUInt32LittleEndian(four);
                    _fileOffset[count] = -2;
                    break;
                case ChunkDontCare:
                    _fileOffset[count] = -1;
                    break;
                case ChunkCrc:
                    pos += totalSize;
                    continue;
                default:
                    throw new FsException($"Corrupt sparse image: unknown chunk type 0x{type:X4}.");
            }
            _outStart[count] = outPos;
            _outLength[count] = outLen;
            count++;
            outPos += outLen;
            pos += totalSize;
        }
        Array.Resize(ref _outStart, count);
        Array.Resize(ref _outLength, count);
        Array.Resize(ref _fileOffset, count);
        Array.Resize(ref _fill, count);
        Length = Math.Max(outPos, (long)totalBlocks * blockSize);
    }

    public long Length { get; }

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= Length) return 0;
        if (buffer.Length > Length - offset) buffer = buffer[..(int)(Length - offset)];
        int total = 0;
        while (buffer.Length > 0)
        {
            int idx = Array.BinarySearch(_outStart, offset);
            if (idx < 0) idx = ~idx - 1;
            if (idx < 0 || idx >= _outStart.Length || offset >= _outStart[idx] + _outLength[idx])
            {
                // Past the last chunk: implicit zeros.
                buffer.Clear();
                total += buffer.Length;
                break;
            }
            long inChunk = offset - _outStart[idx];
            int n = (int)Math.Min(buffer.Length, _outLength[idx] - inChunk);
            Span<byte> part = buffer[..n];
            long fo = _fileOffset[idx];
            if (fo >= 0) _inner.ReadExactly(fo + inChunk, part);
            else if (fo == -1 || _fill[idx] == 0) part.Clear();
            else
            {
                uint v = _fill[idx];
                for (int i = 0; i < n; i++) part[i] = (byte)(v >> (int)(((inChunk + i) & 3) * 8));
            }
            buffer = buffer[n..];
            offset += n;
            total += n;
        }
        return total;
    }

    public void Dispose() => _inner.Dispose();
}

/// <summary>Read-only stream over a range of a block source.</summary>
public sealed class BlockSourceStream : Stream
{
    private readonly IBlockSource _source;
    private readonly long _start;
    private readonly long _length;
    private long _position;

    public BlockSourceStream(IBlockSource source, long start, long length)
    {
        _source = source;
        _start = start;
        _length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, _length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        long left = _length - _position;
        if (left <= 0 || buffer.Length == 0) return 0;
        if (buffer.Length > left) buffer = buffer[..(int)left];
        int n = _source.Read(_start + _position, buffer);
        if (n <= 0) throw new FsException("The image is truncated.");
        _position += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Little-endian field accessors used by the on-disk structure parsers.</summary>
internal static class Le
{
    public static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    public static short S16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadInt16LittleEndian(b[o..]);
    public static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    public static ulong U64(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b[o..]);
    public static void W16(Span<byte> b, int o, int v) => BinaryPrimitives.WriteUInt16LittleEndian(b[o..], (ushort)v);
    public static void W32(Span<byte> b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b[o..], v);
    public static void W64(Span<byte> b, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b[o..], v);
}

/// <summary>Growable little-endian byte buffer used to serialise on-disk structures.</summary>
internal sealed class ByteWriter
{
    private byte[] _buf;
    private int _len;

    public ByteWriter(int capacity = 256) => _buf = new byte[capacity];

    public int Length => _len;
    public ReadOnlySpan<byte> Span => _buf.AsSpan(0, _len);
    public byte[] ToArray() => _buf.AsSpan(0, _len).ToArray();
    public void Clear() => _len = 0;

    private Span<byte> Reserve(int n)
    {
        if (_len + n > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + n));
        Span<byte> s = _buf.AsSpan(_len, n);
        _len += n;
        return s;
    }

    public void U8(int v) => Reserve(1)[0] = (byte)v;
    public void U16(int v) => BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), (ushort)v);
    public void U32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), v);
    public void U64(ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(Reserve(8), v);
    public void Bytes(ReadOnlySpan<byte> data) => data.CopyTo(Reserve(data.Length));
    public void Zeros(int n) => Reserve(n).Clear();
    public void PatchU32(int offset, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(offset, 4), v);
    public void PatchU16(int offset, int v) => BinaryPrimitives.WriteUInt16LittleEndian(_buf.AsSpan(offset, 2), (ushort)v);
}
