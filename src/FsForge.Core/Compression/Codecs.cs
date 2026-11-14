using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace FsForge.Core.Compression;

/// <summary>
/// Block codec used by SquashFS. Instances are not thread-safe; create one per worker.
/// </summary>
public interface IBlockCodec
{
    /// <summary>Decompresses a block; returns the number of bytes written to <paramref name="dst"/>.</summary>
    int Decompress(ReadOnlySpan<byte> src, Span<byte> dst);

    /// <summary>
    /// Compresses a block. Returns the compressed size, or -1 when the result does not fit into
    /// <paramref name="dst"/> (the caller then stores the block uncompressed).
    /// </summary>
    int Compress(ReadOnlySpan<byte> src, Span<byte> dst);
}

// ------------------------------------------------------------------- zlib

public sealed class ZlibCodec : IBlockCodec
{
    private readonly int _level;

    public ZlibCodec(int level = 9) => _level = Math.Clamp(level, 1, 9);

    public int Decompress(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(src.Length);
        try
        {
            src.CopyTo(rented);
            using var ms = new MemoryStream(rented, 0, src.Length, false);
            using var z = new ZLibStream(ms, CompressionMode.Decompress);
            int total = 0;
            while (total < dst.Length)
            {
                int n = z.Read(dst[total..]);
                if (n <= 0) break;
                total += n;
            }
            if (total == dst.Length)
            {
                Span<byte> one = stackalloc byte[1];
                if (z.Read(one) > 0) throw new FsException("Corrupt gzip block: output is larger than expected.");
            }
            return total;
        }
        catch (InvalidDataException ex)
        {
            throw new FsException("Corrupt gzip block.", ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public int Compress(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        using var ms = new MemoryStream(src.Length / 2 + 64);
        var options = new ZLibCompressionOptions { CompressionLevel = _level };
        using (var z = new ZLibStream(ms, options, leaveOpen: true))
            z.Write(src);
        if (ms.Length > dst.Length) return -1;
        ms.GetBuffer().AsSpan(0, (int)ms.Length).CopyTo(dst);
        return (int)ms.Length;
    }
}

// --------------------------------------------------------------------- xz

public sealed class XzCodec : IBlockCodec
{
    private readonly LzmaDecoder _decoder = new();
    private readonly Lzma2Encoder? _encoder;
    private readonly int _dictCode;
    private readonly int _dictSize;
    private readonly BcjFilter[] _filters;
    private byte[] _work = Array.Empty<byte>();
    private byte[] _filtered = Array.Empty<byte>();
    private byte[] _best = Array.Empty<byte>();

    /// <param name="dictionarySize">LZMA2 dictionary size advertised in the stream headers.</param>
    /// <param name="filters">BCJ filters to try for every block (the smallest result wins).</param>
    /// <param name="settings">Encoder settings; null creates a decode-only codec.</param>
    public XzCodec(int dictionarySize, IReadOnlyList<BcjFilter>? filters = null, LzmaEncoderSettings? settings = null)
    {
        _dictSize = Math.Max(dictionarySize, 4096);
        _dictCode = XzFormat.DictCode(_dictSize);
        _filters = filters?.Where(Bcj.IsSupported).Distinct().ToArray() ?? Array.Empty<BcjFilter>();
        if (settings is not null) _encoder = new Lzma2Encoder(settings);
    }

    public int Decompress(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        if (src.Length < 12 + 8 + 12 || !src[..6].SequenceEqual(XzFormat.HeaderMagic))
            throw new FsException("Corrupt XZ block: bad stream header.");
        if (Crc32.Compute(src.Slice(6, 2)) != BinaryPrimitives.ReadUInt32LittleEndian(src[8..]))
            throw new FsException("Corrupt XZ block: stream header checksum mismatch.");
        int checkType = src[7] & 0x0F;
        int pos = 12;
        int total = 0;
        while (true)
        {
            if (pos >= src.Length) throw new FsException("Corrupt XZ block: truncated stream.");
            if (src[pos] == 0) break;       // index indicator: no more blocks
            XzFormat.BlockHeader bh = XzFormat.ParseBlockHeader(src[pos..]);
            pos += bh.HeaderSize;
            Span<byte> output = dst[total..];
            int produced = XzFormat.DecodeLzma2(_decoder, src[pos..], output, out int consumed);
            for (int i = bh.Filters.Length - 1; i >= 0; i--) Bcj.Apply(bh.Filters[i], output[..produced], encode: false);
            pos += consumed;
            pos += (4 - (consumed & 3)) & 3;
            if (checkType == 1)
            {
                if (pos + 4 > src.Length) throw new FsException("Corrupt XZ block: truncated check.");
                if (Crc32.Compute(output[..produced]) != BinaryPrimitives.ReadUInt32LittleEndian(src[pos..]))
                    throw new FsException("Corrupt XZ block: data checksum mismatch.");
            }
            pos += XzFormat.CheckSize(checkType);
            total += produced;
        }
        return total;
    }

    public int Compress(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        if (_encoder is null) throw new InvalidOperationException("The codec was created for decoding only.");
        int head = XzFormat.HeaderSizeFor(1);
        int cap = head + dst.Length + XzFormat.TrailerMax + 16;
        if (_work.Length < cap) _work = new byte[cap];
        uint crc = Crc32.Compute(src);

        // Plain LZMA2 first, then every configured BCJ filter; keep the smallest stream.
        int bestLen = -1;
        int payloadCap = dst.Length - 32;
        if (payloadCap <= 0) return -1;

        int size = _encoder.Encode(src, _work, head, payloadCap, _dictSize);
        if (size >= 0)
        {
            (int start, int len) = XzFormat.Wrap(_work, head, size, src.Length, crc, _dictCode, ReadOnlySpan<BcjFilter>.Empty);
            if (len <= dst.Length)
            {
                bestLen = len;
                if (_filters.Length == 0)
                {
                    _work.AsSpan(start, len).CopyTo(dst);
                    return len;
                }
                if (_best.Length < len) _best = new byte[Math.Max(len, dst.Length)];
                _work.AsSpan(start, len).CopyTo(_best);
            }
        }

        if (_filters.Length > 0)
        {
            if (_filtered.Length < src.Length) _filtered = new byte[src.Length];
            Span<BcjFilter> one = stackalloc BcjFilter[1];
            foreach (BcjFilter filter in _filters)
            {
                src.CopyTo(_filtered);
                Span<byte> f = _filtered.AsSpan(0, src.Length);
                Bcj.Apply(filter, f, encode: true);
                if (f.SequenceEqual(src)) continue;         // the filter changed nothing
                int limit = bestLen > 0 ? Math.Min(payloadCap, bestLen) : payloadCap;
                size = _encoder.Encode(f, _work, head, limit, _dictSize);
                if (size < 0) continue;
                one[0] = filter;
                (int start, int len) = XzFormat.Wrap(_work, head, size, src.Length, crc, _dictCode, one);
                if (len > dst.Length || (bestLen > 0 && len >= bestLen)) continue;
                bestLen = len;
                if (_best.Length < len) _best = new byte[Math.Max(len, dst.Length)];
                _work.AsSpan(start, len).CopyTo(_best);
            }
        }

        if (bestLen < 0) return -1;
        _best.AsSpan(0, bestLen).CopyTo(dst);
        return bestLen;
    }
}

// ------------------------------------------------------------ legacy lzma

/// <summary>Legacy "lzma" SquashFS compressor: LZMA_ALONE streams (decode only).</summary>
public sealed class LzmaAloneCodec : IBlockCodec
{
    private readonly LzmaDecoder _decoder = new();

    public int Decompress(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        if (src.Length < 13 + 5) throw new FsException("Corrupt LZMA block.");
        _decoder.SetProps(src[0]);
        _decoder.ResetDictionary();
        long size = BinaryPrimitives.ReadInt64LittleEndian(src[5..]);
        if (size > dst.Length) throw new FsException("Corrupt LZMA block: output is larger than expected.");
        return _decoder.Decode(dst, 0, 0, size < 0 ? -1 : (int)size, src[13..]);
    }

    public int Compress(ReadOnlySpan<byte> src, Span<byte> dst) =>
        throw new FsException("Writing the legacy LZMA SquashFS compression is not supported; choose XZ instead.");
}

// -------------------------------------------------------------------- lz4

public sealed class Lz4Codec : IBlockCodec
{
    private readonly int[] _table = new int[1 << 14];

    public int Decompress(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int ip = 0, op = 0;
        try
        {
            while (true)
            {
                int token = src[ip++];
                int litLen = token >> 4;
                if (litLen == 15)
                {
                    int b;
                    do { b = src[ip++]; litLen += b; } while (b == 255);
                }
                src.Slice(ip, litLen).CopyTo(dst[op..]);
                ip += litLen;
                op += litLen;
                if (ip >= src.Length) break;
                int offset = src[ip] | (src[ip + 1] << 8);
                ip += 2;
                int matchLen = token & 15;
                if (matchLen == 15)
                {
                    int b;
                    do { b = src[ip++]; matchLen += b; } while (b == 255);
                }
                matchLen += 4;
                int from = op - offset;
                if (offset == 0 || from < 0 || op + matchLen > dst.Length) throw new FsException("Corrupt LZ4 block.");
                if (offset >= matchLen)
                {
                    dst.Slice(from, matchLen).CopyTo(dst[op..]);
                    op += matchLen;
                }
                else
                {
                    for (int i = 0; i < matchLen; i++) dst[op++] = dst[from++];
                }
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException)
        {
            throw new FsException("Corrupt LZ4 block.");
        }
        return op;
    }

    public int Compress(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        // Greedy single-probe hash compressor producing a standard LZ4 block.
        const int MinMatch = 4, MfLimit = 12, LastLiterals = 5;
        int n = src.Length;
        int op = 0;
        int anchor = 0;
        int[] table = _table;
        Array.Fill(table, -1);

        bool Emit(ReadOnlySpan<byte> s, Span<byte> d, int litStart, int litLen, int offset, int matchLen)
        {
            int need = 1 + litLen + litLen / 255 + 1 + (offset > 0 ? 2 + matchLen / 255 + 1 : 0);
            if (op + need > d.Length) return false;
            int tokenPos = op++;
            int token;
            if (litLen >= 15)
            {
                token = 15 << 4;
                int rest = litLen - 15;
                while (rest >= 255) { d[op++] = 255; rest -= 255; }
                d[op++] = (byte)rest;
            }
            else
            {
                token = litLen << 4;
            }
            s.Slice(litStart, litLen).CopyTo(d[op..]);
            op += litLen;
            if (offset > 0)
            {
                d[op++] = (byte)offset;
                d[op++] = (byte)(offset >> 8);
                int ml = matchLen - MinMatch;
                if (ml >= 15)
                {
                    token |= 15;
                    ml -= 15;
                    while (ml >= 255) { d[op++] = 255; ml -= 255; }
                    d[op++] = (byte)ml;
                }
                else
                {
                    token |= ml;
                }
            }
            d[tokenPos] = (byte)token;
            return true;
        }

        if (n >= MfLimit + 1)
        {
            int limit = n - MfLimit;
            int matchEnd = n - LastLiterals;
            int ip = 0;
            while (ip < limit)
            {
                uint v = BinaryPrimitives.ReadUInt32LittleEndian(src[ip..]);
                int h = (int)((v * 2654435761u) >> 18);
                int cand = table[h];
                table[h] = ip;
                if (cand >= 0 && ip - cand <= 65535 && BinaryPrimitives.ReadUInt32LittleEndian(src[cand..]) == v)
                {
                    int len = 4;
                    while (ip + len < matchEnd && src[cand + len] == src[ip + len]) len++;
                    if (!Emit(src, dst, anchor, ip - anchor, ip - cand, len)) return -1;
                    ip += len;
                    anchor = ip;
                }
                else
                {
                    ip++;
                }
            }
        }
        if (!Emit(src, dst, anchor, n - anchor, 0, 0)) return -1;
        return op;
    }
}

// -------------------------------------------------------------------- lzo

/// <summary>LZO1X: the decompressor, and a compressor that writes the same format (UBIFS data blocks).</summary>
public sealed class LzoCodec : IBlockCodec
{
    public int Decompress(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        try
        {
            return Decode(src, dst);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException)
        {
            throw new FsException("Corrupt LZO block.");
        }
    }

    private static int Decode(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int ip = 0, op = 0;
        int t;
        int mPos;
        // 0: start of main loop, 1: after a literal run, 2: match (t holds the instruction), 3: match_next
        int step = 0;

        if (src[ip] > 17)
        {
            t = src[ip++] - 17;
            if (t < 4)
            {
                step = 3;
            }
            else
            {
                src.Slice(ip, t).CopyTo(dst[op..]);
                ip += t;
                op += t;
                step = 1;
            }
        }
        else
        {
            t = 0;
        }

        while (true)
        {
            if (step == 0)
            {
                t = src[ip++];
                if (t >= 16)
                {
                    step = 2;
                }
                else
                {
                    if (t == 0)
                    {
                        while (src[ip] == 0) { t += 255; ip++; }
                        t += 15 + src[ip++];
                    }
                    t += 3;
                    src.Slice(ip, t).CopyTo(dst[op..]);
                    ip += t;
                    op += t;
                    step = 1;
                }
            }

            if (step == 1)
            {
                t = src[ip++];
                if (t >= 16)
                {
                    step = 2;
                }
                else
                {
                    mPos = op - (1 + 0x0800) - (t >> 2) - (src[ip++] << 2);
                    if (mPos < 0) throw new FsException("Corrupt LZO block.");
                    dst[op++] = dst[mPos++];
                    dst[op++] = dst[mPos++];
                    dst[op++] = dst[mPos];
                    step = 4;
                }
            }

            if (step == 2)
            {
                int len;
                if (t >= 64)
                {
                    mPos = op - 1 - ((t >> 2) & 7) - (src[ip++] << 3);
                    len = (t >> 5) - 1 + 2;
                }
                else if (t >= 32)
                {
                    t &= 31;
                    if (t == 0)
                    {
                        while (src[ip] == 0) { t += 255; ip++; }
                        t += 31 + src[ip++];
                    }
                    mPos = op - 1 - ((src[ip] >> 2) + (src[ip + 1] << 6));
                    ip += 2;
                    len = t + 2;
                }
                else if (t >= 16)
                {
                    mPos = op - ((t & 8) << 11);
                    t &= 7;
                    if (t == 0)
                    {
                        while (src[ip] == 0) { t += 255; ip++; }
                        t += 7 + src[ip++];
                    }
                    mPos -= (src[ip] >> 2) + (src[ip + 1] << 6);
                    ip += 2;
                    if (mPos == op) return op;      // end of stream
                    mPos -= 0x4000;
                    len = t + 2;
                }
                else
                {
                    mPos = op - 1 - (t >> 2) - (src[ip++] << 2);
                    len = 2;
                }
                if (mPos < 0 || op + len > dst.Length) throw new FsException("Corrupt LZO block.");
                for (int i = 0; i < len; i++) dst[op++] = dst[mPos++];
                step = 4;
            }

            if (step == 4)
            {
                t = src[ip - 2] & 3;
                if (t == 0)
                {
                    step = 0;
                    continue;
                }
                step = 3;
            }

            if (step == 3)
            {
                // Copy 1..3 literals that follow a match, then read the next instruction.
                for (int i = 0; i < t; i++) dst[op++] = src[ip++];
                t = src[ip++];
                step = 2;
            }
        }
    }

    // ------------------------------------------------------------ compress

    private const int HashBits = 13, MaxDistance = 16384, MaxChain = 48;
    private readonly int[] _head = new int[1 << HashBits];
    private int[] _prev = Array.Empty<int>();

    /// <summary>
    /// Compresses to the LZO1X format (what lzo1x_decompress reads). Matches are searched through
    /// hash chains within the last 16 KiB, which keeps every match in the two short forms of the format.
    /// </summary>
    public int Compress(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int n = src.Length;
        if (_prev.Length < n) _prev = new int[n];
        Array.Fill(_head, -1);
        var o = new Output(dst);
        int literalStart = 0, ip = 0;

        while (ip + 3 <= n)
        {
            int hash = (int)(((uint)(src[ip] | src[ip + 1] << 8 | src[ip + 2] << 16) * 0x9E3779B1u) >> (32 - HashBits));
            int bestLength = 0, bestDistance = 0;
            int candidate = _head[hash];
            for (int chain = 0; candidate >= 0 && chain < MaxChain; chain++, candidate = _prev[candidate])
            {
                int distance = ip - candidate;
                if (distance > MaxDistance) break;
                int length = src[candidate..].CommonPrefixLength(src[ip..]);
                // A three-byte match pays off only in the two-byte form, which reaches 2048 bytes back.
                if (length > bestLength && (length >= 4 || length == 3 && distance <= 2048))
                {
                    bestLength = length;
                    bestDistance = distance;
                }
            }
            _prev[ip] = _head[hash];
            _head[hash] = ip;
            if (bestLength < 3)
            {
                ip++;
                continue;
            }

            if (!o.Literals(src[literalStart..ip]) || !o.Match(bestLength, bestDistance)) return -1;
            int end = ip + bestLength;
            for (ip++; ip < end && ip + 3 <= n; ip++)
            {
                int h = (int)(((uint)(src[ip] | src[ip + 1] << 8 | src[ip + 2] << 16) * 0x9E3779B1u) >> (32 - HashBits));
                _prev[ip] = _head[h];
                _head[h] = ip;
            }
            ip = end;
            literalStart = end;
        }
        if (!o.Literals(src[literalStart..]) || !o.End()) return -1;
        return o.Length;
    }

    private ref struct Output(Span<byte> buffer)
    {
        private readonly Span<byte> _buffer = buffer;
        private int _length;
        private bool _afterMatch;       // the last instruction was a match, whose low bits can announce up to 3 literals

        public readonly int Length => _length;

        private bool Put(int value)
        {
            if (_length >= _buffer.Length) return false;
            _buffer[_length++] = (byte)value;
            return true;
        }

        /// <summary>A run length above what the instruction byte holds: zero bytes count 255 each, the last byte the rest.</summary>
        private bool Extend(int rest)
        {
            for (; rest > 255; rest -= 255)
                if (!Put(0)) return false;
            return Put(rest);
        }

        public bool Literals(ReadOnlySpan<byte> run)
        {
            int count = run.Length;
            if (count == 0) return true;
            if (_length == 0 && count <= 238)
            {
                if (!Put(17 + count)) return false;         // only the first byte of a stream can say this
            }
            else if (count <= 3 && _afterMatch)
            {
                _buffer[_length - 2] |= (byte)count;
            }
            else if (count <= 3)
            {
                return false;                               // cannot happen: short runs follow a match or start the stream
            }
            else if (count <= 18)
            {
                if (!Put(count - 3)) return false;
            }
            else if (!Put(0) || !Extend(count - 18))
            {
                return false;
            }
            if (_length + count > _buffer.Length) return false;
            run.CopyTo(_buffer[_length..]);
            _length += count;
            _afterMatch = false;
            return true;
        }

        public bool Match(int length, int distance)
        {
            int d = distance - 1;
            _afterMatch = true;
            if (length <= 8 && distance <= 2048)
                return Put((length - 1) << 5 | (d & 7) << 2) && Put(d >> 3);
            if (length <= 33)
            {
                if (!Put(32 | (length - 2))) return false;
            }
            else if (!Put(32) || !Extend(length - 33))
            {
                return false;
            }
            return Put(d << 2 & 0xFF) && Put(d >> 6);
        }

        public bool End() => Put(17) && Put(0) && Put(0);
    }
}

// ------------------------------------------------------------------- zstd

public sealed class ZstdCodec : IBlockCodec, IDisposable
{
    private readonly ZstdSharp.Decompressor _decompressor = new();
    private readonly ZstdSharp.Compressor _compressor;

    public ZstdCodec(int level = 15) => _compressor = new ZstdSharp.Compressor(Math.Clamp(level, 1, 22));

    public int Decompress(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        try
        {
            if (!_decompressor.TryUnwrap(src, dst, out int written))
                throw new FsException("Corrupt Zstandard block: output is larger than expected.");
            return written;
        }
        catch (ZstdSharp.ZstdException ex)
        {
            throw new FsException("Corrupt Zstandard block.", ex);
        }
    }

    public int Compress(ReadOnlySpan<byte> src, Span<byte> dst) =>
        _compressor.TryWrap(src, dst, out int written) ? written : -1;

    public void Dispose()
    {
        _decompressor.Dispose();
        _compressor.Dispose();
    }
}
