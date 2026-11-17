using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FsForge.Core;
using FsForge.Core.Firmware;
using FsForge.Core.Images;
using FsForge.Core.Tar;

namespace FsForge.Cli;

/// <summary>
/// U-Boot FIT images (.itb, image.ub). The device trees are built here from the devicetree specification (header,
/// memory reservation block, structure and strings blocks), independently of the parser under test, the way
/// mkimage lays out a FIT: data in the tree, behind it (-E, data-offset) or at a fixed position (-p,
/// data-position), with crc32 and sha256 hashes; a PetaLinux image.ub with its ramdisk wrapped in a uImage; an
/// OpenWrt image with the root filesystem behind the FIT. Each is opened at its filesystem, which must read as the
/// image itself; FIT images are not rebuilt yet, so the filesystem is only saved alone.
/// </summary>
internal static partial class SelfTest
{
    private static void FitTests()
    {
        Test("FIT: images in the tree, hashes, choice of image", FitEmbedded);
        Test("FIT: external data (data-offset, data-position), image.ub, root filesystem behind", FitLayouts);
        Test("FIT: wrong hash, signature, plain DTB, never saved over", FitChecks);
        Test("FIT: corrupted images are rejected cleanly", FitFuzz);
    }

    private sealed class DtNode(string name)
    {
        public string Name { get; } = name;
        public List<(string Name, byte[] Value)> Props { get; } = new();
        public List<DtNode> Children { get; } = new();

        public DtNode Prop(string name, string value) { Props.Add((name, Encoding.ASCII.GetBytes(value + "\0"))); return this; }
        public DtNode Prop(string name, uint value) { byte[] v = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(v, value); Props.Add((name, v)); return this; }
        public DtNode Prop(string name, byte[] value) { Props.Add((name, value)); return this; }
        public DtNode Add(DtNode child) { Children.Add(child); return this; }
    }

    /// <summary>A flattened device tree, version 17, as dtc writes it: header, empty reservation map, structure, strings.</summary>
    private static byte[] BuildFdt(DtNode root)
    {
        var structure = new MemoryStream();
        var strings = new MemoryStream();
        var names = new Dictionary<string, int>();
        void Word(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); structure.Write(b); }
        void Pad() { while (structure.Length % 4 != 0) structure.WriteByte(0); }
        void Node(DtNode n)
        {
            Word(1);
            structure.Write(Encoding.ASCII.GetBytes(n.Name + "\0"));
            Pad();
            foreach ((string name, byte[] value) in n.Props)
            {
                if (!names.TryGetValue(name, out int off))
                {
                    off = (int)strings.Length;
                    names[name] = off;
                    strings.Write(Encoding.ASCII.GetBytes(name + "\0"));
                }
                Word(3);
                Word((uint)value.Length);
                Word((uint)off);
                structure.Write(value);
                Pad();
            }
            foreach (DtNode c in n.Children) Node(c);
            Word(2);
        }
        Node(root);
        Word(9);
        const int header = 40, rsvmap = 16;
        int offStruct = header + rsvmap, offStrings = offStruct + (int)structure.Length;
        int total = offStrings + (int)strings.Length;
        byte[] dtb = new byte[total];
        void H(int at, uint v) => BinaryPrimitives.WriteUInt32BigEndian(dtb.AsSpan(at), v);
        H(0, 0xD00DFEED); H(4, (uint)total); H(8, (uint)offStruct); H(12, (uint)offStrings); H(16, header);
        H(20, 17); H(24, 16); H(28, 0); H(32, (uint)strings.Length); H(36, (uint)structure.Length);
        structure.ToArray().CopyTo(dtb, offStruct);
        strings.ToArray().CopyTo(dtb, offStrings);
        return dtb;
    }

    private static byte[] Crc32Value(byte[] data)
    {
        byte[] v = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(v, RefCrc32(data));
        return v;
    }

    private enum FitData { Embedded, Offset, Position }

    /// <summary>A FIT image with the given images (name, type, data, hash algorithm) as mkimage lays it out.</summary>
    private static byte[] BuildFit(string description, (string Name, string Type, byte[] Data, string? Hash)[] images, FitData layout, bool signed = false)
    {
        // First pass to learn the size of the tree with external data; the sizes of offsets do not change it.
        byte[] Tree(long[] offsets)
        {
            var imagesNode = new DtNode("images");
            for (int i = 0; i < images.Length; i++)
            {
                (string name, string type, byte[] data, string? hash) = images[i];
                var n = new DtNode(name).Prop("description", name).Prop("type", type).Prop("arch", "arm").Prop("compression", type == "kernel" ? "gzip" : "none");
                if (layout == FitData.Embedded) n.Prop("data", data);
                else n.Prop(layout == FitData.Offset ? "data-offset" : "data-position", (uint)offsets[i]).Prop("data-size", (uint)data.Length);
                if (hash is not null)
                    n.Add(new DtNode("hash-1").Prop("algo", hash).Prop("value", hash == "crc32" ? Crc32Value(data) : SHA256.HashData(data)));
                if (signed) n.Add(new DtNode("signature-1").Prop("algo", "sha256,rsa2048").Prop("key-name-hint", "dev").Prop("value", new byte[256]));
                imagesNode.Add(n);
            }
            var conf = new DtNode("configurations").Prop("default", "conf-1")
                .Add(new DtNode("conf-1").Prop("description", "board").Prop("kernel", images[0].Name));
            return BuildFdt(new DtNode("").Prop("description", description).Prop("#address-cells", 1u).Add(imagesNode).Add(conf));
        }
        long[] offs = new long[images.Length];
        if (layout == FitData.Embedded) return Tree(offs);
        int treeSize = Tree(offs).Length;
        long position = layout == FitData.Offset ? 0 : 0x1000;
        long dataStart = layout == FitData.Offset ? (treeSize + 3) & ~3 : position;
        long at = 0;
        for (int i = 0; i < images.Length; i++)
        {
            offs[i] = (layout == FitData.Offset ? 0 : position) + at;
            at += (images[i].Data.Length + 3) & ~3;
        }
        byte[] tree = Tree(offs);
        byte[] fit = new byte[dataStart + at];
        tree.CopyTo(fit, 0);
        long pos = dataStart;
        foreach (var img in images)
        {
            img.Data.CopyTo(fit, pos);
            pos += (img.Data.Length + 3) & ~3;
        }
        return fit;
    }

    private static byte[] Gzipped(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    private static void FitEmbedded()
    {
        var rnd = new Random(41);
        FsTree tree = SmallTree(41, 10);
        byte[] kernel = Gzipped(Bytes(rnd, 120000)), dtb = BuildFdt(new DtNode("").Prop("model", "board")), cpio = Ramdisk(tree, ArchiveCompression.Gzip), squash = Squash(tree);
        byte[] fit = BuildFit("OpenWrt FIT", new[] { ("kernel-1", "kernel", kernel, "crc32"), ("fdt-1", "flat_dt", dtb, "sha256"), ("ramdisk-1", "ramdisk", cpio, "sha256"), ("rootfs-1", "filesystem", squash, (string?)"crc32") }, FitData.Embedded);
        string path = WriteFile("embedded.itb", fit);
        Check(ImageDocument.Detect(path) is ImageFormat.Cpio or ImageFormat.SquashFs, "detected as " + ImageDocument.Detect(path));
        string expected = Program.DumpToString(tree);
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false, volume: "rootfs-1"))
        {
            Check(doc.Format == ImageFormat.SquashFs && doc.Firmware?.Image is FitImage { Name: "OpenWrt FIT", DefaultConfiguration: "conf-1", HashesChecked: 4, HashesWrong: 0, Signed: false, ExternalData: false },
                "opened at rootfs-1: " + string.Join("; ", doc.Firmware?.Image.Parts.Select(p => p.ToString()) ?? []));
            Check(doc.Firmware!.Image.Parts is [{ Name: "kernel-1", IsReadable: false, ContentName: "kernel (gzip)" }, { Name: "fdt-1", ContentName: "device tree" },
                { Name: "ramdisk-1", Content: PartitionContent.Cpio }, { Name: "rootfs-1", Content: PartitionContent.SquashFs }], "parts");
            Check(doc.SavesFilesystemOnly && doc.Warnings.Any(w => w.Contains("FIT image is not supported yet")), "FIT images are only read");
            Same(expected, Program.DumpToString(doc.Tree), "rootfs-1");
        }
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false, volume: "ramdisk-1"))
        {
            Check(doc.Format == ImageFormat.Cpio && doc.Firmware?.Part.Name == "ramdisk-1", "opened at ramdisk-1");
            Same(expected, Program.DumpToString(doc.Tree), "ramdisk-1");
        }
        ExpectFs(() => ImageDocument.Open(path, _dir, searchEmbedded: false, volume: "fdt-1").Dispose());
        ExpectFs(() => ImageDocument.Open(path, _dir, searchEmbedded: false, volume: "kernel-1").Dispose());
        int asked = 0;
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false, chooseFirmwarePart: img => { asked++; return img.Find("rootfs-1"); }))
            Check(asked == 1 && doc.Firmware?.Part.Name == "rootfs-1", "chosen by the caller");
    }

    private static void FitLayouts()
    {
        var rnd = new Random(42);
        FsTree tree = SmallTree(42, 8);
        string expected = Program.DumpToString(tree);
        byte[] kernel = Gzipped(Bytes(rnd, 50001)), squash = Squash(tree);
        foreach (FitData layout in new[] { FitData.Offset, FitData.Position })
        {
            byte[] fit = BuildFit("external", new[] { ("kernel-1", "kernel", kernel, "crc32"), ("rootfs-1", "filesystem", squash, (string?)"sha256") }, layout);
            string path = WriteFile($"external-{layout}.itb", fit);
            using ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
            Check(doc.Format == ImageFormat.SquashFs && doc.Firmware?.Image is FitImage { ExternalData: true, HashesChecked: 2, HashesWrong: 0 } && doc.Firmware.Part.Name == "rootfs-1",
                $"{layout}: opened");
            Same(expected, Program.DumpToString(doc.Tree), layout.ToString());
        }

        // image.ub from PetaLinux: the ramdisk is a uImage around a gzipped cpio archive.
        byte[] wrapped = BuildUImage(UbArm, UbRamdisk, UbGzip, "petalinux-image-minimal", Ramdisk(tree, ArchiveCompression.Gzip));
        byte[] ub = BuildFit("PetaLinux", new[] { ("kernel@1", "kernel", kernel, "sha256"), ("ramdisk@1", "ramdisk", wrapped, (string?)"sha256") }, FitData.Embedded);
        string ubPath = WriteFile("image.ub", ub);
        using (ImageDocument doc = ImageDocument.Open(ubPath, _dir, searchEmbedded: false))
        {
            Check(doc.Format == ImageFormat.Cpio && doc.Firmware?.Part is { Name: "ramdisk@1" } p && p.Detail.Contains("inside a uImage 'petalinux-image-minimal'"), "image.ub: the ramdisk inside the uImage");
            Same(expected, Program.DumpToString(doc.Tree), "image.ub");
        }

        // OpenWrt: kernel and device tree in the FIT, the SquashFS behind it at the next 64 KiB.
        byte[] small = BuildFit("ARM64 OpenWrt FIT", new[] { ("kernel-1", "kernel", kernel, (string?)"crc32") }, FitData.Embedded);
        string owrt = WriteFile("openwrt-sysupgrade.itb", PadTo(small, 65536, 0xFF).Concat(squash).ToArray());
        using (ImageDocument doc = ImageDocument.Open(owrt, _dir, searchEmbedded: false))
        {
            Check(doc.Firmware?.Part is { Name: "rootfs", BehindImage: true, Offset: 65536 } && doc.Format == ImageFormat.SquashFs, "the root filesystem behind the FIT");
            Same(expected, Program.DumpToString(doc.Tree), "behind the FIT");
        }
    }

    private static void FitChecks()
    {
        FsTree tree = SmallTree(43, 5);
        byte[] squash = Squash(tree);
        byte[] fit = BuildFit("checks", new[] { ("rootfs-1", "filesystem", squash, (string?)"sha256") }, FitData.Embedded, signed: true);
        string path = WriteFile("checks.itb", fit);
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
            Check(doc.Firmware?.Image is FitImage { Signed: true, HashesWrong: 0 }, "signature nodes are noticed");

        // A changed byte of the data: opened, with a warning.
        int at = fit.AsSpan().IndexOf("hsqs"u8);
        byte[] changed = (byte[])fit.Clone();
        changed[at + squash.Length - 10] ^= 1;
        path = WriteFile("changed.itb", changed);
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
            Check(doc.Firmware?.Image is FitImage { HashesWrong: 1 } && doc.Warnings.Any(w => w.Contains("sha256 hash of image 'rootfs-1'")), "a wrong hash is reported");

        // A plain device tree is not a FIT image.
        path = WriteFile("plain.dtb", BuildFdt(new DtNode("").Prop("model", "board").Add(new DtNode("cpus"))));
        ExpectFs(() => ImageDocument.Open(path, _dir, searchEmbedded: false).Dispose());

        // Saving: the filesystem alone, never over the FIT image.
        path = WriteFile("save.itb", fit);
        using ImageDocument d = ImageDocument.Open(path, _dir, searchEmbedded: false);
        d.Tree.Remove(d.Tree.Resolve("/file0")!);
        SaveOptions o = d.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir });
        Check(!d.WritesContainer(o) && !d.WritesFirmware(o), "no FIT image is written");
        ExpectFs(() => d.Save(path, o));
        Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(fit), "the FIT image was changed");
        string alone = Path.Combine(_dir, "fit-alone.sqfs");
        d.Save(alone, o).Document?.Dispose();
        using ImageDocument back = ImageDocument.Open(alone, _dir, searchEmbedded: false);
        Check(back.Firmware is null && back.Tree.Resolve("/file0") is null && back.Tree.Resolve("/file2") is not null, "saved alone");
    }

    private static void FitFuzz()
    {
        var rnd = new Random(44);
        FsTree tree = SmallTree(44, 4);
        byte[] embedded = BuildFit("fuzz", new[] { ("kernel-1", "kernel", Gzipped(Bytes(rnd, 3000)), "crc32"), ("ramdisk-1", "ramdisk", Ramdisk(tree, ArchiveCompression.Gzip), (string?)"sha256") }, FitData.Embedded);
        byte[] external = BuildFit("fuzz", new[] { ("kernel-1", "kernel", Gzipped(Bytes(rnd, 3000)), "crc32"), ("rootfs-1", "filesystem", Squash(tree), (string?)"crc32") }, FitData.Offset);
        string path = Path.Combine(_dir, "fitfuzz.itb");
        int iterations = int.TryParse(Environment.GetEnvironmentVariable("FSFORGE_FUZZ"), out int custom) ? custom : 120;
        var crashes = new List<string>();
        foreach ((string name, byte[] good) in new[] { ("embedded", embedded), ("external", external) })
        {
            int treeSize = (int)BinaryPrimitives.ReadUInt32BigEndian(good.AsSpan(4));
            for (int i = 0; i < iterations; i++)
            {
                byte[] bad = (byte[])good.Clone();
                if (i % 4 == 3) bad = bad.AsSpan(0, rnd.Next(1, bad.Length)).ToArray();
                else
                    for (int k = 0; k < 1 + rnd.Next(i % 4 == 0 ? 1 : 10); k++)
                    {
                        // Mostly the tree itself: header, tokens, lengths, name offsets.
                        int at = rnd.Next(3) > 0 ? rnd.Next(Math.Min(treeSize, bad.Length)) : rnd.Next(bad.Length);
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
                    File.Copy(path, Path.Combine(_dir, $"crash-fit-{name}-{i}.bin"), true);
                }
            }
        }
        Check(crashes.Count == 0, string.Join(" | ", crashes));
    }
}
