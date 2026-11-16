using System.Numerics;
using System.Runtime.CompilerServices;

namespace FsForge.Core.Compression;

/// <summary>Tunables of the LZMA2 encoder.</summary>
public sealed record LzmaEncoderSettings(int Lc = 3, int Lp = 0, int Pb = 2, int NiceLength = 64, int Depth = 0)
{
    /// <summary>Preset by effort: 0 = fast, 1 = normal, 2 = maximum.</summary>
    public static LzmaEncoderSettings FromEffort(int effort) => effort switch
    {
        <= 0 => new LzmaEncoderSettings(NiceLength: 24, Depth: 12),
        1 => new LzmaEncoderSettings(NiceLength: 64, Depth: 48),
        _ => new LzmaEncoderSettings(NiceLength: 273, Depth: 256),
    };

    public int PropsByte => (Pb * 5 + Lp) * 9 + Lc;
}

/// <summary>
/// LZMA2 encoder for in-memory blocks: binary-tree match finder (BT4) and a price based
/// optimal parser. The whole input is the dictionary, so the block size is limited only by memory.
/// The output is a raw LZMA2 stream made of compressed chunks and terminated with the end marker.
/// </summary>
internal sealed class Lzma2Encoder
{
    private const int NumStates = 12;
    private const ushort ProbInit = 1024;
    private const int MatchMax = 273;
    private const int LenSymbols = 272;
    private const int OptsMax = 2048;
    private const uint Infinity = 1u << 30;
    private const int ChunkCompressedMax = 1 << 16;
    private const int ChunkUncompressedMax = 1 << 21;
    private const int SymbolMargin = 96;       // upper bound of bytes one symbol can add to the range coder

    private static readonly byte[] LitNext = { 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 4, 5 };
    private static readonly byte[] MatchNext = { 7, 7, 7, 7, 7, 7, 7, 10, 10, 10, 10, 10 };
    private static readonly byte[] RepNext = { 8, 8, 8, 8, 8, 8, 8, 11, 11, 11, 11, 11 };
    private static readonly byte[] ShortRepNext = { 9, 9, 9, 9, 9, 9, 9, 11, 11, 11, 11, 11 };
    private static readonly uint[] ProbPrices = BuildProbPrices();

    private readonly LzmaEncoderSettings _settings;
    private readonly int _lc, _lp, _pb, _niceLen, _depth;
    private readonly uint _pbMask, _lpMask;

    // Probability model.
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

    // Price tables.
    private readonly uint[] _lenPrices;        // [posState * 272 + len - 2]
    private readonly uint[] _repLenPrices;
    private readonly uint[] _slotPrices = new uint[4 << 6];
    private readonly uint[] _distPrices = new uint[4 << 7];
    private readonly uint[] _alignPrices = new uint[16];
    private readonly uint[] _highTmp = new uint[256];
    private int _sincePriceUpdate;

    // Encoder state.
    private int _state;
    private uint _rep0, _rep1, _rep2, _rep3;

    // Input and match finder.
    private byte[] _buf = Array.Empty<byte>();
    private int _n;
    private readonly int[] _hash2 = new int[1 << 16];
    private readonly int[] _hash3 = new int[1 << 16];
    private int[] _hash4 = Array.Empty<int>();
    private int _hash4Shift;
    private int[] _son = Array.Empty<int>();
    private readonly int[] _mLen = new int[MatchMax + 2];
    private readonly int[] _mDist = new int[MatchMax + 2];
    private int _mCount;
    private int _mPos;          // position the cached matches belong to
    private int _mfNext;        // first position not yet inserted into the match finder
    private int _dictLimit = int.MaxValue;

    // Optimal parser nodes.
    private readonly uint[] _oPrice = new uint[OptsMax + MatchMax + 4];
    private readonly int[] _oPrev = new int[OptsMax + MatchMax + 4];
    private readonly int[] _oBack = new int[OptsMax + MatchMax + 4];
    private readonly byte[] _oState = new byte[OptsMax + MatchMax + 4];
    private readonly uint[] _oReps = new uint[(OptsMax + MatchMax + 4) * 4];

    // Pending symbols produced by the parser.
    private readonly int[] _symLen = new int[OptsMax + 4];
    private readonly int[] _symBack = new int[OptsMax + 4];
    private int _symCount, _symIndex;

    // Range encoder.
    private byte[] _out = Array.Empty<byte>();
    private int _outPos;
    private ulong _low;
    private uint _range;
    private byte _cache;
    private long _cacheSize;

    public Lzma2Encoder(LzmaEncoderSettings settings)
    {
        if (settings.Lc < 0 || settings.Lp < 0 || settings.Lc + settings.Lp > 4 || settings.Pb is < 0 or > 4)
            throw new ArgumentException("Invalid lc/lp/pb combination.");
        _settings = settings;
        _lc = settings.Lc;
        _lp = settings.Lp;
        _pb = settings.Pb;
        _pbMask = (1u << _pb) - 1;
        _lpMask = (1u << _lp) - 1;
        _niceLen = Math.Clamp(settings.NiceLength, 8, MatchMax);
        _depth = settings.Depth > 0 ? settings.Depth : 16 + _niceLen / 2;
        _literal = new ushort[0x300 << (_lc + _lp)];
        _lenPrices = new uint[(1 << _pb) * LenSymbols];
        _repLenPrices = new uint[(1 << _pb) * LenSymbols];
    }

    public int PropsByte => _settings.PropsByte;

    private static uint[] BuildProbPrices()
    {
        var prices = new uint[2048 >> 4];
        for (int i = 8; i < 2048; i += 16)
        {
            uint w = (uint)i;
            uint bitCount = 0;
            for (int j = 0; j < 4; j++)
            {
                w *= w;
                bitCount <<= 1;
                while (w >= 1u << 16)
                {
                    w >>= 1;
                    bitCount++;
                }
            }
            prices[i >> 4] = (11u << 4) - 15 - bitCount;
        }
        return prices;
    }

    // ------------------------------------------------------------ public API

    /// <summary>
    /// Compresses <paramref name="src"/> into a raw LZMA2 stream.
    /// Returns the number of bytes written, or -1 when the result would not fit into <paramref name="dst"/>.
    /// </summary>
    /// <param name="dictionarySize">Largest match distance the decoder is guaranteed to support.</param>
    public int Encode(ReadOnlySpan<byte> src, byte[] dst, int dstOffset, int dstCapacity, int dictionarySize = int.MaxValue)
    {
        int n = src.Length;
        _dictLimit = Math.Max(dictionarySize, 4096);
        int limit = dstOffset + dstCapacity;
        if (n == 0)
        {
            if (dstCapacity < 1) return -1;
            dst[dstOffset] = 0;
            return 1;
        }

        Prepare(src);
        _out = dst;
        int outPos = dstOffset;
        int pos = 0;
        bool first = true;

        while (pos < n)
        {
            int headerSize = first ? 6 : 5;
            int chunkStart = outPos;
            int dataStart = chunkStart + headerSize;
            if (dataStart + 16 > limit) return -1;

            _outPos = dataStart;
            _low = 0;
            _range = 0xFFFFFFFF;
            _cache = 0;
            _cacheSize = 1;
            int chunkIn = pos;

            while (pos < n)
            {
                long pending = _outPos - dataStart + _cacheSize + 4;
                if (pending + SymbolMargin > ChunkCompressedMax) break;
                if (pos - chunkIn + MatchMax > ChunkUncompressedMax) break;
                if (_outPos + _cacheSize + SymbolMargin + 8 > limit) return -1;

                if (_symIndex == _symCount) Parse(pos);
                int len = _symLen[_symIndex];
                int back = _symBack[_symIndex];
                _symIndex++;
                EncodeSymbol(pos, len, back);
                pos += len;
            }

            for (int i = 0; i < 5; i++) ShiftLow();
            int compressed = _outPos - dataStart;
            int uncompressed = pos - chunkIn;
            if (compressed > ChunkCompressedMax || _outPos > limit) return -1;

            // Control byte: 0x80 = LZMA chunk; bits 5-6: 3 = reset dictionary, state and properties.
            int control = 0x80 | (first ? 0x60 : 0x00) | ((uncompressed - 1) >> 16);
            dst[chunkStart] = (byte)control;
            dst[chunkStart + 1] = (byte)((uncompressed - 1) >> 8);
            dst[chunkStart + 2] = (byte)(uncompressed - 1);
            dst[chunkStart + 3] = (byte)((compressed - 1) >> 8);
            dst[chunkStart + 4] = (byte)(compressed - 1);
            if (first) dst[chunkStart + 5] = (byte)PropsByte;
            outPos = _outPos;
            first = false;
        }

        if (outPos + 1 > limit) return -1;
        dst[outPos++] = 0;      // end of the LZMA2 stream
        _out = Array.Empty<byte>();
        return outPos - dstOffset;
    }

    private byte[] _micro = Array.Empty<byte>();

    /// <summary>
    /// Compresses the beginning of <paramref name="src"/> into one MicroLZMA stream of at most
    /// <paramref name="dst"/>.Length bytes: a raw LZMA stream without an end marker whose first byte holds
    /// the bitwise negation of the properties byte. As much input as fits is taken, symbol by symbol.
    /// </summary>
    /// <param name="consumed">Number of input bytes the stream stands for.</param>
    /// <returns>The compressed size, or -1 when not even the first symbol fits.</returns>
    public int EncodeMicroLzma(ReadOnlySpan<byte> src, Span<byte> dst, int dictionarySize, out int consumed)
    {
        consumed = 0;
        int n = src.Length, capacity = dst.Length;
        if (n == 0 || capacity < 6) return -1;
        _dictLimit = Math.Max(dictionarySize, 4096);
        Prepare(src);
        if (_micro.Length < capacity + SymbolMargin + 64) _micro = new byte[capacity + SymbolMargin + 64];
        _out = _micro;
        _outPos = 0;
        _low = 0;
        _range = 0xFFFFFFFF;
        _cache = 0;
        _cacheSize = 1;

        int pos = 0;
        while (pos < n)
        {
            // The state of the range coder before the symbol: when the symbol turns out not to fit,
            // the stream ends here. The probability model needs no rollback, nothing is coded after it.
            int outPos = _outPos;
            ulong low = _low;
            uint range = _range;
            byte cache = _cache;
            long cacheSize = _cacheSize;

            if (_symIndex == _symCount) Parse(pos);
            int len = _symLen[_symIndex];
            int back = _symBack[_symIndex];
            _symIndex++;
            EncodeSymbol(pos, len, back);
            // Flushing writes what is pending plus four more bytes.
            if (_outPos + _cacheSize + 4 > capacity)
            {
                _outPos = outPos;
                _low = low;
                _range = range;
                _cache = cache;
                _cacheSize = cacheSize;
                break;
            }
            pos += len;
        }
        if (pos == 0)
        {
            _out = Array.Empty<byte>();
            return -1;
        }
        for (int i = 0; i < 5; i++) ShiftLow();
        int size = _outPos;
        _micro[0] = (byte)~PropsByte;
        _micro.AsSpan(0, size).CopyTo(dst);
        _out = Array.Empty<byte>();
        consumed = pos;
        return size;
    }

    private void Prepare(ReadOnlySpan<byte> src)
    {
        _n = src.Length;
        if (_buf.Length < _n + 8) _buf = new byte[Math.Max(_n + 8, 1 << 16)];
        src.CopyTo(_buf);
        Array.Clear(_buf, _n, 8);

        int bits = Math.Clamp(BitOperations.Log2((uint)Math.Max(_n, 2) - 1) + 1, 12, 22);
        if (_hash4.Length != 1 << bits) _hash4 = new int[1 << bits];
        _hash4Shift = 32 - bits;
        if (_son.Length < _n * 2) _son = new int[_n * 2];
        Array.Fill(_hash2, -1);
        Array.Fill(_hash3, -1);
        Array.Fill(_hash4, -1);
        _mPos = -1;
        _mfNext = 0;
        _mCount = 0;
        _symCount = _symIndex = 0;

        Array.Fill(_isMatch, ProbInit);
        Array.Fill(_isRep, ProbInit);
        Array.Fill(_isRepG0, ProbInit);
        Array.Fill(_isRepG1, ProbInit);
        Array.Fill(_isRepG2, ProbInit);
        Array.Fill(_isRep0Long, ProbInit);
        Array.Fill(_posSlot, ProbInit);
        Array.Fill(_posSpecial, ProbInit);
        Array.Fill(_align, ProbInit);
        Array.Fill(_lenChoice, ProbInit);
        Array.Fill(_lenLow, ProbInit);
        Array.Fill(_lenMid, ProbInit);
        Array.Fill(_lenHigh, ProbInit);
        Array.Fill(_repChoice, ProbInit);
        Array.Fill(_repLow, ProbInit);
        Array.Fill(_repMid, ProbInit);
        Array.Fill(_repHigh, ProbInit);
        Array.Fill(_literal, ProbInit);
        _state = 0;
        _rep0 = _rep1 = _rep2 = _rep3 = 0;
        UpdatePrices();
    }

    // ---------------------------------------------------------- range coder

    private void ShiftLow()
    {
        if ((uint)_low < 0xFF000000u || (int)(_low >> 32) != 0)
        {
            byte carry = (byte)(_low >> 32);
            byte temp = _cache;
            do
            {
                _out[_outPos++] = (byte)(temp + carry);
                temp = 0xFF;
            }
            while (--_cacheSize != 0);
            _cache = (byte)((uint)_low >> 24);
        }
        _cacheSize++;
        _low = (_low & 0x00FFFFFF) << 8;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Bit(ref ushort prob, uint bit)
    {
        uint p = prob;
        uint bound = (_range >> 11) * p;
        if (bit == 0)
        {
            _range = bound;
            prob = (ushort)(p + ((2048 - p) >> 5));
        }
        else
        {
            _low += bound;
            _range -= bound;
            prob = (ushort)(p - (p >> 5));
        }
        while (_range < 0x01000000)
        {
            _range <<= 8;
            ShiftLow();
        }
    }

    private void Direct(uint value, int bits)
    {
        for (int i = bits - 1; i >= 0; i--)
        {
            _range >>= 1;
            if (((value >> i) & 1) != 0) _low += _range;
            while (_range < 0x01000000)
            {
                _range <<= 8;
                ShiftLow();
            }
        }
    }

    private void Tree(ushort[] probs, int offset, int bits, uint symbol)
    {
        uint m = 1;
        for (int i = bits - 1; i >= 0; i--)
        {
            uint bit = (symbol >> i) & 1;
            Bit(ref probs[offset + (int)m], bit);
            m = (m << 1) | bit;
        }
    }

    private void ReverseTree(ushort[] probs, int offset, int bits, uint symbol)
    {
        uint m = 1;
        for (int i = 0; i < bits; i++)
        {
            uint bit = symbol & 1;
            symbol >>= 1;
            Bit(ref probs[offset + (int)m], bit);
            m = (m << 1) | bit;
        }
    }

    // --------------------------------------------------------------- prices

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Price0(ushort prob) => ProbPrices[prob >> 4];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Price1(ushort prob) => ProbPrices[(prob ^ 2047) >> 4];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Price(ushort prob, uint bit) => ProbPrices[(prob ^ ((0 - bit) & 2047)) >> 4];

    private static uint TreePrice(ushort[] probs, int offset, int bits, uint symbol)
    {
        uint price = 0, m = 1;
        for (int i = bits - 1; i >= 0; i--)
        {
            uint bit = (symbol >> i) & 1;
            price += Price(probs[offset + (int)m], bit);
            m = (m << 1) | bit;
        }
        return price;
    }

    private static uint ReverseTreePrice(ushort[] probs, int offset, int bits, uint symbol)
    {
        uint price = 0, m = 1;
        for (int i = 0; i < bits; i++)
        {
            uint bit = symbol & 1;
            symbol >>= 1;
            price += Price(probs[offset + (int)m], bit);
            m = (m << 1) | bit;
        }
        return price;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DistSlot(uint dist)
    {
        if (dist < 4) return (int)dist;
        int n = BitOperations.Log2(dist);
        return (n << 1) + (int)((dist >> (n - 1)) & 1);
    }

    private void UpdateLenPrices(ushort[] choice, ushort[] low, ushort[] mid, ushort[] high, uint[] table)
    {
        uint a0 = Price0(choice[0]);
        uint a1 = Price1(choice[0]);
        uint b0 = a1 + Price0(choice[1]);
        uint b1 = a1 + Price1(choice[1]);
        for (uint i = 0; i < 256; i++) _highTmp[i] = b1 + TreePrice(high, 0, 8, i);
        int posStates = 1 << _pb;
        for (int ps = 0; ps < posStates; ps++)
        {
            int t = ps * LenSymbols;
            for (uint i = 0; i < 8; i++) table[t + (int)i] = a0 + TreePrice(low, ps << 3, 3, i);
            for (uint i = 0; i < 8; i++) table[t + 8 + (int)i] = b0 + TreePrice(mid, ps << 3, 3, i);
            Array.Copy(_highTmp, 0, table, t + 16, 256);
        }
    }

    private void UpdatePrices()
    {
        UpdateLenPrices(_lenChoice, _lenLow, _lenMid, _lenHigh, _lenPrices);
        UpdateLenPrices(_repChoice, _repLow, _repMid, _repHigh, _repLenPrices);

        for (int ls = 0; ls < 4; ls++)
        {
            for (uint slot = 0; slot < 64; slot++)
            {
                uint price = TreePrice(_posSlot, ls << 6, 6, slot);
                if (slot >= 14) price += (uint)(((int)(slot >> 1) - 1 - 4) << 4);
                _slotPrices[(ls << 6) + (int)slot] = price;
            }
            for (uint dist = 0; dist < 128; dist++)
            {
                int slot = DistSlot(dist);
                uint price = _slotPrices[(ls << 6) + slot];
                if (dist >= 4)
                {
                    int footer = (slot >> 1) - 1;
                    uint baseDist = (2u | ((uint)slot & 1)) << footer;
                    price += ReverseTreePrice(_posSpecial, (int)baseDist - slot - 1, footer, dist - baseDist);
                }
                _distPrices[(ls << 7) + (int)dist] = price;
            }
        }
        for (uint i = 0; i < 16; i++) _alignPrices[i] = ReverseTreePrice(_align, 0, 4, i);
        _sincePriceUpdate = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint DistPrice(uint dist, int lenState)
    {
        if (dist < 128) return _distPrices[(lenState << 7) + (int)dist];
        return _slotPrices[(lenState << 6) + DistSlot(dist)] + _alignPrices[dist & 15];
    }

    private uint LiteralPrice(int pos, int state, uint symbol, uint matchByte)
    {
        int prev = pos > 0 ? _buf[pos - 1] : 0;
        int litBase = 0x300 * ((int)(((uint)pos & _lpMask) << _lc) + (prev >> (8 - _lc)));
        ushort[] lit = _literal;
        uint price = 0;
        symbol |= 0x100;
        if (state < 7)
        {
            do
            {
                price += Price(lit[litBase + (int)(symbol >> 8)], (symbol >> 7) & 1);
                symbol <<= 1;
            }
            while (symbol < 0x10000);
        }
        else
        {
            uint offset = 0x100;
            do
            {
                matchByte <<= 1;
                uint matchBit = matchByte & offset;
                price += Price(lit[litBase + (int)(offset + matchBit + (symbol >> 8))], (symbol >> 7) & 1);
                symbol <<= 1;
                offset &= ~(matchByte ^ symbol);
            }
            while (symbol < 0x10000);
        }
        return price;
    }

    // --------------------------------------------------------- match finder

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int MatchLength(int a, int b, int start, int limit)
    {
        byte[] buf = _buf;
        int len = start;
        while (len < limit && buf[a + len] == buf[b + len]) len++;
        return len;
    }

    /// <summary>Makes sure the cached matches describe position <paramref name="pos"/>.</summary>
    private void EnsureMatches(int pos)
    {
        if (_mPos == pos) return;
        while (_mfNext < pos) InsertOnly(_mfNext++);
        FindMatches(pos);
        _mfNext = pos + 1;
        _mPos = pos;

        // The finder stops at the nice length; extend the longest match to its real length.
        if (_mCount > 0 && _mLen[_mCount - 1] == _niceLen)
        {
            int avail = Math.Min(_n - pos, MatchMax);
            int last = _mCount - 1;
            _mLen[last] = MatchLength(pos, pos - _mDist[last] - 1, _mLen[last], avail);
        }
    }

    private void FindMatches(int pos)
    {
        byte[] buf = _buf;
        _mCount = 0;
        int lenLimit = Math.Min(_n - pos, _niceLen);
        if (lenLimit < 4) return;

        uint b0 = buf[pos], b1 = buf[pos + 1], b2 = buf[pos + 2], b3 = buf[pos + 3];
        int h2 = (int)(b0 | (b1 << 8));
        uint v3 = b0 | (b1 << 8) | (b2 << 16);
        int h3 = (int)((v3 * 506832829u) >> 16);
        int h4 = (int)(((v3 | (b3 << 24)) * 2654435761u) >> _hash4Shift);

        int c2 = _hash2[h2];
        int c3 = _hash3[h3];
        int curMatch = _hash4[h4];
        _hash2[h2] = pos;
        _hash3[h3] = pos;
        _hash4[h4] = pos;
        int minPos = pos - _dictLimit;      // candidates below this are outside the dictionary
        if (c2 < minPos) c2 = -1;
        if (c3 < minPos) c3 = -1;

        int count = 0;
        int lenBest = 0;
        int candidate = -1;
        if (c2 >= 0)
        {
            lenBest = 2;
            _mLen[0] = 2;
            _mDist[0] = pos - c2 - 1;
            count = 1;
            candidate = c2;
        }
        if (c3 >= 0 && c3 != c2 && buf[c3] == b0 && buf[c3 + 1] == b1 && buf[c3 + 2] == b2)
        {
            lenBest = 3;
            _mLen[count] = 3;
            _mDist[count] = pos - c3 - 1;
            count++;
            candidate = c3;
        }
        if (count != 0)
        {
            lenBest = MatchLength(pos, candidate, lenBest, lenLimit);
            _mLen[count - 1] = lenBest;
            if (lenBest == lenLimit)
            {
                _mCount = count;
                InsertTree(pos, curMatch, lenLimit);
                return;
            }
        }
        if (lenBest < 3) lenBest = 3;

        int[] son = _son;
        int ptr0 = (pos << 1) + 1, ptr1 = pos << 1;
        int len0 = 0, len1 = 0;
        int depth = _depth;
        while (true)
        {
            if (curMatch < 0 || curMatch < minPos || depth-- == 0)
            {
                son[ptr0] = -1;
                son[ptr1] = -1;
                break;
            }
            int pair = curMatch << 1;
            int len = Math.Min(len0, len1);
            if (buf[curMatch + len] == buf[pos + len])
            {
                len = MatchLength(pos, curMatch, len + 1, lenLimit);
                if (lenBest < len)
                {
                    lenBest = len;
                    _mLen[count] = len;
                    _mDist[count] = pos - curMatch - 1;
                    count++;
                    if (len == lenLimit)
                    {
                        son[ptr1] = son[pair];
                        son[ptr0] = son[pair + 1];
                        break;
                    }
                }
                else if (len == lenLimit)
                {
                    son[ptr1] = son[pair];
                    son[ptr0] = son[pair + 1];
                    break;
                }
            }
            if (buf[curMatch + len] < buf[pos + len])
            {
                son[ptr1] = curMatch;
                ptr1 = pair + 1;
                curMatch = son[ptr1];
                len1 = len;
            }
            else
            {
                son[ptr0] = curMatch;
                ptr0 = pair;
                curMatch = son[ptr0];
                len0 = len;
            }
        }
        _mCount = count;
    }

    /// <summary>Inserts a position into the hash tables and the tree without collecting matches.</summary>
    private void InsertOnly(int pos)
    {
        byte[] buf = _buf;
        int lenLimit = Math.Min(_n - pos, _niceLen);
        if (lenLimit < 4) return;
        uint b0 = buf[pos], b1 = buf[pos + 1], b2 = buf[pos + 2], b3 = buf[pos + 3];
        int h2 = (int)(b0 | (b1 << 8));
        uint v3 = b0 | (b1 << 8) | (b2 << 16);
        int h3 = (int)((v3 * 506832829u) >> 16);
        int h4 = (int)(((v3 | (b3 << 24)) * 2654435761u) >> _hash4Shift);
        int curMatch = _hash4[h4];
        _hash2[h2] = pos;
        _hash3[h3] = pos;
        _hash4[h4] = pos;
        InsertTree(pos, curMatch, lenLimit);
    }

    private void InsertTree(int pos, int curMatch, int lenLimit)
    {
        byte[] buf = _buf;
        int[] son = _son;
        int ptr0 = (pos << 1) + 1, ptr1 = pos << 1;
        int len0 = 0, len1 = 0;
        int depth = _depth;
        int minPos = pos - _dictLimit;
        while (true)
        {
            if (curMatch < 0 || curMatch < minPos || depth-- == 0)
            {
                son[ptr0] = -1;
                son[ptr1] = -1;
                return;
            }
            int pair = curMatch << 1;
            int len = Math.Min(len0, len1);
            if (buf[curMatch + len] == buf[pos + len])
            {
                len = MatchLength(pos, curMatch, len + 1, lenLimit);
                if (len == lenLimit)
                {
                    son[ptr1] = son[pair];
                    son[ptr0] = son[pair + 1];
                    return;
                }
            }
            if (buf[curMatch + len] < buf[pos + len])
            {
                son[ptr1] = curMatch;
                ptr1 = pair + 1;
                curMatch = son[ptr1];
                len1 = len;
            }
            else
            {
                son[ptr0] = curMatch;
                ptr0 = pair;
                curMatch = son[ptr0];
                len0 = len;
            }
        }
    }

    // ------------------------------------------------------- optimal parser

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Relax(ref int lenEnd, int index, uint price, int prev, int back, byte state, uint r0, uint r1, uint r2, uint r3)
    {
        while (lenEnd < index) _oPrice[++lenEnd] = Infinity;
        if (price < _oPrice[index])
        {
            _oPrice[index] = price;
            _oPrev[index] = prev;
            _oBack[index] = back;
            _oState[index] = state;
            int r = index << 2;
            _oReps[r] = r0;
            _oReps[r + 1] = r1;
            _oReps[r + 2] = r2;
            _oReps[r + 3] = r3;
        }
    }

    /// <summary>
    /// Chooses the cheapest sequence of symbols starting at <paramref name="pos"/> and stores it
    /// in the pending symbol queue.
    /// </summary>
    private void Parse(int pos)
    {
        if (_sincePriceUpdate >= 64) UpdatePrices();

        byte[] buf = _buf;
        int n = _n;
        _symCount = 0;
        _symIndex = 0;

        _oPrice[0] = 0;
        _oState[0] = (byte)_state;
        _oReps[0] = _rep0;
        _oReps[1] = _rep1;
        _oReps[2] = _rep2;
        _oReps[3] = _rep3;
        int lenEnd = 0;
        int cur = 0;
        int longLen = 0, longBack = 0;

        while (true)
        {
            int p = pos + cur;
            EnsureMatches(p);

            uint basePrice = _oPrice[cur];
            int s = _oState[cur];
            int ri = cur << 2;
            uint r0 = _oReps[ri], r1 = _oReps[ri + 1], r2 = _oReps[ri + 2], r3 = _oReps[ri + 3];
            int avail = Math.Min(n - p, MatchMax);
            int posState = (int)((uint)p & _pbMask);
            int ims = (s << 4) + posState;
            uint curByte = buf[p];
            bool rep0Valid = r0 < (uint)p;
            uint matchByte = rep0Valid ? buf[p - (int)r0 - 1] : 0u;

            // Long matches are taken immediately, this also bounds the work per position.
            int bestRepLen = 0, bestRepIdx = 0;
            int repLen0 = 0, repLen1 = 0, repLen2 = 0, repLen3 = 0;
            if (avail >= 2)
            {
                if (rep0Valid) { repLen0 = MatchLength(p, p - (int)r0 - 1, 0, avail); if (repLen0 > bestRepLen) { bestRepLen = repLen0; bestRepIdx = 0; } }
                if (r1 < (uint)p) { repLen1 = MatchLength(p, p - (int)r1 - 1, 0, avail); if (repLen1 > bestRepLen) { bestRepLen = repLen1; bestRepIdx = 1; } }
                if (r2 < (uint)p) { repLen2 = MatchLength(p, p - (int)r2 - 1, 0, avail); if (repLen2 > bestRepLen) { bestRepLen = repLen2; bestRepIdx = 2; } }
                if (r3 < (uint)p) { repLen3 = MatchLength(p, p - (int)r3 - 1, 0, avail); if (repLen3 > bestRepLen) { bestRepLen = repLen3; bestRepIdx = 3; } }
            }
            int mainLen = _mCount > 0 ? _mLen[_mCount - 1] : 0;
            if (bestRepLen >= _niceLen)
            {
                longLen = bestRepLen;
                longBack = bestRepIdx;
                break;
            }
            if (mainLen >= _niceLen)
            {
                longLen = mainLen;
                longBack = _mDist[_mCount - 1] + 4;
                break;
            }

            // Literal.
            uint price = basePrice + Price0(_isMatch[ims]) + LiteralPrice(p, s, curByte, matchByte);
            Relax(ref lenEnd, cur + 1, price, cur, -1, LitNext[s], r0, r1, r2, r3);

            uint matchPrice = basePrice + Price1(_isMatch[ims]);
            uint repPrice = matchPrice + Price1(_isRep[s]);

            // Short rep (one byte at distance rep0).
            if (rep0Valid && matchByte == curByte)
            {
                price = repPrice + Price0(_isRepG0[s]) + Price0(_isRep0Long[ims]);
                Relax(ref lenEnd, cur + 1, price, cur, 0, ShortRepNext[s], r0, r1, r2, r3);
            }

            if (avail >= 2)
            {
                int lpBase = posState * LenSymbols - 2;
                byte repState = RepNext[s];
                if (repLen0 >= 2)
                {
                    uint pp = repPrice + Price0(_isRepG0[s]) + Price1(_isRep0Long[ims]);
                    for (int l = repLen0; l >= 2; l--)
                        Relax(ref lenEnd, cur + l, pp + _repLenPrices[lpBase + l], cur, 0, repState, r0, r1, r2, r3);
                }
                if (repLen1 >= 2)
                {
                    uint pp = repPrice + Price1(_isRepG0[s]) + Price0(_isRepG1[s]);
                    for (int l = repLen1; l >= 2; l--)
                        Relax(ref lenEnd, cur + l, pp + _repLenPrices[lpBase + l], cur, 1, repState, r1, r0, r2, r3);
                }
                if (repLen2 >= 2)
                {
                    uint pp = repPrice + Price1(_isRepG0[s]) + Price1(_isRepG1[s]) + Price0(_isRepG2[s]);
                    for (int l = repLen2; l >= 2; l--)
                        Relax(ref lenEnd, cur + l, pp + _repLenPrices[lpBase + l], cur, 2, repState, r2, r0, r1, r3);
                }
                if (repLen3 >= 2)
                {
                    uint pp = repPrice + Price1(_isRepG0[s]) + Price1(_isRepG1[s]) + Price1(_isRepG2[s]);
                    for (int l = repLen3; l >= 2; l--)
                        Relax(ref lenEnd, cur + l, pp + _repLenPrices[lpBase + l], cur, 3, repState, r3, r0, r1, r2);
                }

                int startLen = bestRepLen >= 2 ? bestRepLen + 1 : 2;
                if (mainLen >= startLen)
                {
                    uint normalPrice = matchPrice + Price0(_isRep[s]);
                    byte matchState = MatchNext[s];
                    int k = 0;
                    while (_mLen[k] < startLen) k++;
                    for (int l = startLen; ; l++)
                    {
                        uint dist = (uint)_mDist[k];
                        price = normalPrice + _lenPrices[lpBase + l] + DistPrice(dist, l < 6 ? l - 2 : 3);
                        Relax(ref lenEnd, cur + l, price, cur, (int)dist + 4, matchState, dist, r0, r1, r2);
                        if (l == _mLen[k] && ++k == _mCount) break;
                    }
                }
            }

            cur++;
            if (cur == lenEnd || cur >= OptsMax) break;
        }

        // Walk back from the end of the cheapest path and emit it in forward order.
        int count = 0;
        for (int i = cur; i > 0; i = _oPrev[i]) count++;
        int w = count;
        for (int i = cur; i > 0; i = _oPrev[i])
        {
            w--;
            _symLen[w] = i - _oPrev[i];
            _symBack[w] = _oBack[i];
        }
        if (longLen > 0)
        {
            _symLen[count] = longLen;
            _symBack[count] = longBack;
            count++;
        }
        _symCount = count;
    }

    // -------------------------------------------------------------- symbols

    private void EncodeSymbol(int pos, int len, int back)
    {
        int s = _state;
        int posState = (int)((uint)pos & _pbMask);
        int ims = (s << 4) + posState;
        _sincePriceUpdate++;

        if (back < 0)
        {
            Bit(ref _isMatch[ims], 0);
            int prev = pos > 0 ? _buf[pos - 1] : 0;
            int litBase = 0x300 * ((int)(((uint)pos & _lpMask) << _lc) + (prev >> (8 - _lc)));
            uint symbol = (uint)_buf[pos] | 0x100;
            ushort[] lit = _literal;
            if (s < 7)
            {
                do
                {
                    Bit(ref lit[litBase + (int)(symbol >> 8)], (symbol >> 7) & 1);
                    symbol <<= 1;
                }
                while (symbol < 0x10000);
            }
            else
            {
                uint matchByte = _buf[pos - (int)_rep0 - 1];
                uint offset = 0x100;
                do
                {
                    matchByte <<= 1;
                    uint matchBit = matchByte & offset;
                    Bit(ref lit[litBase + (int)(offset + matchBit + (symbol >> 8))], (symbol >> 7) & 1);
                    symbol <<= 1;
                    offset &= ~(matchByte ^ symbol);
                }
                while (symbol < 0x10000);
            }
            _state = LitNext[s];
            return;
        }

        Bit(ref _isMatch[ims], 1);
        if (back < 4)
        {
            Bit(ref _isRep[s], 1);
            if (back == 0)
            {
                Bit(ref _isRepG0[s], 0);
                Bit(ref _isRep0Long[ims], len == 1 ? 0u : 1u);
            }
            else
            {
                Bit(ref _isRepG0[s], 1);
                uint dist;
                if (back == 1)
                {
                    Bit(ref _isRepG1[s], 0);
                    dist = _rep1;
                }
                else
                {
                    Bit(ref _isRepG1[s], 1);
                    Bit(ref _isRepG2[s], (uint)(back - 2));
                    if (back == 2)
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
            if (len == 1)
            {
                _state = ShortRepNext[s];
            }
            else
            {
                EncodeLen(_repChoice, _repLow, _repMid, _repHigh, len - 2, posState);
                _state = RepNext[s];
            }
            return;
        }

        Bit(ref _isRep[s], 0);
        _state = MatchNext[s];
        EncodeLen(_lenChoice, _lenLow, _lenMid, _lenHigh, len - 2, posState);
        uint d = (uint)(back - 4);
        int lenState = len < 6 ? len - 2 : 3;
        int slot = DistSlot(d);
        Tree(_posSlot, lenState << 6, 6, (uint)slot);
        if (slot >= 4)
        {
            int footer = (slot >> 1) - 1;
            uint baseDist = (2u | ((uint)slot & 1)) << footer;
            uint reduced = d - baseDist;
            if (slot < 14)
            {
                ReverseTree(_posSpecial, (int)baseDist - slot - 1, footer, reduced);
            }
            else
            {
                Direct(reduced >> 4, footer - 4);
                ReverseTree(_align, 0, 4, reduced & 15);
            }
        }
        _rep3 = _rep2;
        _rep2 = _rep1;
        _rep1 = _rep0;
        _rep0 = d;
    }

    private void EncodeLen(ushort[] choice, ushort[] low, ushort[] mid, ushort[] high, int len, int posState)
    {
        if (len < 8)
        {
            Bit(ref choice[0], 0);
            Tree(low, posState << 3, 3, (uint)len);
        }
        else if (len < 16)
        {
            Bit(ref choice[0], 1);
            Bit(ref choice[1], 0);
            Tree(mid, posState << 3, 3, (uint)(len - 8));
        }
        else
        {
            Bit(ref choice[0], 1);
            Bit(ref choice[1], 1);
            Tree(high, 0, 8, (uint)(len - 16));
        }
    }
}
