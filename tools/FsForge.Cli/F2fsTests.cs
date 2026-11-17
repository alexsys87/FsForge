using System.Buffers.Binary;
using System.Text;
using FsForge.Core;
using FsForge.Core.Compression;
using FsForge.Core.F2fs;
using FsForge.Core.Images;

namespace FsForge.Cli;

/// <summary>
/// F2FS: round trips with every option; the layout (superblock, checkpoint, NAT, SIT, SSA) checked against the
/// format; a directory large enough for several hash levels and a sparse file large enough for indirect nodes;
/// the variants only the kernel writes (inline data, inline dentries, inline xattrs, extra attributes,
/// compressed clusters, the NAT journal) made by patching an image as the format describes them; damaged copies.
/// </summary>
internal static partial class SelfTest
{
    private static void F2fsTests(FsTree tree, string reference)
    {
        Test("f2fs, size from the content", () => RoundTrip(tree, reference, "w.f2fs", F2fsSave(new F2fsOptions())));
        Test("f2fs of 160 MiB, labelled", () => RoundTrip(tree, reference, "w.f2fs",
            F2fsSave(new F2fsOptions { SizeBytes = 160L << 20, VolumeName = "userdata" })));
        Test("f2fs without extended attributes", () => RoundTrip(tree, reference, "w.f2fs", F2fsSave(new F2fsOptions { StoreXattrs = false, ExtraFreeBytes = 0 })));
        Test("f2fs: layout, checkpoint, NAT and SIT, settings kept on saving", () => F2fsLayout(tree));
        Test("f2fs: 5000 entries in one directory, a sparse file with indirect nodes", F2fsLarge);
        Test("f2fs: inline data, dentries and xattrs, extra attributes, compression, NAT journal", F2fsKernelVariants);
        Test("f2fs: limits and damaged copies", () => F2fsLimits(tree));
        Test("f2fs: userdata partition of a disk image, saved in place", () => F2fsInDisk(tree, reference));
    }

    private static void F2fsInDisk(FsTree tree, string reference)
    {
        long p1 = 4 * MiB, p2 = 8 * MiB, slot = 96 * MiB;
        SaveOptions f2 = F2fsSave(new F2fsOptions { SizeBytes = slot, VolumeName = "userdata" });
        var ms = new MemoryStream();
        ImageDocument.WriteTo(tree, ms, f2);
        byte[] disk = MbrDisk(p2 + slot + 2 * MiB,
            new Part(0x0C, p1, 4 * MiB, FakeFat(4, (int)(4 * MiB), "boot")),
            new Part(0x83, p2, slot, ms.ToArray()));
        string path = Path.Combine(_dir, "board-f2fs.img");
        File.WriteAllBytes(path, disk);

        ImageDocument doc = ImageDocument.Open(path, _dir);
        try
        {
            PartitionInfo part = doc.Container?.Partition ?? throw new TestFailure("the partition table was not recognised");
            Check(part.Number == 2 && part.Content == PartitionContent.F2fs && part.ContentName == "F2FS" && part.Label == "userdata",
                "f2fs partition: " + part);
            Check(doc.Format == ImageFormat.F2fs && doc.F2fs is { VolumeName: "userdata" }, "wrong detection");
            Same(Normalize(reference, f2), Normalize(Program.DumpToString(doc.Tree), f2), "tree of the f2fs partition");
            Guid uuid = doc.F2fs!.Uuid;

            AddFile(doc, "/etc", "hostname.new", Encoding.ASCII.GetBytes("board\n"));
            string expected = Program.DumpToString(doc.Tree);
            ImageDocument.SaveResult r = doc.Save(path, DiskSave(doc));
            doc = r.Document;
            Check(doc.Container is { Partition: { Number: 2, Content: PartitionContent.F2fs } }, "the saved file is not a disk image");
            Check(doc.F2fs!.BlockCount * doc.F2fs.BlockSize == slot && doc.F2fs.Uuid == uuid && doc.F2fs.VolumeName == "userdata",
                "the filesystem did not keep its size, UUID and label");
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

    private static SaveOptions F2fsSave(F2fsOptions o) => new() { Format = ImageFormat.F2fs, F2fs = o };

    // A minimal reader of the structures, independent of F2fsReader, to check and patch images.
    private sealed class F2fsImage(byte[] img)
    {
        public readonly byte[] Img = img;
        public const int B = 4096;
        public uint U32(long at) => BinaryPrimitives.ReadUInt32LittleEndian(Img.AsSpan((int)at));
        public void W32(long at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Img.AsSpan((int)at), v);
        public uint NatBlk => U32(1024 + 84);
        public uint MainBlk => U32(1024 + 92);
        public uint CpBlk => U32(1024 + 76);
        public long NatEntry(uint nid) => (NatBlk + (nid / 455 << 1) - (nid / 455 & 511)) * B + nid % 455 * 9;
        public uint NodeAddr(uint nid) => U32(NatEntry(nid) + 5);
        public Span<byte> Block(long addr) => Img.AsSpan((int)(addr * B), B);
        public void FixCp()
        {
            Span<byte> cp = Block(CpBlk);
            BinaryPrimitives.WriteUInt32LittleEndian(cp[4092..], F2fsFormat.Crc(cp[..4092]));
            cp.CopyTo(Block(CpBlk + 7));
        }
    }

    private static void F2fsLayout(FsTree tree)
    {
        byte[] img = WriteImage(tree, F2fsSave(new F2fsOptions { VolumeName = "data", Uuid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff") }));
        var f = new F2fsImage(img);
        Check(f.U32(1024) == F2fsFormat.Magic && img.AsSpan(1024, 3072).SequenceEqual(img.AsSpan(4096 + 1024, 3072)), "superblock and its copy");
        Check(f.U32(1024 + 72) == 512 && f.CpBlk == 512 && img.Length % (2 << 20) == 0, "segment 0 at 2 MiB");
        uint segs = f.U32(1024 + 48), ckpt = f.U32(1024 + 52), sit = f.U32(1024 + 56), nat = f.U32(1024 + 60), ssa = f.U32(1024 + 64), main = f.U32(1024 + 68);
        Check(segs == ckpt + sit + nat + ssa + main && f.MainBlk == 512 + (segs - main) * 512, "areas");
        Span<byte> cp = f.Block(f.CpBlk);
        Check(F2fsFormat.Crc(cp[..4092]) == BinaryPrimitives.ReadUInt32LittleEndian(cp[4092..]), "checkpoint CRC");
        Check(cp[..4096].SequenceEqual(f.Block(f.CpBlk + 7)) && f.Block(f.CpBlk).SequenceEqual(f.Block(f.CpBlk + 512)), "checkpoint copies");
        uint rsvd = BinaryPrimitives.ReadUInt32LittleEndian(cp[24..]), ovp = BinaryPrimitives.ReadUInt32LittleEndian(cp[28..]);
        ulong user = BinaryPrimitives.ReadUInt64LittleEndian(cp[8..]), valid = BinaryPrimitives.ReadUInt64LittleEndian(cp[16..]);
        Check(rsvd > 0 && ovp > rsvd && user == (main - ovp) * 512UL && valid < user, $"reserved {rsvd}, overprovision {ovp}");
        // Six current segments, all different, in the main area.
        var curs = new HashSet<uint>();
        for (int k = 0; k < 3; k++)
        {
            curs.Add(BinaryPrimitives.ReadUInt32LittleEndian(cp[(36 + k * 4)..]));
            curs.Add(BinaryPrimitives.ReadUInt32LittleEndian(cp[(84 + k * 4)..]));
        }
        Check(curs.Count == 6 && curs.All(s => s < main), "current segments");
        // The root inode (nid 3) through the NAT, its footer and the "." entry of its first dentry block.
        uint rootAddr = f.NodeAddr(3);
        Span<byte> root = f.Block(rootAddr);
        Check(BinaryPrimitives.ReadUInt32LittleEndian(root[4072..]) == 3 && BinaryPrimitives.ReadUInt16LittleEndian(root) == 0x41ED, "root inode");
        Span<byte> dent = f.Block(BinaryPrimitives.ReadUInt32LittleEndian(root[360..]));
        Check((dent[0] & 3) == 3 && BinaryPrimitives.ReadUInt32LittleEndian(dent[(30 + 4)..]) == 3 && dent[2384] == (byte)'.', "\".\" and \"..\" of the root");
        // Every SIT entry counts the bits of its map.
        uint sitBlk = f.U32(1024 + 80);
        long counted = 0;
        for (int s = 0; s < main; s++)
        {
            Span<byte> e = f.Block(sitBlk + s / 55).Slice(s % 55 * 74, 74);
            int n = BinaryPrimitives.ReadUInt16LittleEndian(e) & 0x3FF, bits = 0;
            foreach (byte b in e.Slice(2, 64)) bits += System.Numerics.BitOperations.PopCount(b);
            Check(n == bits, $"SIT entry of segment {s}");
            counted += n;
        }
        Check((ulong)counted == valid, "valid blocks in SIT and checkpoint");

        string path = WriteFile("layout.f2fs", img);
        Check(ImageDocument.Detect(path) == ImageFormat.F2fs, "detected as " + ImageDocument.Detect(path));
        ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
        try
        {
            Check(doc.FormatName == "F2FS" && doc.F2fs is { VolumeName: "data", BlockSize: 4096, NotCleanlyUnmounted: false } && doc.F2fs.Uuid == Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
                "info");
            Check(doc.Warnings.Count == 0, "warnings: " + string.Join("; ", doc.Warnings));
            SaveOptions o = doc.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir });
            Check(o.Format == ImageFormat.F2fs && o.F2fs.SizeBytes == img.Length && o.F2fs.VolumeName == "data", "options from the image");
            doc.Tree.Remove(doc.Tree.Resolve("/etc/passwd")!);
            doc = doc.Save(path, o).Document;
            Check(doc.F2fs is { VolumeName: "data" } && new FileInfo(path).Length == img.Length && doc.Tree.Resolve("/etc/passwd") is null, "saved in place");
        }
        finally
        {
            doc.Dispose();
        }
    }

    private static void F2fsLarge()
    {
        using var t = new FsTree(0x1ED, 1700000000);
        FsNode big = t.CreateDirectory(t.Root, "big");
        for (int i = 0; i < 5000; i++) t.CreateFile(big, $"entry-{i}-{new string('x', i % 50)}", null, 0x1A4, 0, 0, 1700000000 + i);
        // 40 MiB: past the inode, both direct nodes and into the first indirect node; data only here and there.
        byte[] sparse = new byte[40 << 20];
        var rnd = new Random(31);
        foreach (int mib in new[] { 0, 3, 17, 39 }) Bytes(rnd, 70000).CopyTo(sparse, mib << 20);
        t.CreateFile(t.Root, "sparse", new MemoryFileData(sparse));
        string reference = Program.DumpToString(t);
        byte[] img = WriteImage(t, F2fsSave(new F2fsOptions()));
        string path = WriteFile("large.f2fs", img);
        using ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
        // 5000 inodes and their dentry blocks, and only the blocks of the sparse file that hold data.
        Check(doc.F2fs!.ValidBlocks < 7000, $"the holes were written: {doc.F2fs.ValidBlocks} blocks in use");
        Same(reference, Program.DumpToString(doc.Tree), "large directory and sparse file");
        // The directory needed several levels of its hash table.
        var f = new F2fsImage(img);
        uint dirNid = 4;
        Check(f.U32(f.NodeAddr(dirNid) * F2fsImage.B + 72) >= 4, "depth of the large directory");
    }

    private static void F2fsKernelVariants()
    {
        using var t = new FsTree(0x1ED, 1700000000);
        var rnd = new Random(41);
        byte[] small = Encoding.ASCII.GetBytes("inline data\n");
        byte[] text = Bytes(rnd, 4 * 4096, compressible: true);
        byte[] zst = Bytes(rnd, 4 * 4096 - 100, compressible: true);
        FsNode d = t.CreateDirectory(t.Root, "d");
        t.CreateFile(t.Root, "a-small", new MemoryFileData(small));
        t.CreateFile(t.Root, "b-lz4", new MemoryFileData(text));
        t.CreateFile(t.Root, "c-zstd", new MemoryFileData(zst));
        t.CreateFile(d, "x", new MemoryFileData(Encoding.ASCII.GetBytes("x\n")));
        t.CreateFile(d, "y", null);
        byte[] img = WriteImage(t, F2fsSave(new F2fsOptions()));
        var f = new F2fsImage(img);
        // Inodes in the order the writer gives them: root 3, a-small 4, b-lz4 5, c-zstd 6, d 7, x 8, y 9.
        Span<byte> Inode(uint nid) => f.Block(f.NodeAddr(nid));

        // Inline data with an inline xattr area (50 addresses at the end) holding one attribute.
        Span<byte> a = Inode(4);
        a[3] = F2fsFormat.InlineData | F2fsFormat.DataExist | F2fsFormat.InlineXattr;
        a.Slice(360, 923 * 4).Clear();
        small.CopyTo(a[(360 + 4)..]);
        Span<byte> x = a.Slice(360 + (923 - 50) * 4, 200);
        BinaryPrimitives.WriteUInt32LittleEndian(x, F2fsFormat.XattrMagic);
        x[24] = 1;                                   // user.
        x[25] = 4;
        BinaryPrimitives.WriteUInt16LittleEndian(x[26..], 3);
        "testabc"u8.CopyTo(x[28..]);

        // Extra attributes and compressed clusters of four blocks: LZ4 and ZSTD, each into a free block.
        uint free = (uint)(img.Length / 4096) - 2;
        void Compressed(uint nid, int algorithm, byte[] data, byte[] packed, uint at)
        {
            Span<byte> ino = Inode(nid);
            ino[3] = F2fsFormat.ExtraAttr;
            BinaryPrimitives.WriteUInt32LittleEndian(ino[80..], F2fsFormat.ComprFl);
            ino.Slice(360, 923 * 4).Clear();
            BinaryPrimitives.WriteUInt16LittleEndian(ino[360..], 36);
            ino[360 + 32] = (byte)algorithm;
            ino[360 + 33] = 2;
            uint[] addrs = { F2fsFormat.CompressAddr, at, F2fsFormat.NewAddr, F2fsFormat.NewAddr };
            for (int k = 0; k < 4; k++) BinaryPrimitives.WriteUInt32LittleEndian(ino[(360 + 36 + k * 4)..], addrs[k]);
            Span<byte> blk = f.Block(at);
            blk.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(blk, (uint)packed.Length);
            packed.CopyTo(blk[24..]);
        }
        byte[] lz4 = new byte[8192];
        int n = new Lz4Codec().Compress(text, lz4);
        Check(n > 0 && n < 4000, "LZ4 block of the test");
        Compressed(5, F2fsFormat.CompressLz4, text, lz4.AsSpan(0, n).ToArray(), free);
        byte[] z = new ZstdSharp.Compressor(3).Wrap(zst).ToArray();
        Check(z.Length < 4000, "ZSTD frame of the test");
        Compressed(6, F2fsFormat.CompressZstd, zst, z, free + 1);

        // An inline dentry area for "d", made from its dentry block; its NAT entry moved into the checkpoint journal.
        Span<byte> dir = Inode(7);
        Span<byte> blk0 = f.Block(BinaryPrimitives.ReadUInt32LittleEndian(dir[360..]));
        dir[3] = F2fsFormat.InlineDentry;
        dir.Slice(360, 923 * 4).Clear();
        int max = 4 * (923 - 1), nr = max * 8 / (19 * 8 + 1), bitmap = (nr + 7) / 8, reserved = max - (19 * nr + bitmap);
        Span<byte> area = dir.Slice(364, max);
        for (int slot = 0; slot < 214; slot++)
        {
            if ((blk0[slot >> 3] & (1 << (slot & 7))) == 0) continue;
            area[slot >> 3] |= (byte)(1 << (slot & 7));
            blk0.Slice(30 + slot * 11, 11).CopyTo(area[(bitmap + reserved + slot * 11)..]);
            blk0.Slice(2384 + slot * 8, 8).CopyTo(area[(bitmap + reserved + nr * 11 + slot * 8)..]);
        }
        Span<byte> cp = f.Block(f.CpBlk);
        Span<byte> hot = f.Block(f.CpBlk + 1);
        long natAt = f.NatEntry(7);
        BinaryPrimitives.WriteUInt16LittleEndian(hot[3584..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(hot[3586..], 7);
        img.AsSpan((int)natAt, 9).CopyTo(hot[3590..]);
        img.AsSpan((int)natAt, 9).Clear();
        f.FixCp();
        Span<byte> cp2 = f.Block(f.CpBlk + 512);
        cp2[100] ^= 0xFF;                            // the second pack is damaged: the first, with the journal, must win
        _ = cp;

        string path = WriteFile("variants.f2fs", img);
        using ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
        Check(doc.Warnings.Count == 0, "warnings: " + string.Join("; ", doc.Warnings));
        byte[] Read(string p)
        {
            using Stream s = doc.Tree.Resolve(p)!.Inode.Data!.OpenRead();
            var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        Check(Read("/a-small").AsSpan().SequenceEqual(small), "inline data");
        Check(doc.Tree.Resolve("/a-small")!.Inode.XAttrs is { } xa && xa.TryGetValue("user.test", out byte[]? v) && v.AsSpan().SequenceEqual("abc"u8), "inline xattr");
        Check(Read("/b-lz4").AsSpan().SequenceEqual(text), "LZ4 cluster");
        Check(Read("/c-zstd").AsSpan().SequenceEqual(zst), "ZSTD cluster");
        Check(doc.Tree.Resolve("/d/x") is not null && doc.Tree.Resolve("/d/y") is not null && Read("/d/x").AsSpan().SequenceEqual("x\n"u8), "inline dentries through the NAT journal");
        Check(doc.F2fs!.CompressedFiles == 2, "compressed files counted");
    }

    private static void F2fsLimits(FsTree tree)
    {
        byte[] img = WriteImage(tree, F2fsSave(new F2fsOptions()));
        // The primary superblock damaged: the copy is used.
        byte[] bad = (byte[])img.Clone();
        bad[1024] ^= 0xFF;
        using (ImageDocument doc = ImageDocument.Open(WriteFile("badsb.f2fs", bad), _dir, searchEmbedded: false))
            Check(doc.Warnings.Any(w => w.Contains("backup copy")) && doc.Tree.Resolve("/etc/passwd") is not null, "backup superblock: " + string.Join("; ", doc.Warnings));
        // The first checkpoint pack damaged: the second is used.
        bad = (byte[])img.Clone();
        bad[512 * 4096 + 10] ^= 1;
        using (ImageDocument doc = ImageDocument.Open(WriteFile("badcp.f2fs", bad), _dir, searchEmbedded: false))
            Check(doc.Tree.Resolve("/etc/passwd") is not null, "second checkpoint pack");
        // Both packs damaged.
        bad[(512 + 512) * 4096 + 10] ^= 1;
        ExpectFs(() => ImageDocument.Open(WriteFile("badcp2.f2fs", bad), _dir, searchEmbedded: false).Dispose());
        // A size too small for the content is refused.
        try
        {
            WriteImage(tree, F2fsSave(new F2fsOptions { SizeBytes = 40L << 20 }));
            Check(false, "an F2FS of 40 MiB took the test tree");
        }
        catch (FsException ex)
        {
            Check(ex.Message.Contains("does not fit") || ex.Message.Contains("too small"), ex.Message);
        }
    }
}
