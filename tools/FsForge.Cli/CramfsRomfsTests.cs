using System.Buffers.Binary;
using System.Text;
using FsForge.Core;
using FsForge.Core.Compression;
using FsForge.Core.Cramfs;
using FsForge.Core.Images;
using FsForge.Core.Romfs;

namespace FsForge.Cli;

/// <summary>
/// CramFS and romfs: round trips with every option, the layout the kernel expects, settings kept on saving,
/// CramFS images built by hand with what only other tools write (uncompressed blocks, direct block pointers,
/// holes, the old superblock without CRC), romfs hard links and checksums, and the limits of both formats.
/// </summary>
internal static partial class SelfTest
{
    private static void CramfsRomfsTests()
    {
        // The test tree without the name of 255 bytes, which neither format can hold.
        FsTree tree = BuildTree(new Random(5));
        tree.Remove(tree.Resolve("/weird names/" + new string('n', 255))!);
        string reference = Program.DumpToString(tree);

        foreach ((string name, CramfsOptions o) in new (string, CramfsOptions)[]
        {
            ("little-endian", new CramfsOptions()),
            ("big-endian", new CramfsOptions { BigEndian = true }),
            ("boot pad and holes", new CramfsOptions { Pad = true, Holes = true, Level = 1 }),
            ("16 KiB blocks", new CramfsOptions { BlockSize = 16384, BigEndian = true }),
            ("64 KiB blocks, all owned by root", new CramfsOptions { BlockSize = 65536, AllRoot = true }),
        })
            Test($"cramfs {name}", () => RoundTrip(tree, reference, "w.cramfs", new SaveOptions { Format = ImageFormat.Cramfs, Cramfs = o }));
        foreach (int align in new[] { 16, 512, 4096 })
            Test($"romfs, data aligned to {align}", () => RoundTrip(tree, reference, "w.romfs",
                new SaveOptions { Format = ImageFormat.Romfs, Romfs = new RomfsOptions { DataAlignment = align } }));
        Test("cramfs: layout, shared data, settings kept on saving", () => CramfsLayout(tree));
        Test("cramfs: uncompressed blocks, direct pointers, holes, old superblock", CramfsHandBuilt);
        Test("romfs: layout, hard links, checksums, settings kept on saving", () => RomfsLayout(tree));
        Test("cramfs and romfs: limits of the formats", CramfsRomfsLimits);
    }

    /// <summary>What CramFS and romfs keep of a listing (see <see cref="Normalize"/>).</summary>
    private static void NormalizeCramfsRomfs(string[] f, SaveOptions o)
    {
        f[5] = "0";                                                     // no times
        f[8] = "-";                                                     // no extended attributes
        if (o.Format == ImageFormat.Cramfs)
        {
            f[7] = "-";                                                 // hard links become names that share data
            if (o.Cramfs.AllRoot) f[3] = f[4] = "0";
            f[3] = (uint.Parse(f[3]) & 0xFFFF).ToString();
            f[4] = (uint.Parse(f[4]) & 0xFF).ToString();
            if (f[1] is "c" or "b")
            {
                string[] d = f[6].Split(':');
                f[6] = $"{uint.Parse(d[0]) & 0xFF}:{uint.Parse(d[1]) & 0xFF}";
            }
            return;
        }
        // romfs: no owners; the permissions the kernel gives each type, plus whether a file is executable.
        f[3] = f[4] = "0";
        bool exec = f[1] is "d" or "f" && (Convert.ToInt32(f[2], 8) & 0x49) != 0;
        f[2] = f[1] switch
        {
            "l" => "0777",
            "c" or "b" => "0600",
            _ => exec ? "0755" : "0644",
        };
        if (f[1] is "c" or "b")
        {
            string[] d = f[6].Split(':');
            f[6] = $"{uint.Parse(d[0]) & 0xFFFF}:{uint.Parse(d[1]) & 0xFFFF}";
        }
    }

    private static void CramfsLayout(FsTree tree)
    {
        using var t = new FsTree(0x1ED, 1700000000);
        FsNode d = t.CreateDirectory(t.Root, "d", 0x1C0, 1000, 300);
        byte[] same = Bytes(new Random(1), 10000);
        t.CreateFile(t.Root, "b", new MemoryFileData(same), 0x1A4);
        t.CreateFile(d, "a", new MemoryFileData((byte[])same.Clone()), 0x1ED);
        t.CreateFile(t.Root, "a", new MemoryFileData(Encoding.ASCII.GetBytes("hello\n")), 0x1A4);
        t.CreateFile(t.Root, "empty", null, 0x1A4);
        t.CreateSpecial(t.Root, "null", FsNodeType.CharDevice, 1, 3, 0x1B6);
        byte[] img = WriteImage(t, new SaveOptions { Format = ImageFormat.Cramfs });
        ReadOnlySpan<byte> sb = img;
        Check(BinaryPrimitives.ReadUInt32LittleEndian(sb) == CramfsFormat.Magic && sb.Slice(16, 16).SequenceEqual(CramfsFormat.Signature), "magic or signature");
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(sb[4..]), flags = BinaryPrimitives.ReadUInt32LittleEndian(sb[8..]);
        Check(size == img.Length && img.Length % 4096 == 0, $"size {size} of {img.Length}");
        Check(flags == (CramfsFormat.FlagFsidV2 | CramfsFormat.FlagSortedDirs), $"flags 0x{flags:X}");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(sb[44..]) == 7, "file count");             // root + 6
        Check(BinaryPrimitives.ReadUInt32LittleEndian(sb[40..]) == 4, "block count");            // "a" 1, "b" 3 (shared with d/a)
        // The root's entries start right after the superblock, sorted by name.
        Check((BinaryPrimitives.ReadUInt32LittleEndian(sb[72..]) >> 6) * 4 == 76, "root offset");
        string names = Encoding.ASCII.GetString(img, 76, 120);
        Check(names.IndexOf("a\0", StringComparison.Ordinal) < names.IndexOf("b\0", StringComparison.Ordinal)
              && names.IndexOf("b\0", StringComparison.Ordinal) < names.IndexOf("d\0", StringComparison.Ordinal)
              && names.IndexOf("d\0", StringComparison.Ordinal) < names.IndexOf("empty", StringComparison.Ordinal), "entries are not sorted");
        // The CRC covers the image with its own field zero.
        byte[] zero = (byte[])img.Clone();
        zero.AsSpan(32, 4).Clear();
        Check(FsForge.Core.Compression.Crc32.Update(0, zero) == BinaryPrimitives.ReadUInt32LittleEndian(sb[32..]), "CRC");

        string path = WriteFile("layout.cramfs", img);
        Check(ImageDocument.Detect(path) == ImageFormat.Cramfs, "detected as " + ImageDocument.Detect(path));
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
        {
            Check(doc.FormatName == "CramFS" && doc.Cramfs is { BigEndian: false, BlockSize: 4096, CrcOk: true, Files: 7, VolumeName: "Compressed" }, "info");
            Check(doc.Warnings.Count == 0, "warnings: " + string.Join("; ", doc.Warnings));
            Check(ReferenceEquals(doc.Tree.Resolve("/b")!.Inode.Data, doc.Tree.Resolve("/d/a")!.Inode.Data), "identical files do not share their data");
            Check(doc.Tree.Resolve("/d") is { Inode: { Uid: 1000, Gid: 300 & 0xFF, Mode: 0x1C0 } }, "owner and mode of a directory");
        }

        // Big-endian, 16 KiB blocks, padded, with holes and a name: all kept when the image is saved again.
        string big = WriteFile("layout-be.cramfs", WriteImage(tree, new SaveOptions
        {
            Format = ImageFormat.Cramfs,
            Cramfs = new CramfsOptions { BigEndian = true, BlockSize = 16384, Pad = true, Holes = true, VolumeName = "rootfs", Edition = 3 },
        }));
        Check(BinaryPrimitives.ReadUInt32BigEndian(File.ReadAllBytes(big).AsSpan(512)) == CramfsFormat.Magic, "big-endian magic at 512");
        ImageDocument doc2 = ImageDocument.Open(big, _dir, searchEmbedded: false);
        try
        {
            Check(doc2.Cramfs is { BigEndian: true, BlockSize: 16384, Padded: true, Holes: true, VolumeName: "rootfs", Edition: 3, CrcOk: true }, "info of the big-endian image");
            SaveOptions o = doc2.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir });
            Check(o.Format == ImageFormat.Cramfs && o.Cramfs is { BigEndian: true, BlockSize: 16384, Pad: true, Holes: true, VolumeName: "rootfs", Edition: 3 }, "options from the image");
            doc2.Tree.Remove(doc2.Tree.Resolve("/etc/passwd")!);
            doc2 = doc2.Save(big, o).Document;
            Check(doc2.Cramfs is { BigEndian: true, BlockSize: 16384 } && doc2.Tree.Resolve("/etc/passwd") is null, "saved in place");
        }
        finally
        {
            doc2.Dispose();
        }
    }

    /// <summary>
    /// A CramFS image as the kernel's newer features allow it: block pointers with flags (an uncompressed block,
    /// direct pointers to an uncompressed and a compressed block), a hole, a block after a hole.
    /// </summary>
    private static void CramfsHandBuilt()
    {
        var rnd = new Random(11);
        byte[] u = Bytes(rnd, 5000), dd = Bytes(rnd, 6000, compressible: true), h = new byte[8192];
        Bytes(rnd, 3000, compressible: true).CopyTo(h, 4096);
        byte[] img = new byte[64 * 1024];
        var dir = new List<(string Name, int Mode, uint Size, uint Offset)>();
        int pos = 76 + 3 * 16;                                       // superblock, then three entries with 4-byte names

        static byte[] Z(ReadOnlySpan<byte> plain)
        {
            byte[] dst = new byte[plain.Length + 64];
            int n = new ZlibCodec(9).Compress(plain, dst);
            return dst.AsSpan(0, n).ToArray();
        }
        void Put(ReadOnlySpan<byte> data)
        {
            data.CopyTo(img.AsSpan(pos));
            pos += data.Length;
        }
        void Align() => pos = (pos + 3) & ~3;
        void Ptr(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(at), value);

        // "d": both blocks by direct pointer; the first uncompressed, the second compressed behind a 16-bit length.
        int dTable = pos;
        pos += 8;
        Align();
        int d0 = pos;
        Put(dd.AsSpan(0, 4096));
        Align();
        int d1 = pos;
        byte[] z1 = Z(dd.AsSpan(4096));
        Put(new[] { (byte)z1.Length, (byte)(z1.Length >> 8) });
        Put(z1);
        Ptr(dTable, (uint)(d0 / 4) | CramfsFormat.BlkFlagDirectPtr | CramfsFormat.BlkFlagUncompressed);
        Ptr(dTable + 4, (uint)(d1 / 4) | CramfsFormat.BlkFlagDirectPtr);
        dir.Add(("d", 0x81A4, 6000, (uint)dTable));
        // "h": a hole, then a compressed block.
        Align();
        int hTable = pos;
        pos += 8;
        Ptr(hTable, (uint)pos);
        Put(Z(h.AsSpan(4096)));
        Ptr(hTable + 4, (uint)pos);
        dir.Add(("h", 0x81A4, 8192, (uint)hTable));
        // "u": an uncompressed block marked in its pointer, then a compressed one.
        Align();
        int uTable = pos;
        pos += 8;
        Put(u.AsSpan(0, 4096));
        Ptr(uTable, (uint)pos | CramfsFormat.BlkFlagUncompressed);
        Put(Z(u.AsSpan(4096)));
        Ptr(uTable + 4, (uint)pos);
        dir.Add(("u", 0x81ED, 5000, (uint)uTable));

        int e = 76;
        foreach ((string name, int mode, uint size, uint offset) in dir)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(e), (uint)mode);
            BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(e + 4), size);
            BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(e + 8), 1 | (offset / 4) << 6);
            img[e + 12] = (byte)name[0];
            e += 16;
        }
        int end = (pos + 4095) & ~4095;
        Array.Resize(ref img, end);
        BinaryPrimitives.WriteUInt32LittleEndian(img, CramfsFormat.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(4), (uint)end);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(8), CramfsFormat.FlagFsidV2 | CramfsFormat.FlagSortedDirs | CramfsFormat.FlagHoles | CramfsFormat.FlagExtBlockPointers);
        CramfsFormat.Signature.CopyTo(img.AsSpan(16));
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(44), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(64), 0x41ED);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(68), 3 * 16);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(72), (76 / 4) << 6);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(32), FsForge.Core.Compression.Crc32.Update(0, img));

        void Expect(string file, bool crc, string what)
        {
            using ImageDocument doc = ImageDocument.Open(WriteFile(file, img), _dir, searchEmbedded: false);
            Check(doc.Cramfs is { ExtBlockPointers: true, Holes: true } && doc.Cramfs.CrcOk == (crc ? true : null), what + ": info");
            Check(Program.DumpToString(doc.Tree).Contains("/u\tf\t0755"), what + ": mode");
            foreach ((string name, byte[] want) in new[] { ("/d", dd), ("/h", h), ("/u", u) })
            {
                using Stream s = doc.Tree.Resolve(name)!.Inode.Data!.OpenRead();
                byte[] got = new byte[want.Length];
                s.ReadExactly(got);
                Check(got.AsSpan().SequenceEqual(want), $"{what}: content of {name}");
            }
        }
        Expect("hand.cramfs", true, "version 2");

        // A damaged byte is reported by the CRC.
        byte[] damaged = (byte[])img.Clone();
        damaged[^1] ^= 1;
        using (ImageDocument doc = ImageDocument.Open(WriteFile("crc.cramfs", damaged), _dir, searchEmbedded: false))
            Check(doc.Cramfs!.CrcOk == false && doc.Warnings.Any(w => w.Contains("CRC")), "a wrong CRC is not reported");

        // The old superblock: no size, counts or CRC.
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(8), CramfsFormat.FlagSortedDirs | CramfsFormat.FlagHoles | CramfsFormat.FlagExtBlockPointers);
        img.AsSpan(32, 16).Clear();
        Expect("old.cramfs", false, "version 1");

        // Unknown feature flags are refused.
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(8), 0x10000);
        try
        {
            using ImageDocument doc = ImageDocument.Open(WriteFile("future.cramfs", img), _dir, searchEmbedded: false);
            Check(false, "an image with unknown features was opened");
        }
        catch (FsException ex) when (ex.Message.Contains("features"))
        {
        }
    }

    private static void RomfsLayout(FsTree tree)
    {
        byte[] img = WriteImage(tree, new SaveOptions { Format = ImageFormat.Romfs, Romfs = new RomfsOptions { VolumeName = "rootfs" } });
        Check(img.AsSpan(0, 8).SequenceEqual("-rom1fs-"u8) && img.Length % 1024 == 0, "magic or padding");
        uint size = BinaryPrimitives.ReadUInt32BigEndian(img.AsSpan(8));
        Check(size <= img.Length && size % 16 == 0, "size");
        uint sum = 0;
        for (int i = 0; i < 512; i += 4) sum += BinaryPrimitives.ReadUInt32BigEndian(img.AsSpan(i));
        Check(sum == 0, "checksum of the first 512 bytes");
        // The root is the first entry, ".", a directory whose list starts with itself, then "..".
        Check(Encoding.ASCII.GetString(img, 16, 7) == "rootfs\0" && Encoding.ASCII.GetString(img, 48, 2) == ".\0", "volume name or root entry");
        uint first = BinaryPrimitives.ReadUInt32BigEndian(img.AsSpan(32));
        Check((first & 7) == RomfsFormat.TypeDirectory && BinaryPrimitives.ReadUInt32BigEndian(img.AsSpan(36)) == 32, "root entry");
        Check(Encoding.ASCII.GetString(img, (int)(first & ~15u) + 16, 3) == "..\0", "\"..\" after the root");
        // Every header has a zero checksum over itself and its name.
        uint hsum = 0;
        for (int i = 32; i < 64; i += 4) hsum += BinaryPrimitives.ReadUInt32BigEndian(img.AsSpan(i));
        Check(hsum == 0, "header checksum");

        string path = WriteFile("layout.romfs", img);
        Check(ImageDocument.Detect(path) == ImageFormat.Romfs, "detected as " + ImageDocument.Detect(path));
        ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
        try
        {
            Check(doc.FormatName == "romfs" && doc.Romfs is { VolumeName: "rootfs", ChecksumOk: true, DataAlignment: >= 16 }, "info");
            Check(doc.Warnings.Count == 0, "warnings: " + string.Join("; ", doc.Warnings));
            Check(doc.Tree.Resolve("/bin/busybox")!.Inode.LinkCount == 3 && ReferenceEquals(doc.Tree.Resolve("/etc/busybox-third")!.Inode, doc.Tree.Resolve("/bin/busybox")!.Inode),
                "hard links");
            SaveOptions o = doc.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir });
            Check(o.Format == ImageFormat.Romfs && o.Romfs.VolumeName == "rootfs", "options from the image");
            o.Romfs.DataAlignment = 4096;
            doc.Tree.Remove(doc.Tree.Resolve("/etc/passwd")!);
            doc = doc.Save(path, o).Document;
            Check(doc.Romfs is { VolumeName: "rootfs", DataAlignment: 4096 } && doc.Tree.Resolve("/etc/passwd") is null, "saved in place");
        }
        finally
        {
            doc.Dispose();
        }

        // A wrong checksum is reported (the kernel refuses such an image).
        img[600] ^= 0x40;
        img[20] ^= 1;
        using (ImageDocument bad = ImageDocument.Open(WriteFile("bad.romfs", img), _dir, searchEmbedded: false))
            Check(bad.Romfs!.ChecksumOk == false && bad.Warnings.Any(w => w.Contains("checksum")), "a wrong checksum is not reported");
    }

    private static void CramfsRomfsLimits()
    {
        void Fails(FsTree t, SaveOptions o, string text, string what)
        {
            try
            {
                WriteImage(t, o);
                Check(false, what + " was accepted");
            }
            catch (FsException ex)
            {
                Check(ex.Message.Contains(text), $"{what}: {ex.Message}");
            }
        }
        var cramfs = new SaveOptions { Format = ImageFormat.Cramfs };
        var romfs = new SaveOptions { Format = ImageFormat.Romfs };

        using (var t = new FsTree())
        {
            t.CreateFile(t.Root, new string('x', 253), null);
            Fails(t, cramfs, "252 bytes", "a name of 253 bytes in CramFS");
        }
        using (var t = new FsTree())
        {
            t.CreateFile(t.Root, new string('x', 128), null);
            Fails(t, romfs, "127 bytes", "a name of 128 bytes in romfs");
        }
        using (var t = new FsTree())
        {
            t.CreateFile(t.Root, new string('x', 252), null);
            t.CreateFile(t.Root, "big", new MemoryFileData(new byte[16 << 20]));
            Fails(t, cramfs, "16 MiB", "a file of 16 MiB in CramFS");
            t.Remove(t.Resolve("/big")!);
            t.CreateFile(t.Root, "fits", new MemoryFileData(new byte[(16 << 20) - 1]));
            using var doc = new MemoryStream();
            IReadOnlyList<string> w = ImageDocument.WriteTo(t, doc, cramfs);
            Check(w.Count == 0, "warnings: " + string.Join("; ", w));
        }
        // What does not fit is cut, with a warning.
        using (var t = new FsTree())
        {
            FsNode f = t.CreateFile(t.Root, "f", new MemoryFileData(new byte[] { 1 }), 0x9ED, 70000, 300);
            t.CreateHardLink(t.Root, "g", f);
            t.CreateSpecial(t.Root, "dev", FsNodeType.BlockDevice, 259, 70000);
            f.Inode.XAttrs = new(StringComparer.Ordinal) { ["user.a"] = new byte[] { 1 } };
            using var ms = new MemoryStream();
            IReadOnlyList<string> w = ImageDocument.WriteTo(t, ms, cramfs);
            Check(w.Count == 5 && w.Any(x => x.Contains("above 65535")) && w.Any(x => x.Contains("above 255")) && w.Any(x => x.Contains("hard links"))
                  && w.Any(x => x.Contains("major or minor")) && w.Any(x => x.Contains("extended attributes")), "CramFS warnings: " + string.Join("; ", w));
            ms.SetLength(0);
            w = ImageDocument.WriteTo(t, ms, romfs);
            Check(w.Count == 4 && w.Any(x => x.Contains("no owners")) && w.Any(x => x.Contains("executable")) && w.Any(x => x.Contains("65535"))
                  && w.Any(x => x.Contains("extended attributes")), "romfs warnings: " + string.Join("; ", w));
        }
    }
}
