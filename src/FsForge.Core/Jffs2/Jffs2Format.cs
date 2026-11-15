using System.Buffers.Binary;

namespace FsForge.Core.Jffs2;

/// <summary>Compressor of a JFFS2 data node (jffs2_raw_inode.compr).</summary>
public enum Jffs2Compression : byte
{
    None = 0,
    /// <summary>A hole: the node covers <c>dsize</c> zero bytes and stores nothing.</summary>
    Zero = 1,
    Rtime = 2,
    RubinMips = 3,
    Copy = 4,
    DynRubin = 5,
    Zlib = 6,
    Lzo = 7,
    Lzma = 8,
}

/// <summary>Parameters of a new JFFS2 image (the options of mkfs.jffs2).</summary>
public sealed class Jffs2Options
{
    /// <summary>Byte order of the target CPU (mkfs.jffs2 -b / -l). MIPS routers are often big-endian.</summary>
    public bool BigEndian { get; set; }

    /// <summary>Erase block size of the flash (mkfs.jffs2 -e). Nodes never cross an erase block.</summary>
    public int EraseBlockSize { get; set; } = 64 * 1024;

    /// <summary>
    /// A clean marker at the start of every erase block (the default of mkfs.jffs2). NAND flash keeps
    /// it in the spare area instead, so images for NAND are made without (mkfs.jffs2 -n).
    /// </summary>
    public bool Cleanmarkers { get; set; } = true;

    /// <summary>Size of a clean marker (mkfs.jffs2 -c); 12 unless the kernel was told otherwise.</summary>
    public int CleanmarkerSize { get; set; } = 12;

    /// <summary>
    /// Compressor of file data. zlib falls back to rtime like mkfs.jffs2 does; a block that does not
    /// shrink is stored as it is. lzo needs a kernel with CONFIG_JFFS2_LZO.
    /// </summary>
    public Jffs2Compression Compression { get; set; } = Jffs2Compression.Zlib;

    /// <summary>Largest amount of file data in one node (mkfs.jffs2 -s): the page size of the target.</summary>
    public int PageSize { get; set; } = 4096;

    /// <summary>Fill the last erase block up with 0xFF (mkfs.jffs2 -p without a size).</summary>
    public bool PadToEraseBlock { get; set; }

    /// <summary>
    /// Write extended attributes and POSIX ACLs (mkfs.jffs2 --with-xattr). Off by default: a kernel built
    /// without CONFIG_JFFS2_FS_XATTR refuses to mount an image that has them.
    /// </summary>
    public bool StoreXattrs { get; set; }

    public bool AllRoot { get; set; }

    public Jffs2Options Clone() => (Jffs2Options)MemberwiseClone();

    /// <summary>Options that reproduce the layout of an existing image.</summary>
    public static Jffs2Options FromInfo(Jffs2Info info) => new()
    {
        BigEndian = info.BigEndian,
        EraseBlockSize = info.EraseBlockSize,
        Cleanmarkers = info.Cleanmarkers,
        CleanmarkerSize = info.Cleanmarkers ? info.CleanmarkerSize : 12,
        Compression = info.MainCompression is Jffs2Compression.Lzo or Jffs2Compression.Rtime or Jffs2Compression.None
            ? info.MainCompression : Jffs2Compression.Zlib,
        StoreXattrs = info.HasXattrs,
    };

    public void Validate()
    {
        if (EraseBlockSize < 4096 || EraseBlockSize > 16 << 20 || (EraseBlockSize & (EraseBlockSize - 1)) != 0)
            throw new FsException("The JFFS2 erase block size must be a power of two between 4 KiB and 16 MiB.");
        if (PageSize < 512 || PageSize > EraseBlockSize / 2 || (PageSize & (PageSize - 1)) != 0)
            throw new FsException("The JFFS2 page size must be a power of two of at least 512 bytes and at most half an erase block.");
        if (CleanmarkerSize < 12 || CleanmarkerSize > 256 || CleanmarkerSize % 4 != 0)
            throw new FsException("The JFFS2 clean marker size must be a multiple of 4 between 12 and 256 bytes.");
        if (Compression is not (Jffs2Compression.Zlib or Jffs2Compression.Lzo or Jffs2Compression.Rtime or Jffs2Compression.None))
            throw new FsException($"Writing JFFS2 with the {Compression} compressor is not supported.");
    }
}

/// <summary>Facts about an opened JFFS2 image.</summary>
public sealed class Jffs2Info
{
    public bool BigEndian { get; init; }

    /// <summary>Erase block size: from the clean markers when there are several, otherwise estimated.</summary>
    public int EraseBlockSize { get; init; }

    /// <summary>True when <see cref="EraseBlockSize"/> comes from the spacing of clean markers.</summary>
    public bool EraseBlockSizeKnown { get; init; }

    public bool Cleanmarkers { get; init; }
    public int CleanmarkerSize { get; init; }

    /// <summary>The compressor used by most data nodes (zlib when no node is compressed).</summary>
    public Jffs2Compression MainCompression { get; init; }

    public IReadOnlyDictionary<Jffs2Compression, int> CompressionCounts { get; init; } = new Dictionary<Jffs2Compression, int>();

    /// <summary>Valid nodes of all kinds, including the ones that newer nodes replace.</summary>
    public int Nodes { get; init; }

    /// <summary>Nodes marked obsolete by the kernel, or replaced by newer versions.</summary>
    public int ObsoleteNodes { get; init; }

    /// <summary>Places where a node was expected but the header or a checksum was wrong.</summary>
    public int DamagedNodes { get; init; }

    /// <summary>Erase block summaries (CONFIG_JFFS2_SUMMARY, sumtool) were found; they are not needed to read.</summary>
    public bool HasSummary { get; init; }

    public bool HasXattrs { get; init; }

    /// <summary>End of the last node: the image without the erased space behind it.</summary>
    public long UsedBytes { get; init; }

    public long ImageBytes { get; init; }
}

/// <summary>On-disk constants and helpers shared by the JFFS2 reader and writer.</summary>
internal static class Jffs2Format
{
    public const ushort Magic = 0x1985, OldMagic = 0x1984;

    public const ushort CompatMask = 0xC000, Incompat = 0xC000, RoCompat = 0x8000, RwCompatCopy = 0x4000, Accurate = 0x2000;

    public const ushort NodeDirent = 0xE001, NodeInode = 0xE002, NodeCleanmarker = 0x2003, NodePadding = 0x2004,
        NodeSummary = 0x2006, NodeXattr = 0xE008, NodeXref = 0xE009;

    public const int HeaderSize = 12, DirentSize = 40, InodeSize = 68, XattrSize = 32, XrefSize = 28;
    public const int MaxNameLength = 254;

    public const byte XprefixUser = 1, XprefixSecurity = 2, XprefixAclAccess = 3, XprefixAclDefault = 4, XprefixTrusted = 5;
    public const uint XattrDeleted = 0xFFFFFFFF;     // version of a deleted attribute value
    public const uint XrefDeleted = 1;               // set in the sequence number of a deleted reference

    public const int DtFifo = 1, DtChr = 2, DtDir = 4, DtBlk = 6, DtReg = 8, DtLnk = 10, DtSock = 12;

    public static int Pad4(int n) => (n + 3) & ~3;
    public static long Pad4(long n) => (n + 3) & ~3L;

    private static readonly uint[] CrcTable = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320 : c >> 1;
            t[i] = c;
        }
        return t;
    }

    /// <summary>The CRC-32 of JFFS2: the kernel's crc32(0, ...), i.e. reflected, seeded with 0 and not inverted.</summary>
    public static uint Crc(ReadOnlySpan<byte> data, uint crc = 0)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    public static ushort U16(ReadOnlySpan<byte> b, int o, bool be) =>
        be ? BinaryPrimitives.ReadUInt16BigEndian(b[o..]) : BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);

    public static uint U32(ReadOnlySpan<byte> b, int o, bool be) =>
        be ? BinaryPrimitives.ReadUInt32BigEndian(b[o..]) : BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);

    public static void W16(Span<byte> b, int o, int v, bool be)
    {
        if (be) BinaryPrimitives.WriteUInt16BigEndian(b[o..], (ushort)v);
        else BinaryPrimitives.WriteUInt16LittleEndian(b[o..], (ushort)v);
    }

    public static void W32(Span<byte> b, int o, uint v, bool be)
    {
        if (be) BinaryPrimitives.WriteUInt32BigEndian(b[o..], v);
        else BinaryPrimitives.WriteUInt32LittleEndian(b[o..], v);
    }

    /// <summary>Byte order of a node header at the start of the span, or null when it is not a valid one.</summary>
    public static bool? HeaderOrder(ReadOnlySpan<byte> h)
    {
        if (h.Length < HeaderSize) return null;
        foreach (bool be in new[] { false, true })
        {
            if (U16(h, 0, be) != Magic) continue;
            if (HeaderCrcValid(h, be) && U32(h, 4, be) >= HeaderSize) return be;
        }
        return null;
    }

    /// <summary>
    /// Checks the header CRC. The kernel marks a node obsolete by clearing its "accurate" bit in place, without
    /// rewriting the CRC, so the CRC is computed as if the bit were set (as the kernel's scan does).
    /// </summary>
    public static bool HeaderCrcValid(ReadOnlySpan<byte> h, bool be)
    {
        Span<byte> copy = stackalloc byte[8];
        h[..8].CopyTo(copy);
        W16(copy, 2, U16(h, 2, be) | Accurate, be);
        return Crc(copy) == U32(h, 8, be);
    }

    // ------------------------------------------------------------- rtime

    /// <summary>The "rtime" run-length compressor of JFFS2. Returns -1 when the data does not shrink.</summary>
    public static int RtimeCompress(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        Span<ushort> positions = stackalloc ushort[256];
        positions.Clear();
        int pos = 0, outpos = 0;
        if (dst.Length <= 3) return -1;
        while (pos < src.Length && outpos <= dst.Length - 2)
        {
            int runlen = 0;
            byte value = src[pos];
            dst[outpos++] = src[pos++];
            int backpos = positions[value];
            positions[value] = (ushort)pos;
            while (backpos < pos && pos < src.Length && src[pos] == src[backpos++] && runlen < 255)
            {
                pos++;
                runlen++;
            }
            dst[outpos++] = (byte)runlen;
        }
        // Like the kernel: give up unless all of the input is covered and the output is smaller.
        return pos < src.Length || outpos >= pos ? -1 : outpos;
    }

    public static void RtimeDecompress(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        Span<ushort> positions = stackalloc ushort[256];
        positions.Clear();
        int pos = 0, outpos = 0;
        while (outpos < dst.Length)
        {
            if (pos + 2 > src.Length) throw new FsException("Corrupt rtime data in a JFFS2 node.");
            byte value = src[pos++];
            dst[outpos++] = value;
            int repeat = src[pos++];
            int backoffs = positions[value];
            positions[value] = (ushort)outpos;
            if (outpos + repeat > dst.Length) throw new FsException("Corrupt rtime data in a JFFS2 node.");
            while (repeat-- > 0) dst[outpos++] = dst[backoffs++];
        }
    }

    // --------------------------------------------------------------- ACL

    /// <summary>A POSIX ACL in the JFFS2 form (byte order of the image) as the getfacl xattr form; null when malformed.</summary>
    public static byte[]? AclToXattr(ReadOnlySpan<byte> acl, bool be)
    {
        if (!be) return Ext.ExtFormat.AclDiskToXattr(acl);
        // The layout is the ext4 one with every field in the byte order of the image: swap it to little-endian first.
        if (acl.Length < 4) return null;
        byte[] le = acl.ToArray();
        Swap32(le, 0);
        int p = 4;
        while (p + 4 <= le.Length)
        {
            Swap16(le, p);
            Swap16(le, p + 2);
            int tag = BinaryPrimitives.ReadUInt16LittleEndian(le.AsSpan(p));
            if (tag is 2 or 8)
            {
                if (p + 8 > le.Length) return null;
                Swap32(le, p + 4);
                p += 8;
            }
            else
            {
                p += 4;
            }
        }
        return Ext.ExtFormat.AclDiskToXattr(le);
    }

    /// <summary>The getfacl xattr form of a POSIX ACL in the JFFS2 form; null when malformed.</summary>
    public static byte[]? AclFromXattr(ReadOnlySpan<byte> xattr, bool be)
    {
        byte[]? le = Ext.ExtFormat.AclXattrToDisk(xattr);
        if (le is null || !be) return le;
        Swap32(le, 0);
        int p = 4;
        while (p + 4 <= le.Length)
        {
            int tag = BinaryPrimitives.ReadUInt16LittleEndian(le.AsSpan(p));
            Swap16(le, p);
            Swap16(le, p + 2);
            if (tag is 2 or 8)
            {
                Swap32(le, p + 4);
                p += 8;
            }
            else
            {
                p += 4;
            }
        }
        return le;
    }

    private static void Swap16(byte[] b, int o) => (b[o], b[o + 1]) = (b[o + 1], b[o]);

    private static void Swap32(byte[] b, int o)
    {
        (b[o], b[o + 3]) = (b[o + 3], b[o]);
        (b[o + 1], b[o + 2]) = (b[o + 2], b[o + 1]);
    }

    public static string? XattrPrefix(byte prefix) => prefix switch
    {
        XprefixUser => "user.",
        XprefixSecurity => "security.",
        XprefixAclAccess => "system.posix_acl_access",
        XprefixAclDefault => "system.posix_acl_default",
        XprefixTrusted => "trusted.",
        _ => null,
    };
}
