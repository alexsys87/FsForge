using System.Buffers.Binary;
using System.Text;

namespace FsForge.Core.Romfs;

/// <summary>Facts about an opened romfs image.</summary>
public sealed class RomfsInfo
{
    public string VolumeName { get; init; } = "";

    /// <summary>Size of the filesystem from its superblock.</summary>
    public long Size { get; init; }

    /// <summary>The checksum of the first 512 bytes is right (the kernel refuses an image whose checksum is wrong).</summary>
    public bool ChecksumOk { get; init; }

    /// <summary>Largest power of two (16 .. 4096) that every file's data starts at a multiple of.</summary>
    public int DataAlignment { get; init; } = 16;
}

/// <summary>Parameters of a new romfs image.</summary>
public sealed class RomfsOptions
{
    /// <summary>Volume name (genromfs -V).</summary>
    public string VolumeName { get; set; } = "rom";

    /// <summary>The data of every regular file starts at a multiple of this (genromfs -a): 16 .. 65536, a power of two.</summary>
    public int DataAlignment { get; set; } = 16;

    public RomfsOptions Clone() => (RomfsOptions)MemberwiseClone();

    public static RomfsOptions FromInfo(RomfsInfo info) => new() { VolumeName = info.VolumeName, DataAlignment = info.DataAlignment };

    public void Validate()
    {
        if (DataAlignment is < 16 or > 65536 || (DataAlignment & (DataAlignment - 1)) != 0)
            throw new FsException("The romfs data alignment must be a power of two from 16 to 65536 bytes.");
        if (Encoding.UTF8.GetByteCount(VolumeName) > RomfsFormat.MaxNameLength)
            throw new FsException($"The romfs volume name must not be longer than {RomfsFormat.MaxNameLength} bytes.");
    }
}

/// <summary>On-disk constants of romfs (Documentation/filesystems/romfs.rst).</summary>
public static class RomfsFormat
{
    public static ReadOnlySpan<byte> Magic => "-rom1fs-"u8;
    public const int HeaderSize = 16;

    /// <summary>Longest name the kernel reads (ROMFS_MAXFN is 128 with the terminating zero).</summary>
    public const int MaxNameLength = 127;

    public const int TypeHardLink = 0, TypeDirectory = 1, TypeFile = 2, TypeSymlink = 3, TypeBlock = 4, TypeChar = 5,
        TypeSocket = 6, TypeFifo = 7, ExecBit = 8;

    internal static long Align16(long n) => (n + 15) & ~15L;

    /// <summary>The sum of the big-endian 32-bit words, which is zero over a checked area.</summary>
    internal static uint Sum(ReadOnlySpan<byte> b)
    {
        uint s = 0;
        for (int i = 0; i + 4 <= b.Length; i += 4) s += BinaryPrimitives.ReadUInt32BigEndian(b[i..]);
        return s;
    }

    /// <summary>The permissions the kernel gives each type (romfs stores only whether a file is executable).</summary>
    internal static int ModeOf(int type, bool exec) => type switch
    {
        TypeDirectory or TypeFile or TypeSocket or TypeFifo => 0x1A4 | (exec ? 0x49 : 0),     // 0644, +0111
        TypeSymlink => 0x1FF,
        _ => 0x180 | (exec ? 0x49 : 0),                                                       // devices: 0600
    };
}
