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
        Test("FIT: wrong hash, signed images only read, plain DTB, never saved over", FitChecks);
        Test("FIT: rebuilt with data in the tree, behind it and at a position; hashes, other images kept", FitRebuild);
        Test("FIT: image.ub ramdisk rebuilt in its uImage; root filesystem behind an OpenWrt FIT", FitRebuildWrapped);
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
            Check(!doc.SavesFilesystemOnly, "a FIT image is rebuilt when saved");
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
            Check(doc.Firmware?.Image is FitImage { Signed: true, HashesWrong: 0 } && doc.SavesFilesystemOnly && doc.Warnings.Any(w => w.Contains("is signed")),
                "a signed FIT image is only read");

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

    /// <summary>
    /// The images of a FIT file as an independent reader finds them (header, structure, strings, external data):
    /// name, data, and each hash with whether it matches the data.
    /// </summary>
    private static Dictionary<string, (byte[] Data, List<(string Algo, bool Ok)> Hashes)> ReadFitImages(byte[] fit)
    {
        uint U(int at) => BinaryPrimitives.ReadUInt32BigEndian(fit.AsSpan(at));
        Check(U(0) == 0xD00DFEED, "not a device tree");
        int total = (int)U(4), offStruct = (int)U(8), offStrings = (int)U(12);
        int dataBase = (total + 3) & ~3;
        string Str(int at) { int e = Array.IndexOf(fit, (byte)0, at); return Encoding.ASCII.GetString(fit, at, e - at); }
        var result = new Dictionary<string, (byte[], List<(string, bool)>)>();
        var path = new List<string>();
        var props = new List<Dictionary<string, byte[]>>();
        for (int pos = offStruct; ;)
        {
            uint token = U(pos);
            pos += 4;
            if (token == 1)
            {
                string name = Str(pos);
                pos += (name.Length + 4) & ~3;
                path.Add(name);
                props.Add(new Dictionary<string, byte[]>());
            }
            else if (token == 3)
            {
                int len = (int)U(pos), name = (int)U(pos + 4);
                props[^1][Str(offStrings + name)] = fit[(pos + 8)..(pos + 8 + len)];
                pos += 8 + ((len + 3) & ~3);
            }
            else if (token == 2)
            {
                // Closing a hash node adds it to its image; closing an image under /images makes the entry.
                Dictionary<string, byte[]> p = props[^1];
                string name = path[^1];
                if (path.Count == 4 && path[1] == "images" && name.StartsWith("hash"))
                {
                    var image = props[^2];
                    image["#hash:" + Encoding.ASCII.GetString(p["algo"]).TrimEnd('\0')] = p["value"];
                }
                if (path.Count == 3 && path[1] == "images")
                {
                    byte[] data = p.TryGetValue("data", out byte[]? d) ? d
                        : fit[(p.ContainsKey("data-offset") ? dataBase + (int)BinaryPrimitives.ReadUInt32BigEndian(p["data-offset"]) : (int)BinaryPrimitives.ReadUInt32BigEndian(p["data-position"]))..][..(int)BinaryPrimitives.ReadUInt32BigEndian(p["data-size"])];
                    var hashes = p.Where(kv => kv.Key.StartsWith("#hash:")).Select(kv =>
                    {
                        string algo = kv.Key[6..];
                        byte[] calc = algo == "crc32" ? Crc32Value(data) : SHA256.HashData(data);
                        return (algo, calc.AsSpan().SequenceEqual(kv.Value));
                    }).ToList();
                    result[name] = (data, hashes);
                }
                path.RemoveAt(path.Count - 1);
                props.RemoveAt(props.Count - 1);
            }
            else if (token == 9) break;
            else if (token != 4) throw new TestFailure($"bad token {token}");
        }
        return result;
    }

    private static void FitRebuild()
    {
        var rnd = new Random(45);
        FsTree tree = SmallTree(45, 8);
        byte[] kernel = Gzipped(Bytes(rnd, 70001)), dtb = BuildFdt(new DtNode("").Prop("model", "board")), cpio = Ramdisk(tree, ArchiveCompression.Gzip), squash = Squash(tree);
        foreach (FitData layout in new[] { FitData.Embedded, FitData.Offset, FitData.Position })
            foreach (string changed in new[] { "ramdisk-1", "rootfs-1" })
            {
                byte[] fit = BuildFit("rebuild", new[] { ("kernel-1", "kernel", kernel, "crc32"), ("fdt-1", "flat_dt", dtb, "sha256"), ("ramdisk-1", "ramdisk", cpio, "sha256"),
                    ("rootfs-1", "filesystem", squash, (string?)"crc32") }, layout);
                string path = WriteFile($"rebuild-{layout}-{changed}.itb", fit);
                string what = $"{layout} {changed}";
                ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false, volume: changed);
                try
                {
                    doc.Tree.CreateFile(doc.Tree.Root, "added", new MemoryFileData(Bytes(rnd, 40000)));
                    SaveOptions o = doc.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir });
                    Check(doc.WritesFirmware(o), what + ": the FIT image is written");
                    doc = doc.Save(path, o).Document!;
                    Check(doc.Firmware is { Part.Name: var n, Image: FitImage { HashesWrong: 0 } } && n == changed && doc.Tree.Resolve("/added") is not null, what + ": reopened");
                }
                finally
                {
                    doc.Dispose();
                }
                var images = ReadFitImages(File.ReadAllBytes(path));
                Check(images.Count == 4 && images.Values.All(i => i.Hashes.Count == 1 && i.Hashes[0].Ok), what + ": hashes " +
                      string.Join(", ", images.Select(i => i.Key + ":" + string.Join("/", i.Value.Hashes.Select(h => h.Algo + (h.Ok ? "" : "!"))))));
                Check(images["kernel-1"].Data.AsSpan().SequenceEqual(kernel) && images["fdt-1"].Data.AsSpan().SequenceEqual(dtb), what + ": kernel and device tree kept");
                string other = changed == "ramdisk-1" ? "rootfs-1" : "ramdisk-1";
                Check(images[other].Data.AsSpan().SequenceEqual(changed == "ramdisk-1" ? squash : cpio), what + ": the other image kept");
                Check(!images[changed].Data.AsSpan().SequenceEqual(changed == "ramdisk-1" ? cpio : squash), what + ": the changed image is the same");
            }
    }

    private static void FitRebuildWrapped()
    {
        var rnd = new Random(46);
        FsTree tree = SmallTree(46, 6);
        byte[] kernel = Gzipped(Bytes(rnd, 30001));
        byte[] wrapped = BuildUImage(UbArm, UbRamdisk, UbGzip, "petalinux-image-minimal", Ramdisk(tree, ArchiveCompression.Gzip));
        string path = WriteFile("rebuild.ub", BuildFit("PetaLinux", new[] { ("kernel@1", "kernel", kernel, "sha256"), ("ramdisk@1", "ramdisk", wrapped, (string?)"sha256") }, FitData.Embedded));
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
        {
            doc.Tree.CreateFile(doc.Tree.Root, "init", new MemoryFileData(Bytes(rnd, 3000, compressible: true)), 0x1ED);
            doc.Save(path, doc.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir })).Document?.Dispose();
        }
        var images = ReadFitImages(File.ReadAllBytes(path));
        Check(images.Values.All(i => i.Hashes.All(h => h.Ok)) && images["kernel@1"].Data.AsSpan().SequenceEqual(kernel), "image.ub: hashes and kernel");
        byte[] ramdisk = images["ramdisk@1"].Data;
        uint size = CheckUImage(ramdisk, "image.ub ramdisk");
        Check(size == ramdisk.Length - 64 && ramdisk.AsSpan(32, 32).SequenceEqual(wrapped.AsSpan(32, 32)) && ramdisk[64] == 0x1F && ramdisk[65] == 0x8B,
            "image.ub: the ramdisk is a gzipped archive in its uImage again");
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
            Check(doc.Format == ImageFormat.Cpio && doc.Tree.Resolve("/init") is not null, "image.ub: reopened");

        // OpenWrt: the root filesystem behind the FIT image; the image itself stays byte for byte, the marker on its boundary.
        byte[] fit = BuildFit("ARM64 OpenWrt FIT", new[] { ("kernel-1", "kernel", kernel, (string?)"crc32") }, FitData.Embedded);
        byte[] padded = PadTo(fit, 65536, 0xFF);
        var file = new List<byte>(padded);
        file.AddRange(Squash(tree));
        while (file.Count % 65536 != 0) file.Add(0xFF);
        file.AddRange(new byte[] { 0xDE, 0xAD, 0xC0, 0xDE });
        path = WriteFile("rebuild-openwrt.itb", file.ToArray());
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
        {
            Check(doc.Firmware?.Part is { Name: "rootfs", BehindImage: true }, "OpenWrt FIT: the root filesystem behind it");
            doc.Tree.CreateFile(doc.Tree.Root, "big", new MemoryFileData(Bytes(rnd, 200000)));
            doc.Save(path, doc.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir })).Document?.Dispose();
        }
        byte[] after = File.ReadAllBytes(path);
        Check(after.AsSpan(0, padded.Length).SequenceEqual(padded), "OpenWrt FIT: the FIT image changed");
        long fsEnd = padded.Length + (long)BinaryPrimitives.ReadUInt64LittleEndian(after.AsSpan(padded.Length + 40));
        long marker = (fsEnd + 65535) / 65536 * 65536;
        // The old marker stood on a boundary of 64, 128 and 256 KiB alike: each of these gets one behind the new filesystem.
        List<int> marks = Markers(after);
        Check(marks.Contains((int)marker) && marks[^1] + 4 == after.Length && marks.All(m => m >= fsEnd), "OpenWrt FIT: the markers behind the new filesystem");
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
