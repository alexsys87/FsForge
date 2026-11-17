using System.Buffers.Binary;
using System.Text;
using FsForge.Core;
using FsForge.Core.Firmware;
using FsForge.Core.Images;

namespace FsForge.Cli;

/// <summary>
/// Broadcom TRX images and Netgear CHK images holding one. The images are built here from OpenWrt's trx.h as its trx
/// tool lays them out (header, parts at aligned offsets, the JFFS2 marker, a CRC-32 from the flags to the end),
/// independently of the reader; the root filesystem must read as the image itself. TRX images are not rebuilt yet.
/// </summary>
internal static partial class SelfTest
{
    private static void TrxTests()
    {
        Test("TRX: version 1 and 2, parts, CRC, Netgear CHK", TrxRead);
        Test("TRX: wrong CRC, broken headers, the filesystem alone never over the image", TrxChecks);
        Test("TRX: rebuilt as trx lays it out (version 1, -F, version 2 with bin-header)", TrxRebuild);
        Test("TRX: Netgear CHK rebuilt with its checksums; unknown checksums only read", TrxRebuildChk);
        Test("TRX: corrupted images are rejected cleanly", TrxFuzz);
    }

    /// <summary>
    /// A TRX image as OpenWrt's trx makes it: the header (28 bytes in version 1, 32 in version 2), each part padded to 4
    /// bytes at its alignment (-a), optionally the JFFS2 marker, the whole padded to 4 KiB, the CRC-32 from the flags
    /// to the end started at ~0 and not inverted (zlib's, inverted, with <paramref name="zlibCrc"/>). In version 2 the
    /// part in slot 3 is the bin-header, whose 8 bytes from 22 count as 0xFF for the CRC. With <paramref name="fsmark"/>
    /// the last part is appended behind the length and the CRC (trx -F) and gets no offset.
    /// </summary>
    private static byte[] BuildTrx(byte[][] parts, int version, int[] aligns, bool marker = false, bool zlibCrc = false, bool fsmark = false)
    {
        int header = version == 1 ? 28 : 32;
        var body = new List<byte>(new byte[header]);
        var offsets = new List<uint>();
        int mark = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            while (body.Count % aligns[i] != 0) body.Add(0);
            if (fsmark && i == parts.Length - 1) mark = body.Count;
            else offsets.Add((uint)body.Count);
            body.AddRange(parts[i]);
            while (body.Count % 4 != 0) body.Add(0);
        }
        if (marker)
        {
            while (body.Count % 65536 != 0) body.Add(0xFF);
            body.AddRange(new byte[] { 0xDE, 0xAD, 0xC0, 0xDE });
        }
        while (body.Count % 4096 != 0) body.Add(0);
        byte[] trx = body.ToArray();
        int length = fsmark ? mark : trx.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(trx, 0x30524448);
        BinaryPrimitives.WriteUInt32LittleEndian(trx.AsSpan(4), (uint)length);
        BinaryPrimitives.WriteUInt16LittleEndian(trx.AsSpan(14), (ushort)version);
        for (int i = 0; i < offsets.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(trx.AsSpan(16 + 4 * i), offsets[i]);
        byte[] covered = trx[12..length];
        if (version == 2 && offsets.Count == 4) covered.AsSpan((int)offsets[3] + 22 - 12, 8).Fill(0xFF);
        uint crc = RefCrc32(covered);
        BinaryPrimitives.WriteUInt32LittleEndian(trx.AsSpan(8), zlibCrc ? crc : ~crc);
        return trx;
    }

    /// <summary>Netgear's checksum as mkchkimg computes it: two running byte sums, each folded to 16 bits.</summary>
    private static uint NetgearSum(IEnumerable<byte> data)
    {
        uint c0 = 0, c1 = 0;
        foreach (byte b in data)
        {
            c0 += b;
            c1 += c0;
        }
        uint f0 = (c0 & 0xFFFF) + (c0 >> 16), f1 = (c1 & 0xFFFF) + (c1 >> 16);
        return ((((f1 >> 16) + f1) & 0xFFFF) << 16) | (((f0 >> 16) + f0) & 0xFFFF);
    }

    /// <summary>
    /// A Netgear CHK image: the 40-byte big-endian header and the board name, then the TRX image as its kernel; with
    /// <paramref name="sums"/> its checksums as mkchkimg makes them (kernel, root filesystem, image, header).
    /// </summary>
    private static byte[] BuildChk(byte[] trx, string board, bool sums = false)
    {
        byte[] name = Encoding.ASCII.GetBytes(board);
        byte[] h = new byte[40 + name.Length];
        BinaryPrimitives.WriteUInt32BigEndian(h, 0x2A23245E);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(4), (uint)h.Length);
        h[8] = 1;                                                           // region
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(24), (uint)trx.Length);
        name.CopyTo(h, 40);
        if (sums)
        {
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(16), NetgearSum(trx));
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(20), NetgearSum(Array.Empty<byte>()));
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(32), NetgearSum(trx));
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(36), NetgearSum(h));
        }
        return h.Concat(trx).ToArray();
    }

    /// <summary>Checks the CHK checksums of a file as mkchkimg makes them.</summary>
    private static bool ChkSumsRight(byte[] file)
    {
        int headerLength = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(4));
        int kernel = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(24)), rootfs = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(28));
        byte[] h = file[..headerLength];
        uint headerSum = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(36));
        h.AsSpan(36, 4).Clear();
        return BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(16)) == NetgearSum(file.Skip(headerLength).Take(kernel))
               && BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(20)) == NetgearSum(file.Skip(headerLength + kernel).Take(rootfs))
               && BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(32)) == NetgearSum(file.Skip(headerLength).Take(kernel + rootfs))
               && headerSum == NetgearSum(h);
    }

    private static void TrxRead()
    {
        var rnd = new Random(51);
        FsTree tree = SmallTree(51, 9);
        string expected = Program.DumpToString(tree);
        byte[] loader = Bytes(rnd, 3001), kernel = new byte[] { 0x5D, 0, 0, 0x80, 0 }.Concat(Bytes(rnd, 90000)).ToArray(), squash = Squash(tree);

        string path = WriteFile("v1.trx", BuildTrx(new[] { loader, kernel, squash }, 1, new[] { 4, 1024, 65536 }, marker: true));
        Check(ImageDocument.Detect(path) == ImageFormat.SquashFs, "detected as " + ImageDocument.Detect(path));
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
        {
            Check(doc.Format == ImageFormat.SquashFs && doc.Firmware?.Image is TrxImage { Version: 1, CrcScheme: "fwtool", ChkBoard: null, TrxOffset: 0 } && doc.Firmware.Part.Name == "rootfs",
                "version 1: opened at the root filesystem");
            // Each part at its alignment behind the one before: the loader at 28, the kernel at 3072, the SquashFS at 128 KiB.
            Check(doc.Firmware!.Image.Parts is [{ Name: "loader", Offset: 28 }, { Name: "kernel", Offset: 3072, ContentName: "LZMA data" }, { Name: "rootfs", Offset: 131072 }],
                "version 1: parts " + string.Join("; ", doc.Firmware.Image.Parts));
            Check(!doc.SavesFilesystemOnly, "a TRX image is rebuilt when saved");
            Same(expected, Program.DumpToString(doc.Tree), "version 1");
        }
        ExpectFs(() => ImageDocument.Open(path, _dir, searchEmbedded: false, volume: "kernel").Dispose());

        path = WriteFile("v2.trx", BuildTrx(new[] { kernel, squash }, 2, new[] { 4, 4096 }, zlibCrc: true));
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
        {
            Check(doc.Firmware?.Image is TrxImage { Version: 2, CrcScheme: "standard" } && doc.Firmware.Image.Parts is [{ Name: "kernel", Offset: 32 }, { Name: "rootfs", Offset: (32 + 90005 + 4095) / 4096 * 4096 }],
                "version 2: parts " + string.Join("; ", doc.Firmware?.Image.Parts ?? []));
            Same(expected, Program.DumpToString(doc.Tree), "version 2");
        }

        byte[] trx = BuildTrx(new[] { loader, kernel, squash }, 1, new[] { 4, 1024, 65536 });
        path = WriteFile("netgear.chk", BuildChk(trx, "U12H139T00_NETGEAR"));
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
        {
            Check(doc.Firmware?.Image is TrxImage { ChkBoard: "U12H139T00_NETGEAR", TrxOffset: 58, CrcScheme: "fwtool" } t && t.Description.Contains("U12H139T00_NETGEAR")
                  && doc.Firmware.Part.Offset == 58 + 131072, "Netgear CHK");
            Same(expected, Program.DumpToString(doc.Tree), "Netgear CHK");
        }
    }

    private static void TrxChecks()
    {
        var rnd = new Random(52);
        FsTree tree = SmallTree(52, 5);
        byte[] kernel = Bytes(rnd, 20000), squash = Squash(tree);
        byte[] trx = BuildTrx(new[] { kernel, squash }, 1, new[] { 4, 4096 });

        byte[] changed = (byte[])trx.Clone();
        changed[^50] ^= 1;
        string path = WriteFile("changed.trx", changed);
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
            Check(doc.Firmware?.Image is TrxImage { CrcMatches: false, ChecksumsValid: false } && doc.Warnings.Any(w => w.Contains("CRC-32 of the TRX image")), "a wrong CRC is reported");

        // Headers that are no TRX: an unknown version, an offset beyond the image, a length beyond the file.
        foreach ((string what, Action<byte[]> spoil) in new (string, Action<byte[]>)[]
                 {
                     ("version 3", b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(14), 3)),
                     ("offset beyond the image", b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), (uint)b.Length + 4)),
                     ("length beyond the file", b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)b.Length + 4096)),
                 })
        {
            byte[] bad = (byte[])trx.Clone();
            spoil(bad);
            path = WriteFile("notrx.trx", bad);
            using ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: true);
            int squashAt = bad.AsSpan().IndexOf("hsqs"u8);
            Check(doc.Firmware is null && doc.EmbeddedOffset == squashAt, what + ": not taken for a TRX image, the SquashFS found by the search");
        }

        // The filesystem alone (or a tar archive) is never written over the TRX image.
        path = WriteFile("save.trx", trx);
        using ImageDocument d = ImageDocument.Open(path, _dir, searchEmbedded: false);
        d.Tree.Remove(d.Tree.Resolve("/file0")!);
        SaveOptions o = d.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir, KeepContainer = false });
        Check(!d.WritesContainer(o) && !d.WritesFirmware(o), "no TRX image is written");
        ExpectFs(() => d.Save(path, o));
        ExpectFs(() => d.Save(path, d.CreateSaveOptions(ImageFormat.Tar, new SaveOptions { TempDirectory = _dir })));
        Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(trx), "the TRX image was changed");
        string alone = Path.Combine(_dir, "trx-alone.sqfs");
        d.Save(alone, o).Document?.Dispose();
        using ImageDocument back = ImageDocument.Open(alone, _dir, searchEmbedded: false);
        Check(back.Firmware is null && back.Tree.Resolve("/file0") is null && back.Tree.Resolve("/file2") is not null, "saved alone");
    }

    /// <summary>Opens a file at its root filesystem, adds a file of the given size (or removes it with 0) and saves in place.</summary>
    private static ImageDocument.SaveResult EditRootfs(string path, int size, Random rnd)
    {
        using ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
        if (size > 0) doc.Tree.CreateFile(doc.Tree.Root, "big", new MemoryFileData(Bytes(rnd, size)));
        else doc.Tree.Remove(doc.Tree.Resolve("/big")!);
        SaveOptions o = doc.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir });
        Check(doc.WritesFirmware(o), "the firmware is written");
        ImageDocument.SaveResult r = doc.Save(path, o);
        r.Document?.Dispose();
        return r;
    }

    /// <summary>The SquashFS of a file at or after an offset, with the 4 KiB padding the writer gives it.</summary>
    private static byte[] SquashAt(byte[] file, int from)
    {
        int at = file.AsSpan(from).IndexOf("hsqs"u8) + from;
        long used = (long)BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(at + 40));
        return file[at..(at + (int)((used + 4095) & ~4095L))];
    }

    private static void TrxRebuild()
    {
        var rnd = new Random(54);
        FsTree tree = SmallTree(54, 6);
        byte[] loader = Bytes(rnd, 3001), kernel = new byte[] { 0x5D, 0, 0, 0x80, 0 }.Concat(Bytes(rnd, 60000)).ToArray(), binHeader = Bytes(rnd, 64);
        byte[] squash = Squash(tree);
        // Each layout as trx makes it; after the change, the file must be what trx makes from the same parts and the new filesystem.
        var layouts = new (string Name, Func<byte[], byte[]> Make)[]
        {
            ("version 1", root => BuildTrx(new[] { loader, kernel, root }, 1, new[] { 4, 1024, 65536 })),
            ("trx -F", root => BuildTrx(new[] { loader, kernel, root }, 1, new[] { 4, 1024, 65536 }, fsmark: true)),
            ("version 2 with bin-header", root => BuildTrx(new[] { loader, kernel, root, binHeader }, 2, new[] { 4, 1024, 65536, 4 })),
            ("zlib CRC", root => BuildTrx(new[] { kernel, root }, 1, new[] { 4, 4096 }, zlibCrc: true)),
        };
        foreach ((string name, Func<byte[], byte[]> make) in layouts)
        {
            string path = WriteFile("rebuild.trx", make(squash));
            using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
                Check(doc.Firmware?.Image is TrxImage { CrcMatches: true } && doc.Firmware.Part.Name == "rootfs" && !doc.SavesFilesystemOnly, name + ": opened");
            foreach (int size in new[] { 300000, 0 })
            {
                EditRootfs(path, size, rnd);
                byte[] after = File.ReadAllBytes(path);
                byte[] expected = make(SquashAt(after, 4096));
                Check(after.AsSpan().SequenceEqual(expected), $"{name}, {(size > 0 ? "grown" : "shrunk")}: the file differs from what trx makes ({after.Length} / {expected.Length} bytes)");
            }
        }

        // OpenWrt's JFFS2 marker behind the root filesystem inside the image moves to the next 64 KiB behind the new one.
        string marked = WriteFile("rebuild-marker.trx", BuildTrx(new[] { loader, kernel, squash }, 1, new[] { 4, 1024, 65536 }, marker: true));
        EditRootfs(marked, 200000, rnd);
        byte[] m = File.ReadAllBytes(marked);
        List<int> marks = Markers(m);
        int rootAt = m.AsSpan(4096).IndexOf("hsqs"u8) + 4096;
        long fsEnd = rootAt + (long)BinaryPrimitives.ReadUInt64LittleEndian(m.AsSpan(rootAt + 40));
        Check(marks.Contains((int)((fsEnd + 65535) & ~65535L)) && BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(4)) == m.Length, "the marker behind the new filesystem");
        using (ImageDocument doc = ImageDocument.Open(marked, _dir, searchEmbedded: false))
            Check(doc.Firmware?.Image is TrxImage { CrcMatches: true } && doc.Tree.Resolve("/big") is not null, "the image with the marker reads back");
    }

    private static void TrxRebuildChk()
    {
        var rnd = new Random(55);
        FsTree tree = SmallTree(55, 5);
        byte[] kernel = Bytes(rnd, 40000);
        foreach (bool fsmark in new[] { false, true })
        {
            string what = fsmark ? "CHK with trx -F" : "CHK";
            string path = WriteFile("rebuild.chk", BuildChk(BuildTrx(new[] { kernel, Squash(tree) }, 1, new[] { 4, 65536 }, fsmark: fsmark), "R7000", sums: true));
            using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
                Check(doc.Firmware?.Image is TrxImage { ChkChecksumsValid: true, ChkBoard: "R7000" } && !doc.SavesFilesystemOnly, what + ": checksums recognized");
            EditRootfs(path, 150000, rnd);
            byte[] after = File.ReadAllBytes(path);
            Check(ChkSumsRight(after), what + ": the CHK checksums of the new image are wrong");
            Check(after.AsSpan(40, 5).SequenceEqual("R7000"u8) && after[8] == 1, what + ": board and region kept");
            using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
                Check(doc.Firmware?.Image is TrxImage { ChkChecksumsValid: true, CrcMatches: true } && doc.Tree.Resolve("/big") is not null, what + ": reads back");
        }

        // Checksums this program does not reproduce: only read, the filesystem saved alone.
        string unknown = WriteFile("unknown.chk", BuildChk(BuildTrx(new[] { kernel, Squash(tree) }, 1, new[] { 4, 65536 }), "R7000"));
        using ImageDocument d = ImageDocument.Open(unknown, _dir, searchEmbedded: false);
        Check(d.Firmware?.Image is TrxImage { ChkChecksumsValid: false } && d.SavesFilesystemOnly && d.Warnings.Any(w => w.Contains("Netgear CHK header do not match")),
            "unknown CHK checksums: only read");
    }

    private static void TrxFuzz()
    {
        var rnd = new Random(53);
        FsTree tree = SmallTree(53, 4);
        byte[] trx = BuildTrx(new[] { Bytes(rnd, 2000), Bytes(rnd, 9000), Squash(tree) }, 1, new[] { 4, 512, 4096 });
        byte[] chk = BuildChk(BuildTrx(new[] { Bytes(rnd, 9000), Squash(tree) }, 2, new[] { 4, 4096 }), "BOARD");
        string path = Path.Combine(_dir, "trxfuzz.trx");
        int iterations = int.TryParse(Environment.GetEnvironmentVariable("FSFORGE_FUZZ"), out int custom) ? custom : 120;
        var crashes = new List<string>();
        foreach ((string name, byte[] good) in new[] { ("trx", trx), ("chk", chk) })
        {
            for (int i = 0; i < iterations; i++)
            {
                byte[] bad = (byte[])good.Clone();
                if (i % 4 == 3) bad = bad.AsSpan(0, rnd.Next(1, bad.Length)).ToArray();
                else
                    for (int k = 0; k < 1 + rnd.Next(i % 4 == 0 ? 1 : 10); k++)
                    {
                        // Mostly the headers: lengths, versions, offsets.
                        int at = rnd.Next(3) > 0 ? rnd.Next(Math.Min(100, bad.Length)) : rnd.Next(bad.Length);
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
                    File.Copy(path, Path.Combine(_dir, $"crash-trx-{name}-{i}.bin"), true);
                }
            }
        }
        Check(crashes.Count == 0, string.Join(" | ", crashes));
    }
}
