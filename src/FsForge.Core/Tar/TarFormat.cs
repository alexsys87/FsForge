using System.IO.Compression;
using FsForge.Core.Compression;

namespace FsForge.Core.Tar;

/// <summary>Outer compression of a tar archive.</summary>
public enum ArchiveCompression
{
    None,
    Gzip,
    Xz,
    Zstd,
}

/// <summary>How names, numbers and attributes that do not fit a classic header are stored.</summary>
public enum TarDialect
{
    /// <summary>POSIX.1-2001: plain ustar headers plus "x" extended headers where needed. Keeps extended attributes.</summary>
    Pax,

    /// <summary>GNU tar: long names in "L"/"K" entries, large numbers in base-256. Cannot hold extended attributes.</summary>
    Gnu,
}

/// <summary>Facts about an opened tar archive.</summary>
public sealed class TarInfo
{
    public ArchiveCompression Compression { get; init; }
    public long Entries { get; init; }
    public bool HasPaxHeaders { get; init; }
    public bool HasGnuExtensions { get; init; }

    /// <summary>Prefix shared by all member names: "./" or "" (names without a prefix).</summary>
    public string PathPrefix { get; init; } = "./";

    /// <summary>The archive contains an entry for the root directory itself ("./").</summary>
    public bool HasRootEntry { get; init; }
}

/// <summary>Parameters of a new tar archive.</summary>
public sealed class TarOptions
{
    public ArchiveCompression Compression { get; set; } = ArchiveCompression.Gzip;
    public TarDialect Dialect { get; set; } = TarDialect.Pax;

    /// <summary>1 (fastest) .. 9 (smallest).</summary>
    public int GzipLevel { get; set; } = 6;

    /// <summary>1 .. 22.</summary>
    public int ZstdLevel { get; set; } = 10;

    /// <summary>0 = fast, 1 = normal, 2 = maximum.</summary>
    public int XzEffort { get; set; } = 1;

    /// <summary>Text put in front of every member name; "./" matches "tar -c -C rootfs .".</summary>
    public string PathPrefix { get; set; } = "./";

    /// <summary>Write an entry for the root directory itself (keeps its mode and owner).</summary>
    public bool IncludeRootEntry { get; set; } = true;

    public bool StoreXattrs { get; set; } = true;

    /// <summary>Force owner and group of every member to root.</summary>
    public bool AllRoot { get; set; }

    /// <summary>Worker threads for XZ; 0 means one per processor.</summary>
    public int Threads { get; set; }

    public TarOptions Clone() => (TarOptions)MemberwiseClone();

    public static TarOptions FromInfo(TarInfo info) => new()
    {
        Compression = info.Compression,
        Dialect = info.HasGnuExtensions && !info.HasPaxHeaders ? TarDialect.Gnu : TarDialect.Pax,
        PathPrefix = info.PathPrefix,
        IncludeRootEntry = info.HasRootEntry,
    };

    public void Validate()
    {
        if (PathPrefix.Contains('\0') || PathPrefix.Contains('\\'))
            throw new FsException("The tar path prefix contains invalid characters.");
        if (PathPrefix.Length > 0 && !PathPrefix.EndsWith('/'))
            throw new FsException("The tar path prefix must be empty or end with '/'.");
        if (PathPrefix.StartsWith('/') || FsTree.SplitPath(PathPrefix).Contains(".."))
            throw new FsException("The tar path prefix must be relative and must not contain '..'.");
    }
}

/// <summary>Detection and (de)compression helpers shared by the tar reader and writer.</summary>
public static class ArchiveCodec
{
    /// <summary>Recognises the compression from the first bytes; null when the data is not a known compressed stream.</summary>
    public static ArchiveCompression? DetectCompression(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 3 && head[0] == 0x1F && head[1] == 0x8B && head[2] == 8) return ArchiveCompression.Gzip;
        if (head.Length >= 6 && head[..6].SequenceEqual(XzFormat.HeaderMagic)) return ArchiveCompression.Xz;
        if (head.Length >= 4 && head[0] == 0x28 && head[1] == 0xB5 && head[2] == 0x2F && head[3] == 0xFD) return ArchiveCompression.Zstd;
        return null;
    }

    /// <summary>Names a compression we recognise but cannot read, or null.</summary>
    public static string? DetectUnsupported(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 3 && head[0] == (byte)'B' && head[1] == (byte)'Z' && head[2] == (byte)'h') return "bzip2";
        if (head.Length >= 4 && head[0] == 0x04 && head[1] == 0x22 && head[2] == 0x4D && head[3] == 0x18) return "LZ4";
        if (head.Length >= 4 && head[0] == 0x5D && head[1] == 0 && head[2] == 0) return "LZMA";
        if (head.Length >= 2 && head[0] == 0x1F && head[1] == 0x9D) return "compress (.Z)";
        return null;
    }

    public static Stream OpenDecompressor(Stream input, ArchiveCompression compression) => compression switch
    {
        ArchiveCompression.Gzip => new GZipStream(input, CompressionMode.Decompress),
        ArchiveCompression.Xz => new XzInputStream(input),
        ArchiveCompression.Zstd => new ZstdSharp.DecompressionStream(input),
        _ => input,
    };

    public static Stream OpenCompressor(Stream output, TarOptions o) => o.Compression switch
    {
        ArchiveCompression.Gzip => new GZipStream(output,
            new ZLibCompressionOptions { CompressionLevel = Math.Clamp(o.GzipLevel, 1, 9) }, leaveOpen: true),
        ArchiveCompression.Xz => new XzOutputStream(output, o.XzEffort, 1 << 22, o.Threads, leaveOpen: true),
        ArchiveCompression.Zstd => new ZstdSharp.CompressionStream(output, Math.Clamp(o.ZstdLevel, 1, 22), leaveOpen: true),
        _ => new PassThroughStream(output),
    };

    /// <summary>Checks whether a 512-byte block is a tar header (valid checksum).</summary>
    public static bool IsTarHeader(ReadOnlySpan<byte> block)
    {
        if (block.Length < 512) return false;
        long stored = TarNumbers.ParseOctal(block.Slice(148, 8));
        if (stored < 0) return false;
        long unsigned = 0, signed = 0;
        for (int i = 0; i < 512; i++)
        {
            byte b = i is >= 148 and < 156 ? (byte)' ' : block[i];
            unsigned += b;
            signed += (sbyte)b;
        }
        // All-zero blocks have a checksum of 256 and a stored value of 0: not a header.
        return stored == unsigned || stored == signed;
    }

    public static string DefaultExtension(ArchiveCompression c) => c switch
    {
        ArchiveCompression.Gzip => ".tar.gz",
        ArchiveCompression.Xz => ".tar.xz",
        ArchiveCompression.Zstd => ".tar.zst",
        _ => ".tar",
    };

    /// <summary>Guesses the compression a user expects from a file name.</summary>
    public static ArchiveCompression? CompressionFromFileName(string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        if (name.EndsWith(".tar.gz") || name.EndsWith(".tgz")) return ArchiveCompression.Gzip;
        if (name.EndsWith(".tar.xz") || name.EndsWith(".txz")) return ArchiveCompression.Xz;
        if (name.EndsWith(".tar.zst") || name.EndsWith(".tzst")) return ArchiveCompression.Zstd;
        if (name.EndsWith(".tar")) return ArchiveCompression.None;
        return null;
    }

    private sealed class PassThroughStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>Numeric fields of tar headers: octal text or GNU base-256.</summary>
internal static class TarNumbers
{
    /// <summary>Parses an octal field; returns -1 when it holds no valid number.</summary>
    public static long ParseOctal(ReadOnlySpan<byte> field)
    {
        int i = 0;
        while (i < field.Length && (field[i] == ' ' || field[i] == 0)) i++;
        if (i == field.Length) return 0;
        long value = 0;
        bool any = false;
        for (; i < field.Length; i++)
        {
            byte c = field[i];
            if (c == ' ' || c == 0) break;
            if (c < '0' || c > '7') return -1;
            value = (value << 3) | (uint)(c - '0');
            any = true;
        }
        return any ? value : 0;
    }

    /// <summary>Parses an octal or base-256 field (the latter may be negative).</summary>
    public static long Parse(ReadOnlySpan<byte> field, string what)
    {
        if (field.Length > 0 && (field[0] & 0x80) != 0)
        {
            // Base-256: big-endian two's complement; the top bit of the first byte is the marker.
            bool negative = (field[0] & 0x40) != 0;
            long value = negative ? -1 : 0;
            for (int i = 0; i < field.Length; i++)
            {
                byte b = i == 0 ? (byte)(negative ? field[0] | 0x80 : field[0] & 0x7F) : field[i];
                if (i >= field.Length - 8) value = (value << 8) | b;
            }
            return value;
        }
        long v = ParseOctal(field);
        if (v < 0) throw new FsException($"Corrupt tar header: invalid {what} field.");
        return v;
    }

    /// <summary>Writes an octal field with a terminating NUL; false when the value does not fit.</summary>
    public static bool TryWriteOctal(Span<byte> field, long value)
    {
        if (value < 0) return false;
        int digits = field.Length - 1;
        if (digits < 21 && value >= 1L << (3 * digits)) return false;
        for (int i = digits - 1; i >= 0; i--)
        {
            field[i] = (byte)('0' + (int)(value & 7));
            value >>= 3;
        }
        field[digits] = 0;
        return true;
    }

    public static void WriteBase256(Span<byte> field, long value)
    {
        for (int i = field.Length - 1; i >= 0; i--)
        {
            field[i] = (byte)value;
            value >>= 8;                                // arithmetic shift keeps the sign fill
        }
        field[0] |= 0x80;
    }
}
