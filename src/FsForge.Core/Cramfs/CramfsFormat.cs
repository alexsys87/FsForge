using System.Buffers.Binary;
using System.Text;

namespace FsForge.Core.Cramfs;

/// <summary>Facts about an opened CramFS image.</summary>
public sealed class CramfsInfo
{
    /// <summary>The image was made for a big-endian machine (mkfs.cramfs -N big).</summary>
    public bool BigEndian { get; init; }

    /// <summary>Uncompressed size of a block: the page size of the machine the image is for.</summary>
    public int BlockSize { get; init; } = 4096;

    /// <summary>Size of the filesystem from its superblock (0 for images without the version 2 fields).</summary>
    public long Size { get; init; }

    /// <summary>The superblock follows 512 bytes left for boot code (mkfs.cramfs -p).</summary>
    public bool Padded { get; init; }

    public uint Flags { get; init; }
    public string VolumeName { get; init; } = "";
    public uint Edition { get; init; }
    public uint Files { get; init; }
    public uint Blocks { get; init; }

    /// <summary>The superblock carries a CRC (version 2 layout); null when it does not.</summary>
    public bool? CrcOk { get; init; }

    /// <summary>Blocks of zeros are stored as holes.</summary>
    public bool Holes => (Flags & CramfsFormat.FlagHoles) != 0;

    /// <summary>Block pointers carry flags (uncompressed blocks, direct pointers: Linux 4.15+).</summary>
    public bool ExtBlockPointers => (Flags & CramfsFormat.FlagExtBlockPointers) != 0;
}

/// <summary>Parameters of a new CramFS image.</summary>
public sealed class CramfsOptions
{
    /// <summary>Write the image for a big-endian machine (MIPS, PowerPC routers).</summary>
    public bool BigEndian { get; set; }

    /// <summary>Uncompressed block size; the kernel reads only images whose block size is its page size.</summary>
    public int BlockSize { get; set; } = 4096;

    /// <summary>zlib level 1 .. 9.</summary>
    public int Level { get; set; } = 9;

    /// <summary>Volume name, at most 16 bytes.</summary>
    public string VolumeName { get; set; } = "Compressed";

    /// <summary>Edition number, part of the filesystem id.</summary>
    public uint Edition { get; set; }

    /// <summary>Leave 512 bytes for boot code in front of the superblock (mkfs.cramfs -p).</summary>
    public bool Pad { get; set; }

    /// <summary>Store blocks of zeros as holes (mkfs.cramfs -z).</summary>
    public bool Holes { get; set; }

    /// <summary>Force owner and group of every object to root.</summary>
    public bool AllRoot { get; set; }

    /// <summary>Worker threads for compression; 0 means one per processor.</summary>
    public int Threads { get; set; }

    public CramfsOptions Clone() => (CramfsOptions)MemberwiseClone();

    public static CramfsOptions FromInfo(CramfsInfo info) => new()
    {
        BigEndian = info.BigEndian,
        BlockSize = info.BlockSize,
        VolumeName = info.VolumeName,
        Edition = info.Edition,
        Pad = info.Padded,
        Holes = info.Holes,
    };

    public void Validate()
    {
        if (BlockSize is < 4096 or > 65536 || (BlockSize & (BlockSize - 1)) != 0)
            throw new FsException("The CramFS block size must be a power of two from 4096 to 65536 bytes.");
        if (Level is < 1 or > 9) throw new FsException("The CramFS compression level must be from 1 to 9.");
        if (Encoding.UTF8.GetByteCount(VolumeName) > 16) throw new FsException("The CramFS volume name must not be longer than 16 bytes.");
    }
}

/// <summary>On-disk constants of CramFS (linux/include/uapi/linux/cramfs_fs.h).</summary>
public static class CramfsFormat
{
    public const uint Magic = 0x28CD3D45;
    public const int SuperSize = 76, InodeSize = 12, RootInodeOffset = 64;
    public static ReadOnlySpan<byte> Signature => "Compressed ROMFS"u8;

    public const uint FlagFsidV2 = 0x1, FlagSortedDirs = 0x2, FlagHoles = 0x100, FlagWrongSignature = 0x200,
        FlagShiftedRootOffset = 0x400, FlagExtBlockPointers = 0x800;
    public const uint SupportedFlags = 0xFF | FlagHoles | FlagWrongSignature | FlagShiftedRootOffset | FlagExtBlockPointers;

    public const uint BlkFlagUncompressed = 1u << 31, BlkFlagDirectPtr = 1u << 30, BlkFlags = BlkFlagUncompressed | BlkFlagDirectPtr;

    /// <summary>Longest name: 6 bits counting 4-byte units.</summary>
    public const int MaxNameLength = 63 * 4;

    /// <summary>Largest file and directory: a 24-bit size.</summary>
    public const long MaxFileSize = (1 << 24) - 1;

    /// <summary>Offsets are stored in 4-byte units in 26 bits.</summary>
    public const long MaxOffset = (1L << 26) * 4;

    internal static uint U32(ReadOnlySpan<byte> b, int at, bool big) =>
        big ? BinaryPrimitives.ReadUInt32BigEndian(b[at..]) : BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);

    internal static void W32(Span<byte> b, int at, uint v, bool big)
    {
        if (big) BinaryPrimitives.WriteUInt32BigEndian(b[at..], v);
        else BinaryPrimitives.WriteUInt32LittleEndian(b[at..], v);
    }

    /// <summary>A CramFS inode: the bit fields packed as the kernel of the image's byte order sees them.</summary>
    internal readonly record struct Inode(int Mode, uint Uid, uint Size, uint Gid, int NameLen, uint Offset)
    {
        public static Inode Parse(ReadOnlySpan<byte> b, bool big)
        {
            uint w0 = U32(b, 0, big), w1 = U32(b, 4, big), w2 = U32(b, 8, big);
            return big
                ? new Inode((int)(w0 >> 16), w0 & 0xFFFF, w1 >> 8, w1 & 0xFF, (int)(w2 >> 26), w2 & 0x3FFFFFF)
                : new Inode((int)(w0 & 0xFFFF), w0 >> 16, w1 & 0xFFFFFF, w1 >> 24, (int)(w2 & 0x3F), w2 >> 6);
        }

        public void Write(Span<byte> b, bool big)
        {
            if (big)
            {
                W32(b, 0, ((uint)Mode << 16) | (Uid & 0xFFFF), true);
                W32(b, 4, (Size << 8) | (Gid & 0xFF), true);
                W32(b, 8, ((uint)NameLen << 26) | (Offset & 0x3FFFFFF), true);
            }
            else
            {
                W32(b, 0, (uint)(Mode & 0xFFFF) | (Uid << 16), false);
                W32(b, 4, (Size & 0xFFFFFF) | (Gid << 24), false);
                W32(b, 8, (uint)(NameLen & 0x3F) | (Offset << 6), false);
            }
        }
    }
}
