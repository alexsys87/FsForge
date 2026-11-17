using System.Buffers.Binary;
using System.Text;

namespace FsForge.Core.F2fs;

/// <summary>Facts about an opened F2FS image.</summary>
public sealed class F2fsInfo
{
    public int BlockSize { get; init; }
    public long BlockCount { get; init; }
    public int SegmentCount { get; init; }
    public int MainSegments { get; init; }
    public uint Features { get; init; }
    public string VolumeName { get; init; } = "";
    public Guid Uuid { get; init; }
    public string Version { get; init; } = "";
    public ulong CheckpointVersion { get; init; }
    public uint CheckpointFlags { get; init; }
    public long ValidBlocks { get; init; }
    public long UserBlocks { get; init; }
    public long FreeSegments { get; init; }
    public uint ValidInodes { get; init; }

    /// <summary>Files whose content is encrypted (file-based encryption) and could not be read.</summary>
    public int EncryptedFiles { get; init; }

    /// <summary>Files stored compressed (F2FS compression).</summary>
    public int CompressedFiles { get; init; }

    /// <summary>The filesystem was not cleanly unmounted: the last checkpoint is not an unmount checkpoint.</summary>
    public bool NotCleanlyUnmounted => (CheckpointFlags & F2fsFormat.CpUmount) == 0;

    public IReadOnlyList<string> FeatureNames => F2fsFormat.FeatureNames(Features);
}

/// <summary>Parameters of a new F2FS image.</summary>
public sealed class F2fsOptions
{
    /// <summary>Size of the image in bytes; 0 makes it as small as the content allows (plus <see cref="ExtraFreeBytes"/>).</summary>
    public long SizeBytes { get; set; }

    /// <summary>Free space to leave for users when the size is automatic.</summary>
    public long ExtraFreeBytes { get; set; } = 16L << 20;

    /// <summary>Volume name (up to 512 UTF-16 characters).</summary>
    public string VolumeName { get; set; } = "";

    /// <summary>UUID of the filesystem; null makes a random one.</summary>
    public Guid? Uuid { get; set; }

    public bool StoreXattrs { get; set; } = true;
    public bool AllRoot { get; set; }

    public F2fsOptions Clone() => (F2fsOptions)MemberwiseClone();

    public static F2fsOptions FromInfo(F2fsInfo info) => new()
    {
        SizeBytes = info.BlockCount * info.BlockSize,
        VolumeName = info.VolumeName,
        Uuid = info.Uuid,
    };

    public void Validate()
    {
        if (SizeBytes < 0 || ExtraFreeBytes < 0) throw new FsException("The F2FS size must not be negative.");
        if (VolumeName.Length > F2fsFormat.MaxVolumeName) throw new FsException("The F2FS volume name must not be longer than 512 characters.");
    }
}

/// <summary>On-disk constants and helpers of F2FS (linux/include/linux/f2fs_fs.h, fs/f2fs/f2fs.h).</summary>
public static class F2fsFormat
{
    public const uint Magic = 0xF2F52010, XattrMagic = 0xF2F52011;
    public const int SuperOffset = 1024, SuperSize = 3072, SuperCrcOffset = 3068, MaxVolumeName = 512;
    public const int NodeFooterSize = 24, OffsetOfIAddr = 360, NameLen = 255, InlineXattrAddrs = 50;
    public const int SlotLen = 8, DirEntrySize = 11, SummarySize = 7, SumFooterSize = 5, NatEntrySize = 9, SitEntrySize = 74;
    public const int MaxDirHashDepth = 63;
    public const uint NullAddr = 0, NewAddr = 0xFFFFFFFF, CompressAddr = 0xFFFFFFFE;
    public const uint XattrNodeOffset = 0x1FFFFFFF;

    // Features of the superblock.
    public const uint FeatEncrypt = 0x1, FeatBlkzoned = 0x2, FeatAtomicWrite = 0x4, FeatExtraAttr = 0x8, FeatPrjquota = 0x10,
        FeatInodeChksum = 0x20, FeatFlexibleInlineXattr = 0x40, FeatQuotaIno = 0x80, FeatInodeCrtime = 0x100, FeatLostFound = 0x200,
        FeatVerity = 0x400, FeatSbChksum = 0x800, FeatCasefold = 0x1000, FeatCompression = 0x2000, FeatRo = 0x4000, FeatDeviceAlias = 0x8000;

    public static IReadOnlyList<string> FeatureNames(uint f)
    {
        string[] names = { "encrypt", "blkzoned", "atomic_write", "extra_attr", "project_quota", "inode_checksum", "flexible_inline_xattr",
            "quota", "inode_crtime", "lost_found", "verity", "sb_checksum", "casefold", "compression", "ro", "device_alias" };
        var list = new List<string>();
        for (int i = 0; i < 32; i++)
            if ((f & (1u << i)) != 0) list.Add(i < names.Length ? names[i] : $"0x{1u << i:X}");
        return list;
    }

    // Checkpoint flags.
    public const uint CpUmount = 0x1, CpOrphanPresent = 0x2, CpCompactSum = 0x4, CpError = 0x8, CpFsck = 0x10, CpNatBits = 0x80,
        CpTrimmed = 0x100, CpLargeNatBitmap = 0x400, CpDisabled = 0x1000;

    // i_inline flags.
    public const byte InlineXattr = 0x01, InlineData = 0x02, InlineDentry = 0x04, DataExist = 0x08, InlineDots = 0x10, ExtraAttr = 0x20;

    // i_advise bits and i_flags.
    public const byte AdviseEncrypt = 0x04;
    public const uint ComprFl = 0x4, CasefoldFl = 0x40000000;

    // Segment types (CURSEG_*): 0..2 data (hot, warm, cold), 3..5 node.
    public const int HotData = 0, WarmData = 1, ColdData = 2, HotNode = 3, WarmNode = 4, ColdNode = 5;

    // Compression algorithms.
    public const int CompressLzo = 0, CompressLz4 = 1, CompressZstd = 2, CompressLzoRle = 3;

    public static int AddrsPerInode(int blockSize) => (blockSize - OffsetOfIAddr - 20 - NodeFooterSize) / 4;
    public static int AddrsPerBlock(int blockSize) => (blockSize - NodeFooterSize) / 4;
    public static int NatEntriesPerBlock(int blockSize) => blockSize / NatEntrySize;
    public static int SitEntriesPerBlock(int blockSize) => blockSize / SitEntrySize;
    public static int EntriesInSum(int blockSize) => blockSize / 8;
    public static int SumJournalSize(int blockSize) => blockSize - SumFooterSize - SummarySize * EntriesInSum(blockSize);
    public static int DentriesInBlock(int blockSize) => blockSize * 8 / ((DirEntrySize + SlotLen) * 8 + 1);
    public static int DentryBitmapSize(int blockSize) => (DentriesInBlock(blockSize) + 7) / 8;

    /// <summary>The CRC-32 of F2FS: reflected, no inversion, seeded with the magic number (f2fs_crc32).</summary>
    public static uint Crc(ReadOnlySpan<byte> data, uint seed = Magic)
    {
        uint crc = seed;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0);
        }
        return crc;
    }

    /// <summary>The hash a directory entry is filed under (fs/f2fs/hash.c, without casefolding).</summary>
    public static uint DentryHash(ReadOnlySpan<byte> name)
    {
        if (name.Length <= 2 && name.Length > 0 && name[0] == (byte)'.' && (name.Length == 1 || name[1] == (byte)'.')) return 0;
        Span<uint> buf = stackalloc uint[] { 0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476 };
        Span<uint> input = stackalloc uint[4];
        int len = name.Length, p = 0;
        while (true)
        {
            Str2HashBuf(name[p..], len, input);
            Tea(buf, input);
            p += 16;
            if (len <= 16) break;
            len -= 16;
        }
        return buf[0];
    }

    private static void Str2HashBuf(ReadOnlySpan<byte> msg, int len, Span<uint> buf)
    {
        uint pad = (uint)len | ((uint)len << 8);
        pad |= pad << 16;
        uint val = pad;
        int num = 4, o = 0;
        if (len > num * 4) len = num * 4;
        for (int i = 0; i < len; i++)
        {
            if (i % 4 == 0) val = pad;
            val = msg[i] + (val << 8);
            if (i % 4 == 3)
            {
                buf[o++] = val;
                val = pad;
                num--;
            }
        }
        if (--num >= 0) buf[o++] = val;
        while (--num >= 0) buf[o++] = pad;
    }

    private static void Tea(Span<uint> buf, ReadOnlySpan<uint> input)
    {
        uint sum = 0, b0 = buf[0], b1 = buf[1], a = input[0], b = input[1], c = input[2], d = input[3];
        for (int n = 0; n < 16; n++)
        {
            sum += 0x9E3779B9;
            b0 += ((b1 << 4) + a) ^ (b1 + sum) ^ ((b1 >> 5) + b);
            b1 += ((b0 << 4) + c) ^ (b0 + sum) ^ ((b0 >> 5) + d);
        }
        buf[0] += b0;
        buf[1] += b1;
    }

    /// <summary>Buckets of a level of the directory hash table (dir_level 0 and above).</summary>
    public static long DirBuckets(int level, int dirLevel) => level + dirLevel < MaxDirHashDepth / 2 ? 1L << (level + dirLevel) : 1L << (MaxDirHashDepth / 2 - 1);

    public static int BucketBlocks(int level) => level < MaxDirHashDepth / 2 ? 2 : 4;

    /// <summary>The first block of a bucket.</summary>
    public static long DirBlockIndex(int level, int dirLevel, long bucket)
    {
        long index = 0;
        for (int i = 0; i < level; i++) index += DirBuckets(i, dirLevel) * BucketBlocks(i);
        return index + bucket * BucketBlocks(level);
    }

    public static int FileType(FsNodeType t) => t switch
    {
        FsNodeType.File => 1,
        FsNodeType.Directory => 2,
        FsNodeType.CharDevice => 3,
        FsNodeType.BlockDevice => 4,
        FsNodeType.Fifo => 5,
        FsNodeType.Socket => 6,
        FsNodeType.Symlink => 7,
        _ => 0,
    };

    /// <summary>The prefix an xattr name index stands for; null for the ones that are not shown (advise, encryption, verity).</summary>
    public static string? XattrPrefix(int index) => index switch
    {
        1 => "user.",
        2 => "system.posix_acl_access",
        3 => "system.posix_acl_default",
        4 => "trusted.",
        5 => "lustre.",
        6 => "security.",
        _ => null,
    };

    public static bool SplitXattrName(string full, out int index, out string suffix)
    {
        (index, suffix) = (0, "");
        if (full == "system.posix_acl_access") { index = 2; return true; }
        if (full == "system.posix_acl_default") { index = 3; return true; }
        foreach ((int i, string prefix) in new[] { (1, "user."), (4, "trusted."), (5, "lustre."), (6, "security.") })
            if (full.StartsWith(prefix, StringComparison.Ordinal) && full.Length > prefix.Length)
            {
                (index, suffix) = (i, full[prefix.Length..]);
                return true;
            }
        return false;
    }

    internal static uint U32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);
    internal static ushort U16(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b[at..]);
    internal static ulong U64(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt64LittleEndian(b[at..]);

    /// <summary>Bit <paramref name="nr"/> of an F2FS bitmap (SIT, NAT version bitmaps): the most significant bit first.</summary>
    internal static bool TestBitMsb(ReadOnlySpan<byte> map, long nr) => (map[(int)(nr >> 3)] & (0x80 >> (int)(nr & 7))) != 0;

    internal static void SetBitMsb(Span<byte> map, long nr) => map[(int)(nr >> 3)] |= (byte)(0x80 >> (int)(nr & 7));

    /// <summary>Bit <paramref name="nr"/> of a dentry bitmap: the least significant bit first.</summary>
    internal static bool TestBitLe(ReadOnlySpan<byte> map, int nr) => (map[nr >> 3] & (1 << (nr & 7))) != 0;

    internal static void SetBitLe(Span<byte> map, int nr) => map[nr >> 3] |= (byte)(1 << (nr & 7));

    public static string VolumeNameOf(ReadOnlySpan<byte> utf16)
    {
        int n = 0;
        while (n + 1 < utf16.Length && (utf16[n] != 0 || utf16[n + 1] != 0)) n += 2;
        return Encoding.Unicode.GetString(utf16[..n]);
    }
}
