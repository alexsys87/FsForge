using System.Buffers.Binary;
using System.Text;
using FsForge.Core;
using FsForge.Core.Ext;
using FsForge.Core.Images;
using FsForge.Core.SquashFs;
using FsForge.Core.Tar;

namespace FsForge.Cli;

/// <summary>
/// Tests of disk images with a partition table: the filesystem is edited in its partition and the
/// rest of the disk has to come out byte for byte as it went in.
/// </summary>
internal static partial class SelfTest
{
    private const long MiB = 1L << 20;
    private const long Kib64 = 0x10000;

    private static long Align(long value, long step) => (value + step - 1) / step * step;

    private static void DiskTests(FsTree tree, string reference)
    {
        Test("disk image: OpenWrt-like sdcard.img.gz is saved as a whole disk", () => DiskSdCard(tree, reference));
        Test("disk image: data behind the filesystem and other partitions survive", () => DiskOverlay(tree));
        Test("disk image: the last MBR partition grows with the filesystem", () => DiskGrow(tree));
        Test("disk image: ext4 root partition (FAT + ext4), xz compressed", () => DiskExt(tree, reference));
        Test("disk image: GPT with a backup table", () => DiskGpt(tree));
        Test("disk image: several filesystems, logical partitions, explicit choice", () => DiskChoice(tree));
        Test("disk image: filesystem found without a partition table is saved alone", () => DiskScan(tree));
        Test("disk image: damaged partition tables are rejected cleanly", DiskFuzz);
    }

    // ------------------------------------------------------------ builders

    private static byte[] SquashImage(FsTree tree, SquashCompression compression = SquashCompression.Gzip)
    {
        var ms = new MemoryStream();
        ImageDocument.WriteTo(tree, ms, new SaveOptions
        {
            Format = ImageFormat.SquashFs,
            SquashFs = new SquashFsOptions { Compression = compression, GzipLevel = 9, XzEffort = 0 },
        });
        return ms.ToArray();
    }

    /// <summary>A FAT16 boot sector followed by noise: enough to be recognised and to notice any change.</summary>
    private static byte[] FakeFat(int seed, int length, string label)
    {
        byte[] b = Bytes(new Random(seed), length);
        Array.Clear(b, 0, 512);
        b[0] = 0xEB; b[1] = 0x3C; b[2] = 0x90;
        Encoding.ASCII.GetBytes("MSDOS5.0").CopyTo(b, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(11), 512);
        b[13] = 4;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(14), 1);
        b[16] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(17), 512);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(19), (ushort)Math.Min(0xFFFF, length / 512));
        b[21] = 0xF8;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(22), 32);
        b[38] = 0x29;
        Encoding.ASCII.GetBytes(label.PadRight(11)).CopyTo(b, 43);
        Encoding.ASCII.GetBytes("FAT16   ").CopyTo(b, 54);
        b[510] = 0x55; b[511] = 0xAA;
        return b;
    }

    private readonly record struct Part(byte Type, long Offset, long Length, byte[]? Content = null, bool Boot = false);

    private static void WriteMbrEntry(byte[] sector, int slot, Part p, long relativeTo = 0)
    {
        Span<byte> e = sector.AsSpan(446 + 16 * slot, 16);
        e[0] = (byte)(p.Boot ? 0x80 : 0);
        e[1] = 0xFE; e[2] = 0xFF; e[3] = 0xFF;
        e[4] = p.Type;
        e[5] = 0xFE; e[6] = 0xFF; e[7] = 0xFF;
        BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)((p.Offset - relativeTo) / 512));
        BinaryPrimitives.WriteUInt32LittleEndian(e[12..], (uint)(p.Length / 512));
    }

    /// <summary>Builds a disk with up to four primary partitions; the file ends at <paramref name="fileLength"/>.</summary>
    private static byte[] MbrDisk(long fileLength, params Part[] parts)
    {
        byte[] disk = new byte[fileLength];
        new Random(77).NextBytes(disk.AsSpan(0, 440));      // boot code
        disk[0] = 0xEB; disk[1] = 0x63; disk[2] = 0x90;
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(440), 0x5452574F);    // disk signature (PARTUUID)
        for (int i = 0; i < parts.Length; i++)
        {
            WriteMbrEntry(disk, i, parts[i]);
            Place(disk, parts[i]);
        }
        disk[510] = 0x55; disk[511] = 0xAA;
        // Boot loader between the table and the first partition, as on real cards.
        new Random(78).NextBytes(disk.AsSpan(512, (int)Math.Min(parts[0].Offset, fileLength) - 512));
        return disk;
    }

    private static void Place(byte[] disk, Part p)
    {
        if (p.Content is null || p.Offset >= disk.Length) return;
        p.Content.AsSpan(0, (int)Math.Min(p.Content.Length, disk.Length - p.Offset)).CopyTo(disk.AsSpan((int)p.Offset));
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ (0xEDB88320 & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    /// <summary>Builds a GPT disk (protective MBR, primary and backup tables) with the given partitions.</summary>
    private static byte[] GptDisk(long length, params (int Slot, Guid Type, string Name, Part Part)[] parts)
    {
        byte[] disk = new byte[length];
        long sectors = length / 512;
        WriteMbrEntry(disk, 0, new Part(0xEE, 512, Math.Min(length - 512, 0xFFFFFFFFL * 512)));
        disk[510] = 0x55; disk[511] = 0xAA;

        byte[] table = new byte[128 * 128];
        foreach ((int slot, Guid type, string name, Part p) in parts)
        {
            Span<byte> e = table.AsSpan((slot - 1) * 128, 128);
            type.TryWriteBytes(e);
            Guid.NewGuid().TryWriteBytes(e[16..]);
            BinaryPrimitives.WriteUInt64LittleEndian(e[32..], (ulong)(p.Offset / 512));
            BinaryPrimitives.WriteUInt64LittleEndian(e[40..], (ulong)((p.Offset + p.Length) / 512 - 1));
            Encoding.Unicode.GetBytes(name).CopyTo(e[56..]);
            Place(disk, p);
        }

        void Header(long at, long other, long tableLba)
        {
            Span<byte> h = disk.AsSpan((int)(at * 512), 92);
            "EFI PART"u8.CopyTo(h);
            BinaryPrimitives.WriteUInt32LittleEndian(h[8..], 0x00010000);
            BinaryPrimitives.WriteUInt32LittleEndian(h[12..], 92);
            BinaryPrimitives.WriteUInt64LittleEndian(h[24..], (ulong)at);
            BinaryPrimitives.WriteUInt64LittleEndian(h[32..], (ulong)other);
            BinaryPrimitives.WriteUInt64LittleEndian(h[40..], 34);
            BinaryPrimitives.WriteUInt64LittleEndian(h[48..], (ulong)(sectors - 34));
            new Guid("0F0E0D0C-0B0A-0908-0706-050403020100").TryWriteBytes(h[56..]);
            BinaryPrimitives.WriteUInt64LittleEndian(h[72..], (ulong)tableLba);
            BinaryPrimitives.WriteUInt32LittleEndian(h[80..], 128);
            BinaryPrimitives.WriteUInt32LittleEndian(h[84..], 128);
            BinaryPrimitives.WriteUInt32LittleEndian(h[88..], Crc32(table));
            BinaryPrimitives.WriteUInt32LittleEndian(h[16..], Crc32(h));
        }

        table.CopyTo(disk, 2 * 512);
        table.CopyTo(disk, (int)((sectors - 33) * 512));
        Header(1, sectors - 1, 2);
        Header(sectors - 1, 1, sectors - 33);
        return disk;
    }

    private static byte[] Unpacked(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        if (ArchiveCodec.DetectCompression(file.AsSpan(0, Math.Min(8, file.Length))) is not { } c) return file;
        using Stream z = ArchiveCodec.OpenDecompressor(new MemoryStream(file), c);
        var ms = new MemoryStream();
        z.CopyTo(ms);
        return ms.ToArray();
    }

    private static void WritePacked(string path, byte[] disk, ArchiveCompression c)
    {
        using var fs = new FileStream(path, FileMode.Create);
        using Stream z = ArchiveCodec.OpenCompressor(fs, new TarOptions { Compression = c, XzEffort = 0, ZstdLevel = 3 });
        z.Write(disk);
    }

    private static void SameBytes(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string what)
    {
        if (expected.Length != actual.Length) throw new TestFailure($"{what}: {actual.Length} bytes instead of {expected.Length}");
        int at = expected.CommonPrefixLength(actual);
        if (at != expected.Length) throw new TestFailure($"{what}: first difference at offset 0x{at:X}");
    }

    private static bool AllZero(ReadOnlySpan<byte> data) => data.IndexOfAnyExcept((byte)0) < 0;

    private static SaveOptions DiskSave(ImageDocument doc) =>
        doc.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir });

    private static void AddFile(ImageDocument doc, string dir, string name, byte[] data) =>
        doc.Tree.CreateFile(doc.Tree.Resolve(dir)!, name, new MemoryFileData(data), 0x1A4, 0, 0, 1700000700);

    // ---------------------------------------------------------------- tests

    private static void DiskSdCard(FsTree tree, string reference)
    {
        byte[] sq = SquashImage(tree);
        var sqOptions = new SaveOptions { Format = ImageFormat.SquashFs };
        long p1 = 1 * MiB, p2 = 5 * MiB;
        long slot = Align(sq.Length, Kib64) + 6 * MiB;
        // OpenWrt pads the root filesystem to 64 KiB and ends the file there: the partition is longer than the file.
        byte[] disk = MbrDisk(p2 + Align(sq.Length, Kib64),
            new Part(0x0C, p1, 4 * MiB, FakeFat(1, (int)(4 * MiB), "BOOT"), Boot: true),
            new Part(0x83, p2, slot, sq));
        string path = Path.Combine(_dir, "sdcard.img.gz");
        WritePacked(path, disk, ArchiveCompression.Gzip);
        byte[] packedBefore = File.ReadAllBytes(path);
        int temps = Directory.GetFiles(_dir, "fsforge-*.img").Length;

        Check(ImageDocument.Detect(path) == ImageFormat.SquashFs, "Detect does not see the root filesystem of a compressed disk image");
        ImageDocument doc = ImageDocument.Open(path, _dir);
        try
        {
            DiskContainer c = doc.Container ?? throw new TestFailure("the partition table was not recognised");
            Check(c.Table.Scheme == PartitionScheme.Mbr && c.Table.Partitions.Count == 2, "wrong partition list");
            PartitionInfo boot = c.Table.Partitions[0], root = c.Table.Partitions[1];
            Check(boot.Content == PartitionContent.Fat && boot.ContentName == "FAT16" && boot.Label == "BOOT" && boot.Bootable, "boot partition: " + boot);
            Check(ReferenceEquals(root, c.Partition) && root.Number == 2 && root.Offset == p2 && root.Length == slot, "root partition: " + root);
            Check(root.StoredLength == Align(sq.Length, Kib64) && root.IsLast && c.CanGrow, "root partition extent");
            Check(doc.Format == ImageFormat.SquashFs && doc.OuterCompression == ArchiveCompression.Gzip && doc.EmbeddedOffset == p2, "wrong detection");
            Check(!doc.SavesFilesystemOnly, "a partitioned disk image must be saved as a disk image");
            Check(doc.Warnings.Count == 0, "unexpected warning: " + doc.Warnings.FirstOrDefault());
            Same(Normalize(reference, sqOptions), Normalize(Program.DumpToString(doc.Tree), sqOptions), "tree of the root partition");

            AddFile(doc, "/etc", "added.conf", Encoding.ASCII.GetBytes("option enabled 1\n"));
            doc.Tree.Remove(doc.Tree.Resolve("/var/many")!);
            doc.Tree.SetMode(new[] { doc.Tree.Resolve("/bin")! }, 0x1C0, recursive: true, targets: FsTree.Targets.Files);
            string expected = Program.DumpToString(doc.Tree);

            SaveOptions o = DiskSave(doc);
            o.KeepBackup = true;
            Check(doc.WritesContainer(o), "the save options do not keep the container");
            ImageDocument.SaveResult r = doc.Save(path, o);
            doc = r.Document;
            Check(doc.Container is { Partition.Number: 2 } && doc.OuterCompression == ArchiveCompression.Gzip, "the saved file is not a compressed disk image");
            Same(expected, Program.DumpToString(doc.Tree), "tree after saving the disk image");
            SameBytes(packedBefore, File.ReadAllBytes(path + ".bak"), "backup of the disk image");

            byte[] after = Unpacked(path);
            SameBytes(disk.AsSpan(0, (int)p2), after.AsSpan(0, (int)p2), "partition table, boot loader and boot partition");
            Check(BinaryPrimitives.ReadUInt32LittleEndian(after.AsSpan((int)p2)) == 0x73717368, "no SquashFS at the start of the root partition");
            long fsEnd = (long)BinaryPrimitives.ReadUInt64LittleEndian(after.AsSpan((int)p2 + 40));
            Check(after.Length >= disk.Length && after.Length <= p2 + slot, $"length of the saved disk is {after.Length}");
            Check(after.Length >= p2 + Math.Min(slot, Align(fsEnd, Kib64) + Kib64) || Align(fsEnd, Kib64) == Align(sq.Length, Kib64),
                "no zeroed area where the overlay starts");
            Check(AllZero(after.AsSpan((int)(p2 + Align(fsEnd, 4096)))), "the area behind the filesystem is not clean");

            // The same disk, written without compression and with the filesystem alone.
            string plain = Path.Combine(_dir, "sdcard-copy.img");
            doc = doc.Save(plain, DiskSave(doc)).Document;
            Check(doc.Container is not null && doc.OuterCompression is null, "saving under an .img name must give a plain disk image");
            byte[] again = File.ReadAllBytes(plain);
            after.AsSpan((int)p2 + 8, 4).CopyTo(again.AsSpan((int)p2 + 8));    // the creation time of the filesystem is new
            SameBytes(after, again, "disk image saved again without changes");
            Check(ImageDocument.Detect(plain) == ImageFormat.SquashFs, "Detect does not see the root filesystem of a disk image");

            string alone = Path.Combine(_dir, "sdcard-root.squashfs");
            SaveOptions fsOnly = DiskSave(doc);
            fsOnly.KeepContainer = false;
            doc = doc.Save(alone, fsOnly).Document;
            Check(doc.Container is null && doc.EmbeddedOffset == 0 && doc.Format == ImageFormat.SquashFs, "the filesystem was not saved alone");
            Check(new FileInfo(alone).Length < 5 * MiB + sq.Length, "the file with the filesystem alone is too large");
            Same(expected, Program.DumpToString(doc.Tree), "tree of the filesystem saved alone");
        }
        finally
        {
            doc.Dispose();
        }
        Check(Directory.GetFiles(_dir, "fsforge-*.img").Length == temps, "temporary images were left behind");
        Check(Directory.GetFiles(_dir, "*.fsforge-tmp").Length == 0, "temporary output files were left behind");
    }

    private static void DiskOverlay(FsTree tree)
    {
        byte[] sq = SquashImage(tree);
        long p1 = 1 * MiB, p2 = 3 * MiB;
        long overlayAt = Align(sq.Length, Kib64);
        long slot = overlayAt + 2 * MiB;
        long p3 = p2 + slot;
        // The root partition is followed by leftovers of a used card: a formatted overlay, then a data partition.
        byte[] rootfs = new byte[slot];
        sq.CopyTo(rootfs, 0);
        byte[] overlay = Bytes(new Random(5), (int)(slot - overlayAt));
        overlay.CopyTo(rootfs, overlayAt);
        byte[] data = Bytes(new Random(6), (int)MiB);
        byte[] disk = MbrDisk(p3 + MiB,
            new Part(0x0C, p1, 2 * MiB, FakeFat(2, (int)(2 * MiB), "KERNEL")),
            new Part(0x83, p2, slot, rootfs),
            new Part(0x83, p3, MiB, data));
        string path = Path.Combine(_dir, "used-card.img");
        File.WriteAllBytes(path, disk);

        ImageDocument doc = ImageDocument.Open(path, _dir);
        try
        {
            Check(doc.Container is { Partition.Number: 2, CanGrow: false }, "wrong partition was opened");
            Check(doc.Container!.Table.Partitions[2].Content == PartitionContent.Unknown, "noise was taken for a filesystem");

            // 1. A change that keeps the size: the overlay must stay where it is, bit for bit.
            doc.Tree.SetMode(new[] { doc.Tree.Resolve("/etc/passwd")! }, 0x180, recursive: false, targets: FsTree.Targets.All);
            ImageDocument.SaveResult r = doc.Save(path, DiskSave(doc));
            doc = r.Document;
            byte[] after = File.ReadAllBytes(path);
            Check(after.Length == disk.Length, "the disk changed its size");
            SameBytes(disk.AsSpan(0, (int)p2), after.AsSpan(0, (int)p2), "data in front of the root partition");
            SameBytes(overlay, after.AsSpan((int)(p2 + overlayAt), overlay.Length), "overlay behind an unchanged filesystem size");
            SameBytes(disk.AsSpan((int)p3), after.AsSpan((int)p3), "partition behind the root partition");
            Check(!r.Warnings.Any(w => w.Contains("overlay")), "an overlay warning although the overlay was kept");
            Check(doc.Tree.Resolve("/etc/passwd")!.Inode.Mode == 0x180, "the edit is missing");

            // 2. A change that moves the end of the filesystem: the overlay is cleared and reported.
            AddFile(doc, "/var", "blob.bin", Bytes(new Random(8), 300_000));
            r = doc.Save(path, DiskSave(doc));
            doc = r.Document;
            after = File.ReadAllBytes(path);
            long fsEnd = (long)BinaryPrimitives.ReadUInt64LittleEndian(after.AsSpan((int)p2 + 40));
            Check(Align(fsEnd, Kib64) > overlayAt, "the test did not move the end of the filesystem");
            Check(r.Warnings.Any(w => w.Contains("overlay")), "clearing the overlay was not reported");
            Check(after.Length == disk.Length, "the disk changed its size");
            Check(AllZero(after.AsSpan((int)(p2 + Align(fsEnd, 4096)), (int)(slot - Align(fsEnd, 4096)))), "stale overlay data is left behind the filesystem");
            SameBytes(disk.AsSpan(0, (int)p2), after.AsSpan(0, (int)p2), "data in front of the root partition");
            SameBytes(disk.AsSpan((int)p3), after.AsSpan((int)p3), "partition behind the root partition");
            Check(doc.Tree.Resolve("/var/blob.bin") is { Inode.Size: 300_000 }, "the added file is missing");

            // 3. More than the partition can take, and no way to enlarge it: a clean refusal.
            AddFile(doc, "/var", "huge.bin", Bytes(new Random(9), (int)(3 * MiB)));
            try
            {
                doc.Save(path, DiskSave(doc));
                throw new TestFailure("a filesystem larger than its partition was written");
            }
            catch (FsException ex)
            {
                Check(ex.Message.Contains("partition 2") && ex.Message.Contains("other partitions follow"), "unhelpful message: " + ex.Message);
            }
            Check(!doc.IsDisposed && doc.IsModified, "the document was closed by a failed save");
            SameBytes(after, File.ReadAllBytes(path), "disk image after a refused save");
            Check(!File.Exists(path + ".fsforge-tmp"), "the temporary file was left behind");
        }
        finally
        {
            doc.Dispose();
        }
    }

    private static void DiskGrow(FsTree tree)
    {
        byte[] sq = SquashImage(tree);
        long p1 = 1 * MiB, p2 = 3 * MiB;
        long room = MiB;
        long slot = Align(sq.Length, Kib64) + room;
        byte[] disk = MbrDisk(p2 + sq.Length,
            new Part(0x0C, p1, 2 * MiB, FakeFat(3, (int)(2 * MiB), "BOOT")),
            new Part(0x83, p2, slot, sq));
        string path = Path.Combine(_dir, "small-root.img");
        File.WriteAllBytes(path, disk);

        ImageDocument doc = ImageDocument.Open(path, _dir);
        try
        {
            AddFile(doc, "/var", "big.bin", Bytes(new Random(10), (int)(3 * MiB)));
            ImageDocument.SaveResult r = doc.Save(path, DiskSave(doc));
            doc = r.Document;
            PartitionInfo root = doc.Container!.Partition;
            long fsEnd = Align(doc.SquashFs!.BytesUsed, Kib64);
            Check(r.Warnings.Any(w => w.Contains("enlarged")), "the enlarged partition was not reported");
            Check(root.Length > slot && root.Length % MiB == 0 && root.Offset == p2, $"new partition size {root.Length}");
            Check(root.Length - fsEnd >= room, "the room behind the filesystem shrank");
            Check(doc.Tree.Resolve("/var/big.bin") is { Inode.Size: 3 * MiB }, "the added file is missing");

            byte[] after = File.ReadAllBytes(path);
            int field = 446 + 16 + 12;
            SameBytes(disk.AsSpan(0, field), after.AsSpan(0, field), "partition table in front of the size field");
            SameBytes(disk.AsSpan(field + 4, (int)p2 - field - 4), after.AsSpan(field + 4, (int)p2 - field - 4), "data in front of the root partition");
            Check(BinaryPrimitives.ReadUInt32LittleEndian(after.AsSpan(field)) == root.Length / 512, "the size field of the partition table is wrong");
            Check(after.Length <= p2 + root.Length, "the file is longer than the disk it describes");
        }
        finally
        {
            doc.Dispose();
        }
    }

    private static void DiskExt(FsTree tree, string reference)
    {
        long p1 = 4 * MiB, p2 = 8 * MiB, slot = 40 * MiB;
        var ext = new SaveOptions { Format = ImageFormat.Ext, Ext = new ExtOptions { SizeBytes = slot, Label = "rootfs", Timestamp = 1700000000 } };
        var ms = new MemoryStream();
        ImageDocument.WriteTo(tree, ms, ext);
        byte[] disk = MbrDisk(p2 + slot,
            new Part(0x0C, p1, 4 * MiB, FakeFat(4, (int)(4 * MiB), "bootfs")),
            new Part(0x83, p2, slot, ms.ToArray()));
        string path = Path.Combine(_dir, "raspi-like.img.xz");
        WritePacked(path, disk, ArchiveCompression.Xz);

        ImageDocument doc = ImageDocument.Open(path, _dir);
        try
        {
            PartitionInfo root = doc.Container?.Partition ?? throw new TestFailure("the partition table was not recognised");
            Check(root.Number == 2 && root.Content == PartitionContent.Ext && root.ContentName == "ext4" && root.Label == "rootfs", "root partition: " + root);
            Check(doc.Format == ImageFormat.Ext && doc.OuterCompression == ArchiveCompression.Xz, "wrong detection");
            Same(Normalize(reference, ext), Normalize(Program.DumpToString(doc.Tree), ext), "tree of the ext4 partition");
            Guid uuid = doc.Ext!.Uuid;

            AddFile(doc, "/etc", "hostname.new", Encoding.ASCII.GetBytes("board\n"));
            string expected = Program.DumpToString(doc.Tree);
            SaveOptions o = DiskSave(doc);
            o.Tar.XzEffort = 0;
            ImageDocument.SaveResult r = doc.Save(path, o);
            doc = r.Document;
            Check(doc.Container is { Partition.Number: 2 } && doc.OuterCompression == ArchiveCompression.Xz, "the saved file is not an xz compressed disk image");
            Check(doc.Ext!.SizeBytes == slot && doc.Ext.Uuid == uuid && doc.Ext.Label == "rootfs", "the filesystem did not keep its size, UUID and label");
            Same(expected, Program.DumpToString(doc.Tree), "tree after saving");
            byte[] after = Unpacked(path);
            Check(after.Length == disk.Length, "the disk changed its size");
            SameBytes(disk.AsSpan(0, (int)p2), after.AsSpan(0, (int)p2), "partition table and boot partition");

            // More content than the partition holds: the last partition and the filesystem grow together.
            AddFile(doc, "/var", "big.bin", Bytes(new Random(11), (int)(34 * MiB)));
            r = doc.Save(Path.Combine(_dir, "raspi-like-grown.img.zst"), DiskSave(doc));
            doc = r.Document;
            Check(doc.OuterCompression == ArchiveCompression.Zstd, "a .zst name did not give a zstd compressed image");
            root = doc.Container!.Partition;
            Check(r.Warnings.Any(w => w.Contains("enlarged")), "the enlarged partition was not reported");
            Check(root.Length > slot && root.Length % MiB == 0 && doc.Ext!.SizeBytes <= root.Length, "the partition does not hold the filesystem");
            Check(doc.Ext!.FreeBlocks * doc.Ext.BlockSize >= 3 * MiB, "no free space was left in the enlarged filesystem");
            Check(doc.Tree.Resolve("/var/big.bin") is { Inode.Size: 34 * MiB }, "the added file is missing");

            // A different filesystem in the same partition is still a disk image.
            doc.Tree.Remove(doc.Tree.Resolve("/var/big.bin")!);
            o = doc.CreateSaveOptions(ImageFormat.SquashFs, new SaveOptions { Verify = VerifyLevel.Structure, TempDirectory = _dir });
            o.SquashFs = new SquashFsOptions { Compression = SquashCompression.Lz4 };
            doc = doc.Save(Path.Combine(_dir, "raspi-like-squash.img"), o).Document;
            Check(doc.Container is { Partition: { Number: 2, Content: PartitionContent.SquashFs } } && doc.Format == ImageFormat.SquashFs,
                "the converted filesystem is not in the partition");
        }
        finally
        {
            doc.Dispose();
        }
    }

    private static void DiskGpt(FsTree tree)
    {
        byte[] sq = SquashImage(tree, SquashCompression.Zstd);
        long p1 = 1 * MiB, p2 = 3 * MiB;
        long slot = Align(sq.Length, MiB) + MiB;
        long length = p2 + slot + MiB;
        byte[] disk = GptDisk(length,
            (128, new Guid("21686148-6449-6E6F-744E-656564454649"), "", new Part(0, 34 * 512, 478 * 512, Bytes(new Random(12), 478 * 512))),
            (1, new Guid("C12A7328-F81F-11D2-BA4B-00A0C93EC93B"), "kernel", new Part(0, p1, 2 * MiB, FakeFat(5, (int)(2 * MiB), "EFI"))),
            (2, new Guid("0FC63DAF-8483-4772-8E79-3D69D8477DE4"), "rootfs", new Part(0, p2, slot, sq)));
        string path = Path.Combine(_dir, "gpt.img");
        File.WriteAllBytes(path, disk);

        ImageDocument doc = ImageDocument.Open(path, _dir);
        try
        {
            DiskContainer c = doc.Container ?? throw new TestFailure("the GPT was not recognised");
            Check(c.Table.Scheme == PartitionScheme.Gpt && c.Table.Partitions.Count == 3, "wrong partition list");
            Check(c.Partition is { Number: 2, Name: "rootfs", TypeName: "Linux filesystem" } && c.Partition.Offset == p2 && c.Partition.Length == slot, "root partition: " + c.Partition);
            Check(c.Table.Partitions[0] is { Number: 1, TypeName: "EFI System", Content: PartitionContent.Fat }, "EFI partition: " + c.Table.Partitions[0]);
            Check(c.Table.Partitions[2] is { Number: 128, TypeName: "BIOS boot" } && !c.CanGrow, "BIOS boot partition");

            AddFile(doc, "/etc", "gpt.conf", Encoding.ASCII.GetBytes("x\n"));
            string expected = Program.DumpToString(doc.Tree);
            doc = doc.Save(path, DiskSave(doc)).Document;
            Same(expected, Program.DumpToString(doc.Tree), "tree after saving");
            byte[] after = File.ReadAllBytes(path);
            Check(after.Length == disk.Length, "the disk changed its size");
            SameBytes(disk.AsSpan(0, (int)p2), after.AsSpan(0, (int)p2), "GPT and partitions in front of the root partition");
            SameBytes(disk.AsSpan((int)(p2 + slot)), after.AsSpan((int)(p2 + slot)), "backup GPT");

            AddFile(doc, "/var", "big.bin", Bytes(new Random(13), (int)(3 * MiB)));
            try
            {
                doc.Save(path, DiskSave(doc));
                throw new TestFailure("a filesystem larger than its GPT partition was written");
            }
            catch (FsException ex)
            {
                Check(ex.Message.Contains("GPT"), "unhelpful message: " + ex.Message);
            }
            SameBytes(after, File.ReadAllBytes(path), "disk image after a refused save");
        }
        finally
        {
            doc.Dispose();
        }
    }

    private static void DiskChoice(FsTree tree)
    {
        byte[] sq = SquashImage(tree, SquashCompression.Lz4);
        using var small = new FsTree();
        small.CreateFile(small.Root, "vmlinuz", new MemoryFileData(Bytes(new Random(14), 200_000)), 0x1A4, 0, 0, 1700000000);
        var bootMs = new MemoryStream();
        ImageDocument.WriteTo(small, bootMs, new SaveOptions { Format = ImageFormat.Ext, Ext = new ExtOptions { Kind = ExtKind.Ext2, SizeBytes = 2 * MiB, BlockSize = 1024, Label = "boot" } });

        // 1: ext2 boot, 2: extended { 5: SquashFS root, 6: noise }, 3: swap signature.
        long p1 = MiB, ext = 3 * MiB, p5 = ext + MiB;
        long slot5 = Align(sq.Length, MiB) + MiB;
        long ebr2 = p5 + slot5, p6 = ebr2 + MiB;
        long p3 = p6 + MiB;
        byte[] swap = new byte[4096];
        "SWAPSPACE2"u8.CopyTo(swap.AsSpan(4086));
        byte[] disk = MbrDisk(p3 + MiB,
            new Part(0x83, p1, 2 * MiB, bootMs.ToArray(), Boot: true),
            new Part(0x05, ext, p3 - ext),
            new Part(0x82, p3, MiB, swap));
        byte[] e1 = new byte[512], e2 = new byte[512];
        WriteMbrEntry(e1, 0, new Part(0x83, p5, slot5), relativeTo: ext);
        WriteMbrEntry(e1, 1, new Part(0x05, ebr2, 2 * MiB), relativeTo: ext);
        WriteMbrEntry(e2, 0, new Part(0x83, p6, MiB), relativeTo: ebr2);
        e1[510] = e2[510] = 0x55; e1[511] = e2[511] = 0xAA;
        e1.CopyTo(disk, ext);
        e2.CopyTo(disk, ebr2);
        sq.CopyTo(disk, p5);
        Bytes(new Random(15), (int)MiB).CopyTo(disk, p6);
        string path = Path.Combine(_dir, "choice.img");
        File.WriteAllBytes(path, disk);

        using (ImageDocument doc = ImageDocument.Open(path, _dir))
        {
            PartitionTable t = doc.Container!.Table;
            Check(string.Join(",", t.Partitions.Select(p => p.Number)) == "1,3,5,6", "partition numbers: " + string.Join(",", t.Partitions.Select(p => p.Number)));
            Check(t.Find(1) is { Content: PartitionContent.Ext, ContentName: "ext2", Label: "boot" }, "boot partition: " + t.Find(1));
            Check(t.Find(3) is { Content: PartitionContent.Swap, IsLast: true }, "swap partition: " + t.Find(3));
            Check(t.Find(5) is { Content: PartitionContent.SquashFs } l && l.Offset == p5 && l.Length == slot5, "logical partition: " + t.Find(5));
            Check(t.Find(6) is { Content: PartitionContent.Unknown } n && n.Offset == p6, "second logical partition: " + t.Find(6));
            Check(doc.Container.Partition.Number == 5 && doc.Format == ImageFormat.SquashFs, "the largest filesystem is not the default");
        }

        int asked = 0;
        using (ImageDocument doc = ImageDocument.Open(path, _dir, choosePartition: t => { asked++; return t.Find(1); }))
            Check(asked == 1 && doc.Format == ImageFormat.Ext && doc.Container!.Partition.Number == 1 && doc.Tree.Resolve("/vmlinuz") is not null, "the chosen partition was not opened");
        try
        {
            ImageDocument.Open(path, _dir, choosePartition: _ => null).Dispose();
            throw new TestFailure("a cancelled choice still opened the image");
        }
        catch (OperationCanceledException)
        {
        }
        ExpectFs(() => ImageDocument.Open(path, _dir, partition: 3).Dispose());
        ExpectFs(() => ImageDocument.Open(path, _dir, partition: 9).Dispose());

        // The boot partition is edited and saved; the root filesystem next to it must not notice.
        ImageDocument boot = ImageDocument.Open(path, _dir, partition: 1);
        try
        {
            AddFile(boot, "/", "cmdline.txt", Encoding.ASCII.GetBytes("console=ttyPS0,115200 root=/dev/mmcblk0p5\n"));
            boot = boot.Save(path, DiskSave(boot)).Document;
            Check(boot.Container!.Partition.Number == 1 && boot.Tree.Resolve("/cmdline.txt") is not null && boot.Ext!.SizeBytes == 2 * MiB, "the boot partition was not saved in place");
            byte[] after = File.ReadAllBytes(path);
            SameBytes(disk.AsSpan(0, (int)p1), after.AsSpan(0, (int)p1), "partition table");
            SameBytes(disk.AsSpan((int)(p1 + 2 * MiB)), after.AsSpan((int)(p1 + 2 * MiB)), "everything behind the boot partition");
        }
        finally
        {
            boot.Dispose();
        }
    }

    private static void DiskFuzz()
    {
        using var small = new FsTree(0x1ED, 1700000000);
        var rnd = new Random(4242);
        for (int i = 0; i < 12; i++)
            small.CreateFile(small.Root, "file" + i, new MemoryFileData(Bytes(rnd, 500 + i * 900, i % 2 == 0)), 0x1A4, 0, 0, 1700000000 + i);
        byte[] sq = SquashImage(small, SquashCompression.Lz4);
        long slot = Align(sq.Length, MiB);

        // MBR with a logical partition, and GPT: the tables are small, so every byte of them gets its turn.
        long ext = 2 * MiB, p5 = ext + 64 * 1024;
        byte[] mbr = MbrDisk(p5 + slot,
            new Part(0x0C, MiB, MiB, FakeFat(20, 65536, "BOOT"), Boot: true),
            new Part(0x0F, ext, 64 * 1024 + slot));
        byte[] ebr = new byte[512];
        WriteMbrEntry(ebr, 0, new Part(0x83, p5, slot), relativeTo: ext);
        ebr[510] = 0x55; ebr[511] = 0xAA;
        ebr.CopyTo(mbr, ext);
        sq.CopyTo(mbr, p5);
        byte[] gpt = GptDisk(2 * MiB + slot + 64 * 1024,
            (1, new Guid("C12A7328-F81F-11D2-BA4B-00A0C93EC93B"), "boot", new Part(0, MiB, MiB, FakeFat(21, 65536, "EFI"))),
            (2, new Guid("0FC63DAF-8483-4772-8E79-3D69D8477DE4"), "rootfs", new Part(0, 2 * MiB, slot, sq)));

        var variants = new (string Name, byte[] Disk, (int Start, int Length)[] Tables)[]
        {
            ("mbr", mbr, new[] { (440, 72), ((int)ext + 440, 72), ((int)p5, 96) }),
            ("gpt", gpt, new[] { (440, 72), (512, 92), (1024, 256), ((int)(2 * MiB), 96) }),
        };
        string path = Path.Combine(_dir, "fuzz-disk.img");
        int iterations = int.TryParse(Environment.GetEnvironmentVariable("FSFORGE_FUZZ"), out int custom) ? custom * 4 : 600;
        var failures = new List<string>();
        int opened = 0;
        foreach ((string name, byte[] good, (int Start, int Length)[] tables) in variants)
        {
            File.WriteAllBytes(path, good);
            using (ImageDocument intact = ImageDocument.Open(path, _dir, searchEmbedded: false))
                Check(intact.Container is not null && intact.Tree.Root.Children.Count == 12, name + ": the intact disk does not open");

            for (int iter = 0; iter < iterations; iter++)
            {
                byte[] bad = (byte[])good.Clone();
                if (iter % 7 == 6)
                {
                    bad = bad.AsSpan(0, rnd.Next(1, bad.Length)).ToArray();
                }
                else
                {
                    int hits = 1 + rnd.Next(iter % 3 == 0 ? 1 : 6);
                    for (int h = 0; h < hits; h++)
                    {
                        (int start, int length) = tables[rnd.Next(tables.Length)];
                        int at = start + rnd.Next(length);
                        // Whole fields at a time now and then: huge sector numbers are the interesting values.
                        if (rnd.Next(4) == 0 && at + 8 <= bad.Length) rnd.NextBytes(bad.AsSpan(at, rnd.Next(2) == 0 ? 4 : 8));
                        else bad[at] = rnd.Next(3) == 0 ? (byte)0xFF : (byte)rnd.Next(256);
                    }
                }
                File.WriteAllBytes(path, bad);
                try
                {
                    ImageDocument.Detect(path);
                    using ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: iter % 2 == 0);
                    opened++;
                    foreach (FsNode n in doc.Tree.Walk())
                    {
                        if (n.Inode.Data is null) continue;
                        using Stream data = n.Inode.Data.OpenRead();
                        data.CopyTo(Stream.Null);
                    }
                    // Whatever was opened must also be writable without surprises.
                    if (iter % 5 == 0)
                        doc.Save(Path.Combine(_dir, "fuzz-disk-out.img"), doc.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Structure, TempDirectory = _dir })).Document.Dispose();
                }
                catch (FsException ex) when (ex is not FsUnexpectedException)
                {
                    // The expected, clean outcome.
                }
                catch (Exception ex)
                {
                    if (failures.Count < 3)
                    {
                        File.Copy(path, Path.Combine(_dir, $"crash-disk-{name}-{iter}.img"), true);
                        Console.WriteLine($"        {name} #{iter}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace?.Split('\n').Take(5).Aggregate((x, y) => x + "\n" + y)}");
                    }
                    failures.Add($"{name} #{iter}");
                }
            }
        }
        File.Delete(path);
        Check(failures.Count == 0, $"damaged disk images caused {failures.Count} unexpected exceptions");
        Check(opened > iterations / 4, $"only {opened} of the damaged disks opened - the test does not reach the code behind the tables");
    }

    private static void DiskScan(FsTree tree)
    {
        byte[] sq = SquashImage(tree, SquashCompression.Lz4);
        byte[] blob = new byte[0x20000 + sq.Length];
        new Random(16).NextBytes(blob.AsSpan(0, 0x20000));
        blob[510] = 0;                              // certainly not a partition table
        sq.CopyTo(blob, 0x20000);
        string path = Path.Combine(_dir, "firmware.bin");
        File.WriteAllBytes(path, blob);

        using ImageDocument doc = ImageDocument.Open(path, _dir);
        Check(doc.Container is null && doc.EmbeddedOffset == 0x20000 && doc.SavesFilesystemOnly, "a firmware blob was taken for a disk image");
        Check(doc.Warnings.Any(w => w.Contains("filesystem alone")), "the limitation was not reported");
        ExpectFs(() => ImageDocument.Open(path, _dir, partition: 1).Dispose());
    }
}
