namespace FsForge.Core.Compression;

/// <summary>
/// A deflate compressor that writes a bare stream (no zlib header) and never refers further back than
/// a given window. UBIFS needs both: the kernel inflates its "zlib" data blocks with an 11-bit window,
/// which the compressor of .NET cannot be limited to.
/// </summary>
public sealed class RawDeflate
{
    private const int HashBits = 13, MaxChain = 64, MinMatch = 3, MaxMatch = 258;
    private const int EndOfBlock = 256, LiteralCodes = 286, DistanceCodes = 30, LengthCodes = 19;

    private static readonly int[] LengthBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
    private static readonly int[] LengthExtra = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
    private static readonly int[] DistanceBase = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
    private static readonly int[] DistanceExtra = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
    private static readonly int[] LengthOrder = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };
    private static readonly byte[] LengthCodeOf = BuildLengthCodes();

    private readonly int _window;
    private readonly int[] _head = new int[1 << HashBits];
    private int[] _prev = Array.Empty<int>();
    // A token is a literal (distance 0, value in Length) or a match.
    private (ushort Length, ushort Distance)[] _tokens = Array.Empty<(ushort, ushort)>();

    /// <param name="windowBits">Size of the window as a power of two, 8 to 15.</param>
    public RawDeflate(int windowBits = 15)
    {
        if (windowBits is < 8 or > 15) throw new ArgumentOutOfRangeException(nameof(windowBits));
        _window = 1 << windowBits;
    }

    private static byte[] BuildLengthCodes()
    {
        byte[] codes = new byte[MaxMatch + 1];
        for (int code = 0; code < LengthBase.Length; code++)
            for (int length = LengthBase[code]; length <= MaxMatch && (code == LengthBase.Length - 1 || length < LengthBase[code + 1]); length++)
                codes[length] = (byte)code;
        return codes;
    }

    private static int DistanceCodeOf(int distance)
    {
        int code = 0;
        while (code + 1 < DistanceCodes && DistanceBase[code + 1] <= distance) code++;
        return code;
    }

    /// <summary>Compresses a block; returns the compressed size, or -1 when the result does not fit into <paramref name="dst"/>.</summary>
    public int Compress(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int count = Tokenize(src);
        var bits = new BitWriter(dst);
        // One block holds the statistics of at most 32768 tokens; a 4 KiB block of UBIFS is always one deflate block.
        const int perBlock = 1 << 15;
        int from = 0;
        do
        {
            int n = Math.Min(perBlock, count - from);
            WriteBlock(ref bits, _tokens.AsSpan(from, n), last: from + n == count);
            from += n;
        }
        while (from < count);
        return bits.Finish();
    }

    // -------------------------------------------------------------- LZ77

    private int Hash(ReadOnlySpan<byte> s, int i) => (int)(((uint)(s[i] | s[i + 1] << 8 | s[i + 2] << 16) * 0x9E3779B1u) >> (32 - HashBits));

    private void Insert(ReadOnlySpan<byte> s, int i)
    {
        int h = Hash(s, i);
        _prev[i] = _head[h];
        _head[h] = i;
    }

    private (int Length, int Distance) Longest(ReadOnlySpan<byte> s, int i)
    {
        int best = 0, bestDistance = 0;
        int limit = Math.Min(MaxMatch, s.Length - i);
        if (limit < MinMatch) return (0, 0);
        int candidate = _head[Hash(s, i)];
        for (int chain = 0; candidate >= 0 && chain < MaxChain; chain++, candidate = _prev[candidate])
        {
            int distance = i - candidate;
            if (distance > _window) break;
            int length = s.Slice(candidate, limit).CommonPrefixLength(s.Slice(i, limit));
            if (length > best)
            {
                best = length;
                bestDistance = distance;
                if (length == limit) break;
            }
        }
        // A short match far away costs more bits than its three literals.
        if (best == MinMatch && bestDistance > 1024) best = 0;
        return best >= MinMatch ? (best, bestDistance) : (0, 0);
    }

    private int Tokenize(ReadOnlySpan<byte> s)
    {
        int n = s.Length;
        if (_prev.Length < n) _prev = new int[n];
        if (_tokens.Length < n + 1) _tokens = new (ushort, ushort)[n + 1];
        Array.Fill(_head, -1);
        int count = 0, i = 0;
        while (i < n)
        {
            (int length, int distance) = Longest(s, i);
            if (length > 0 && i + 1 < n)
            {
                // Lazy matching: a longer match one byte further on is worth a literal.
                if (i + MinMatch <= n) Insert(s, i);
                (int nextLength, int nextDistance) = Longest(s, i + 1);
                if (nextLength > length)
                {
                    _tokens[count++] = (s[i], 0);
                    i++;
                    (length, distance) = (nextLength, nextDistance);
                }
                _tokens[count++] = ((ushort)length, (ushort)distance);
                int end = i + length;
                for (int k = i + (i + MinMatch <= n && _head[Hash(s, i)] == i ? 1 : 0); k < end && k + MinMatch <= n; k++) Insert(s, k);
                i = end;
                continue;
            }
            if (length > 0)
            {
                _tokens[count++] = ((ushort)length, (ushort)distance);
                i += length;
                continue;
            }
            if (i + MinMatch <= n) Insert(s, i);
            _tokens[count++] = (s[i], 0);
            i++;
        }
        return count;
    }

    // ----------------------------------------------------------- Huffman

    /// <summary>Code lengths of an optimal prefix code, limited to <paramref name="maxBits"/>. The code is always complete.</summary>
    private static byte[] BuildLengths(int[] frequencies, int maxBits)
    {
        int n = frequencies.Length;
        byte[] lengths = new byte[n];
        var symbols = new List<int>();
        for (int i = 0; i < n; i++)
            if (frequencies[i] > 0) symbols.Add(i);
        if (symbols.Count == 0) return lengths;
        if (symbols.Count == 1)
        {
            lengths[symbols[0]] = 1;
            return lengths;
        }

        // The Huffman tree, built with a priority queue; "parent" gives the depth of every leaf.
        int[] parent = new int[2 * symbols.Count];
        var queue = new PriorityQueue<int, (long Weight, int Order)>();
        for (int i = 0; i < symbols.Count; i++) queue.Enqueue(i, (frequencies[symbols[i]], i));
        int next = symbols.Count;
        while (queue.Count > 1)
        {
            queue.TryDequeue(out int a, out (long Weight, int Order) wa);
            queue.TryDequeue(out int b, out (long Weight, int Order) wb);
            parent[a] = parent[b] = next;
            queue.Enqueue(next, (wa.Weight + wb.Weight, next));
            next++;
        }
        int root = next - 1;
        for (int i = 0; i < symbols.Count; i++)
        {
            int depth = 0;
            for (int node = i; node != root; node = parent[node]) depth++;
            lengths[symbols[i]] = (byte)Math.Min(depth, maxBits);
        }

        // Cutting long codes short oversubscribes the code: lengthen the rarest shorter codes until it fits again,
        // then give back what was taken too much, so that no code value stays unused.
        long full = 1L << maxBits, used = 0;
        foreach (int s in symbols) used += full >> lengths[s];
        if (used == full) return lengths;
        List<int> byFrequency = symbols.OrderBy(s => frequencies[s]).ThenByDescending(s => lengths[s]).ToList();
        while (used > full)
        {
            int pick = -1;
            foreach (int s in byFrequency)
                if (lengths[s] < maxBits && (pick < 0 || lengths[s] > lengths[pick])) pick = s;
            used -= full >> lengths[pick];
            lengths[pick]++;
            used += full >> lengths[pick];
        }
        while (used < full)
        {
            // The longest code can always be shortened by one bit: the gap is a multiple of its share.
            int pick = -1;
            for (int k = byFrequency.Count - 1; k >= 0; k--)
            {
                int s = byFrequency[k];
                if (lengths[s] > 1 && used + (full >> lengths[s]) <= full && (pick < 0 || lengths[s] > lengths[pick])) pick = s;
            }
            used += full >> lengths[pick];
            lengths[pick]--;
        }
        return lengths;
    }

    /// <summary>Canonical codes for the given lengths, bit-reversed: deflate sends Huffman codes most significant bit first.</summary>
    private static ushort[] BuildCodes(byte[] lengths)
    {
        int[] countPerLength = new int[16], nextCode = new int[16];
        foreach (byte l in lengths) countPerLength[l]++;
        countPerLength[0] = 0;
        int code = 0;
        for (int b = 1; b < 16; b++)
        {
            code = (code + countPerLength[b - 1]) << 1;
            nextCode[b] = code;
        }
        ushort[] codes = new ushort[lengths.Length];
        for (int i = 0; i < lengths.Length; i++)
        {
            int l = lengths[i];
            if (l == 0) continue;
            int c = nextCode[l]++, reversed = 0;
            for (int b = 0; b < l; b++) reversed |= ((c >> b) & 1) << (l - 1 - b);
            codes[i] = (ushort)reversed;
        }
        return codes;
    }

    private static readonly byte[] FixedLiteralLengths = BuildFixedLiteralLengths();
    private static readonly byte[] FixedDistanceLengths = Enumerable.Repeat((byte)5, DistanceCodes).ToArray();

    private static byte[] BuildFixedLiteralLengths()
    {
        byte[] l = new byte[288];
        for (int i = 0; i < 288; i++) l[i] = (byte)(i < 144 ? 8 : i < 256 ? 9 : i < 280 ? 7 : 8);
        return l;
    }

    private void WriteBlock(ref BitWriter bits, ReadOnlySpan<(ushort Length, ushort Distance)> tokens, bool last)
    {
        int[] literalFrequency = new int[LiteralCodes], distanceFrequency = new int[DistanceCodes];
        foreach ((ushort length, ushort distance) in tokens)
        {
            if (distance == 0) literalFrequency[length]++;
            else
            {
                literalFrequency[257 + LengthCodeOf[length]]++;
                distanceFrequency[DistanceCodeOf(distance)]++;
            }
        }
        literalFrequency[EndOfBlock]++;
        // A block without matches still describes one distance code.
        if (Array.TrueForAll(distanceFrequency, f => f == 0)) distanceFrequency[0] = 1;

        byte[] literalLengths = BuildLengths(literalFrequency, 15), distanceLengths = BuildLengths(distanceFrequency, 15);
        int literalCount = LiteralCodes, distanceCount = DistanceCodes;
        while (literalCount > 257 && literalLengths[literalCount - 1] == 0) literalCount--;
        while (distanceCount > 1 && distanceLengths[distanceCount - 1] == 0) distanceCount--;

        // The two tables of lengths go out as one run-length coded sequence, itself Huffman coded.
        var sequence = new List<(int Symbol, int Extra)>();
        byte[] all = new byte[literalCount + distanceCount];
        literalLengths.AsSpan(0, literalCount).CopyTo(all);
        distanceLengths.AsSpan(0, distanceCount).CopyTo(all.AsSpan(literalCount));
        for (int i = 0; i < all.Length;)
        {
            int run = 1;
            while (i + run < all.Length && all[i + run] == all[i]) run++;
            int value = all[i];
            i += run;
            if (value == 0)
            {
                for (; run >= 11; run -= Math.Min(run, 138)) sequence.Add((18, Math.Min(run, 138) - 11));
                if (run >= 3) { sequence.Add((17, run - 3)); run = 0; }
            }
            else
            {
                sequence.Add((value, 0));
                run--;
                for (; run >= 3; run -= Math.Min(run, 6)) sequence.Add((16, Math.Min(run, 6) - 3));
            }
            for (; run > 0; run--) sequence.Add((value, 0));
        }
        int[] lengthFrequency = new int[LengthCodes];
        foreach ((int symbol, int _) in sequence) lengthFrequency[symbol]++;
        byte[] lengthLengths = BuildLengths(lengthFrequency, 7);
        int lengthCount = LengthCodes;
        while (lengthCount > 4 && lengthLengths[LengthOrder[lengthCount - 1]] == 0) lengthCount--;

        long dynamicBits = 14 + 3L * lengthCount, fixedBits = 0;
        foreach ((int symbol, int _) in sequence) dynamicBits += lengthLengths[symbol] + (symbol == 16 ? 2 : symbol == 17 ? 3 : symbol == 18 ? 7 : 0);
        for (int i = 0; i < LiteralCodes; i++)
        {
            int extra = i >= 257 ? LengthExtra[i - 257] : 0;
            dynamicBits += (long)literalFrequency[i] * (literalLengths[i] + extra);
            fixedBits += (long)literalFrequency[i] * (FixedLiteralLengths[i] + extra);
        }
        for (int i = 0; i < DistanceCodes; i++)
        {
            dynamicBits += (long)distanceFrequency[i] * (distanceLengths[i] + DistanceExtra[i]);
            fixedBits += (long)distanceFrequency[i] * (5 + DistanceExtra[i]);
        }

        bool dynamic = dynamicBits < fixedBits;
        bits.Write(last ? 1 : 0, 1);
        bits.Write(dynamic ? 2 : 1, 2);
        if (dynamic)
        {
            bits.Write(literalCount - 257, 5);
            bits.Write(distanceCount - 1, 5);
            bits.Write(lengthCount - 4, 4);
            for (int i = 0; i < lengthCount; i++) bits.Write(lengthLengths[LengthOrder[i]], 3);
            ushort[] lengthCodes = BuildCodes(lengthLengths);
            foreach ((int symbol, int extra) in sequence)
            {
                bits.Write(lengthCodes[symbol], lengthLengths[symbol]);
                if (symbol == 16) bits.Write(extra, 2);
                else if (symbol == 17) bits.Write(extra, 3);
                else if (symbol == 18) bits.Write(extra, 7);
            }
        }
        else
        {
            literalLengths = FixedLiteralLengths;
            distanceLengths = FixedDistanceLengths;
        }

        ushort[] literalCodes = BuildCodes(literalLengths), distanceCodes = BuildCodes(distanceLengths);
        foreach ((ushort length, ushort distance) in tokens)
        {
            if (distance == 0)
            {
                bits.Write(literalCodes[length], literalLengths[length]);
                continue;
            }
            int lc = LengthCodeOf[length];
            bits.Write(literalCodes[257 + lc], literalLengths[257 + lc]);
            bits.Write(length - LengthBase[lc], LengthExtra[lc]);
            int dc = DistanceCodeOf(distance);
            bits.Write(distanceCodes[dc], distanceLengths[dc]);
            bits.Write(distance - DistanceBase[dc], DistanceExtra[dc]);
        }
        bits.Write(literalCodes[EndOfBlock], literalLengths[EndOfBlock]);
    }

    private ref struct BitWriter(Span<byte> buffer)
    {
        private readonly Span<byte> _buffer = buffer;
        private ulong _bits;
        private int _count, _length;
        private bool _overflow;

        public void Write(int value, int count)
        {
            if (count == 0) return;
            _bits |= (ulong)(uint)value << _count;
            _count += count;
            while (_count >= 8)
            {
                if (_length < _buffer.Length) _buffer[_length++] = (byte)_bits;
                else _overflow = true;
                _bits >>= 8;
                _count -= 8;
            }
        }

        /// <summary>Flushes the last bits; returns the number of bytes written, or -1 when the buffer was too small.</summary>
        public int Finish()
        {
            if (_count > 0) Write(0, 8 - _count);
            return _overflow ? -1 : _length;
        }
    }
}
