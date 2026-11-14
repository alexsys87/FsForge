using System.Buffers.Binary;
using System.Text;
using FsForge.Core;
using FsForge.Core.Ext;
using FsForge.Core.Images;

namespace FsForge.Cli;

/// <summary>
/// Replay of an ext journal that was left unreplayed. The test writes an image in its old state,
/// puts the changes into its journal by hand (with escaped blocks, a revoked block and an
/// uncommitted transaction), marks it "needs recovery" and expects the reader to show the new state.
/// </summary>
internal static partial class SelfTest
{
    private const uint JbdMagic = 0xC03B3998;

    private static void JournalTests()
    {
        foreach (ExtKind kind in new[] { ExtKind.Ext3, ExtKind.Ext4 })
            foreach (int bs in new[] { 1024, 4096 })
                Test($"{kind} block={bs}: an unreplayed journal is replayed while reading", () => JournalReplay(kind, bs));
    }

    /// <summary>The same small tree before and after a few edits that keep the image layout.</summary>
    private static FsTree JournalTree(bool edited, out byte[] keep)
    {
        var rnd = new Random(777);
        keep = new byte[20000];
        rnd.NextBytes(keep);
        byte[] change = new byte[6000];
        rnd.NextBytes(change);
        const long T = 1700000000;

        var t = new FsTree(0x1ED, T);
        FsNode etc = t.CreateDirectory(t.Root, "etc", mtime: T);
        FsNode data = t.CreateDirectory(t.Root, "data", mtime: T);
        FsNode bin = t.CreateDirectory(t.Root, "bin", edited ? 0x1C0 : 0x1ED, mtime: T);
        t.CreateFile(etc, "hostname", new MemoryFileData(Encoding.ASCII.GetBytes(edited ? "new-host\n" : "old-host\n")), mtime: edited ? T + 500 : T);
        t.CreateFile(etc, edited ? "issu" : "motd", new MemoryFileData(Encoding.ASCII.GetBytes("welcome\n")), mtime: T);
        t.CreateFile(data, "keep.bin", new MemoryFileData(keep), mtime: T);
        // A block that starts with the journal magic is stored "escaped" in the journal.
        if (edited) BinaryPrimitives.WriteUInt32BigEndian(change, JbdMagic);
        t.CreateFile(data, "change.bin", new MemoryFileData(change), mtime: T);
        t.CreateSymlink(bin, "sh", "busybox", mtime: T);
        return t;
    }

    private static byte[] WriteImage(FsTree tree, SaveOptions o)
    {
        using var ms = new MemoryStream();
        ImageDocument.WriteTo(tree, ms, o);
        return ms.ToArray();
    }

    private static string DumpFile(string path, out IReadOnlyList<string> warnings, out ExtInfo info)
    {
        using ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false);
        warnings = doc.Warnings.ToList();
        info = doc.Ext!;
        return Program.DumpToString(doc.Tree);
    }

    private static uint Be32(byte[] b, long o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)o));
    private static void Be32(byte[] b, long o, uint v) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan((int)o), v);

    private static long FindBlock(byte[] image, int bs, ReadOnlySpan<byte> content)
    {
        for (long i = 0; i < image.Length / bs; i++)
            if (image.AsSpan((int)(i * bs), bs).SequenceEqual(content)) return i;
        throw new TestFailure("a data block of the test file was not found in the image");
    }

    private static void JournalReplay(ExtKind kind, int bs)
    {
        var o = new SaveOptions
        {
            Format = ImageFormat.Ext,
            Ext = new ExtOptions
            {
                Kind = kind, BlockSize = bs, SizeBytes = 16 << 20, Journal = true, Timestamp = 1700000000,
                Uuid = new Guid("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0"),
            },
        };
        byte[] keep;
        byte[] a, b;
        using (FsTree before = JournalTree(false, out keep)) a = WriteImage(before, o);
        using (FsTree after = JournalTree(true, out _)) b = WriteImage(after, o);
        Check(a.Length == b.Length, "the test images differ in size");
        long blocks = a.Length / bs;

        // The journal written by ExtWriter is one contiguous run that starts with its superblock.
        long jsb = -1;
        for (long i = 1; i < blocks && jsb < 0; i++)
            if (Be32(a, i * bs) == JbdMagic && Be32(a, i * bs + 4) == 4 && Be32(a, i * bs + 12) == bs) jsb = i;
        Check(jsb > 0, "journal superblock not found");
        long maxLen = Be32(a, jsb * bs + 0x10);

        var changed = new List<long>();
        for (long i = 0; i < blocks; i++)
            if (!a.AsSpan((int)(i * bs), bs).SequenceEqual(b.AsSpan((int)(i * bs), bs))) changed.Add(i);
        Check(changed.Count >= 3, "the edits changed too few blocks");
        Check(changed.All(c => c < jsb || c >= jsb + maxLen), "the edits moved the journal; the test needs a fixed layout");

        long x = FindBlock(a, bs, keep.AsSpan(0, bs));          // overwritten, then revoked: keeps its content
        long y = FindBlock(a, bs, keep.AsSpan(bs, bs));         // overwritten by an uncommitted transaction
        byte[] junk = new byte[bs];
        new Random(5).NextBytes(junk);

        // ---- the log, from journal block 1
        var log = new List<byte[]>();
        byte[] Header(int type, uint seq)
        {
            byte[] h = new byte[bs];
            Be32(h, 0, JbdMagic);
            Be32(h, 4, (uint)type);
            Be32(h, 8, seq);
            return h;
        }
        void Blocks(uint seq, IReadOnlyList<(long Block, byte[] Data)> list)
        {
            int perDescriptor = (bs - 12 - 16) / 8;
            for (int at = 0; at < list.Count; at += perDescriptor)
            {
                byte[] d = Header(1, seq);
                var payload = new List<byte[]>();
                int p = 12, n = Math.Min(perDescriptor, list.Count - at);
                for (int i = 0; i < n; i++)
                {
                    byte[] data = (byte[])list[at + i].Data.Clone();
                    int flags = i > 0 ? 2 : 0;                          // SAME_UUID after the first tag
                    if (i == n - 1) flags |= 8;                         // LAST_TAG
                    if (Be32(data, 0) == JbdMagic)
                    {
                        flags |= 1;                                     // ESCAPE
                        Be32(data, 0, 0);
                    }
                    Be32(d, p, (uint)list[at + i].Block);
                    BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(p + 6), (ushort)flags);
                    p += i == 0 ? 8 + 16 : 8;
                    payload.Add(data);
                }
                log.Add(d);
                log.AddRange(payload);
            }
        }
        void Commit(uint seq) => log.Add(Header(2, seq));

        Blocks(1, changed.Select(c => (c, b.AsSpan((int)(c * bs), bs).ToArray())).ToList());
        Commit(1);
        Blocks(2, new[] { (x, junk) });
        Commit(2);
        byte[] revoke = Header(5, 3);
        Be32(revoke, 12, 16 + 4);
        Be32(revoke, 16, (uint)x);
        log.Add(revoke);
        Commit(3);
        Blocks(4, new[] { (y, junk) });                                 // never committed

        Check(log.Count < maxLen - 1, "the test log does not fit in the journal");
        byte[] img = (byte[])a.Clone();
        for (int i = 0; i < log.Count; i++)
        {
            long at = (jsb + 1 + i) * bs;
            Check(img.AsSpan((int)at, bs).IndexOfAnyExcept((byte)0) < 0, "the journal is not one contiguous run");
            log[i].CopyTo(img, at);
        }
        Be32(img, jsb * bs + 0x18, 1);                                  // first transaction ID
        Be32(img, jsb * bs + 0x1C, 1);                                  // log start
        uint incompat = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(1024 + 0x60));
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(1024 + 0x60), incompat | 0x4);                            // needs_recovery

        string pathA = Path.Combine(_dir, "jr_old.img"), pathB = Path.Combine(_dir, "jr_new.img"), path = Path.Combine(_dir, "jr_dirty.img");
        File.WriteAllBytes(pathA, a);
        File.WriteAllBytes(pathB, b);
        File.WriteAllBytes(path, img);
        string oldDump = DumpFile(pathA, out _, out _);
        string newDump = DumpFile(pathB, out _, out _);
        Check(oldDump != newDump, "the edits are not visible in the dump");

        string got = DumpFile(path, out IReadOnlyList<string> warnings, out ExtInfo info);
        Same(newDump, got, "replayed image");
        Check(info.JournalReplayed && !info.NeedsRecovery && info.ReplayedTransactions == 3,
            $"replay state: replayed={info.JournalReplayed} needsRecovery={info.NeedsRecovery} transactions={info.ReplayedTransactions}");
        Check(!info.FeatureNames.Contains("needs_recovery"), "needs_recovery is still reported after the replay");
        Check(warnings.Any(w => w.Contains("replayed while reading")), "no message about the replay: " + string.Join(" | ", warnings));
        Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(img), "opening the image changed the file");

        // Saving writes the recovered state as a clean image.
        string saved = Path.Combine(_dir, "jr_saved.img");
        using (ImageDocument doc = ImageDocument.Open(path, _dir, searchEmbedded: false))
        {
            ImageDocument.SaveResult r = doc.Save(saved, doc.CreateSaveOptions());
            r.Document.Dispose();
        }
        Same(newDump, DumpFile(saved, out IReadOnlyList<string> savedWarnings, out ExtInfo savedInfo), "saved image");
        Check(savedWarnings.Count == 0 && !savedInfo.NeedsRecovery && !savedInfo.JournalReplayed, "the saved image is not clean");

        // A journal that cannot be read leaves the image as it is outside the journal, with a warning.
        byte[] bad = (byte[])img.Clone();
        Be32(bad, jsb * bs + 0x28, 0x80000000);                         // unknown incompat feature of the journal
        File.WriteAllBytes(path, bad);
        Same(oldDump, DumpFile(path, out IReadOnlyList<string> badWarnings, out ExtInfo badInfo), "image with an unreadable journal");
        Check(badInfo.NeedsRecovery && badWarnings.Any(w => w.Contains("could not be replayed")), "no warning about the unreadable journal");

        foreach (string f in new[] { pathA, pathB, path, saved }) File.Delete(f);
    }
}
