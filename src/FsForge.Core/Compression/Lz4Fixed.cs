using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FsForge.Core.Compression;

/// <summary>
/// LZ4 block compressor for a fixed output size (what LZ4_compress_destSize does): it takes as much of
/// the input as fits into the output buffer. EROFS stores data this way - the compressed cluster has a
/// fixed size and the amount of data it stands for varies.
/// <para>
/// Matches are found through hash chains; the level sets how far a chain is followed. The blocks keep the
/// end-of-block rules of the format relative to the input actually taken (the last five bytes are literals,
/// the last match starts at least twelve bytes before the end), so the strict decoders unpack them too.
/// </para>
/// </summary>
public sealed class Lz4FixedEncoder
{
    private const int MinMatch = 4, MfLimit = 12, LastLiterals = 5, MaxDistance = 65535;
    private const int HashBits = 16;

    private readonly int _attempts;
    private readonly bool _lazy;
    private readonly int[] _head = new int[1 << HashBits];
    private int[] _prev = Array.Empty<int>();

    /// <summary>
    /// Added to every position stored in the tables. Each call starts above the values of the calls before,
    /// so older entries read as "no candidate" and the tables never need clearing.
    /// </summary>
    private int _base = 1;

    /// <param name="level">1 and 2: one candidate per position (fast); 3 to 12: hash chains with lazy matching.</param>
    public Lz4FixedEncoder(int level)
    {
        level = Math.Clamp(level, 1, 12);
        _attempts = level <= 2 ? level : Math.Min(1 << (level - 1), 4096);
        _lazy = level >= 3;
    }

    private static int LengthBytes(int value) => value >= 15 ? 1 + (value - 15) / 255 : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Hash(uint sequence) => (int)((sequence * 2654435761u) >> (32 - HashBits));

    /// <summary>The final literals a block needs after a match of the given length.</summary>
    private static int TailNeed(int matchLength) => Math.Max(LastLiterals, MfLimit - matchLength);


    /// <summary>
    /// Compresses the beginning of <paramref name="src"/> into at most <paramref name="dst"/>.Length bytes.
    /// </summary>
    /// <param name="consumed">Number of input bytes the block stands for.</param>
    /// <returns>The size of the block.</returns>
    public int Compress(ReadOnlySpan<byte> src, Span<byte> dst, out int consumed)
    {
        int n = src.Length, cap = dst.Length;
        consumed = 0;
        if (n == 0 || cap < 2) return 0;
        if (_prev.Length < n) _prev = new int[Math.Max(n, 1 << 16)];
        if (_base > int.MaxValue - n - 2)
        {
            Array.Clear(_head);
            _base = 1;
        }
        int bias = _base;
        _base += n + 1;
        int[] head = _head, prev = _prev;

        int op = 0, anchor = 0, ip = 0;
        int searchEnd = n - MfLimit;            // a match may start here at the latest
        int matchEnd = n - LastLiterals;        // and must end here at the latest
        int inserted = 0;                       // positions below this one are in the hash chains
        // Every position read below lies at least MfLimit bytes before the end of the input.
        void Insert(ReadOnlySpan<byte> s, int upTo)
        {
            ref byte s0 = ref MemoryMarshal.GetReference(s);
            for (; inserted < upTo; inserted++)
            {
                int h = Hash(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref s0, inserted)));
                prev[inserted] = head[h];
                head[h] = inserted + bias;
            }
        }

        // The longest match at "at" (which must be inserted already, or be the next position to insert).
        int Find(ReadOnlySpan<byte> s, int at, out int distance)
        {
            distance = 0;
            Insert(s, at);
            ref byte s0 = ref MemoryMarshal.GetReference(s);
            uint first = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref s0, at));
            int best = 0, limit = matchEnd - at;
            int candidate = head[Hash(first)] - bias;
            for (int tries = _attempts; candidate >= 0 && tries > 0; tries--, candidate = prev[candidate] - bias)
            {
                if (at - candidate > MaxDistance) break;
                if (Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref s0, candidate)) != first) continue;
                if (best >= MinMatch && Unsafe.Add(ref s0, candidate + best) != Unsafe.Add(ref s0, at + best)) continue;
                int length = s.Slice(candidate, limit).CommonPrefixLength(s.Slice(at, limit));
                if (length > best)
                {
                    best = length;
                    distance = at - candidate;
                    if (length == limit) break;
                }
            }
            return best >= MinMatch ? best : 0;
        }

        while (ip <= searchEnd)
        {
            int length = Find(src, ip, out int distance);
            if (length == 0)
            {
                ip++;
                continue;
            }
            // A longer match one byte further on is worth a literal.
            while (_lazy && ip + 1 <= searchEnd)
            {
                int next = Find(src, ip + 1, out int nextDistance);
                if (next <= length) break;
                ip++;
                length = next;
                distance = nextDistance;
            }
            // Extend backwards over literals that match too.
            while (ip > anchor && ip - distance > 0 && src[ip - 1] == src[ip - distance - 1])
            {
                ip--;
                length++;
            }

            int literals = ip - anchor;
            int fixedCost = 1 + LengthBytes(literals) + literals + 2;
            int room = cap - op - fixedCost;
            // Behind the match there must be room for the literals that end the block.
            if (room - LengthBytes(length - MinMatch) < 1 + TailNeed(length))
            {
                // The match as it is does not fit: a shorter one may.
                int spare = room - (1 + LastLiterals);
                if (length < MfLimit - LastLiterals || spare < 0) break;
                length = Math.Min(length, MinMatch + 14 + 255 * spare);
                if (length < MfLimit - LastLiterals) break;
            }

            // token, literal length, literals, offset, match length
            int token = op++;
            int code = length - MinMatch;
            dst[token] = (byte)((Math.Min(literals, 15) << 4) | Math.Min(code, 15));
            if (literals >= 15) op = PutLength(dst, op, literals - 15);
            src.Slice(anchor, literals).CopyTo(dst[op..]);
            op += literals;
            dst[op++] = (byte)distance;
            dst[op++] = (byte)(distance >> 8);
            if (code >= 15) op = PutLength(dst, op, code - 15);

            ip += length;
            anchor = ip;
            if (_lazy) Insert(src, Math.Min(ip, searchEnd + 1));
            else inserted = Math.Max(inserted, Math.Min(ip, searchEnd + 1));
        }

        // The last sequence: literals only, as many as fit.
        int run = Math.Min(n - anchor, cap - op - 1);
        while (run > 0 && 1 + LengthBytes(run) + run > cap - op) run--;
        if (run <= 0) return 0;                 // cannot happen: every match leaves room for the tail
        dst[op++] = (byte)(Math.Min(run, 15) << 4);
        if (run >= 15) op = PutLength(dst, op, run - 15);
        src.Slice(anchor, run).CopyTo(dst[op..]);
        op += run;
        consumed = anchor + run;
        return op;
    }

    private static int PutLength(Span<byte> dst, int op, int rest)
    {
        for (; rest >= 255; rest -= 255) dst[op++] = 255;
        dst[op++] = (byte)rest;
        return op;
    }

    /// <summary>
    /// Unpacks an LZ4 block into <paramref name="dst"/> and stops when the buffer is full: the block may
    /// stand for more data than is asked for. Returns the number of bytes produced.
    /// </summary>
    public static int Decompress(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int ip = 0, op = 0;
        try
        {
            while (op < dst.Length && ip < src.Length)
            {
                int token = src[ip++];
                int literals = token >> 4;
                if (literals == 15)
                {
                    int b;
                    do { b = src[ip++]; literals += b; } while (b == 255);
                }
                int take = Math.Min(literals, dst.Length - op);
                src.Slice(ip, take).CopyTo(dst[op..]);
                op += take;
                ip += literals;
                // The output is complete: what follows is padding, or data nobody asked for.
                if (take < literals || ip >= src.Length || op >= dst.Length) break;

                int offset = src[ip] | (src[ip + 1] << 8);
                ip += 2;
                int length = token & 15;
                if (length == 15)
                {
                    int b;
                    do { b = src[ip++]; length += b; } while (b == 255);
                }
                length = Math.Min(length + MinMatch, dst.Length - op);
                int from = op - offset;
                if (offset == 0 || from < 0) throw new FsException("Corrupt LZ4 data.");
                if (offset >= length)
                {
                    dst.Slice(from, length).CopyTo(dst[op..]);
                    op += length;
                }
                else
                {
                    for (int i = 0; i < length; i++) dst[op++] = dst[from++];
                }
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException)
        {
            throw new FsException("Corrupt LZ4 data.");
        }
        return op;
    }
}
