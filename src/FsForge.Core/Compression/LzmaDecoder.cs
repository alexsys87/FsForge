using System.Runtime.CompilerServices;

namespace FsForge.Core.Compression;

/// <summary>
/// LZMA decoder working on a flat output buffer that doubles as the dictionary.
/// One call decodes one complete range-coded chunk (a whole LZMA1 stream or one LZMA2 chunk),
/// the probability model and the repeat distances persist between calls.
/// </summary>
internal sealed class LzmaDecoder
{
    private const int NumStates = 12;
    private const ushort ProbInit = 1024;

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
    private ushort[] _literal = Array.Empty<ushort>();

    private int _lc, _lp, _pb;
    private uint _state, _rep0, _rep1, _rep2, _rep3;
    private uint _processed;      // bytes produced since the last dictionary reset
    private bool _propsSet;

    public bool HasProps => _propsSet;

    /// <summary>Sets lc/lp/pb from the LZMA properties byte and resets the model.</summary>
    public void SetProps(int propsByte)
    {
        if (propsByte >= 9 * 5 * 5) throw new FsException("Corrupt LZMA data: invalid properties.");
        int lc = propsByte % 9;
        propsByte /= 9;
        int lp = propsByte % 5;
        int pb = propsByte / 5;
        _lc = lc;
        _lp = lp;
        _pb = pb;
        int litSize = 0x300 << (lc + lp);
        if (_literal.Length != litSize) _literal = new ushort[litSize];
        _propsSet = true;
        ResetState();
    }

    /// <summary>Resets probabilities, the state machine and the repeat distances.</summary>
    public void ResetState()
    {
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
    }

    /// <summary>Call when the dictionary is reset (position based contexts restart).</summary>
    public void ResetDictionary() => _processed = 0;

    /// <summary>Accounts for bytes stored without compression (LZMA2 uncompressed chunks).</summary>
    public void SkipUncompressed(int count) => _processed += (uint)count;

    private ref struct Rc
    {
        public ReadOnlySpan<byte> Src;
        public int Pos;
        public uint Range;
        public uint Code;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Bit(ref ushort prob)
        {
            if (Range < 0x01000000)
            {
                Range <<= 8;
                Code = (Code << 8) | Src[Pos++];
            }
            uint p = prob;
            uint bound = (Range >> 11) * p;
            if (Code < bound)
            {
                Range = bound;
                prob = (ushort)(p + ((2048 - p) >> 5));
                return 0;
            }
            Range -= bound;
            Code -= bound;
            prob = (ushort)(p - (p >> 5));
            return 1;
        }

        public uint Direct(int bits)
        {
            uint result = 0;
            for (; bits > 0; bits--)
            {
                if (Range < 0x01000000)
                {
                    Range <<= 8;
                    Code = (Code << 8) | Src[Pos++];
                }
                Range >>= 1;
                Code -= Range;
                uint t = 0 - (Code >> 31);
                Code += Range & t;
                result = (result << 1) + (t + 1);
            }
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Tree(ushort[] probs, int offset, int bits)
        {
            uint m = 1;
            for (int i = 0; i < bits; i++) m = (m << 1) | Bit(ref probs[offset + (int)m]);
            return m - (1u << bits);
        }

        public uint ReverseTree(ushort[] probs, int offset, int bits)
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
    }

    /// <summary>
    /// Decodes one range-coded chunk.
    /// </summary>
    /// <param name="buf">Output buffer; bytes [dictStart, pos) are the usable dictionary.</param>
    /// <param name="pos">Where to store the first decoded byte.</param>
    /// <param name="dictStart">Index of the oldest valid dictionary byte.</param>
    /// <param name="count">Exact number of bytes to produce, or -1 to stop at the end marker.</param>
    /// <param name="src">Complete compressed chunk.</param>
    /// <returns>Number of bytes produced.</returns>
    public int Decode(Span<byte> buf, int pos, int dictStart, int count, ReadOnlySpan<byte> src)
    {
        if (!_propsSet) throw new FsException("Corrupt LZMA data: properties are missing.");
        if (src.Length < 5 || src[0] != 0) throw new FsException("Corrupt LZMA data: bad range coder header.");

        var rc = new Rc
        {
            Src = src,
            Pos = 5,
            Range = 0xFFFFFFFF,
            Code = ((uint)src[1] << 24) | ((uint)src[2] << 16) | ((uint)src[3] << 8) | src[4],
        };

        int start = pos;
        int end = count < 0 ? buf.Length : pos + count;
        if (end > buf.Length) throw new FsException("Corrupt LZMA data: output is larger than expected.");

        uint state = _state, rep0 = _rep0, rep1 = _rep1, rep2 = _rep2, rep3 = _rep3;
        uint processed = _processed;
        uint pbMask = (1u << _pb) - 1, lpMask = (1u << _lp) - 1;
        int lc = _lc;
        ushort[] literal = _literal;

        try
        {
            while (pos < end)
            {
                int posState = (int)(processed & pbMask);
                int ims = (int)(state << 4) + posState;

                if (rc.Bit(ref _isMatch[ims]) == 0)
                {
                    int prev = pos > dictStart ? buf[pos - 1] : 0;
                    int litBase = 0x300 * ((int)((processed & lpMask) << lc) + (prev >> (8 - lc)));
                    uint symbol = 1;
                    if (state < 7)
                    {
                        do { symbol = (symbol << 1) | rc.Bit(ref literal[litBase + (int)symbol]); }
                        while (symbol < 0x100);
                    }
                    else
                    {
                        if (rep0 >= (uint)(pos - dictStart)) throw new FsException("Corrupt LZMA data: invalid distance.");
                        uint matchByte = buf[pos - (int)rep0 - 1];
                        uint offs = 0x100;
                        do
                        {
                            matchByte <<= 1;
                            uint matchBit = matchByte & offs;
                            uint bit = rc.Bit(ref literal[litBase + (int)(offs + matchBit + symbol)]);
                            symbol = (symbol << 1) | bit;
                            offs &= bit != 0 ? matchBit : ~matchBit;
                        }
                        while (symbol < 0x100);
                    }
                    buf[pos++] = (byte)symbol;
                    processed++;
                    state = state < 4 ? 0 : state < 10 ? state - 3 : state - 6;
                    continue;
                }

                int len;
                if (rc.Bit(ref _isRep[state]) == 0)
                {
                    rep3 = rep2;
                    rep2 = rep1;
                    rep1 = rep0;
                    len = DecodeLen(ref rc, _lenChoice, _lenLow, _lenMid, _lenHigh, posState);
                    state = state < 7 ? 7u : 10u;
                    int lenState = len < 4 ? len : 3;
                    uint slot = rc.Tree(_posSlot, lenState << 6, 6);
                    if (slot < 4)
                    {
                        rep0 = slot;
                    }
                    else
                    {
                        int direct = (int)(slot >> 1) - 1;
                        rep0 = (2 | (slot & 1)) << direct;
                        if (slot < 14)
                        {
                            rep0 += rc.ReverseTree(_posSpecial, (int)(rep0 - slot - 1), direct);
                        }
                        else
                        {
                            rep0 += rc.Direct(direct - 4) << 4;
                            rep0 += rc.ReverseTree(_align, 0, 4);
                            if (rep0 == 0xFFFFFFFF)
                            {
                                if (count >= 0) throw new FsException("Corrupt LZMA data: unexpected end marker.");
                                break;
                            }
                        }
                    }
                }
                else
                {
                    if (rc.Bit(ref _isRepG0[state]) == 0)
                    {
                        if (rc.Bit(ref _isRep0Long[ims]) == 0)
                        {
                            // Short rep: one byte at distance rep0.
                            if (rep0 >= (uint)(pos - dictStart)) throw new FsException("Corrupt LZMA data: invalid distance.");
                            state = state < 7 ? 9u : 11u;
                            buf[pos] = buf[pos - (int)rep0 - 1];
                            pos++;
                            processed++;
                            continue;
                        }
                    }
                    else
                    {
                        uint dist;
                        if (rc.Bit(ref _isRepG1[state]) == 0)
                        {
                            dist = rep1;
                        }
                        else
                        {
                            if (rc.Bit(ref _isRepG2[state]) == 0)
                            {
                                dist = rep2;
                            }
                            else
                            {
                                dist = rep3;
                                rep3 = rep2;
                            }
                            rep2 = rep1;
                        }
                        rep1 = rep0;
                        rep0 = dist;
                    }
                    len = DecodeLen(ref rc, _repChoice, _repLow, _repMid, _repHigh, posState);
                    state = state < 7 ? 8u : 11u;
                }

                len += 2;
                if (rep0 >= (uint)(pos - dictStart)) throw new FsException("Corrupt LZMA data: invalid distance.");
                if (len > end - pos) throw new FsException("Corrupt LZMA data: match crosses the end of the block.");
                int from = pos - (int)rep0 - 1;
                if (rep0 + 1 >= (uint)len)
                {
                    buf.Slice(from, len).CopyTo(buf[pos..]);
                    pos += len;
                }
                else
                {
                    for (int i = 0; i < len; i++) buf[pos++] = buf[from++];
                }
                processed += (uint)len;
            }
        }
        catch (IndexOutOfRangeException)
        {
            throw new FsException("Corrupt LZMA data: compressed stream is truncated.");
        }

        _state = state;
        _rep0 = rep0;
        _rep1 = rep1;
        _rep2 = rep2;
        _rep3 = rep3;
        _processed = processed;
        return pos - start;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DecodeLen(ref Rc rc, ushort[] choice, ushort[] low, ushort[] mid, ushort[] high, int posState)
    {
        if (rc.Bit(ref choice[0]) == 0) return (int)rc.Tree(low, posState << 3, 3);
        if (rc.Bit(ref choice[1]) == 0) return 8 + (int)rc.Tree(mid, posState << 3, 3);
        return 16 + (int)rc.Tree(high, 0, 8);
    }
}
