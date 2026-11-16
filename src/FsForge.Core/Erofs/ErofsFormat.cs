using System.Numerics;

namespace FsForge.Core.Erofs;

public enum ErofsCompression
{
    /// <summary>Files are stored as they are.</summary>
    None,

    /// <summary>LZ4, fast match search.</summary>
    Lz4,

    /// <summary>LZ4 with a thorough match search (the same format as <see cref="Lz4"/>, smaller output).</summary>
    Lz4Hc,

    /// <summary>MicroLZMA: the best ratio, the slowest to write and to read.</summary>
    Lzma,

    /// <summary>DEFLATE (read only).</summary>
    Deflate,

    /// <summary>Zstandard (read only).</summary>
    Zstd,
}

/// <summary>Facts about an opened EROFS image.</summary>
public sealed class ErofsInfo
{
    public int BlockSize { get; init; }

    /// <summary>Size of the filesystem in blocks, as the superblock states it.</summary>
    public long Blocks { get; init; }

    public long SizeBytes => Blocks * BlockSize;
    public long InodeCount { get; init; }

    /// <summary>The time every inode without a time of its own carries (the "build time" of mkfs.erofs).</summary>
    public long BuildTime { get; init; }

    public Guid Uuid { get; init; }
    public string VolumeName { get; init; } = "";
    public uint FeatureCompat { get; init; }
    public uint FeatureIncompat { get; init; }

    /// <summary>Compression methods the superblock announces (empty for an uncompressed image).</summary>
    public IReadOnlyList<ErofsCompression> Algorithms { get; init; } = Array.Empty<ErofsCompression>();

    /// <summary>The method most compressed files use; <see cref="ErofsCompression.None"/> when nothing is compressed.</summary>
    public ErofsCompression Compression { get; init; }

    /// <summary>The largest compressed cluster seen (or announced by the superblock), in bytes.</summary>
    public int PclusterSize { get; init; }

    /// <summary>LZMA dictionary size from the superblock; 0 when LZMA is not used.</summary>
    public int LzmaDictionarySize { get; init; }

    public bool SuperblockChecksum => (FeatureCompat & ErofsFormat.CompatSbChecksum) != 0;
    public bool BigPcluster { get; init; }
    public bool TailPacking => (FeatureIncompat & ErofsFormat.IncompatZtailpacking) != 0;
    public bool Fragments { get; init; }
    public bool Deduplicated { get; init; }
    public bool ChunkedFiles => (FeatureIncompat & ErofsFormat.IncompatChunkedFile) != 0;

    /// <summary>Names of the features of the superblock, the way dump.erofs prints them.</summary>
    public IReadOnlyList<string> FeatureNames
    {
        get
        {
            var names = new List<string>();
            if ((FeatureCompat & ErofsFormat.CompatSbChecksum) != 0) names.Add("sb_csum");
            if ((FeatureCompat & ErofsFormat.CompatMtime) != 0) names.Add("mtime");
            if ((FeatureCompat & ErofsFormat.CompatXattrFilter) != 0) names.Add("xattr_filter");
            if ((FeatureIncompat & ErofsFormat.IncompatZeroPadding) != 0) names.Add("0padding");
            if ((FeatureIncompat & ErofsFormat.IncompatComprCfgs) != 0) names.Add(BigPcluster ? "big_pcluster" : "compr_cfgs");
            if ((FeatureIncompat & ErofsFormat.IncompatChunkedFile) != 0) names.Add("chunked_file");
            if ((FeatureIncompat & ErofsFormat.IncompatDeviceTable) != 0) names.Add("device_table");
            if ((FeatureIncompat & ErofsFormat.IncompatZtailpacking) != 0) names.Add("ztailpacking");
            if ((FeatureIncompat & ErofsFormat.IncompatFragments) != 0) names.Add(Fragments ? "fragments" : "dedupe");
            if ((FeatureIncompat & ErofsFormat.IncompatXattrPrefixes) != 0) names.Add("xattr_prefixes");
            return names;
        }
    }
}

/// <summary>Parameters of a new EROFS image (the options of mkfs.erofs).</summary>
public sealed class ErofsOptions
{
    public ErofsCompression Compression { get; set; } = ErofsCompression.Lz4Hc;

    /// <summary>
    /// Size of a compressed cluster: a multiple of the block size up to 1 MiB. Every cluster is unpacked
    /// as a whole, so larger clusters compress better and read slower. More than one block needs Linux 5.13.
    /// </summary>
    public int PclusterSize { get; set; } = ErofsFormat.BlockSize;

    /// <summary>LZ4HC search depth, 1 (fast) to 12 (thorough).</summary>
    public int Lz4Level { get; set; } = 9;

    /// <summary>LZMA effort: 0 = fast, 1 = normal, 2 = maximum.</summary>
    public int LzmaEffort { get; set; } = 1;

    /// <summary>LZMA dictionary size; 0 picks one that suits the cluster size.</summary>
    public int LzmaDictionarySize { get; set; }

    /// <summary>Keep the tail of a file that does not fill a block next to its inode instead of in a block of its own.</summary>
    public bool InlineData { get; set; } = true;

    /// <summary>Store identical files once.</summary>
    public bool Deduplicate { get; set; } = true;

    public bool StoreXattrs { get; set; } = true;

    /// <summary>Store an attribute value that many files share (SELinux labels) once.</summary>
    public bool ShareXattrs { get; set; } = true;

    /// <summary>Force owner and group of every inode to root.</summary>
    public bool AllRoot { get; set; }

    /// <summary>
    /// The time of the image; inodes with exactly this modification time take 32 bytes instead of 64.
    /// Null picks the modification time most files have.
    /// </summary>
    public long? BuildTime { get; set; }

    /// <summary>Filesystem UUID; null makes a new one.</summary>
    public Guid? Uuid { get; set; }

    /// <summary>Volume name, up to 16 bytes.</summary>
    public string VolumeName { get; set; } = "";

    /// <summary>Worker threads; 0 means one per processor.</summary>
    public int Threads { get; set; }

    public ErofsOptions Clone() => (ErofsOptions)MemberwiseClone();

    /// <summary>The LZMA dictionary size the image gets.</summary>
    public int EffectiveLzmaDictionary => LzmaDictionarySize > 0
        ? LzmaDictionarySize
        : (int)Math.Clamp(BitOperations.RoundUpToPowerOf2((uint)PclusterSize * 8), 1u << 20, (uint)ErofsFormat.MaxExtentBytes);

    /// <summary>Options that reproduce the settings of an existing image as closely as possible.</summary>
    public static ErofsOptions FromInfo(ErofsInfo info) => new()
    {
        Compression = info.Compression switch
        {
            // Methods that are read but not written fall back to the closest writable one.
            ErofsCompression.Deflate or ErofsCompression.Zstd => ErofsCompression.Lzma,
            ErofsCompression.Lz4 => ErofsCompression.Lz4Hc,
            var c => c,
        },
        PclusterSize = Math.Clamp(info.PclusterSize / ErofsFormat.BlockSize * ErofsFormat.BlockSize, ErofsFormat.BlockSize, ErofsFormat.MaxPclusterSize),
        LzmaDictionarySize = info.LzmaDictionarySize is >= ErofsFormat.MinLzmaDictionary and <= ErofsFormat.MaxLzmaDictionary ? info.LzmaDictionarySize : 0,
        BuildTime = info.BuildTime,
        Uuid = info.Uuid == Guid.Empty ? null : info.Uuid,
        VolumeName = info.VolumeName,
    };

    public void Validate()
    {
        if (Compression is ErofsCompression.Deflate or ErofsCompression.Zstd)
            throw new FsException($"Writing EROFS with the {Compression} compressor is not supported; choose LZ4 or LZMA.");
        if (PclusterSize < ErofsFormat.BlockSize || PclusterSize > ErofsFormat.MaxPclusterSize || PclusterSize % ErofsFormat.BlockSize != 0)
            throw new FsException("The EROFS cluster size must be a multiple of 4 KiB between 4 KiB and 1 MiB.");
        if (Lz4Level is < 1 or > 12) throw new FsException("The LZ4HC level must be between 1 and 12.");
        if (LzmaDictionarySize != 0 && LzmaDictionarySize is < ErofsFormat.MinLzmaDictionary or > ErofsFormat.MaxLzmaDictionary)
            throw new FsException("The EROFS LZMA dictionary size must be between 4 KiB and 8 MiB.");
        if (PosixName.Encode(VolumeName).Length > 16 || VolumeName.Contains('\0'))
            throw new FsException("An EROFS volume name has at most 16 bytes.");
    }
}

/// <summary>On-disk constants shared by the EROFS reader and writer.</summary>
internal static class ErofsFormat
{
    public const uint Magic = 0xE0F5E1E2;
    public const int SuperOffset = 1024;
    public const int SuperSize = 128;

    /// <summary>The block size of the images the writer makes (the page size of nearly every device).</summary>
    public const int BlockSize = 4096;

    public const int MaxPclusterSize = 1 << 20;

    /// <summary>The longest piece of a file a writer puts into one compressed cluster.</summary>
    public const int MaxExtentBytes = 4 << 20;

    public const int MinLzmaDictionary = 4096, MaxLzmaDictionary = 8 << 20;

    public const uint CompatSbChecksum = 0x1, CompatMtime = 0x2, CompatXattrFilter = 0x4,
        CompatSharedEaInMetabox = 0x8, CompatPlainXattrPrefixes = 0x10;

    public const uint IncompatZeroPadding = 0x1, IncompatComprCfgs = 0x2, IncompatChunkedFile = 0x4,
        IncompatDeviceTable = 0x8, IncompatZtailpacking = 0x10, IncompatFragments = 0x20,
        IncompatXattrPrefixes = 0x40, Incompat48Bit = 0x80, IncompatMetabox = 0x100;

    /// <summary>Everything the reader understands; any other bit makes the image unreadable for it.</summary>
    public const uint IncompatSupported = IncompatZeroPadding | IncompatComprCfgs | IncompatChunkedFile | IncompatDeviceTable |
        IncompatZtailpacking | IncompatFragments | IncompatXattrPrefixes;

    public const int LayoutFlatPlain = 0, LayoutCompressedFull = 1, LayoutFlatInline = 2, LayoutCompressedCompact = 3, LayoutChunkBased = 4;

    public const int InodeCompactSize = 32, InodeExtendedSize = 64;

    /// <summary>Inodes are addressed in units of 32 bytes from the start of the metadata area.</summary>
    public const int SlotBits = 5;

    public const int AlgLz4 = 0, AlgLzma = 1, AlgDeflate = 2, AlgZstd = 3, AlgMax = 4;

    public const int AdviseCompacted2B = 0x1, AdviseBigPcluster1 = 0x2, AdviseBigPcluster2 = 0x4,
        AdviseInlinePcluster = 0x8, AdviseInterlacedPcluster = 0x10, AdviseFragmentPcluster = 0x20;

    public const int ClusterPlain = 0, ClusterHead1 = 1, ClusterNonHead = 2, ClusterHead2 = 3;

    /// <summary>In the first cluster after a head, the "distance" is the number of blocks of the compressed cluster.</summary>
    public const int D0BlockCount = 1 << 11;

    public const int PartialRef = 1 << 15;

    public const int ChunkBlkBitsMask = 0x1F, ChunkIndexes = 0x20;
    public const uint NullAddr = 0xFFFFFFFF;

    public const int DirentSize = 12;
    public const int FtRegular = 1, FtDir = 2, FtChar = 3, FtBlock = 4, FtFifo = 5, FtSocket = 6, FtSymlink = 7;

    public const int XattrHeaderSize = 12;
    public const int XattrLongPrefix = 0x80;

    /// <summary>Name prefix of every xattr name index (index 0 and the unused Lustre index have none).</summary>
    public static readonly string?[] XattrPrefixes =
    {
        null, "user.", "system.posix_acl_access", "system.posix_acl_default", "trusted.", null, "security.",
    };

    public static int FileType(FsNodeType type) => type switch
    {
        FsNodeType.File => FtRegular,
        FsNodeType.Directory => FtDir,
        FsNodeType.CharDevice => FtChar,
        FsNodeType.BlockDevice => FtBlock,
        FsNodeType.Fifo => FtFifo,
        FsNodeType.Socket => FtSocket,
        FsNodeType.Symlink => FtSymlink,
        _ => 0,
    };

    public static uint EncodeDev(uint major, uint minor) =>
        (minor & 0xFF) | ((major & 0xFFF) << 8) | ((minor & ~0xFFu) << 12);

    public static (uint Major, uint Minor) DecodeDev(uint rdev) =>
        ((rdev >> 8) & 0xFFF, (rdev & 0xFF) | ((rdev >> 12) & 0xFFF00));

    /// <summary>Splits an attribute name into the name index and the rest; index 0 when no prefix of the format fits.</summary>
    public static (int Index, string Suffix) SplitXattrName(string name)
    {
        // The ACL names are whole names: an empty suffix.
        if (name == XattrPrefixes[2]) return (2, "");
        if (name == XattrPrefixes[3]) return (3, "");
        foreach (int i in new[] { 1, 4, 6 })
            if (name.StartsWith(XattrPrefixes[i]!, StringComparison.Ordinal) && name.Length > XattrPrefixes[i]!.Length)
                return (i, name[XattrPrefixes[i]!.Length..]);
        return (0, name);
    }

    public static string AlgorithmName(int id) => id switch
    {
        AlgLz4 => "lz4",
        AlgLzma => "lzma",
        AlgDeflate => "deflate",
        AlgZstd => "zstd",
        _ => $"#{id}",
    };

    public static ErofsCompression Algorithm(int id) => id switch
    {
        AlgLz4 => ErofsCompression.Lz4,
        AlgLzma => ErofsCompression.Lzma,
        AlgDeflate => ErofsCompression.Deflate,
        _ => ErofsCompression.Zstd,
    };
}
