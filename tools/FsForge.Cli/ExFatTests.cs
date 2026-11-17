using System.Buffers.Binary;
using System.Text;
using FsForge.Core;
using FsForge.Core.ExFat;
using FsForge.Core.Images;

namespace FsForge.Cli;

/// <summary>
/// exFAT: round trips with every option; the layout checked against the specification (boot region and its
/// checksum, backup copy, FAT chains against the allocation bitmap, up-case table, entry sets and name hashes);
/// the variants Windows and the Linux driver write (contiguous files without FAT chains, ValidDataLength,
/// deleted entries, two FATs) made by patching an image; damage; limits; a data partition of an SD card.
/// </summary>
internal static partial class SelfTest
{
    private static void ExFatTests()
    {
        using FsTree tree = FatTree(new Random(41));
        string reference = Program.DumpToString(tree);
        var variants = new (string Name, ExFatOptions Options)[]
        {
            ("size from the content", new ExFatOptions()),
            ("64 MiB, 512-byte clusters, label and serial", new ExFatOptions { SizeBytes = 64 << 20, ClusterSize = 512, Label = "CAMERA", VolumeSerial = 0x1234ABCD }),
            ("4 KiB sectors, 32 KiB clusters", new ExFatOptions { SizeBytes = 48 << 20, SectorSize = 4096, ClusterSize = 32768 }),
            ("128 KiB clusters, extra free space, a Unicode label", new ExFatOptions { ClusterSize = 131072, ExtraFreeBytes = 5 << 20, Label = "Фото 2024" }),
        };
        foreach ((string name, ExFatOptions o) in variants)
            Test($"exfat {name}", () => RoundTrip(tree, reference, "w.exfat", new SaveOptions { Format = ImageFormat.ExFat, ExFat = o }));
        Test("exfat: layout, checksums, backup boot region, bitmap and FAT, up-case table, entry sets", () => ExFatLayout(tree));
        Test("exfat: contiguous files without FAT chains, ValidDataLength, deleted entries, two FATs", () => ExFatVariants(tree));
        Test("exfat: damaged boot region, bad checksums, dirty volume", () => ExFatDamage(tree));
        Test("exfat limits: names, case, label, objects exFAT cannot hold", () => ExFatLimits(tree));
        Test("exfat SD card: the data partition is edited in place", ExFatSdCard);
    }

    private static SaveOptions ExFatSave(ExFatOptions? o = null) => new() { Format = ImageFormat.ExFat, ExFat = o ?? new ExFatOptions() };

    /// <summary>An independent view of an exFAT image, to check and patch it.</summary>
    private sealed class ExFatImage(byte[] img)
    {
        public readonly byte[] Img = img;
        public int Bps => 1 << Img[108];
        public int Cs => 1 << (Img[108] + Img[109]);
        public long FatOffset => BinaryPrimitives.ReadUInt32LittleEndian(Img.AsSpan(80)) * (long)Bps;
        public long FatLength => BinaryPrimitives.ReadUInt32LittleEndian(Img.AsSpan(84)) * (long)Bps;
        public long Heap => BinaryPrimitives.ReadUInt32LittleEndian(Img.AsSpan(88)) * (long)Bps;
        public uint Clusters => BinaryPrimitives.ReadUInt32LittleEndian(Img.AsSpan(92));
        public uint RootCluster => BinaryPrimitives.ReadUInt32LittleEndian(Img.AsSpan(96));
        public long ClusterAt(uint c) => Heap + (long)(c - 2) * Cs;
        public uint Fat(uint c) => BinaryPrimitives.ReadUInt32LittleEndian(Img.AsSpan((int)(FatOffset + c * 4)));
        public void SetFat(uint c, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Img.AsSpan((int)(FatOffset + c * 4)), v);

        public List<uint> Chain(uint first)
        {
            var l = new List<uint>();
            for (uint c = first; c >= 2 && c < Clusters + 2 && l.Count <= Clusters; c = Fat(c))
            {
                l.Add(c);
                if (Fat(c) >= 0xFFFFFFF8) break;
            }
            return l;
        }

        /// <summary>The offsets of the entry sets of a directory (file entry), by name.</summary>
        public Dictionary<string, long> Sets(uint dirCluster)
        {
            var r = new Dictionary<string, long>();
            foreach (uint c in Chain(dirCluster))
            {
                long at = ClusterAt(c);
                for (int i = 0; i < Cs; i += 32)
                {
                    byte type = Img[at + i];
                    if (type == 0) return r;
                    if (type != 0x85) continue;
                    int n = Img[at + i + 32 + 3];
                    var name = new StringBuilder();
                    for (int k = 0; name.Length < n; k++)
                        name.Append(Encoding.Unicode.GetString(Img, (int)(at + i + 64 + k * 32 + 2), Math.Min(15, n - name.Length) * 2));
                    r[name.ToString()] = at + i;
                }
            }
            return r;
        }

        public void FixSetChecksum(long set)
        {
            int count = Img[set + 1] + 1;
            BinaryPrimitives.WriteUInt16LittleEndian(Img.AsSpan((int)set + 2), ExFatFormat.SetChecksum(Img.AsSpan((int)set, count * 32)));
        }

        public void FixBootChecksum(int region = 0)
        {
            int start = region * 12 * Bps;
            uint sum = ExFatFormat.BootChecksum(Img.AsSpan(start, 11 * Bps));
            for (int i = 11 * Bps; i < 12 * Bps; i += 4) BinaryPrimitives.WriteUInt32LittleEndian(Img.AsSpan(start + i), sum);
        }
    }

    private static byte[] ExFatBytes(FsTree tree, ExFatOptions o)
    {
        var ms = new MemoryStream();
        ImageDocument.WriteTo(tree, ms, ExFatSave(o));
        return ms.ToArray();
    }

    private static ImageDocument OpenBytes(byte[] img, string name)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, img);
        return ImageDocument.Open(path, _dir);
    }

    private static void ExFatLayout(FsTree tree)
    {
        var x = new ExFatImage(ExFatBytes(tree, new ExFatOptions { SizeBytes = 32 << 20, Label = "Test", VolumeSerial = 0xCAFEBABE }));
        byte[] img = x.Img;
        Check(ExFatFormat.HasSignature(img) && img[510] == 0x55 && img[511] == 0xAA && img.AsSpan(11, 53).IndexOfAnyExcept((byte)0) < 0, "main boot sector");
        Check(img.Length == 32 << 20 && BinaryPrimitives.ReadUInt64LittleEndian(img.AsSpan(72)) == (ulong)(img.Length / x.Bps), "volume length");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(100)) == 0xCAFEBABE && BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(104)) == 0x0100, "serial and revision");
        uint sum = ExFatFormat.BootChecksum(img.AsSpan(0, 11 * x.Bps));
        for (int i = 11 * x.Bps; i < 12 * x.Bps; i += 4) Check(BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(i)) == sum, "checksum sector");
        for (int s = 1; s <= 8; s++) Check(BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan((s + 1) * x.Bps - 4)) == 0xAA550000, $"extended boot sector {s}");
        SameBytes(img.AsSpan(0, 12 * x.Bps), img.AsSpan(12 * x.Bps, 12 * x.Bps), "backup boot region");
        Check(x.FatLength >= (x.Clusters + 2) * 4 && x.Heap >= x.FatOffset + x.FatLength && x.Heap % 4096 == 0, "FAT and cluster heap");
        Check(x.Fat(0) == 0xFFFFFFF8 && x.Fat(1) == 0xFFFFFFFF, "media entries of the FAT");

        // The root: label, bitmap, up-case table; the bitmap marks exactly the clusters in FAT chains.
        long root = x.ClusterAt(x.RootCluster);
        Check(img[root] == 0x83 && img[root + 1] == 4 && Encoding.Unicode.GetString(img, (int)root + 2, 8) == "Test", "label entry");
        Check(img[root + 32] == 0x81 && img[root + 64] == 0x82, "bitmap and up-case entries");
        uint bitmapCluster = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan((int)root + 32 + 20));
        uint upCluster = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan((int)root + 64 + 20));
        long upLength = (long)BinaryPrimitives.ReadUInt64LittleEndian(img.AsSpan((int)root + 64 + 24));
        Check(BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan((int)root + 64 + 4)) == ExFatFormat.TableChecksum(img.AsSpan((int)x.ClusterAt(upCluster), (int)upLength)),
            "up-case table checksum");
        Check(upLength == ExFatFormat.UpcaseTable.Length, "up-case table length");
        long bitmap = x.ClusterAt(bitmapCluster);
        for (uint c = 2; c < x.Clusters + 2; c++)
        {
            bool used = (img[bitmap + (c - 2) / 8] & (1 << (int)((c - 2) % 8))) != 0;
            Check(used == (x.Fat(c) != 0), $"cluster {c}: bitmap {(used ? "used" : "free")}, FAT {x.Fat(c):X8}");
        }

        // Entry sets: checksum, name hash, sizes.
        foreach ((string name, long set) in x.Sets(x.RootCluster))
        {
            int count = img[set + 1] + 1;
            Check(BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan((int)set + 2)) == ExFatFormat.SetChecksum(img.AsSpan((int)set, count * 32)), name + ": set checksum");
            Check(BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan((int)set + 36)) == ExFatFormat.NameHash(name), name + ": name hash");
            Check(count == 2 + (name.Length + 14) / 15, name + ": entry count");
            Check(BinaryPrimitives.ReadUInt64LittleEndian(img.AsSpan((int)set + 40)) == BinaryPrimitives.ReadUInt64LittleEndian(img.AsSpan((int)set + 56)), name + ": valid length");
        }
        // The up-case table maps a small letter to the capital and leaves the rest.
        Check(ExFatFormat.Upcase('a') == 'A' && ExFatFormat.Upcase('я') == 'Я' && ExFatFormat.Upcase('1') == '1' && ExFatFormat.NameHash("Abc") == ExFatFormat.NameHash("aBC"), "up-case mapping");

        // Settings are kept when an image is saved again.
        using ImageDocument doc = OpenBytes(img, "layout.exfat");
        Check(doc.ExFat is { Label: "Test", VolumeSerial: 0xCAFEBABE, ClusterSize: 4096, SizeBytes: 32 << 20, Revision: "1.00" } && !doc.ExFat.Dirty, "information");
        SaveOptions same = doc.CreateSaveOptions(null, new SaveOptions());
        Check(same.Format == ImageFormat.ExFat && same.ExFat.SizeBytes == 32 << 20 && same.ExFat.Label == "Test" && same.ExFat.VolumeSerial == 0xCAFEBABE, "options from the image");
    }

    private static void ExFatVariants(FsTree tree)
    {
        var x = new ExFatImage(ExFatBytes(tree, new ExFatOptions { SizeBytes = 32 << 20 }));
        string want = Program.DumpToString(tree);
        Dictionary<string, long> sets = x.Sets(x.RootCluster);

        // A contiguous file without a FAT chain (NoFatChain), as Windows and Linux write them: the FAT entries are free.
        long elf = sets["start4.elf"];
        uint first = BinaryPrimitives.ReadUInt32LittleEndian(x.Img.AsSpan((int)elf + 32 + 20));
        foreach (uint c in x.Chain(first)) x.SetFat(c, 0);
        x.Img[elf + 32 + 1] |= ExFatFormat.FlagNoFatChain;
        x.FixSetChecksum(elf);
        // The same for a directory.
        long many = sets["many"];
        uint manyCluster = BinaryPrimitives.ReadUInt32LittleEndian(x.Img.AsSpan((int)many + 32 + 20));
        List<uint> manyChain = x.Chain(manyCluster);
        Check(manyChain.Count > 1, "the test directory takes one cluster only");
        foreach (uint c in manyChain) x.SetFat(c, 0);
        x.Img[many + 32 + 1] |= ExFatFormat.FlagNoFatChain;
        x.FixSetChecksum(many);
        // ValidDataLength below DataLength: the rest of the file reads as zeros.
        long cfg = sets["cmdline.txt"];
        BinaryPrimitives.WriteUInt64LittleEndian(x.Img.AsSpan((int)cfg + 32 + 8), 8);
        x.FixSetChecksum(cfg);
        // A deleted entry set (the InUse bit cleared) disappears.
        long upper = sets["UPPER.TXT"];
        for (int k = 0; k <= x.Img[upper + 1]; k++) x.Img[upper + k * 32] &= 0x7F;

        using (ImageDocument doc = OpenBytes(x.Img, "variants.exfat"))
        {
            Check(doc.Warnings.Count == 0, "warnings: " + string.Join("; ", doc.Warnings));
            Check(doc.Tree.Resolve("/UPPER.TXT") is null, "the deleted file is still there");
            byte[] cmd = ReadJ2(doc.Tree.Resolve("/cmdline.txt")!.Inode.Data!);
            byte[] orig = ReadJ2(tree.Resolve("/cmdline.txt")!.Inode.Data!);
            Check(cmd.Length == orig.Length && cmd.AsSpan(0, 8).SequenceEqual(orig.AsSpan(0, 8)) && cmd.AsSpan(8).IndexOfAnyExcept((byte)0) < 0, "ValidDataLength");
            SameBytes(ReadJ2(tree.Resolve("/start4.elf")!.Inode.Data!), ReadJ2(doc.Tree.Resolve("/start4.elf")!.Inode.Data!), "contiguous file");
            Check(doc.Tree.Resolve("/many")!.Children.Count == 300, "contiguous directory");
        }

        // Two FATs (TexFAT): the second is active; the first holds garbage.
        var y = new ExFatImage(ExFatBytes(tree, new ExFatOptions { SizeBytes = 64 << 20 }));
        long fatLength = y.FatLength;
        Check(y.Heap - y.FatOffset >= 2 * fatLength, "no room for a second FAT in the test image");
        y.Img.AsSpan((int)y.FatOffset, (int)fatLength).CopyTo(y.Img.AsSpan((int)(y.FatOffset + fatLength)));
        y.Img.AsSpan((int)y.FatOffset + 8, (int)fatLength - 8).Fill(0x77);
        y.Img[110] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(y.Img.AsSpan(106), 1);          // ActiveFat = second
        y.FixBootChecksum();
        y.Img.AsSpan(0, 12 * y.Bps).CopyTo(y.Img.AsSpan(12 * y.Bps));
        using (ImageDocument doc = OpenBytes(y.Img, "texfat.exfat"))
        {
            Check(doc.ExFat!.Fats == 2, "number of FATs");
            Same(Normalize(want, ExFatSave()), Normalize(Program.DumpToString(doc.Tree), ExFatSave()), "image with two FATs");
        }
    }

    private static void ExFatDamage(FsTree tree)
    {
        byte[] good = ExFatBytes(tree, new ExFatOptions { SizeBytes = 32 << 20, Label = "BACKUP" });
        string want = Normalize(Program.DumpToString(tree), ExFatSave());

        // A damaged main boot region (its checksum no longer matches): the backup is used.
        var a = new ExFatImage((byte[])good.Clone());
        a.Img[300] ^= 0xFF;                                                      // boot code, covered by the checksum
        using (ImageDocument doc = OpenBytes(a.Img, "damaged-main.exfat"))
        {
            Check(doc.ExFat!.UsedBackupBootRegion && doc.Warnings.Any(w => w.Contains("backup")), "backup boot region: " + string.Join("; ", doc.Warnings));
            Same(want, Normalize(Program.DumpToString(doc.Tree), ExFatSave()), "tree read with the backup boot region");
        }
        // A main boot sector that is not recognisable at all.
        var b = new ExFatImage((byte[])good.Clone());
        b.Img.AsSpan(0, 512).Clear();
        Check(ImageDocument.Detect(Path.Combine(_dir, WriteTemp(b.Img, "nomain.exfat"))) == ImageFormat.ExFat, "the backup boot sector is not found");
        using (ImageDocument doc = OpenBytes(b.Img, "nomain.exfat"))
            Check(doc.ExFat!.UsedBackupBootRegion && doc.ExFat.Label == "BACKUP", "image without its main boot sector");

        // A damaged entry set: skipped with a warning, the rest is read.
        var c = new ExFatImage((byte[])good.Clone());
        long set = c.Sets(c.RootCluster)["config.txt"];
        c.Img[set + 32 + 24] ^= 1;                                               // data length, without fixing the checksum
        using (ImageDocument doc = OpenBytes(c.Img, "badset.exfat"))
            Check(doc.Tree.Resolve("/config.txt") is null && doc.Tree.Resolve("/cmdline.txt") is not null && doc.Warnings.Any(w => w.Contains("checksum")),
                "damaged entry set: " + string.Join("; ", doc.Warnings));

        // A volume that was not unmounted: read with a warning.
        var d = new ExFatImage((byte[])good.Clone());
        BinaryPrimitives.WriteUInt16LittleEndian(d.Img.AsSpan(106), 2);          // VolumeDirty (not covered by the checksum)
        using (ImageDocument doc = OpenBytes(d.Img, "dirty.exfat"))
            Check(doc.ExFat!.Dirty && doc.Warnings.Any(w => w.Contains("cleanly")), "dirty volume");

        // A truncated image: the missing part reads as zeros, with a warning.
        using (ImageDocument doc = OpenBytes(good.AsSpan(0, good.Length / 2).ToArray(), "half.exfat"))
            Check(doc.Warnings.Any(w => w.Contains("shorter")), "truncated image: " + string.Join("; ", doc.Warnings));
    }

    private static string WriteTemp(byte[] data, string name)
    {
        File.WriteAllBytes(Path.Combine(_dir, name), data);
        return name;
    }

    private static void ExFatLimits(FsTree tree)
    {
        foreach (string bad in new[] { "a:b", "what?", "tab\there", "back\\slash", "pipe|" })
        {
            using var t = new FsTree(0x1ED, 1700000000);
            t.CreateFile(t.Root, bad, new MemoryFileData(new byte[] { 1 }), 0x1A4, 0, 0, 1700000000);
            try
            {
                ImageDocument.WriteTo(t, new MemoryStream(), ExFatSave());
                throw new TestFailure($"'{bad}' was accepted");
            }
            catch (FsException ex)
            {
                Check(ex.Message.Contains("exFAT"), "message: " + ex.Message);
            }
        }
        // Names that FAT refuses but exFAT keeps: a trailing dot or space, 255 characters, characters beyond the BMP.
        using (var t = new FsTree(0x1ED, 1700000000))
        {
            foreach (string ok in new[] { "trailing.", "trailing ", new string('x', 255), "😀😀😀", "a+b,c;d=e[f]" })
                t.CreateFile(t.Root, ok, new MemoryFileData(Encoding.UTF8.GetBytes(ok)), 0x1A4, 0, 0, 1700000000);
            RoundTrip(t, Program.DumpToString(t), "names.exfat", ExFatSave());
        }
        using (var t = new FsTree(0x1ED, 1700000000))
        {
            t.CreateFile(t.Root, "Makefile", new MemoryFileData(new byte[] { 1 }), 0x1A4, 0, 0, 1700000000);
            t.CreateFile(t.Root, "makefile", new MemoryFileData(new byte[] { 2 }), 0x1A4, 0, 0, 1700000000);
            t.CreateFile(t.Root, "Ёлка", new MemoryFileData(new byte[] { 3 }), 0x1A4, 0, 0, 1700000000);
            try
            {
                ImageDocument.WriteTo(t, new MemoryStream(), ExFatSave());
                throw new TestFailure("names that differ only in case were accepted");
            }
            catch (FsException ex)
            {
                Check(ex.Message.Contains("case"), "message: " + ex.Message);
            }
        }
        foreach (string label in new[] { "twelve chars", "a:b" })
        {
            try
            {
                ExFatSave(new ExFatOptions { Label = label }).ExFat.Validate();
                throw new TestFailure($"the label '{label}' was accepted");
            }
            catch (FsException)
            {
            }
        }
        // Links, devices and sockets are left out, hard links become copies; the warnings say so.
        using var odd = new FsTree(0x1ED, 1700000000);
        FsNode file = odd.CreateFile(odd.Root, "file", new MemoryFileData(new byte[] { 1, 2, 3 }), 0x1A4, 0, 0, 1700000000);
        odd.CreateHardLink(odd.Root, "second-name", file);
        odd.CreateSymlink(odd.Root, "link", "file");
        odd.CreateSpecial(odd.Root, "null", FsNodeType.CharDevice, 1, 3);
        IReadOnlyList<string> warnings = ImageDocument.WriteTo(odd, new MemoryStream(), ExFatSave());
        Check(warnings.Any(w => w.Contains("cannot be stored in exFAT")) && warnings.Any(w => w.Contains("hard links")), "warnings: " + string.Join("; ", warnings));
        // Too much content for a fixed size.
        try
        {
            ImageDocument.WriteTo(tree, new MemoryStream(), ExFatSave(new ExFatOptions { SizeBytes = 2 << 20 }));
            throw new TestFailure("content larger than the filesystem was accepted");
        }
        catch (FsNoSpaceException)
        {
        }
    }

    private static void ExFatSdCard()
    {
        const long MiB = 1 << 20;
        long p1 = 4 * MiB, p2 = p1 + 8 * MiB, slot = 48 * MiB;
        using FsTree data = FatTree(new Random(43));
        var fs = new MemoryStream();
        ImageDocument.WriteTo(data, fs, ExFatSave(new ExFatOptions { SizeBytes = slot, Label = "SDXC", VolumeSerial = 0x5D5D0001, PartitionOffset = p2 / 512 }));
        byte[] disk = MbrDisk(p2 + slot + 2 * MiB,
            new Part(0x0C, p1, 8 * MiB, FakeFat(4, (int)(8 * MiB), "boot")),
            new Part(0x07, p2, slot, fs.ToArray()));
        string path = Path.Combine(_dir, "sdxc.img");
        File.WriteAllBytes(path, disk);

        ImageDocument doc = ImageDocument.Open(path, _dir);
        try
        {
            PartitionInfo part = doc.Container?.Partition ?? throw new TestFailure("the partition table was not recognised");
            Check(part.Number == 2 && part.Content == PartitionContent.ExFat && part.Label == "SDXC" && part.IsEditable, "exFAT partition: " + part);
            Check(doc.Format == ImageFormat.ExFat && doc.ExFat!.PartitionOffset == p2 / 512, "partition offset");
            AddFile(doc, "/", "added.jpg", Bytes(new Random(44), 123456));
            string expected = Program.DumpToString(doc.Tree);
            doc = doc.Save(path, DiskSave(doc)).Document;
            Check(doc.ExFat is { Label: "SDXC", VolumeSerial: 0x5D5D0001, SizeBytes: 48 * MiB } && doc.ExFat.PartitionOffset == p2 / 512, "saved filesystem: " + doc.ExFat);
            Same(expected, Program.DumpToString(doc.Tree), "tree after saving");
            byte[] after = File.ReadAllBytes(path);
            Check(after.Length == disk.Length, "the disk changed its size");
            SameBytes(disk.AsSpan(0, (int)p2), after.AsSpan(0, (int)p2), "partition table and boot partition");
            SameBytes(disk.AsSpan((int)(p2 + slot)), after.AsSpan((int)(p2 + slot)), "data behind the partition");
        }
        finally
        {
            doc.Dispose();
        }
    }
}
