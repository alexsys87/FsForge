using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FsForge.Core.Tar;

namespace FsForge.Core.Images;

/// <summary>
/// Files that sit next to an image and describe its contents, as FlashTool, bmaptool and image publishers
/// keep them: SHA-256 checksums (<c>image.img.xz.sha256</c>, <c>SHA256SUMS</c>) and the block map
/// (<c>image.img.bmap</c>). Once the image changes they no longer match: FlashTool would then refuse the
/// image or, with an outdated block map, skip blocks that now hold data. They are brought up to date.
/// </summary>
internal static class Sidecars
{
    private static readonly string[] ChecksumSuffixes = { ".sha256", ".sha256sum", ".sha" };
    private static readonly string[] ChecksumLists = { "SHA256SUMS", "sha256sums", "SHA256SUMS.txt", "sha256sums.txt", "sha256sum.txt" };
    private const long MaxTextSize = 1 << 20;
    public const int BmapBlockSize = 4096;

    private static readonly Regex Gnu = new(@"^(?<hash>[0-9A-Fa-f]{64})\s+\*?(?<name>.+)$", RegexOptions.CultureInvariant);
    private static readonly Regex Bsd = new(@"^SHA256\s*\((?<name>.+)\)\s*=\s*(?<hash>[0-9A-Fa-f]{64})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Bare = new(@"^(?<hash>[0-9A-Fa-f]{64})$", RegexOptions.CultureInvariant);

    /// <summary>The block map that belongs to an image, if there is one: "x.wic.zst" uses "x.wic.bmap" or "x.wic.zst.bmap".</summary>
    public static string? FindBmap(string imagePath)
    {
        string dir = Path.GetDirectoryName(imagePath) ?? "";
        string name = Path.GetFileName(imagePath);
        string stripped = ArchiveCodec.RemoveCompressionExtension(name);
        IEnumerable<string> candidates = stripped == name ? new[] { name + ".bmap" } : new[] { stripped + ".bmap", name + ".bmap" };
        return candidates.Select(c => Path.Combine(dir, c)).FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// A block map in the format of bmaptool 2.0 for an uncompressed image: every 4 KiB block that holds
    /// anything but zeros is mapped, with the SHA-256 of each range and of the file itself.
    /// </summary>
    public static byte[] BuildBmap(string rawImage, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        using var input = new FileStream(rawImage, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        long size = input.Length;
        long blocks = (size + BmapBlockSize - 1) / BmapBlockSize;
        var ranges = new List<(long First, long Last, string Hash)>();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buf = new byte[1 << 20];
        long block = 0, rangeStart = -1;
        int fill = 0;
        while (true)
        {
            int n = input.Read(buf, fill, buf.Length - fill);
            fill += n;
            bool end = n <= 0;
            int whole = end ? fill : fill / BmapBlockSize * BmapBlockSize;
            for (int off = 0; off < whole; off += BmapBlockSize, block++)
            {
                ReadOnlySpan<byte> b = buf.AsSpan(off, Math.Min(BmapBlockSize, whole - off));
                bool data = b.IndexOfAnyExcept((byte)0) >= 0;
                if (data)
                {
                    if (rangeStart < 0) rangeStart = block;
                    sha.AppendData(b);
                }
                else if (rangeStart >= 0)
                {
                    ranges.Add((rangeStart, block - 1, Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant()));
                    rangeStart = -1;
                }
            }
            if (end) break;
            Buffer.BlockCopy(buf, whole, buf, 0, fill - whole);
            fill -= whole;
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ProgressInfo("bmap", block * BmapBlockSize, size));
        }
        if (rangeStart >= 0) ranges.Add((rangeStart, blocks - 1, Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant()));

        long mapped = ranges.Sum(r => r.Last - r.First + 1);
        string zeros = new('0', 64);
        var t = new StringBuilder();
        t.Append("<?xml version=\"1.0\" ?>\n");
        t.Append("<!-- Block map of an image saved by FsForge, in the format of bmaptool.\n");
        t.Append(CultureInfo.InvariantCulture, $"     Image size {size} bytes, {mapped} of {blocks} blocks of {BmapBlockSize} bytes hold data;\n");
        t.Append("     the rest are zeros that do not have to be written. -->\n");
        t.Append("<bmap version=\"2.0\">\n");
        t.Append(CultureInfo.InvariantCulture, $"    <ImageSize> {size} </ImageSize>\n");
        t.Append(CultureInfo.InvariantCulture, $"    <BlockSize> {BmapBlockSize} </BlockSize>\n");
        t.Append(CultureInfo.InvariantCulture, $"    <BlocksCount> {blocks} </BlocksCount>\n");
        t.Append(CultureInfo.InvariantCulture, $"    <MappedBlocksCount> {mapped} </MappedBlocksCount>\n");
        t.Append("    <ChecksumType> sha256 </ChecksumType>\n");
        t.Append(CultureInfo.InvariantCulture, $"    <BmapFileChecksum> {zeros} </BmapFileChecksum>\n");
        t.Append("    <BlockMap>\n");
        foreach (var (first, last, hash) in ranges)
        {
            string range = first == last ? first.ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{first}-{last}");
            t.Append(CultureInfo.InvariantCulture, $"        <Range chksum=\"{hash}\"> {range} </Range>\n");
        }
        t.Append("    </BlockMap>\n");
        t.Append("</bmap>\n");
        byte[] bytes = Encoding.ASCII.GetBytes(t.ToString());
        // The checksum of the file is computed with its own field set to zeros, as bmaptool does.
        int at = bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(zeros));
        Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()).CopyTo(bytes, at);
        return bytes;
    }

    /// <summary>
    /// Brings the files next to a just saved image up to date. Returns a note for each file that was changed;
    /// a file that cannot be written is reported as well, it is never a reason to fail the save.
    /// </summary>
    public static List<string> Update(string imagePath, byte[]? bmap)
    {
        var notes = new List<string>();
        string dir = Path.GetDirectoryName(imagePath) ?? "";
        string name = Path.GetFileName(imagePath);

        if (bmap is not null && FindBmap(imagePath) is { } bmapPath)
        {
            try
            {
                File.WriteAllBytes(bmapPath, bmap);
                notes.Add($"The block map '{Path.GetFileName(bmapPath)}' was rebuilt for the new image.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notes.Add($"The block map '{Path.GetFileName(bmapPath)}' could not be updated ({ex.Message}); it no longer matches the image and should be deleted.");
            }
        }

        var files = ChecksumSuffixes.Select(s => (Path: Path.Combine(dir, name + s), Own: true))
            .Concat(ChecksumLists.Select(l => (Path: Path.Combine(dir, l), Own: false)))
            .Where(f => File.Exists(f.Path))
            .DistinctBy(f => OperatingSystem.IsWindows() ? f.Path.ToUpperInvariant() : f.Path)
            .ToList();
        if (files.Count == 0) return notes;

        string? hash = null;
        foreach (var (path, own) in files)
        {
            try
            {
                if (new FileInfo(path).Length > MaxTextSize) continue;
                string text = File.ReadAllText(path);
                hash ??= Convert.ToHexString(Sha256Of(imagePath)).ToLowerInvariant();
                if (Replace(text, name, hash, own) is not { } updated) continue;
                if (updated == text) continue;
                File.WriteAllText(path, updated, new UTF8Encoding(false));
                notes.Add($"The SHA-256 checksum of '{name}' in '{Path.GetFileName(path)}' was updated for the new file.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notes.Add($"The checksum file '{Path.GetFileName(path)}' could not be updated ({ex.Message}); it no longer matches '{name}'.");
            }
        }
        return notes;
    }

    private static byte[] Sha256Of(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return SHA256.HashData(fs);
    }

    /// <summary>
    /// The text with the value for <paramref name="fileName"/> replaced, keeping the style of each line; null when the
    /// file lists no value for it. A file of the image's own name (<c>image.sha256</c>) with a single value is
    /// taken to be about the image whatever name it gives.
    /// </summary>
    internal static string? Replace(string text, string fileName, string hash, bool own)
    {
        string[] lines = text.Split('\n');
        int values = 0;
        var matching = new List<int>();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r').Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            Match m = Gnu.Match(line);
            if (!m.Success) m = Bsd.Match(line);
            if (!m.Success) m = Bare.Match(line);
            if (!m.Success) continue;
            values++;
            string listed = m.Groups["name"].Success ? m.Groups["name"].Value.Trim() : "";
            if (listed.Length > 0 && string.Equals(Path.GetFileName(listed.Replace('\\', '/')), fileName, StringComparison.OrdinalIgnoreCase))
                matching.Add(i);
            else if (listed.Length == 0 || own) matching.Add(-i - 1);
        }
        var chosen = matching.Where(i => i >= 0).ToList();
        if (chosen.Count == 0 && own && values == 1) chosen = matching.Select(i => i < 0 ? -i - 1 : i).ToList();
        if (chosen.Count == 0) return null;
        foreach (int i in chosen)
        {
            string old = lines[i];
            Match m = Gnu.Match(old.Trim().TrimStart('﻿'));
            if (!m.Success) m = Bsd.Match(old.Trim().TrimStart('﻿'));
            if (!m.Success) m = Bare.Match(old.Trim().TrimStart('﻿'));
            string oldHash = m.Groups["hash"].Value;
            bool upper = oldHash.Any(char.IsUpper);
            lines[i] = old.Replace(oldHash, upper ? hash.ToUpperInvariant() : hash);
        }
        return string.Join('\n', lines);
    }
}
