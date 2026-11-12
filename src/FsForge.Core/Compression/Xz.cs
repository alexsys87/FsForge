using System.Buffers.Binary;

namespace FsForge.Core.Compression;

/// <summary>CRC-32 (IEEE 802.3), slicing-by-4.</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[4 * 256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        for (int i = 0; i < 256; i++)
        {
            uint c = t[i];
            for (int s = 1; s < 4; s++)
            {
                c = t[c & 0xFF] ^ (c >> 8);
                t[s * 256 + i] = c;
            }
        }
        return t;
    }

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        uint[] t = Table;
        uint c = ~crc;
        int i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            c ^= BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
            c = t[768 + (c & 0xFF)] ^ t[512 + ((c >> 8) & 0xFF)] ^ t[256 + ((c >> 16) & 0xFF)] ^ t[c >> 24];
        }
        for (; i < data.Length; i++) c = t[(c ^ data[i]) & 0xFF] ^ (c >> 8);
        return ~c;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => Update(0, data);
}

/// <summary>Branch/call/jump converters used as XZ pre-filters for executable code.</summary>
public enum BcjFilter
{
    X86 = 4,
    PowerPc = 5,
    Ia64 = 6,
    Arm = 7,
    ArmThumb = 8,
    Sparc = 9,
    Arm64 = 10,
    RiscV = 11,
}

internal static class Bcj
{
    public static bool IsSupported(BcjFilter filter) =>
        filter is BcjFilter.X86 or BcjFilter.PowerPc or BcjFilter.Arm or BcjFilter.ArmThumb or BcjFilter.Sparc or BcjFilter.Arm64;

    /// <summary>Converts the buffer in place. The start offset of the filter is always zero.</summary>
    public static void Apply(BcjFilter filter, Span<byte> buf, bool encode)
    {
        switch (filter)
        {
            case BcjFilter.X86: X86(buf, encode); break;
            case BcjFilter.PowerPc: PowerPc(buf, encode); break;
            case BcjFilter.Arm: Arm(buf, encode); break;
            case BcjFilter.ArmThumb: ArmThumb(buf, encode); break;
            case BcjFilter.Sparc: Sparc(buf, encode); break;
            case BcjFilter.Arm64: Arm64(buf, encode); break;
            default: throw new FsException($"The XZ filter '{filter}' is not supported.");
        }
    }

    private static bool Test86(byte b) => b == 0 || b == 0xFF;

    private static void X86(Span<byte> buffer, bool encode)
    {
        ReadOnlySpan<uint> maskToBit = stackalloc uint[] { 0, 1, 2, 2, 3 };
        uint prevMask = 0;
        uint prevPos = unchecked((uint)-5);
        if (buffer.Length < 5) return;
        int limit = buffer.Length - 5;
        int pos = 0;
        while (pos <= limit)
        {
            byte b = buffer[pos];
            if (b != 0xE8 && b != 0xE9)
            {
                pos++;
                continue;
            }
            uint offset = (uint)pos - prevPos;
            prevPos = (uint)pos;
            if (offset > 5)
            {
                prevMask = 0;
            }
            else
            {
                for (uint i = 0; i < offset; i++)
                {
                    prevMask &= 0x77;
                    prevMask <<= 1;
                }
            }
            b = buffer[pos + 4];
            if (Test86(b) && (prevMask >> 1) <= 4 && (prevMask >> 1) != 3)
            {
                uint src = ((uint)b << 24) | ((uint)buffer[pos + 3] << 16) | ((uint)buffer[pos + 2] << 8) | buffer[pos + 1];
                uint dest;
                while (true)
                {
                    dest = encode ? src + ((uint)pos + 5) : src - ((uint)pos + 5);
                    if (prevMask == 0) break;
                    int i = (int)maskToBit[(int)(prevMask >> 1)];
                    b = (byte)(dest >> (24 - i * 8));
                    if (!Test86(b)) break;
                    src = dest ^ ((1u << (32 - i * 8)) - 1);
                }
                buffer[pos + 4] = (byte)~(((dest >> 24) & 1) - 1);
                buffer[pos + 3] = (byte)(dest >> 16);
                buffer[pos + 2] = (byte)(dest >> 8);
                buffer[pos + 1] = (byte)dest;
                pos += 5;
                prevMask = 0;
            }
            else
            {
                pos++;
                prevMask |= 1;
                if (Test86(b)) prevMask |= 0x10;
            }
        }
    }

    private static void Arm(Span<byte> b, bool encode)
    {
        int size = b.Length & ~3;
        for (int i = 0; i < size; i += 4)
        {
            if (b[i + 3] != 0xEB) continue;
            uint src = ((uint)b[i + 2] << 16) | ((uint)b[i + 1] << 8) | b[i];
            src <<= 2;
            uint dest = encode ? (uint)i + 8 + src : src - ((uint)i + 8);
            dest >>= 2;
            b[i + 2] = (byte)(dest >> 16);
            b[i + 1] = (byte)(dest >> 8);
            b[i] = (byte)dest;
        }
    }

    private static void ArmThumb(Span<byte> b, bool encode)
    {
        if (b.Length < 4) return;
        int size = b.Length - 4;
        for (int i = 0; i <= size; i += 2)
        {
            if ((b[i + 1] & 0xF8) != 0xF0 || (b[i + 3] & 0xF8) != 0xF8) continue;
            uint src = (((uint)b[i + 1] & 7) << 19) | ((uint)b[i] << 11) | (((uint)b[i + 3] & 7) << 8) | b[i + 2];
            src <<= 1;
            uint dest = encode ? (uint)i + 4 + src : src - ((uint)i + 4);
            dest >>= 1;
            b[i + 1] = (byte)(0xF0 | ((dest >> 19) & 7));
            b[i] = (byte)(dest >> 11);
            b[i + 3] = (byte)(0xF8 | ((dest >> 8) & 7));
            b[i + 2] = (byte)dest;
            i += 2;
        }
    }

    private static void Arm64(Span<byte> b, bool encode)
    {
        int size = b.Length & ~3;
        for (int i = 0; i < size; i += 4)
        {
            uint pc = (uint)i;
            uint instr = BinaryPrimitives.ReadUInt32LittleEndian(b[i..]);
            if ((instr >> 26) == 0x25)
            {
                uint src = instr;
                instr = 0x94000000;
                pc >>= 2;
                if (!encode) pc = 0u - pc;
                instr |= (src + pc) & 0x03FFFFFF;
                BinaryPrimitives.WriteUInt32LittleEndian(b[i..], instr);
            }
            else if ((instr & 0x9F000000) == 0x90000000)
            {
                uint src = ((instr >> 29) & 3) | ((instr >> 3) & 0x001FFFFC);
                if (((src + 0x00020000) & 0x001C0000) != 0) continue;
                instr &= 0x9000001F;
                pc >>= 12;
                if (!encode) pc = 0u - pc;
                uint dest = src + pc;
                instr |= (dest & 3) << 29;
                instr |= (dest & 0x0003FFFC) << 3;
                instr |= (0u - (dest & 0x00020000)) & 0x00E00000;
                BinaryPrimitives.WriteUInt32LittleEndian(b[i..], instr);
            }
        }
    }

    private static void PowerPc(Span<byte> b, bool encode)
    {
        int size = b.Length & ~3;
        for (int i = 0; i < size; i += 4)
        {
            if ((b[i] >> 2) != 0x12 || (b[i + 3] & 3) != 1) continue;
            uint src = (((uint)b[i] & 3) << 24) | ((uint)b[i + 1] << 16) | ((uint)b[i + 2] << 8) | ((uint)b[i + 3] & ~3u);
            uint dest = encode ? (uint)i + src : src - (uint)i;
            b[i] = (byte)(0x48 | ((dest >> 24) & 0x03));
            b[i + 1] = (byte)(dest >> 16);
            b[i + 2] = (byte)(dest >> 8);
            b[i + 3] &= 0x03;
            b[i + 3] |= (byte)dest;
        }
    }

    private static void Sparc(Span<byte> b, bool encode)
    {
        int size = b.Length & ~3;
        for (int i = 0; i < size; i += 4)
        {
            if (!((b[i] == 0x40 && (b[i + 1] & 0xC0) == 0x00) || (b[i] == 0x7F && (b[i + 1] & 0xC0) == 0xC0))) continue;
            uint src = ((uint)b[i] << 24) | ((uint)b[i + 1] << 16) | ((uint)b[i + 2] << 8) | b[i + 3];
            src <<= 2;
            uint dest = encode ? (uint)i + src : src - (uint)i;
            dest >>= 2;
            dest = (((0u - ((dest >> 22) & 1)) << 22) & 0x3FFFFFFF) | (dest & 0x3FFFFF) | 0x40000000;
            b[i] = (byte)(dest >> 24);
            b[i + 1] = (byte)(dest >> 16);
            b[i + 2] = (byte)(dest >> 8);
            b[i + 3] = (byte)dest;
        }
    }
}

/// <summary>Helpers for the .xz container: one stream holding one block.</summary>
internal static class XzFormat
{
    public static readonly byte[] HeaderMagic = { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 };
    private const ulong FilterLzma2 = 0x21;

    public static int CheckSize(int checkType) => checkType switch
    {
        0 => 0,
        1 or 2 or 3 => 4,
        4 or 5 or 6 => 8,
        7 or 8 or 9 => 16,
        10 or 11 or 12 => 32,
        _ => 64,
    };

    /// <summary>Smallest LZMA2 dictionary size code that covers <paramref name="size"/> bytes.</summary>
    public static int DictCode(long size)
    {
        for (int code = 0; code < 40; code++)
            if (DictSizeFromCode(code) >= size) return code;
        return 40;
    }

    public static long DictSizeFromCode(int code)
    {
        if (code > 40) throw new FsException("Corrupt XZ data: invalid dictionary size.");
        if (code == 40) return uint.MaxValue;
        return (2L | (code & 1)) << (code / 2 + 11);
    }

    public static int WriteVli(Span<byte> dst, ulong value)
    {
        int i = 0;
        while (value >= 0x80)
        {
            dst[i++] = (byte)(value | 0x80);
            value >>= 7;
        }
        dst[i++] = (byte)value;
        return i;
    }

    public static ulong ReadVli(ReadOnlySpan<byte> src, ref int pos)
    {
        ulong value = 0;
        for (int shift = 0; shift < 63; shift += 7)
        {
            if (pos >= src.Length) throw new FsException("Corrupt XZ data: truncated integer.");
            byte b = src[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
        }
        throw new FsException("Corrupt XZ data: integer is too long.");
    }

    /// <summary>Parsed block header.</summary>
    public struct BlockHeader
    {
        public int HeaderSize;
        public long CompressedSize;     // -1 when absent
        public long UncompressedSize;   // -1 when absent
        public int DictCode;
        public BcjFilter[] Filters;
    }

    public static BlockHeader ParseBlockHeader(ReadOnlySpan<byte> src)
    {
        int size = (src[0] + 1) * 4;
        if (size > src.Length) throw new FsException("Corrupt XZ data: truncated block header.");
        ReadOnlySpan<byte> h = src[..size];
        if (Crc32.Compute(h[..(size - 4)]) != BinaryPrimitives.ReadUInt32LittleEndian(h[(size - 4)..]))
            throw new FsException("Corrupt XZ data: block header checksum mismatch.");
        int flags = h[1];
        if ((flags & 0x3C) != 0) throw new FsException("Unsupported XZ block header flags.");
        int filterCount = (flags & 3) + 1;
        int pos = 2;
        var result = new BlockHeader { HeaderSize = size, CompressedSize = -1, UncompressedSize = -1, DictCode = -1 };
        if ((flags & 0x40) != 0) result.CompressedSize = (long)ReadVli(h, ref pos);
        if ((flags & 0x80) != 0) result.UncompressedSize = (long)ReadVli(h, ref pos);
        var filters = new List<BcjFilter>();
        for (int i = 0; i < filterCount; i++)
        {
            ulong id = ReadVli(h, ref pos);
            int propsSize = (int)ReadVli(h, ref pos);
            if (pos + propsSize > size - 4) throw new FsException("Corrupt XZ data: bad filter properties.");
            if (id == FilterLzma2)
            {
                if (i != filterCount - 1 || propsSize != 1) throw new FsException("Corrupt XZ data: bad LZMA2 filter.");
                result.DictCode = h[pos];
            }
            else if (id is >= 4 and <= 11)
            {
                if (propsSize == 4 && BinaryPrimitives.ReadUInt32LittleEndian(h[pos..]) != 0)
                    throw new FsException("XZ BCJ filters with a start offset are not supported.");
                filters.Add((BcjFilter)id);
            }
            else
            {
                throw new FsException($"Unsupported XZ filter 0x{id:X}.");
            }
            pos += propsSize;
        }
        if (result.DictCode < 0) throw new FsException("Corrupt XZ data: the LZMA2 filter is missing.");
        result.Filters = filters.ToArray();
        return result;
    }

    /// <summary>
    /// Decodes a raw LZMA2 stream into a flat buffer. Returns the number of produced bytes;
    /// <paramref name="consumed"/> receives the size of the LZMA2 stream including the end marker.
    /// </summary>
    public static int DecodeLzma2(LzmaDecoder decoder, ReadOnlySpan<byte> src, Span<byte> dst, out int consumed)
    {
        int ip = 0, op = 0, dictStart = 0;
        bool needDictReset = true, needProps = true;
        while (true)
        {
            if (ip >= src.Length) throw new FsException("Corrupt XZ data: truncated LZMA2 stream.");
            int control = src[ip++];
            if (control == 0) break;

            if (control < 0x80)
            {
                if (control > 2) throw new FsException("Corrupt XZ data: invalid LZMA2 control byte.");
                if (control == 1)
                {
                    needDictReset = false;
                    decoder.ResetDictionary();
                    dictStart = op;
                }
                else if (needDictReset)
                {
                    throw new FsException("Corrupt XZ data: the first LZMA2 chunk does not reset the dictionary.");
                }
                if (ip + 2 > src.Length) throw new FsException("Corrupt XZ data: truncated LZMA2 chunk.");
                int size = ((src[ip] << 8) | src[ip + 1]) + 1;
                ip += 2;
                if (ip + size > src.Length || op + size > dst.Length) throw new FsException("Corrupt XZ data: bad uncompressed chunk.");
                src.Slice(ip, size).CopyTo(dst[op..]);
                decoder.SkipUncompressed(size);
                ip += size;
                op += size;
                continue;
            }

            if (ip + 4 > src.Length) throw new FsException("Corrupt XZ data: truncated LZMA2 chunk.");
            int unpacked = (((control & 0x1F) << 16) | (src[ip] << 8) | src[ip + 1]) + 1;
            int packed = ((src[ip + 2] << 8) | src[ip + 3]) + 1;
            ip += 4;
            int mode = (control >> 5) & 3;
            if (mode == 3)
            {
                needDictReset = false;
                decoder.ResetDictionary();
                dictStart = op;
            }
            else if (needDictReset)
            {
                throw new FsException("Corrupt XZ data: the first LZMA2 chunk does not reset the dictionary.");
            }
            if (mode >= 2)
            {
                if (ip >= src.Length) throw new FsException("Corrupt XZ data: truncated LZMA2 chunk.");
                decoder.SetProps(src[ip++]);
                needProps = false;
            }
            else if (needProps)
            {
                throw new FsException("Corrupt XZ data: LZMA2 properties are missing.");
            }
            else if (mode == 1)
            {
                decoder.ResetState();
            }
            if (ip + packed > src.Length || op + unpacked > dst.Length)
                throw new FsException("Corrupt XZ data: LZMA2 chunk exceeds the buffer.");
            decoder.Decode(dst, op, dictStart, unpacked, src.Slice(ip, packed));
            ip += packed;
            op += unpacked;
        }
        consumed = ip;
        return op;
    }

    /// <summary>Wraps a raw LZMA2 stream that is already located at dst[payloadOffset..] into an .xz stream.</summary>
    public static int HeaderSizeFor(int filterCount) => 12 + BlockHeaderMax(filterCount);

    private static int BlockHeaderMax(int filterCount) => 2 + 9 + 9 + filterCount * 2 + 3 + 3 + 4 + 4;

    /// <summary>
    /// Builds a complete single-block .xz stream around an LZMA2 payload.
    /// Layout of <paramref name="dst"/>: the payload must start at <paramref name="payloadOffset"/>,
    /// which has to be at least <see cref="HeaderSizeFor"/> bytes. Returns the start and length of the stream.
    /// </summary>
    public static (int Start, int Length) Wrap(byte[] dst, int payloadOffset, int payloadSize, long uncompressedSize,
        uint crc, int dictCode, ReadOnlySpan<BcjFilter> filters)
    {
        // Block header.
        Span<byte> bh = stackalloc byte[64];
        int p = 1;
        bh[p++] = (byte)(0xC0 | filters.Length);            // both sizes present, filter count - 1
        p += WriteVli(bh[p..], (ulong)payloadSize);
        p += WriteVli(bh[p..], (ulong)uncompressedSize);
        foreach (BcjFilter f in filters)
        {
            bh[p++] = (byte)f;
            bh[p++] = 0;
        }
        bh[p++] = (byte)FilterLzma2;
        bh[p++] = 1;
        bh[p++] = (byte)dictCode;
        while ((p & 3) != 0) bh[p++] = 0;
        int bhSize = p + 4;
        bh[0] = (byte)(bhSize / 4 - 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bh[p..], Crc32.Compute(bh[..p]));

        int start = payloadOffset - bhSize - 12;
        if (start < 0) throw new InvalidOperationException("Not enough headroom for the XZ header.");
        Span<byte> o = dst.AsSpan(start);
        HeaderMagic.CopyTo(o);
        o[6] = 0;
        o[7] = 1;                                           // check type: CRC32
        BinaryPrimitives.WriteUInt32LittleEndian(o[8..], Crc32.Compute(o.Slice(6, 2)));
        bh[..bhSize].CopyTo(o[12..]);

        int q = payloadOffset + payloadSize;
        while (((q - payloadOffset) & 3) != 0) dst[q++] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(q), crc);
        q += 4;

        // Index.
        int indexStart = q;
        dst[q++] = 0;
        dst[q++] = 1;
        q += WriteVli(dst.AsSpan(q), (ulong)(bhSize + payloadSize + 4));
        q += WriteVli(dst.AsSpan(q), (ulong)uncompressedSize);
        while (((q - indexStart) & 3) != 0) dst[q++] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(q), Crc32.Compute(dst.AsSpan(indexStart, q - indexStart)));
        q += 4;
        int indexSize = q - indexStart;

        // Footer.
        BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(q + 4), (uint)(indexSize / 4 - 1));
        dst[q + 8] = 0;
        dst[q + 9] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(q), Crc32.Compute(dst.AsSpan(q + 4, 6)));
        dst[q + 10] = (byte)'Y';
        dst[q + 11] = (byte)'Z';
        q += 12;
        return (start, q - start);
    }

    /// <summary>Bytes the container adds after the payload (padding, check, index, footer).</summary>
    public const int TrailerMax = 3 + 4 + 2 + 9 + 9 + 3 + 4 + 12;
}

/// <summary>
/// Forward-only stream that decompresses an .xz file (all streams, all blocks).
/// The whole dictionary is kept in a flat buffer that slides when it fills up.
/// </summary>
public sealed class XzInputStream : Stream
{
    private readonly Stream _input;
    private readonly bool _leaveOpen;
    private readonly LzmaDecoder _decoder = new();
    private byte[] _dict = Array.Empty<byte>();
    private int _dictPos;           // end of valid data in _dict
    private int _dictStart;         // oldest valid byte
    private int _readPos;           // next byte to hand out
    private int _dictSize;
    private readonly byte[] _chunk = new byte[1 << 16];
    private bool _inBlock, _finished, _needDictReset, _needProps;
    private int _checkSize;
    private long _blockCompressed;

    public XzInputStream(Stream input, bool leaveOpen = false)
    {
        _input = input;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    private void ReadFully(Span<byte> buffer)
    {
        int done = 0;
        while (done < buffer.Length)
        {
            int n = _input.Read(buffer[done..]);
            if (n <= 0) throw new FsException("Corrupt XZ data: unexpected end of file.");
            done += n;
        }
    }

    private int ReadByteOrEnd()
    {
        Span<byte> one = stackalloc byte[1];
        return _input.Read(one) == 1 ? one[0] : -1;
    }

    private bool StartStream()
    {
        // Skip stream padding (zero bytes, multiple of four) and detect the end of the file.
        int b;
        do { b = ReadByteOrEnd(); } while (b == 0);
        if (b < 0) return false;
        Span<byte> hdr = stackalloc byte[12];
        hdr[0] = (byte)b;
        ReadFully(hdr[1..]);
        if (!hdr[..6].SequenceEqual(XzFormat.HeaderMagic)) throw new FsException("Not an XZ stream.");
        if (hdr[6] != 0 || (hdr[7] & 0xF0) != 0) throw new FsException("Unsupported XZ stream flags.");
        if (Crc32.Compute(hdr.Slice(6, 2)) != BinaryPrimitives.ReadUInt32LittleEndian(hdr[8..]))
            throw new FsException("Corrupt XZ data: stream header checksum mismatch.");
        _checkSize = XzFormat.CheckSize(hdr[7] & 0x0F);
        return true;
    }

    private bool StartBlock()
    {
        int first = ReadByteOrEnd();
        if (first < 0) throw new FsException("Corrupt XZ data: unexpected end of file.");
        if (first == 0)
        {
            SkipIndexAndFooter();
            return false;
        }
        int size = (first + 1) * 4;
        byte[] header = new byte[size];
        header[0] = (byte)first;
        ReadFully(header.AsSpan(1));
        XzFormat.BlockHeader bh = XzFormat.ParseBlockHeader(header);
        if (bh.Filters.Length != 0) throw new FsException("XZ files with BCJ filters are not supported for streaming.");
        long dictSize = Math.Max(4096, XzFormat.DictSizeFromCode(bh.DictCode));
        if (dictSize > 1 << 30) throw new FsException("The XZ dictionary is too large.");
        _dictSize = (int)dictSize;
        int need = (int)Math.Min((long)_dictSize + (1 << 21) + (1 << 16), int.MaxValue - 64);
        if (_dict.Length < need) _dict = new byte[need];
        _dictPos = _dictStart = _readPos = 0;
        _needDictReset = true;
        _needProps = true;
        _blockCompressed = 0;
        _inBlock = true;
        return true;
    }

    private void SkipIndexAndFooter()
    {
        // Index: records (VLI pairs), padding and CRC32. The index indicator was consumed already.
        long consumed = 1;
        ulong ReadVli()
        {
            ulong v = 0;
            for (int shift = 0; shift < 63; shift += 7)
            {
                int b = ReadByteOrEnd();
                if (b < 0) throw new FsException("Corrupt XZ data: truncated index.");
                consumed++;
                v |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return v;
            }
            throw new FsException("Corrupt XZ data: bad index.");
        }
        ulong records = ReadVli();
        for (ulong i = 0; i < records; i++)
        {
            ReadVli();
            ReadVli();
        }
        Span<byte> tail = stackalloc byte[3 + 4 + 12];
        int pad = (int)((4 - consumed % 4) % 4);
        ReadFully(tail[..(pad + 4 + 12)]);
        if (tail[pad + 4 + 10] != 'Y' || tail[pad + 4 + 11] != 'Z') throw new FsException("Corrupt XZ data: bad stream footer.");
    }

    /// <summary>Decodes the next LZMA2 chunk into the dictionary buffer. Returns false at the end of the block.</summary>
    private bool DecodeChunk()
    {
        int control = ReadByteOrEnd();
        if (control < 0) throw new FsException("Corrupt XZ data: unexpected end of file.");
        _blockCompressed++;
        if (control == 0)
        {
            Span<byte> skip = stackalloc byte[3 + 64];
            int pad = (int)((4 - _blockCompressed % 4) % 4);
            ReadFully(skip[..(pad + _checkSize)]);
            _inBlock = false;
            return false;
        }

        // Slide the window when the next chunk might not fit.
        if (_dictPos + (1 << 21) > _dict.Length)
        {
            int keep = Math.Min(_dictSize, _dictPos - _dictStart);
            Buffer.BlockCopy(_dict, _dictPos - keep, _dict, 0, keep);
            _dictStart = 0;
            _dictPos = keep;
            _readPos = keep;
        }

        Span<byte> hdr = stackalloc byte[5];
        if (control < 0x80)
        {
            if (control > 2) throw new FsException("Corrupt XZ data: invalid LZMA2 control byte.");
            if (control == 1)
            {
                _needDictReset = false;
                _decoder.ResetDictionary();
                _dictStart = _dictPos;
            }
            else if (_needDictReset)
            {
                throw new FsException("Corrupt XZ data: the first LZMA2 chunk does not reset the dictionary.");
            }
            ReadFully(hdr[..2]);
            int size = ((hdr[0] << 8) | hdr[1]) + 1;
            ReadFully(_dict.AsSpan(_dictPos, size));
            _decoder.SkipUncompressed(size);
            _dictPos += size;
            _blockCompressed += 2 + size;
            return true;
        }

        ReadFully(hdr[..4]);
        int unpacked = (((control & 0x1F) << 16) | (hdr[0] << 8) | hdr[1]) + 1;
        int packed = ((hdr[2] << 8) | hdr[3]) + 1;
        _blockCompressed += 4;
        int mode = (control >> 5) & 3;
        if (mode == 3)
        {
            _needDictReset = false;
            _decoder.ResetDictionary();
            _dictStart = _dictPos;
        }
        else if (_needDictReset)
        {
            throw new FsException("Corrupt XZ data: the first LZMA2 chunk does not reset the dictionary.");
        }
        if (mode >= 2)
        {
            int props = ReadByteOrEnd();
            if (props < 0) throw new FsException("Corrupt XZ data: unexpected end of file.");
            _blockCompressed++;
            _decoder.SetProps(props);
            _needProps = false;
        }
        else if (_needProps)
        {
            throw new FsException("Corrupt XZ data: LZMA2 properties are missing.");
        }
        else if (mode == 1)
        {
            _decoder.ResetState();
        }
        ReadFully(_chunk.AsSpan(0, packed));
        _blockCompressed += packed;
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
            if (_finished) return 0;
            if (!_inBlock)
            {
                if (!_streamOpen)
                {
                    if (!StartStream())
                    {
                        _finished = true;
                        return 0;
                    }
                    _streamOpen = true;
                }
                if (!StartBlock())
                {
                    _streamOpen = false;
                    continue;
                }
            }
            DecodeChunk();
        }
        int n = Math.Min(buffer.Length, _dictPos - _readPos);
        _dict.AsSpan(_readPos, n).CopyTo(buffer);
        _readPos += n;
        return n;
    }

    private bool _streamOpen;

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
