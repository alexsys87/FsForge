using System.Text;
using FsForge.Core;
using FsForge.Core.Images;
using FsForge.Core.SquashFs;
using FsForge.Core.Ubifs;

namespace FsForge.Cli;

/// <summary>
/// UBIFS and UBI are read-only formats: the tests make their own images with
/// <see cref="UbifsImageBuilder"/>, read them back, replay a journal and convert the content.
/// </summary>
internal static partial class SelfTest
{
    private static void UbifsTests(FsTree tree, string reference)
    {
        foreach (UbifsCompression c in new[] { UbifsCompression.None, UbifsCompression.Zlib, UbifsCompression.Zstd })
            Test($"ubifs {c}: read", () => UbifsRead(tree, reference, c, c == UbifsCompression.Zlib ? 3 : 8));
        Test("ubifs: saved as SquashFS, ext4 and tar; writing UBIFS is refused", () => UbifsConvert(tree, reference));
        Test("ubi: volumes, choice of a volume, scrambled blocks, image inside a firmware file", () => UbiVolumes(tree, reference));
        Test("ubi: a SquashFS volume is opened and saved as the filesystem alone", () => UbiSquashFs(tree, reference));
        Test("ubifs: an unreplayed journal is replayed while reading", UbifsJournal);
        Test("ubifs: stale and foreign logs are not followed", UbifsLogEdges);
        Test("ubifs: a damaged node costs one file, not the image", UbifsDamagedNode);
        Test("ubifs and ubi: corrupted images are rejected cleanly", UbifsFuzz);
    }

    private static string WriteFile(string name, byte[] data)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static void UbifsRead(FsTree tree, string reference, UbifsCompression compression, int fanout)
    {
        string path = WriteFile($"ubifs_{compression}.ubifs", UbifsImageBuilder.FromTree(tree, compression, fanout).ToArray());
        Check(ImageDocument.Detect(path) == ImageFormat.Ubifs, "the image was not detected as UBIFS");
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
        {
            Check(doc.Format == ImageFormat.Ubifs && doc.FormatName == "UBIFS" && doc.Ubi is null, "format: " + doc.FormatName);
            UbifsInfo u = doc.Ubifs!;
            Check(u.LebSize == UbifsImageBuilder.LebSize && u.MinIoSize == UbifsImageBuilder.MinIo && u.DefaultCompression == compression
                  && u.Fanout == fanout && u.FormatVersion == 4 && u.ReplayedNodes == 0 && !u.Dirty && u.CommitNumber == 7,
                $"info: leb={u.LebSize} minio={u.MinIoSize} comp={u.DefaultCompression} fanout={u.Fanout} commit={u.CommitNumber}");
            Check(u.Uuid == Guid.Parse("a0a1a2a3-a4a5-a6a7-a8a9-aaabacadaeaf"), "uuid: " + u.Uuid);
            Check(doc.SavesFilesystemOnly && !ImageDocument.CanWrite(doc.Format), "a UBIFS document must not be saved in place");
            Check(doc.Warnings.Count == 1 && doc.Warnings[0].Contains("reading only"), "warnings: " + string.Join("; ", doc.Warnings));
            Same(reference, Program.DumpToString(doc.Tree), "ubifs " + compression);
            var rules = new TreeComparer.Rules();
            List<string> diff = TreeComparer.Compare(tree, doc.Tree, rules, content: true);
            Check(diff.Count == 0, "TreeComparer: " + string.Join("; ", diff.Take(3)));
        }
        File.Delete(path);
    }

    private static void UbifsConvert(FsTree tree, string reference)
    {
        string path = WriteFile("convert.ubifs", UbifsImageBuilder.FromTree(tree).ToArray());
        foreach ((ImageFormat format, string ext) in new[] { (ImageFormat.SquashFs, ".sqfs"), (ImageFormat.Ext, ".ext4"), (ImageFormat.Tar, ".tar.gz") })
        {
            ImageDocument doc = ImageDocument.Open(path, _dir);
            try
            {
                // Without a choice the content of a read-only format goes to SquashFS.
                SaveOptions o = doc.CreateSaveOptions();
                Check(o.Format == ImageFormat.SquashFs, "default target format: " + o.Format);
                o = doc.CreateSaveOptions(format);
                o.Verify = VerifyLevel.Content;
                o.SquashFs.XzEffort = 0;
                string target = Path.Combine(_dir, "from-ubifs" + ext);
                doc = doc.Save(target, o).Document;
                Check(doc.Format == format && doc.FilePath == target && !doc.SavesFilesystemOnly, "saved as " + doc.FormatName);
                Same(Normalize(reference, o), Normalize(Program.DumpToString(doc.Tree), o), "ubifs -> " + ext);
            }
            finally
            {
                doc.Dispose();
            }
        }

        using ImageDocument source = ImageDocument.Open(path, _dir);
        var refused = new SaveOptions { Format = ImageFormat.Ubifs };
        ExpectFs(() => source.Save(Path.Combine(_dir, "refused.ubifs"), refused));
        ExpectFs(() => ImageDocument.WriteTo(source.Tree, new MemoryStream(), refused));
        Check(!File.Exists(Path.Combine(_dir, "refused.ubifs")) && !source.IsDisposed, "a refused save had side effects");
        using ImageDocument fresh = ImageDocument.CreateNew(ImageFormat.Ubifs);
        Check(fresh.Format == ImageFormat.SquashFs, "a new image cannot be UBIFS");
    }

    private static FsTree SmallTree(string marker)
    {
        var t = new FsTree(0x1ED, 1700000000);
        FsNode upper = t.CreateDirectory(t.Root, "upper", mtime: 1700000000);
        t.CreateFile(upper, "marker", new MemoryFileData(Encoding.ASCII.GetBytes(marker)), mtime: 1700000000);
        return t;
    }

    private static void UbiVolumes(FsTree tree, string reference)
    {
        using FsTree small = SmallTree("rootfs_data\n");
        string smallReference = Program.DumpToString(small);
        byte[] root = UbifsImageBuilder.FromTree(tree, UbifsCompression.Zstd).ToArray();
        byte[] data = UbifsImageBuilder.FromTree(small).ToArray();
        byte[] kernel = Bytes(new Random(5), 300000);
        var volumes = new[]
        {
            new UbifsImageBuilder.Volume("kernel", kernel, Static: true),
            new UbifsImageBuilder.Volume("rootfs", root),
            new UbifsImageBuilder.Volume("rootfs_data", data, Id: 5),
        };

        foreach (bool shuffled in new[] { false, true })
        {
            string path = WriteFile(shuffled ? "shuffled.ubi" : "plain.ubi", UbifsImageBuilder.WrapUbi(volumes, shuffle: shuffled ? new Random(3) : null));
            Check(ImageDocument.Detect(path) == ImageFormat.Ubifs, "the UBI image was not detected");
            using (ImageDocument doc = ImageDocument.Open(path, _dir))
            {
                UbiContainer ubi = doc.Ubi ?? throw new TestFailure("no UBI container");
                Check(doc.Format == ImageFormat.Ubifs && ubi.Volume is { Name: "rootfs", Id: 1, IsStatic: false, Content: PartitionContent.Ubifs }, "default volume: " + ubi.Volume);
                Check(ubi.Image.PebSize == 131072 && ubi.Image.LebSize == UbifsImageBuilder.LebSize && ubi.Image.Volumes.Count == 3, $"geometry: {ubi.Image.PebSize}/{ubi.Image.LebSize}, {ubi.Image.Volumes.Count} volumes");
                Check(ubi.Image.Volumes[0] is { Name: "kernel", Id: 0, IsStatic: true, IsReadable: false } k && k.Length == kernel.Length, "kernel volume: " + ubi.Image.Volumes[0]);
                Check(ubi.Image.Volumes[2] is { Name: "rootfs_data", Id: 5, IsReadable: true }, "data volume: " + ubi.Image.Volumes[2]);
                Check(doc.SavesFilesystemOnly && doc.EmbeddedOffset == 0, "a UBI volume must be saved as a filesystem alone");
                Same(reference, Program.DumpToString(doc.Tree), "rootfs volume");
                // The static volume reads back exactly, including its short last block.
                using IBlockSource raw = ubi.Image.OpenVolume(ubi.Image.Volumes[0]);
                Check(raw.ReadBytes(0, (int)raw.Length).AsSpan().SequenceEqual(kernel), "the static volume was read incorrectly");
            }
            foreach (string name in new[] { "rootfs_data", "5" })
            {
                using ImageDocument doc = ImageDocument.Open(path, _dir, volume: name);
                Check(doc.Ubi!.Volume.Id == 5, "volume chosen by '" + name + "': " + doc.Ubi.Volume);
                Same(smallReference, Program.DumpToString(doc.Tree), "rootfs_data volume");
            }
            int asked = 0;
            using (ImageDocument doc = ImageDocument.Open(path, _dir, chooseVolume: u => { asked++; return u.Find("rootfs_data"); }))
                Check(asked == 1 && doc.Ubi!.Volume.Name == "rootfs_data", "the chosen volume was not opened");
            try
            {
                ImageDocument.Open(path, _dir, chooseVolume: _ => null).Dispose();
                throw new TestFailure("a cancelled choice opened the image");
            }
            catch (OperationCanceledException) { /* expected */ }
            ExpectFs(() => ImageDocument.Open(path, _dir, volume: "kernel").Dispose());
            ExpectFs(() => ImageDocument.Open(path, _dir, volume: "nosuch").Dispose());
            ExpectFs(() => ImageDocument.Open(path, _dir, partition: 1).Dispose());
        }
        ExpectFs(() => ImageDocument.Open(Path.Combine(_dir, "convert.ubifs"), _dir, volume: "rootfs").Dispose());

        // A factory image: a kernel in front, the UBI image behind it at an erase block boundary.
        byte[] ubiImage = File.ReadAllBytes(Path.Combine(_dir, "shuffled.ubi"));
        byte[] firmware = new byte[3 * 131072 + ubiImage.Length];
        Bytes(new Random(6), 3 * 131072).CopyTo(firmware, 0);
        ubiImage.CopyTo(firmware, 3 * 131072);
        string fw = WriteFile("factory.bin", firmware);
        using (ImageDocument doc = ImageDocument.Open(fw, _dir))
        {
            Check(doc.EmbeddedOffset == 3 * 131072 && doc.Ubi?.Volume.Name == "rootfs", $"embedded UBI: offset {doc.EmbeddedOffset}");
            Same(reference, Program.DumpToString(doc.Tree), "UBI inside a firmware file");
        }
        ExpectFs(() => ImageDocument.Open(fw, _dir, searchEmbedded: false).Dispose());

        // Two firmware slots in one dump: the blocks of the second image must not be mixed into the first.
        byte[] other = UbifsImageBuilder.WrapUbi(new[] { new UbifsImageBuilder.Volume("rootfs", data) }, imageSeq: 0x55AA55AA);
        string dual = WriteFile("dual.bin", ubiImage.Concat(other).ToArray());
        using (ImageDocument doc = ImageDocument.Open(dual, _dir))
        {
            Check(doc.Warnings.Any(w => w.Contains("another UBI image")), "no warning about the second UBI image");
            Same(reference, Program.DumpToString(doc.Tree), "first of two UBI images");
        }

        // A UBI image without a filesystem this program reads.
        string none = WriteFile("kernel-only.ubi", UbifsImageBuilder.WrapUbi(new[] { volumes[0] }));
        Check(ImageDocument.Detect(none) is null, "a UBI image without filesystems was detected as an image");
        ExpectFs(() => ImageDocument.Open(none, _dir).Dispose());
    }

    private static void UbiSquashFs(FsTree tree, string reference)
    {
        var o = new SaveOptions { Format = ImageFormat.SquashFs, SquashFs = new SquashFsOptions { Compression = SquashCompression.Gzip, GzipLevel = 6 } };
        byte[] squash = WriteImage(tree, o);
        using FsTree small = SmallTree("overlay\n");
        string path = WriteFile("openwrt.ubi", UbifsImageBuilder.WrapUbi(new[]
        {
            new UbifsImageBuilder.Volume("rootfs", squash),
            new UbifsImageBuilder.Volume("rootfs_data", UbifsImageBuilder.FromTree(small).ToArray()),
        }, shuffle: new Random(11)));
        Check(ImageDocument.Detect(path) == ImageFormat.SquashFs, "detected as " + ImageDocument.Detect(path));

        ImageDocument doc = ImageDocument.Open(path, _dir);
        try
        {
            Check(doc.Format == ImageFormat.SquashFs && doc.Ubi?.Volume.Name == "rootfs" && doc.Ubi.Volume.Content == PartitionContent.SquashFs, "volume: " + doc.Ubi?.Volume);
            Check(doc.SavesFilesystemOnly && doc.Container is null, "a UBI volume must be saved as a filesystem alone");
            Check(doc.Warnings.Any(w => w.Contains("UBI images cannot be written")), "warnings: " + string.Join("; ", doc.Warnings));
            Same(Normalize(reference, o), Normalize(Program.DumpToString(doc.Tree), o), "SquashFS volume");

            doc.Tree.Remove(doc.Tree.Resolve("/etc/passwd")!);
            string target = Path.Combine(_dir, "openwrt-root.sqfs");
            SaveOptions save = doc.CreateSaveOptions();
            save.Verify = VerifyLevel.Content;
            doc = doc.Save(target, save).Document;
            Check(doc.Format == ImageFormat.SquashFs && doc.Ubi is null && !doc.SavesFilesystemOnly && doc.SquashFs!.Compression == SquashCompression.Gzip,
                "the saved filesystem: " + doc.FormatName);
            Check(doc.Tree.Resolve("/etc/passwd") is null && doc.Tree.Resolve("/bin/busybox") is not null, "the edit was not saved");
        }
        finally
        {
            doc.Dispose();
        }
    }

    // ---------------------------------------------------------- journal

    private const long JournalTime = 1700000000;

    /// <summary>The tree the journal tests start from.</summary>
    private static FsTree UbifsJournalTree(Random rnd)
    {
        var t = new FsTree(0x1ED, JournalTime);
        FsNode etc = t.CreateDirectory(t.Root, "etc", mtime: JournalTime);
        FsNode data = t.CreateDirectory(t.Root, "data", mtime: JournalTime);
        FsNode old = t.CreateDirectory(t.Root, "old", mtime: JournalTime);
        t.CreateFile(etc, "hostname", new MemoryFileData(Encoding.ASCII.GetBytes("old-host\n")), mtime: JournalTime);
        t.CreateFile(etc, "motd", new MemoryFileData(Encoding.ASCII.GetBytes("welcome\n")), mtime: JournalTime);
        FsNode keep = t.CreateFile(data, "keep.bin", new MemoryFileData(Bytes(rnd, 20000)), mtime: JournalTime);
        t.CreateHardLink(data, "keep-link", keep);
        t.CreateFile(data, "change.bin", new MemoryFileData(Bytes(rnd, 70000)), mtime: JournalTime);
        t.CreateFile(data, "cut.bin", new MemoryFileData(Bytes(rnd, 50000)), mtime: JournalTime);
        t.CreateFile(data, "grow.bin", new MemoryFileData(Bytes(rnd, 10000)), mtime: JournalTime);
        t.CreateFile(old, "gone1", new MemoryFileData(Bytes(rnd, 9000)), mtime: JournalTime);
        t.CreateFile(old, "gone2", new MemoryFileData(Bytes(rnd, 100)), mtime: JournalTime);
        t.CreateSymlink(etc, "link", "motd", mtime: JournalTime);
        data.Inode.XAttrs = new(StringComparer.Ordinal) { ["user.kept"] = new byte[] { 1 } };
        return t;
    }

    private static byte[] ContentOf(FsNode node)
    {
        using Stream s = node.Inode.Data!.OpenRead();
        var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Writes a committed image, then puts a series of changes into its journal and makes the same
    /// changes to the tree. The reader has to show the changed state.
    /// </summary>
    private static void UbifsJournal()
    {
        foreach (UbifsCompression compression in new[] { UbifsCompression.None, UbifsCompression.Zlib })
        {
            using FsTree t = UbifsJournalTree(new Random(4242));
            UbifsImageBuilder b = UbifsImageBuilder.FromTree(t, compression, fanout: 4);
            var rnd = new Random(31);
            FsNode etc = t.Resolve("/etc")!, data = t.Resolve("/data")!, old = t.Resolve("/old")!;
            uint etcInum = b.InumOf(etc), dataInum = b.InumOf(data);
            const int file = UnixMode.S_IFREG | 0x1A4, dir = UnixMode.S_IFDIR | 0x1ED;

            // A new file of two blocks, with an extended attribute.
            byte[] fresh = Bytes(rnd, 5000);
            uint freshInum = b.NewInum();
            b.JournalInode(freshInum, file, fresh.Length, 1, mtime: JournalTime + 1, uid: 7, gid: 8);
            b.JournalData(freshInum, 0, fresh.AsSpan(0, 4096));
            b.JournalData(freshInum, 1, fresh.AsSpan(4096));
            b.JournalEntry(etcInum, "fresh.bin", freshInum);
            b.JournalXattr(freshInum, "user.note", Encoding.ASCII.GetBytes("journal"));
            FsNode freshNode = t.CreateFile(etc, "fresh.bin", new MemoryFileData(fresh), 0x1A4, 7, 8, JournalTime + 1);
            t.SetXAttr(freshNode, "user.note", Encoding.ASCII.GetBytes("journal"));

            // A small file rewritten: a new inode node and a new first block.
            FsNode hostname = t.Resolve("/etc/hostname")!;
            byte[] host = Encoding.ASCII.GetBytes("new-host\n");
            b.JournalInode(b.InumOf(hostname), file, host.Length, 1, mtime: JournalTime + 2);
            b.JournalData(b.InumOf(hostname), 0, host);
            t.SetData(hostname, new MemoryFileData(host), JournalTime + 2);

            // One block in the middle of a file replaced; the same block is written twice and the later write counts.
            FsNode change = t.Resolve("/data/change.bin")!;
            byte[] changed = ContentOf(change);
            b.JournalData(b.InumOf(change), 3, Bytes(rnd, 4096));
            Bytes(rnd, 4096).CopyTo(changed, 3 * 4096);
            b.JournalData(b.InumOf(change), 3, changed.AsSpan(3 * 4096, 4096));
            t.SetData(change, new MemoryFileData(changed), JournalTime);

            // Rename, and a directory with a file in it.
            FsNode motd = t.Resolve("/etc/motd")!;
            b.JournalEntry(etcInum, "motd", 0);
            b.JournalEntry(etcInum, "issue", b.InumOf(motd));
            t.Rename(motd, "issue");
            uint subInum = b.NewInum(), innerInum = b.NewInum();
            b.JournalInode(subInum, dir, 160, 2, mtime: JournalTime + 3);
            b.JournalEntry(dataInum, "sub", subInum, FsNodeType.Directory);
            b.JournalInode(innerInum, UnixMode.S_IFLNK | 0x1FF, 4, 1, Encoding.ASCII.GetBytes("../x"), mtime: JournalTime + 3);
            b.JournalEntry(subInum, "inner", innerInum, FsNodeType.Symlink);
            FsNode sub = t.CreateDirectory(data, "sub", 0x1ED, 0, 0, JournalTime + 3);
            t.CreateSymlink(sub, "inner", "../x", 0, 0, JournalTime + 3);

            b.NewBud();

            // Truncation to a size inside a block, as the kernel writes it: the blocks behind the new end go and the
            // cut block is written again in its short form. The file then grows once more - over a hole, not over
            // the old data.
            FsNode cut = t.Resolve("/data/cut.bin")!;
            byte[] kept = ContentOf(cut).AsSpan(0, 9000).ToArray();
            b.JournalTruncate(b.InumOf(cut), 50000, 9000);
            b.JournalInode(b.InumOf(cut), file, 9000, 1, mtime: JournalTime + 4);
            b.JournalData(b.InumOf(cut), 2, kept.AsSpan(2 * 4096));
            b.JournalInode(b.InumOf(cut), file, 30000, 1, mtime: JournalTime + 4);
            Array.Resize(ref kept, 30000);
            t.SetData(cut, new MemoryFileData(kept), JournalTime + 4);

            // Data appended behind a hole, the inode with the new size never reached the flash: the size follows the data.
            FsNode grow = t.Resolve("/data/grow.bin")!;
            byte[] tail = Bytes(rnd, 1234);
            b.JournalData(b.InumOf(grow), 6, tail);
            byte[] grown = new byte[6 * 4096 + tail.Length];
            ContentOf(grow).CopyTo(grown, 0);
            tail.CopyTo(grown, 6 * 4096);
            t.SetData(grow, new MemoryFileData(grown), JournalTime);

            // A file deleted, and a whole directory with its files.
            FsNode gone1 = t.Resolve("/old/gone1")!, gone2 = t.Resolve("/old/gone2")!;
            b.JournalEntry(b.InumOf(old), "gone1", 0);
            b.JournalDeleteInode(b.InumOf(gone1));
            b.JournalEntry(b.InumOf(old), "gone2", 0);
            b.JournalDeleteInode(b.InumOf(gone2));
            b.JournalEntry(1, "old", 0);
            b.JournalDeleteInode(b.InumOf(old));
            t.Remove(old);

            // One name of a hard-linked file removed, another one added; an attribute dropped.
            FsNode keep = t.Resolve("/data/keep.bin")!;
            b.JournalEntry(dataInum, "keep-link", 0);
            b.JournalEntry(etcInum, "keep-too", b.InumOf(keep));
            t.Remove(t.Resolve("/data/keep-link")!);
            t.CreateHardLink(etc, "keep-too", keep);

            // A file created and deleted again within the journal leaves nothing behind.
            uint brief = b.NewInum();
            b.JournalInode(brief, file, 3, 1);
            b.JournalData(brief, 0, new byte[] { 1, 2, 3 });
            b.JournalEntry(etcInum, "brief", brief);
            b.JournalEntry(etcInum, "brief", 0);
            b.JournalDeleteInode(brief);

            b.JournalTornWrite();

            string path = WriteFile($"journal_{compression}.ubifs", b.ToArray());
            using ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
            UbifsInfo u = doc.Ubifs!;
            Check(u.Dirty && u.ReplayedBuds == 2 && u.ReplayedNodes > 30, $"replay: dirty={u.Dirty} buds={u.ReplayedBuds} nodes={u.ReplayedNodes}");
            Check(doc.Warnings.Any(w => w.Contains("not cleanly unmounted")) && doc.Warnings.Any(w => w.Contains("interrupted write")),
                "warnings: " + string.Join("; ", doc.Warnings));
            Same(Program.DumpToString(t), Program.DumpToString(doc.Tree), "replayed journal");
        }
    }

    private static void UbifsLogEdges()
    {
        using FsTree t = UbifsJournalTree(new Random(4242));
        string reference = Program.DumpToString(t);

        // A block of an older log behind the current one refers to a deletion that was committed long ago.
        UbifsImageBuilder b = UbifsImageBuilder.FromTree(t);
        b.StaleLogBlock(1, "etc");
        string path = WriteFile("stale-log.ubifs", b.ToArray());
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
        {
            Check(doc.Ubifs!.ReplayedNodes == 0, "a stale log block was replayed");
            Same(reference, Program.DumpToString(doc.Tree), "stale log");
        }

        // A log that belongs to another commit is not trusted: the committed state is shown, with a warning.
        b = UbifsImageBuilder.FromTree(t);
        b.LogCommitNumber = 99;
        b.JournalEntry(1, "etc", 0);
        path = WriteFile("foreign-log.ubifs", b.ToArray());
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
        {
            Check(doc.Ubifs!.ReplayedNodes == 0 && doc.Warnings.Any(w => w.Contains("journal was not replayed")), "warnings: " + string.Join("; ", doc.Warnings));
            Same(reference, Program.DumpToString(doc.Tree), "foreign log");
        }
    }

    private static void UbifsDamagedNode()
    {
        using FsTree t = UbifsJournalTree(new Random(4242));
        UbifsImageBuilder b = UbifsImageBuilder.FromTree(t, fanout: 4);
        long at = b.InodeOffset(t.Resolve("/data/change.bin")!);
        byte[] image = b.ToArray();
        image[at + 100] ^= 0x10;                                // a flipped bit in the inode of one file
        string path = WriteFile("bitflip.ubifs", image);
        using ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
        Check(doc.Warnings.Any(w => w.Contains("damaged node was skipped") && w.Contains("bad checksum")), "warnings: " + string.Join("; ", doc.Warnings));
        Check(doc.Warnings.Any(w => w.Contains("'/data/change.bin' points to a missing inode")), "warnings: " + string.Join("; ", doc.Warnings));
        t.Remove(t.Resolve("/data/change.bin")!);
        Same(Program.DumpToString(t), Program.DumpToString(doc.Tree), "image with a damaged inode");
    }

    // ------------------------------------------------------------- fuzz

    private static void UbifsFuzz()
    {
        using FsTree t = UbifsJournalTree(new Random(1));
        UbifsImageBuilder b = UbifsImageBuilder.FromTree(t, UbifsCompression.Zlib, fanout: 3);
        b.JournalEntry(1, "etc", 0);
        byte[] ubifs = b.ToArray();
        byte[] ubi = UbifsImageBuilder.WrapUbi(new[] { new UbifsImageBuilder.Volume("rootfs", UbifsImageBuilder.FromTree(t, UbifsCompression.Zstd).ToArray()) });
        // The nodes of the test images sit right behind the fixed areas, in the first blocks of the main area.
        int main = 10 * UbifsImageBuilder.LebSize;
        var failures = new List<string>();
        string path = Path.Combine(_dir, "fuzz-ubifs.bin");
        var rnd = new Random(77);
        int iterations = int.TryParse(Environment.GetEnvironmentVariable("FSFORGE_FUZZ"), out int custom) ? custom : 200;
        foreach ((string name, byte[] good, int from, int span) in new[]
                 {
                     ("ubifs", ubifs, 0, 4096), ("ubifs", ubifs, UbifsImageBuilder.LebSize, 1024), ("ubifs", ubifs, 3 * UbifsImageBuilder.LebSize, 4200),
                     ("ubifs", ubifs, main, ubifs.Length - main),
                     ("ubi", ubi, 0, 2 * 131072), ("ubi", ubi, 2 * 131072 + 4096 + main, 400000), ("ubi", ubi, 0, ubi.Length),
                 })
        {
            int crashes = 0;
            for (int iter = 0; iter < iterations; iter++)
            {
                byte[] bad = (byte[])good.Clone();
                if (iter % 5 == 4)
                {
                    bad = bad.AsSpan(0, rnd.Next(1, bad.Length)).ToArray();
                }
                else
                {
                    int hits = 1 + rnd.Next(iter % 5 == 0 ? 1 : 10);
                    for (int h = 0; h < hits; h++)
                    {
                        int at = Math.Min(bad.Length - 1, from + rnd.Next(Math.Max(1, span)));
                        bad[at] = iter % 2 == 0 ? (byte)rnd.Next(256) : (byte)(bad[at] ^ (1 << rnd.Next(8)));
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
                        while (s.Read(sink, 0, sink.Length) > 0) { }
                    }
                }
                catch (FsException ex) when (ex is not FsUnexpectedException)
                {
                    // The expected, clean outcome.
                }
                catch (Exception ex)
                {
                    if (++crashes <= 2)
                        Console.WriteLine($"        {name} at {from} #{iter}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace?.Split('\n').Take(4).Aggregate((x, y) => x + "\n" + y)}");
                }
            }
            if (crashes > 0) failures.Add($"{name} at {from}: {crashes}");
        }
        File.Delete(path);
        Check(failures.Count == 0, "damaged images caused unexpected exceptions - " + string.Join(", ", failures));
    }
}
