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
        Test("TRX: wrong CRC, broken headers, never saved over", TrxChecks);
        Test("TRX: corrupted images are rejected cleanly", TrxFuzz);
    }

    /// <summary>A TRX image: header (28 bytes in version 1, 32 in version 2), parts at their alignments, optionally the JFFS2 marker.</summary>
    private static byte[] BuildTrx(byte[][] parts, int version, int[] aligns, bool marker = false, bool zlibCrc = false)
    {
        int header = version == 1 ? 28 : 32;
        var body = new List<byte>(new byte[header]);
        var offsets = new List<uint>();
        for (int i = 0; i < parts.Length; i++)
        {
            while (body.Count % aligns[i] != 0) body.Add(0);
            offsets.Add((uint)body.Count);
            body.AddRange(parts[i]);
        }
        if (marker)
        {
            while (body.Count % 65536 != 0) body.Add(0xFF);
            body.AddRange(new byte[] { 0xDE, 0xAD, 0xC0, 0xDE });
        }
        while (body.Count % 4 != 0) body.Add(0);
        byte[] trx = body.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(trx, 0x30524448);
        BinaryPrimitives.WriteUInt32LittleEndian(trx.AsSpan(4), (uint)trx.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(trx.AsSpan(14), (ushort)version);
        for (int i = 0; i < offsets.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(trx.AsSpan(16 + 4 * i), offsets[i]);
        // OpenWrt's trx: the CRC started at ~0 and not inverted at the end; zlib's is inverted.
        uint crc = RefCrc32(trx[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(trx.AsSpan(8), zlibCrc ? crc : ~crc);
        return trx;
    }

    /// <summary>A Netgear CHK image: the 40-byte big-endian header and the board name, then the TRX image.</summary>
    private static byte[] BuildChk(byte[] trx, string board)
    {
        byte[] name = Encoding.ASCII.GetBytes(board);
        byte[] h = new byte[40 + name.Length];
        BinaryPrimitives.WriteUInt32BigEndian(h, 0x2A23245E);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(4), (uint)h.Length);
        name.CopyTo(h, 40);
        return h.Concat(trx).ToArray();
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
            Check(doc.Firmware.Image.Parts is [{ Name: "loader", Offset: 28 }, { Name: "kernel", Offset: 3072, ContentName: "LZMA data" }, { Name: "rootfs", Offset: 131072 }],
                "version 1: parts " + string.Join("; ", doc.Firmware.Image.Parts));
            Check(doc.SavesFilesystemOnly && doc.Warnings.Any(w => w.Contains("TRX image is not supported yet")), "TRX images are only read");
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

        // Saving: the filesystem alone, never over the TRX image.
        path = WriteFile("save.trx", trx);
        using ImageDocument d = ImageDocument.Open(path, _dir, searchEmbedded: false);
        d.Tree.Remove(d.Tree.Resolve("/file0")!);
        SaveOptions o = d.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir });
        Check(!d.WritesContainer(o) && !d.WritesFirmware(o), "no TRX image is written");
        ExpectFs(() => d.Save(path, o));
        Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(trx), "the TRX image was changed");
        string alone = Path.Combine(_dir, "trx-alone.sqfs");
        d.Save(alone, o).Document?.Dispose();
        using ImageDocument back = ImageDocument.Open(alone, _dir, searchEmbedded: false);
        Check(back.Firmware is null && back.Tree.Resolve("/file0") is null && back.Tree.Resolve("/file2") is not null, "saved alone");
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
