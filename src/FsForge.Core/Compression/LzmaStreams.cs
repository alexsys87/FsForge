using System.Runtime.CompilerServices;

namespace FsForge.Core.Compression;

/// <summary>
/// Streaming LZMA (LZMA1) decoder with a circular dictionary, for data that does not fit into memory:
/// the coder of 7z archives written by 7-Zip before 9.30 and the "lzma" method of later versions.
/// </summary>
public sealed class LzmaInputStream : Stream
{
    private const int NumStates = 12;
    private const ushort ProbInit = 1024;

    private readonly Stream _input;
    private readonly bool _leaveOpen;
    private readonly byte[] _inBuf = new byte[1 << 16];
    private int _inPos, _inLen, _overrun;
    private uint _range, _code;

    private readonly byte[] _win;
    private int _winPos;
    private bool _winFull;
    private long _total;
    private readonly long _outSize;
    private bool _finished;

    private readonly ushort[] _isMatch = new ushort[NumStates << 4];
    private readonly ushort[] _isRep = new ushort[NumStates];
    private readonly ushort[] _isRepG0 = new ushort[NumStates];
    private readonly ushort[] _isRepG1 = new ushort[NumStates];
    private readonly ushort[] _isRepG2 = new ushort[NumStates];
    private readonly ushort[] _isRep0Long = new ushort[NumStates << 4];
    private readonly ushort[] _posSlot = new ushort[4 << 6];
    private readonly ushort[] _posSpecial = new ushort[128];
    private readonly ushort[] _align = new ushort[16];
    private readonly ushort[] _lenChoice = new ushort[2];
    private readonly ushort[] _lenLow = new ushort[16 << 3];
    private readonly ushort[] _lenMid = new ushort[16 << 3];
    private readonly ushort[] _lenHigh = new ushort[256];
    private readonly ushort[] _repChoice = new ushort[2];
    private readonly ushort[] _repLow = new ushort[16 << 3];
    private readonly ushort[] _repMid = new ushort[16 << 3];
    private readonly ushort[] _repHigh = new ushort[256];
    private readonly ushort[] _literal;
    private readonly int _lc, _lp, _pb;
    private uint _state, _rep0, _rep1, _rep2, _rep3;
    private int _pendingLen;

    /// <param name="properties">The five property bytes: lc/lp/pb and the dictionary size.</param>
    /// <param name="outSize">Size of the decoded data, or -1 when the stream ends with an end marker.</param>
    public LzmaInputStream(Stream input, ReadOnlySpan<byte> properties, long outSize, bool leaveOpen = false)
    {
        if (properties.Length < 5) throw new FsException("Corrupt LZMA data: the properties are missing.");
        int p = properties[0];
        if (p >= 9 * 5 * 5) throw new FsException("Corrupt LZMA data: invalid properties.");
        _lc = p % 9;
        _lp = p / 9 % 5;
        _pb = p / 45;
        long dict = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(properties[1..]);
        if (outSize >= 0) dict = Math.Min(dict, Math.Max(outSize, 1));
        dict = Math.Max(dict, 4096);
        if (dict > 1L << 30) throw new FsException("The LZMA dictionary is larger than 1 GiB, which is not supported.");
        _win = new byte[dict];
        _literal = new ushort[0x300 << (_lc + _lp)];
        foreach (ushort[] a in new[] { _isMatch, _isRep, _isRepG0, _isRepG1, _isRepG2, _isRep0Long, _posSlot, _posSpecial, _align,
                     _lenChoice, _lenLow, _lenMid, _lenHigh, _repChoice, _repLow, _repMid, _repHigh, _literal })
            Array.Fill(a, ProbInit);
        _input = input;
        _leaveOpen = leaveOpen;
        _outSize = outSize;
        if (InByte() != 0) throw new FsException("Corrupt LZMA data: bad range coder header.");
        _range = 0xFFFFFFFF;
        for (int i = 0; i < 4; i++) _code = (_code << 8) | InByte();
        if (outSize == 0) _finished = true;
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

    private byte InByte()
    {
        if (_inPos == _inLen)
        {
            _inLen = _input.Read(_inBuf, 0, _inBuf.Length);
            _inPos = 0;
            if (_inLen <= 0)
            {
                _inLen = 0;
                if (++_overrun > 4) throw new FsException("Corrupt LZMA data: the compressed stream is truncated.");
                return 0;
            }
        }
        return _inBuf[_inPos++];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint Bit(ref ushort prob)
    {
        if (_range < 0x01000000)
        {
            _range <<= 8;
            _code = (_code << 8) | InByte();
        }
        uint p = prob;
        uint bound = (_range >> 11) * p;
        if (_code < bound)
        {
            _range = bound;
            prob = (ushort)(p + ((2048 - p) >> 5));
            return 0;
        }
        _range -= bound;
        _code -= bound;
        prob = (ushort)(p - (p >> 5));
        return 1;
    }

    private uint Direct(int bits)
    {
        uint result = 0;
        for (; bits > 0; bits--)
        {
            if (_range < 0x01000000)
            {
                _range <<= 8;
                _code = (_code << 8) | InByte();
            }
            _range >>= 1;
            _code -= _range;
            uint t = 0 - (_code >> 31);
            _code += _range & t;
            result = (result << 1) + (t + 1);
        }
        return result;
    }

    private uint Tree(ushort[] probs, int offset, int bits)
    {
        uint m = 1;
        for (int i = 0; i < bits; i++) m = (m << 1) | Bit(ref probs[offset + (int)m]);
        return m - (1u << bits);
    }

    private uint ReverseTree(ushort[] probs, int offset, int bits)
    {
        uint m = 1, result = 0;
        for (int i = 0; i < bits; i++)
        {
            uint b = Bit(ref probs[offset + (int)m]);
            m = (m << 1) | b;
            result |= b << i;
        }
        return result;
    }

    private int DecodeLen(ushort[] choice, ushort[] low, ushort[] mid, ushort[] high, int posState)
    {
        if (Bit(ref choice[0]) == 0) return (int)Tree(low, posState << 3, 3);
        if (Bit(ref choice[1]) == 0) return 8 + (int)Tree(mid, posState << 3, 3);
        return 16 + (int)Tree(high, 0, 8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte Back(uint dist)
    {
        int i = _winPos - (int)dist - 1;
        if (i < 0) i += _win.Length;
        return _win[i];
    }

    private void CheckDistance(uint dist)
    {
        if (!_winFull && dist >= (uint)_winPos || dist >= (uint)_win.Length)
            throw new FsException("Corrupt LZMA data: invalid distance.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Put(byte b)
    {
        _win[_winPos++] = b;
        if (_winPos == _win.Length)
        {
            _winPos = 0;
            _winFull = true;
        }
        _total++;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> dst)
    {
        int n = 0;
        uint pbMask = (1u << _pb) - 1, lpMask = (1u << _lp) - 1;
        while (n < dst.Length && !_finished)
        {
            if (_outSize >= 0 && _total >= _outSize)
            {
                _finished = true;
                break;
            }
            if (_pendingLen > 0)
            {
                byte b = Back(_rep0);
                Put(b);
                dst[n++] = b;
                _pendingLen--;
                continue;
            }

            int posState = (int)((uint)_total & pbMask);
            int ims = (int)(_state << 4) + posState;
            if (Bit(ref _isMatch[ims]) == 0)
            {
                int prev = _total > 0 ? Back(0) : 0;
                int litBase = 0x300 * ((int)(((uint)_total & lpMask) << _lc) + (prev >> (8 - _lc)));
                uint symbol = 1;
                if (_state < 7)
                {
                    do { symbol = (symbol << 1) | Bit(ref _literal[litBase + (int)symbol]); }
                    while (symbol < 0x100);
                }
                else
                {
                    CheckDistance(_rep0);
                    uint matchByte = Back(_rep0);
                    uint offs = 0x100;
                    do
                    {
                        matchByte <<= 1;
                        uint matchBit = matchByte & offs;
                        uint bit = Bit(ref _literal[litBase + (int)(offs + matchBit + symbol)]);
                        symbol = (symbol << 1) | bit;
                        offs &= bit != 0 ? matchBit : ~matchBit;
                    }
                    while (symbol < 0x100);
                }
                Put((byte)symbol);
                dst[n++] = (byte)symbol;
                _state = _state < 4 ? 0 : _state < 10 ? _state - 3 : _state - 6;
                continue;
            }

            int len;
            if (Bit(ref _isRep[_state]) == 0)
            {
                _rep3 = _rep2;
                _rep2 = _rep1;
                _rep1 = _rep0;
                len = DecodeLen(_lenChoice, _lenLow, _lenMid, _lenHigh, posState);
                _state = _state < 7 ? 7u : 10u;
                int lenState = len < 4 ? len : 3;
                uint slot = Tree(_posSlot, lenState << 6, 6);
                if (slot < 4)
                {
                    _rep0 = slot;
                }
                else
                {
                    int direct = (int)(slot >> 1) - 1;
                    _rep0 = (2 | (slot & 1)) << direct;
                    if (slot < 14)
                    {
                        _rep0 += ReverseTree(_posSpecial, (int)(_rep0 - slot - 1), direct);
                    }
                    else
                    {
                        _rep0 += Direct(direct - 4) << 4;
                        _rep0 += ReverseTree(_align, 0, 4);
                        if (_rep0 == 0xFFFFFFFF)
                        {
                            // End marker.
                            if (_outSize >= 0 && _total != _outSize) throw new FsException("Corrupt LZMA data: unexpected end marker.");
                            _finished = true;
                            break;
                        }
                    }
                }
            }
            else
            {
                if (Bit(ref _isRepG0[_state]) == 0)
                {
                    if (Bit(ref _isRep0Long[ims]) == 0)
                    {
                        CheckDistance(_rep0);
                        _state = _state < 7 ? 9u : 11u;
                        byte b = Back(_rep0);
                        Put(b);
                        dst[n++] = b;
                        continue;
                    }
                }
                else
                {
                    uint dist;
                    if (Bit(ref _isRepG1[_state]) == 0)
                    {
                        dist = _rep1;
                    }
                    else
                    {
                        if (Bit(ref _isRepG2[_state]) == 0)
                        {
                            dist = _rep2;
                        }
                        else
                        {
                            dist = _rep3;
                            _rep3 = _rep2;
                        }
                        _rep2 = _rep1;
                    }
                    _rep1 = _rep0;
                    _rep0 = dist;
                }
                len = DecodeLen(_repChoice, _repLow, _repMid, _repHigh, posState);
                _state = _state < 7 ? 8u : 11u;
            }
            CheckDistance(_rep0);
            len += 2;
            if (_outSize >= 0 && len > _outSize - _total) throw new FsException("Corrupt LZMA data: a match crosses the end of the data.");
            _pendingLen = len;
        }
        return n;
    }
}

/// <summary>Streaming decoder of a raw LZMA2 stream (the LZMA2 coder of 7z archives).</summary>
public sealed class Lzma2InputStream : Stream
{
    private readonly Stream _input;
    private readonly bool _leaveOpen;
    private readonly LzmaDecoder _decoder = new();
    private readonly byte[] _dict;
    private readonly int _dictSize;
    private readonly byte[] _chunk = new byte[1 << 16];
    private int _dictPos, _dictStart, _readPos;
    private bool _needDictReset = true, _needProps = true, _finished;

    public Lzma2InputStream(Stream input, long dictionarySize, bool leaveOpen = false)
    {
        if (dictionarySize > 1L << 30) throw new FsException("The LZMA2 dictionary is larger than 1 GiB, which is not supported.");
        _dictSize = (int)Math.Max(dictionarySize, 4096);
        _dict = new byte[_dictSize + (1 << 21) + (1 << 16)];
        _input = input;
        _leaveOpen = leaveOpen;
    }

    /// <summary>Dictionary size of an LZMA2 properties byte (0-40).</summary>
    public static long DictionarySize(int prop)
    {
        if (prop > 40) throw new FsException("Corrupt LZMA2 data: invalid dictionary size.");
        return prop == 40 ? 0xFFFFFFFF : (2L | (prop & 1)) << (prop / 2 + 11);
    }

    /// <summary>The smallest properties byte whose dictionary holds <paramref name="size"/> bytes.</summary>
    public static int DictionaryProp(long size)
    {
        for (int p = 0; p < 40; p++) if (DictionarySize(p) >= size) return p;
        return 40;
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

    private void ReadFully(Span<byte> buffer)
    {
        int done = 0;
        while (done < buffer.Length)
        {
            int n = _input.Read(buffer[done..]);
            if (n <= 0) throw new FsException("Corrupt LZMA2 data: unexpected end of the stream.");
            done += n;
        }
    }

    private bool DecodeChunk()
    {
        Span<byte> hdr = stackalloc byte[5];
        ReadFully(hdr[..1]);
        int control = hdr[0];
        if (control == 0) return false;

        if (_dictPos + (1 << 21) > _dict.Length)
        {
            int keep = Math.Min(_dictSize, _dictPos - _dictStart);
            Buffer.BlockCopy(_dict, _dictPos - keep, _dict, 0, keep);
            _dictStart = 0;
            _dictPos = keep;
            _readPos = keep;
        }

        if (control < 0x80)
        {
            if (control > 2) throw new FsException("Corrupt LZMA2 data: invalid control byte.");
            if (control == 1)
            {
                _needDictReset = false;
                _decoder.ResetDictionary();
                _dictStart = _dictPos;
            }
            else if (_needDictReset)
            {
                throw new FsException("Corrupt LZMA2 data: the first chunk does not reset the dictionary.");
            }
            ReadFully(hdr[..2]);
            int size = ((hdr[0] << 8) | hdr[1]) + 1;
            ReadFully(_dict.AsSpan(_dictPos, size));
            _decoder.SkipUncompressed(size);
            _dictPos += size;
            return true;
        }

        ReadFully(hdr[..4]);
        int unpacked = (((control & 0x1F) << 16) | (hdr[0] << 8) | hdr[1]) + 1;
        int packed = ((hdr[2] << 8) | hdr[3]) + 1;
        int mode = (control >> 5) & 3;
        if (mode == 3)
        {
            _needDictReset = false;
            _decoder.ResetDictionary();
            _dictStart = _dictPos;
        }
        else if (_needDictReset)
        {
            throw new FsException("Corrupt LZMA2 data: the first chunk does not reset the dictionary.");
        }
        if (mode >= 2)
        {
            ReadFully(hdr[..1]);
            _decoder.SetProps(hdr[0]);
            _needProps = false;
        }
        else if (_needProps)
        {
            throw new FsException("Corrupt LZMA2 data: the properties are missing.");
        }
        else if (mode == 1)
        {
            _decoder.ResetState();
        }
        ReadFully(_chunk.AsSpan(0, packed));
        int minStart = Math.Max(_dictStart, _dictPos - _dictSize);
        _decoder.Decode(_dict.AsSpan(0, _dictPos + unpacked), _dictPos, minStart, unpacked, _chunk.AsSpan(0, packed));
        _dictPos += unpacked;
        return true;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0) return 0;
        while (_readPos == _dictPos)
        {
            if (_finished || !DecodeChunk())
            {
                _finished = true;
                return 0;
            }
        }
        int n = Math.Min(buffer.Length, _dictPos - _readPos);
        _dict.AsSpan(_readPos, n).CopyTo(buffer);
        _readPos += n;
        return n;
    }
}

/// <summary>
/// Write-only stream that produces one raw LZMA2 stream (the LZMA2 coder of 7z, as 7-Zip writes it with
/// several threads): the input is cut into blocks that are compressed in parallel, each starting with a
/// dictionary reset; the block size is the dictionary size.
/// </summary>
public sealed class Lzma2OutputStream : Stream
{
    private readonly Stream _output;
    private readonly bool _leaveOpen;
    private readonly int _blockSize, _workers;
    private readonly LzmaEncoderSettings _settings;
    private readonly Queue<Task<byte[]>> _pending = new();
    private readonly Stack<Lzma2Encoder> _encoders = new();
    private byte[] _block;
    private int _fill;
    private bool _disposed;

    public Lzma2OutputStream(Stream output, int effort = 1, int blockSize = 1 << 22, int threads = 0, bool leaveOpen = false)
    {
        _output = output;
        _leaveOpen = leaveOpen;
        _blockSize = Math.Clamp(blockSize, 1 << 16, 1 << 26);
        _workers = threads > 0 ? threads : Environment.ProcessorCount;
        _settings = LzmaEncoderSettings.FromEffort(effort);
        _block = new byte[_blockSize];
    }

    /// <summary>The properties byte of the coder: a dictionary that holds one block.</summary>
    public byte PropertiesByte => (byte)Lzma2InputStream.DictionaryProp(_blockSize);

    /// <summary>Bytes written to the output so far.</summary>
    public long CompressedLength { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (buffer.Length > 0)
        {
            int n = Math.Min(buffer.Length, _blockSize - _fill);
            buffer[..n].CopyTo(_block.AsSpan(_fill));
            _fill += n;
            buffer = buffer[n..];
            if (_fill == _blockSize) Submit();
        }
    }

    private void Submit()
    {
        if (_fill == 0) return;
        byte[] data = _block;
        int length = _fill;
        _block = new byte[_blockSize];
        _fill = 0;
        while (_pending.Count >= _workers) Drain(1);
        _pending.Enqueue(Task.Run(() => Compress(data, length)));
    }

    /// <summary>The LZMA2 chunks of one block, without the end marker.</summary>
    private byte[] Compress(byte[] data, int length)
    {
        Lzma2Encoder? enc;
        lock (_encoders) _encoders.TryPop(out enc);
        enc ??= new Lzma2Encoder(_settings);
        try
        {
            byte[] dst = new byte[length + length / 16 + 64];
            int n = enc.Encode(data.AsSpan(0, length), dst, 0, dst.Length, _blockSize);
            if (n > 1) return dst.AsSpan(0, n - 1).ToArray();
            return Stored(data.AsSpan(0, length));
        }
        finally
        {
            lock (_encoders) _encoders.Push(enc);
        }
    }

    /// <summary>Uncompressed chunks; the first resets the dictionary, and the next block's first chunk resets it again.</summary>
    private static byte[] Stored(ReadOnlySpan<byte> data)
    {
        const int Piece = 1 << 16;
        var ms = new MemoryStream(data.Length + data.Length / Piece * 3 + 3);
        for (int off = 0; off < data.Length; off += Piece)
        {
            int n = Math.Min(Piece, data.Length - off);
            ms.WriteByte((byte)(off == 0 ? 1 : 2));
            ms.WriteByte((byte)((n - 1) >> 8));
            ms.WriteByte((byte)(n - 1));
            ms.Write(data.Slice(off, n));
        }
        return ms.ToArray();
    }

    private void Drain(int count)
    {
        for (int i = 0; i < count && _pending.Count > 0; i++)
        {
            byte[] chunks = _pending.Dequeue().GetAwaiter().GetResult();
            _output.Write(chunks);
            CompressedLength += chunks.Length;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            Submit();
            Drain(int.MaxValue);
            _output.WriteByte(0);
            CompressedLength++;
            _output.Flush();
            if (!_leaveOpen) _output.Dispose();
        }
        base.Dispose(disposing);
    }
}
