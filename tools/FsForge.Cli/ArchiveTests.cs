using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FsForge.Core;
using FsForge.Core.Archives;
using FsForge.Core.Compression;
using FsForge.Core.Ext;
using FsForge.Core.Images;
using FsForge.Core.Tar;

namespace FsForge.Cli;

/// <summary>
/// Images packed as FlashTool packs them: bzip2 (one stream, several streams as pbzip2 writes them, the
/// randomised blocks of SharpZipLib), zip and 7z archives with other files beside the image; the checksum
/// files and block maps next to them.
/// </summary>
internal static partial class SelfTest
{
    private static void ArchiveTests(FsTree tree, string reference)
    {
        Test("bzip2: round trips, runs, several blocks and streams, randomised blocks, damage", Bzip2Codec);
        Test("7z: written archives read back with names, times, empty files and folders; damage", SevenZipCodec);
        Test("image in a 7z archive: chosen file, other files carried over, checksum and block map updated", () => ImageInArchive(tree, reference, ArchiveCompression.SevenZip));
        Test("image in a zip archive: several images, chosen by name, other files carried over", () => ImageInArchive(tree, reference, ArchiveCompression.Zip));
        Test("new zip and 7z archives get the image under the archive's name; tar.bz2 and cpio.bz2", () => NewArchives(tree, reference));
        Test("checksum files: sha256sum, BSD and bare formats, lists of several files", ChecksumFiles);
    }

    private static byte[] Bz2(byte[] data, int threads = 0)
    {
        var ms = new MemoryStream();
        using (var z = new Bzip2OutputStream(ms, 9, threads, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    private static byte[] Pack7z(byte[] image)
    {
        var ms = new MemoryStream();
        SevenZipWriter.Write(ms, new[]
        {
            new ArchiveWriteEntry { Name = "README", Length = 5, WriteData = s => s.Write("read\n"u8) },
            new ArchiveWriteEntry { Name = "rootfs.img", Length = image.Length, WriteData = s => s.Write(image) },
        }, 0, 1);
        return ms.ToArray();
    }

    private static byte[] PackZip(byte[] image)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (Stream s = zip.CreateEntry("README").Open()) s.Write("read\n"u8);
            using (Stream s = zip.CreateEntry("rootfs.img", CompressionLevel.Fastest).Open()) s.Write(image);
        }
        return ms.ToArray();
    }

    private static byte[] UnBz2(byte[] packed)
    {
        using var z = new Bzip2InputStream(new MemoryStream(packed));
        var ms = new MemoryStream();
        z.CopyTo(ms);
        return ms.ToArray();
    }

    private static void Bzip2Codec()
    {
        var rnd = new Random(31);
        var cases = new List<(string, byte[])>
        {
            ("empty", Array.Empty<byte>()),
            ("one byte", new byte[] { 42 }),
            ("runs of 3, 4, 5, 255, 256 and 1000 equal bytes", Enumerable.Repeat((byte)7, 3).Concat(new byte[] { 1 }).Concat(Enumerable.Repeat((byte)7, 4)).Concat(new byte[] { 2 })
                .Concat(Enumerable.Repeat((byte)7, 5)).Concat(Enumerable.Repeat((byte)8, 255)).Concat(Enumerable.Repeat((byte)9, 256)).Concat(Enumerable.Repeat((byte)0, 1000)).ToArray()),
            ("random, several blocks", Bytes(rnd, 2_500_000)),
            ("text-like, several blocks", Bytes(rnd, 2_000_000, compressible: true)),
            ("zeros", new byte[3_000_000]),
            ("periodic", Enumerable.Range(0, 1_200_000).Select(i => (byte)(i % 3 == 0 ? 'a' : 'b')).ToArray()),
            ("all byte values", Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7 + i / 256)).ToArray()),
        };
        foreach (var (name, data) in cases)
        {
            byte[] packed = Bz2(data);
            Check(ArchiveCodec.DetectCompression(packed) == ArchiveCompression.Bzip2, name + ": not recognised as bzip2");
            SameBytes(data, UnBz2(packed), name);
            SameBytes(data, UnBz2(Bz2(data, threads: 1)), name + " (one thread)");
        }

        // Several streams in a row, as pbzip2 and lbzip2 write them.
        byte[] a = Bytes(rnd, 300_000, compressible: true), b = Bytes(rnd, 200_000);
        SameBytes(a.Concat(b).ToArray(), UnBz2(Bz2(a).Concat(Bz2(b)).ToArray()), "two streams");

        // SharpZipLib (FlashTool's bzip2 encoder) writes randomised blocks for repetitive data.
        byte[] randomised = Convert.FromHexString(
            "425a683931415926535903d2215e8001604001e00000800018200070334026a4a69de097b029e42f652736ae41d814bb2a13b428bc2a7dd22afb485b2297e2ee48a70a12007a442bc0");
        byte[] expected = Enumerable.Range(0, 200).SelectMany(_ => new byte[1000].Append((byte)1)).ToArray();
        Check((randomised[14] & 0x80) != 0, "the sample block is not randomised");
        SameBytes(expected, UnBz2(randomised), "randomised block");

        // Damage is reported, never a crash or wrong data.
        byte[] good = Bz2(Bytes(rnd, 100_000, compressible: true));
        foreach (int at in new[] { 20, good.Length / 2, good.Length - 5 })
        {
            byte[] bad = (byte[])good.Clone();
            bad[at] ^= 0x10;
            try
            {
                UnBz2(bad);
                throw new TestFailure($"damage at {at} was not noticed");
            }
            catch (FsException)
            {
            }
        }
        try
        {
            UnBz2(good.AsSpan(0, good.Length / 2).ToArray());
            throw new TestFailure("a truncated stream was not noticed");
        }
        catch (FsException)
        {
        }
    }

    private static void SevenZipCodec()
    {
        var rnd = new Random(32);
        DateTime t = new(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        var files = new List<(string Name, byte[]? Data)>
        {
            ("dir", null), ("dir/a.txt", Bytes(rnd, 50_000, compressible: true)), ("empty.bin", Array.Empty<byte>()),
            ("big.img", Bytes(rnd, 9_000_000, compressible: true)), ("rnd.bin", Bytes(rnd, 5_000_000)), ("тест.txt", Encoding.UTF8.GetBytes("Привет\n")),
        };
        var entries = files.Select(f => new ArchiveWriteEntry
        {
            Name = f.Name, IsDirectory = f.Data is null, Length = f.Data?.Length ?? 0, LastWriteTimeUtc = t, Attributes = f.Data is null ? 0x10u : null,
            WriteData = f.Data is { } d ? s => s.Write(d) : null,
        }).ToList();
        string path = Path.Combine(_dir, "t.7z");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite)) SevenZipWriter.Write(fs, entries, 0, 0);

        using (SevenZipArchive a = SevenZipArchive.Open(File.OpenRead(path)))
        {
            Check(a.Entries.Count == files.Count && a.Methods == "LZMA2" && a.IsSolid, $"archive: {a.Entries.Count} entries, {a.Methods}");
            for (int i = 0; i < files.Count; i++)
            {
                ArchiveEntry e = a.Entries[i];
                Check(e.Name == files[i].Name && e.IsDirectory == (files[i].Data is null) && e.LastWriteTimeUtc == t, "entry " + e);
                if (files[i].Data is { } d)
                {
                    using Stream s = a.OpenEntry(e);
                    var ms = new MemoryStream();
                    s.CopyTo(ms);
                    SameBytes(d, ms.ToArray(), e.Name);
                }
            }
            int seen = 0;
            a.ReadAll((e, s) =>
            {
                var ms = new MemoryStream();
                s.CopyTo(ms);
                SameBytes(files[seen].Data ?? Array.Empty<byte>(), ms.ToArray(), "read in order: " + e.Name);
                seen++;
            });
            Check(seen == files.Count, "ReadAll missed entries");
        }

        // Damage anywhere is reported cleanly.
        byte[] good = File.ReadAllBytes(path);
        var r = new Random(5);
        for (int i = 0; i < 60; i++)
        {
            byte[] bad = (byte[])good.Clone();
            int at = i < 10 ? new[] { 0, 3, 5, 6, 9, 13, 17, 22, 26, 30 }[i] : i < 30 ? r.Next(good.Length - 200, good.Length) : r.Next(32, good.Length);
            bad[at] ^= (byte)(1 << r.Next(8));
            string bp = Path.Combine(_dir, "bad.7z");
            File.WriteAllBytes(bp, bad);
            try
            {
                using SevenZipArchive a = SevenZipArchive.Open(File.OpenRead(bp));
                a.ReadAll((e, s) => s.CopyTo(Stream.Null));
                throw new TestFailure($"damage at {at} was not noticed");
            }
            catch (FsException)
            {
            }
        }
        string cut = Path.Combine(_dir, "cut.7z");
        File.WriteAllBytes(cut, good.AsSpan(0, good.Length / 2).ToArray());
        try
        {
            using SevenZipArchive a = SevenZipArchive.Open(File.OpenRead(cut));
            throw new TestFailure("a truncated archive was opened");
        }
        catch (FsException ex)
        {
            Check(ex.Message.Contains("truncated"), "message for a truncated archive: " + ex.Message);
        }
    }

    private static byte[] ExtImage(FsTree tree, out SaveOptions ext)
    {
        ext = new SaveOptions { Format = ImageFormat.Ext, Ext = new ExtOptions { BlockSize = 1024, SizeBytes = 24 << 20 } };
        var ms = new MemoryStream();
        ImageDocument.WriteTo(tree, ms, ext);
        return ms.ToArray();
    }

    private static string Sha256Hex(string path)
    {
        using FileStream fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    private static void ImageInArchive(FsTree tree, string reference, ArchiveCompression kind)
    {
        byte[] image = ExtImage(tree, out SaveOptions ext);
        byte[] readme = Encoding.ASCII.GetBytes("flash with FlashTool\n");
        byte[] second = (byte[])image.Clone();
        string ext7 = kind == ArchiveCompression.Zip ? ".zip" : ".7z";
        string path = Path.Combine(_dir, "firmware" + ext7);
        var names = new[] { "README.txt", "sdcard.img", "spare.img" };
        var data = new[] { readme, image, second };
        if (kind == ArchiveCompression.Zip)
        {
            using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
            for (int i = 0; i < 3; i++)
            {
                using Stream s = zip.CreateEntry(names[i], i == 0 ? CompressionLevel.NoCompression : CompressionLevel.Fastest).Open();
                s.Write(data[i]);
            }
            // spare.img is the only file that is edited in this variant.
        }
        else
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
            SevenZipWriter.Write(fs, Enumerable.Range(0, 2).Select(i => new ArchiveWriteEntry
            {
                Name = names[i], Length = data[i].Length, LastWriteTimeUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), WriteData = s => s.Write(data[i]),
            }).ToList(), 0, 0);
        }
        File.WriteAllText(path + ".sha256", Sha256Hex(path) + "  " + Path.GetFileName(path) + "\n");
        File.WriteAllText(Path.Combine(_dir, "SHA256SUMS"), "0000000000000000000000000000000000000000000000000000000000000000  other.img\n" +
                                                          Sha256Hex(path) + " *" + Path.GetFileName(path) + "\n");
        string bmap = Path.Combine(_dir, "firmware.bmap");
        File.WriteAllText(bmap, "<bmap version=\"2.0\"></bmap>\n");

        Check(ImageDocument.Detect(path) == ImageFormat.Ext, "an image in an archive must be recognised");
        string? entry = kind == ArchiveCompression.Zip ? "spare.img" : null;
        int asked = 0;
        ImageDocument doc = ImageDocument.Open(path, _dir, archiveEntry: entry,
            chooseArchiveEntry: a => { asked++; return a.Find("sdcard.img"); });
        try
        {
            string opened = entry ?? "sdcard.img";
            Check(doc.Format == ImageFormat.Ext && doc.OuterCompression == kind && doc.Archive is { } arc && arc.EntryName == opened
                  && arc.OtherFiles.Contains("README.txt"), "archive information: " + doc.Archive);
            Check(asked == 0, "the chooser was asked although a name was given or there was a single image");
            Same(Normalize(reference, ext), Normalize(Program.DumpToString(doc.Tree), ext), "image in the archive");

            AddFile(doc, "/etc", "flashed.conf", Encoding.ASCII.GetBytes("1\n"));
            string expected = Program.DumpToString(doc.Tree);
            SaveOptions o = doc.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir });
            o.Tar.XzEffort = 0;
            ImageDocument.SaveResult r = doc.Save(path, o);
            doc = r.Document;
            Check(doc.Archive is { } again && again.EntryName == opened && doc.OuterCompression == kind, "the saved file is not the same kind of archive");
            Same(expected, Program.DumpToString(doc.Tree), "after saving into the archive");

            using (var a = ImageArchive.Open(path, kind))
            {
                var list = a.Entries.Select(e => e.Name).ToList();
                var want = kind == ArchiveCompression.Zip ? names.ToList() : names.Take(2).ToList();
                Check(list.SequenceEqual(want), "entries after saving: " + string.Join(", ", list));
                foreach (ArchiveEntry e in a.Entries.Where(e => e.Name != opened))
                {
                    using Stream s = a.OpenEntry(e);
                    var ms = new MemoryStream();
                    s.CopyTo(ms);
                    SameBytes(data[Array.IndexOf(names, e.Name)], ms.ToArray(), "carried over: " + e.Name);
                }
            }

            string hash = Sha256Hex(path);
            Check(File.ReadAllText(path + ".sha256") == hash + "  " + Path.GetFileName(path) + "\n", "the .sha256 file was not updated");
            string sums = File.ReadAllText(Path.Combine(_dir, "SHA256SUMS"));
            Check(sums.Contains(hash + " *" + Path.GetFileName(path)) && sums.Contains(new string('0', 64) + "  other.img"), "SHA256SUMS: " + sums);
            Check(r.Warnings.Count == 0 && r.UpdatedFiles is { } up && up.Contains(Path.GetFileName(path) + ".sha256") && up.Contains("SHA256SUMS") && up.Contains("firmware.bmap"),
                "updated files: " + string.Join("; ", r.UpdatedFiles ?? Array.Empty<string>()) + " / warnings: " + string.Join("; ", r.Warnings));
            CheckBmap(bmap, doc);
        }
        finally
        {
            doc.Dispose();
            File.Delete(Path.Combine(_dir, "SHA256SUMS"));
            File.Delete(bmap);
        }
    }

    /// <summary>The rebuilt block map maps exactly the blocks of the image that hold data, with their hashes.</summary>
    private static void CheckBmap(string bmapPath, ImageDocument doc)
    {
        string text = File.ReadAllText(bmapPath);
        string tmp = Path.Combine(_dir, "bmap-check.img");
        using (var a = ImageArchive.Open(doc.FilePath!, doc.OuterCompression!.Value))
        using (Stream s = a.OpenEntry(a.Find(doc.Archive!.EntryName)!))
        using (var f = File.Create(tmp))
            s.CopyTo(f);
        byte[] raw = File.ReadAllBytes(tmp);
        File.Delete(tmp);
        var ranges = System.Text.RegularExpressions.Regex.Matches(text, "<Range chksum=\"([0-9a-f]{64})\"> ([0-9]+)(?:-([0-9]+))? </Range>");
        Check(ranges.Count > 0 && text.Contains($"<ImageSize> {raw.Length} </ImageSize>"), "block map: " + text[..Math.Min(400, text.Length)]);
        var mapped = new bool[(raw.Length + 4095) / 4096];
        foreach (System.Text.RegularExpressions.Match m in ranges)
        {
            long first = long.Parse(m.Groups[2].Value), last = m.Groups[3].Success ? long.Parse(m.Groups[3].Value) : first;
            long end = Math.Min((last + 1) * 4096, raw.Length);
            string h = Convert.ToHexString(SHA256.HashData(raw.AsSpan((int)(first * 4096), (int)(end - first * 4096)))).ToLowerInvariant();
            Check(h == m.Groups[1].Value, $"block map: wrong hash for blocks {first}-{last}");
            for (long b = first; b <= last; b++) mapped[b] = true;
        }
        for (int b = 0; b < mapped.Length; b++)
        {
            bool data = raw.AsSpan(b * 4096, Math.Min(4096, raw.Length - b * 4096)).IndexOfAnyExcept((byte)0) >= 0;
            Check(data == mapped[b], $"block map: block {b} {(data ? "holds data but is not mapped" : "is mapped but empty")}");
        }
        // The checksum of the file itself, computed with its own field as zeros.
        var own = System.Text.RegularExpressions.Regex.Match(text, "<BmapFileChecksum> ([0-9a-f]{64}) </BmapFileChecksum>");
        string zeroed = text.Replace(own.Groups[1].Value, new string('0', 64));
        Check(Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(zeroed))).ToLowerInvariant() == own.Groups[1].Value, "block map: wrong file checksum");
    }

    private static void NewArchives(FsTree tree, string reference)
    {
        byte[] image = ExtImage(tree, out SaveOptions ext);
        string src = Path.Combine(_dir, "rootfs.ext4");
        File.WriteAllBytes(src, image);
        ImageDocument doc = ImageDocument.Open(src, _dir);
        try
        {
            foreach (var (name, kind, inner) in new[] { ("backup.7z", ArchiveCompression.SevenZip, "backup.img"), ("rootfs.ext4.zip", ArchiveCompression.Zip, "rootfs.ext4") })
            {
                string target = Path.Combine(_dir, name);
                SaveOptions o = doc.CreateSaveOptions(null, new SaveOptions { Verify = VerifyLevel.Content, TempDirectory = _dir });
                o.Tar.XzEffort = 0;
                using ImageDocument saved = doc.Save(target, o).Document;
                // Save() disposed the source document: reopen it for the next round.
                doc = ImageDocument.Open(src, _dir);
                Check(saved.Archive is { } a && a.Kind == kind && a.EntryName == inner && a.OtherFiles.Count == 0, $"{name}: {saved.Archive}");
                Same(Normalize(reference, ext), Normalize(Program.DumpToString(saved.Tree), ext), name);
            }
        }
        finally
        {
            doc.Dispose();
        }

        foreach (ImageFormat f in new[] { ImageFormat.Tar, ImageFormat.Cpio })
        {
            string name = f == ImageFormat.Tar ? "w.tar.bz2" : "w.cpio.bz2";
            var o = new SaveOptions { Format = f };
            o.Tar.Compression = ArchiveCompression.Bzip2;
            o.Cpio.Compression = ArchiveCompression.Bzip2;
            string path = Path.Combine(_dir, name);
            using (var fs = File.Create(path)) ImageDocument.WriteTo(tree, fs, o);
            Check(ArchiveCodec.DetectCompression(File.ReadAllBytes(path)) == ArchiveCompression.Bzip2, name + " is not bzip2");
            using ImageDocument d = ImageDocument.Open(path, _dir);
            Check(d.Format == f && (f == ImageFormat.Tar ? d.Tar!.Compression : d.Cpio!.Compression) == ArchiveCompression.Bzip2, name + ": " + d.FormatName);
            Same(Normalize(reference, o), Normalize(Program.DumpToString(d.Tree), o), name);
        }
    }

    private static void ChecksumFiles()
    {
        string h = new('a', 64), n = new('b', 64);
        // A sidecar of the image's own name with a single value: taken whatever name it lists.
        Check(Sidecar("aaaa".PadRight(64, 'a') + "  sdcard.img.xz\n", true) == n + "  sdcard.img.xz\n", "gnu line");
        Check(Sidecar(h.ToUpperInvariant() + "\n", true) == n.ToUpperInvariant() + "\n", "bare upper-case value");
        Check(Sidecar($"SHA256 (sdcard.img.xz) = {h}\n", false) == $"SHA256 (sdcard.img.xz) = {n}\n", "BSD line");
        Check(Sidecar($"{h}  old-name.img.xz\n", true) == $"{n}  old-name.img.xz\n", "own sidecar, other name");
        Check(Sidecar($"{h}  other.img\n", false) is null, "a list without the file must stay untouched");
        Check(Sidecar($"# comment\r\n{h} *./images/sdcard.img.xz\r\n{h}  other.img\r\n", false) == $"# comment\r\n{n} *./images/sdcard.img.xz\r\n{h}  other.img\r\n", "list with paths and CRLF");
    }

    private static string? Sidecar(string text, bool own) =>
        typeof(ImageDocument).Assembly.GetType("FsForge.Core.Images.Sidecars")!
            .GetMethod("Replace", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, new object[] { text, "sdcard.img.xz", new string('b', 64), own }) as string;
}
