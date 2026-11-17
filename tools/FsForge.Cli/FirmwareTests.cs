using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FsForge.Core;
using FsForge.Core.Cpio;
using FsForge.Core.Ext;
using FsForge.Core.Firmware;
using FsForge.Core.Images;
using FsForge.Core.SquashFs;
using FsForge.Core.Tar;

namespace FsForge.Cli;

/// <summary>
/// Vendor firmware headers. The uImage files are built here from U-Boot's image.h, with a CRC-32 of their own,
/// independently of the code under test: an OpenWrt-style sysupgrade image (kernel uImage, padding to the erase
/// block, SquashFS behind it), a uInitrd, a multi-file image with a gzipped ext2 ramdisk and a device tree.
/// Each is opened at its filesystem, which must read as the image itself. Saving rebuilds the firmware: the header
/// sizes and checksums are checked against this independent CRC, everything but the changed part must stay byte for
/// byte, and what follows a root filesystem behind the kernel must stay on its erase block boundaries.
/// </summary>
internal static partial class SelfTest
{
    private static void FirmwareTests()
    {
        Test("uImage: kernel with the root filesystem behind it (sysupgrade)", UImageSysupgrade);
        Test("uImage: uInitrd and filesystem images", UImageRamdisk);
        Test("uImage: multi-file image, gzipped ext2 ramdisk, choice of part", UImageMulti);
        Test("uImage: checksums; the filesystem alone is never written over the firmware", UImageChecks);
        Test("uImage: sysupgrade rebuilt; padding markers and trailer kept on their boundaries", UImageRebuildSysupgrade);
        Test("uImage: uInitrd and multi-file images rebuilt; sizes, checksums, other images kept", UImageRebuildInner);
        Test("uImage: corrupted images are rejected cleanly", UImageFuzz);
    }

    private const byte UbArm = 2, UbMips = 5, UbKernel = 2, UbRamdisk = 3, UbMulti = 4, UbFilesystem = 7, UbNone = 0, UbGzip = 1, UbLzma = 3;

    /// <summary>A legacy uImage as mkimage makes it: the 64-byte big-endian header, then the data.</summary>
    private static byte[] BuildUImage(byte arch, byte type, byte comp, string name, byte[] data, uint load = 0, uint time = 1700000000)
    {
        byte[] img = new byte[64 + data.Length];
        Span<byte> h = img.AsSpan(0, 64);
        BinaryPrimitives.WriteUInt32BigEndian(h, 0x27051956);
        BinaryPrimitives.WriteUInt32BigEndian(h[8..], time);
        BinaryPrimitives.WriteUInt32BigEndian(h[12..], (uint)data.Length);
        BinaryPrimitives.WriteUInt32BigEndian(h[16..], load);
        BinaryPrimitives.WriteUInt32BigEndian(h[20..], load);
        BinaryPrimitives.WriteUInt32BigEndian(h[24..], RefCrc32(data));
        h[28] = 5;                  // Linux
        h[29] = arch;
        h[30] = type;
        h[31] = comp;
        Encoding.ASCII.GetBytes(name).CopyTo(h[32..]);
        BinaryPrimitives.WriteUInt32BigEndian(h[4..], RefCrc32(h.ToArray()));
        data.CopyTo(img, 64);
        return img;
    }

    /// <summary>Data of a multi-file image: the sizes, a zero, then the images padded to 4 bytes.</summary>
    private static byte[] MultiData(params byte[][] images)
    {
        var ms = new MemoryStream();
        Span<byte> w = stackalloc byte[4];
        foreach (byte[] i in images.Append(Array.Empty<byte>()))
        {
            BinaryPrimitives.WriteUInt32BigEndian(w, (uint)i.Length);
            ms.Write(w);
        }
        for (int k = 0; k < images.Length; k++)
        {
            ms.Write(images[k]);
            if (k < images.Length - 1) ms.Write(new byte[(4 - images[k].Length % 4) % 4]);
        }
        return ms.ToArray();
    }

    /// <summary>Bitwise CRC-32 (IEEE 802.3, as zlib), kept apart from the table-driven one of the core.</summary>
    private static uint RefCrc32(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320 : c >> 1;
        }
        return ~c;
    }

    private static byte[] PadTo(byte[] data, int block, byte fill)
    {
        byte[] r = new byte[(data.Length + block - 1) / block * block];
        r.AsSpan().Fill(fill);
        data.CopyTo(r, 0);
        return r;
    }

    private static byte[] Squash(FsTree tree) =>
        WriteImage(tree, new SaveOptions { Format = ImageFormat.SquashFs, SquashFs = new SquashFsOptions { Compression = SquashCompression.Gzip } });

    private static void UImageSysupgrade()
    {
        var rnd = new Random(31);
        FsTree tree = SmallTree(31, 12);
        byte[] kernel = BuildUImage(UbMips, UbKernel, UbLzma, "OpenWrt Linux-6.6.73", Bytes(rnd, 300000), 0x80060000);
        byte[] rootfs = Squash(tree);
        // OpenWrt: the kernel padded to the erase block, the SquashFS, then the JFFS2 marker for rootfs_data.
        byte[] fw = PadTo(kernel, 65536, 0xFF).Concat(PadTo(rootfs, 65536, 0)).Concat(new byte[] { 0xDE, 0xAD, 0xC0, 0xDE }).ToArray();
        string path = WriteFile("sysupgrade.bin", fw);
        Check(ImageDocument.Detect(path) == ImageFormat.SquashFs, "detected as " + ImageDocument.Detect(path));
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
        {
            Check(doc.Format == ImageFormat.SquashFs && doc.Firmware is { Part.Name: "rootfs", Image: UImage ui } && ui.Name == "OpenWrt Linux-6.6.73"
                  && ui.DataCrcMatches && ui.LoadAddress == 0x80060000, "opened at the rootfs");
            Check(doc.Firmware!.Part.Offset == PadTo(kernel, 65536, 0xFF).Length && doc.EmbeddedOffset == doc.Firmware.Part.Offset, "offset of the rootfs");
            Check(doc.Firmware.Image.Parts is [{ Name: "kernel", IsReadable: false, ContentName: "kernel (lzma)" }, { Name: "rootfs" }], "parts");
            Check(!doc.SavesFilesystemOnly, "the firmware is rebuilt when saved");
            Same(Program.DumpToString(tree), Program.DumpToString(doc.Tree), "rootfs");
        }
        // The rootfs right behind the kernel, with no padding.
        path = WriteFile("sysupgrade-tight.bin", kernel.Concat(rootfs).ToArray());
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
            Check(doc.Firmware?.Part.Offset == kernel.Length, "rootfs right after the kernel");
        // The same firmware packed with gzip, as it is often downloaded.
        string gz = Path.Combine(_dir, "sysupgrade.bin.gz");
        using (var f = File.Create(gz))
        using (var z = new GZipStream(f, CompressionLevel.Fastest))
            z.Write(fw);
        using (ImageDocument doc = ImageDocument.Open(gz, _dir, searchEmbedded: false))
            Check(doc.Firmware is { Part.Name: "rootfs" } && doc.OuterCompression == ArchiveCompression.Gzip, "gzipped firmware");
    }

    private static void UImageRamdisk()
    {
        FsTree tree = SmallTree(32, 8);
        byte[] cpio = Ramdisk(tree, ArchiveCompression.Gzip);
        string path = WriteFile("uInitrd", BuildUImage(UbArm, UbRamdisk, UbGzip, "initramfs", cpio));
        Check(ImageDocument.Detect(path) == ImageFormat.Cpio, "uInitrd detected as " + ImageDocument.Detect(path));
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
        {
            Check(doc.Format == ImageFormat.Cpio && doc.Cpio!.Compression == ArchiveCompression.Gzip && doc.Firmware is { Part.Name: "ramdisk", Part.Offset: 64 },
                "uInitrd opened at the ramdisk");
            Check(!doc.SavesFilesystemOnly && doc.EmbeddedOffset == 64, "uInitrd is rebuilt when saved");
            Same(Program.DumpToString(tree), Program.DumpToString(doc.Tree), "uInitrd");
        }
        path = WriteFile("fs.uImage", BuildUImage(UbArm, UbFilesystem, UbNone, "rootfs", Squash(tree)));
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
        {
            Check(doc.Format == ImageFormat.SquashFs && doc.Firmware is { Part.Name: "filesystem" }, "filesystem image");
            Same(Program.DumpToString(tree), Program.DumpToString(doc.Tree), "filesystem image");
        }
    }

    private static void UImageMulti()
    {
        var rnd = new Random(33);
        FsTree tree = SmallTree(33, 10);
        byte[] ext2 = WriteImage(tree, new SaveOptions { Format = ImageFormat.Ext, Ext = new ExtOptions { Kind = ExtKind.Ext2, BlockSize = 1024, Timestamp = 1700000000 } });
        var gz = new MemoryStream();
        using (var z = new GZipStream(gz, CompressionLevel.Fastest, leaveOpen: true)) z.Write(ext2);
        byte[] dtb = new byte[1001];
        BinaryPrimitives.WriteUInt32BigEndian(dtb, 0xD00DFEED);
        // Sizes that are not multiples of 4, so that the padding between the images matters.
        byte[] data = MultiData(Bytes(rnd, 99999), gz.ToArray(), dtb);
        string path = WriteFile("multi.uImage", BuildUImage(UbArm, UbMulti, UbNone, "multi", data));
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
        {
            Check(doc.Format == ImageFormat.Ext && doc.Firmware is { Part.Name: "ramdisk", Image.Kind: "U-Boot uImage (multi-file)" }, "opened at the ramdisk");
            Check(doc.Firmware!.Image.Parts is [{ Name: "kernel", Offset: 80 }, { Name: "ramdisk", Offset: 80 + 100000, Packed: ArchiveCompression.Gzip }, { Name: "fdt", ContentName: "device tree" }],
                "parts of the multi-file image");
            // ext2 gains a lost+found: compare with the ext2 image itself.
            using ImageDocument plain = ImageDocument.Open(WriteFile("multi-ramdisk.ext2", ext2), _dir, searchEmbedded: false);
            Same(Program.DumpToString(plain.Tree), Program.DumpToString(doc.Tree), "ext2 ramdisk");
        }
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false, volume: "1"))
            Check(doc.Firmware?.Part.Name == "ramdisk", "part chosen by number");
        ExpectFs(() => ImageDocument.Open(path, _dir, searchEmbedded: false, volume: "fdt").Dispose());
        ExpectFs(() => ImageDocument.Open(path, _dir, searchEmbedded: false, volume: "nothing").Dispose());
        ExpectFs(() => ImageDocument.Open(path, _dir, searchEmbedded: false, partition: 2).Dispose());
        // The caller is asked when there are several parts it could open.
        byte[] two = MultiData(Bytes(rnd, 5000), Ramdisk(tree, ArchiveCompression.None), Squash(tree));
        path = WriteFile("multi2.uImage", BuildUImage(UbArm, UbMulti, UbNone, "two", two));
        int asked = 0;
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false,
                   chooseFirmwarePart: image => { asked++; return image.Parts.Single(p => p.Content == PartitionContent.Cpio); }))
            Check(asked == 1 && doc.Format == ImageFormat.Cpio && doc.Firmware?.Part.Name == "ramdisk", "chosen by the caller");
        ExpectCancel(() => ImageDocument.Open(path, _dir, searchEmbedded: false, chooseFirmwarePart: _ => null).Dispose());
        // A kernel alone holds nothing to open.
        path = WriteFile("kernel.uImage", BuildUImage(UbArm, UbKernel, UbGzip, "kernel", Bytes(rnd, 20000)));
        ExpectFs(() => ImageDocument.Open(path, _dir, searchEmbedded: false).Dispose());
    }

    private static void ExpectCancel(Action a)
    {
        try
        {
            a();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        throw new TestFailure("the operation was not cancelled");
    }

    private static void UImageChecks()
    {
        FsTree tree = SmallTree(34, 6);
        byte[] rootfs = Squash(tree);
        byte[] img = BuildUImage(UbArm, UbFilesystem, UbNone, "rootfs", rootfs);
        // A changed byte in the data: opened, with a warning.
        byte[] changed = (byte[])img.Clone();
        changed[^1] ^= 0x55;
        string path = WriteFile("crc.uImage", changed);
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
            Check(doc.Firmware?.Image is UImage { DataCrcMatches: false } && doc.Warnings.Any(w => w.Contains("CRC-32")), "wrong data CRC reported");
        // A damaged header is no uImage: the SquashFS inside is then only found by the search.
        byte[] badHeader = (byte[])img.Clone();
        badHeader[40] ^= 1;
        path = WriteFile("badheader.uImage", badHeader);
        ExpectFs(() => ImageDocument.Open(path, _dir, searchEmbedded: false).Dispose());
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: true))
            Check(doc.Firmware is null && doc.EmbeddedOffset == 64, "found by the search");
        // A truncated image: opened as far as it goes, with a warning.
        byte[] truncated = BuildUImage(UbArm, UbFilesystem, UbNone, "rootfs", rootfs.Concat(new byte[8192]).ToArray()).AsSpan(0, 64 + rootfs.Length).ToArray();
        path = WriteFile("truncated.uImage", truncated);
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
        {
            // It cannot be rebuilt: saved alone, never over the file.
            Check(doc.Warnings.Any(w => w.Contains("truncated")) && doc.SavesFilesystemOnly && doc.Warnings.Any(w => w.Contains("cannot be rebuilt")), "truncation reported");
            SaveOptions t = doc.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir });
            Check(!doc.WritesContainer(t), "a truncated firmware is not rebuilt");
            ExpectFs(() => doc.Save(path, t));
        }
        Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(truncated), "the truncated firmware was changed");

        // The filesystem alone (or a tar archive) is never written over the firmware, only under another name.
        path = WriteFile("save.uImage", img);
        using ImageDocument d = ImageDocument.Open(path, _dir, searchEmbedded: false);
        d.Tree.Remove(d.Tree.Resolve("/file0")!);
        SaveOptions o = d.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir, KeepContainer = false });
        Check(!d.WritesContainer(o) && !d.WritesFirmware(o), "no container is written");
        ExpectFs(() => d.Save(path, o));
        SaveOptions tar = d.CreateSaveOptions(ImageFormat.Tar, new SaveOptions { TempDirectory = _dir });
        Check(!d.WritesFirmware(tar), "a tar archive goes into no firmware");
        ExpectFs(() => d.Save(path, tar));
        Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(img), "the firmware file was changed");
        string alone = Path.Combine(_dir, "save-alone.sqfs");
        d.Save(alone, o).Document?.Dispose();
        using ImageDocument back = ImageDocument.Open(alone, _dir, searchEmbedded: false);
        Check(back.Format == ImageFormat.SquashFs && back.Firmware is null && back.Tree.Resolve("/file0") is null && back.Tree.Resolve("/file2") is not null, "saved alone");
    }

    /// <summary>Checks the header of a written uImage with the independent CRC and returns its data size.</summary>
    private static uint CheckUImage(byte[] img, string what)
    {
        byte[] h = img[..64];
        uint hcrc = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(4));
        h.AsSpan(4, 4).Clear();
        uint size = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(12));
        Check(RefCrc32(h) == hcrc, what + ": header CRC");
        Check(64 + size <= img.Length && RefCrc32(img.AsSpan(64, (int)size).ToArray()) == BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(24)), what + ": data CRC");
        return size;
    }

    private static List<int> Markers(byte[] img) =>
        Enumerable.Range(0, img.Length / 4).Select(i => i * 4).Where(i => BinaryPrimitives.ReadUInt32BigEndian(img.AsSpan(i)) == 0xDEADC0DE).ToList();

    private static void UImageRebuildSysupgrade()
    {
        var rnd = new Random(36);
        FsTree tree = SmallTree(36, 6);
        byte[] kernel = PadTo(BuildUImage(UbMips, UbKernel, UbLzma, "OpenWrt Linux", Bytes(rnd, 200000), 0x80060000), 65536, 0xFF);
        // As OpenWrt's padjffs2 leaves it: 0xdeadc0de at the 4, 8, 16, 32 and 64 KiB boundaries behind the SquashFS,
        // then a metadata block of fwtool at the end.
        var fw = new List<byte>(kernel);
        fw.AddRange(Squash(tree));
        foreach (int b in new[] { 4096, 8192, 16384, 32768, 65536 })
        {
            while (fw.Count % b != 0) fw.Add(0xFF);
            fw.AddRange(new byte[] { 0xDE, 0xAD, 0xC0, 0xDE });
        }
        byte[] meta = Encoding.ASCII.GetBytes("{\"supported_devices\":[\"board\"]}");
        fw.AddRange(meta);
        fw.AddRange("FWx0"u8.ToArray().Concat(new byte[] { 1, 2, 3, 4, 1, 0, 0, 0, 0, 0, 0, (byte)(meta.Length + 16) }));
        byte[] original = fw.ToArray();
        int tailStart = Markers(original)[0];
        byte[] tail = original[tailStart..];
        string path = WriteFile("rebuild-sysupgrade.bin", original);

        // A file that makes the SquashFS grow past several of the boundaries, then removed again.
        long grownLength = 0;
        foreach ((string what, int grow) in new[] { ("grown", 300000), ("shrunk", 0) })
        {
            ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
            try
            {
                if (grow > 0) doc.Tree.CreateFile(doc.Tree.Root, "big", new MemoryFileData(Bytes(rnd, grow)));
                else doc.Tree.Remove(doc.Tree.Resolve("/big")!);
                SaveOptions o = doc.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir });
                Check(doc.WritesContainer(o) && doc.WritesFirmware(o), what + ": the firmware is written");
                ImageDocument.SaveResult r = doc.Save(path, o);
                doc = r.Document!;
                Check(r.Warnings.Any(w => w.Contains("fwtool")) && (grow == 0 || r.Warnings.Any(w => w.Contains("grew"))), what + ": warnings " + string.Join(" | ", r.Warnings));
                Check(doc.Firmware is { Part.Name: "rootfs" } && (doc.Tree.Resolve("/big") is not null) == (grow > 0), what + ": reopened");
            }
            finally
            {
                doc.Dispose();
            }
            byte[] after = File.ReadAllBytes(path);
            CheckUImage(after, what);
            Check(after.AsSpan(0, kernel.Length).SequenceEqual(kernel), what + ": the kernel and its padding changed");
            List<int> marks = Markers(after);
            long used = (long)BinaryPrimitives.ReadUInt64LittleEndian(after.AsSpan(kernel.Length + 40));
            long fsEnd = kernel.Length + used;
            // Whatever the erase block of the flash (4 to 64 KiB), its first boundary behind the filesystem holds a marker.
            foreach (int block in new[] { 4096, 8192, 16384, 32768, 65536 })
                Check(marks.Contains((int)((fsEnd + block - 1) / block * block)), what + $": no marker at the first {block / 1024} KiB boundary; markers at " +
                                                                                 string.Join(",", marks.Select(m => $"0x{m:X}")));
            Check(marks[0] >= fsEnd && after.AsSpan(marks[^1] + 4).SequenceEqual(tail.AsSpan(Markers(original)[^1] - tailStart + 4)),
                what + ": the data behind the markers changed");
            if (grow > 0) grownLength = after.Length;
            else Check(after.Length < grownLength, "the firmware did not shrink when the file was removed");
        }
    }

    private static void UImageRebuildInner()
    {
        var rnd = new Random(37);
        FsTree tree = SmallTree(37, 8);
        // uInitrd: the archive keeps its gzip, the header gets the new size and checksums.
        byte[] initrd = BuildUImage(UbArm, UbRamdisk, UbGzip, "initramfs", Ramdisk(tree, ArchiveCompression.Gzip));
        string path = WriteFile("rebuild.uInitrd", initrd);
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
        {
            doc.Tree.CreateFile(doc.Tree.Root, "init", new MemoryFileData(Bytes(rnd, 5000, compressible: true)), 0x1ED);
            SaveOptions o = doc.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir });
            Check(o.Format == ImageFormat.Cpio && o.Cpio.Compression == ArchiveCompression.Gzip && doc.WritesFirmware(o), "uInitrd options");
            doc.Save(path, o).Document?.Dispose();
        }
        byte[] after = File.ReadAllBytes(path);
        uint size = CheckUImage(after, "uInitrd");
        Check(size == after.Length - 64 && after.AsSpan(8, 4).SequenceEqual(initrd.AsSpan(8, 4)) && after.AsSpan(16, 8).SequenceEqual(initrd.AsSpan(16, 8))
              && after.AsSpan(28, 36).SequenceEqual(initrd.AsSpan(28, 36)) && after[64] == 0x1F && after[65] == 0x8B, "uInitrd: header fields and gzip kept");
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
            Check(doc.Format == ImageFormat.Cpio && doc.Tree.Resolve("/init") is not null, "uInitrd: reopened");

        // Multi-file: kernel, gzipped ext2 ramdisk, device tree, and a SquashFS behind the image at a 64 KiB boundary.
        byte[] ext2 = WriteImage(tree, new SaveOptions { Format = ImageFormat.Ext, Ext = new ExtOptions { Kind = ExtKind.Ext2, BlockSize = 1024, Timestamp = 1700000000 } });
        var gz = new MemoryStream();
        using (var z = new GZipStream(gz, CompressionLevel.Fastest, leaveOpen: true)) z.Write(ext2);
        byte[] kernel = Bytes(rnd, 30001), dtb = new byte[999];
        BinaryPrimitives.WriteUInt32BigEndian(dtb, 0xD00DFEED);
        byte[] multi = PadTo(BuildUImage(UbArm, UbMulti, UbNone, "multi", MultiData(kernel, gz.ToArray(), dtb)), 65536, 0xFF);
        byte[] behind = Squash(SmallTree(38, 3));
        path = WriteFile("rebuild-multi.uImage", multi.Concat(behind).ToArray());
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false, volume: "ramdisk"))
        {
            Check(doc.Format == ImageFormat.Ext && doc.Firmware!.Image.Parts.Count == 4, "multi: parts " + string.Join("; ", doc.Firmware!.Image.Parts));
            doc.Tree.CreateFile(doc.Tree.Root, "added", new MemoryFileData(Bytes(rnd, 70000)));
            SaveOptions o = doc.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir });
            o.Ext.SizeBytes = 0;                // the ext2 ramdisk grows as needed
            doc.Save(path, o).Document?.Dispose();
        }
        after = File.ReadAllBytes(path);
        size = CheckUImage(after, "multi");
        uint k = BinaryPrimitives.ReadUInt32BigEndian(after.AsSpan(64)), r = BinaryPrimitives.ReadUInt32BigEndian(after.AsSpan(68)), d = BinaryPrimitives.ReadUInt32BigEndian(after.AsSpan(72));
        Check(k == kernel.Length && d == dtb.Length && BinaryPrimitives.ReadUInt32BigEndian(after.AsSpan(76)) == 0, "multi: list of sizes");
        int rdAt = 80 + (kernel.Length + 3) / 4 * 4, dtAt = rdAt + (int)(r + 3) / 4 * 4;
        Check(after.AsSpan(80, kernel.Length).SequenceEqual(kernel) && after.AsSpan(dtAt, dtb.Length).SequenceEqual(dtb) && 64 + size == dtAt + dtb.Length, "multi: kernel and device tree kept");
        Check(after[rdAt] == 0x1F && after[rdAt + 1] == 0x8B, "multi: the ramdisk is gzipped again");
        int behindAt = after.Length - behind.Length;
        Check(behindAt % 65536 == 0 && behindAt >= 64 + size && after.AsSpan(behindAt).SequenceEqual(behind), "multi: the filesystem behind the image moved off its boundary or changed");
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false, volume: "ramdisk"))
            Check(doc.Tree.Resolve("/added") is not null && doc.Firmware!.Part.Packed == ArchiveCompression.Gzip, "multi: reopened");
    }

    private static void UImageFuzz()
    {
        var rnd = new Random(35);
        FsTree tree = SmallTree(35, 4);
        byte[] multi = BuildUImage(UbArm, UbMulti, UbNone, "multi", MultiData(Bytes(rnd, 3000), Ramdisk(tree, ArchiveCompression.Gzip), Squash(tree)));
        byte[] sysupgrade = PadTo(BuildUImage(UbMips, UbKernel, UbLzma, "k", Bytes(rnd, 7000)), 4096, 0xFF).Concat(Squash(tree)).ToArray();
        string path = Path.Combine(_dir, "ufuzz.bin");
        int iterations = int.TryParse(Environment.GetEnvironmentVariable("FSFORGE_FUZZ"), out int custom) ? custom : 120;
        var crashes = new List<string>();
        foreach ((string name, byte[] good) in new[] { ("multi", multi), ("sysupgrade", sysupgrade) })
        {
            for (int i = 0; i < iterations; i++)
            {
                byte[] bad = (byte[])good.Clone();
                if (i % 4 == 3) bad = bad.AsSpan(0, rnd.Next(1, bad.Length)).ToArray();
                else
                    for (int k = 0; k < 1 + rnd.Next(i % 4 == 0 ? 1 : 10); k++)
                    {
                        // Mostly the header and the list of sizes, with the header checksum made right again.
                        int at = rnd.Next(2) == 0 ? 8 + rnd.Next(Math.Min(88, bad.Length - 8)) : rnd.Next(bad.Length);
                        bad[at] = i % 4 == 1 ? (byte)rnd.Next(256) : (byte)(bad[at] ^ (1 << rnd.Next(8)));
                    }
                if (bad.Length >= 64)
                {
                    bad.AsSpan(4, 4).Clear();
                    BinaryPrimitives.WriteUInt32BigEndian(bad.AsSpan(4), RefCrc32(bad[..64]));
                }
                File.WriteAllBytes(path, bad);
                try
                {
                    ImageDocument.Detect(path);
                    using ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
                    foreach (FsNode n in doc.Tree.Walk())
                        if (n.Inode.Data is { } d)
                        {
                            using Stream s = d.OpenRead();
                            s.CopyTo(Stream.Null);
                        }
                }
                catch (FsException ex) when (ex is not FsUnexpectedException)
                {
                }
                catch (Exception ex)
                {
                    if (crashes.Count < 3) crashes.Add($"{name} #{i}: {ex.GetType().Name}: {ex.Message}");
                    File.Copy(path, Path.Combine(_dir, $"crash-{name}-{i}.bin"), true);
                }
            }
        }
        Check(crashes.Count == 0, string.Join(" | ", crashes));
    }
}
