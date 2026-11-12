using System.Diagnostics;
using System.Text;
using FsForge.Core;
using FsForge.Core.Compression;
using FsForge.Core.Ext;
using FsForge.Core.Host;
using FsForge.Core.Images;
using FsForge.Core.SquashFs;
using FsForge.Core.Tar;

namespace FsForge.Cli;

/// <summary>
/// Command line front end of the FsForge core. It doubles as the test driver: "dump" prints a
/// canonical listing that can be compared with a listing of a mounted image.
/// </summary>
internal static class Program
{
    private const string Usage = """
        fsforge - SquashFS / ext2-3-4 / tar image tool

          fsforge-cli info    <image>                       format, partitions of a disk image, statistics
          fsforge-cli ls      <image> [path]
          fsforge-cli dump    <image>                       canonical listing with SHA-256 of every file
          fsforge-cli convert <image> <output> [options]    rewrite or convert an image
          fsforge-cli edit    <image> <output> <changes> [options]   change files and save (output may be the image itself)
                              changes: add:<dir in image>:<host file or folder>  rm:<path>  mkdir:<path>  chmod:<octal>:<path>
          fsforge-cli extract <image> <folder> [path]       unpack to a host folder (writes .fsforge-meta.json)
          fsforge-cli pack    <folder> <output> [options]   build an image from a host folder
          fsforge-cli selftest [folder]                     run the built-in tests

        A disk image with a partition table (SD card images: boot partition + root filesystem) is opened
        at its root filesystem partition; add part=<n> to any command to pick another one. "convert" writes
        the whole disk image again with only that partition replaced, unless the output name says otherwise
        (.squashfs, .ext4, .tar...) or container=0 is given. Output names ending in .gz, .xz or .zst are compressed.

        options (key=value):
          container=0|1                                 write the filesystem alone / the whole disk image
          format=squashfs|ext2|ext3|ext4|tar            default: by output extension, else the source format
          verify=none|structure|content                 default: structure
          squashfs: comp=gzip|xz|lz4|zstd block=<bytes> level=<n> effort=0..2 bcj=x86|arm|... nofrag=1 dedup=0 allroot=1
          ext:      bs=1024|2048|4096 size=<MiB> free=<MiB> isize=128|256 inodes=<n> journal=0|1 label=<text> reserved=<percent>
          tar:      comp=none|gzip|xz|zstd dialect=pax|gnu prefix=<text> level=<n> root=0|1
        """;

    /// <summary>Partition requested with part=N on the command line; 0 when the image should decide.</summary>
    private static int _partition;

    private static ImageDocument OpenImage(string path, bool searchEmbedded = true) =>
        ImageDocument.Open(path, searchEmbedded: searchEmbedded, partition: _partition);

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        try
        {
            // part=N applies to the image being opened, whatever the command.
            if (Array.Find(args, x => x.StartsWith("part=", StringComparison.OrdinalIgnoreCase)) is { } part && args[0] != "pack")
            {
                if (!int.TryParse(part[5..], out _partition) || _partition <= 0) throw new FsException($"Invalid option '{part}'.");
                args = args.Where(x => !ReferenceEquals(x, part)).ToArray();
            }
            switch (args[0])
            {
                case "info": return Info(args);
                case "ls": return List(args);
                case "dump" or "sqdump" or "extdump" or "tardump": return DumpCmd(args);
                case "convert": return Convert(args);
                case "edit": return Edit(args);
                case "extract": return Extract(args);
                case "pack": return Pack(args);
                case "selftest": return SelfTest.Run(args.Length > 1 ? args[1] : null);
                case "xztest": return XzTest(args);
                // Positional forms kept for the verification scripts.
                case "sqrepack": return Convert(Positional(args, "squashfs", "comp", "block", "effort", "bcj"));
                case "extrepack": return Convert(Positional(args, null, "format", "bs", "size", "isize", "journal"));
                case "tarwrite": return Convert(Positional(args, "tar", "comp", "dialect", "prefix"));
                default:
                    Console.Error.WriteLine("unknown command");
                    return 2;
            }
        }
        catch (FsException ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    private static string[] Positional(string[] a, string? format, params string[] keys)
    {
        var list = new List<string> { "convert", a[1], a[2] };
        if (format is not null) list.Add("format=" + format);
        for (int i = 0; i < keys.Length && i + 3 < a.Length; i++) list.Add(keys[i] + "=" + a[i + 3]);
        list.Add("verify=none");
        return list.ToArray();
    }

    private static Dictionary<string, string> Options(string[] a, int from)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = from; i < a.Length; i++)
        {
            int eq = a[i].IndexOf('=');
            if (eq <= 0) throw new FsException($"Invalid option '{a[i]}' (expected key=value).");
            d[a[i][..eq]] = a[i][(eq + 1)..];
        }
        return d;
    }

    // ------------------------------------------------------------- commands

    private static int Info(string[] a)
    {
        using ImageDocument doc = OpenImage(a[1]);
        TreeStats st = doc.Tree.GetStats();
        Console.WriteLine($"File:        {doc.FilePath}");
        Console.WriteLine($"Format:      {doc.FormatName}");
        Console.WriteLine($"File size:   {doc.FileSize}");
        if (doc.SquashFs is { } s)
        {
            Console.WriteLine($"Compression: {s.Compression}, block {s.BlockSize}");
            Console.WriteLine($"Inodes:      {s.InodeCount}, fragments {s.FragmentCount}, ids {s.IdCount}");
            Console.WriteLine($"Flags:       0x{s.Flags:X4}{(s.Exportable ? " exportable" : "")}{(s.NoFragments ? " no-fragments" : "")}{(s.Deduplicated ? " duplicates" : "")}");
        }
        if (doc.Ext is { } e)
        {
            Console.WriteLine($"Blocks:      {e.BlocksCount} x {e.BlockSize} ({e.FreeBlocks} free)");
            Console.WriteLine($"Inodes:      {e.InodesCount} x {e.InodeSize} ({e.FreeInodes} free)");
            Console.WriteLine($"Journal:     {(e.HasJournal ? e.JournalBytes + " bytes" : "none")}");
            Console.WriteLine($"Label/UUID:  '{e.Label}' {e.Uuid}");
            Console.WriteLine($"Features:    {string.Join(' ', e.FeatureNames)}");
        }
        if (doc.Tar is { } t)
            Console.WriteLine($"Archive:     {t.Entries} entries, compression {t.Compression}, prefix '{t.PathPrefix}'{(t.HasPaxHeaders ? ", pax" : "")}{(t.HasGnuExtensions ? ", gnu" : "")}");
        if (doc.OuterCompression is { } outer) Console.WriteLine($"Compressed:  {outer.ToString().ToLowerInvariant()}");
        if (doc.Container is { } c)
        {
            Console.WriteLine($"Disk image:  {c.DiskLength} bytes, {c.Table.Scheme.ToString().ToUpperInvariant()} partition table");
            foreach (PartitionInfo p in c.Table.Partitions)
                Console.WriteLine($"  {(ReferenceEquals(p, c.Partition) ? '*' : ' ')} {p}  [offset 0x{p.Offset:X}{(p.StoredLength < p.Length ? $", {p.StoredLength} bytes in the file" : "")}]");
        }
        if (doc.EmbeddedOffset > 0) Console.WriteLine($"Offset:      0x{doc.EmbeddedOffset:X}");
        Console.WriteLine($"Content:     {st.Files} files, {st.Directories} directories, {st.Symlinks} symlinks, {st.Specials} special, {st.DataBytes} bytes");
        foreach (string w in doc.Warnings) Console.Error.WriteLine("warning: " + w);
        return 0;
    }

    private static int List(string[] a)
    {
        using ImageDocument doc = OpenImage(a[1]);
        FsNode dir = doc.Tree.Resolve(a.Length > 2 ? a[2] : "/") ?? throw new FsException("No such path in the image.");
        IEnumerable<FsNode> items = dir.IsDirectory ? dir.SortedChildren() : new[] { dir };
        foreach (FsNode n in items)
        {
            FsInode i = n.Inode;
            string size = i.Type is FsNodeType.CharDevice or FsNodeType.BlockDevice ? $"{i.DevMajor},{i.DevMinor}" : i.Size.ToString();
            string time = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(i.MTime, -62135596800, 253402300799)).UtcDateTime.ToString("yyyy-MM-dd HH:mm");
            string link = i.Type == FsNodeType.Symlink ? " -> " + i.LinkTarget : "";
            Console.WriteLine($"{UnixMode.Format(i.Type, i.Mode)} {i.LinkCount,3} {i.Uid,6} {i.Gid,6} {size,12} {time} {n.Name}{link}");
        }
        return 0;
    }

    private static int DumpCmd(string[] a)
    {
        using ImageDocument doc = OpenImage(a[1], searchEmbedded: a[0] == "dump");
        var w = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), 1 << 16);
        Dump(doc.Tree, w);
        w.Flush();
        foreach (string s in doc.Warnings) Console.Error.WriteLine("warning: " + s);
        if (doc.Ext is { } e)
            Console.Error.WriteLine($"{e.Kind} bs={e.BlockSize} blocks={e.BlocksCount} inodes={e.InodesCount} isize={e.InodeSize} journal={e.JournalBytes} features={string.Join(',', e.FeatureNames)}");
        if (doc.Tar is { } t)
            Console.Error.WriteLine($"tar comp={t.Compression} entries={t.Entries} pax={t.HasPaxHeaders} gnu={t.HasGnuExtensions} prefix='{t.PathPrefix}' root={t.HasRootEntry}");
        return 0;
    }

    internal static SaveOptions BuildOptions(ImageDocument doc, string output, Dictionary<string, string> o)
    {
        ImageFormat? format = null;
        ExtKind? kind = null;
        string? f = o.GetValueOrDefault("format");
        // A name that spells out a filesystem asks for that filesystem alone, not for a disk image around it.
        bool namedFilesystem = false;
        if (f is null)
        {
            string name = Path.GetFileName(output).ToLowerInvariant();
            if (ArchiveCodec.CompressionFromFileName(name) is not null) f = "tar";
            else
            {
                foreach (string z in new[] { ".gz", ".xz", ".zst", ".zstd" })
                    if (name.EndsWith(z)) name = name[..^z.Length];
                if (name.EndsWith(".sqfs") || name.EndsWith(".squashfs")) f = "squashfs";
                else if (name.EndsWith(".ext4") || name.EndsWith(".ext3") || name.EndsWith(".ext2")) f = name[^4..];
                namedFilesystem = f is not null;
            }
        }
        switch (f?.ToLowerInvariant())
        {
            case null: break;
            case "squashfs" or "sqfs": format = ImageFormat.SquashFs; break;
            case "tar": format = ImageFormat.Tar; break;
            case "ext2": format = ImageFormat.Ext; kind = ExtKind.Ext2; break;
            case "ext3": format = ImageFormat.Ext; kind = ExtKind.Ext3; break;
            case "ext4" or "ext": format = ImageFormat.Ext; kind = ExtKind.Ext4; break;
            default: throw new FsException($"Unknown format '{f}'.");
        }

        SaveOptions s = doc.CreateSaveOptions(format);
        s.KeepContainer = o.TryGetValue("container", out string? keep) ? keep != "0" : !namedFilesystem;
        if (format == ImageFormat.Ext && doc.Format != ImageFormat.Ext) s.Ext = new ExtOptions();
        if (kind is not null) s.Ext.Kind = kind.Value;
        if (s.Format == ImageFormat.Tar && doc.Format != ImageFormat.Tar && ArchiveCodec.CompressionFromFileName(output) is { } byName)
            s.Tar.Compression = byName;
        if (o.TryGetValue("verify", out string? v)) s.Verify = Enum.Parse<VerifyLevel>(v, true);

        foreach ((string key, string value) in o)
        {
            switch (key.ToLowerInvariant())
            {
                case "format" or "verify" or "container": break;
                case "comp":
                    if (s.Format == ImageFormat.Tar) s.Tar.Compression = Enum.Parse<ArchiveCompression>(value, true);
                    else s.SquashFs.Compression = Enum.Parse<SquashCompression>(value, true);
                    break;
                case "block": s.SquashFs.BlockSize = int.Parse(value); break;
                case "level":
                    s.SquashFs.GzipLevel = s.Tar.GzipLevel = Math.Clamp(int.Parse(value), 1, 9);
                    s.SquashFs.ZstdLevel = s.Tar.ZstdLevel = int.Parse(value);
                    break;
                case "effort": s.SquashFs.XzEffort = s.Tar.XzEffort = int.Parse(value); break;
                case "bcj": s.SquashFs.XzFilters.Add(Enum.Parse<BcjFilter>(value, true)); break;
                case "dict": s.SquashFs.XzDictionarySize = int.Parse(value); break;
                case "nofrag": s.SquashFs.NoFragments = value != "0"; break;
                case "dedup": s.SquashFs.Deduplicate = value != "0"; break;
                case "allroot": s.SquashFs.AllRoot = s.Ext.AllRoot = s.Tar.AllRoot = value != "0"; break;
                case "bs": s.Ext.BlockSize = int.Parse(value); break;
                case "size": s.Ext.SizeBytes = long.Parse(value) << 20; break;
                case "free": s.Ext.ExtraFreeBytes = long.Parse(value) << 20; break;
                case "isize": s.Ext.InodeSize = int.Parse(value); break;
                case "inodes": s.Ext.InodeCount = uint.Parse(value); break;
                case "journal": s.Ext.Journal = value != "0"; break;
                case "label": s.Ext.Label = value; break;
                case "reserved": s.Ext.ReservedPercent = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "dialect": s.Tar.Dialect = Enum.Parse<TarDialect>(value, true); break;
                case "prefix": s.Tar.PathPrefix = value; break;
                case "root": s.Tar.IncludeRootEntry = value != "0"; break;
                default: throw new FsException($"Unknown option '{key}'.");
            }
        }
        // A converted ext image gets its size from the content unless one was requested.
        // (Inside a disk image the size is the partition's business: see ImageDocument.Save.)
        if (s.Format == ImageFormat.Ext && !o.ContainsKey("size") && (doc.Format != ImageFormat.Ext || o.ContainsKey("bs") || o.ContainsKey("free")))
        {
            s.Ext.SizeBytes = 0;
            if (!o.ContainsKey("inodes")) s.Ext.InodeCount = 0;
            s.Ext.JournalBytes = 0;
        }
        return s;
    }

    private static int Convert(string[] a)
    {
        if (a.Length < 3) throw new FsException("usage: fsforge-cli convert <image> <output> [options]");
        ImageDocument doc = OpenImage(a[1]);
        try
        {
            foreach (string w in doc.Warnings) Console.Error.WriteLine("warning: " + w);
            SaveOptions s = BuildOptions(doc, a[2], Options(a, 3));
            var sw = Stopwatch.StartNew();
            ImageDocument.SaveResult r = doc.Save(a[2], s);
            doc = r.Document;
            foreach (string w in r.Warnings) Console.Error.WriteLine("warning: " + w);
            Console.Error.WriteLine($"written {r.BytesWritten} bytes in {sw.ElapsedMilliseconds} ms");
        }
        finally
        {
            doc.Dispose();
        }
        return 0;
    }

    private static readonly string[] EditVerbs = { "add:", "rm:", "mkdir:", "chmod:" };

    private static int Edit(string[] a)
    {
        if (a.Length < 4) throw new FsException("usage: fsforge-cli edit <image> <output> <changes> [options]");
        string[] changes = a.Skip(3).Where(x => EditVerbs.Any(v => x.StartsWith(v, StringComparison.Ordinal))).ToArray();
        string[] rest = a.Take(3).Concat(a.Skip(3).Except(changes)).ToArray();
        ImageDocument doc = OpenImage(a[1]);
        try
        {
            foreach (string w in doc.Warnings) Console.Error.WriteLine("warning: " + w);
            FsTree tree = doc.Tree;
            FsNode Need(string path) => tree.Resolve(path) ?? throw new FsException($"No '{path}' in the image.");
            foreach (string change in changes)
            {
                string verb = change[..change.IndexOf(':')], arg = change[(change.IndexOf(':') + 1)..];
                int sep = arg.IndexOf(':');
                switch (verb)
                {
                    case "add":
                        if (sep <= 0) throw new FsException($"Invalid change '{change}' (expected add:<dir in image>:<host path>).");
                        ImportResult ir = HostImporter.Import(tree, Need(arg[..sep]), new[] { Path.GetFullPath(arg[(sep + 1)..]) }, new ImportOptions());
                        foreach (string w in ir.Warnings) Console.Error.WriteLine("warning: " + w);
                        break;
                    case "rm":
                        tree.Remove(Need(arg));
                        break;
                    case "mkdir":
                        int slash = arg.TrimEnd('/').LastIndexOf('/');
                        if (slash < 0) throw new FsException($"Invalid change '{change}' (expected an absolute path).");
                        tree.CreateDirectory(Need(slash == 0 ? "/" : arg[..slash]), arg.TrimEnd('/')[(slash + 1)..], 0x1ED, 0, 0, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                        break;
                    case "chmod":
                        if (sep <= 0) throw new FsException($"Invalid change '{change}' (expected chmod:<octal>:<path>).");
                        tree.SetMode(new[] { Need(arg[(sep + 1)..]) }, System.Convert.ToInt32(arg[..sep], 8), recursive: false, targets: FsTree.Targets.All);
                        break;
                }
            }
            SaveOptions s = BuildOptions(doc, a[2], Options(rest, 3));
            var sw = Stopwatch.StartNew();
            ImageDocument.SaveResult r = doc.Save(a[2], s);
            doc = r.Document;
            foreach (string w in r.Warnings) Console.Error.WriteLine("warning: " + w);
            Console.Error.WriteLine($"{changes.Length} changes; written {r.BytesWritten} bytes in {sw.ElapsedMilliseconds} ms");
        }
        finally
        {
            doc.Dispose();
        }
        return 0;
    }

    private static int Extract(string[] a)
    {
        if (a.Length < 3) throw new FsException("usage: fsforge-cli extract <image> <folder> [path]");
        using ImageDocument doc = OpenImage(a[1]);
        FsNode node = doc.Tree.Resolve(a.Length > 3 ? a[3] : "/") ?? throw new FsException("No such path in the image.");
        ExtractResult r = HostExtractor.Extract(new[] { node }, a[2], new ExtractOptions());
        foreach (string w in r.Warnings) Console.Error.WriteLine("warning: " + w);
        Console.Error.WriteLine($"extracted {r.Files} files, {r.Directories} directories, {r.Symlinks} symlinks, {r.Specials} special files, {r.Bytes} bytes; {r.Renamed} renamed, {r.Skipped} skipped");
        return 0;
    }

    private static int Pack(string[] a)
    {
        if (a.Length < 3) throw new FsException("usage: fsforge-cli pack <folder> <output> [options]");
        Dictionary<string, string> o = Options(a, 3);
        ImageDocument doc = ImageDocument.CreateNew(ImageFormat.SquashFs);
        try
        {
            ImportResult ir = HostImporter.ImportContents(doc.Tree, doc.Tree.Root, a[1], new ImportOptions());
            foreach (string w in ir.Warnings) Console.Error.WriteLine("warning: " + w);
            SaveOptions s = BuildOptions(doc, a[2], o);
            var sw = Stopwatch.StartNew();
            ImageDocument.SaveResult r = doc.Save(a[2], s);
            doc = r.Document;
            foreach (string w in r.Warnings) Console.Error.WriteLine("warning: " + w);
            Console.Error.WriteLine($"packed {ir.Files} files, {ir.Directories} directories; written {r.BytesWritten} bytes in {sw.ElapsedMilliseconds} ms");
        }
        finally
        {
            doc.Dispose();
        }
        return 0;
    }

    // xztest <file> <out.xz> [effort] [blocksize] [filter]
    private static int XzTest(string[] a)
    {
        byte[] data = File.ReadAllBytes(a[1]);
        int effort = a.Length > 3 ? int.Parse(a[3]) : 2;
        int block = a.Length > 4 ? int.Parse(a[4]) : data.Length;
        var filters = new List<BcjFilter>();
        if (a.Length > 5) filters.Add(Enum.Parse<BcjFilter>(a[5], true));
        var codec = new XzCodec(Math.Max(block, 4096), filters, LzmaEncoderSettings.FromEffort(effort));
        using var o = File.Create(a[2]);
        var sw = Stopwatch.StartNew();
        long total = 0;
        byte[] dst = new byte[block + 1024];
        byte[] back = new byte[block];
        for (int off = 0; off < data.Length; off += block)
        {
            int len = Math.Min(block, data.Length - off);
            int n = codec.Compress(data.AsSpan(off, len), dst.AsSpan(0, len + 512));
            if (n < 0) { Console.Error.WriteLine($"block at {off}: incompressible"); continue; }
            int m = codec.Decompress(dst.AsSpan(0, n), back);
            if (m != len || !back.AsSpan(0, m).SequenceEqual(data.AsSpan(off, len))) { Console.Error.WriteLine("ROUNDTRIP MISMATCH"); return 1; }
            o.Write(dst, 0, n);
            total += n;
        }
        Console.WriteLine($"{data.Length} -> {total} ({100.0 * total / Math.Max(1, data.Length):F2}%) in {sw.ElapsedMilliseconds} ms, {data.Length / 1048576.0 / sw.Elapsed.TotalSeconds:F2} MiB/s");
        return 0;
    }

    // ---------------------------------------------------------------- dump

    /// <summary>
    /// Canonical listing: path, type, mode, uid, gid, mtime, size:sha256 or link target or device,
    /// first hard link name, extended attributes. One line per entry, sorted by path bytes.
    /// </summary>
    internal static void Dump(FsTree tree, TextWriter w, bool hash = true)
    {
        var nodes = new List<(byte[] Path, FsNode Node)>();
        foreach (FsNode n in tree.Walk()) nodes.Add((PosixName.Encode(n.Path), n));
        nodes.Sort((x, y) => x.Path.AsSpan().SequenceCompareTo(y.Path));
        var first = new Dictionary<FsInode, string>(ReferenceEqualityComparer.Instance);
        var counts = new Dictionary<FsInode, int>(ReferenceEqualityComparer.Instance);
        foreach (var (_, n) in nodes) counts[n.Inode] = counts.GetValueOrDefault(n.Inode) + 1;
        var hashes = new Dictionary<FsInode, string>(ReferenceEqualityComparer.Instance);
        foreach (var (path, n) in nodes)
        {
            FsInode i = n.Inode;
            string p = Escape(path);
            string hl = "-";
            if (!n.IsDirectory && counts[i] > 1)
            {
                if (!first.TryGetValue(i, out string? f)) first[i] = f = p;
                hl = f;
            }
            string extra = i.Type switch
            {
                FsNodeType.File => i.Size + ":" + (hash ? (hashes.TryGetValue(i, out string? h) ? h : hashes[i] = TreeComparer.Hash(i)) : "-"),
                FsNodeType.Symlink => Escape(PosixName.Encode(i.LinkTarget ?? "")),
                FsNodeType.CharDevice or FsNodeType.BlockDevice => $"{i.DevMajor}:{i.DevMinor}",
                _ => "-",
            };
            string mode = i.Type == FsNodeType.Symlink ? "0777" : UnixMode.ToOctal(i.Mode);
            var xa = new StringBuilder();
            if (i.XAttrs is not null)
                foreach (var (k, v) in i.XAttrs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    xa.Append(k).Append('=').Append(System.Convert.ToHexString(v).ToLowerInvariant()).Append(';');
            w.Write($"{p}\t{TypeChar(i.Type)}\t{mode}\t{i.Uid}\t{i.Gid}\t{i.MTime}\t{extra}\t{hl}\t{(xa.Length == 0 ? "-" : xa.ToString())}\n");
        }
    }

    internal static string DumpToString(FsTree tree)
    {
        var sw = new StringWriter();
        Dump(tree, sw);
        return sw.ToString();
    }

    private static char TypeChar(FsNodeType t) => t switch
    {
        FsNodeType.Directory => 'd', FsNodeType.File => 'f', FsNodeType.Symlink => 'l', FsNodeType.CharDevice => 'c',
        FsNodeType.BlockDevice => 'b', FsNodeType.Fifo => 'p', _ => 's',
    };

    private static string Escape(byte[] b)
    {
        var sb = new StringBuilder(b.Length);
        foreach (byte c in b)
        {
            if (c > 0x20 && c < 0x7F && c != '\\') sb.Append((char)c);
            else sb.Append($"\\x{c:x2}");
        }
        return sb.ToString();
    }
}
