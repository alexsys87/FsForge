using System.Text;
using FsForge.Core;
using FsForge.Core.Images;
using FsForge.Core.SquashFs;
using FsForge.Core.Ubifs;

namespace FsForge.Cli;

/// <summary>
/// UBIFS and UBI. The writers are tested by writing and reading back, like the other formats; an
/// independent, test-only writer (<see cref="UbifsImageBuilder"/>) provides images with a journal,
/// with damaged nodes and with UBI layouts the product itself does not produce.
/// </summary>
internal static partial class SelfTest
{
    private static void UbifsTests(FsTree tree, string reference)
    {
        foreach (UbifsCompression c in new[] { UbifsCompression.None, UbifsCompression.Zlib, UbifsCompression.Zstd })
            Test($"ubifs {c}: read", () => UbifsRead(tree, reference, c, c == UbifsCompression.Zlib ? 3 : 8));
        Test("lzo and deflate compressors", UbifsCodecs);
        foreach (UbifsCompression c in Enum.GetValues<UbifsCompression>())
            Test($"ubifs {c}: write", () => RoundTrip(tree, reference, $"w_{c}.ubifs", new SaveOptions { Format = ImageFormat.Ubifs, Ubifs = new UbifsOptions { Compression = c } }));
        Test("ubifs NOR geometry, fanout 3, space fixup", () => RoundTrip(tree, reference, "w_nor.ubifs", new SaveOptions
        {
            Format = ImageFormat.Ubifs,
            Ubifs = new UbifsOptions { LebSize = 65408, MinIoSize = 1, Fanout = 3, SpaceFixup = true, Compression = UbifsCompression.Zlib },
        }));
        Test("ubifs 4 KiB pages, large maximum size (big LPT)", () => RoundTrip(tree, reference, "w_big.ubifs", new SaveOptions
        {
            Format = ImageFormat.Ubifs,
            Ubifs = new UbifsOptions { LebSize = 253952, MinIoSize = 4096, MaxLebCount = 500000, Compression = UbifsCompression.Zstd },
        }));
        Test("ubifs: geometry is kept on saving, invalid options and oversized content are rejected", () => UbifsWriteOptions(tree));
        Test("ubifs: saved as SquashFS, ext4 and tar, and back as UBIFS in place", () => UbifsConvert(tree, reference));
        Test("ubi: a new UBI image around UBIFS and around SquashFS", () => UbiNew(tree, reference));
        Test("ubi: a volume is replaced, the other volumes are carried over", () => UbiRewrite(tree, reference));
        Test("ubi: volumes, choice of a volume, scrambled blocks, image inside a firmware file", () => UbiVolumes(tree, reference));
        Test("ubi: a SquashFS volume is opened and, on request, saved as the filesystem alone", () => UbiSquashFs(tree, reference));
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
            Check(!doc.SavesFilesystemOnly && doc.Warnings.Count == 0, "warnings: " + string.Join("; ", doc.Warnings));
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
                SaveOptions o = doc.CreateSaveOptions();
                Check(o.Format == ImageFormat.Ubifs && o.Ubifs.Compression == UbifsCompression.Zlib, "default target format: " + o.Format);
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

        // Edited and saved over itself, with a backup: the file stays UBIFS with the same identity.
        ImageDocument own = ImageDocument.Open(path, _dir);
        try
        {
            Guid uuid = own.Ubifs!.Uuid;
            own.Tree.Remove(own.Tree.Resolve("/etc/passwd")!);
            SaveOptions o = own.CreateSaveOptions();
            o.Verify = VerifyLevel.Content;
            o.KeepBackup = true;
            own = own.Save(path, o).Document;
            Check(own.Format == ImageFormat.Ubifs && own.Ubifs!.Uuid == uuid && own.Ubifs.DefaultCompression == UbifsCompression.Zlib && own.Ubifs.Fanout == 8,
                "identity after saving in place: " + own.Ubifs!.Uuid);
            Check(own.Tree.Resolve("/etc/passwd") is null && own.Tree.Resolve("/bin/busybox") is not null && own.Warnings.Count == 0, "the edit was not saved");
            using ImageDocument backup = ImageDocument.Open(path + ".bak", _dir);
            Same(reference, Program.DumpToString(backup.Tree), "backup of the UBIFS image");
        }
        finally
        {
            own.Dispose();
        }
    }

    private static void UbifsCodecs()
    {
        var rnd = new Random(8);
        var lzo = new FsForge.Core.Compression.LzoCodec();
        byte[] text = Bytes(rnd, 70000, compressible: true), mixed = new byte[9000];
        Bytes(rnd, 3000).CopyTo(mixed, 2000);
        foreach (byte[] data in new[] { text, mixed, Bytes(rnd, 5000), new byte[4096], new byte[1], Array.Empty<byte>(), text.AsSpan(0, 4096).ToArray(), text.AsSpan(1, 127).ToArray() })
        {
            byte[] packed = new byte[data.Length * 2 + 64], back = new byte[data.Length];
            int n = lzo.Compress(data, packed);
            Check(n > 0 && lzo.Decompress(packed.AsSpan(0, n), back) == data.Length && back.AsSpan().SequenceEqual(data), $"lzo round trip of {data.Length} bytes");
            Check(data.Length < 4096 || data == mixed || rnd.Next() >= 0 && (data.Any(b => b != data[0]) && data != text ? true : n < data.Length / 2), $"lzo ratio of {data.Length} bytes: {n}");

            foreach (int window in new[] { 8, 11, 15 })
            {
                n = new FsForge.Core.Compression.RawDeflate(window).Compress(data, packed);
                Check(n > 0, "deflate did not fit");
                using var inflate = new System.IO.Compression.DeflateStream(new MemoryStream(packed, 0, n), System.IO.Compression.CompressionMode.Decompress);
                var plain = new MemoryStream();
                inflate.CopyTo(plain);
                Check(plain.ToArray().AsSpan().SequenceEqual(data), $"deflate round trip of {data.Length} bytes, window 2^{window}");
            }
        }
        Check(lzo.Compress(text, new byte[100]) == -1 && new FsForge.Core.Compression.RawDeflate(11).Compress(Bytes(rnd, 5000), new byte[100]) == -1, "an output that does not fit must be reported");
    }

    private static void UbifsWriteOptions(FsTree tree)
    {
        var o = new SaveOptions
        {
            Format = ImageFormat.Ubifs,
            Ubifs = new UbifsOptions { LebSize = 129024, MinIoSize = 2048, MaxLebCount = 3000, Fanout = 16, Compression = UbifsCompression.Zstd, SpaceFixup = true, Uuid = Guid.Parse("01020304-0506-0708-090a-0b0c0d0e0f10") },
        };
        string path = WriteFile("geometry.ubifs", WriteImage(tree, o));
        using (ImageDocument doc = ImageDocument.Open(path, _dir))
        {
            UbifsInfo u = doc.Ubifs!;
            Check(u is { LebSize: 129024, MinIoSize: 2048, MaxLebCount: 3000, Fanout: 16, DefaultCompression: UbifsCompression.Zstd, SpaceFixup: true, FormatVersion: 4, Dirty: false, ReplayedNodes: 0 }
                  && u.Uuid == o.Ubifs.Uuid && u.LebCount * (long)u.LebSize == new FileInfo(path).Length, $"written geometry: {u.LebSize}/{u.MinIoSize}/{u.MaxLebCount}/{u.Fanout} {u.Uuid}");
            UbifsOptions again = doc.CreateSaveOptions().Ubifs;
            Check(again is { LebSize: 129024, MinIoSize: 2048, MaxLebCount: 3000, Fanout: 16, Compression: UbifsCompression.Zstd, SpaceFixup: true } && again.Uuid == o.Ubifs.Uuid
                  && again.JournalBytes == u.JournalBytes && again.LogLebs == u.LogLebs, "options taken from the image");
            using (var src = new FileBlockSource(path))
                Check(UbifsReader.MinimumVolumeLebs(src) >= u.LebCount, "minimum volume size");
        }

        foreach (Action<UbifsOptions> spoil in new Action<UbifsOptions>[]
                 {
                     x => x.LebSize = 10000, x => x.LebSize = 126977, x => x.MinIoSize = 3000, x => x.LebSize = 3 << 20, x => x.Fanout = 2,
                     x => x.MaxLebCount = 10, x => x.OrphanLebs = 0, x => x.Compression = (UbifsCompression)9, x => x.LogLebs = 1,
                 })
        {
            var bad = new UbifsOptions();
            spoil(bad);
            ExpectFs(() => ImageDocument.WriteTo(tree, new MemoryStream(), new SaveOptions { Format = ImageFormat.Ubifs, Ubifs = bad }));
        }
        // More content than the filesystem is allowed to take.
        try
        {
            ImageDocument.WriteTo(tree, new MemoryStream(), new SaveOptions { Format = ImageFormat.Ubifs, Ubifs = new UbifsOptions { MaxLebCount = 40 } });
            throw new TestFailure("content beyond the maximum size was accepted");
        }
        catch (FsNoSpaceException) { /* expected */ }

        // Attributes of namespaces UBIFS does not have are dropped with a warning; an empty tree is a valid image.
        using var t = new FsTree(0x1ED, 1700000000);
        FsNode f = t.CreateFile(t.Root, "acl", new MemoryFileData(new byte[] { 1 }), mtime: 1700000000);
        f.Inode.XAttrs = new(StringComparer.Ordinal) { ["system.posix_acl_access"] = new byte[] { 2, 0, 0, 0 }, ["user.kept"] = new byte[] { 7 } };
        var ms = new MemoryStream();
        IReadOnlyList<string> warnings = ImageDocument.WriteTo(t, ms, new SaveOptions { Format = ImageFormat.Ubifs });
        Check(warnings.Count == 1 && warnings[0].Contains("system.posix_acl_access"), "warnings: " + string.Join("; ", warnings));
        using (ImageDocument doc = ImageDocument.Open(WriteFile("xattr.ubifs", ms.ToArray()), _dir))
            Check(doc.Tree.Resolve("/acl")!.Inode.XAttrs is { Count: 1 } x && x.ContainsKey("user.kept"), "attributes after writing");
        using var empty = new FsTree(0x1C0, 5);
        using (ImageDocument doc = ImageDocument.Open(WriteFile("empty.ubifs", WriteImage(empty, new SaveOptions { Format = ImageFormat.Ubifs })), _dir))
            Check(doc.Tree.Root.ChildCount == 0 && doc.Tree.Root.Inode is { Mode: 0x1C0, MTime: 5 }, "empty UBIFS image");
    }

    private static void UbiNew(FsTree tree, string reference)
    {
        byte[] plain = WriteImage(tree, new SaveOptions { Format = ImageFormat.SquashFs, SquashFs = new SquashFsOptions { Compression = SquashCompression.Gzip, GzipLevel = 6 } });
        foreach ((ImageFormat format, UbiOptions ubi) in new[]
                 {
                     (ImageFormat.Ubifs, new UbiOptions()),
                     (ImageFormat.Ubifs, new UbiOptions { SubPageSize = 512, VolumeName = "root fs", VolumeId = 4, AutoResize = false, VolumeBytes = 64 << 20 }),
                     (ImageFormat.Ubifs, new UbiOptions { PebSize = 65536, MinIoSize = 1 }),
                     (ImageFormat.SquashFs, new UbiOptions { PebSize = 262144, MinIoSize = 4096, VolumeName = "rootfs" }),
                 })
        {
            string path = Path.Combine(_dir, $"new_{format}_{ubi.PebSize}_{ubi.SubPageSize}.ubi");
            // The test tree comes in through a plain image of it, as it would in a conversion.
            ImageDocument doc = ImageDocument.Open(WriteFile("ubi-source.sqfs", plain), _dir);
            try
            {
                SaveOptions o = doc.CreateSaveOptions(format);
                o.NewUbi = true;
                o.Ubi = ubi;
                o.Verify = VerifyLevel.Content;
                o.SquashFs.XzEffort = 0;
                Check(doc.WritesUbi(o) && !doc.WritesContainer(o), "a new UBI image was not announced");
                doc = doc.Save(path, o).Document;
                UbiContainer c = doc.Ubi ?? throw new TestFailure("the saved file is not a UBI image");
                Check(doc.Format == format && c.Volume.Name == ubi.VolumeName && c.Volume.Id == ubi.VolumeId && c.Volume.AutoResize == ubi.AutoResize && !c.Volume.IsStatic,
                    "volume: " + c.Volume);
                Check(c.Image.PebSize == ubi.PebSize && c.Image.LebSize == ubi.LebSize && c.Image.VidHeaderOffset == ubi.VidHeaderOffset && c.Image.Volumes.Count == 1
                      && c.Image.Warnings.Count == 0, $"geometry: {c.Image.PebSize}/{c.Image.LebSize}/{c.Image.VidHeaderOffset}");
                Check(c.Image.GuessMinIoSize() == ubi.MinIoSize, "guessed min. I/O unit: " + c.Image.GuessMinIoSize());
                if (format == ImageFormat.Ubifs)
                {
                    Check(doc.Ubifs is { } u && u.LebSize == ubi.LebSize && u.MinIoSize == Math.Max(8, ubi.MinIoSize), "UBIFS geometry follows the UBI image");
                    Check(c.Volume.ReservedLebs >= Math.Max(doc.Ubifs!.LebCount, (int)(ubi.VolumeBytes / ubi.LebSize)), "reserved size: " + c.Volume.ReservedLebs);
                }
                Same(Normalize(reference, o), Normalize(Program.DumpToString(doc.Tree), o), "new UBI image");
                Check(!doc.SavesFilesystemOnly && doc.WritesContainer(doc.CreateSaveOptions()), "the saved UBI image must be saved as a UBI image again");
            }
            finally
            {
                doc.Dispose();
            }
        }
        using ImageDocument ext = ImageDocument.CreateNew(ImageFormat.Ext);
        ExpectFs(() => ext.Save(Path.Combine(_dir, "ext.ubi"), new SaveOptions { Format = ImageFormat.Ext, NewUbi = true }));
        foreach (Action<UbiOptions> spoil in new Action<UbiOptions>[] { x => x.PebSize = 1000, x => x.MinIoSize = 3, x => x.SubPageSize = 4096, x => x.VolumeName = "", x => x.VolumeId = 200 })
        {
            var bad = new UbiOptions();
            spoil(bad);
            ExpectFs(bad.Validate);
        }
    }

    /// <summary>
    /// A UBI image as ubinize or a device leaves it - a static kernel volume, the root filesystem and a
    /// data volume, blocks in scrambled order - is edited and saved in place, one volume at a time.
    /// </summary>
    private static void UbiRewrite(FsTree tree, string reference)
    {
        using FsTree small = SmallTree("rootfs_data\n");
        string smallReference = Program.DumpToString(small);
        byte[] kernel = Bytes(new Random(5), 300000);
        byte[] squash = WriteImage(tree, new SaveOptions { Format = ImageFormat.SquashFs, SquashFs = new SquashFsOptions { Compression = SquashCompression.Gzip, GzipLevel = 6 } });
        foreach (bool squashRoot in new[] { false, true })
        {
            string path = WriteFile(squashRoot ? "device-sq.ubi" : "device.ubi", UbifsImageBuilder.WrapUbi(new[]
            {
                new UbifsImageBuilder.Volume("kernel", kernel, Static: true),
                new UbifsImageBuilder.Volume("rootfs", squashRoot ? squash : UbifsImageBuilder.FromTree(tree, UbifsCompression.Zstd).ToArray()),
                new UbifsImageBuilder.Volume("rootfs_data", UbifsImageBuilder.FromTree(small).ToArray(), Id: 5),
            }, shuffle: new Random(3)));

            // The root volume: one file removed, saved over the same file.
            ImageDocument doc = ImageDocument.Open(path, _dir);
            try
            {
                SaveOptions o = doc.CreateSaveOptions();
                Check(doc.WritesContainer(o) && doc.WritesUbi(o) && o.Format == (squashRoot ? ImageFormat.SquashFs : ImageFormat.Ubifs), "the UBI image must be kept by default");
                doc.Tree.Remove(doc.Tree.Resolve("/etc/passwd")!);
                o.Verify = VerifyLevel.Content;
                ImageDocument.SaveResult r = doc.Save(path, o);
                doc = r.Document;
                UbiContainer c = doc.Ubi ?? throw new TestFailure("the saved file is not a UBI image");
                Check(c.Volume is { Name: "rootfs", Id: 1 } && c.Image.Volumes.Count == 3 && c.Image.Warnings.Count == 0, "volumes after saving: " + string.Join("; ", c.Image.Volumes));
                Check(c.Image.Volumes[0] is { Name: "kernel", IsStatic: true } k && k.Length == kernel.Length
                      && c.Image.OpenVolume(k).ReadBytes(0, kernel.Length).AsSpan().SequenceEqual(kernel), "the static volume changed");
                Check(c.Image.Volumes[2] is { Name: "rootfs_data", Id: 5 }, "the data volume: " + c.Image.Volumes[2]);
                Check(doc.Tree.Resolve("/etc/passwd") is null && doc.Tree.Resolve("/bin/busybox") is not null, "the edit was not saved");
                if (!squashRoot) Check(doc.Ubifs is { DefaultCompression: UbifsCompression.Zstd, LebSize: UbifsImageBuilder.LebSize, MinIoSize: UbifsImageBuilder.MinIo }, "UBIFS parameters of the volume");
            }
            finally
            {
                doc.Dispose();
            }

            // The data volume next: it grows, the others stay.
            doc = ImageDocument.Open(path, _dir, volume: "rootfs_data");
            try
            {
                Same(smallReference, Program.DumpToString(doc.Tree), "data volume after the root volume was replaced");
                doc.Tree.CreateFile(doc.Tree.Resolve("/upper")!, "big.bin", new MemoryFileData(Bytes(new Random(9), 3 << 20)), mtime: 1700000001);
                string expected = Program.DumpToString(doc.Tree);
                SaveOptions o = doc.CreateSaveOptions();
                o.Verify = VerifyLevel.Content;
                ImageDocument.SaveResult r = doc.Save(path, o);
                doc = r.Document;
                Check(doc.Ubi!.Volume.Name == "rootfs_data" && r.Warnings.Any(w => w.Contains("enlarged")), "warnings: " + string.Join("; ", r.Warnings));
                Same(expected, Program.DumpToString(doc.Tree), "data volume after growing");
            }
            finally
            {
                doc.Dispose();
            }
            using (ImageDocument root = ImageDocument.Open(path, _dir, volume: "rootfs"))
                Check(root.Ubi!.Volume.Name == "rootfs" && root.Tree.Resolve("/etc/passwd") is null && root.Tree.Resolve("/var/sparse") is not null, "the root volume after the data volume was replaced");

            // The filesystem alone on request, and a tar archive: neither touches the UBI image.
            byte[] before = File.ReadAllBytes(path);
            doc = ImageDocument.Open(path, _dir, volume: "rootfs");
            try
            {
                SaveOptions o = doc.CreateSaveOptions();
                o.KeepContainer = false;
                Check(!doc.WritesContainer(o) && !doc.WritesUbi(o), "filesystem alone");
                string alone = Path.Combine(_dir, squashRoot ? "alone.sqfs" : "alone.ubifs");
                doc = doc.Save(alone, o).Document;
                Check(doc.Ubi is null && doc.Format == (squashRoot ? ImageFormat.SquashFs : ImageFormat.Ubifs) && doc.FilePath == alone, "saved alone: " + doc.FormatName);
                // A SquashFS volume becomes a UBIFS volume when the format is changed and the image kept.
                if (squashRoot)
                {
                    doc.Dispose();
                    doc = ImageDocument.Open(path, _dir, volume: "rootfs");
                    SaveOptions u = doc.CreateSaveOptions(ImageFormat.Ubifs);
                    string converted = Path.Combine(_dir, "device-converted.ubi");
                    doc = doc.Save(converted, u).Document;
                    Check(doc.Format == ImageFormat.Ubifs && doc.Ubi is { Volume.Name: "rootfs" } && doc.Ubifs is { LebSize: UbifsImageBuilder.LebSize, MinIoSize: UbifsImageBuilder.MinIo },
                        "SquashFS volume converted to UBIFS: " + doc.FormatName);
                }
            }
            finally
            {
                doc.Dispose();
            }
            Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(before), "saving elsewhere changed the UBI image");
        }
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
                Check(!doc.SavesFilesystemOnly && doc.EmbeddedOffset == 0, "a UBI image can be saved in place");
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
            Check(!doc.SavesFilesystemOnly && doc.Container is null, "a UBI image can be saved in place");
            Same(Normalize(reference, o), Normalize(Program.DumpToString(doc.Tree), o), "SquashFS volume");

            doc.Tree.Remove(doc.Tree.Resolve("/etc/passwd")!);
            string target = Path.Combine(_dir, "openwrt-root.sqfs");
            SaveOptions save = doc.CreateSaveOptions();
            save.KeepContainer = false;
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
