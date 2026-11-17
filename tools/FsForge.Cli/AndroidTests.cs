using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FsForge.Core;
using FsForge.Core.Android;
using FsForge.Core.Cpio;
using FsForge.Core.Erofs;
using FsForge.Core.Ext;
using FsForge.Core.Images;
using FsForge.Core.Tar;

namespace FsForge.Cli;

/// <summary>
/// Android containers. The images are built here from the specifications (liblp, mkbootimg, AVB), independently
/// of the code under test: super.img with several partitions, extents, groups and slots, raw and sparse; boot
/// images of header versions 0, 2, 3 and 4 with an AVB footer; vendor_boot v4 with two ramdisk fragments.
/// Each is opened, a part is changed and saved, and everything else must stay byte for byte.
/// </summary>
internal static partial class SelfTest
{
    private static void AndroidTests()
    {
        Test("super: partitions, extents, groups, slots and their copies", SuperRead);
        Test("super: a partition changed in place and grown into the free space", SuperWrite);
        Test("super: sparse image in, sparse image out; a partition saved alone", SuperSparse);
        Test("super: limits of the device and of a group", SuperLimits);
        Test("boot: header versions 0, 2, 3, 4; SHA-1 id; sections kept", BootVersions);
        Test("boot: AVB footer and vbmeta kept at the end of the partition", BootAvb);
        Test("vendor_boot v4: two ramdisk fragments and their table", VendorBootV4);
        Test("android: corrupted containers are rejected cleanly", AndroidFuzz);
    }

    // ------------------------------------------------------------ super

    private sealed record LpPart(string Name, uint Group, (ulong Sectors, uint Type, ulong Target)[] Extents, uint Attributes = 1);

    /// <summary>A super image as liblp writes it: reserved space, two geometries, primary and backup metadata per slot.</summary>
    private static byte[] BuildSuper(long deviceSize, uint metadataMax, uint slots, LpPart[] parts, (string Name, ulong Max)[] groups,
        Dictionary<ulong, byte[]> data, ushort minor = 0)
    {
        byte[] img = new byte[deviceSize];
        byte[] g = new byte[52];
        BinaryPrimitives.WriteUInt32LittleEndian(g, SuperImage.GeometryMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(g.AsSpan(4), 52);
        BinaryPrimitives.WriteUInt32LittleEndian(g.AsSpan(40), metadataMax);
        BinaryPrimitives.WriteUInt32LittleEndian(g.AsSpan(44), slots);
        BinaryPrimitives.WriteUInt32LittleEndian(g.AsSpan(48), 4096);
        SHA256.HashData(g).CopyTo(g, 8);
        g.CopyTo(img, 4096);
        g.CopyTo(img, 8192);

        var pt = new List<byte>();
        var et = new List<byte>();
        uint extentIndex = 0;
        foreach (LpPart p in parts)
        {
            byte[] e = new byte[52];
            Encoding.ASCII.GetBytes(p.Name).CopyTo(e, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(36), p.Attributes);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(40), extentIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(44), (uint)p.Extents.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(48), p.Group);
            pt.AddRange(e);
            foreach ((ulong sectors, uint type, ulong target) in p.Extents)
            {
                byte[] x = new byte[24];
                BinaryPrimitives.WriteUInt64LittleEndian(x, sectors);
                BinaryPrimitives.WriteUInt32LittleEndian(x.AsSpan(8), type);
                BinaryPrimitives.WriteUInt64LittleEndian(x.AsSpan(12), target);
                et.AddRange(x);
                extentIndex++;
            }
        }
        var gt = new List<byte>();
        foreach ((string name, ulong max) in groups)
        {
            byte[] e = new byte[48];
            Encoding.ASCII.GetBytes(name).CopyTo(e, 0);
            BinaryPrimitives.WriteUInt64LittleEndian(e.AsSpan(40), max);
            gt.AddRange(e);
        }
        byte[] bd = new byte[64];
        ulong first = (ulong)(4096 + (4096 + metadataMax * slots) * 2) / 512;
        BinaryPrimitives.WriteUInt64LittleEndian(bd, first);
        BinaryPrimitives.WriteUInt32LittleEndian(bd.AsSpan(8), 1 << 20);
        BinaryPrimitives.WriteUInt64LittleEndian(bd.AsSpan(16), (ulong)deviceSize);
        Encoding.ASCII.GetBytes("super").CopyTo(bd, 24);
        byte[] tables = [.. pt, .. et, .. gt, .. bd];

        int headerSize = minor >= 2 ? 256 : 128;
        byte[] h = new byte[headerSize];
        BinaryPrimitives.WriteUInt32LittleEndian(h, SuperImage.HeaderMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(4), 10);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(6), minor);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), (uint)headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(44), (uint)tables.Length);
        SHA256.HashData(tables).CopyTo(h, 48);
        void Desc(int at, int offset, int count, int size)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(at), (uint)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(at + 4), (uint)count);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(at + 8), (uint)size);
        }
        Desc(80, 0, parts.Length, 52);
        Desc(92, pt.Count, (int)extentIndex, 24);
        Desc(104, pt.Count + et.Count, groups.Length, 48);
        Desc(116, pt.Count + et.Count + gt.Count, 1, 64);
        SHA256.HashData(h).CopyTo(h, 12);
        byte[] meta = [.. h, .. tables];
        for (uint s = 0; s < slots; s++)
        {
            meta.CopyTo(img, 12288 + metadataMax * s);
            meta.CopyTo(img, 12288 + metadataMax * (slots + s));
        }
        foreach ((ulong sector, byte[] bytes) in data) bytes.CopyTo(img, (long)sector * 512);
        return img;
    }

    private static FsTree SmallTree(int seed, int files)
    {
        var t = new FsTree(0x1ED, 1700000000);
        FsNode etc = t.CreateDirectory(t.Root, "etc", 0x1ED, 0, 0, 1700000001);
        var rnd = new Random(seed);
        for (int i = 0; i < files; i++)
            t.CreateFile(i % 2 == 0 ? t.Root : etc, $"file{i}", new MemoryFileData(Bytes(rnd, 500 + i * 997, i % 3 == 0)), 0x1A4, (uint)i, 0, 1700000000 + i);
        t.CreateSymlink(etc, "link", "../file0");
        t.CreateSpecial(t.Root, "null", FsNodeType.CharDevice, 1, 3, 0x1B6);
        return t;
    }

    /// <summary>A device of 64 MiB: system_a (ext4) at 1 MiB, vendor_a (EROFS) in two extents, an empty system_b and a zero extent.</summary>
    private static (byte[] Image, byte[] System, byte[] Vendor) SampleSuper(uint slots = 2, ushort minor = 0)
    {
        byte[] system = WriteImage(SmallTree(1, 30), new SaveOptions { Format = ImageFormat.Ext, Ext = new ExtOptions { SizeBytes = 4 << 20, Journal = false } });
        byte[] vendor = WriteImage(SmallTree(2, 12), new SaveOptions { Format = ImageFormat.Erofs, Erofs = new ErofsOptions { Compression = ErofsCompression.None } });
        vendor = vendor.Concat(new byte[(4096 - vendor.Length % 4096) % 4096]).ToArray();
        ulong sysAt = 2048, sysSectors = (ulong)system.Length / 512;              // 1 MiB
        ulong half = (ulong)vendor.Length / 512 / 2 / 8 * 8;
        ulong v1 = 20 << 11, v2 = 30 << 11;                                       // 20 MiB and 30 MiB
        var data = new Dictionary<ulong, byte[]>
        {
            [sysAt] = system,
            [v1] = vendor.AsSpan(0, (int)half * 512).ToArray(),
            [v2] = vendor.AsSpan((int)half * 512).ToArray(),
        };
        LpPart[] parts =
        {
            new("system_a", 1, new[] { (sysSectors, 0u, sysAt) }),
            new("vendor_a", 1, new[] { (half, 0u, v1), ((ulong)vendor.Length / 512 - half, 0u, v2) }),
            new("system_b", 2, Array.Empty<(ulong, uint, ulong)>()),
            new("zeros", 0, new[] { (8ul, 1u, 0ul) }, 0),
        };
        return (BuildSuper(64 << 20, 65536, slots, parts, new[] { ("default", 0ul), ("main_a", 40ul << 20), ("main_b", 40ul << 20) }, data, minor), system, vendor);
    }

    private static void SuperRead()
    {
        (byte[] img, byte[] system, byte[] vendor) = SampleSuper(minor: 2);
        string path = WriteFile("super.img", img);
        Check(ImageDocument.Detect(path) == ImageFormat.Ext, "detected as " + ImageDocument.Detect(path));
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
        {
            Check(doc.Format == ImageFormat.Ext && doc.Android is { Part.Name: "system_a", Image: SuperImage { SlotCount: 2, MinorVersion: 2 } }, "opened part");
            Check(doc.Warnings.Count == 0, "warnings: " + string.Join("; ", doc.Warnings));
            IReadOnlyList<AndroidPart> parts = doc.Android!.Image.Parts;
            Check(parts.Count == 4 && parts[1] is { Content: PartitionContent.Erofs, Detail: "group main_a, read-only" } && parts[2].Content == PartitionContent.Empty
                  && parts[3] is { IsReadable: false, Length: 4096 }, "parts: " + string.Join("; ", parts));
            var ext = new SaveOptions { Format = ImageFormat.Ext };            // ext adds lost+found
            Same(Normalize(Program.DumpToString(SmallTree(1, 30)), ext), Normalize(Program.DumpToString(doc.Tree), ext), "system_a");
        }
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false, volume: "VENDOR_A"))
        {
            Check(doc.Format == ImageFormat.Erofs && doc.Android!.Part.Name == "vendor_a", "vendor_a by name");
            Same(Program.DumpToString(SmallTree(2, 12)), Program.DumpToString(doc.Tree), "vendor_a across two extents");
        }
        foreach (string bad in new[] { "system_b", "nosuch" })
            ExpectFs(() => ImageDocument.Open(path, _dir, searchEmbedded: false, volume: bad).Dispose());

        // Damaged primary copies: the backups are used, with a warning.
        img[4096 + 20] ^= 1;
        img[12288 + 200] ^= 1;
        using (ImageDocument doc = ImageDocument.Open(WriteFile("super-damaged.img", img), _dir, searchEmbedded: false))
            Check(doc.Warnings.Any(w => w.Contains("primary geometry")) && doc.Warnings.Any(w => w.Contains("another copy")) && doc.Tree.Resolve("/etc/link") is not null,
                "backup copies: " + string.Join("; ", doc.Warnings));
    }

    private static void SuperWrite()
    {
        (byte[] img, byte[] system, byte[] vendor) = SampleSuper();
        string path = WriteFile("superw.img", img);
        var save = new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir };

        // Changed in place: system_a keeps its extent, vendor_a and the metadata area outside the slots stay.
        ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
        try
        {
            doc.Tree.Remove(doc.Tree.Resolve("/file0")!);
            SaveOptions o = doc.CreateSaveOptions(null, save);
            Check(doc.WritesContainer(o), "the super image is not written again");
            doc = doc.Save(path, o).Document;
            Check(doc.Android is { Part.Name: "system_a" } && doc.Tree.Resolve("/file0") is null, "saved in place");
            var sup = (SuperImage)doc.Android!.Image;
            Check(sup.Partitions[0].Extents is [{ TargetData: 2048 }] && sup.Partitions[1].Extents.Count == 2, "extents moved");
        }
        finally
        {
            doc.Dispose();
        }
        byte[] after = File.ReadAllBytes(path);
        Check(after.Length == img.Length, "length of the super image");
        Check(after.AsSpan(20 << 20, vendor.Length / 2).SequenceEqual(img.AsSpan(20 << 20, vendor.Length / 2)), "vendor_a was touched");
        // Every slot, primary and backup, holds the same valid metadata.
        byte[] meta = after.AsSpan(12288, 65536).ToArray();
        for (int i = 1; i < 4; i++) Check(after.AsSpan(12288 + i * 65536, 65536).SequenceEqual(meta), $"metadata copy {i}");

        // Grown beyond its room: vendor_a gets new extents in the free space, aligned to 1 MiB.
        doc = ImageDocument.Open(path, _dir, searchEmbedded: false, volume: "vendor_a");
        try
        {
            doc.Tree.CreateFile(doc.Tree.Root, "big", new MemoryFileData(Bytes(new Random(9), 3 << 20)));
            ImageDocument.SaveResult r = doc.Save(path, doc.CreateSaveOptions(null, save));
            doc = r.Document;
            var sup = (SuperImage)doc.Android!.Image;
            List<SuperImage.LpExtent> ext = sup.Partitions[1].Extents;
            Check(ext.All(e => e.TargetData % 2048 == 0) && ext.Sum(e => (long)e.Sectors) * 512 >= 3 << 20, "new extents: " + string.Join(", ", ext));
            Check(doc.Tree.Resolve("/big")!.Inode.Size == 3 << 20, "the grown partition");
        }
        finally
        {
            doc.Dispose();
        }
        using (ImageDocument sysDoc = ImageDocument.Open(path, _dir, searchEmbedded: false, volume: "system_a"))
            Check(sysDoc.Tree.Resolve("/file0") is null && sysDoc.Tree.Resolve("/file2") is not null, "system_a after vendor_a grew");
    }

    /// <summary>An Android sparse file: raw chunks for data, DONT_CARE for zeros.</summary>
    private static byte[] Sparse(byte[] raw)
    {
        var chunks = new List<byte[]>();
        int blocks = raw.Length / 4096;
        for (int b = 0; b < blocks;)
        {
            bool zero = !raw.AsSpan(b * 4096, 4096).ContainsAnyExcept((byte)0);
            int n = 1;
            while (b + n < blocks && !raw.AsSpan((b + n) * 4096, 4096).ContainsAnyExcept((byte)0) == zero) n++;
            byte[] ch = new byte[12 + (zero ? 0 : n * 4096)];
            BinaryPrimitives.WriteUInt16LittleEndian(ch, (ushort)(zero ? 0xCAC3 : 0xCAC1));
            BinaryPrimitives.WriteUInt32LittleEndian(ch.AsSpan(4), (uint)n);
            BinaryPrimitives.WriteUInt32LittleEndian(ch.AsSpan(8), (uint)ch.Length);
            if (!zero) raw.AsSpan(b * 4096, n * 4096).CopyTo(ch.AsSpan(12));
            chunks.Add(ch);
            b += n;
        }
        byte[] head = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(head, AndroidSparseSource.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(8), 28);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(16), (uint)blocks);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(20), (uint)chunks.Count);
        return head.Concat(chunks.SelectMany(c => c)).ToArray();
    }

    private static void SuperSparse()
    {
        (byte[] img, byte[] system, _) = SampleSuper(slots: 1);
        string path = WriteFile("super-sparse.img", Sparse(img));
        ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
        try
        {
            Check(doc.Android?.Image is SuperImage { Sparse: true } && !doc.SavesFilesystemOnly && doc.Warnings.Count == 0, "a sparse super image");
            doc.Tree.CreateFile(doc.Tree.Root, "new", new MemoryFileData(Encoding.ASCII.GetBytes("x")));
            string again = Path.Combine(_dir, "super-sparse2.img");
            doc = doc.Save(again, doc.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir })).Document;
            byte[] written = File.ReadAllBytes(again);
            Check(BinaryPrimitives.ReadUInt32LittleEndian(written) == AndroidSparseSource.Magic && written.Length < img.Length / 4, "not written as a sparse file");
            Check(doc.Android?.Image is SuperImage { Sparse: true } && doc.Tree.Resolve("/new") is not null, "the sparse image read back");

            // The filesystem alone, for "fastboot flash system_a".
            SaveOptions alone = doc.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir });
            alone.KeepContainer = false;
            Check(!doc.WritesContainer(alone), "KeepContainer=false still writes the container");
            string fsPath = Path.Combine(_dir, "system_a.img");
            doc = doc.Save(fsPath, alone).Document;
            Check(doc.Android is null && doc.Format == ImageFormat.Ext && doc.FilePath == fsPath, "the partition saved alone");
        }
        finally
        {
            doc.Dispose();
        }
    }

    private static void SuperLimits()
    {
        (byte[] img, _, _) = SampleSuper();
        string path = WriteFile("super-limits.img", img);
        foreach ((int mib, string text) in new[] { (45, "maximum"), (70, "does not fit") })
        {
            using ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false, volume: "vendor_a");
            doc.Tree.CreateFile(doc.Tree.Root, "huge", new MemoryFileData(Bytes(new Random(mib), mib << 20)));
            SaveOptions o = doc.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.None, TempDirectory = _dir });
            try
            {
                doc.Save(Path.Combine(_dir, "super-limits2.img"), o).Document.Dispose();
                Check(false, $"{mib} MiB were accepted");
            }
            catch (FsException ex)
            {
                Check(ex.Message.Contains(text), $"{mib} MiB: {ex.Message}");
            }
        }
    }

    // ------------------------------------------------------------- boot

    private static byte[] Ramdisk(FsTree tree, ArchiveCompression c) =>
        WriteImage(tree, new SaveOptions { Format = ImageFormat.Cpio, Cpio = new CpioOptions { Compression = c, GzipLevel = 6 } });

    private static long PageAlign(long n, int page) => (n + page - 1) / page * page;

    /// <summary>A boot image as mkbootimg makes it (versions 0 to 4).</summary>
    private static byte[] BuildBoot(int version, int page, byte[] kernel, byte[] ramdisk, byte[] second, byte[] dtbo, byte[] dtb)
    {
        var sections = version >= 3 ? new List<byte[]> { kernel, ramdisk } : new List<byte[]> { kernel, ramdisk, second };
        if (version is 1 or 2) sections.Add(dtbo);
        if (version == 2) sections.Add(dtb);
        if (version >= 3) page = 4096;
        byte[] h = new byte[page];
        Encoding.ASCII.GetBytes("ANDROID!").CopyTo(h, 0);
        if (version >= 3)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), (uint)kernel.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), (uint)ramdisk.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(20), version == 4 ? 1584u : 1580u);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), (uint)version);
            Encoding.ASCII.GetBytes("console=ttyMSM0").CopyTo(h, 44);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), (uint)kernel.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), (uint)ramdisk.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24), (uint)second.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(36), (uint)page);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), (uint)version);
            Encoding.ASCII.GetBytes("console=ttyS0").CopyTo(h, 64);
            if (version >= 1)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(1632), (uint)dtbo.Length);
                long at = page + PageAlign(kernel.Length, page) + PageAlign(ramdisk.Length, page) + PageAlign(second.Length, page);
                BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(1636), (ulong)at);
                BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(1644), version == 2 ? 1660u : 1648u);
            }
            if (version == 2) BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(1648), (uint)dtb.Length);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            foreach (byte[] s in sections)
            {
                sha.AppendData(s);
                byte[] len = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)s.Length);
                sha.AppendData(len);
            }
            sha.GetHashAndReset().CopyTo(h, 576);
        }
        var img = new List<byte>(h);
        foreach (byte[] s in sections)
        {
            img.AddRange(s);
            img.AddRange(new byte[PageAlign(s.Length, page) - s.Length]);
        }
        return img.ToArray();
    }

    private static void BootVersions()
    {
        var rnd = new Random(21);
        byte[] kernel = Bytes(rnd, 100000), second = Bytes(rnd, 3000), dtbo = Bytes(rnd, 2000), dtb = Bytes(rnd, 5000);
        FsTree tree = SmallTree(3, 10);
        foreach ((int version, int page, ArchiveCompression c) in new[] { (0, 2048, ArchiveCompression.Gzip), (2, 2048, ArchiveCompression.Lz4), (3, 4096, ArchiveCompression.Lz4), (4, 4096, ArchiveCompression.None) })
        {
            byte[] img = BuildBoot(version, page, kernel, Ramdisk(tree, c), second, dtbo, dtb).Concat(Encoding.ASCII.GetBytes("SEANDROIDENFORCE")).ToArray();
            string path = WriteFile($"boot{version}.img", img);
            Check(ImageDocument.Detect(path) == ImageFormat.Cpio, $"v{version}: detected as {ImageDocument.Detect(path)}");
            ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
            try
            {
                Check(doc.Format == ImageFormat.Cpio && doc.Cpio!.Compression == c && doc.Android is { Part.Name: "ramdisk", Image: BootImage { HeaderVersion: var hv } } && hv == version,
                    $"v{version}: opened");
                Same(Program.DumpToString(tree), Program.DumpToString(doc.Tree), $"v{version} ramdisk");
                doc.Tree.CreateFile(doc.Tree.Root, "added", new MemoryFileData(Bytes(rnd, 20000)));
                SaveOptions o = doc.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir });
                Check(o.Format == ImageFormat.Cpio && o.Cpio.Compression == c && doc.WritesContainer(o), $"v{version}: options");
                doc = doc.Save(path, o).Document;
                Check(doc.Tree.Resolve("/added") is not null && doc.Android is not null, $"v{version}: saved");
            }
            finally
            {
                doc.Dispose();
            }
            // The image as mkbootimg would make it from the same pieces and the new ramdisk, then the vendor trailer.
            byte[] after = File.ReadAllBytes(path);
            int pg = version >= 3 ? 4096 : page;
            uint rdSize = BinaryPrimitives.ReadUInt32LittleEndian(after.AsSpan(version >= 3 ? 12 : 16));
            long rdAt = pg + PageAlign(kernel.Length, pg);
            byte[] newRamdisk = after.AsSpan((int)rdAt, (int)rdSize).ToArray();
            byte[] expected = BuildBoot(version, page, kernel, newRamdisk, second, dtbo, dtb).Concat(Encoding.ASCII.GetBytes("SEANDROIDENFORCE")).ToArray();
            Check(after.AsSpan().SequenceEqual(expected), $"v{version}: the image differs from what mkbootimg makes");
        }
    }

    private static void BootAvb()
    {
        var rnd = new Random(22);
        byte[] boot = BuildBoot(3, 4096, Bytes(rnd, 50000), Ramdisk(SmallTree(4, 6), ArchiveCompression.Lz4), Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>());
        // A partition of 256 KiB: the image, vbmeta at the next 4 KiB, the footer in the last 64 bytes.
        const int partition = 256 << 10;
        long vbmetaAt = boot.Length;
        byte[] vbmeta = Bytes(rnd, 1200);
        byte[] img = new byte[partition];
        boot.CopyTo(img, 0);
        vbmeta.CopyTo(img, vbmetaAt);
        Span<byte> f = img.AsSpan(partition - 64);
        "AVBf"u8.CopyTo(f);
        BinaryPrimitives.WriteUInt32BigEndian(f[4..], 1);
        BinaryPrimitives.WriteUInt64BigEndian(f[12..], (ulong)boot.Length);
        BinaryPrimitives.WriteUInt64BigEndian(f[20..], (ulong)vbmetaAt);
        BinaryPrimitives.WriteUInt64BigEndian(f[28..], (ulong)vbmeta.Length);
        string path = WriteFile("boot-avb.img", img);

        ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
        try
        {
            Check(doc.Android?.Image is BootImage { HasAvbFooter: true }, "AVB footer not recognized");
            doc.Tree.Remove(doc.Tree.Resolve("/file0")!);
            ImageDocument.SaveResult r = doc.Save(path, doc.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir }));
            doc = r.Document;
            Check(r.Warnings.Any(w => w.Contains("Verified Boot")), "no warning about the signature");
        }
        finally
        {
            doc.Dispose();
        }
        byte[] after = File.ReadAllBytes(path);
        Check(after.Length == partition, "the partition size changed");
        Check(after.AsSpan((int)vbmetaAt, vbmeta.Length).SequenceEqual(vbmeta), "vbmeta moved or changed");
        long original = (long)BinaryPrimitives.ReadUInt64BigEndian(after.AsSpan(partition - 64 + 12));
        Check(after.AsSpan(partition - 64, 4).SequenceEqual("AVBf"u8) && original <= vbmetaAt && original % 4096 == 0, "footer: original size " + original);

        // A ramdisk that does not fit before vbmeta is refused.
        doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
        try
        {
            doc.Tree.CreateFile(doc.Tree.Root, "big", new MemoryFileData(Bytes(rnd, 300000)));
            SaveOptions o = doc.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.None, TempDirectory = _dir });
            o.Cpio.Compression = ArchiveCompression.None;
            try
            {
                doc.Save(Path.Combine(_dir, "boot-avb2.img"), o).Document.Dispose();
                Check(false, "a ramdisk larger than the partition was accepted");
            }
            catch (FsException ex)
            {
                Check(ex.Message.Contains("AVB"), ex.Message);
            }
        }
        finally
        {
            doc.Dispose();
        }
    }

    private static void VendorBootV4()
    {
        var rnd = new Random(23);
        byte[] r0 = Ramdisk(SmallTree(5, 5), ArchiveCompression.Lz4), r1 = Ramdisk(SmallTree(6, 7), ArchiveCompression.Gzip);
        byte[] dtb = Bytes(rnd, 9000), bootconfig = Encoding.ASCII.GetBytes("androidboot.hardware=qcom\n");
        const int page = 4096;
        byte[] h = new byte[page];
        Encoding.ASCII.GetBytes("VNDRBOOT").CopyTo(h, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), page);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24), (uint)(r0.Length + r1.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(2096), 2128);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(2100), (uint)dtb.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(2112), 2 * 108);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(2116), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(2120), 108);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(2124), (uint)bootconfig.Length);
        byte[] table = new byte[216];
        void Entry(int i, int size, int offset, int type, string name)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(i * 108), (uint)size);
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(i * 108 + 4), (uint)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(i * 108 + 8), (uint)type);
            Encoding.ASCII.GetBytes(name).CopyTo(table, i * 108 + 12);
        }
        Entry(0, r0.Length, 0, 1, "platform");
        Entry(1, r1.Length, r0.Length, 3, "dlkm");
        byte[] Build(byte[] a, byte[] b, byte[] header, byte[] tbl)
        {
            var img = new List<byte>(header);
            foreach (byte[] s in new[] { a.Concat(b).ToArray(), dtb, tbl, bootconfig })
            {
                img.AddRange(s);
                img.AddRange(new byte[PageAlign(s.Length, page) - s.Length]);
            }
            return img.ToArray();
        }
        string path = WriteFile("vendor_boot.img", Build(r0, r1, h, table));
        ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false, volume: "dlkm");
        try
        {
            Check(doc.Android is { Image.Kind: AndroidImageKind.VendorBoot, Part.Name: "dlkm" } && doc.Android.Image.Parts is [{ Name: "platform", Detail: "platform" }, { Detail: "dlkm" }],
                "parts: " + string.Join("; ", doc.Android!.Image.Parts));
            Same(Program.DumpToString(SmallTree(6, 7)), Program.DumpToString(doc.Tree), "the dlkm fragment");
            doc.Tree.CreateFile(doc.Tree.Root, "module.ko", new MemoryFileData(Bytes(rnd, 7777)));
            doc = doc.Save(path, doc.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir })).Document;
        }
        finally
        {
            doc.Dispose();
        }
        byte[] after = File.ReadAllBytes(path);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(after.AsSpan(24));
        byte[] newR1 = after.AsSpan(page + r0.Length, (int)size - r0.Length).ToArray();
        byte[] h2 = (byte[])h.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(h2.AsSpan(24), (uint)(r0.Length + newR1.Length));
        Entry(1, newR1.Length, r0.Length, 3, "dlkm");
        Check(after.AsSpan().SequenceEqual(Build(r0, newR1, h2, table)), "the vendor boot image differs from what mkbootimg makes");
        using (ImageDocument plat = ImageDocument.Open(path, _dir, searchEmbedded: false, volume: "platform"))
            Same(Program.DumpToString(SmallTree(5, 5)), Program.DumpToString(plat.Tree), "the platform fragment after the change");
    }

    private static void AndroidFuzz()
    {
        var rnd = new Random(77);
        (byte[] super, _, _) = SampleSuper(slots: 1);
        byte[] boot = BuildBoot(2, 2048, Bytes(rnd, 20000), Ramdisk(SmallTree(7, 4), ArchiveCompression.Gzip), Bytes(rnd, 100), Bytes(rnd, 100), Bytes(rnd, 100));
        string path = Path.Combine(_dir, "afuzz.img");
        int iterations = int.TryParse(Environment.GetEnvironmentVariable("FSFORGE_FUZZ"), out int custom) ? custom : 120;
        var crashes = new List<string>();
        foreach ((string name, byte[] good, int metaEnd) in new[] { ("super", super, 12288 + 2 * 65536), ("boot", boot, boot.Length) })
        {
            for (int i = 0; i < iterations; i++)
            {
                byte[] bad = (byte[])good.Clone();
                if (i % 4 == 3) bad = bad.AsSpan(0, rnd.Next(1, bad.Length)).ToArray();
                else
                    for (int k = 0; k < 1 + rnd.Next(i % 4 == 0 ? 1 : 10); k++)
                    {
                        int at = name == "super" && rnd.Next(3) > 0 ? 4096 + rnd.Next(metaEnd - 4096) : rnd.Next(Math.Min(metaEnd, bad.Length));
                        bad[at] = i % 4 == 1 ? (byte)rnd.Next(256) : (byte)(bad[at] ^ (1 << rnd.Next(8)));
                    }
                File.WriteAllBytes(path, bad);
                try
                {
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
