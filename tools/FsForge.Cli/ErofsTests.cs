using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FsForge.Core;
using FsForge.Core.Compression;
using FsForge.Core.Erofs;
using FsForge.Core.Images;
using FsForge.Core.Ubifs;

namespace FsForge.Cli;

/// <summary>
/// EROFS: round trips with every compressor and cluster size the writer has, the layout it promises (short
/// inodes, shared attributes, a valid checksum), an image put together by hand with the variants only
/// mkfs.erofs makes (full cluster indexes, deduplicated clusters, inline and interlaced clusters, fragments,
/// chunk-based files, DEFLATE and Zstandard, long attribute name prefixes), and EROFS inside a disk image
/// and a UBI volume.
/// </summary>
internal static partial class SelfTest
{
    private static void ErofsTests(FsTree tree, string reference)
    {
        var variants = new (string Name, ErofsOptions Options)[]
        {
            ("uncompressed", new ErofsOptions { Compression = ErofsCompression.None }),
            ("lz4", new ErofsOptions { Compression = ErofsCompression.Lz4 }),
            ("lz4hc", new ErofsOptions { Compression = ErofsCompression.Lz4Hc }),
            ("lz4hc level 12, 16 KiB clusters", new ErofsOptions { Compression = ErofsCompression.Lz4Hc, Lz4Level = 12, PclusterSize = 16384 }),
            ("lz4hc, 1 MiB clusters", new ErofsOptions { Compression = ErofsCompression.Lz4Hc, Lz4Level = 3, PclusterSize = 1 << 20 }),
            ("lzma", new ErofsOptions { Compression = ErofsCompression.Lzma, LzmaEffort = 0 }),
            ("lzma, 64 KiB clusters", new ErofsOptions { Compression = ErofsCompression.Lzma, LzmaEffort = 0, PclusterSize = 65536, LzmaDictionarySize = 1 << 16 }),
            ("no inline data, no duplicates, no shared attributes", new ErofsOptions { InlineData = false, Deduplicate = false, ShareXattrs = false }),
            ("one thread, fixed time, name and UUID", new ErofsOptions { Threads = 1, BuildTime = 1700000001, VolumeName = "rootfs", Uuid = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef") }),
        };
        foreach ((string name, ErofsOptions o) in variants)
            Test($"erofs {name}", () => RoundTrip(tree, reference, "w.erofs", new SaveOptions { Format = ImageFormat.Erofs, Erofs = o }));
        Test("erofs: layout, superblock and settings kept on saving", () => ErofsLayout(tree));
        Test("erofs: data that does not shrink, large files, many small ones", ErofsData);
        Test("erofs: variants only mkfs.erofs writes are read (image built by hand)", ErofsHandBuilt);
        Test("erofs: unsupported layouts and invalid options are rejected", () => ErofsLimits(tree));
        Test("erofs: root partition of a disk image is edited in place and enlarged", () => ErofsDisk(tree, reference));
        Test("erofs: a UBI volume is replaced, the other volumes are carried over", () => ErofsUbi(tree, reference));
    }

    private static SaveOptions ErofsSave(ErofsOptions? o = null) => new() { Format = ImageFormat.Erofs, Erofs = o ?? new ErofsOptions() };

    private static void ErofsLayout(FsTree tree)
    {
        var o = new ErofsOptions
        {
            Compression = ErofsCompression.Lzma, LzmaEffort = 0, PclusterSize = 32768,
            VolumeName = "selftest", Uuid = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        };
        string path = WriteFile("layout.erofs", WriteImage(tree, ErofsSave(o)));
        Check(ImageDocument.Detect(path) == ImageFormat.Erofs, "detected as " + ImageDocument.Detect(path));
        long firstSize = new FileInfo(path).Length;
        ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
        try
        {
            ErofsInfo i = doc.Erofs!;
            Check(doc.FormatName == "EROFS" && doc.Warnings.Count == 0, "warnings: " + string.Join("; ", doc.Warnings));
            Check(i.BlockSize == 4096 && i.SizeBytes == firstSize, $"size {i.SizeBytes} of {firstSize}");
            Check(i.Compression == ErofsCompression.Lzma && i.PclusterSize == 32768 && i.BigPcluster && i.LzmaDictionarySize == 1 << 20,
                $"compression {i.Compression}, cluster {i.PclusterSize}, dictionary {i.LzmaDictionarySize}");
            Check(i.VolumeName == "selftest" && i.Uuid == o.Uuid && i.SuperblockChecksum, $"name '{i.VolumeName}', UUID {i.Uuid}");
            Check(i.InodeCount == doc.Tree.Walk().Select(n => n.Inode).Distinct().Count(), "inode count " + i.InodeCount);
            // The time of the image is the one most inodes have (the 1500 small files do not share one, the directories do).
            Check(i.FeatureNames.SequenceEqual(new[] { "sb_csum", "mtime", "0padding", "big_pcluster" }), "features: " + string.Join(' ', i.FeatureNames));

            // Saved again without options: the same method, cluster size, name and UUID.
            SaveOptions same = doc.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir });
            Check(same.Format == ImageFormat.Erofs && same.Erofs.Compression == ErofsCompression.Lzma && same.Erofs.PclusterSize == 32768, "options from the image");
            same.Erofs.LzmaEffort = 0;
            AddFile(doc, "/etc", "added", Bytes(new Random(4), 12345, compressible: true));
            doc = doc.Save(path, same).Document;
            i = doc.Erofs!;
            Check(i.Compression == ErofsCompression.Lzma && i.PclusterSize == 32768 && i.VolumeName == "selftest" && i.Uuid == o.Uuid, "settings after saving");
            Check(doc.Tree.Resolve("/etc/added") is { Inode.Size: 12345 }, "the added file is missing");
        }
        finally
        {
            doc.Dispose();
        }

        // Inodes that share the time of the image, small owners and link counts take 32 bytes; the others 64.
        using var t = new FsTree(0x1ED, 1700000000);
        for (int n = 0; n < 2000; n++) t.CreateFile(t.Root, "f" + n, null, 0x1A4, 0, 0, 1700000000);
        long compact = WriteImage(t, ErofsSave()).Length;
        int k = 0;
        foreach (FsNode n in t.Walk().Skip(1))
        {
            if (k % 2 == 0) n.Inode.MTime = 1700000001 + k;
            else n.Inode.Uid = 70000;
            k++;
        }
        long extended = WriteImage(t, ErofsSave()).Length;
        Check(extended - compact >= 2000 * 32 - 4096 && extended - compact <= 2000 * 32 + 8192, $"short inodes: {compact} bytes, long ones: {extended}");
        RoundTrip(t, Program.DumpToString(t), "inodes.erofs", ErofsSave());

        // An attribute many files carry is stored once.
        using var x = new FsTree(0x1ED, 1700000000);
        byte[] label = Encoding.ASCII.GetBytes("u:object_r:system_file_with_a_long_label:s0\0");
        for (int n = 0; n < 1000; n++)
        {
            FsNode f = x.CreateFile(x.Root, "f" + n, null, 0x1A4, 0, 0, 1700000000);
            f.Inode.XAttrs = new(StringComparer.Ordinal) { ["security.selinux"] = label, ["user.own"] = BitConverter.GetBytes(n) };
        }
        x.Root.Inode.XAttrs = new(StringComparer.Ordinal)
        {
            ["system.posix_acl_default"] = new byte[] { 2, 0, 0, 0, 1, 0, 7, 0, 255, 255, 255, 255 },
            ["trusted.big"] = Bytes(new Random(1), 60000),
        };
        long shared = WriteImage(x, ErofsSave()).Length, separate = WriteImage(x, ErofsSave(new ErofsOptions { ShareXattrs = false })).Length;
        Check(separate - shared > 1000 * 40, $"shared attributes: {shared} bytes, separate: {separate}");
        foreach (bool share in new[] { true, false })
        {
            string xp = WriteFile("xattr.erofs", WriteImage(x, ErofsSave(new ErofsOptions { ShareXattrs = share })));
            using ImageDocument xd = ImageDocument.Open(xp, _dir, searchEmbedded: false);
            var all = new TreeComparer.Rules();
            List<string> diff = TreeComparer.Compare(x, xd.Tree, all, content: true);
            Check(diff.Count == 0, "attributes: " + string.Join("; ", diff.Take(3)));
        }

        // Attributes EROFS has no namespace for are dropped, and that is said.
        using var u = new FsTree(0x1ED, 1700000000);
        u.Root.Inode.XAttrs = new(StringComparer.Ordinal) { ["system.other"] = new byte[] { 1 }, ["user.kept"] = new byte[] { 2 } };
        using var ms = new MemoryStream();
        IReadOnlyList<string> warnings = ImageDocument.WriteTo(u, ms, ErofsSave());
        Check(warnings.Count == 1 && warnings[0].Contains("system.other"), "warnings: " + string.Join("; ", warnings));

        // A damaged superblock is noticed through its checksum.
        byte[] image = WriteImage(tree, ErofsSave(new ErofsOptions { Compression = ErofsCompression.Lz4 }));
        image[1024 + 70] ^= 1;      // a byte of the volume name
        using ImageDocument bad = ImageDocument.Open(WriteFile("badsum.erofs", image), _dir, searchEmbedded: false);
        Check(bad.Warnings.Any(w => w.Contains("checksum")), "a wrong superblock checksum was not reported");
    }

    private static void ErofsData()
    {
        var rnd = new Random(8);
        using var t = new FsTree(0x1ED, 1700000000);
        // Random data between compressible data: clusters of both kinds in one file, also across the pieces
        // a large file is compressed in.
        byte[] mixed = new byte[9 << 20];
        Bytes(rnd, 3 << 20, compressible: true).CopyTo(mixed, 0);
        Bytes(rnd, (3 << 20) + 777).CopyTo(mixed, 3 << 20);
        Bytes(rnd, (3 << 20) - 777, compressible: true).CopyTo(mixed, (6 << 20) + 777);
        t.CreateFile(t.Root, "mixed", new MemoryFileData(mixed), mtime: 1700000000);
        t.CreateFile(t.Root, "random", new MemoryFileData(Bytes(rnd, 300001)), mtime: 1700000000);
        t.CreateFile(t.Root, "zeros", new MemoryFileData(new byte[20 << 20]), mtime: 1700000000);
        // Larger than what is read into memory at once: streamed, in pieces.
        byte[] large = new byte[(40 << 20) + 5];
        for (int i = 0; i + 60000 <= large.Length; i += 1 << 20) Bytes(rnd, 60000).CopyTo(large, i);
        t.CreateFile(t.Root, "large", new MemoryFileData(large), mtime: 1700000000);
        t.CreateFile(t.Root, "large-copy", new MemoryFileData((byte[])large.Clone()), mtime: 1700000000);
        foreach (int size in new[] { 4097, 8191, 8192, 8193, 12288, 100000 })
        {
            t.CreateFile(t.Root, "text" + size, new MemoryFileData(Bytes(rnd, size, compressible: true)), mtime: 1700000000);
            t.CreateFile(t.Root, "noise" + size, new MemoryFileData(Bytes(rnd, size)), mtime: 1700000000);
        }
        FsNode dir = t.CreateDirectory(t.Root, "big-directory", mtime: 1700000000);
        for (int i = 0; i < 5000; i++) t.CreateFile(dir, $"{(char)('0' + i % 75)}entry-{i}", new MemoryFileData(BitConverter.GetBytes(i)), mtime: 1700000000);
        string reference = Program.DumpToString(t);

        foreach (ErofsOptions o in new[]
        {
            new ErofsOptions { Compression = ErofsCompression.Lz4Hc, Lz4Level = 4 },
            new ErofsOptions { Compression = ErofsCompression.Lz4, PclusterSize = 262144 },
            new ErofsOptions { Compression = ErofsCompression.Lzma, LzmaEffort = 0, PclusterSize = 131072 },
            new ErofsOptions { Compression = ErofsCompression.None },
        })
        {
            byte[] image = WriteImage(t, ErofsSave(o));
            string path = WriteFile("data.erofs", image);
            using ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
            Same(reference, Program.DumpToString(doc.Tree), $"data {o.Compression} {o.PclusterSize}");
            if (o.Compression == ErofsCompression.None) continue;
            // Zeros and the copy of the large file cost next to nothing; random data costs no more than itself.
            long raw = 300001 + (3 << 20) + 777 + 40 * 60000 + 4097 + 8191 + 8192 + 8193 + 12288 + 100000;
            Check(image.Length < raw + (4 << 20), $"{o.Compression} {o.PclusterSize}: {image.Length} bytes");
        }
    }

    // ------------------------------------------------------- hand-built image

    /// <summary>Puts an EROFS image together structure by structure: metadata in blocks 1 to 8, data behind them.</summary>
    private sealed class ErofsHand
    {
        public const int Blk = 4096, MetaBlocks = 8;
        private readonly byte[] _meta = new byte[MetaBlocks * Blk];
        private readonly MemoryStream _data = new();
        private int _metaPos = 32;

        public uint NextBlock => (uint)(1 + MetaBlocks + _data.Length / Blk);

        /// <summary>Appends whole blocks (the last one padded) and returns the address of the first.</summary>
        public uint Blocks(ReadOnlySpan<byte> bytes)
        {
            uint first = NextBlock;
            _data.Write(bytes);
            if (bytes.Length % Blk != 0) _data.Write(new byte[Blk - bytes.Length % Blk]);
            return first;
        }

        /// <summary>A compressed cluster of one block: the data at its end, zeros in front.</summary>
        public uint Cluster(ReadOnlySpan<byte> packed)
        {
            byte[] block = new byte[Blk];
            packed.CopyTo(block.AsSpan(Blk - packed.Length));
            return Blocks(block);
        }

        /// <summary>Writes a 64-byte inode, what follows it (attributes, indexes, inline data) and returns its number.</summary>
        public ulong Inode(int mode, long size, int layout, uint union, byte[]? xattrs = null, byte[]? after = null, long mtime = 1700000000,
            uint uid = 0, uint gid = 0, uint links = 1)
        {
            int total = 64 + (xattrs?.Length ?? 0) + (after?.Length ?? 0);
            if (_metaPos % Blk + total > Blk) _metaPos = (_metaPos / Blk + 1) * Blk;
            Span<byte> b = _meta.AsSpan(_metaPos);
            BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)(1 | layout << 1));
            BinaryPrimitives.WriteUInt16LittleEndian(b[2..], (ushort)(xattrs is null ? 0 : (xattrs.Length - 12) / 4 + 1));
            BinaryPrimitives.WriteUInt16LittleEndian(b[4..], (ushort)mode);
            BinaryPrimitives.WriteUInt64LittleEndian(b[8..], (ulong)size);
            BinaryPrimitives.WriteUInt32LittleEndian(b[16..], union);
            BinaryPrimitives.WriteUInt32LittleEndian(b[24..], uid);
            BinaryPrimitives.WriteUInt32LittleEndian(b[28..], gid);
            BinaryPrimitives.WriteUInt64LittleEndian(b[32..], (ulong)mtime);
            BinaryPrimitives.WriteUInt32LittleEndian(b[44..], links);
            xattrs?.CopyTo(b[64..]);
            after?.CopyTo(b[(64 + (xattrs?.Length ?? 0))..]);
            ulong nid = (ulong)(_metaPos / 32);
            _metaPos = (_metaPos + total + 31) & ~31;
            return nid;
        }

        /// <summary>A 32-byte inode: 16-bit owners and link count, the time of the image.</summary>
        public ulong CompactInode(int mode, uint size, int layout, uint union, byte[] after, ushort uid, ushort gid)
        {
            if (_metaPos % Blk + 32 + after.Length > Blk) _metaPos = (_metaPos / Blk + 1) * Blk;
            Span<byte> b = _meta.AsSpan(_metaPos);
            BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)(layout << 1));
            BinaryPrimitives.WriteUInt16LittleEndian(b[4..], (ushort)mode);
            BinaryPrimitives.WriteUInt16LittleEndian(b[6..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(b[8..], size);
            BinaryPrimitives.WriteUInt32LittleEndian(b[16..], union);
            BinaryPrimitives.WriteUInt16LittleEndian(b[24..], uid);
            BinaryPrimitives.WriteUInt16LittleEndian(b[26..], gid);
            after.CopyTo(b[32..]);
            ulong nid = (ulong)(_metaPos / 32);
            _metaPos = (_metaPos + 32 + after.Length + 31) & ~31;
            return nid;
        }

        public byte[] Image(ulong rootNid, ulong packedNid, int prefixCount, uint prefixStart, uint xattrBlock, uint incompat, int algorithms, byte[] configs, long epoch)
        {
            byte[] image = new byte[(1 + MetaBlocks) * Blk + _data.Length];
            Span<byte> sb = image.AsSpan(1024);
            BinaryPrimitives.WriteUInt32LittleEndian(sb, 0xE0F5E1E2);
            sb[12] = 12;
            BinaryPrimitives.WriteUInt16LittleEndian(sb[14..], (ushort)rootNid);
            BinaryPrimitives.WriteUInt64LittleEndian(sb[16..], 30);
            BinaryPrimitives.WriteUInt64LittleEndian(sb[24..], (ulong)epoch);
            BinaryPrimitives.WriteUInt32LittleEndian(sb[36..], (uint)(image.Length / Blk));
            BinaryPrimitives.WriteUInt32LittleEndian(sb[40..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(sb[44..], xattrBlock);
            Encoding.ASCII.GetBytes("hand-built").CopyTo(sb[64..]);
            BinaryPrimitives.WriteUInt32LittleEndian(sb[80..], incompat);
            BinaryPrimitives.WriteUInt16LittleEndian(sb[84..], (ushort)algorithms);
            sb[91] = (byte)prefixCount;
            BinaryPrimitives.WriteUInt32LittleEndian(sb[92..], prefixStart);
            BinaryPrimitives.WriteUInt64LittleEndian(sb[96..], packedNid);
            configs.CopyTo(sb[128..]);
            _meta.CopyTo(image, Blk);
            _data.ToArray().CopyTo(image, (1 + MetaBlocks) * Blk);
            return image;
        }

        /// <summary>One block of a directory: the entries sorted by name, the names behind the table.</summary>
        public static byte[] Directory(params (string Name, ulong Nid, int Type)[] entries)
        {
            var sorted = entries.OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
            var names = sorted.Select(e => Encoding.UTF8.GetBytes(e.Name)).ToArray();
            byte[] b = new byte[sorted.Length * 12 + names.Sum(n => n.Length)];
            int at = sorted.Length * 12;
            for (int i = 0; i < sorted.Length; i++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(i * 12), sorted[i].Nid);
                BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i * 12 + 8), (ushort)at);
                b[i * 12 + 10] = (byte)sorted[i].Type;
                names[i].CopyTo(b, at);
                at += names[i].Length;
            }
            return b;
        }

        public static byte[] Xattr(int index, string name, byte[] value)
        {
            byte[] n = Encoding.UTF8.GetBytes(name);
            byte[] e = new byte[(4 + n.Length + value.Length + 3) & ~3];
            e[0] = (byte)n.Length;
            e[1] = (byte)index;
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(2), (ushort)value.Length);
            n.CopyTo(e, 4);
            value.CopyTo(e, 4 + n.Length);
            return e;
        }

        public static byte[] XattrBody(uint[] shared, params byte[][] inline)
        {
            var w = new MemoryStream();
            w.Write(new byte[4]);
            w.WriteByte((byte)shared.Length);
            w.Write(new byte[7]);
            foreach (uint id in shared) w.Write(BitConverter.GetBytes(id));
            foreach (byte[] e in inline) w.Write(e);
            return w.ToArray();
        }

        /// <summary>
        /// The map header and the full (8 bytes per 4 KiB) cluster index of a compressed file, padded in front so
        /// that it starts at a multiple of eight bytes behind an inode with <paramref name="xattrSize"/> bytes of attributes.
        /// </summary>
        public static byte[] FullIndex(int xattrSize, int advise, int algorithms, uint headerWord, params (int Length, uint Block, int Type, bool Partial)[] extents)
        {
            var w = new MemoryStream();
            w.Write(new byte[(8 - (64 + xattrSize) % 8) % 8]);
            w.Write(BitConverter.GetBytes(headerWord));
            w.Write(BitConverter.GetBytes((ushort)advise));
            w.WriteByte((byte)algorithms);
            w.WriteByte(0);
            w.Write(new byte[8]);
            void Entry(int word, int offset, uint value)
            {
                w.Write(BitConverter.GetBytes((ushort)word));
                w.Write(BitConverter.GetBytes((ushort)offset));
                w.Write(BitConverter.GetBytes(value));
            }
            int offset = 0;
            foreach ((int length, uint block, int type, bool partial) in extents)
            {
                int word = type | (partial ? 0x8000 : 0), count = length;
                int d0 = 0, d1 = (offset + count) / Blk;
                if (d1 == 0)
                {
                    Entry(word, offset, block);
                    offset = 0;
                    continue;
                }
                do
                {
                    if (d0 == 0) Entry(word, offset, block);
                    else Entry(2, 0, (uint)(d0 | d1 << 16));
                    count -= Blk - offset;
                    offset = 0;
                    d0++;
                    d1--;
                }
                while (count >= Blk);
                offset = count;
            }
            if (offset != 0) Entry(0, offset, 0);
            return w.ToArray();
        }

        /// <summary>A block whose content is rotated so that it lies where it would in a file made of whole blocks.</summary>
        public static byte[] Interlaced(ReadOnlySpan<byte> data, long position)
        {
            byte[] block = new byte[Blk];
            int skip = (int)(position % Blk), right = Math.Min(Blk - skip, data.Length);
            data[..right].CopyTo(block.AsSpan(skip));
            data[right..].CopyTo(block);
            return block;
        }
    }

    private static void ErofsHandBuilt()
    {
        const int Reg = 0x81A4, Dir = 0x41ED, Lnk = 0xA1FF, Chr = 0x21B6;
        const int FlatPlain = 0, Full = 1, FlatInline = 2, Chunked = 4;
        var rnd = new Random(21);
        var h = new ErofsHand();
        var lz4 = new Lz4FixedEncoder(9);
        var expected = new Dictionary<string, byte[]>();
        var entries = new List<(string, ulong, int)>();
        byte[] Pack(ReadOnlySpan<byte> data, int room, out int taken)
        {
            byte[] dst = new byte[room];
            int n = lz4.Compress(data, dst, out taken);
            return dst[..n];
        }
        void File(string name, byte[] content, ulong nid)
        {
            expected[name] = content;
            entries.Add((name, nid, 1));
        }

        // Uncompressed: all blocks; a block and an inline tail; a 32-byte inode with an inline file.
        byte[] flat = Bytes(rnd, 5000);
        ulong flatNid = h.Inode(Reg, flat.Length, FlatPlain, h.Blocks(flat), links: 2);
        File("flat", flat, flatNid);
        byte[] inline = Bytes(rnd, 4096 + 300);
        File("inline", inline, h.Inode(Reg, inline.Length, FlatInline, h.Blocks(inline.AsSpan(0, 4096)), after: inline[4096..]));
        byte[] small = Encoding.ASCII.GetBytes("a 32-byte inode\n");
        entries.Add(("compact", h.CompactInode(Reg, (uint)small.Length, FlatInline, 0xFFFFFFFF, small, 1000, 100), 1));

        // Chunk-based files with holes: a table of block addresses, and a table of 8-byte chunk indexes.
        byte[] map = new byte[3 * 4096 - 100];
        Bytes(rnd, 4096).CopyTo(map, 0);
        Bytes(rnd, 4096 - 100).CopyTo(map, 8192);
        uint m0 = h.Blocks(map.AsSpan(0, 4096)), m2 = h.Blocks(map.AsSpan(8192));
        var table = new MemoryStream();
        foreach (uint a in new[] { m0, 0xFFFFFFFF, m2 }) table.Write(BitConverter.GetBytes(a));
        File("chunk-map", map, h.Inode(Reg, map.Length, Chunked, 0, after: table.ToArray()));
        byte[] idx = new byte[4 * 8192 + 1];
        Bytes(rnd, 16384).CopyTo(idx, 8192);
        idx[^1] = 42;
        uint i1 = h.Blocks(idx.AsSpan(8192, 16384)), i4 = h.Blocks(idx.AsSpan(4 * 8192));
        table = new MemoryStream();
        foreach (uint a in new[] { 0xFFFFFFFF, i1, i1 + 2, 0xFFFFFFFF, i4 })
        {
            table.Write(new byte[4]);
            table.Write(BitConverter.GetBytes(a));
        }
        File("chunk-index", idx, h.Inode(Reg, idx.Length, Chunked, 0x20 | 1, after: table.ToArray()));

        // A full cluster index; the last extent uses the beginning of the first cluster again (deduplication).
        byte[] a10 = Bytes(rnd, 10000, compressible: true), b9 = Bytes(rnd, 9000, compressible: true);
        byte[] pa = Pack(a10, 4096, out int ta), pb = Pack(b9, 4096, out int tb);
        Check(ta == a10.Length && tb == b9.Length, "the test data does not fit one cluster");
        uint ca = h.Cluster(pa), cb = h.Cluster(pb);
        byte[] legacy = a10.Concat(b9).Concat(a10.Take(5000)).ToArray();
        File("legacy", legacy, h.Inode(Reg, legacy.Length, Full, 2,
            after: ErofsHand.FullIndex(0, 0, 0, 0, (10000, ca, 1, false), (9000, cb, 1, false), (5000, ca, 1, true))));

        // The tail of a file next to its inode: compressed, and as it is.
        byte[] noise = Bytes(rnd, 4096), text = Bytes(rnd, 1904, compressible: true);
        byte[] pt = Pack(text, 1000, out int tt);
        Check(tt == text.Length, "the tail does not fit");
        byte[] ztailIndex = ErofsHand.FullIndex(0, 0x8, 0, (uint)pt.Length << 16, (4096, h.Blocks(noise), 0, false), (1904, 0, 1, false));
        File("ztail", noise.Concat(text).ToArray(), h.Inode(Reg, 6000, Full, 1, after: ztailIndex.Concat(pt).ToArray()));
        byte[] rawTail = Bytes(rnd, 700);
        byte[] rawIndex = ErofsHand.FullIndex(0, 0x8, 0, 700u << 16, (4096, h.Blocks(noise), 0, false), (700, 0, 0, false));
        File("ztail-raw", noise.Concat(rawTail).ToArray(), h.Inode(Reg, 4796, Full, 1, after: rawIndex.Concat(rawTail).ToArray()));

        // The packed inode: the table of attribute name prefixes, then the fragments.
        byte[] prefix = Encoding.ASCII.GetBytes("\u0001long.prefix.");       // base index 1 (user.) and the rest
        byte[] fragment = Bytes(rnd, 3000), tailFragment = Bytes(rnd, 1500);
        var packed = new MemoryStream();
        packed.Write(new byte[8]);
        packed.Write(BitConverter.GetBytes((ushort)prefix.Length));
        packed.Write(prefix);
        while (packed.Length % 4 != 0) packed.WriteByte(0);
        long fragmentAt = packed.Length;
        packed.Write(fragment);
        long tailAt = packed.Length;
        packed.Write(tailFragment);
        ulong packedNid = h.Inode(Reg, packed.Length, FlatPlain, h.Blocks(packed.ToArray()));
        // A file that is a fragment as a whole: the header is the offset with the top bit set.
        File("fragment", fragment, h.Inode(Reg, fragment.Length, Full, 0, after: BitConverter.GetBytes((ulong)fragmentAt | 1UL << 63)));
        // A compressed file whose tail is a fragment.
        byte[] fragIndex = ErofsHand.FullIndex(0, 0x20, 0, (uint)tailAt, (10000, ca, 1, false), (1500, 0, 1, false));
        File("fragment-tail", a10.Concat(tailFragment).ToArray(), h.Inode(Reg, 11500, Full, 1, after: fragIndex));

        // Uncompressed clusters stored "interlaced".
        byte[] inter = Bytes(rnd, 10000 + 4096 + 500);
        a10.CopyTo(inter, 0);
        byte[] interIndex = ErofsHand.FullIndex(0, 0x10, 0, 0,
            (10000, ca, 1, false),
            (4096, h.Blocks(ErofsHand.Interlaced(inter.AsSpan(10000, 4096), 10000)), 0, false),
            (500, h.Blocks(ErofsHand.Interlaced(inter.AsSpan(14096), 14096)), 0, false));
        File("interlaced", inter, h.Inode(Reg, inter.Length, Full, 3, after: interIndex));

        // Zstandard and DEFLATE clusters, and a file with two methods (the second kind of head).
        byte[] z30 = Bytes(rnd, 30000, compressible: true);
        using var zstd = new ZstdSharp.Compressor(3);
        byte[] pz = zstd.Wrap(z30).ToArray();
        var deflated = new MemoryStream();
        using (var d = new DeflateStream(deflated, CompressionLevel.Optimal, leaveOpen: true)) d.Write(z30);
        Check(pz.Length < 4096 && deflated.Length < 4096, "the test data does not fit one cluster");
        uint cz = h.Cluster(pz), cd = h.Cluster(deflated.ToArray());
        File("zstd", z30, h.Inode(Reg, z30.Length, Full, 1, after: ErofsHand.FullIndex(0, 0, 3, 0, (30000, cz, 1, false))));
        File("deflate", z30, h.Inode(Reg, z30.Length, Full, 1, after: ErofsHand.FullIndex(0, 0, 2, 0, (30000, cd, 1, false))));
        File("two-methods", a10.Concat(z30).ToArray(), h.Inode(Reg, 40000, Full, 2,
            after: ErofsHand.FullIndex(0, 0, 0 | 3 << 4, 0, (10000, ca, 1, false), (30000, cz, 3, false))));

        // Extended attributes: inline, shared, and with a long name prefix.
        byte[] sharedArea = ErofsHand.Xattr(6, "selinux", Encoding.ASCII.GetBytes("u:object_r:shared:s0"));
        uint xattrBlock = h.Blocks(new byte[40].Concat(sharedArea).ToArray());
        byte[] body = ErofsHand.XattrBody(new uint[] { 10 }, ErofsHand.Xattr(1, "inline", new byte[] { 1, 2, 3 }), ErofsHand.Xattr(0x80, "name", new byte[] { 9 }),
            ErofsHand.Xattr(2, "", new byte[] { 2, 0, 0, 0 }));
        entries.Add(("attributes", h.Inode(Reg, 0, FlatPlain, 0xFFFFFFFF, xattrs: body), 1));

        // A directory in a block of its own, with a hard link, a symbolic link and a device.
        ulong link = h.Inode(Lnk, 7, FlatInline, 0xFFFFFFFF, after: Encoding.ASCII.GetBytes("../flat"));
        ulong device = h.Inode(Chr, 0, FlatPlain, (uint)((300 & 0xFF) | (4 << 8) | ((300 & ~0xFF) << 12)));
        ulong subNid = 0;
        // The number of the directory is needed for its own "." entry: it is placed first, its block written after.
        byte[] subDir = Array.Empty<byte>();
        uint subBlock = h.Blocks(new byte[4096]);
        subNid = h.Inode(Dir, 0, FlatPlain, subBlock, links: 2);
        entries.Add(("sub", subNid, 2));

        byte[] rootDir = ErofsHand.Directory(entries.Concat(new[] { (".", 1UL, 2), ("..", 1UL, 2) }).ToArray());
        // The root is inode 1 only if it is the first: it was not, so its real number goes into its entries.
        ulong rootNid = h.Inode(Dir, rootDir.Length, FlatInline, 0xFFFFFFFF, after: rootDir, links: 3);
        rootDir = ErofsHand.Directory(entries.Concat(new[] { (".", rootNid, 2), ("..", rootNid, 2) }).ToArray());
        subDir = ErofsHand.Directory((".", subNid, 2), ("..", rootNid, 2), ("hardlink", flatNid, 1), ("link", link, 7), ("ttyS300", device, 3));

        var configs = new MemoryStream();
        configs.Write(new byte[] { 14, 0, 0xFF, 0xFF, 1, 0 });      // lz4: distance, cluster blocks
        configs.Write(new byte[10]);
        configs.Write(new byte[] { 6, 0, 15, 0, 0, 0, 0, 0 });      // deflate: window bits
        configs.Write(new byte[] { 6, 0, 0, 0, 0, 0, 0, 0 });       // zstd
        byte[] image = h.Image(rootNid, packedNid, 1, 2, xattrBlock, incompat: 0x1 | 0x2 | 0x4 | 0x10 | 0x20 | 0x40, algorithms: 1 | 4 | 8, configs.ToArray(), epoch: 1600000000);
        // The blocks that depend on inode numbers: the directory of "sub", and the root's own entries.
        subDir.CopyTo(image, (int)subBlock * 4096);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(4096 + (int)subNid * 32 + 8), (ulong)subDir.Length);
        rootDir.CopyTo(image, 4096 + (int)rootNid * 32 + 64);

        string path = WriteFile("hand.erofs", image);
        Check(ImageDocument.Detect(path) == ImageFormat.Erofs, "detected as " + ImageDocument.Detect(path));
        ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
        try
        {
            Check(doc.Warnings.Count == 0, "warnings: " + string.Join("; ", doc.Warnings));
            ErofsInfo info = doc.Erofs!;
            Check(info.VolumeName == "hand-built" && info.Fragments && info.TailPacking && info.ChunkedFiles && info.BuildTime == 1600000000, "info of the image");
            Check(info.Algorithms.SequenceEqual(new[] { ErofsCompression.Lz4, ErofsCompression.Deflate, ErofsCompression.Zstd }), "methods: " + string.Join(',', info.Algorithms));
            foreach ((string name, byte[] content) in expected)
            {
                FsNode n = doc.Tree.Resolve("/" + name) ?? throw new TestFailure($"'{name}' is missing");
                Check(n.Inode.Size == content.Length, $"{name}: size {n.Inode.Size}, expected {content.Length}");
                using var ms = new MemoryStream();
                using (Stream s = n.Inode.Data!.OpenRead()) s.CopyTo(ms);
                Check(ms.ToArray().AsSpan().SequenceEqual(content), $"{name}: the content differs");
            }
            Check(doc.Tree.Root.ChildCount == entries.Count, $"{doc.Tree.Root.ChildCount} entries in the root");
            FsInode c = doc.Tree.Resolve("/compact")!.Inode;
            using (Stream s = c.Data!.OpenRead())
                Check(c.Uid == 1000 && c.Gid == 100 && c.MTime == 1600000000 && new StreamReader(s).ReadToEnd() == "a 32-byte inode\n", "the 32-byte inode");
            Check(doc.Tree.Resolve("/chunk-index")!.Inode.Data is ISparseFileData { DataBytesHint: 16384 + 1 }, "holes of a chunk-based file");
            var x = doc.Tree.Resolve("/attributes")!.Inode.XAttrs!;
            Check(x.Count == 4 && x["user.inline"].SequenceEqual(new byte[] { 1, 2, 3 }) && x["user.long.prefix.name"].SequenceEqual(new byte[] { 9 })
                  && Encoding.ASCII.GetString(x["security.selinux"]) == "u:object_r:shared:s0" && x["system.posix_acl_access"].Length == 4,
                "attributes: " + string.Join(", ", x.Keys));
            Check(ReferenceEquals(doc.Tree.Resolve("/sub/hardlink")!.Inode, doc.Tree.Resolve("/flat")!.Inode) && doc.Tree.Resolve("/flat")!.Inode.LinkCount == 2, "the hard link");
            Check(doc.Tree.Resolve("/sub/link")!.Inode.LinkTarget == "../flat", "the symbolic link");
            Check(doc.Tree.Resolve("/sub/ttyS300")!.Inode is { Type: FsNodeType.CharDevice, DevMajor: 4, DevMinor: 300 }, "the device");

            // Saved by FsForge it is one of its own images with the same content.
            string reference = Program.DumpToString(doc.Tree);
            doc = doc.Save(Path.Combine(_dir, "hand-saved.erofs"), doc.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir })).Document;
            Check(doc.Erofs!.Compression == ErofsCompression.Lz4 && doc.Erofs.VolumeName == "hand-built" && !doc.Erofs.Fragments, "the saved image: " + string.Join(' ', doc.Erofs.FeatureNames));
            Same(reference, Program.DumpToString(doc.Tree), "saved again");
        }
        finally
        {
            doc.Dispose();
        }
    }

    // ----------------------------------------------------------------- limits

    private static void ErofsLimits(FsTree tree)
    {
        ExpectFs(() => WriteImage(tree, ErofsSave(new ErofsOptions { Compression = ErofsCompression.Zstd })));
        ExpectFs(() => WriteImage(tree, ErofsSave(new ErofsOptions { PclusterSize = 5000 })));
        ExpectFs(() => WriteImage(tree, ErofsSave(new ErofsOptions { PclusterSize = 2 << 20 })));
        ExpectFs(() => WriteImage(tree, ErofsSave(new ErofsOptions { VolumeName = "seventeen letters" })));
        ExpectFs(() => WriteImage(tree, ErofsSave(new ErofsOptions { Lz4Level = 13 })));
        ExpectFs(() => WriteImage(tree, ErofsSave(new ErofsOptions { LzmaDictionarySize = 1000 })));

        using FsTree small = SmallTree("limits\n");
        byte[] good = WriteImage(small, ErofsSave());
        void Rejected(string what, Action<byte[]> change, string message)
        {
            byte[] bad = (byte[])good.Clone();
            change(bad);
            string path = WriteFile("limit.erofs", bad);
            try
            {
                ImageDocument.Open(path, _dir, searchEmbedded: false).Dispose();
            }
            catch (FsException ex) when (ex is not FsUnexpectedException)
            {
                Check(ex.Message.Contains(message), $"{what}: {ex.Message}");
                return;
            }
            throw new TestFailure(what + " was accepted");
        }
        Rejected("the 48-bit layout", b => b[1024 + 80] |= 0x80, "48-bit");
        Rejected("compressed metadata", b => b[1024 + 81] |= 0x01, "metabox");
        Rejected("an unknown feature", b => b[1024 + 82] |= 0x01, "features");
        Rejected("data on another device", b => { b[1024 + 80] |= 0x08; b[1024 + 86] = 1; }, "device");
        Rejected("directory blocks of another size", b => b[1024 + 90] = 1, "directory blocks");
        Rejected("a root that is not a directory", b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1024 + 14), 2), "");
    }

    // -------------------------------------------------------- disk image, UBI

    private static void ErofsDisk(FsTree tree, string reference)
    {
        var o = ErofsSave(new ErofsOptions { VolumeName = "rootfs" });
        byte[] fs = WriteImage(tree, o);
        long p1 = 1 * MiB, p2 = 3 * MiB, slot = Align(fs.Length, MiB) + MiB;
        byte[] disk = MbrDisk(p2 + fs.Length, new Part(0x0C, p1, 2 * MiB, FakeFat(3, (int)(2 * MiB), "BOOT")), new Part(0x83, p2, slot, fs));
        string path = WriteFile("erofs-disk.img", disk);
        Check(ImageDocument.Detect(path) == ImageFormat.Erofs, "detected as " + ImageDocument.Detect(path));

        ImageDocument doc = ImageDocument.Open(path, _dir);
        try
        {
            PartitionInfo root = doc.Container!.Partition;
            Check(doc.Format == ImageFormat.Erofs && root is { Number: 2, Content: PartitionContent.Erofs, ContentName: "EROFS", Label: "rootfs", IsEditable: true }, "root partition: " + root);
            Same(reference, Program.DumpToString(doc.Tree), "EROFS partition");

            // A small edit: the partition keeps its size, everything in front of it its bytes.
            doc.Tree.Remove(doc.Tree.Resolve("/var/zeros")!);
            AddFile(doc, "/etc", "added.conf", Encoding.ASCII.GetBytes("setting=1\n"));
            ImageDocument.SaveResult r = doc.Save(path, DiskSave(doc));
            doc = r.Document;
            byte[] after = File.ReadAllBytes(path);
            Check(r.Warnings.Count == 0, "warnings: " + string.Join("; ", r.Warnings));
            SameBytes(disk.AsSpan(0, (int)p2), after.AsSpan(0, (int)p2), "data in front of the root partition");
            Check(doc.Container!.Partition.Length == slot && doc.Erofs!.VolumeName == "rootfs" && doc.Erofs.Uuid != Guid.Empty, "the partition after the edit");
            Check(doc.Tree.Resolve("/etc/added.conf") is not null && doc.Tree.Resolve("/var/zeros") is null, "the edit was not saved");

            // More than the partition holds: the last partition of an MBR disk grows.
            AddFile(doc, "/var", "big.bin", Bytes(new Random(10), (int)(3 * MiB)));
            r = doc.Save(path, DiskSave(doc));
            doc = r.Document;
            Check(r.Warnings.Any(w => w.Contains("enlarged")), "the enlarged partition was not reported");
            Check(doc.Container!.Partition.Length > slot && doc.Container.Partition.Offset == p2, "new partition size " + doc.Container.Partition.Length);
            Check(doc.Tree.Resolve("/var/big.bin") is { Inode.Size: 3 * MiB }, "the added file is missing");

            // The filesystem alone, on request.
            SaveOptions alone = DiskSave(doc);
            alone.KeepContainer = false;
            doc = doc.Save(Path.Combine(_dir, "erofs-root.erofs"), alone).Document;
            Check(doc.Container is null && doc.Format == ImageFormat.Erofs && doc.EmbeddedOffset == 0, "the filesystem saved alone");
        }
        finally
        {
            doc.Dispose();
        }
    }

    private static void ErofsUbi(FsTree tree, string reference)
    {
        byte[] fs = WriteImage(tree, ErofsSave(new ErofsOptions { Compression = ErofsCompression.Lz4 }));
        using FsTree small = SmallTree("overlay\n");
        byte[] data = UbifsImageBuilder.FromTree(small).ToArray();
        string path = WriteFile("erofs.ubi", UbifsImageBuilder.WrapUbi(new[]
        {
            new UbifsImageBuilder.Volume("rootfs", fs),
            new UbifsImageBuilder.Volume("rootfs_data", data),
        }, shuffle: new Random(12)));
        Check(ImageDocument.Detect(path) == ImageFormat.Erofs, "detected as " + ImageDocument.Detect(path));

        ImageDocument doc = ImageDocument.Open(path, _dir);
        try
        {
            Check(doc.Format == ImageFormat.Erofs && doc.Ubi?.Volume is { Name: "rootfs", Content: PartitionContent.Erofs }, "volume: " + doc.Ubi?.Volume);
            Same(reference, Program.DumpToString(doc.Tree), "EROFS volume");
            AddFile(doc, "/etc", "added.conf", Encoding.ASCII.GetBytes("setting=1\n"));
            SaveOptions save = doc.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir });
            Check(doc.WritesContainer(save) && doc.WritesUbi(save), "the UBI image is not written as a whole");
            doc = doc.Save(path, save).Document;
            Check(doc.Format == ImageFormat.Erofs && doc.Ubi?.Volume.Name == "rootfs" && doc.Ubi.Image.Volumes.Count == 2, "the saved UBI image");
            Check(doc.Tree.Resolve("/etc/added.conf") is not null, "the edit was not saved");
        }
        finally
        {
            doc.Dispose();
        }
        using ImageDocument other = ImageDocument.Open(path, _dir, volume: "rootfs_data");
        Same(Program.DumpToString(small), Program.DumpToString(other.Tree), "the other volume");
    }
}
