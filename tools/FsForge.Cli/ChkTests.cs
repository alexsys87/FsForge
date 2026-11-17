using System.Buffers.Binary;
using System.Text;
using FsForge.Core;
using FsForge.Core.Firmware;
using FsForge.Core.Images;

namespace FsForge.Cli;

/// <summary>
/// Netgear CHK images around something other than a TRX image, built here as OpenWrt's mkchkimg makes them,
/// independently of the reader: a kernel and a root filesystem part (-k, -f), and an OpenWrt sysupgrade image (a uImage
/// with the root filesystem and the 0xdeadc0de marker behind it) given whole as the kernel.
/// </summary>
internal static partial class SelfTest
{
    private static void ChkTests()
    {
        Test("Netgear CHK: kernel and root filesystem parts, sysupgrade image inside, checksums", ChkRead);
        Test("Netgear CHK: look-alikes refused, wrong checksums only read", ChkChecks);
        Test("Netgear CHK: rebuilt as mkchkimg makes it", ChkRebuild);
        Test("Netgear CHK: corrupted images are rejected cleanly", ChkFuzz);
    }

    /// <summary>
    /// The image as mkchkimg writes it: the 40-byte big-endian header (magic, header length, region and the version bytes
    /// 1, 1, 99, the checksums and lengths) and the board name, then the kernel and the root filesystem.
    /// </summary>
    private static byte[] BuildMkChkImg(byte[] kernel, byte[]? rootfs, string board = "U12H072T00_NETGEAR", byte region = 1)
    {
        byte[] name = Encoding.ASCII.GetBytes(board);
        byte[] h = new byte[40 + name.Length];
        BinaryPrimitives.WriteUInt32BigEndian(h, 0x2A23245E);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(4), (uint)h.Length);
        h[8] = region; h[9] = 1; h[10] = 1; h[11] = 99;
        byte[] fs = rootfs ?? Array.Empty<byte>();
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(16), NetgearSum(kernel));
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(20), rootfs is null ? 0 : NetgearSum(fs));
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(24), (uint)kernel.Length);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(28), (uint)fs.Length);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(32), NetgearSum(kernel.Concat(fs)));
        name.CopyTo(h, 40);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(36), NetgearSum(h));
        return h.Concat(kernel).Concat(fs).ToArray();
    }

    /// <summary>
    /// OpenWrt's sysupgrade image: the uImage and the root filesystem, padded by padjffs2 with its default sizes (4, 8, 16,
    /// 64, 128 and 256 KiB): 0xFF up to the next boundary of the smallest size left and the 0xdeadc0de marker there, after
    /// which every size that boundary is aligned to is done.
    /// </summary>
    private static byte[] Sysupgrade(byte[] uimage, byte[] rootfs)
    {
        var img = new List<byte>(uimage.Concat(rootfs));
        var sizes = new SortedSet<int> { 4 << 10, 8 << 10, 16 << 10, 64 << 10, 128 << 10, 256 << 10 };
        while (sizes.Count > 0)
        {
            int m = sizes.Min, at = (img.Count + m - 1) / m * m;
            img.AddRange(Enumerable.Repeat((byte)0xFF, at - img.Count));
            img.AddRange(new byte[] { 0xDE, 0xAD, 0xC0, 0xDE });
            sizes.RemoveWhere(x => at % x == 0);
        }
        return img.ToArray();
    }

    private static void ChkRead()
    {
        var rnd = new Random(71);
        FsTree tree = SmallTree(71, 6);
        string expected = Program.DumpToString(tree);
        byte[] kernel = BuildUImage(UbMips, UbKernel, UbLzma, "Linux", Bytes(rnd, 70001)), squash = Squash(tree);

        string path = WriteFile("chk-f.chk", BuildMkChkImg(kernel, squash, "R7800", 2));
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
        {
            Check(doc.Firmware?.Image is NetgearChkImage { Board: "R7800", Region: 2, ChkChecksumsValid: true, RootfsInKernel: false } c && c.Description.Contains("R7800"),
                "mkchkimg -k -f: " + doc.Firmware?.Image.Description);
            Check(doc.Firmware!.Image.Parts is [{ Name: "kernel", Offset: 45, ContentName: "uImage" }, { Name: "rootfs", Content: PartitionContent.SquashFs }]
                  && doc.Firmware.Part.Name == "rootfs" && doc.Firmware.Part.Offset == 45 + kernel.Length, "parts: " + string.Join("; ", doc.Firmware.Image.Parts));
            Check(!doc.SavesFilesystemOnly, "a CHK image is rebuilt when saved");
            Same(expected, Program.DumpToString(doc.Tree), "mkchkimg -f");
        }

        path = WriteFile("chk-sysupgrade.chk", BuildMkChkImg(Sysupgrade(kernel, squash), null));
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
        {
            Check(doc.Firmware?.Image is NetgearChkImage { ChkChecksumsValid: true, RootfsInKernel: true, RootfsPartLength: 0 }
                  && doc.Firmware.Part is { Name: "rootfs" } p && p.Offset == 58 + kernel.Length
                  && doc.Firmware.Image.Parts[0] is { Name: "kernel", ContentName: "uImage" } k && k.Length == kernel.Length,
                "sysupgrade inside: " + string.Join("; ", doc.Firmware?.Image.Parts ?? []));
            Same(expected, Program.DumpToString(doc.Tree), "sysupgrade inside the CHK");
        }

        // A CHK holding a TRX stays with the TRX reader.
        byte[] trx = BuildTrx(new[] { Bytes(rnd, 3000), squash }, 1, new[] { 4, 4096 });
        path = WriteFile("chk-trx.chk", BuildChk(trx, "U12H139T00_NETGEAR", sums: true));
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
            Check(doc.Firmware?.Image is TrxImage { ChkBoard: "U12H139T00_NETGEAR" }, "a CHK with a TRX: " + doc.Firmware?.Image.Kind);
    }

    private static void ChkChecks()
    {
        var rnd = new Random(72);
        FsTree tree = SmallTree(72, 4);
        byte[] kernel = BuildUImage(UbMips, UbKernel, UbLzma, "Linux", Bytes(rnd, 30000)), squash = Squash(tree);
        byte[] img = BuildMkChkImg(kernel, squash);
        int fsAt = 58 + kernel.Length;

        foreach ((string what, Action<byte[]> spoil) in new (string, Action<byte[]>)[]
                 {
                     ("kernel beyond the file", b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(24), (uint)b.Length)),
                     ("root filesystem beyond the file", b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(28), (uint)b.Length)),
                     ("no kernel", b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(24), 0)),
                     ("header shorter than its fields", b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), 30)),
                     ("board not text", b => b[42] = 1),
                 })
        {
            byte[] bad = (byte[])img.Clone();
            spoil(bad);
            string p = WriteFile("nochk.bin", bad);
            using ImageDocument doc = ImageDocument.Open(p, _dir, searchEmbedded: true);
            Check(doc.Firmware is null && doc.EmbeddedOffset == fsAt, what + ": not taken for a CHK image, the SquashFS found by the search");
        }

        // A changed byte: reported, and the image is only read.
        byte[] changed = (byte[])img.Clone();
        changed[200] ^= 1;
        string path = WriteFile("chk-bad.chk", changed);
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
            Check(doc.Firmware?.Image is NetgearChkImage { ChkChecksumsValid: false, ChecksumsValid: false } && doc.SavesFilesystemOnly
                  && doc.Warnings.Any(w => w.Contains("checksums of the Netgear CHK header")), "wrong checksums: only read");

        // Only the root filesystem is rebuilt; a CHK whose kernel holds no filesystem has nothing to open.
        path = WriteFile("chk-kernel.chk", BuildMkChkImg(kernel, null));
        try
        {
            using ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
            Check(false, "a CHK with a kernel alone was opened");
        }
        catch (FsException ex) when (ex.Message.Contains("Netgear CHK")) { }
    }

    private static void ChkRebuild()
    {
        var rnd = new Random(73);
        FsTree tree = SmallTree(73, 5);
        byte[] kernel = BuildUImage(UbMips, UbKernel, UbLzma, "Linux", Bytes(rnd, 50001)), squash = Squash(tree);
        var layouts = new (string Name, Func<byte[], byte[]> Make)[]
        {
            ("-k -f", root => BuildMkChkImg(kernel, root, "R7800", 2)),
            ("sysupgrade as -k", root => BuildMkChkImg(Sysupgrade(kernel, root), null, "R6220")),
        };
        foreach ((string name, Func<byte[], byte[]> make) in layouts)
        {
            string path = WriteFile("chk-rebuild.chk", make(squash));
            foreach (int size in new[] { 300000, 0 })
            {
                EditRootfs(path, size, rnd);
                byte[] after = File.ReadAllBytes(path);
                byte[] expected = make(SquashAt(after, kernel.Length));
                Check(after.AsSpan().SequenceEqual(expected), $"{name}, {(size > 0 ? "grown" : "shrunk")}: the file differs from what mkchkimg makes ({after.Length} / {expected.Length} bytes)");
                using ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
                Check(doc.Firmware?.Image is NetgearChkImage { ChkChecksumsValid: true } && (doc.Tree.Resolve("/big") is not null) == (size > 0), $"{name}: reads back with the checksums right");
            }
        }
    }

    private static void ChkFuzz()
    {
        var rnd = new Random(74);
        FsTree tree = SmallTree(74, 4);
        byte[] kernel = BuildUImage(UbMips, UbKernel, UbLzma, "Linux", Bytes(rnd, 9000)), squash = Squash(tree);
        string path = Path.Combine(_dir, "chkfuzz.bin");
        int iterations = int.TryParse(Environment.GetEnvironmentVariable("FSFORGE_FUZZ"), out int custom) ? custom : 120;
        var crashes = new List<string>();
        foreach ((string name, byte[] good) in new[] { ("-f", BuildMkChkImg(kernel, squash)), ("sysupgrade", BuildMkChkImg(Sysupgrade(kernel, squash), null)) })
        {
            for (int i = 0; i < iterations; i++)
            {
                byte[] bad = (byte[])good.Clone();
                if (i % 4 == 3) bad = bad.AsSpan(0, rnd.Next(1, bad.Length)).ToArray();
                else
                    for (int k = 0; k < 1 + rnd.Next(i % 4 == 0 ? 1 : 10); k++)
                    {
                        // Mostly the CHK and uImage headers.
                        int at = rnd.Next(3) > 0 ? rnd.Next(Math.Min(0x80, bad.Length)) : rnd.Next(bad.Length);
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
                    File.Copy(path, Path.Combine(_dir, $"crash-chk-{name}-{i}.bin"), true);
                }
            }
        }
        Check(crashes.Count == 0, string.Join(" | ", crashes));
    }
}
