using System.Text;
using FsForge.Core;
using FsForge.Core.Cpio;
using FsForge.Core.Images;
using FsForge.Core.Tar;

namespace FsForge.Cli;

/// <summary>
/// cpio: round trips in every header format and compression, hard links whichever name carries the data,
/// several archives one after another (an initramfs with early microcode), odc archives, checksums,
/// truncated archives and the limits of the format.
/// </summary>
internal static partial class SelfTest
{
    private static void CpioTests(FsTree tree, string reference)
    {
        foreach (CpioHeader header in new[] { CpioHeader.Newc, CpioHeader.Crc })
            foreach (ArchiveCompression c in Enum.GetValues<ArchiveCompression>())
                Test($"cpio {header} {c}", () => RoundTrip(tree, reference, "w" + CpioFormat.DefaultExtension(c), CpioSave(new CpioOptions
                {
                    Header = header, Compression = c, GzipLevel = 6, ZstdLevel = 3, XzEffort = 0,
                })));
        Test("cpio with ./ names, without root entry and padding, all owned by root", () => RoundTrip(tree, reference, "dot.cpio",
            CpioSave(new CpioOptions { Compression = ArchiveCompression.None, PathPrefix = "./", IncludeRootEntry = false, Pad512 = false }), noRoot: true));
        Test("cpio: layout of the archive, settings kept on saving", () => CpioLayout(tree));
        Test("cpio: hard links with the data on the first or on the last name; odc", CpioHandBuilt);
        Test("cpio: archives one after another (early microcode + compressed initramfs)", CpioConcatenated);
        Test("cpio: damaged checksums, truncated archives, limits of the format", () => CpioLimits(tree));
    }

    private static SaveOptions CpioSave(CpioOptions o) => new() { Format = ImageFormat.Cpio, Cpio = o };

    private static void CpioLayout(FsTree tree)
    {
        byte[] archive = WriteImage(tree, CpioSave(new CpioOptions { Compression = ArchiveCompression.None }));
        Check(archive.Length % 512 == 0, "the archive is not padded to 512 bytes");
        string text = Encoding.ASCII.GetString(archive);
        Check(text.StartsWith("070701") && text.Contains("TRAILER!!!"), "magic or trailer missing");
        // The first entry is the root ("."), the names have no prefix, directories come before what they hold.
        Check(text.AsSpan(110, 2).SequenceEqual(".\0"), "the root entry is not first");
        int bin = text.IndexOf("bin\0", StringComparison.Ordinal), busybox = text.IndexOf("bin/busybox\0", StringComparison.Ordinal);
        Check(bin > 0 && busybox > bin, "a directory does not come before its content");

        string path = WriteFile("layout.cpio.zst", WriteImage(tree, CpioSave(new CpioOptions { Compression = ArchiveCompression.Zstd, ZstdLevel = 3 })));
        Check(ImageDocument.Detect(path) == ImageFormat.Cpio, "detected as " + ImageDocument.Detect(path));
        ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
        try
        {
            Check(doc.FormatName == "cpio.zst" && doc.Cpio is { Header: CpioHeader.Newc, Compression: ArchiveCompression.Zstd, PathPrefix: "", HasRootEntry: true, Parts: 1 },
                "info: " + doc.FormatName);
            Check(doc.Warnings.Count == 0, "warnings: " + string.Join("; ", doc.Warnings));
            SaveOptions same = doc.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir });
            Check(same.Format == ImageFormat.Cpio && same.Cpio.Compression == ArchiveCompression.Zstd, "options from the archive");
            doc.Tree.Remove(doc.Tree.Resolve("/etc/passwd")!);
            doc = doc.Save(path, same).Document;
            Check(doc.Cpio is { Compression: ArchiveCompression.Zstd } && doc.Tree.Resolve("/etc/passwd") is null, "saved in place");
        }
        finally
        {
            doc.Dispose();
        }

        // Extended attributes and times outside 1970..2106 cannot be stored: that is said.
        using var t = new FsTree(0x1ED, 1700000000);
        FsNode f = t.CreateFile(t.Root, "labelled", new MemoryFileData(new byte[] { 1 }), 0x1A4, 0, 0, -5);
        f.Inode.XAttrs = new(StringComparer.Ordinal) { ["security.selinux"] = Encoding.ASCII.GetBytes("u:object_r:x:s0\0") };
        using var ms = new MemoryStream();
        IReadOnlyList<string> warnings = ImageDocument.WriteTo(t, ms, CpioSave(new CpioOptions()));
        Check(warnings.Count == 2 && warnings.Any(w => w.Contains("extended attributes")) && warnings.Any(w => w.Contains("clamped")), "warnings: " + string.Join("; ", warnings));
    }

    // ------------------------------------------------------------ hand-built

    /// <summary>One newc entry: header, name, data, each padded to four bytes.</summary>
    private static void NewcEntry(MemoryStream w, string name, int mode, long ino, long nlink, byte[]? data, long mtime = 1700000000,
        uint uid = 0, uint gid = 0, uint rdevMajor = 0, uint rdevMinor = 0, string magic = "070701", uint? check = null)
    {
        byte[] n = Encoding.UTF8.GetBytes(name);
        data ??= Array.Empty<byte>();
        uint sum = 0;
        foreach (byte b in data) sum += b;
        string h = magic + string.Concat(new long[] { ino, mode, uid, gid, nlink, mtime, data.Length, 0, 0, rdevMajor, rdevMinor, n.Length + 1, check ?? (magic == "070702" ? sum : 0) }
            .Select(v => v.ToString("x8")));
        w.Write(Encoding.ASCII.GetBytes(h));
        w.Write(n);
        w.WriteByte(0);
        while (w.Length % 4 != 0) w.WriteByte(0);
        w.Write(data);
        while (w.Length % 4 != 0) w.WriteByte(0);
    }

    private static void Trailer(MemoryStream w, string magic = "070701") => NewcEntry(w, "TRAILER!!!", 0, 0, 1, null, 0, magic: magic);

    /// <summary>One odc entry: a 76-byte header in octal, the name, the data; no padding.</summary>
    private static void OdcEntry(MemoryStream w, string name, int mode, long ino, long nlink, byte[]? data, long rdev = 0)
    {
        byte[] n = Encoding.UTF8.GetBytes(name);
        data ??= Array.Empty<byte>();
        string O(long v, int digits) => Convert.ToString(v, 8).PadLeft(digits, '0');
        string h = "070707" + O(5, 6) + O(ino, 6) + O(mode, 6) + O(1000, 6) + O(100, 6) + O(nlink, 6) + O(rdev, 6) + O(1700000000, 11) + O(n.Length + 1, 6) + O(data.Length, 11);
        w.Write(Encoding.ASCII.GetBytes(h));
        w.Write(n);
        w.WriteByte(0);
        w.Write(data);
    }

    private static void CpioHandBuilt()
    {
        byte[] content = Encoding.ASCII.GetBytes("linked content\n");
        foreach (bool dataFirst in new[] { true, false })
        {
            var w = new MemoryStream();
            NewcEntry(w, ".", 0x41ED, 1, 3, null);
            NewcEntry(w, "bin", 0x41ED, 2, 2, null);
            // Three names of one inode; the data comes with the first or (GNU cpio, gen_init_cpio) with the last one.
            NewcEntry(w, "bin/a", 0x81ED, 7, 3, dataFirst ? content : null);
            NewcEntry(w, "bin/b", 0x81ED, 7, 3, null);
            NewcEntry(w, "c", 0x81ED, 7, 3, dataFirst ? null : content);
            NewcEntry(w, "dev/console", 0x2180, 9, 1, null, rdevMajor: 5, rdevMinor: 1);   // its directory has no entry
            NewcEntry(w, "link", 0xA1FF, 10, 1, Encoding.ASCII.GetBytes("bin/a"));
            NewcEntry(w, "/abs/file", 0x81A4, 11, 1, new byte[] { 1, 2, 3 });
            NewcEntry(w, "c", 0x81A4, 12, 1, Encoding.ASCII.GetBytes("replaced"));       // a later entry replaces an earlier one
            NewcEntry(w, "../evil", 0x81A4, 13, 1, new byte[] { 9 });
            Trailer(w);
            string path = WriteFile($"links-{dataFirst}.cpio", w.ToArray());
            using ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
            FsInode a = doc.Tree.Resolve("/bin/a")!.Inode;
            Check(ReferenceEquals(a, doc.Tree.Resolve("/bin/b")!.Inode) && a.LinkCount == 2, $"hard links ({dataFirst})");
            using (Stream s = a.Data!.OpenRead()) Check(new StreamReader(s).ReadToEnd() == "linked content\n", $"content of the hard link ({dataFirst})");
            using (Stream s = doc.Tree.Resolve("/c")!.Inode.Data!.OpenRead()) Check(new StreamReader(s).ReadToEnd() == "replaced", "the later entry does not replace the earlier one");
            Check(doc.Tree.Resolve("/dev/console")!.Inode is { Type: FsNodeType.CharDevice, DevMajor: 5, DevMinor: 1, Mode: 0x180 }, "device");
            Check(doc.Tree.Resolve("/dev")!.Inode.Mode == 0x1ED, "implicit directory");
            Check(doc.Tree.Resolve("/link")!.Inode.LinkTarget == "bin/a", "symbolic link");
            Check(doc.Tree.Resolve("/abs/file") is { Inode.Size: 3 }, "absolute name");
            Check(doc.Tree.Resolve("/evil") is null && doc.Warnings.Any(x => x.Contains("'..'")), "a name with '..' was not refused");
            Check(doc.Cpio is { HasRootEntry: true, PathPrefix: "" }, "root entry and prefix");
        }

        // odc: octal headers without padding; GNU cpio stores the data with every name of a hard link.
        var o = new MemoryStream();
        OdcEntry(o, ".", 0x41ED, 1, 2, null);
        OdcEntry(o, "one", 0x81A4, 3, 2, content);
        OdcEntry(o, "two", 0x81A4, 3, 2, content);
        OdcEntry(o, "tty", 0x21A4, 4, 1, null, rdev: 4 << 8 | 64);
        OdcEntry(o, "TRAILER!!!", 0, 0, 1, null);
        string odc = WriteFile("old.cpio.gz", Gzip(o.ToArray()));
        using (ImageDocument doc = ImageDocument.Open(odc, _dir, searchEmbedded: false))
        {
            Check(doc.Cpio is { Header: CpioHeader.Odc, Compression: ArchiveCompression.Gzip }, "odc header");
            FsInode one = doc.Tree.Resolve("/one")!.Inode;
            Check(ReferenceEquals(one, doc.Tree.Resolve("/two")!.Inode) && one.Uid == 1000 && one.Gid == 100 && one.Size == content.Length, "odc hard link");
            Check(doc.Tree.Resolve("/tty")!.Inode is { DevMajor: 4, DevMinor: 64 }, "odc device");
            // Saved again it becomes newc: odc is read only.
            SaveOptions so = doc.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir });
            Check(so.Cpio.Header == CpioHeader.Newc, "odc is not written");
        }
    }

    private static byte[] Gzip(byte[] data)
    {
        var ms = new MemoryStream();
        using (var g = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true)) g.Write(data);
        return ms.ToArray();
    }

    private static void CpioConcatenated()
    {
        // Early microcode: an uncompressed archive, padded; then the system, compressed; within it two archives.
        var early = new MemoryStream();
        NewcEntry(early, "kernel", 0x41ED, 1, 3, null);
        NewcEntry(early, "kernel/x86", 0x41ED, 2, 3, null);
        NewcEntry(early, "kernel/x86/microcode", 0x41ED, 3, 2, null);
        NewcEntry(early, "kernel/x86/microcode/GenuineIntel.bin", 0x81A4, 4, 1, Bytes(new Random(3), 5000));
        Trailer(early);
        while (early.Length % 512 != 0) early.WriteByte(0);

        var main = new MemoryStream();
        NewcEntry(main, ".", 0x41ED, 1, 2, null);
        NewcEntry(main, "./init", 0x81ED, 2, 1, Encoding.ASCII.GetBytes("#!/bin/sh\n"));
        Trailer(main);
        main.Write(new byte[100]);
        NewcEntry(main, "./extra.conf", 0x81A4, 3, 1, Encoding.ASCII.GetBytes("added later\n"));
        Trailer(main);

        foreach (ArchiveCompression c in new[] { ArchiveCompression.Zstd, ArchiveCompression.Gzip, ArchiveCompression.None })
        {
            byte[] tail = c switch
            {
                ArchiveCompression.Zstd => new ZstdSharp.Compressor(3).Wrap(main.ToArray()).ToArray(),
                ArchiveCompression.Gzip => Gzip(main.ToArray()),
                _ => main.ToArray(),
            };
            string path = WriteFile("initrd.img", early.ToArray().Concat(tail).ToArray());
            Check(ImageDocument.Detect(path) == ImageFormat.Cpio, $"{c}: detected as {ImageDocument.Detect(path)}");
            using ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
            Check(doc.Cpio is { Parts: 3 } && doc.Warnings.Any(w => w.Contains("3 cpio archives")), $"{c}: parts {doc.Cpio?.Parts}; " + string.Join("; ", doc.Warnings));
            Check(doc.Cpio!.Compression == c, $"{c}: compression {doc.Cpio.Compression}");
            // The names of the system decide the prefix, not those of the microcode in front of it.
            Check(doc.Cpio is { PathPrefix: "./", HasRootEntry: true }, $"{c}: prefix '{doc.Cpio.PathPrefix}', root entry {doc.Cpio.HasRootEntry}");
            Check(doc.Tree.Resolve("/kernel/x86/microcode/GenuineIntel.bin") is { Inode.Size: 5000 } && doc.Tree.Resolve("/init") is not null
                  && doc.Tree.Resolve("/extra.conf") is not null, $"{c}: content of the parts");
        }

        // Something else after the archive is reported and left alone.
        var junk = new MemoryStream();
        junk.Write(main.ToArray());
        junk.Write(Encoding.ASCII.GetBytes("BZh91AY&SY"));
        string withJunk = WriteFile("junk.cpio", junk.ToArray());
        using (ImageDocument doc = ImageDocument.Open(withJunk, _dir, searchEmbedded: false))
            Check(doc.Warnings.Any(w => w.Contains("bzip2")) && doc.Tree.Resolve("/extra.conf") is not null, "data after the archive: " + string.Join("; ", doc.Warnings));
    }

    // ---------------------------------------------------------------- limits

    /// <summary>Content that only claims a size.</summary>
    private sealed class HugeData(long length) : IFileData
    {
        public long Length => length;
        public Stream OpenRead() => throw new InvalidOperationException("the content of a huge file was read");
    }

    private static void CpioLimits(FsTree tree)
    {
        // A wrong checksum in a crc archive.
        var w = new MemoryStream();
        NewcEntry(w, "good", 0x81A4, 1, 1, new byte[] { 1, 2, 3 }, magic: "070702");
        NewcEntry(w, "bad", 0x81A4, 2, 1, new byte[] { 1, 2, 3 }, magic: "070702", check: 7);
        Trailer(w, "070702");
        using (ImageDocument doc = ImageDocument.Open(WriteFile("sum.cpio", w.ToArray()), _dir, searchEmbedded: false))
            Check(doc.Cpio!.Header == CpioHeader.Crc && doc.Warnings.Any(x => x.Contains("1 file(s) do not match the checksum")), "checksum: " + string.Join("; ", doc.Warnings));

        // Without its trailer: a warning; cut in a header: an error.
        byte[] full = WriteImage(tree, CpioSave(new CpioOptions { Compression = ArchiveCompression.None, Pad512 = false }));
        int trailer = Encoding.ASCII.GetString(full).LastIndexOf("070701", StringComparison.Ordinal);
        using (ImageDocument doc = ImageDocument.Open(WriteFile("cut.cpio", full[..trailer]), _dir, searchEmbedded: false))
            Check(doc.Cpio!.Truncated && doc.Warnings.Any(x => x.Contains("without its trailer")), "a missing trailer was not reported");
        ExpectFs(() => ImageDocument.Open(WriteFile("cut2.cpio", full[..(trailer - 30)]), _dir, searchEmbedded: false).Dispose());
        byte[] binary = { 0xC7, 0x71, 0, 0, 0, 0, 0, 0, 0, 0 };
        try
        {
            ImageDocument.Open(WriteFile("binary.cpio", binary), _dir, searchEmbedded: false).Dispose();
            throw new TestFailure("a binary cpio archive was accepted");
        }
        catch (FsException ex) when (ex is not FsUnexpectedException)
        {
            Check(ex.Message.Contains("binary"), "binary cpio: " + ex.Message);
        }

        // A file of 4 GiB or more does not fit a newc header; odc is not written; the prefix is checked.
        using var t = new FsTree(0x1ED, 1700000000);
        t.CreateFile(t.Root, "huge", new HugeData(5L << 30));
        ExpectFs(() => WriteImage(t, CpioSave(new CpioOptions { Compression = ArchiveCompression.None })));
        ExpectFs(() => WriteImage(tree, CpioSave(new CpioOptions { Header = CpioHeader.Odc })));
        ExpectFs(() => WriteImage(tree, CpioSave(new CpioOptions { PathPrefix = "root/" })));
    }
}
