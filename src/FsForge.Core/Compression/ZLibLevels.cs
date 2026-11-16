using System.IO.Compression;

namespace FsForge.Core.Compression;

/// <summary>
/// Opens zlib and gzip compressors with a numeric level (1-9). .NET 9 added the numeric level; .NET 8 only has
/// three named levels, so there the nearest one is used (1-3 fastest, 4-8 optimal, 9 smallest).
/// </summary>
internal static class ZLibLevels
{
    public static ZLibStream OpenZLib(Stream output, int level, bool leaveOpen)
    {
#if NET9_0_OR_GREATER
        return new ZLibStream(output, new ZLibCompressionOptions { CompressionLevel = level }, leaveOpen);
#else
        return new ZLibStream(output, Named(level), leaveOpen);
#endif
    }

    /// <summary>A bare deflate stream (no zlib header) with a 32 KiB window.</summary>
    public static DeflateStream OpenDeflate(Stream output, int level, bool leaveOpen)
    {
#if NET9_0_OR_GREATER
        return new DeflateStream(output, new ZLibCompressionOptions { CompressionLevel = level }, leaveOpen);
#else
        return new DeflateStream(output, Named(level), leaveOpen);
#endif
    }

    public static GZipStream OpenGZip(Stream output, int level, bool leaveOpen)
    {
#if NET9_0_OR_GREATER
        return new GZipStream(output, new ZLibCompressionOptions { CompressionLevel = level }, leaveOpen);
#else
        return new GZipStream(output, Named(level), leaveOpen);
#endif
    }

#if !NET9_0_OR_GREATER
    private static CompressionLevel Named(int level) =>
        level <= 3 ? CompressionLevel.Fastest : level >= 9 ? CompressionLevel.SmallestSize : CompressionLevel.Optimal;
#endif
}
