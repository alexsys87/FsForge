using System.Buffers.Binary;

namespace FsForge.Core.Compression;

/// <summary>The CRC of bzip2: CRC-32 with the polynomial 0x04C11DB7, most significant bit first.</summary>
internal static class Bzip2Crc
{
    private static readonly uint[] Table = MakeTable();

    private static uint[] MakeTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i << 24;
            for (int k = 0; k < 8; k++) c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
            t[i] = c;
        }
        return t;
    }

    /// <summary>Updates a running CRC that started at 0xFFFFFFFF; the final value is its complement.</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        uint[] t = Table;
        foreach (byte b in data) crc = (crc << 8) ^ t[(crc >> 24) ^ b];
        return crc;
    }

    public static uint UpdateRun(uint crc, byte b, int count)
    {
        uint[] t = Table;
        for (int i = 0; i < count; i++) crc = (crc << 8) ^ t[(crc >> 24) ^ b];
        return crc;
    }
}

internal static class Bzip2Format
{
    public const ulong BlockMagic = 0x314159265359, EndMagic = 0x177245385090;
    public const int MaxGroups = 6, MaxAlpha = 258, MaxCodeLen = 20, GroupSize = 50, MaxSelectors = 18002;

    public static bool IsBzip2(ReadOnlySpan<byte> head) =>
        head.Length >= 10 && head[0] == 'B' && head[1] == 'Z' && head[2] == 'h' && head[3] is >= (byte)'1' and <= (byte)'9'
        && (head.Slice(4, 6).SequenceEqual(stackalloc byte[] { 0x31, 0x41, 0x59, 0x26, 0x53, 0x59 })
            || head.Slice(4, 6).SequenceEqual(stackalloc byte[] { 0x17, 0x72, 0x45, 0x38, 0x50, 0x90 }));
}

/// <summary>
/// Read-only stream that unpacks bzip2 data, including several streams in a row as pbzip2 and lbzip2
/// write them. Every block and stream CRC is checked.
/// </summary>
public sealed class Bzip2InputStream : Stream
{
    private readonly Stream _input;
    private readonly bool _leaveOpen;
    private readonly byte[] _inBuf = new byte[1 << 16];
    private int _inPos, _inLen;
    private ulong _bits;
    private int _bitCount;

    private int _blockSizeMax;
    private bool _inStream, _finished;
    private uint _combinedCrc;
    private int _streams;

    // The current block: the inverse BWT vector and the state of the final run-length decoding.
    private int[] _tt = Array.Empty<int>();
    private int _blockLength, _blockDone, _tPos;
    private uint _blockCrc, _expectedBlockCrc;
    private int _lastByte = -1, _runLength;
    private int _pendingRun;
    private byte _pendingByte;

    // Decoding tables, reused between blocks.
    private readonly byte[] _selector = new byte[Bzip2Format.MaxSelectors];
    private readonly byte[,] _len = new byte[Bzip2Format.MaxGroups, Bzip2Format.MaxAlpha];
    private readonly int[,] _limit = new int[Bzip2Format.MaxGroups, Bzip2Format.MaxCodeLen + 2];
    private readonly int[,] _base = new int[Bzip2Format.MaxGroups, Bzip2Format.MaxCodeLen + 2];
    private readonly int[,] _perm = new int[Bzip2Format.MaxGroups, Bzip2Format.MaxAlpha];
    private readonly int[] _minLen = new int[Bzip2Format.MaxGroups];

    public Bzip2InputStream(Stream input, bool leaveOpen = false)
    {
        _input = input;
        _leaveOpen = leaveOpen;
    }

    /// <summary>Number of bzip2 streams read so far.</summary>
    public int Streams => _streams;

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

    // ------------------------------------------------------------ bits

    private bool FillByte()
    {
        if (_inPos == _inLen)
        {
            _inLen = _input.Read(_inBuf, 0, _inBuf.Length);
            _inPos = 0;
            if (_inLen <= 0) { _inLen = 0; return false; }
        }
        _bits = (_bits << 8) | _inBuf[_inPos++];
        _bitCount += 8;
        return true;
    }

    private uint Bits(int n)
    {
        while (_bitCount < n)
            if (!FillByte()) throw new FsException("Corrupt bzip2 data: unexpected end of file.");
        _bitCount -= n;
        return (uint)(_bits >> _bitCount) & (uint)((1UL << n) - 1);
    }

    private bool Bit() => Bits(1) != 0;

    /// <summary>Reads the next byte of a new stream, or -1 at the end of the input.</summary>
    private int NextAlignedByte()
    {
        _bitCount -= _bitCount % 8;           // streams start on a byte boundary
        if (_bitCount >= 8) return (int)Bits(8);
        return FillByte() ? (int)Bits(8) : -1;
    }

    // ------------------------------------------------------------ structure

    private bool StartStream()
    {
        int b = NextAlignedByte();
        if (b < 0)
        {
            if (_streams == 0) throw new FsException("Corrupt bzip2 data: the file is empty.");
            return false;
        }
        int z = NextAlignedByte(), h = NextAlignedByte(), level = NextAlignedByte();
        if (b != 'B' || z != 'Z' || h != 'h' || level is < '1' or > '9')
        {
            // Data after the last stream is ignored, as the bzip2 tool does.
            if (_streams > 0) return false;
            throw new FsException("Not a bzip2 stream.");
        }
        _blockSizeMax = (level - '0') * 100000;
        if (_tt.Length < _blockSizeMax) _tt = new int[_blockSizeMax];
        _combinedCrc = 0;
        _inStream = true;
        _streams++;
        return true;
    }

    /// <summary>Reads the next block of the current stream; false at the end of the stream.</summary>
    private bool ReadBlock()
    {
        ulong magic = ((ulong)Bits(24) << 24) | Bits(24);
        uint crc = Bits(32);
        if (magic == Bzip2Format.EndMagic)
        {
            if (crc != _combinedCrc) throw new FsException("Corrupt bzip2 data: the stream CRC does not match.");
            _inStream = false;
            return false;
        }
        if (magic != Bzip2Format.BlockMagic) throw new FsException("Corrupt bzip2 data: bad block signature.");
        _expectedBlockCrc = crc;
        if (Bit()) throw new FsException("The bzip2 data uses randomised blocks (bzip2 0.9.0 and older), which are not supported.");
        int origPtr = (int)Bits(24);

        // Symbols in use.
        Span<byte> seqToUnseq = stackalloc byte[256];
        int nInUse = 0;
        uint used16 = Bits(16);
        for (int i = 0; i < 16; i++)
        {
            if ((used16 & (0x8000u >> i)) == 0) continue;
            uint used = Bits(16);
            for (int j = 0; j < 16; j++)
                if ((used & (0x8000u >> j)) != 0) seqToUnseq[nInUse++] = (byte)(i * 16 + j);
        }
        if (nInUse == 0) throw new FsException("Corrupt bzip2 data: a block uses no symbols.");
        int alphaSize = nInUse + 2;

        int nGroups = (int)Bits(3);
        if (nGroups is < 2 or > 6) throw new FsException("Corrupt bzip2 data: bad number of Huffman tables.");
        int nSelectors = (int)Bits(15);
        if (nSelectors < 1) throw new FsException("Corrupt bzip2 data: no selectors.");
        Span<byte> mtfGroups = stackalloc byte[6];
        for (int i = 0; i < nGroups; i++) mtfGroups[i] = (byte)i;
        int kept = Math.Min(nSelectors, Bzip2Format.MaxSelectors);
        for (int i = 0; i < nSelectors; i++)
        {
            int j = 0;
            while (Bit())
                if (++j >= nGroups) throw new FsException("Corrupt bzip2 data: bad selector.");
            byte v = mtfGroups[j];
            for (; j > 0; j--) mtfGroups[j] = mtfGroups[j - 1];
            mtfGroups[0] = v;
            if (i < kept) _selector[i] = v;
        }
        nSelectors = kept;

        // Code lengths and decoding tables.
        for (int t = 0; t < nGroups; t++)
        {
            int curr = (int)Bits(5);
            for (int i = 0; i < alphaSize; i++)
            {
                while (true)
                {
                    if (curr is < 1 or > Bzip2Format.MaxCodeLen) throw new FsException("Corrupt bzip2 data: bad code length.");
                    if (!Bit()) break;
                    curr += Bit() ? -1 : 1;
                }
                _len[t, i] = (byte)curr;
            }
            MakeDecodeTable(t, alphaSize);
        }

        // Huffman, run-length (RUNA/RUNB) and move-to-front decoding into byte counts and the BWT column.
        Span<int> unzftab = stackalloc int[256];
        unzftab.Clear();
        Span<byte> mtf = stackalloc byte[256];
        for (int i = 0; i < 256; i++) mtf[i] = (byte)i;
        int[] tt = _tt;
        int eob = nInUse + 1, nblock = 0, groupNo = -1, groupPos = 0, table = 0;
        int runLen = 0, runWeight = 1;

        while (true)
        {
            if (groupPos == 0)
            {
                if (++groupNo >= nSelectors) throw new FsException("Corrupt bzip2 data: too few selectors.");
                groupPos = Bzip2Format.GroupSize;
                table = _selector[groupNo];
            }
            groupPos--;
            int sym = DecodeSymbol(table, alphaSize);

            if (sym <= 1)
            {
                // RUNA = 1 x weight, RUNB = 2 x weight (bijective base 2).
                if (runWeight > 1 << 21) throw new FsException("Corrupt bzip2 data: run too long.");
                runLen += (sym + 1) * runWeight;
                runWeight <<= 1;
                continue;
            }
            if (runLen > 0)
            {
                byte b = seqToUnseq[mtf[0]];
                if (nblock + runLen > _blockSizeMax) throw new FsException("Corrupt bzip2 data: block too large.");
                unzftab[b] += runLen;
                for (int k = 0; k < runLen; k++) tt[nblock++] = b;
                runLen = 0;
                runWeight = 1;
            }
            if (sym == eob) break;
            {
                int idx = sym - 1;
                byte v = mtf[idx];
                mtf[..idx].CopyTo(mtf[1..]);
                mtf[0] = v;
                byte b = seqToUnseq[v];
                if (nblock >= _blockSizeMax) throw new FsException("Corrupt bzip2 data: block too large.");
                unzftab[b]++;
                tt[nblock++] = b;
            }
        }
        if (origPtr >= nblock) throw new FsException("Corrupt bzip2 data: bad origin pointer.");

        // Inverse BWT: tt[i] keeps the byte in its low 8 bits and the link to the next position above them.
        Span<int> cftab = stackalloc int[257];
        cftab[0] = 0;
        for (int i = 0; i < 256; i++) cftab[i + 1] = cftab[i] + unzftab[i];
        for (int i = 0; i < nblock; i++)
        {
            int b = tt[i] & 0xFF;
            tt[cftab[b]++] |= i << 8;
        }
        _tPos = tt[origPtr] >> 8;
        _blockLength = nblock;
        _blockDone = 0;
        _blockCrc = 0xFFFFFFFF;
        _lastByte = -1;
        _runLength = 0;
        return true;
    }

    private void MakeDecodeTable(int t, int alphaSize)
    {
        int min = 32, max = 0;
        for (int i = 0; i < alphaSize; i++)
        {
            min = Math.Min(min, _len[t, i]);
            max = Math.Max(max, _len[t, i]);
        }
        int pp = 0;
        for (int l = min; l <= max; l++)
            for (int s = 0; s < alphaSize; s++)
                if (_len[t, s] == l) _perm[t, pp++] = s;
        Span<int> count = stackalloc int[Bzip2Format.MaxCodeLen + 2];
        count.Clear();
        for (int s = 0; s < alphaSize; s++) count[_len[t, s]]++;
        int code = 0, idx = 0;
        for (int l = 0; l <= Bzip2Format.MaxCodeLen + 1; l++)
        {
            _base[t, l] = idx - code;      // perm index = code + base
            code += l <= Bzip2Format.MaxCodeLen ? count[l] : 0;
            idx += l <= Bzip2Format.MaxCodeLen ? count[l] : 0;
            _limit[t, l] = code - 1;        // largest code of this length
            code <<= 1;
        }
        _minLen[t] = min;
    }

    private int DecodeSymbol(int t, int alphaSize)
    {
        int len = _minLen[t];
        int code = (int)Bits(len);
        while (true)
        {
            if (len > Bzip2Format.MaxCodeLen) throw new FsException("Corrupt bzip2 data: bad Huffman code.");
            if (code <= _limit[t, len])
            {
                int i = code + _base[t, len];
                if (i < 0 || i >= alphaSize) throw new FsException("Corrupt bzip2 data: bad Huffman code.");
                return _perm[t, i];
            }
            code = (code << 1) | (int)Bits(1);
            len++;
        }
    }

    private void FinishBlock()
    {
        uint crc = ~_blockCrc;
        if (crc != _expectedBlockCrc) throw new FsException("Corrupt bzip2 data: a block CRC does not match.");
        _combinedCrc = ((_combinedCrc << 1) | (_combinedCrc >> 31)) ^ crc;
        _blockLength = 0;
        _blockDone = 0;
    }

    // ------------------------------------------------------------ output

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int written = 0;
        while (written < buffer.Length)
        {
            if (_pendingRun > 0)
            {
                int n = Math.Min(_pendingRun, buffer.Length - written);
                buffer.Slice(written, n).Fill(_pendingByte);
                _blockCrc = Bzip2Crc.UpdateRun(_blockCrc, _pendingByte, n);
                written += n;
                _pendingRun -= n;
                continue;
            }
            if (_blockDone < _blockLength)
            {
                written += Emit(buffer[written..]);
                continue;
            }
            if (_blockLength > 0) FinishBlock();
            if (_finished) break;
            if (!_inStream && !StartStream())
            {
                _finished = true;
                break;
            }
            ReadBlock();
        }
        return written;
    }

    /// <summary>
    /// Walks the inverse BWT and undoes the initial run-length encoding: after four equal bytes comes the
    /// number of further copies, which is left in <see cref="_pendingRun"/> for the caller.
    /// </summary>
    private int Emit(Span<byte> dst)
    {
        int[] tt = _tt;
        int n = 0, tPos = _tPos, done = _blockDone, total = _blockLength, last = _lastByte, run = _runLength;
        while (n < dst.Length && done < total)
        {
            int entry = tt[tPos];
            byte b = (byte)entry;
            tPos = entry >> 8;
            done++;
            if (run == 4)
            {
                run = 0;
                last = -1;
                if (b > 0)
                {
                    _pendingRun = b;
                    break;
                }
                continue;
            }
            dst[n++] = b;
            _pendingByte = b;
            if (b == last) run++;
            else { last = b; run = 1; }
        }
        _blockCrc = Bzip2Crc.Update(_blockCrc, dst[..n]);
        _tPos = tPos;
        _blockDone = done;
        _lastByte = last;
        _runLength = run;
        return n;
    }
}

/// <summary>
/// Write-only stream that produces one bzip2 stream with blocks of 900 kB (as "bzip2 -9"). The blocks are
/// compressed in parallel; the result is a single stream that every bzip2 decoder reads.
/// </summary>
public sealed class Bzip2OutputStream : Stream
{
    private readonly Stream _output;
    private readonly bool _leaveOpen;
    private readonly int _workers;
    private readonly int _blockMax;
    private readonly Queue<Task<Bzip2BlockEncoder.Result>> _pending = new();
    private readonly Stack<Bzip2BlockEncoder> _encoders = new();
    private byte[] _block;
    private int _fill;
    private int _runByte = -1, _runLength;
    private uint _combined;
    private bool _headerWritten, _disposed;

    // Bit writer for the stream.
    private ulong _bits;
    private int _bitCount;
    private readonly byte[] _outBuf = new byte[1 << 16];
    private int _outLen;

    /// <param name="level">1-9: the block size in units of 100 kB.</param>
    /// <param name="threads">Worker threads; 0 means one per processor.</param>
    public Bzip2OutputStream(Stream output, int level = 9, int threads = 0, bool leaveOpen = false)
    {
        _output = output;
        _leaveOpen = leaveOpen;
        _level = Math.Clamp(level, 1, 9);
        _blockMax = _level * 100000 - 19;
        _workers = threads > 0 ? threads : Environment.ProcessorCount;
        _block = new byte[_blockMax + 8];
    }

    private readonly int _level;

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
        // The initial run-length encoding: runs of 4 to 255 equal bytes become four bytes and a count.
        foreach (byte b in buffer)
        {
            if (b == _runByte && _runLength < 255)
            {
                _runLength++;
                continue;
            }
            FlushRun();
            _runByte = b;
            _runLength = 1;
        }
    }

    /// <summary>Puts the current run into the block, closing the block first when it might not fit.</summary>
    private void FlushRun()
    {
        if (_runByte < 0) return;
        if (_fill + 5 > _blockMax) EndBlock();
        byte b = (byte)_runByte;
        if (_runLength < 4)
        {
            for (int k = 0; k < _runLength; k++) _block[_fill++] = b;
        }
        else
        {
            _block[_fill++] = b;
            _block[_fill++] = b;
            _block[_fill++] = b;
            _block[_fill++] = b;
            _block[_fill++] = (byte)(_runLength - 4);
        }
        _runByte = -1;
        _runLength = 0;
    }

    /// <summary>Hands the block to a worker; its CRC covers the original bytes, recovered from the run-length encoded ones.</summary>
    private void EndBlock()
    {
        if (_fill == 0) return;
        uint crc = BlockCrc(_block.AsSpan(0, _fill));
        byte[] data = _block;
        int length = _fill;
        _block = new byte[_blockMax + 8];
        _fill = 0;
        while (_pending.Count >= _workers) Drain(1);
        _pending.Enqueue(Task.Run(() => EncodeBlock(data, length, crc)));
    }

    private static uint BlockCrc(ReadOnlySpan<byte> rle)
    {
        uint crc = 0xFFFFFFFF;
        int run = 0, last = -1, start = 0;
        for (int i = 0; i < rle.Length; i++)
        {
            byte b = rle[i];
            if (run == 4)
            {
                crc = Bzip2Crc.Update(crc, rle[start..i]);
                crc = Bzip2Crc.UpdateRun(crc, (byte)last, b);
                start = i + 1;
                run = 0;
                last = -1;
                continue;
            }
            if (b == last) run++;
            else { last = b; run = 1; }
        }
        crc = Bzip2Crc.Update(crc, rle[start..]);
        return ~crc;
    }

    private Bzip2BlockEncoder.Result EncodeBlock(byte[] data, int length, uint crc)
    {
        Bzip2BlockEncoder? enc;
        lock (_encoders) _encoders.TryPop(out enc);
        enc ??= new Bzip2BlockEncoder(_level * 100000);
        try
        {
            return enc.Encode(data.AsSpan(0, length), crc);
        }
        finally
        {
            lock (_encoders) _encoders.Push(enc);
        }
    }

    private void Drain(int count)
    {
        for (int i = 0; i < count && _pending.Count > 0; i++)
        {
            Bzip2BlockEncoder.Result r = _pending.Dequeue().GetAwaiter().GetResult();
            WriteHeader();
            _combined = ((_combined << 1) | (_combined >> 31)) ^ r.Crc;
            AppendBits(r.Data, r.BitLength);
        }
    }

    private void WriteHeader()
    {
        if (_headerWritten) return;
        _headerWritten = true;
        PutBits(8, 'B');
        PutBits(8, 'Z');
        PutBits(8, 'h');
        PutBits(8, (uint)('0' + _level));
    }

    private void PutBits(int n, uint v)
    {
        _bits = (_bits << n) | (v & ((1u << n) - 1));
        _bitCount += n;
        while (_bitCount >= 8)
        {
            _bitCount -= 8;
            _outBuf[_outLen++] = (byte)(_bits >> _bitCount);
            if (_outLen == _outBuf.Length) { _output.Write(_outBuf, 0, _outLen); _outLen = 0; }
        }
    }

    private void AppendBits(byte[] data, long bitLength)
    {
        long whole = bitLength / 8;
        if (_bitCount == 0)
        {
            if (_outLen > 0) { _output.Write(_outBuf, 0, _outLen); _outLen = 0; }
            _output.Write(data, 0, (int)whole);
        }
        else
        {
            for (long i = 0; i < whole; i++) PutBits(8, data[i]);
        }
        int rest = (int)(bitLength % 8);
        if (rest > 0) PutBits(rest, (uint)(data[whole] >> (8 - rest)));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            FlushRun();
            EndBlock();
            Drain(int.MaxValue);
            WriteHeader();
            PutBits(24, (uint)(Bzip2Format.EndMagic >> 24));
            PutBits(24, (uint)(Bzip2Format.EndMagic & 0xFFFFFF));
            PutBits(16, _combined >> 16);
            PutBits(16, _combined & 0xFFFF);
            if (_bitCount > 0) PutBits(8 - _bitCount, 0);
            if (_outLen > 0) _output.Write(_outBuf, 0, _outLen);
            _output.Flush();
            if (!_leaveOpen) _output.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Compresses one block (already run-length encoded): BWT, move-to-front, Huffman coding.</summary>
internal sealed class Bzip2BlockEncoder
{
    public readonly record struct Result(byte[] Data, long BitLength, uint Crc);

    private readonly int[] _p, _c, _pn, _cn, _cnt;
    private readonly byte[] _bwt;
    private readonly ushort[] _mtfv;

    public Bzip2BlockEncoder(int blockSize)
    {
        _p = new int[blockSize];
        _c = new int[blockSize];
        _pn = new int[blockSize];
        _cn = new int[blockSize];
        _cnt = new int[Math.Max(256, blockSize)];
        _bwt = new byte[blockSize];
        _mtfv = new ushort[blockSize + 1];
    }

    // Bit writer.
    private byte[] _out = Array.Empty<byte>();
    private long _outBits;
    private ulong _acc;
    private int _accBits;

    private void Put(int n, uint v)
    {
        _acc = (_acc << n) | (v & (uint)((1UL << n) - 1));
        _accBits += n;
        while (_accBits >= 8)
        {
            _accBits -= 8;
            int idx = (int)(_outBits >> 3);
            if (idx >= _out.Length) Array.Resize(ref _out, _out.Length * 2);
            _out[idx] = (byte)(_acc >> _accBits);
            _outBits += 8;
        }
    }

    public Result Encode(ReadOnlySpan<byte> block, uint crc)
    {
        int n = block.Length;
        int origPtr = SortRotations(block);
        _out = new byte[n / 2 + 1024];
        _outBits = 0;
        _acc = 0;
        _accBits = 0;

        Put(24, (uint)(Bzip2Format.BlockMagic >> 24));
        Put(24, (uint)(Bzip2Format.BlockMagic & 0xFFFFFF));
        Put(16, crc >> 16);
        Put(16, crc & 0xFFFF);
        Put(1, 0);                     // not randomised
        Put(24, (uint)origPtr);

        // Symbols in use.
        Span<bool> inUse = stackalloc bool[256];
        inUse.Clear();
        for (int i = 0; i < n; i++) inUse[_bwt[i]] = true;
        Span<byte> unseqToSeq = stackalloc byte[256];
        int nInUse = 0;
        for (int i = 0; i < 256; i++) if (inUse[i]) unseqToSeq[i] = (byte)nInUse++;
        uint used16 = 0;
        for (int i = 0; i < 16; i++)
            for (int j = 0; j < 16; j++)
                if (inUse[i * 16 + j]) { used16 |= 0x8000u >> i; break; }
        Put(16, used16);
        for (int i = 0; i < 16; i++)
        {
            if ((used16 & (0x8000u >> i)) == 0) continue;
            uint u = 0;
            for (int j = 0; j < 16; j++) if (inUse[i * 16 + j]) u |= 0x8000u >> j;
            Put(16, u);
        }

        // Move-to-front and the run-length coding of zeros (RUNA = 0, RUNB = 1).
        int alphaSize = nInUse + 2, eob = nInUse + 1;
        Span<int> freq = stackalloc int[Bzip2Format.MaxAlpha];
        freq.Clear();
        Span<byte> mtf = stackalloc byte[256];
        for (int i = 0; i < 256; i++) mtf[i] = (byte)i;
        ushort[] mtfv = _mtfv;
        int nMtf = 0, zeros = 0;
        void PutRun(Span<int> f, ref int count, ref int z)
        {
            z--;
            while (true)
            {
                ushort s = (ushort)(z & 1);
                mtfv[count++] = s;
                f[s]++;
                if (z < 2) break;
                z = (z - 2) >> 1;
            }
            z = 0;
        }
        for (int i = 0; i < n; i++)
        {
            byte v = unseqToSeq[_bwt[i]];
            if (mtf[0] == v) { zeros++; continue; }
            if (zeros > 0) PutRun(freq, ref nMtf, ref zeros);
            int j = 1;
            while (mtf[j] != v) j++;
            mtf[..j].CopyTo(mtf[1..]);
            mtf[0] = v;
            mtfv[nMtf++] = (ushort)(j + 1);
            freq[j + 1]++;
        }
        if (zeros > 0) PutRun(freq, ref nMtf, ref zeros);
        mtfv[nMtf++] = (ushort)eob;
        freq[eob]++;

        EncodeHuffman(mtfv, nMtf, alphaSize, freq);

        // Flush partial byte into the buffer, keeping the exact bit length.
        long bits = _outBits + _accBits;
        if (_accBits > 0)
        {
            int idx = (int)(_outBits >> 3);
            if (idx >= _out.Length) Array.Resize(ref _out, _out.Length + 16);
            _out[idx] = (byte)(_acc << (8 - _accBits));
        }
        return new Result(_out, bits, crc);
    }

    /// <summary>Sorts the cyclic rotations of the block (prefix doubling) and fills the BWT column; returns the row of rotation 0.</summary>
    private int SortRotations(ReadOnlySpan<byte> s)
    {
        int n = s.Length;
        int[] p = _p, c = _c, pn = _pn, cn = _cn, cnt = _cnt;
        Array.Clear(cnt, 0, 256);
        for (int i = 0; i < n; i++) cnt[s[i]]++;
        for (int i = 1; i < 256; i++) cnt[i] += cnt[i - 1];
        for (int i = n - 1; i >= 0; i--) p[--cnt[s[i]]] = i;
        c[p[0]] = 0;
        int classes = 1;
        for (int i = 1; i < n; i++)
        {
            if (s[p[i]] != s[p[i - 1]]) classes++;
            c[p[i]] = classes - 1;
        }
        for (int h = 1; h < n && classes < n; h <<= 1)
        {
            for (int i = 0; i < n; i++)
            {
                int v = p[i] - h;
                pn[i] = v < 0 ? v + n : v;
            }
            Array.Clear(cnt, 0, classes);
            for (int i = 0; i < n; i++) cnt[c[pn[i]]]++;
            for (int i = 1; i < classes; i++) cnt[i] += cnt[i - 1];
            for (int i = n - 1; i >= 0; i--) p[--cnt[c[pn[i]]]] = pn[i];
            cn[p[0]] = 0;
            int k = 1;
            for (int i = 1; i < n; i++)
            {
                int a = p[i], b = p[i - 1];
                int a2 = a + h, b2 = b + h;
                if (a2 >= n) a2 -= n;
                if (b2 >= n) b2 -= n;
                if (c[a] != c[b] || c[a2] != c[b2]) k++;
                cn[a] = k - 1;
            }
            Array.Copy(cn, c, n);
            classes = k;
        }
        int orig = 0;
        for (int i = 0; i < n; i++)
        {
            int j = p[i];
            if (j == 0) orig = i;
            _bwt[i] = s[j == 0 ? n - 1 : j - 1];
        }
        return orig;
    }

    private void EncodeHuffman(ushort[] mtfv, int nMtf, int alphaSize, ReadOnlySpan<int> freq)
    {
        int nGroups = nMtf < 200 ? 2 : nMtf < 600 ? 3 : nMtf < 1200 ? 4 : nMtf < 2400 ? 5 : 6;
        var len = new byte[nGroups, Bzip2Format.MaxAlpha];

        // Initial tables: split the symbol range into parts of about equal frequency.
        {
            int remaining = nMtf, gs = 0;
            for (int part = nGroups; part > 0; part--)
            {
                int target = remaining / part, ge = gs - 1, acc = 0;
                while (acc < target && ge < alphaSize - 1) acc += freq[++ge];
                if (ge > gs && part != nGroups && part != 1 && ((nGroups - part) % 2 == 1)) acc -= freq[ge--];
                for (int v = 0; v < alphaSize; v++) len[part - 1, v] = (byte)(v >= gs && v <= ge ? 0 : 15);
                gs = ge + 1;
                remaining -= acc;
            }
        }

        int nSelectors = (nMtf + Bzip2Format.GroupSize - 1) / Bzip2Format.GroupSize;
        var selector = new byte[nSelectors];
        var rfreq = new int[nGroups, Bzip2Format.MaxAlpha];
        Span<int> cost = stackalloc int[6];
        for (int iter = 0; iter < 4; iter++)
        {
            Array.Clear(rfreq);
            int sel = 0;
            for (int gs = 0; gs < nMtf; gs += Bzip2Format.GroupSize)
            {
                int ge = Math.Min(gs + Bzip2Format.GroupSize, nMtf);
                cost.Clear();
                for (int i = gs; i < ge; i++)
                {
                    int s = mtfv[i];
                    for (int t = 0; t < nGroups; t++) cost[t] += len[t, s];
                }
                int best = 0;
                for (int t = 1; t < nGroups; t++) if (cost[t] < cost[best]) best = t;
                selector[sel++] = (byte)best;
                for (int i = gs; i < ge; i++) rfreq[best, mtfv[i]]++;
            }
            for (int t = 0; t < nGroups; t++) MakeCodeLengths(rfreq, t, len, alphaSize, 17);
        }

        // Canonical codes.
        var code = new uint[nGroups, Bzip2Format.MaxAlpha];
        for (int t = 0; t < nGroups; t++)
        {
            uint c = 0;
            for (int l = 1; l <= Bzip2Format.MaxCodeLen; l++)
            {
                for (int s = 0; s < alphaSize; s++)
                    if (len[t, s] == l) code[t, s] = c++;
                c <<= 1;
            }
        }

        Put(3, (uint)nGroups);
        Put(15, (uint)nSelectors);
        Span<byte> pos = stackalloc byte[6];
        for (int i = 0; i < nGroups; i++) pos[i] = (byte)i;
        for (int i = 0; i < nSelectors; i++)
        {
            byte v = selector[i];
            int j = 0;
            while (pos[j] != v) j++;
            pos[..j].CopyTo(pos[1..]);
            pos[0] = v;
            for (int k = 0; k < j; k++) Put(1, 1);
            Put(1, 0);
        }
        for (int t = 0; t < nGroups; t++)
        {
            int curr = len[t, 0];
            Put(5, (uint)curr);
            for (int s = 0; s < alphaSize; s++)
            {
                while (curr < len[t, s]) { Put(2, 2); curr++; }
                while (curr > len[t, s]) { Put(2, 3); curr--; }
                Put(1, 0);
            }
        }
        int g = 0;
        for (int gs = 0; gs < nMtf; gs += Bzip2Format.GroupSize, g++)
        {
            int ge = Math.Min(gs + Bzip2Format.GroupSize, nMtf);
            int t = selector[g];
            for (int i = gs; i < ge; i++)
            {
                int s = mtfv[i];
                Put(len[t, s], code[t, s]);
            }
        }
    }

    /// <summary>Huffman code lengths of at most <paramref name="maxLen"/> bits; rare symbols are given weight 1 as bzip2 does.</summary>
    private static void MakeCodeLengths(int[,] freqs, int t, byte[,] len, int alphaSize, int maxLen)
    {
        var weight = new long[alphaSize];
        for (int i = 0; i < alphaSize; i++) weight[i] = Math.Max(1, freqs[t, i]);
        var heap = new PriorityQueue<int, long>();
        var parent = new int[alphaSize * 2];
        var w = new long[alphaSize * 2];
        while (true)
        {
            heap.Clear();
            for (int i = 0; i < alphaSize; i++) { w[i] = weight[i]; heap.Enqueue(i, w[i]); }
            int next = alphaSize;
            while (heap.Count > 1)
            {
                heap.TryDequeue(out int a, out long wa);
                heap.TryDequeue(out int b, out long wb);
                w[next] = wa + wb;
                parent[a] = next;
                parent[b] = next;
                heap.Enqueue(next, w[next]);
                next++;
            }
            int root = next - 1;
            bool tooLong = false;
            for (int i = 0; i < alphaSize; i++)
            {
                int d = 0;
                for (int k = i; k != root; k = parent[k]) d++;
                if (d > maxLen) tooLong = true;
                len[t, i] = (byte)Math.Max(1, d);
            }
            if (!tooLong) return;
            for (int i = 0; i < alphaSize; i++) weight[i] = 1 + weight[i] / 2;
        }
    }
}
