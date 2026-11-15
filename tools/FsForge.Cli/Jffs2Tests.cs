using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FsForge.Core;
using FsForge.Core.Images;
using FsForge.Core.Jffs2;

namespace FsForge.Cli;

/// <summary>
/// JFFS2: round trips of the writer in both byte orders and with every compressor, the limits of the
/// format, and the reading of a log the way the kernel leaves it (newer versions, deletions, renames,
/// truncation with a hole, obsolete and torn nodes). The log is built here, independently of the writer.
/// </summary>
internal static partial class SelfTest
{
    private static void Jffs2Tests()
    {
        using FsTree tree = BuildTree(new Random(12345));
        // The shared tree goes beyond JFFS2 in two places: a 255-byte name and owners above 65535.
        tree.Rename(tree.Resolve("/weird names/" + new string('n', 255))!, new string('n', 254));
        foreach (FsNode n in tree.Walk())
        {
            if (n.Inode.Uid > ushort.MaxValue) n.Inode.Uid = 1000;
            if (n.Inode.Gid > ushort.MaxValue) n.Inode.Gid = 1000;
        }
        string reference = Program.DumpToString(tree);

        var variants = new (string Name, Jffs2Options Options)[]
        {
            ("little-endian zlib", new Jffs2Options()),
            ("big-endian zlib", new Jffs2Options { BigEndian = true }),
            ("lzo", new Jffs2Options { Compression = Jffs2Compression.Lzo }),
            ("rtime, big-endian", new Jffs2Options { Compression = Jffs2Compression.Rtime, BigEndian = true }),
            ("no compression", new Jffs2Options { Compression = Jffs2Compression.None }),
            ("NAND: no clean markers, 128 KiB blocks", new Jffs2Options { Cleanmarkers = false, EraseBlockSize = 128 * 1024 }),
            ("8 KiB blocks, 2 KiB pages, padded", new Jffs2Options { EraseBlockSize = 8192, PageSize = 2048, PadToEraseBlock = true }),
            ("extended attributes, little-endian", new Jffs2Options { StoreXattrs = true }),
            ("extended attributes, big-endian", new Jffs2Options { StoreXattrs = true, BigEndian = true }),
        };
        foreach ((string name, Jffs2Options o) in variants)
            Test($"jffs2 {name}", () => RoundTrip(tree, reference, "w.jffs2", new SaveOptions { Format = ImageFormat.Jffs2, Jffs2 = o }));

        Test("jffs2 limits: names, owners, options", () => Jffs2Limits(tree));
        Test("jffs2 layout is detected and kept on saving", () => Jffs2Layout(tree));
        foreach (bool be in new[] { false, true })
            Test($"jffs2 log as the kernel leaves it ({(be ? "big" : "little")}-endian)", () => Jffs2Log(be));
    }

    private static void Jffs2Limits(FsTree tree)
    {
        using var t = new FsTree(0x1ED, 1700000000);
        FsNode f = t.CreateFile(t.Root, "f", new MemoryFileData(new byte[] { 1 }), 0x1A4, 70000, 0, 1700000000);
        var o = new SaveOptions { Format = ImageFormat.Jffs2 };
        ExpectFs(() => ImageDocument.WriteTo(t, new MemoryStream(), o));            // owner above 65535
        o.Jffs2.AllRoot = true;
        ImageDocument.WriteTo(t, new MemoryStream(), o);                             // fine when everything belongs to root
        f.Inode.Uid = 0;
        t.CreateFile(t.Root, new string('x', 255), null);
        ExpectFs(() => ImageDocument.WriteTo(t, new MemoryStream(), o));            // name of 255 bytes
        foreach (Jffs2Options bad in new[]
                 {
                     new Jffs2Options { EraseBlockSize = 3000 }, new Jffs2Options { EraseBlockSize = 2048 },
                     new Jffs2Options { PageSize = 65536 }, new Jffs2Options { CleanmarkerSize = 14 },
                     new Jffs2Options { Compression = Jffs2Compression.DynRubin },
                 })
            ExpectFs(() => ImageDocument.WriteTo(tree, new MemoryStream(), new SaveOptions { Format = ImageFormat.Jffs2, Jffs2 = bad }));

        // Attributes are left out (with a warning) unless asked for.
        using var a = new FsTree(0x1ED, 1700000000);
        a.CreateFile(a.Root, "x", null).Inode.XAttrs = new(StringComparer.Ordinal) { ["user.a"] = new byte[] { 1 } };
        IReadOnlyList<string> w = ImageDocument.WriteTo(a, new MemoryStream(), new SaveOptions { Format = ImageFormat.Jffs2 });
        Check(w.Any(x => x.Contains("CONFIG_JFFS2_FS_XATTR")), "no warning about dropped attributes: " + string.Join("; ", w));
    }

    private static void Jffs2Layout(FsTree tree)
    {
        string path = Path.Combine(_dir, "layout.jffs2");
        foreach ((bool be, int ebs, bool cm) in new[] { (true, 131072, true), (false, 16384, false), (false, 65536, true) })
        {
            var o = new Jffs2Options { BigEndian = be, EraseBlockSize = ebs, Cleanmarkers = cm, Compression = Jffs2Compression.Lzo };
            using (var fs = new FileStream(path, FileMode.Create))
                ImageDocument.WriteTo(tree, fs, new SaveOptions { Format = ImageFormat.Jffs2, Jffs2 = o });
            Check(ImageDocument.Detect(path) == ImageFormat.Jffs2, "not detected as JFFS2");
            using ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
            Jffs2Info i = doc.Jffs2!;
            Check(doc.Format == ImageFormat.Jffs2 && doc.FormatName == "JFFS2", "format: " + doc.FormatName);
            Check(i.BigEndian == be && i.EraseBlockSize == ebs && i.Cleanmarkers == cm && i.EraseBlockSizeKnown == cm,
                $"layout of {(be ? "BE" : "LE")}/{ebs}/{cm}: {(i.BigEndian ? "BE" : "LE")}/{i.EraseBlockSize}/{i.Cleanmarkers}/known={i.EraseBlockSizeKnown}");
            Check(i.MainCompression == Jffs2Compression.Lzo && i.DamagedNodes == 0 && doc.Warnings.Count == 0,
                $"compression {i.MainCompression}, damaged {i.DamagedNodes}, warnings: {string.Join("; ", doc.Warnings)}");
            // Saving in the same format keeps the layout of the source.
            SaveOptions same = doc.CreateSaveOptions();
            Check(same.Jffs2.BigEndian == be && same.Jffs2.EraseBlockSize == ebs && same.Jffs2.Cleanmarkers == cm && same.Jffs2.Compression == Jffs2Compression.Lzo,
                "options of a re-save do not reproduce the source");
            Check(!doc.WritesContainer(same), "a JFFS2 image has no container to keep");
        }
        // A flash dump that starts with erased blocks is still found.
        byte[] img = File.ReadAllBytes(path);
        byte[] dump = new byte[3 * 65536 + img.Length];
        Array.Fill(dump, (byte)0xFF, 0, 3 * 65536);
        img.CopyTo(dump, 3 * 65536);
        File.WriteAllBytes(path, dump);
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
            Same(Normalize(Program.DumpToString(tree), new SaveOptions { Format = ImageFormat.Jffs2 }),
                 Normalize(Program.DumpToString(doc.Tree), new SaveOptions { Format = ImageFormat.Jffs2 }), "dump with leading erased blocks");
        File.Delete(path);
    }

    // -------------------------------------------------------------- log

    /// <summary>A minimal, independent JFFS2 node writer for building logs by hand.</summary>
    private sealed class J2Log(bool be, int eraseBlock)
    {
        private readonly MemoryStream _out = new();

        public byte[] ToArray() => _out.ToArray();

        private static readonly uint[] Table = Enumerable.Range(0, 256).Select(i =>
        {
            uint c = (uint)i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320 : c >> 1;
            return c;
        }).ToArray();

        private static uint Crc(ReadOnlySpan<byte> d)
        {
            uint c = 0;
            foreach (byte b in d) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
            return c;
        }

        private void W16(byte[] b, int o, int v) { if (be) BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(o), (ushort)v); else BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), (ushort)v); }
        private void W32(byte[] b, int o, uint v) { if (be) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(o), v); else BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v); }

        private void Header(byte[] b, int type, bool obsolete = false)
        {
            W16(b, 0, 0x1985);
            W16(b, 2, type);
            W32(b, 4, (uint)b.Length);
            W32(b, 8, Crc(b.AsSpan(0, 8)));
            if (obsolete) W16(b, 2, type & ~0x2000);          // the kernel clears the bit in place, the CRC stays
        }

        public void Put(byte[] node)
        {
            long pos = _out.Length;
            if (pos % eraseBlock == 0) Cleanmarker();
            if (_out.Length % eraseBlock + node.Length > eraseBlock)
            {
                while (_out.Length % eraseBlock != 0) _out.WriteByte(0xFF);
                Cleanmarker();
            }
            _out.Write(node);
            while (_out.Length % 4 != 0) _out.WriteByte(0xFF);
        }

        private void Cleanmarker()
        {
            byte[] c = new byte[12];
            Header(c, 0x2003);
            _out.Write(c);
        }

        public void Dirent(uint pino, string name, uint ino, uint version, int dtype)
        {
            byte[] n = Encoding.UTF8.GetBytes(name);
            byte[] b = new byte[40 + n.Length];
            Header(b, 0xE001);
            W32(b, 12, pino); W32(b, 16, version); W32(b, 20, ino); W32(b, 24, 1700000000);
            b[28] = (byte)n.Length; b[29] = (byte)dtype;
            W32(b, 32, Crc(b.AsSpan(0, 32)));
            W32(b, 36, Crc(n));
            n.CopyTo(b, 40);
            Put(b);
        }

        /// <summary>compr: 0 none, 1 zero (hole), 2 rtime (given packed data), 6 zlib.</summary>
        public void Inode(uint ino, uint version, int mode, uint isize, uint offset, byte[] plain, int compr = 0,
            byte[]? packed = null, uint dsize = 0, bool obsolete = false, bool badDataCrc = false)
        {
            byte[] data = compr switch
            {
                0 => plain,
                1 => Array.Empty<byte>(),
                6 => Zlib(plain),
                _ => packed!,
            };
            byte[] b = new byte[68 + data.Length];
            Header(b, 0xE002, obsolete);
            W32(b, 12, ino); W32(b, 16, version); W32(b, 20, (uint)mode);
            W32(b, 28, isize); W32(b, 32, 1700000000); W32(b, 36, 1700000000 + version); W32(b, 40, 1700000000);
            W32(b, 44, offset); W32(b, 48, (uint)data.Length); W32(b, 52, compr == 1 ? dsize : (uint)plain.Length);
            b[56] = (byte)compr;
            W32(b, 60, Crc(data) ^ (badDataCrc ? 1u : 0u));
            W32(b, 64, Crc(b.AsSpan(0, 60)));
            data.CopyTo(b, 68);
            Put(b);
        }

        private static byte[] Zlib(byte[] plain)
        {
            var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(plain);
            return ms.ToArray();
        }
    }

    private static byte[] Fill(char c, int n) => Enumerable.Repeat((byte)c, n).ToArray();

    private static void Jffs2Log(bool be)
    {
        const int Dir = 0x41ED, Reg = 0x81A4, Lnk = 0xA1FF;
        var log = new J2Log(be, 16384);
        // The image as first written.
        log.Inode(1, 1, Dir, 0, 0, Array.Empty<byte>());
        log.Dirent(1, "keep", 2, 1, 8);
        log.Inode(2, 1, Reg, 6000, 0, Fill('A', 4096), compr: 6);
        log.Inode(2, 2, Reg, 6000, 4096, Fill('A', 1904));
        log.Dirent(1, "gone", 3, 2, 8);
        log.Inode(3, 1, Reg, 3, 0, Fill('g', 3));
        log.Dirent(1, "old", 4, 3, 8);
        log.Inode(4, 1, Reg, 7, 0, Encoding.ASCII.GetBytes("renamed"));
        log.Dirent(1, "dir", 5, 4, 4);
        log.Inode(5, 1, Dir, 0, 0, Array.Empty<byte>());
        log.Dirent(5, "inner", 6, 5, 10);
        log.Inode(6, 1, Lnk, 6, 0, Encoding.ASCII.GetBytes("target"));
        log.Dirent(1, "trunc", 7, 6, 8);
        // Stored uncompressed, so that the log spans several erase blocks.
        for (uint i = 0; i < 5; i++) log.Inode(7, i + 1, Reg, 20000, i * 4096, Fill('T', (int)Math.Min(4096, 20000 - i * 4096)));

        // What the kernel appends later.
        log.Inode(2, 4, Reg, 6000, 100, Fill('B', 100), compr: 2, packed: new byte[] { (byte)'B', 99 });   // rtime: 'B' and 99 repeats
        log.Inode(2, 5, 0x8180, 6000, 0, Array.Empty<byte>());                                         // chmod 0600
        log.Dirent(1, "gone", 0, 7, 0);                                                                // unlink
        log.Dirent(1, "new", 4, 8, 8);                                                                 // rename old -> new
        log.Dirent(1, "old", 0, 9, 0);
        log.Inode(7, 6, Reg, 3000, 0, Array.Empty<byte>());                                            // truncate to 3000
        log.Inode(7, 7, Reg, 9000, 3000, Array.Empty<byte>(), compr: 1, dsize: 6000);                 // extend with a hole
        log.Inode(7, 8, Reg, 9000, 8000, Fill('Z', 100));
        log.Inode(2, 10, Reg, 6000, 0, Fill('X', 10), obsolete: true);                                 // garbage-collected: ignored
        log.Inode(2, 3, Reg, 6000, 100, Fill('O', 100));                                               // older version found later
        log.Inode(2, 20, Reg, 6000, 0, Fill('Q', 50), badDataCrc: true);                               // torn write: ignored
        log.Dirent(1, "badmode", 8, 10, 8);
        log.Inode(8, 1, 0x0FFF, 0, 0, Array.Empty<byte>());                                            // no file type in the mode
        byte[] image = log.ToArray();
        // An interrupted write at the very end: half a node.
        var torn = new J2Log(be, 1 << 20);
        torn.Inode(2, 21, Reg, 6000, 0, Fill('R', 300));
        byte[] tail = torn.ToArray()[12..200];
        byte[] all = image.Concat(tail).ToArray();

        string path = Path.Combine(_dir, "log.jffs2");
        File.WriteAllBytes(path, all);
        using ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
        Check(doc.Format == ImageFormat.Jffs2 && doc.Jffs2!.BigEndian == be, "not read as JFFS2 of the right byte order");
        FsTree t = doc.Tree;
        Check(t.Resolve("/gone") is null && t.Resolve("/old") is null, "deleted or renamed names are still there");
        Check(t.Resolve("/badmode") is null && doc.Warnings.Any(w => w.Contains("'/badmode' was skipped") && w.Contains("invalid mode 07777")),
            "an inode without a file type: " + string.Join(" | ", doc.Warnings));
        byte[] Content(string p) => ReadJ2(t.Resolve(p)!.Inode.Data!);
        byte[] keep = Content("/keep");
        byte[] expectKeep = Fill('A', 100).Concat(Fill('B', 100)).Concat(Fill('A', 5800)).ToArray();
        Check(keep.AsSpan().SequenceEqual(expectKeep), "the newest data does not win in /keep");
        Check(t.Resolve("/keep")!.Inode.Mode == 0x180, "the metadata-only node (chmod) was lost");
        Check(Encoding.ASCII.GetString(Content("/new")) == "renamed", "rename");
        Check(t.Resolve("/dir/inner")!.Inode.LinkTarget == "target", "symlink in a subdirectory");
        byte[] trunc = Content("/trunc");
        byte[] expectTrunc = Fill('T', 3000).Concat(new byte[5000]).Concat(Fill('Z', 100)).Concat(new byte[900]).ToArray();
        Check(trunc.AsSpan().SequenceEqual(expectTrunc), "truncate and extend with a hole");
        Check(doc.Jffs2.ObsoleteNodes == 1 && doc.Jffs2.DamagedNodes >= 2, $"obsolete {doc.Jffs2.ObsoleteNodes}, damaged {doc.Jffs2.DamagedNodes}");
        Check(doc.Warnings.Any(w => w.Contains("damaged JFFS2 node")), "no warning about the damaged nodes");
        Check(doc.Jffs2.EraseBlockSize == 16384 && doc.Jffs2.EraseBlockSizeKnown, "erase block size " + doc.Jffs2.EraseBlockSize);

        // Saved again, the image holds just the current state, and reads the same.
        string again = Path.Combine(_dir, "log2.jffs2");
        doc.Save(again, doc.CreateSaveOptions()).Document.Dispose();
        using ImageDocument re = ImageDocument.Open(again, _dir, searchEmbedded: false);
        Check(ReadJ2(re.Tree.Resolve("/trunc")!.Inode.Data!).AsSpan().SequenceEqual(expectTrunc) && re.Jffs2!.ObsoleteNodes == 0 && re.Jffs2.DamagedNodes == 0,
            "the saved log differs");
        File.Delete(path);
        File.Delete(again);
    }

    private static byte[] ReadJ2(IFileData data)
    {
        using Stream s = data.OpenRead();
        var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
