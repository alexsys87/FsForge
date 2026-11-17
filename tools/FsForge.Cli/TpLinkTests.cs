using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FsForge.Core;
using FsForge.Core.Firmware;
using FsForge.Core.Images;

namespace FsForge.Cli;

/// <summary>
/// TP-Link firmware images, built here after OpenWrt's mktplinkfw (classic header, versions 1 and 2, the MD5 with the
/// key for normal images and for images with a bootloader) and tplink-safeloader (length, MD5 with its key, vendor
/// information, the fwup-ptn partition table, soft-version and support-list), independently of the reader. The root
/// filesystem must read as the image itself; a wrong MD5 is reported; files that only look alike are not taken.
/// TP-Link images are not rebuilt yet.
/// </summary>
internal static partial class SelfTest
{
    private static void TpLinkTests()
    {
        Test("TP-Link: classic header versions 1 and 2, bootloader, MD5", TpLinkClassic);
        Test("TP-Link: safeloader partition table, soft-version, support-list, MD5", TpLinkSafeloader);
        Test("TP-Link: look-alikes refused, the filesystem alone never over the image", TpLinkChecks);
        Test("TP-Link: rebuilt as mktplinkfw and tplink-safeloader make it", TpLinkRebuild);
        Test("TP-Link: corrupted images are rejected cleanly", TpLinkFuzz);
    }

    private static readonly byte[] TpSaltNormal = { 0xDC, 0xD7, 0x3A, 0xA5, 0xC3, 0x95, 0x98, 0xFB, 0xDD, 0xF9, 0xE7, 0xF4, 0x0E, 0xAE, 0x47, 0x38 };
    private static readonly byte[] TpSaltBoot = { 0x8C, 0xEF, 0x33, 0x5B, 0xD5, 0xC5, 0xCE, 0xFA, 0xA7, 0x9C, 0x28, 0xDA, 0xB2, 0xE9, 0x0F, 0x42 };
    private static readonly byte[] TpSaltSafeloader = { 0x7A, 0x2B, 0x15, 0xED, 0x9B, 0x98, 0x59, 0x6D, 0xE5, 0x04, 0xAB, 0x44, 0xAC, 0x2A, 0x9F, 0x4E };

    /// <summary>
    /// A classic TP-Link image as mktplinkfw builds it: a buffer of the flash layout size filled with 0xFF, the 512-byte
    /// header, the kernel at 0x200, the root filesystem at its offset (none for a combined image), cut behind the root
    /// filesystem with <paramref name="strip"/> (-s); fw_length is the layout size; the MD5 covers the file with the key
    /// in place of the sum. Version 2 uses the field places of mktplinkfw2.
    /// </summary>
    private static byte[] BuildTpLinkClassic(int version, byte[] kernel, byte[]? rootfs, byte[]? boot = null, int rootfsOffset = 0x40000,
        int layoutSize = 0x100000, bool strip = true, bool jffs2 = false)
    {
        byte[] buf = new byte[layoutSize];
        buf.AsSpan().Fill(0xFF);
        buf.AsSpan(0, 0x200).Clear();
        kernel.CopyTo(buf, 0x200);
        int length = 0x200 + kernel.Length, bootOffset = 0;
        if (rootfs is not null)
        {
            rootfs.CopyTo(buf, rootfsOffset);
            length = rootfsOffset + rootfs.Length;
            if (jffs2)
            {
                // pad_jffs2 with its 64 KiB mask: one marker at the next 64 KiB of the file.
                length = (length + 0xFFFF) & ~0xFFFF;
                new byte[] { 0xDE, 0xAD, 0xC0, 0xDE }.CopyTo(buf, length);
                length += 4;
            }
        }
        if (boot is not null)
        {
            bootOffset = length;
            boot.CopyTo(buf, bootOffset);
            length += boot.Length;
        }
        byte[] img = strip ? buf[..length] : buf;
        int hw, md5, lengths;
        if (version == 1)
        {
            Encoding.ASCII.GetBytes("TP-LINK Technologies").CopyTo(img, 4);
            Encoding.ASCII.GetBytes("ver. 1.0").CopyTo(img, 28);
            (hw, md5, lengths) = (64, 76, 116);
        }
        else
        {
            Encoding.ASCII.GetBytes("ver. 2.0.0.1").CopyTo(img, 4);
            (hw, md5, lengths) = (52, 64, 104);
        }
        BinaryPrimitives.WriteUInt32BigEndian(img, (uint)version << 24);
        BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(hw), 0x08410001);
        BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(hw + 4), 5);
        uint[] fields = { 0x80060000, 0x80060000, (uint)layoutSize, 0x200, (uint)kernel.Length, rootfs is null ? 0u : (uint)rootfsOffset,
            (uint)(rootfs?.Length ?? 0), (uint)bootOffset, (uint)(boot?.Length ?? 0) };
        for (int i = 0; i < fields.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(lengths + 4 * i), fields[i]);
        (boot is null ? TpSaltNormal : TpSaltBoot).CopyTo(img, md5);
        MD5.HashData(img).CopyTo(img, md5);
        return img;
    }

    /// <summary>
    /// A safeloader factory image as tplink-safeloader builds it: the size and the MD5 of the key and everything from
    /// 0x14, the vendor information (length and text, 0xFF up to 0x1014), then the payload: the table of
    /// "fwup-ptn" lines ended by a zero byte in 0x800 bytes, and the partitions one after another from base 0x800 on
    /// (partition-table first; soft-version, support-list and extra-para behind a length and a zero word).
    /// </summary>
    private static byte[] BuildTpLinkSafeloader(byte[] kernel, byte[] rootfs, string product = "Archer C7", bool jffs2 = false)
    {
        byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
        byte[] Meta(byte[] data)
        {
            byte[] b = new byte[8 + data.Length];
            BinaryPrimitives.WriteUInt32BigEndian(b, (uint)data.Length);
            data.CopyTo(b, 8);
            return b;
        }
        byte[] flash = new byte[0x800];
        flash.AsSpan().Fill(0xFF);
        byte[] flashText = Ascii("partition fs-uboot base 0x00000 size 0x20000\npartition os-image base 0x20000 size 0x180000\n" +
                                 "partition file-system base 0x1a0000 size 0x620000\n\0");
        if (jffs2)
        {
            // read_file with -j: up to the next 64 KiB of the flash address of file-system (0x1a0000), then the marker.
            int padded = ((rootfs.Length + 0x1a0000 + 0xFFFF) & ~0xFFFF) - 0x1a0000;
            byte[] withMarker = new byte[padded + 4];
            withMarker.AsSpan().Fill(0xFF);
            rootfs.CopyTo(withMarker, 0);
            new byte[] { 0xDE, 0xAD, 0xC0, 0xDE }.CopyTo(withMarker, padded);
            rootfs = withMarker;
        }
        new byte[] { 0, 4, 0, 0 }.Concat(flashText).ToArray().CopyTo(flash, 0);
        var parts = new (string Name, byte[] Data)[]
        {
            ("partition-table", flash),
            ("soft-version", Meta(Ascii("soft_ver:7.0.0\n\0"))),
            ("support-list", Meta(Ascii($"SupportList:\r\n{{product_name:{product},product_ver:5.0.0,special_id:00000000}}\r\n"))),
            ("os-image", kernel),
            ("file-system", rootfs),
            ("extra-para", Meta(new byte[] { 1, 0 })),
        };
        byte[] table = new byte[0x800];
        table.AsSpan().Fill(0xFF);
        var lines = new StringBuilder();
        var data = new List<byte>();
        int baseOffset = 0x800;
        foreach ((string name, byte[] blob) in parts)
        {
            lines.Append($"fwup-ptn {name} base 0x{baseOffset:x5} size 0x{blob.Length:x5}\t\r\n");
            data.AddRange(blob);
            baseOffset += blob.Length;
        }
        byte[] tableText = Ascii(lines.ToString() + "\0");
        tableText.CopyTo(table, 0);
        byte[] header = new byte[0x1000];
        header.AsSpan().Fill(0xFF);
        byte[] vendorText = Ascii("vendor_name:TP-Link\nvendor_id:0\n");
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)vendorText.Length);
        vendorText.CopyTo(header, 4);
        byte[] img = new byte[0x14].Concat(header).Concat(table).Concat(data).ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(img, (uint)img.Length);
        MD5.HashData(TpSaltSafeloader.Concat(img[0x14..]).ToArray()).CopyTo(img, 4);
        return img;
    }

    private static void TpLinkClassic()
    {
        var rnd = new Random(61);
        FsTree tree = SmallTree(61, 8);
        string expected = Program.DumpToString(tree);
        byte[] kernel = BuildUImage(UbMips, UbKernel, UbLzma, "Linux", Bytes(rnd, 100000)), squash = Squash(tree);

        string path = WriteFile("tp-v1.bin", BuildTpLinkClassic(1, kernel, squash));
        Check(ImageDocument.Detect(path) == ImageFormat.SquashFs, "detected as " + ImageDocument.Detect(path));
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
        {
            Check(doc.Firmware?.Image is TpLinkImage { Safeloader: false, HeaderVersion: 1, Vendor: "TP-LINK Technologies", FirmwareVersion: "ver. 1.0", HardwareId: 0x08410001, HardwareRevision: 5, Md5Matches: true },
                "version 1: header");
            Check(doc.Firmware!.Image.Parts is [{ Name: "kernel", Offset: 0x200, ContentName: "uImage" }, { Name: "rootfs", Offset: 0x40000, Content: PartitionContent.SquashFs }]
                  && doc.Firmware.Part.Name == "rootfs", "version 1: parts " + string.Join("; ", doc.Firmware.Image.Parts));
            Check(!doc.SavesFilesystemOnly, "a TP-Link image is rebuilt when saved");
            Same(expected, Program.DumpToString(doc.Tree), "version 1");
        }

        byte[] boot = Bytes(rnd, 5000);
        path = WriteFile("tp-v2.bin", BuildTpLinkClassic(2, kernel, squash, boot));
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
        {
            Check(doc.Firmware?.Image is TpLinkImage { HeaderVersion: 2, FirmwareVersion: "ver. 2.0.0.1", Md5Matches: true } && doc.Firmware.Image.Parts is [{ Name: "kernel" }, { Name: "rootfs" }, { Name: "bootloader", Length: 5000 }],
                "version 2 with a bootloader: " + string.Join("; ", doc.Firmware?.Image.Parts ?? []));
            Same(expected, Program.DumpToString(doc.Tree), "version 2");
        }

        // Padded to the flash layout (no -s), and combined: the root filesystem inside the kernel blob (rootfs_ofs 0).
        path = WriteFile("tp-full.bin", BuildTpLinkClassic(1, kernel, squash, strip: false));
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
            Check(doc.Firmware?.Image is TpLinkImage { Md5Matches: true } && doc.Firmware.Part.Offset == 0x40000 && Program.DumpToString(doc.Tree) == expected, "padded to the layout");
        path = WriteFile("tp-combined.bin", BuildTpLinkClassic(1, kernel.Concat(squash).ToArray(), null));
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
        {
            Check(doc.Firmware?.Image is TpLinkImage { Md5Matches: true } && doc.Firmware.Part is { Name: "rootfs", Offset: var o } && o == 0x200 + kernel.Length,
                "combined: " + string.Join("; ", doc.Firmware?.Image.Parts ?? []));
            Same(expected, Program.DumpToString(doc.Tree), "combined");
        }

        byte[] changed = BuildTpLinkClassic(1, kernel, squash);
        changed[^30] ^= 1;
        path = WriteFile("tp-changed.bin", changed);
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
            Check(doc.Firmware?.Image is TpLinkImage { Md5Matches: false, ChecksumsValid: false } && doc.Warnings.Any(w => w.Contains("MD5 sum in the TP-Link header")), "a wrong MD5 is reported");
    }

    private static void TpLinkSafeloader()
    {
        var rnd = new Random(62);
        FsTree tree = SmallTree(62, 7);
        byte[] kernel = BuildUImage(UbArm, UbKernel, UbLzma, "Linux", Bytes(rnd, 60000)), squash = Squash(tree);
        string path = WriteFile("tp-safeloader.bin", BuildTpLinkSafeloader(kernel, squash));
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
        {
            Check(doc.Firmware?.Image is TpLinkImage { Safeloader: true, Md5Matches: true, FirmwareVersion: "soft_ver:7.0.0" } t && t.SupportList.Contains("product_name:Archer C7")
                  && t.Vendor.StartsWith("vendor_name:TP-Link"), "safeloader: header and lists");
            // The bases count from 0x1014; the partitions follow one another from 0x800 on.
            int fsAt = 0x1014 + 0x800 + 0x800 + (8 + 16) + (8 + 78) + kernel.Length;   // table, soft-version, support-list, os-image
            Check(doc.Firmware!.Image.Parts is [{ Name: "partition-table", Offset: 0x1814 }, { Name: "soft-version" }, { Name: "support-list" }, { Name: "os-image", ContentName: "uImage" },
                      { Name: "file-system", Content: PartitionContent.SquashFs }, { Name: "extra-para" }]
                  && doc.Firmware.Part is { Name: "file-system", Offset: var at } && at == fsAt,
                "safeloader: parts " + string.Join("; ", doc.Firmware.Image.Parts));
            Same(Program.DumpToString(tree), Program.DumpToString(doc.Tree), "safeloader");
        }
        ExpectFs(() => ImageDocument.Open(path, _dir, searchEmbedded: false, volume: "os-image").Dispose());

        byte[] changed = BuildTpLinkSafeloader(kernel, squash);
        changed[^30] ^= 1;
        path = WriteFile("tp-safeloader-changed.bin", changed);
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
            Check(doc.Firmware?.Image is TpLinkImage { Md5Matches: false } && doc.Warnings.Any(w => w.Contains("safeloader image does not match")), "safeloader: a wrong MD5 is reported");
    }

    private static void TpLinkChecks()
    {
        var rnd = new Random(63);
        FsTree tree = SmallTree(63, 4);
        byte[] kernel = Bytes(rnd, 30000), squash = Squash(tree);
        byte[] img = BuildTpLinkClassic(2, kernel, squash);

        // A header whose lengths do not fit the file, or that starts like one without anything behind it, is no TP-Link image.
        foreach ((string what, Action<byte[]> spoil) in new (string, Action<byte[]>)[]
                 {
                     // (fw_length beyond the file is normal: it is the size of the flash layout, and -s cuts the file.)
                     ("kernel beyond the file", b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(120), (uint)b.Length)),
                     ("kernel not behind the header", b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(116), 0x400)),
                     ("root filesystem beyond the firmware", b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(124), (uint)b.Length)),
                     ("unknown version", b => BinaryPrimitives.WriteUInt32BigEndian(b, 0x04000000)),
                 })
        {
            byte[] bad = (byte[])img.Clone();
            spoil(bad);
            string p = WriteFile("notp.bin", bad);
            using ImageDocument doc = ImageDocument.Open(p, _dir, searchEmbedded: true);
            Check(doc.Firmware is null && doc.EmbeddedOffset == 0x40000, what + ": not taken for a TP-Link image, the SquashFS found by the search");
        }
        string random = WriteFile("notp-random.bin", new byte[] { 1, 0, 0, 0 }.Concat(Bytes(rnd, 300000)).ToArray());
        ExpectFs(() => ImageDocument.Open(random, _dir, searchEmbedded: false).Dispose());

        // The filesystem alone (or a tar archive) is never written over the TP-Link image.
        string path = WriteFile("tp-save.bin", img);
        using ImageDocument d = ImageDocument.Open(path, _dir, searchEmbedded: false);
        d.Tree.Remove(d.Tree.Resolve("/file0")!);
        SaveOptions o = d.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir, KeepContainer = false });
        Check(!d.WritesContainer(o) && !d.WritesFirmware(o), "no TP-Link image is written");
        ExpectFs(() => d.Save(path, o));
        ExpectFs(() => d.Save(path, d.CreateSaveOptions(ImageFormat.Tar, new SaveOptions { TempDirectory = _dir })));
        Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(img), "the TP-Link image was changed");
        string alone = Path.Combine(_dir, "tp-alone.sqfs");
        d.Save(alone, o).Document?.Dispose();
        using ImageDocument back = ImageDocument.Open(alone, _dir, searchEmbedded: false);
        Check(back.Firmware is null && back.Tree.Resolve("/file0") is null && back.Tree.Resolve("/file2") is not null, "saved alone");
    }

    private static void TpLinkRebuild()
    {
        var rnd = new Random(65);
        FsTree tree = SmallTree(65, 5);
        byte[] kernel = BuildUImage(UbMips, UbKernel, UbLzma, "Linux", Bytes(rnd, 50001)), squash = Squash(tree);
        // Each layout as the tool makes it; after the change, the file must be what the tool makes from the new filesystem.
        var layouts = new (string Name, Func<byte[], byte[]> Make)[]
        {
            ("classic -s", root => BuildTpLinkClassic(1, kernel, root, layoutSize: 0x200000)),
            ("classic padded", root => BuildTpLinkClassic(1, kernel, root, layoutSize: 0x200000, strip: false)),
            ("classic -j", root => BuildTpLinkClassic(1, kernel, root, layoutSize: 0x200000, jffs2: true)),
            ("classic -c", root => BuildTpLinkClassic(1, kernel.Concat(root).ToArray(), null, layoutSize: 0x200000)),
            ("version 2", root => BuildTpLinkClassic(2, kernel, root, layoutSize: 0x200000)),
            ("safeloader", root => BuildTpLinkSafeloader(kernel, root)),
            ("safeloader -j", root => BuildTpLinkSafeloader(kernel, root, jffs2: true)),
        };
        foreach ((string name, Func<byte[], byte[]> make) in layouts)
        {
            string path = WriteFile("tp-rebuild.bin", make(squash));
            foreach (int size in new[] { 300000, 0 })
            {
                EditRootfs(path, size, rnd);
                byte[] after = File.ReadAllBytes(path);
                byte[] expected = make(SquashAt(after, 0x200));
                Check(after.AsSpan().SequenceEqual(expected), $"{name}, {(size > 0 ? "grown" : "shrunk")}: the file differs from what the tool makes ({after.Length} / {expected.Length} bytes)");
                using ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
                Check(doc.Firmware?.Image is TpLinkImage { Md5Matches: true } && (doc.Tree.Resolve("/big") is not null) == (size > 0), $"{name}: reads back with the MD5 right");
            }
        }

        // The filesystem no longer fits the flash layout: refused, the file unchanged.
        string full = WriteFile("tp-toobig.bin", BuildTpLinkClassic(1, kernel, squash, layoutSize: 0x50000));
        byte[] before = File.ReadAllBytes(full);
        using (ImageDocument doc = ImageDocument.Open(full, _dir, searchEmbedded: false))
        {
            doc.Tree.CreateFile(doc.Tree.Root, "big", new MemoryFileData(Bytes(rnd, 300000)));
            ExpectFs(() => doc.Save(full, doc.CreateSaveOptions(null, new SaveOptions { TempDirectory = _dir })));
        }
        Check(File.ReadAllBytes(full).AsSpan().SequenceEqual(before), "a firmware too big for the layout was written");

        // A bootloader in the image, or an MD5 made with another key: only read.
        string boot = WriteFile("tp-boot.bin", BuildTpLinkClassic(1, kernel, squash, Bytes(rnd, 4000)));
        using (ImageDocument doc = ImageDocument.Open(boot, _dir, searchEmbedded: false))
            Check(doc.SavesFilesystemOnly && doc.Warnings.Any(w => w.Contains("bootloader")), "an image with a bootloader is only read");
        byte[] other = BuildTpLinkClassic(1, kernel, squash);
        other[^20] ^= 1;
        string otherPath = WriteFile("tp-otherkey.bin", other);
        using (ImageDocument doc = ImageDocument.Open(otherPath, _dir, searchEmbedded: false))
            Check(doc.SavesFilesystemOnly, "an image whose MD5 does not match is only read");
    }

    private static void TpLinkFuzz()
    {
        var rnd = new Random(64);
        FsTree tree = SmallTree(64, 4);
        byte[] classic = BuildTpLinkClassic(1, Bytes(rnd, 9000), Squash(tree), rootfsOffset: 0x4000);
        byte[] safeloader = BuildTpLinkSafeloader(Bytes(rnd, 9000), Squash(tree));
        string path = Path.Combine(_dir, "tpfuzz.bin");
        int iterations = int.TryParse(Environment.GetEnvironmentVariable("FSFORGE_FUZZ"), out int custom) ? custom : 120;
        var crashes = new List<string>();
        foreach ((string name, byte[] good, int head) in new[] { ("classic", classic, 0x200), ("safeloader", safeloader, 0x1900) })
        {
            for (int i = 0; i < iterations; i++)
            {
                byte[] bad = (byte[])good.Clone();
                if (i % 4 == 3) bad = bad.AsSpan(0, rnd.Next(1, bad.Length)).ToArray();
                else
                    for (int k = 0; k < 1 + rnd.Next(i % 4 == 0 ? 1 : 10); k++)
                    {
                        // Mostly the headers and the partition table.
                        int at = rnd.Next(3) > 0 ? rnd.Next(Math.Min(head, bad.Length)) : rnd.Next(bad.Length);
                        bad[at] = i % 4 == 1 ? (byte)rnd.Next(256) : (byte)(bad[at] ^ (1 << rnd.Next(8)));
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
                    File.Copy(path, Path.Combine(_dir, $"crash-tp-{name}-{i}.bin"), true);
                }
            }
        }
        Check(crashes.Count == 0, string.Join(" | ", crashes));
    }
}
