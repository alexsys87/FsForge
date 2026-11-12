using FsForge.Core.Compression;

namespace FsForge.Core.SquashFs;

public enum SquashCompression
{
    Gzip = 1,
    Lzma = 2,
    Lzo = 3,
    Xz = 4,
    Lz4 = 5,
    Zstd = 6,
}

/// <summary>Facts about an opened SquashFS image.</summary>
public sealed class SquashFsInfo
{
    public int VersionMajor { get; init; }
    public int VersionMinor { get; init; }
    public int BlockSize { get; init; }
    public SquashCompression Compression { get; init; }
    public int Flags { get; init; }
    public long BytesUsed { get; init; }
    public long MkfsTime { get; init; }
    public uint InodeCount { get; init; }
    public uint FragmentCount { get; init; }
    public int IdCount { get; init; }

    /// <summary>XZ dictionary size from the compressor options (0 when not recorded).</summary>
    public int XzDictionarySize { get; init; }
    public IReadOnlyList<BcjFilter> XzFilters { get; init; } = Array.Empty<BcjFilter>();
    public int GzipLevel { get; init; } = 9;
    public int ZstdLevel { get; init; } = 15;
    public bool Lz4HighCompression { get; init; }

    public bool NoFragments => (Flags & SquashFsFormat.FlagNoFragments) != 0;
    public bool Exportable => (Flags & SquashFsFormat.FlagExportable) != 0;
    public bool Deduplicated => (Flags & SquashFsFormat.FlagDuplicates) != 0;
    public bool HasXattrs { get; init; }
}

/// <summary>Parameters of a new SquashFS image.</summary>
public sealed class SquashFsOptions
{
    public SquashCompression Compression { get; set; } = SquashCompression.Xz;

    /// <summary>Data block size: a power of two between 4 KiB and 1 MiB.</summary>
    public int BlockSize { get; set; } = 256 * 1024;

    public int GzipLevel { get; set; } = 9;
    public int ZstdLevel { get; set; } = 15;

    /// <summary>XZ effort: 0 = fast, 1 = normal, 2 = maximum.</summary>
    public int XzEffort { get; set; } = 2;

    /// <summary>XZ dictionary size; 0 means "same as the block size".</summary>
    public int XzDictionarySize { get; set; }

    public List<BcjFilter> XzFilters { get; set; } = new();
    public int XzLc { get; set; } = 3;
    public int XzLp { get; set; }
    public int XzPb { get; set; } = 2;

    /// <summary>Do not pack small files and file tails into shared fragment blocks.</summary>
    public bool NoFragments { get; set; }

    /// <summary>Store identical files once.</summary>
    public bool Deduplicate { get; set; } = true;

    public bool StoreXattrs { get; set; } = true;

    /// <summary>Write the NFS export (inode lookup) table.</summary>
    public bool Exportable { get; set; } = true;

    /// <summary>Pad the image to a multiple of 4 KiB (mksquashfs default).</summary>
    public bool Pad4K { get; set; } = true;

    /// <summary>Force owner and group of every inode to root.</summary>
    public bool AllRoot { get; set; }

    /// <summary>Creation time stored in the superblock; null means "now".</summary>
    public long? MkfsTime { get; set; }

    /// <summary>Worker threads; 0 means one per processor.</summary>
    public int Threads { get; set; }

    public SquashFsOptions Clone()
    {
        var c = (SquashFsOptions)MemberwiseClone();
        c.XzFilters = new List<BcjFilter>(XzFilters);
        return c;
    }

    /// <summary>Options that reproduce the settings of an existing image as closely as possible.</summary>
    public static SquashFsOptions FromInfo(SquashFsInfo info)
    {
        var o = new SquashFsOptions
        {
            BlockSize = info.BlockSize,
            Compression = info.Compression switch
            {
                // Compressors we can read but not write fall back to the closest writable one.
                SquashCompression.Lzma => SquashCompression.Xz,
                SquashCompression.Lzo => SquashCompression.Gzip,
                var c => c,
            },
            GzipLevel = info.GzipLevel,
            ZstdLevel = info.ZstdLevel,
            XzDictionarySize = info.XzDictionarySize == info.BlockSize ? 0 : info.XzDictionarySize,
            NoFragments = info.NoFragments,
            Exportable = info.Exportable,
            Deduplicate = info.Deduplicated,
        };
        o.XzFilters.AddRange(info.XzFilters.Where(Bcj.IsSupported));
        return o;
    }

    public void Validate()
    {
        if (BlockSize < 4096 || BlockSize > 1 << 20 || (BlockSize & (BlockSize - 1)) != 0)
            throw new FsException("The SquashFS block size must be a power of two between 4 KiB and 1 MiB.");
        if (Compression is SquashCompression.Lzma or SquashCompression.Lzo)
            throw new FsException($"Writing with the {Compression} compressor is not supported.");
        if (XzLc < 0 || XzLp < 0 || XzLc + XzLp > 4 || XzPb is < 0 or > 4)
            throw new FsException("Invalid XZ lc/lp/pb values (lc + lp must not exceed 4, pb must be 0..4).");
        if (XzDictionarySize != 0 && (XzDictionarySize < 8192 || XzDictionarySize > BlockSize))
            throw new FsException("The XZ dictionary size must be between 8 KiB and the block size.");
    }
}

internal static class SquashFsFormat
{
    public const uint Magic = 0x73717368;           // "hsqs"
    public const uint MagicSwapped = 0x68737173;    // big-endian or pre-4.0 images
    public const int SuperblockSize = 96;
    public const int MetadataSize = 8192;
    public const ulong InvalidBlock = 0xFFFFFFFFFFFFFFFF;
    public const uint InvalidFragment = 0xFFFFFFFF;
    public const uint InvalidXattr = 0xFFFFFFFF;
    public const uint BlockUncompressed = 1u << 24;

    public const int FlagUncompressedInodes = 0x0001;
    public const int FlagUncompressedData = 0x0002;
    public const int FlagUncompressedFragments = 0x0008;
    public const int FlagNoFragments = 0x0010;
    public const int FlagAlwaysFragments = 0x0020;
    public const int FlagDuplicates = 0x0040;
    public const int FlagExportable = 0x0080;
    public const int FlagUncompressedXattrs = 0x0100;
    public const int FlagNoXattrs = 0x0200;
    public const int FlagCompressorOptions = 0x0400;
    public const int FlagUncompressedIds = 0x0800;

    public const int TypeDir = 1, TypeFile = 2, TypeSymlink = 3, TypeBlock = 4, TypeChar = 5, TypeFifo = 6, TypeSocket = 7;
    public const int TypeExtOffset = 7;

    public const int XattrUser = 0, XattrTrusted = 1, XattrSecurity = 2;
    public const int XattrOutOfLine = 0x100;

    public static readonly string[] XattrPrefixes = { "user.", "trusted.", "security." };

    public static int BasicType(FsNodeType type) => type switch
    {
        FsNodeType.Directory => TypeDir,
        FsNodeType.File => TypeFile,
        FsNodeType.Symlink => TypeSymlink,
        FsNodeType.BlockDevice => TypeBlock,
        FsNodeType.CharDevice => TypeChar,
        FsNodeType.Fifo => TypeFifo,
        FsNodeType.Socket => TypeSocket,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static uint EncodeDev(uint major, uint minor) =>
        (minor & 0xFF) | ((major & 0xFFF) << 8) | ((minor & ~0xFFu) << 12);

    public static (uint Major, uint Minor) DecodeDev(uint rdev) =>
        ((rdev >> 8) & 0xFFF, (rdev & 0xFF) | ((rdev >> 12) & 0xFFF00));

    public static IBlockCodec CreateDecoder(SquashFsInfo info) => info.Compression switch
    {
        SquashCompression.Gzip => new ZlibCodec(),
        SquashCompression.Xz => new XzCodec(Math.Max(info.BlockSize, MetadataSize)),
        SquashCompression.Lz4 => new Lz4Codec(),
        SquashCompression.Lzo => new LzoCodec(),
        SquashCompression.Zstd => new ZstdCodec(),
        SquashCompression.Lzma => new LzmaAloneCodec(),
        _ => throw new FsException($"Unknown SquashFS compressor id {(int)info.Compression}."),
    };

    public static IBlockCodec CreateEncoder(SquashFsOptions o) => o.Compression switch
    {
        SquashCompression.Gzip => new ZlibCodec(o.GzipLevel),
        SquashCompression.Xz => new XzCodec(
            o.XzDictionarySize > 0 ? o.XzDictionarySize : o.BlockSize,
            o.XzFilters,
            LzmaEncoderSettings.FromEffort(o.XzEffort) with { Lc = o.XzLc, Lp = o.XzLp, Pb = o.XzPb }),
        SquashCompression.Lz4 => new Lz4Codec(),
        SquashCompression.Zstd => new ZstdCodec(o.ZstdLevel),
        _ => throw new FsException($"Writing with the {o.Compression} compressor is not supported."),
    };

    /// <summary>
    /// Codec for metadata blocks. mksquashfs compresses them with an 8 KiB dictionary and no BCJ
    /// filters, which keeps every stream within the limits the kernel derives from the superblock.
    /// </summary>
    public static IBlockCodec CreateMetadataEncoder(SquashFsOptions o) => o.Compression == SquashCompression.Xz
        ? new XzCodec(MetadataSize, null, LzmaEncoderSettings.FromEffort(o.XzEffort) with { Lc = o.XzLc, Lp = o.XzLp, Pb = o.XzPb })
        : CreateEncoder(o);

    /// <summary>XZ compressor option flag for each BCJ filter.</summary>
    public static int XzFilterFlag(BcjFilter f) => f switch
    {
        BcjFilter.X86 => 1,
        BcjFilter.PowerPc => 2,
        BcjFilter.Ia64 => 4,
        BcjFilter.Arm => 8,
        BcjFilter.ArmThumb => 16,
        BcjFilter.Sparc => 32,
        BcjFilter.Arm64 => 64,
        BcjFilter.RiscV => 128,
        _ => 0,
    };
}
