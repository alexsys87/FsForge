using FsForge.Core.Tar;

namespace FsForge.Core.Cpio;

/// <summary>Header format of a cpio archive.</summary>
public enum CpioHeader
{
    /// <summary>SVR4 "newc" (magic 070701): what the Linux kernel unpacks as initramfs.</summary>
    Newc,

    /// <summary>SVR4 with a checksum of every file (magic 070702).</summary>
    Crc,

    /// <summary>POSIX.1 portable "odc" (magic 070707): read only.</summary>
    Odc,
}

/// <summary>Facts about an opened cpio archive.</summary>
public sealed class CpioInfo
{
    public CpioHeader Header { get; init; }
    public ArchiveCompression Compression { get; init; }
    public long Entries { get; init; }

    /// <summary>Prefix shared by all member names: "./" or "".</summary>
    public string PathPrefix { get; init; } = "";

    /// <summary>The archive contains an entry for the root directory itself (".").</summary>
    public bool HasRootEntry { get; init; }

    /// <summary>
    /// Number of archives stored one after another in the file (an initramfs often starts with an
    /// uncompressed archive of CPU microcode, followed by the compressed system).
    /// </summary>
    public int Parts { get; init; } = 1;

    /// <summary>The archive ends without the "TRAILER!!!" entry.</summary>
    public bool Truncated { get; init; }
}

/// <summary>Parameters of a new cpio archive.</summary>
public sealed class CpioOptions
{
    public CpioHeader Header { get; set; } = CpioHeader.Newc;
    public ArchiveCompression Compression { get; set; } = ArchiveCompression.Gzip;

    /// <summary>1 (fastest) .. 9 (smallest).</summary>
    public int GzipLevel { get; set; } = 9;

    /// <summary>1 .. 22.</summary>
    public int ZstdLevel { get; set; } = 19;

    /// <summary>0 = fast, 1 = normal, 2 = maximum.</summary>
    public int XzEffort { get; set; } = 1;

    /// <summary>LZ4 level 1 .. 12 (as lz4hc); Android uses the highest.</summary>
    public int Lz4Level { get; set; } = 9;

    /// <summary>Text put in front of every member name: "" (as the kernel's gen_init_cpio and most initramfs tools) or "./".</summary>
    public string PathPrefix { get; set; } = "";

    /// <summary>Write an entry for the root directory itself ("."), which carries its mode and owner.</summary>
    public bool IncludeRootEntry { get; set; } = true;

    /// <summary>Force owner and group of every member to root.</summary>
    public bool AllRoot { get; set; }

    /// <summary>Pad the archive to a multiple of 512 bytes, as GNU cpio does.</summary>
    public bool Pad512 { get; set; } = true;

    /// <summary>Worker threads for XZ; 0 means one per processor.</summary>
    public int Threads { get; set; }

    public CpioOptions Clone() => (CpioOptions)MemberwiseClone();

    public static CpioOptions FromInfo(CpioInfo info) => new()
    {
        // odc is read only: newc is what replaces it.
        Header = info.Header == CpioHeader.Crc ? CpioHeader.Crc : CpioHeader.Newc,
        Compression = info.Compression,
        PathPrefix = info.PathPrefix,
        IncludeRootEntry = info.HasRootEntry,
    };

    public void Validate()
    {
        if (Header == CpioHeader.Odc) throw new FsException("Writing odc cpio archives is not supported; choose newc.");
        if (PathPrefix is not ("" or "./")) throw new FsException("The cpio path prefix must be empty or \"./\".");
        if (GzipLevel is < 1 or > 9 || ZstdLevel is < 1 or > 22 || XzEffort is < 0 or > 2 || Lz4Level is < 1 or > 12)
            throw new FsException("Invalid compression level for the cpio archive.");
    }

    /// <summary>The compressor settings in the form the shared archive code takes.</summary>
    internal TarOptions CompressorOptions() => new()
    {
        Compression = Compression, GzipLevel = GzipLevel, ZstdLevel = ZstdLevel, XzEffort = XzEffort, Lz4Level = Lz4Level, Threads = Threads,
    };
}

/// <summary>On-disk constants shared by the cpio reader and writer, and the file names of cpio archives.</summary>
public static class CpioFormat
{
    internal const int NewcHeaderSize = 110, OdcHeaderSize = 76;
    internal const string Trailer = "TRAILER!!!";

    internal static ReadOnlySpan<byte> MagicNewc => "070701"u8;
    internal static ReadOnlySpan<byte> MagicCrc => "070702"u8;
    internal static ReadOnlySpan<byte> MagicOdc => "070707"u8;

    /// <summary>The header format a magic names, or null.</summary>
    internal static CpioHeader? Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length < 6) return null;
        if (head[..6].SequenceEqual(MagicNewc)) return CpioHeader.Newc;
        if (head[..6].SequenceEqual(MagicCrc)) return CpioHeader.Crc;
        if (head[..6].SequenceEqual(MagicOdc)) return CpioHeader.Odc;
        return null;
    }

    /// <summary>The old binary format (magic 070707 as a 16-bit number), which is not read.</summary>
    internal static bool IsBinary(ReadOnlySpan<byte> head) =>
        head.Length >= 2 && ((head[0] == 0xC7 && head[1] == 0x71) || (head[0] == 0x71 && head[1] == 0xC7));

    internal static long Hex(ReadOnlySpan<byte> field, string what)
    {
        long v = 0;
        foreach (byte c in field)
        {
            int d = c switch
            {
                >= (byte)'0' and <= (byte)'9' => c - '0',
                >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
                _ => -1,
            };
            if (d < 0) throw new FsException($"Corrupt cpio header: invalid {what} field.");
            v = (v << 4) | (uint)d;
        }
        return v;
    }

    internal static long Octal(ReadOnlySpan<byte> field, string what)
    {
        long v = 0;
        foreach (byte c in field)
        {
            if (c is < (byte)'0' or > (byte)'7') throw new FsException($"Corrupt cpio header: invalid {what} field.");
            v = (v << 3) | (uint)(c - '0');
        }
        return v;
    }

    /// <summary>Eight upper-case hex digits, as the kernel's gen_init_cpio writes them.</summary>
    internal static void WriteHex(Span<byte> field, long value)
    {
        for (int i = 7; i >= 0; i--)
        {
            field[i] = (byte)"0123456789ABCDEF"[(int)(value & 15)];
            value >>= 4;
        }
    }

    internal static int Pad4(long n) => (int)((4 - (n & 3)) & 3);

    public static string DefaultExtension(ArchiveCompression c) => c switch
    {
        ArchiveCompression.Gzip => ".cpio.gz",
        ArchiveCompression.Xz => ".cpio.xz",
        ArchiveCompression.Zstd => ".cpio.zst",
        ArchiveCompression.Lz4 => ".cpio.lz4",
        _ => ".cpio",
    };

    /// <summary>The compression a name asks for ("initramfs.cpio.gz"); null when it does not name a cpio archive.</summary>
    public static ArchiveCompression? CompressionFromFileName(string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        if (name.EndsWith(".cpio.gz") || name.EndsWith(".cpgz")) return ArchiveCompression.Gzip;
        if (name.EndsWith(".cpio.xz")) return ArchiveCompression.Xz;
        if (name.EndsWith(".cpio.zst") || name.EndsWith(".cpio.zstd")) return ArchiveCompression.Zstd;
        if (name.EndsWith(".cpio.lz4")) return ArchiveCompression.Lz4;
        if (name.EndsWith(".cpio")) return ArchiveCompression.None;
        return null;
    }
}
