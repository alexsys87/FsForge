using System.Text;
using FsForge.Core;
using FsForge.Core.Ext;
using FsForge.Core.Fat;
using FsForge.Core.Images;

namespace FsForge.Cli;

/// <summary>
/// FAT12/16/32: round trips with long names, the limits of the format, standalone images, and the boot
/// partition of an SD card image edited in place (config.txt) with the rest of the card left untouched.
/// </summary>
internal static partial class SelfTest
{
    private static void FatTests()
    {
        using FsTree tree = FatTree(new Random(31));
        string reference = Program.DumpToString(tree);
        var variants = new (string Name, FatOptions Options)[]
        {
            ("automatic type and size", new FatOptions()),
            ("FAT12, 4 MiB", new FatOptions { Type = FatType.Fat12, SizeBytes = 4 << 20, Label = "SMALL" }),
            ("FAT16, 64 MiB, label and serial", new FatOptions { Type = FatType.Fat16, SizeBytes = 64 << 20, Label = "BOOT", VolumeId = 0x1234ABCD }),
            ("FAT32, 48 MiB, 512-byte clusters", new FatOptions { Type = FatType.Fat32, SizeBytes = 48 << 20, ClusterSize = 512, Label = "bootfs" }),
            ("FAT16 with 4 KiB sectors", new FatOptions { Type = FatType.Fat16, SizeBytes = 32 << 20, SectorSize = 4096 }),
            ("one FAT, extra free space", new FatOptions { Fats = 1, ExtraFreeBytes = 5 << 20 }),
        };
        foreach ((string name, FatOptions o) in variants)
            Test($"fat {name}", () => RoundTrip(tree, reference, "w.vfat", new SaveOptions { Format = ImageFormat.Fat, Fat = o }));
        Test("fat a root with more entries than a FAT16 root holds by default", FatWideRoot);
        Test("fat limits: names, case, sizes, types", () => FatLimits(tree));
        Test("fat SD card: edit config.txt in the boot partition, the rest stays", FatSdCard);
    }

    /// <summary>Files and directories only, with every kind of name FAT has to map.</summary>
    private static FsTree FatTree(Random rnd)
    {
        var t = new FsTree(0x1ED, 1700000000);
        void File(FsNode dir, string name, byte[] data, long time = 1700000001) => t.CreateFile(dir, name, new MemoryFileData(data), 0x1A4, 0, 0, time);
        FsNode root = t.Root;
        File(root, "config.txt", Encoding.ASCII.GetBytes("dtparam=audio=on\n"));
        File(root, "cmdline.txt", Encoding.ASCII.GetBytes("console=serial0,115200 root=PARTUUID=1234-02 rootwait\n"));
        File(root, "start4.elf", Bytes(rnd, 300000));
        File(root, "UPPER.TXT", new byte[] { 1 });
        File(root, "MiXeD.Txt", new byte[] { 2 });
        File(root, ".hidden", new byte[] { 3 });
        File(root, "multi.dot.name.tar.gz", Bytes(rnd, 5000));
        File(root, "name with spaces.txt", new byte[] { 4 });
        File(root, "файл на русском.txt", Encoding.UTF8.GetBytes("юникод\n"));
        File(root, "日本語", new byte[] { 5 });
        File(root, "emoji-😀.txt", new byte[] { 6 });            // a surrogate pair in the long name
        File(root, new string('n', 250) + ".txt", new byte[] { 7 });
        File(root, "empty", Array.Empty<byte>());
        File(root, "odd-seconds", new byte[] { 8 }, 1700000003);
        File(root, "before-1980", new byte[] { 9 }, 100);
        t.CreateFile(root, "readonly.txt", new MemoryFileData(new byte[] { 10 }), 0x124, 0, 0, 1700000000);
        for (int i = 1; i <= 12; i++) File(root, $"Long File Name {i}.txt", Encoding.ASCII.GetBytes(i.ToString()));
        FsNode sizes = t.CreateDirectory(root, "sizes", 0x1ED, 0, 0, 1600000000);
        foreach (int n in new[] { 1, 511, 512, 513, 4095, 4096, 4097, 65536, 65537, 1 << 20 }) File(sizes, "s" + n, Bytes(rnd, n));
        FsNode many = t.CreateDirectory(root, "many", 0x1ED, 0, 0, 1600000002);
        for (int i = 0; i < 300; i++) File(many, $"entry-with-a-long-name-{i:000}.dat", Encoding.ASCII.GetBytes(i.ToString()));
        FsNode deep = root;
        for (int i = 0; i < 12; i++) deep = t.CreateDirectory(deep, "level" + i, 0x1ED, 0, 0, 1600000004);
        File(deep, "leaf", Encoding.ASCII.GetBytes("leaf\n"));
        t.CreateDirectory(root, "emptydir", 0x1ED, 0, 0, 1600000006);
        return t;
    }

    private static void FatWideRoot()
    {
        using var t = new FsTree(0x1ED, 1700000000);
        for (int i = 0; i < 700; i++) t.CreateFile(t.Root, $"root-entry-with-a-long-name-{i:0000}.txt", new MemoryFileData(Encoding.ASCII.GetBytes(i.ToString())), 0x1A4, 0, 0, 1700000000);
        RoundTrip(t, Program.DumpToString(t), "wide.vfat", new SaveOptions { Format = ImageFormat.Fat, Fat = new FatOptions { Type = FatType.Fat16, SizeBytes = 16 << 20 } });
    }

    private static void FatLimits(FsTree tree)
    {
        SaveOptions Fat(FatOptions? o = null) => new() { Format = ImageFormat.Fat, Fat = o ?? new FatOptions() };
        foreach (string bad in new[] { "a:b", "what?", "tab\there", "trailing.", "trailing " })
        {
            using var t = new FsTree(0x1ED, 1700000000);
            t.CreateFile(t.Root, bad, null);
            ExpectFs(() => ImageDocument.WriteTo(t, new MemoryStream(), Fat()));
        }
        using (var t = new FsTree(0x1ED, 1700000000))
        {
            t.CreateFile(t.Root, "Makefile", null);
            t.CreateFile(t.Root, "makefile", null);
            ExpectFs(() => ImageDocument.WriteTo(t, new MemoryStream(), Fat()));          // names that differ only in case
        }
        // Links, devices and FIFOs are left out with a warning; hard links become copies.
        using (var t = new FsTree(0x1ED, 1700000000))
        {
            FsNode f = t.CreateFile(t.Root, "file", new MemoryFileData(new byte[] { 1, 2, 3 }));
            t.CreateHardLink(t.Root, "file2", f);
            t.CreateSymlink(t.Root, "link", "file");
            t.CreateSpecial(t.Root, "null", FsNodeType.CharDevice, 1, 3);
            var ms = new MemoryStream();
            IReadOnlyList<string> w = ImageDocument.WriteTo(t, ms, Fat());
            Check(w.Any(x => x.Contains("cannot be stored in FAT") && x.Contains("/link")) && w.Any(x => x.Contains("separate copies")), "warnings: " + string.Join(" | ", w));
            string path = Path.Combine(_dir, "links.vfat");
            File.WriteAllBytes(path, ms.ToArray());
            using ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
            Check(doc.Tree.Resolve("/link") is null && doc.Tree.Resolve("/file2")!.Inode.Size == 3 && doc.Tree.Resolve("/file")!.Inode.LinkCount == 1, "links in FAT");
        }
        ExpectFs(() => ImageDocument.WriteTo(tree, new MemoryStream(), Fat(new FatOptions { SizeBytes = 1 << 20 })));          // too small
        ExpectFs(() => ImageDocument.WriteTo(tree, new MemoryStream(), Fat(new FatOptions { Type = FatType.Fat32, SizeBytes = 8 << 20 })));   // FAT32 needs 65525 clusters
        ExpectFs(() => ImageDocument.WriteTo(tree, new MemoryStream(), Fat(new FatOptions { Label = "MUCH TOO LONG" })));
        ExpectFs(() => ImageDocument.WriteTo(tree, new MemoryStream(), Fat(new FatOptions { ClusterSize = 3000 })));
    }

    private static void FatSdCard()
    {
        const long MiB = 1 << 20;
        long p1 = 4 * MiB, p2 = p1 + 40 * MiB;
        // The boot partition as a card maker writes it: FAT32 that knows where its partition starts.
        using FsTree boot = FatTree(new Random(32));
        var bootFs = new MemoryStream();
        ImageDocument.WriteTo(boot, bootFs, new SaveOptions
        {
            Format = ImageFormat.Fat,
            Fat = new FatOptions { Type = FatType.Fat32, SizeBytes = 40 * MiB, ClusterSize = 512, Label = "bootfs", VolumeId = 0xA1B2C3D4, HiddenSectors = (uint)(p1 / 512) },
        });
        using FsTree rootTree = BuildTree(new Random(33));
        var rootFs = new MemoryStream();
        ImageDocument.WriteTo(rootTree, rootFs, new SaveOptions { Format = ImageFormat.Ext, Ext = new ExtOptions { SizeBytes = 24 * MiB } });
        byte[] disk = MbrDisk(p2 + 24 * MiB,
            new Part(0x0C, p1, 40 * MiB, bootFs.ToArray(), Boot: true),
            new Part(0x83, p2, 24 * MiB, rootFs.ToArray()));
        string path = Path.Combine(_dir, "card.img");
        File.WriteAllBytes(path, disk);

        // Without a choice the root filesystem opens, not the boot partition.
        using (ImageDocument d = ImageDocument.Open(path, _dir))
            Check(d.Format == ImageFormat.Ext && d.Container!.Partition.Number == 2, "default partition: " + d.Container?.Partition.Number);
        PartitionTable table = PartitionTable.Read(new FileBlockSource(path))!;
        Check(table.Editable.Count == 2 && table.Partitions[0].IsEditable && table.Partitions[0].ContentName == "FAT32", "the FAT partition is not editable");

        ImageDocument doc = ImageDocument.Open(path, _dir, partition: 1);
        try
        {
            FatInfo before = doc.Fat!;
            Check(doc.Format == ImageFormat.Fat && before.Type == FatType.Fat32 && before.HiddenSectors == p1 / 512 && before.Label == "bootfs", "boot partition: " + doc.FormatName);
            // Edit config.txt and cmdline.txt, add an overlay, delete a file.
            doc.Tree.SetData(doc.Tree.Resolve("/config.txt")!, new MemoryFileData(Encoding.ASCII.GetBytes("dtparam=audio=on\ndtoverlay=vc4-kms-v3d\nenable_uart=1\n")), 1700000900);
            doc.Tree.SetData(doc.Tree.Resolve("/cmdline.txt")!, new MemoryFileData(Encoding.ASCII.GetBytes("console=serial0,115200 root=PARTUUID=1234-02 rootwait quiet\n")), 1700000900);
            doc.Tree.Remove(doc.Tree.Resolve("/UPPER.TXT")!);
            AddFile(doc, "/sizes", "new-overlay.dtbo", Bytes(new Random(34), 7000));
            doc = doc.Save(path, DiskSave(doc)).Document;
            FatInfo after = doc.Fat!;
            Check(doc.Container!.Partition.Number == 1 && after.Type == FatType.Fat32 && after.ClusterSize == 512 && after.SizeBytes == 40 * MiB
                  && after.Label == "bootfs" && after.VolumeId == 0xA1B2C3D4 && after.HiddenSectors == p1 / 512,
                $"saved boot partition: {after.TypeName}, {after.ClusterSize}, {after.SizeBytes}, '{after.Label}', {after.VolumeId:X8}, {after.HiddenSectors}");
            Check(Encoding.ASCII.GetString(ReadJ2(doc.Tree.Resolve("/config.txt")!.Inode.Data!)).Contains("enable_uart=1")
                  && doc.Tree.Resolve("/UPPER.TXT") is null && doc.Tree.Resolve("/sizes/new-overlay.dtbo") is not null, "the edits are not in the card");
            byte[] saved = File.ReadAllBytes(path);
            SameBytes(disk.AsSpan(0, (int)p1), saved.AsSpan(0, (int)p1), "partition table and boot loader");
            SameBytes(disk.AsSpan((int)p2), saved.AsSpan((int)p2), "the root partition");

            // Content that does not fit the partition is refused, and the card stays as it was.
            AddFile(doc, "/", "huge.bin", new byte[45 * MiB]);
            try
            {
                doc.Save(path, DiskSave(doc)).Document.Dispose();
                throw new TestFailure("content larger than the partition was accepted");
            }
            catch (FsException ex)
            {
                Check(ex.Message.Contains("does not fit"), "message: " + ex.Message);
            }
            Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(saved), "a refused save changed the card");
            doc.Tree.Remove(doc.Tree.Resolve("/huge.bin")!);

            // The FAT partition alone, as an image of its own.
            string alone = Path.Combine(_dir, "boot.vfat");
            SaveOptions o = DiskSave(doc);
            o.KeepContainer = false;
            doc = doc.Save(alone, o).Document;
            Check(ImageDocument.Detect(alone) == ImageFormat.Fat && doc.Container is null && doc.Fat!.SizeBytes == 40 * MiB, "the partition saved alone");
        }
        finally
        {
            doc.Dispose();
        }
    }
}
