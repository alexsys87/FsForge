using System.Diagnostics;
using System.Text;
using FsForge.Core;
using FsForge.Core.Compression;
using FsForge.Core.Erofs;
using FsForge.Core.Ext;
using FsForge.Core.Host;
using FsForge.Core.Images;
using FsForge.Core.Jffs2;
using FsForge.Core.SquashFs;
using FsForge.Core.Tar;

namespace FsForge.Cli;

/// <summary>
/// Built-in regression tests of the core. They need no external tools and run on Windows and
/// Linux alike: every format is written, read back and compared entry by entry.
/// </summary>
internal static partial class SelfTest
{
    private static int _passed, _failed;
    private static string _dir = "";

    public static int Run(string? workDir)
    {
        _dir = workDir ?? Path.Combine(Path.GetTempPath(), "fsforge-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        var sw = Stopwatch.StartNew();
        try
        {
            Test("codecs", Codecs);
            Test("xz streams", XzStreams);
            Test("host names", HostNameRules);
            Test("tree editing", TreeEditing);

            using FsTree tree = BuildTree(new Random(12345));
            string reference = Program.DumpToString(tree);

            foreach (SquashCompression c in new[] { SquashCompression.Gzip, SquashCompression.Xz, SquashCompression.Lz4, SquashCompression.Zstd })
                foreach (int block in new[] { 4096, 131072, 1 << 20 })
                    Test($"squashfs {c} block={block}", () => RoundTrip(tree, reference, $"sq_{c}_{block}.sqfs", new SaveOptions
                    {
                        Format = ImageFormat.SquashFs,
                        SquashFs = new SquashFsOptions { Compression = c, BlockSize = block, XzEffort = 0, ZstdLevel = 3, GzipLevel = 6 },
                    }));
            Test("squashfs no fragments, no dedup, bcj", () => RoundTrip(tree, reference, "sq_opts.sqfs", new SaveOptions
            {
                Format = ImageFormat.SquashFs,
                SquashFs = new SquashFsOptions { NoFragments = true, Deduplicate = false, Exportable = false, XzEffort = 0, XzFilters = { BcjFilter.X86, BcjFilter.Arm } },
            }));

            foreach (ExtKind kind in new[] { ExtKind.Ext2, ExtKind.Ext3, ExtKind.Ext4 })
                foreach (int bs in new[] { 1024, 2048, 4096 })
                    Test($"{kind} block={bs}", () => RoundTrip(tree, reference, $"{kind}_{bs}.img", new SaveOptions
                    {
                        Format = ImageFormat.Ext,
                        Ext = new ExtOptions { Kind = kind, BlockSize = bs, Timestamp = 1700000000 },
                    }));
            Test("ext4 fixed size, 128-byte inodes, no journal", () => RoundTrip(tree, reference, "ext4_fixed.img", new SaveOptions
            {
                Format = ImageFormat.Ext,
                Ext = new ExtOptions { SizeBytes = 48 << 20, InodeSize = 128, Journal = false, Label = "selftest", ReservedPercent = 5 },
            }, inode128: true));
            Test("ext4 too small is rejected", () =>
            {
                var o = new SaveOptions { Format = ImageFormat.Ext, Ext = new ExtOptions { SizeBytes = 2 << 20 }, Verify = VerifyLevel.None };
                using var ms = new FileStream(Path.Combine(_dir, "small.img"), FileMode.Create);
                ExpectFs(() => ImageDocument.WriteTo(tree, ms, o));
            });

            foreach (ArchiveCompression c in Enum.GetValues<ArchiveCompression>().Where(c => c is not (ArchiveCompression.Zip or ArchiveCompression.SevenZip)))
                foreach (TarDialect d in Enum.GetValues<TarDialect>())
                    Test($"tar {c} {d}", () => RoundTrip(tree, reference, $"t_{c}_{d}{ArchiveCodec.DefaultExtension(c)}", new SaveOptions
                    {
                        Format = ImageFormat.Tar,
                        Tar = new TarOptions { Compression = c, Dialect = d, XzEffort = 0, ZstdLevel = 3 },
                    }));
            Test("tar without prefix and root entry", () => RoundTrip(tree, reference, "t_noprefix.tar", new SaveOptions
            {
                Format = ImageFormat.Tar,
                Tar = new TarOptions { Compression = ArchiveCompression.None, PathPrefix = "", IncludeRootEntry = false },
            }, noRoot: true));

            Test("conversion chain squashfs -> ext4 -> tar.gz -> ext2 -> squashfs", () => Chain(tree, reference));
            Test("save over the opened file with backup and content check", () => SaveInPlace(tree, reference));
            Test("extract to a folder and pack it again", () => HostRoundTrip(tree, reference));
            Test("edited folder is packed with the edits", () => HostEdits(tree));
            Test("images wrapped in gzip, xz, zstd and bzip2 open transparently", () => CompressedImages(tree, reference));
            DiskTests(tree, reference);
            JournalTests();
            UbifsTests(tree, reference);
            Jffs2Tests();
            FatTests();
            ErofsTests(tree, reference);
            CpioTests(tree, reference);
            CramfsRomfsTests();
            F2fsTests(tree, reference);
            ArchiveTests(tree, reference);
            AndroidTests();
            Test("corrupted images are rejected cleanly", Fuzz);
        }
        finally
        {
            if (workDir is null)
            {
                try { Directory.Delete(_dir, recursive: true); }
                catch (IOException) { /* leave the leftovers */ }
            }
        }
        Console.WriteLine($"{_passed} passed, {_failed} failed in {sw.Elapsed.TotalSeconds:F1} s");
        return _failed == 0 ? 0 : 1;
    }

    private static void Test(string name, Action body)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            body();
            _passed++;
            Console.WriteLine($"  ok    {name} ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"  FAIL  {name}: {ex.GetType().Name}: {ex.Message}");
            if (ex is not TestFailure) Console.WriteLine(ex.StackTrace);
        }
    }

    private sealed class TestFailure(string message) : Exception(message);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new TestFailure(message);
    }

    private static void ExpectFs(Action a)
    {
        try
        {
            a();
        }
        catch (FsException)
        {
            return;
        }
        throw new TestFailure("an FsException was expected");
    }

    // ------------------------------------------------------------ test tree

    private static byte[] Bytes(Random rnd, int n, bool compressible = false)
    {
        byte[] b = new byte[n];
        if (!compressible) rnd.NextBytes(b);
        else for (int i = 0; i < n; i++) b[i] = (byte)("lorem ipsum dolor sit amet "[(i * 7 + i / 31) % 27]);
        return b;
    }

    internal static FsTree BuildTree(Random rnd)
    {
        var t = new FsTree(0x1ED, 1588655105);
        FsNode root = t.Root;
        FsNode bin = t.CreateDirectory(root, "bin", 0x1ED, 0, 0, 1700000001);
        FsNode etc = t.CreateDirectory(root, "etc", 0x5E8 /* 02750 */, 0, 0, 1700000002);
        FsNode dev = t.CreateDirectory(root, "dev", 0x1ED, 0, 0, 1700000003);
        FsNode var = t.CreateDirectory(root, "var", 0x3FF /* 01777 */, 0, 0, 1700000004);
        FsNode weird = t.CreateDirectory(root, "weird names", 0x1ED, 1000, 1000, 946684799);

        FsNode sh = t.CreateFile(bin, "busybox", new MemoryFileData(Bytes(rnd, 300000)), 0x9ED /* 04755 */, 0, 0, 1700000010);
        t.CreateSymlink(bin, "sh", "busybox", 0, 0, 1286705410);
        t.CreateHardLink(bin, "busybox-link", sh);
        t.CreateHardLink(etc, "busybox-third", sh);
        t.CreateSymlink(etc, "mtab", "/proc/mounts");
        t.CreateSymlink(etc, "link59", new string('y', 59));
        t.CreateSymlink(etc, "link60", new string('y', 60));
        t.CreateSymlink(etc, "longlink", string.Concat(Enumerable.Repeat("a/", 400)) + "target");
        t.CreateFile(etc, "passwd", new MemoryFileData(Encoding.ASCII.GetBytes("root:x:0:0:root:/root:/bin/sh\n")), 0x124, 0, 0, 981173106);
        t.CreateFile(etc, "empty", null, 0x180, 0, 0, 0);
        t.CreateFile(etc, "text", new MemoryFileData(Bytes(rnd, 70000, compressible: true)), 0x1A4, 0, 42, 1700000011);
        t.CreateFile(etc, "text-dup", new MemoryFileData(Bytes(rnd, 70000, compressible: true)), 0x1A4, 0, 42, 1700000012);

        t.CreateSpecial(dev, "null", FsNodeType.CharDevice, 1, 3, 0x1B6);
        t.CreateSpecial(dev, "sda", FsNodeType.BlockDevice, 8, 0, 0x1B0, 0, 6);
        t.CreateSpecial(dev, "ttyS300", FsNodeType.CharDevice, 4, 300, 0x190);
        t.CreateSpecial(dev, "bigminor", FsNodeType.BlockDevice, 259, 70000, 0x180);
        t.CreateSpecial(dev, "fifo", FsNodeType.Fifo, 0, 0, 0x1A4);
        t.CreateSpecial(dev, "sock", FsNodeType.Socket, 0, 0, 0x1ED);

        // Sizes around every interesting boundary.
        foreach (int size in new[] { 1, 511, 512, 1023, 1024, 1025, 4095, 4096, 4097, 131071, 131072, 131073, 1 << 20, (1 << 20) + 1 })
            t.CreateFile(var, "f" + size, new MemoryFileData(Bytes(rnd, size)), 0x1A4, (uint)(size % 3 == 0 ? 123456 : 0), (uint)(size % 5 == 0 ? 654321 : 0), 1700000100 + size);
        t.CreateFile(var, "zeros", new MemoryFileData(new byte[3 << 20]), 0x1A4, 0, 0, 1700000020);
        byte[] sparse = new byte[2 << 20];
        Bytes(rnd, 5000).CopyTo(sparse, 0);
        Bytes(rnd, 9000).CopyTo(sparse, 1 << 20);
        Bytes(rnd, 100).CopyTo(sparse, sparse.Length - 100);
        t.CreateFile(var, "sparse", new MemoryFileData(sparse), 0x1A4, 0, 0, 1700000021);
        byte[] frag = new byte[600 * 4096];
        for (int i = 0; i < 600; i += 2) Bytes(rnd, 4096).CopyTo(frag, i * 4096);
        t.CreateFile(var, "fragmented", new MemoryFileData(frag), 0x1A4, 0, 0, 1700000022);
        t.CreateFile(var, "future", new MemoryFileData(new byte[] { 1 }), 0x1A4, 0, 0, 2208988800);       // year 2040

        FsNode many = t.CreateDirectory(var, "many", 0x1ED, 33, 33, 1700000030);
        for (int i = 0; i < 1500; i++)
            t.CreateFile(many, new string('n', i % 40 + 1) + "-" + i, new MemoryFileData(Encoding.ASCII.GetBytes("file number " + i + "\n")), 0x1A4, 0, 0, 1700000040 + i);
        FsNode deep = var;
        foreach (string part in "a/b/c/d/e/f/g/h/i/j".Split('/')) deep = t.CreateDirectory(deep, part, 0x1C0, 0, 0, 1700000050);
        t.CreateFile(deep, "deep-file", new MemoryFileData(Encoding.ASCII.GetBytes("deep\n")));
        t.CreateDirectory(var, "empty-dir", 0, 0, 0, 1700000060);

        // Names that are hard for Windows, for JSON or for sorting.
        void Named(string name) => t.CreateFile(weird, name, new MemoryFileData(Encoding.UTF8.GetBytes(name.Length + "\n")), 0x1A4, 0, 0, 1700000070);
        Named("файл на русском.txt");
        Named("日本語");
        Named("emoji-😀.txt");
        Named("a:b*c?d<e>f|g\"h");
        Named("trailing dot.");
        Named("trailing space ");
        Named("CON");
        Named("nul.txt");
        Named("Makefile");
        Named("makefile");
        Named("MAKEFILE");
        Named("back\\slash");
        Named("tab\there");
        Named("new\nline");
        Named(new string('n', 255));
        Named(PosixName.Decode(new byte[] { (byte)'b', (byte)'a', (byte)'d', 0xFF, 0xFE, (byte)'.', (byte)'b' }));
        Named("%41 percent");
        Named(HostManifest.FileName);
        t.CreateSymlink(weird, "link to busybox", "../bin/busybox");
        FsNode sub = t.CreateDirectory(weird, "sub dir.", 0x1ED, 0, 0, 1700000080);
        t.CreateFile(sub, "aux", new MemoryFileData(Encoding.ASCII.GetBytes("x")));

        // Extended attributes in the three namespaces every format can hold.
        etc.Inode.XAttrs = new(StringComparer.Ordinal) { ["security.selinux"] = Encoding.ASCII.GetBytes("u:object_r:system_file:s0\0") };
        sh.Inode.XAttrs = new(StringComparer.Ordinal)
        {
            ["security.capability"] = new byte[] { 1, 0, 0, 2, 0, 0x20, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            ["user.comment"] = Encoding.ASCII.GetBytes("hello world"),
        };
        var.Inode.XAttrs = new(StringComparer.Ordinal) { ["trusted.overlay.opaque"] = new[] { (byte)'y' } };
        etc.Find("text")!.Inode.XAttrs = new(StringComparer.Ordinal) { ["user.bin"] = new byte[] { 0, 1, 2, 3, 255, 10, 61 }, ["user.empty"] = Array.Empty<byte>() };
        return t;
    }

    // ----------------------------------------------------------- comparison

    /// <summary>Removes from a dump what the given format cannot represent.</summary>
    private static string Normalize(string dump, SaveOptions o, bool noRoot = false, bool inode128 = false)
    {
        var sb = new StringBuilder(dump.Length);
        foreach (string line in dump.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] f = line.Split('\t');
            if (o.Format == ImageFormat.Tar && f[1] == "s") continue;
            if (o.Format == ImageFormat.Ext && f[0] == "/lost+found") continue;
            if (o.Format == ImageFormat.Tar && o.Tar.Dialect == TarDialect.Gnu) f[8] = "-";
            if (o.Format == ImageFormat.Cpio) f[8] = "-";                // cpio has no extended attributes
            if (o.Format is ImageFormat.Cramfs or ImageFormat.Romfs) NormalizeCramfsRomfs(f, o);
            if (o.Format == ImageFormat.F2fs && !o.F2fs.StoreXattrs) f[8] = "-";
            if (o.Format == ImageFormat.Jffs2 && !o.Jffs2.StoreXattrs) f[8] = "-";
            if (o.Format == ImageFormat.Fat)
            {
                // FAT: files and directories only, no owners or permissions, times in 2-second steps from 1980.
                if (f[1] is not ("f" or "d")) continue;
                f[2] = "*"; f[3] = f[4] = "0"; f[7] = f[8] = "-";
                long t = long.Parse(f[5]);
                f[5] = f[0] == "/" ? "*" : (Math.Clamp(t, 315532800, 4354819198) & ~1L).ToString();
            }
            if (noRoot && f[0] == "/") f[5] = "*";
            if (inode128 && long.Parse(f[5]) > int.MaxValue) f[5] = int.MaxValue.ToString();   // 32-bit timestamps are clamped
            sb.Append(string.Join('\t', f)).Append('\n');
        }
        return sb.ToString();
    }

    private static void Same(string expected, string actual, string what)
    {
        if (expected == actual) return;
        string[] a = expected.Split('\n'), b = actual.Split('\n');
        var only = new HashSet<string>(a);
        only.SymmetricExceptWith(b);
        string sample = string.Join(" | ", only.Take(3).Select(l => l.Length > 160 ? l[..160] + "..." : l));
        throw new TestFailure($"{what}: {only.Count} differing lines, e.g. {sample}");
    }

    private static void RoundTrip(FsTree tree, string reference, string file, SaveOptions o, bool noRoot = false, bool inode128 = false)
    {
        string path = Path.Combine(_dir, file);
        using (var fs = new FileStream(path, FileMode.Create))
            ImageDocument.WriteTo(tree, fs, o);
        using ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
        Check(doc.Format == o.Format, "the written file was detected as " + doc.Format);
        Same(Normalize(reference, o, noRoot, inode128), Normalize(Program.DumpToString(doc.Tree), o, noRoot, inode128), file);
        List<string> diff = TreeComparer.Compare(tree, doc.Tree, TreeComparer.RulesFor(o), content: true);
        Check(diff.Count == 0, "TreeComparer: " + string.Join("; ", diff.Take(3)));
        File.Delete(path);
    }

    private static void Chain(FsTree tree, string reference)
    {
        string p0 = Path.Combine(_dir, "chain0.sqfs");
        using (var fs = new FileStream(p0, FileMode.Create))
            ImageDocument.WriteTo(tree, fs, new SaveOptions { Format = ImageFormat.SquashFs, SquashFs = new SquashFsOptions { Compression = SquashCompression.Zstd, ZstdLevel = 3 } });

        ImageDocument doc = ImageDocument.Open(p0, _dir);
        try
        {
            var steps = new (string File, SaveOptions Options)[]
            {
                ("chain1.ext4", new SaveOptions { Format = ImageFormat.Ext, Ext = new ExtOptions { Kind = ExtKind.Ext4 }, Verify = VerifyLevel.Content }),
                ("chain2.tar.gz", new SaveOptions { Format = ImageFormat.Tar, Verify = VerifyLevel.Structure }),
                ("chain3.ext2", new SaveOptions { Format = ImageFormat.Ext, Ext = new ExtOptions { Kind = ExtKind.Ext2, BlockSize = 1024 }, Verify = VerifyLevel.Content }),
                ("chain4.sqfs", new SaveOptions { Format = ImageFormat.SquashFs, SquashFs = new SquashFsOptions { Compression = SquashCompression.Gzip }, Verify = VerifyLevel.Content }),
            };
            foreach ((string file, SaveOptions o) in steps)
                doc = doc.Save(Path.Combine(_dir, file), o).Document;

            // After a trip through tar the socket is gone and ext added lost+found; everything else must survive.
            var tar = new SaveOptions { Format = ImageFormat.Tar };
            var ext = new SaveOptions { Format = ImageFormat.Ext };
            Same(Normalize(Normalize(reference, tar), ext), Normalize(Normalize(Program.DumpToString(doc.Tree), tar), ext), "chain");
        }
        finally
        {
            doc.Dispose();
        }
    }

    private static void SaveInPlace(FsTree tree, string reference)
    {
        string path = Path.Combine(_dir, "inplace.sqfs");
        var o = new SaveOptions { Format = ImageFormat.SquashFs, SquashFs = new SquashFsOptions { Compression = SquashCompression.Lz4 }, Verify = VerifyLevel.Content, KeepBackup = true };
        using (var fs = new FileStream(path, FileMode.Create))
            ImageDocument.WriteTo(tree, fs, o);

        ImageDocument doc = ImageDocument.Open(path, _dir);
        try
        {
            Check(!doc.IsModified, "a freshly opened document is marked as modified");
            FsNode etc = doc.Tree.Resolve("/etc")!;
            doc.Tree.CreateFile(etc, "added.conf", new MemoryFileData(Encoding.ASCII.GetBytes("option 1\n")), 0x1A4, 0, 0, 1700000500);
            doc.Tree.Remove(doc.Tree.Resolve("/var/many")!);
            doc.Tree.Rename(doc.Tree.Resolve("/etc/passwd")!, "passwd.old");
            doc.Tree.SetMode(new[] { doc.Tree.Resolve("/bin")! }, 0x1C0, recursive: true, targets: FsTree.Targets.Files);
            Check(doc.IsModified, "edits did not mark the document as modified");
            string expected = Program.DumpToString(doc.Tree);

            doc = doc.Save(path, doc.CreateSaveOptions(null, o)).Document;
            Check(!doc.IsModified, "the saved document is still marked as modified");
            Same(expected, Program.DumpToString(doc.Tree), "in-place save");
            Check(File.Exists(path + ".bak"), "the backup file is missing");
            Check(!File.Exists(path + ".fsforge-tmp"), "the temporary file was left behind");
            Check(doc.Tree.Resolve("/etc/added.conf") is not null && doc.Tree.Resolve("/var/many") is null, "the edits are not in the saved image");
            Check(doc.Tree.Resolve("/bin/busybox")!.Inode.Mode == 0x1C0, "the recursive chmod was lost");

            using ImageDocument backup = ImageDocument.Open(path + ".bak", _dir);
            Same(reference, Program.DumpToString(backup.Tree), "backup");
        }
        finally
        {
            doc.Dispose();
        }
    }

    private static void CompressedImages(FsTree tree, string reference)
    {
        var ext = new SaveOptions { Format = ImageFormat.Ext, Ext = new ExtOptions { BlockSize = 1024 } };
        var ms = new MemoryStream();
        ImageDocument.WriteTo(tree, ms, ext);
        byte[] image = ms.ToArray();
        int before = Directory.GetFiles(_dir, "fsforge-*.img").Length;

        foreach (ArchiveCompression c in new[] { ArchiveCompression.Gzip, ArchiveCompression.Xz, ArchiveCompression.Zstd, ArchiveCompression.Bzip2 })
        {
            string path = Path.Combine(_dir, "wrapped.img" + ArchiveCodec.DefaultExtension(c)[4..]);
            using (var fs = new FileStream(path, FileMode.Create))
            using (Stream z = ArchiveCodec.OpenCompressor(fs, new TarOptions { Compression = c, XzEffort = 0, ZstdLevel = 3 }))
                z.Write(image);
            Check(ImageDocument.Detect(path) == ImageFormat.Ext, "a compressed image must be recognised as an image, not as a tar archive");
            ImageDocument doc = ImageDocument.Open(path, _dir);
            try
            {
                Check(doc.Format == ImageFormat.Ext && doc.OuterCompression == c && !doc.SavesFilesystemOnly && doc.Container is null, $"{c}: wrong detection");
                Same(Normalize(reference, ext), Normalize(Program.DumpToString(doc.Tree), ext), "wrapped " + c);

                // Saved under its own name the image stays compressed the same way.
                doc.Tree.CreateFile(doc.Tree.Resolve("/etc")!, "wrapped.conf", new MemoryFileData(Encoding.ASCII.GetBytes("1\n")), 0x1A4, 0, 0, 1700000600);
                string expected = Program.DumpToString(doc.Tree);
                SaveOptions o = doc.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir });
                o.Tar.XzEffort = 0;
                doc = doc.Save(path, o).Document;
                Check(doc.Format == ImageFormat.Ext && doc.OuterCompression == c, $"{c}: the image saved in place is not compressed any more");
                // Nothing may keep the saved file open (on Windows it could not be replaced the next time).
                try { using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException ex) { throw new TestFailure($"{c}: the saved file is still open: {ex.Message}"); }
                Same(expected, Program.DumpToString(doc.Tree), "wrapped " + c + " after saving in place");
            }
            finally
            {
                doc.Dispose();
            }
            File.Delete(path);
        }
        Check(Directory.GetFiles(_dir, "fsforge-*.img").Length == before, "temporary unpacked images were left behind");

        // An archive without members is still an archive.
        string empty = Path.Combine(_dir, "empty.tar.gz");
        using (var t = new FsTree())
        using (var fs = new FileStream(empty, FileMode.Create))
            ImageDocument.WriteTo(t, fs, new SaveOptions { Format = ImageFormat.Tar, Tar = new TarOptions { IncludeRootEntry = false } });
        using ImageDocument e = ImageDocument.Open(empty, _dir);
        Check(e.Format == ImageFormat.Tar && e.Tree.Root.Children.Count == 0, "empty tar.gz");
    }

    // ------------------------------------------------------------ host I/O

    private static void HostRoundTrip(FsTree tree, string reference)
    {
        string folder = Path.Combine(_dir, "extracted");
        ExtractResult er = HostExtractor.Extract(new[] { tree.Root }, folder, new ExtractOptions());
        Check(er.Skipped == 0, "skipped during extraction: " + string.Join("; ", er.Warnings.Take(3)));
        Check(er.Renamed > 0, "no name needed to be made Windows-safe, the test tree is wrong");
        foreach (string entry in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories))
            Check(HostNames.IsSafe(Path.GetFileName(entry)), "unsafe host name: " + entry);

        using var back = new FsTree();
        ImportResult ir = HostImporter.ImportContents(back, back.Root, folder, new ImportOptions());
        Check(ir.Skipped == 0, "skipped during import: " + string.Join("; ", ir.Warnings.Take(3)));
        Same(reference, Program.DumpToString(back), "folder round trip");

        // A subfolder dragged on its own still finds the manifest in the parent folder.
        using var part = new FsTree();
        HostImporter.Import(part, part.Root, new[] { Path.Combine(folder, "etc") }, new ImportOptions());
        FsNode etc = part.Resolve("/etc") ?? throw new TestFailure("the subfolder was not imported");
        Check(etc.Inode.Mode == 0x5E8 && etc.Find("mtab")?.Inode.LinkTarget == "/proc/mounts", "subfolder metadata was not restored");
    }

    private static void HostEdits(FsTree tree)
    {
        string folder = Path.Combine(_dir, "edited");
        HostExtractor.Extract(new[] { tree.Root }, folder, new ExtractOptions());

        File.WriteAllText(Path.Combine(folder, "etc", "passwd"), "root:x:0:0:changed\n");
        File.Delete(Path.Combine(folder, "etc", "text"));
        File.WriteAllText(Path.Combine(folder, "etc", "mtab"), "/proc/self/mounts\n");               // edited link placeholder
        File.WriteAllBytes(Path.Combine(folder, "bin", "newtool"), new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 1, 2, 3 });
        File.WriteAllText(Path.Combine(folder, "etc", "new.conf"), "x=1\n");
        Directory.CreateDirectory(Path.Combine(folder, "opt", "app"));
        File.WriteAllText(Path.Combine(folder, "opt", "app", "run.sh"), "#!/bin/sh\necho hi\n");
        File.AppendAllText(Path.Combine(folder, "bin", "busybox-link"), "x");                            // breaks one hard link

        using var t = new FsTree();
        ImportResult ir = HostImporter.ImportContents(t, t.Root, folder, new ImportOptions());
        Check(ir.Skipped == 0, "skipped during import: " + string.Join("; ", ir.Warnings.Take(3)));

        FsInode passwd = t.Resolve("/etc/passwd")!.Inode;
        Check(passwd.Size == 19 && passwd.Mode == 0x124, "the edited file lost its mode or has the wrong content");
        Check(passwd.MTime != 981173106, "the edited file kept its old timestamp");
        Check(t.Resolve("/etc/text") is null, "the deleted file came back");
        Check(t.Resolve("/etc/mtab")!.Inode is { Type: FsNodeType.Symlink, LinkTarget: "/proc/self/mounts" }, "the edited link target was not picked up");
        Check(t.Resolve("/etc/link60")!.Inode.LinkTarget == new string('y', 60), "an untouched link changed");
        Check(t.Resolve("/bin/newtool")!.Inode.Mode == 0x1ED, "the new ELF file is not executable");
        Check(t.Resolve("/opt/app/run.sh")!.Inode.Mode == 0x1ED, "the new script is not executable");
        Check(t.Resolve("/etc/new.conf")!.Inode.Mode == 0x1A4, "the new plain file has the wrong mode");
        Check(t.Resolve("/dev/null")!.Inode is { Type: FsNodeType.CharDevice, DevMajor: 1, DevMinor: 3 }, "the device node was not restored");
        FsNode a = t.Resolve("/bin/busybox")!, b = t.Resolve("/bin/busybox-link")!, c = t.Resolve("/etc/busybox-third")!;
        Check(ReferenceEquals(a.Inode, c.Inode), "the untouched hard link was not restored");
        Check(!ReferenceEquals(a.Inode, b.Inode) && b.Inode.Size == a.Inode.Size + 1, "the modified hard link is still linked");
        Check(t.Resolve("/weird names/a:b*c?d<e>f|g\"h") is not null, "a Windows-unsafe name was not restored");
    }

    // ---------------------------------------------------------------- units

    private static void Codecs()
    {
        var rnd = new Random(7);
        var inputs = new List<byte[]>
        {
            Array.Empty<byte>(), new byte[] { 42 }, Bytes(rnd, 100), Bytes(rnd, 5000, true), Bytes(rnd, 131072), Bytes(rnd, 131072, true), new byte[200000],
        };
        byte[] mixed = Bytes(rnd, 300000, true);
        Bytes(rnd, 50000).CopyTo(mixed, 100000);
        inputs.Add(mixed);

        var codecs = new (string Name, IBlockCodec Codec)[]
        {
            ("zlib", new ZlibCodec(6)), ("lz4", new Lz4Codec()), ("zstd", new ZstdCodec(3)),
            ("xz fast", new XzCodec(1 << 20, null, LzmaEncoderSettings.FromEffort(0))),
            ("xz max + bcj", new XzCodec(1 << 20, new[] { BcjFilter.X86, BcjFilter.Arm, BcjFilter.Arm64 }, LzmaEncoderSettings.FromEffort(2))),
        };
        foreach ((string name, IBlockCodec codec) in codecs)
        {
            foreach (byte[] input in inputs)
            {
                if (input.Length == 0) continue;
                byte[] dst = new byte[input.Length + 1024];
                int n = codec.Compress(input, dst);
                if (n < 0) continue;                    // "does not fit" is a valid answer for random data
                byte[] back = new byte[input.Length];
                int m = codec.Decompress(dst.AsSpan(0, n), back);
                Check(m == input.Length && back.AsSpan().SequenceEqual(input), $"{name}: round trip of {input.Length} bytes failed");
            }
            (codec as IDisposable)?.Dispose();
        }
    }

    private static void XzStreams()
    {
        var rnd = new Random(9);
        foreach (int size in new[] { 0, 1, 70000, (1 << 16) * 5 + 17, 3_000_000 })
        {
            byte[] data = Bytes(rnd, size, compressible: size != 70000);
            var ms = new MemoryStream();
            using (var xz = new XzOutputStream(ms, effort: 0, chunkSize: 1 << 16, threads: 3, leaveOpen: true))
            {
                // Odd write sizes exercise the chunking.
                for (int off = 0; off < data.Length;)
                {
                    int n = Math.Min(data.Length - off, 1 + rnd.Next(100000));
                    xz.Write(data, off, n);
                    off += n;
                }
            }
            ms.Position = 0;
            using var input = new XzInputStream(ms);
            var back = new MemoryStream();
            input.CopyTo(back);
            Check(back.ToArray().AsSpan().SequenceEqual(data), $"xz stream round trip of {size} bytes failed");
        }
    }

    private static void HostNameRules()
    {
        (string In, string Out)[] cases =
        {
            ("plain.txt", "plain.txt"), ("a:b", "a%3Ab"), ("CON", "_CON"), ("con.txt", "_con.txt"), ("COM1", "_COM1"), ("COM10", "COM10"),
            ("dot.", "dot%2E"), ("space ", "space%20"), ("a\\b", "a%5Cb"), ("q?", "q%3F"), ("tab\t", "tab%09"), ("console", "console"),
        };
        foreach ((string input, string expected) in cases)
            Check(HostNames.MakeSafe(input) == expected, $"MakeSafe(\"{input}\") gave \"{HostNames.MakeSafe(input)}\", expected \"{expected}\"");

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Check(HostNames.MakeUnique("Makefile", used) == "Makefile", "first name changed");
        Check(HostNames.MakeUnique("makefile", used) == "makefile~2", "case collision not resolved");
        Check(HostNames.MakeUnique("MAKEFILE", used) == "MAKEFILE~3", "second case collision not resolved");
        Check(HostNames.MakeUnique("a.txt", used) == "a.txt" && HostNames.MakeUnique("A.TXT", used) == "A~2.TXT", "extension handling");

        // Names survive the trip through bytes even when they are not valid UTF-8.
        byte[] raw = { 0x66, 0xFF, 0xC3, 0x28, 0xE2, 0x82, 0xAC, 0x80 };
        Check(PosixName.Encode(PosixName.Decode(raw)).AsSpan().SequenceEqual(raw), "PosixName is not lossless");
    }

    private static void TreeEditing()
    {
        using var t = new FsTree();
        FsNode a = t.CreateDirectory(t.Root, "a");
        FsNode b = t.CreateDirectory(a, "b");
        FsNode f = t.CreateFile(b, "f", new MemoryFileData(new byte[10]));
        FsNode l = t.CreateHardLink(a, "l", f);
        int events = 0;
        t.Changed += () => events++;

        Check(f.Inode.LinkCount == 2 && ReferenceEquals(f.Inode, l.Inode), "hard link count");
        ExpectFs(() => t.CreateFile(b, "f", null));
        ExpectFs(() => t.Move(a, b));
        ExpectFs(() => t.Rename(f, "x/y"));
        ExpectFs(() => t.Rename(f, ".."));
        ExpectFs(() => t.Remove(t.Root));
        ExpectFs(() => t.CreateHardLink(t.Root, "dirlink", a));

        FsNode copy = t.Copy(a, t.Root, "a2");
        Check(t.Resolve("/a2/b/f") is not null && !ReferenceEquals(t.Resolve("/a2/b/f")!.Inode, f.Inode), "copy must not share inodes with the source");
        Check(ReferenceEquals(t.Resolve("/a2/b/f")!.Inode, t.Resolve("/a2/l")!.Inode), "copy must keep internal hard links");
        Check(copy.Inode.LinkCount == 1, "directory link count");

        t.Move(b, t.Root, "moved");
        Check(t.Resolve("/moved/f") is not null && t.Resolve("/a/b") is null, "move");
        t.Remove(t.Resolve("/moved")!);
        Check(l.Inode.LinkCount == 1, "removing a subtree must release its hard links");

        t.BreakHardLink(t.Resolve("/a2/l")!);
        Check(!ReferenceEquals(t.Resolve("/a2/b/f")!.Inode, t.Resolve("/a2/l")!.Inode), "BreakHardLink");

        Check(t.ChangeMode(new[] { t.Root }, 0x49, 0, recursive: true, targets: FsTree.Targets.Files) == 3, "recursive chmod +x on files");
        Check(t.ChangeOwner(new[] { t.Root }, 1000, null, recursive: true) == 7, "recursive chown");
        t.SetXAttr(l, "user.test", new byte[] { 1 });
        t.SetXAttr(l, "user.test", null);
        Check(l.Inode.XAttrs is null, "removing the last attribute");
        ExpectFs(() => t.SetXAttr(l, "noprefix", new byte[] { 1 }));
        Check(FsTree.MakeUniqueName(t.Root, "a2") == "a2 (2)" && FsTree.MakeUniqueName(t.Root, "new") == "new", "MakeUniqueName");

        int before = events;
        using (t.SuspendNotifications())
        {
            t.CreateDirectory(t.Root, "x1");
            t.CreateDirectory(t.Root, "x2");
            Check(events == before, "notifications were not suspended");
        }
        Check(events == before + 1, "a suspended batch must raise exactly one notification");
        Check(UnixMode.Format(FsNodeType.File, 0x9ED) == "-rwsr-xr-x" && UnixMode.Format(FsNodeType.Directory, 0x3FF) == "drwxrwxrwt", "mode formatting");
        Check(UnixMode.TryParseOctal("4755", out int m) && m == 0x9ED && !UnixMode.TryParseOctal("889", out _), "octal parsing");
    }

    // ----------------------------------------------------------------- fuzz

    /// <summary>
    /// Damages valid images at random places. Opening and reading them may fail, but only with
    /// FsException: a corrupt download must never crash or hang the application.
    /// </summary>
    private static void Fuzz()
    {
        using var small = new FsTree(0x1ED, 1700000000);
        var rnd = new Random(99);
        FsNode d = small.CreateDirectory(small.Root, "dir");
        for (int i = 0; i < 40; i++)
            small.CreateFile(i % 3 == 0 ? small.Root : d, "file" + i, new MemoryFileData(Bytes(rnd, 100 + i * 700, i % 2 == 0)), 0x1A4, (uint)i, 0, 1700000000 + i);
        small.CreateSymlink(d, "link", "../file0");
        small.CreateSpecial(d, "null", FsNodeType.CharDevice, 1, 3);
        d.Inode.XAttrs = new(StringComparer.Ordinal) { ["user.x"] = new byte[] { 1, 2, 3 } };

        var variants = new (string Name, SaveOptions Options)[]
        {
            ("squashfs gzip", new SaveOptions { Format = ImageFormat.SquashFs, SquashFs = new SquashFsOptions { Compression = SquashCompression.Gzip, BlockSize = 4096 } }),
            ("squashfs xz", new SaveOptions { Format = ImageFormat.SquashFs, SquashFs = new SquashFsOptions { Compression = SquashCompression.Xz, XzEffort = 0 } }),
            ("squashfs lz4", new SaveOptions { Format = ImageFormat.SquashFs, SquashFs = new SquashFsOptions { Compression = SquashCompression.Lz4 } }),
            ("squashfs zstd", new SaveOptions { Format = ImageFormat.SquashFs, SquashFs = new SquashFsOptions { Compression = SquashCompression.Zstd, ZstdLevel = 3 } }),
            ("ext4", new SaveOptions { Format = ImageFormat.Ext, Ext = new ExtOptions { BlockSize = 1024 } }),
            ("ext2", new SaveOptions { Format = ImageFormat.Ext, Ext = new ExtOptions { Kind = ExtKind.Ext2, BlockSize = 1024, InodeSize = 128 } }),
            ("tar", new SaveOptions { Format = ImageFormat.Tar, Tar = new TarOptions { Compression = ArchiveCompression.None } }),
            ("tar.gz", new SaveOptions { Format = ImageFormat.Tar }),
            ("tar.xz", new SaveOptions { Format = ImageFormat.Tar, Tar = new TarOptions { Compression = ArchiveCompression.Xz, XzEffort = 0 } }),
            ("jffs2", new SaveOptions { Format = ImageFormat.Jffs2, Jffs2 = new Jffs2Options { EraseBlockSize = 8192, StoreXattrs = true } }),
            ("erofs lz4hc", new SaveOptions { Format = ImageFormat.Erofs, Erofs = new ErofsOptions { Lz4Level = 3 } }),
            ("erofs lzma 16k", new SaveOptions { Format = ImageFormat.Erofs, Erofs = new ErofsOptions { Compression = ErofsCompression.Lzma, LzmaEffort = 0, PclusterSize = 16384 } }),
            ("erofs uncompressed", new SaveOptions { Format = ImageFormat.Erofs, Erofs = new ErofsOptions { Compression = ErofsCompression.None } }),
            ("erofs zstd", new SaveOptions { Format = ImageFormat.Erofs, Erofs = new ErofsOptions { Compression = ErofsCompression.Zstd, PclusterSize = 16384 } }),
            ("erofs deflate", new SaveOptions { Format = ImageFormat.Erofs, Erofs = new ErofsOptions { Compression = ErofsCompression.Deflate } }),
            ("cpio newc", new SaveOptions { Format = ImageFormat.Cpio, Cpio = new FsForge.Core.Cpio.CpioOptions { Compression = ArchiveCompression.None } }),
            ("cpio crc gzip", new SaveOptions { Format = ImageFormat.Cpio, Cpio = new FsForge.Core.Cpio.CpioOptions { Header = FsForge.Core.Cpio.CpioHeader.Crc } }),
            ("cramfs", new SaveOptions { Format = ImageFormat.Cramfs }),
            ("cramfs big-endian holes 16k", new SaveOptions { Format = ImageFormat.Cramfs, Cramfs = new FsForge.Core.Cramfs.CramfsOptions { BigEndian = true, Holes = true, BlockSize = 16384, Pad = true } }),
            ("romfs", new SaveOptions { Format = ImageFormat.Romfs }),
            ("f2fs", new SaveOptions { Format = ImageFormat.F2fs }),
            ("tar.bz2", new SaveOptions { Format = ImageFormat.Tar, Tar = new TarOptions { Compression = ArchiveCompression.Bzip2 } }),
            ("squashfs in bzip2", new SaveOptions { Format = ImageFormat.SquashFs, SquashFs = new SquashFsOptions { Compression = SquashCompression.Lz4 } }),
            ("squashfs in 7z", new SaveOptions { Format = ImageFormat.SquashFs, SquashFs = new SquashFsOptions { Compression = SquashCompression.Lz4 } }),
            ("squashfs in zip", new SaveOptions { Format = ImageFormat.SquashFs, SquashFs = new SquashFsOptions { Compression = SquashCompression.Lz4 } }),
            ("jffs2 big-endian lzo", new SaveOptions { Format = ImageFormat.Jffs2, Jffs2 = new Jffs2Options { BigEndian = true, Compression = Jffs2Compression.Lzo, Cleanmarkers = false } }),
        };

        string path = Path.Combine(_dir, "fuzz.bin");
        var failures = new List<string>();
        foreach ((string name, SaveOptions o) in variants)
        {
            var ms = new MemoryStream();
            ImageDocument.WriteTo(small, ms, o);
            byte[] good = ms.ToArray();
            if (name.EndsWith(" in bzip2")) good = Bz2(good);
            else if (name.EndsWith(" in 7z")) good = Pack7z(good);
            else if (name.EndsWith(" in zip")) good = PackZip(good);
            // Metadata lives at the start (and for SquashFS at the end): aim most of the damage there.
            int metaEnd = o.Format == ImageFormat.Ext ? Math.Min(good.Length, 200 * 1024) : good.Length;
            int crashes = 0;
            int iterations = int.TryParse(Environment.GetEnvironmentVariable("FSFORGE_FUZZ"), out int custom) ? custom : 120;
            for (int iter = 0; iter < iterations; iter++)
            {
                byte[] bad = (byte[])good.Clone();
                int kind = iter % 4;
                if (kind == 3)
                {
                    bad = bad.AsSpan(0, rnd.Next(1, bad.Length)).ToArray();       // truncation
                }
                else
                {
                    int hits = 1 + rnd.Next(kind == 0 ? 1 : 12);
                    for (int h = 0; h < hits; h++)
                    {
                        int at = o.Format == ImageFormat.SquashFs && rnd.Next(2) == 0
                            ? bad.Length - 1 - rnd.Next(Math.Min(bad.Length, 4096))
                            : rnd.Next(metaEnd);
                        bad[at] = kind == 1 ? (byte)rnd.Next(256) : (byte)(bad[at] ^ (1 << rnd.Next(8)));
                    }
                }
                File.WriteAllBytes(path, bad);
                try
                {
                    using ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
                    byte[] sink = new byte[1 << 16];
                    foreach (FsNode n in doc.Tree.Walk())
                    {
                        if (n.Inode.Data is null) continue;
                        using Stream s = n.Inode.Data.OpenRead();
                        long left = Math.Min(n.Inode.Size, 8 << 20);
                        while (left > 0)
                        {
                            int got = s.Read(sink, 0, (int)Math.Min(sink.Length, left));
                            if (got <= 0) break;
                            left -= got;
                        }
                    }
                }
                catch (FsException ex) when (ex is not FsUnexpectedException)
                {
                    // The expected, clean outcome.
                }
                catch (Exception ex)
                {
                    crashes++;
                    if (crashes <= 2)
                    {
                        File.Copy(path, Path.Combine(_dir, $"crash-{name.Replace(' ', '-')}-{iter}.bin"), true);
                        Console.WriteLine($"        {name} #{iter}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace?.Split('\n').Take(4).Aggregate((x, y) => x + "\n" + y)}");
                    }
                }
            }
            if (crashes > 0) failures.Add($"{name}: {crashes}");
        }
        File.Delete(path);
        Check(failures.Count == 0, "damaged images caused unexpected exceptions - " + string.Join(", ", failures));
    }
}
