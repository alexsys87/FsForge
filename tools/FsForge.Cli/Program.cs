using System.Diagnostics;
using System.Text;
using FsForge.Core;
using FsForge.Core.Compression;
using FsForge.Core.Cpio;
using FsForge.Core.Erofs;
using FsForge.Core.Ext;
using FsForge.Core.Host;
using FsForge.Core.Images;
using FsForge.Core.SquashFs;
using FsForge.Core.Tar;
using FsForge.Core.Fat;
using FsForge.Core.Jffs2;
using FsForge.Core.Ubifs;

namespace FsForge.Cli;

/// <summary>
/// Command line front end of the FsForge core. It doubles as the test driver: "dump" prints a
/// canonical listing that can be compared with a listing of a mounted image.
/// </summary>
internal static class Program
{
    private const string Usage = """
        fsforge - SquashFS / EROFS / CramFS / romfs / F2FS / ext2-3-4 / UBIFS / JFFS2 / FAT / tar / cpio image tool

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
        (.squashfs, .erofs, .cramfs, .romfs, .ext4, .tar...) or container=0 is given. Output names ending in .gz, .xz, .zst
        or .bz2 are compressed; .zip and .7z make an archive holding the image (as FlashTool writes them).

        Images packed with gzip, xz, zstd or bzip2 (also several streams in a row: pigz, pbzip2, pzstd) and images
        inside zip or 7z archives are unpacked to a temporary file first. An archive with several files is opened
        at its image; add entry=<name> to pick another file. Saving into the same archive keeps the image's name
        and carries the other files over. A SHA-256 checksum file next to the saved file (name.sha256,
        SHA256SUMS) is updated and a block map (name.bmap) rebuilt.

        Android images are opened at what they hold: super.img (also sparse) at its largest logical partition,
        boot.img / init_boot.img / vendor_boot.img at the ramdisk (a cpio archive); add vol=<name> to pick another
        partition (system_a, vendor_a...) or vendor ramdisk. "convert" writes the whole Android image again with only
        that part replaced, unless the output name or container=0 says otherwise.

        TP-Link firmware images (the classic header of mktplinkfw, versions 1 to 3, and safeloader factory images)
        are opened at their root filesystem (vol=rootfs or file-system); the MD5 is checked. They are not rebuilt
        yet: the filesystem is saved alone, under another name.

        Broadcom TRX images (.trx) and Netgear .chk images holding one are opened at their root filesystem
        (vol=rootfs, kernel, loader); the CRC-32 is checked. Saving writes the image again as OpenWrt's trx lays it
        out (new length, offsets and CRC-32) and remakes the CHK checksums when the old ones could be verified.

        U-Boot FIT images (.itb, image.ub) are opened the same way, at the image that holds a filesystem or
        ramdisk (vol=<image node name>, e.g. vol=ramdisk-1); hashes are checked. Saving writes the FIT image
        again with that image replaced: its hashes, size and the offsets of external data are recalculated.
        Signed FIT images are only read: their filesystem is saved alone, under another name.

        U-Boot uImage files (uImage, uInitrd, OpenWrt sysupgrade images with the root filesystem behind the
        kernel) are opened at the filesystem or ramdisk they hold; add vol=<name or number> to pick another part
        (rootfs, ramdisk, filesystem, or the number of the image as mkimage -l counts them; rootfs:<volume> for a
        UBI image behind the kernel). "edit" and "convert" write the whole firmware again with only that part
        replaced: sizes and CRC-32 checksums in the header are recalculated, and OpenWrt's 0xdeadc0de markers are put
        on the erase block boundaries behind the new root filesystem. container=0 or a filesystem name (.squashfs...)
        writes the filesystem alone.

        A UBI image (ubinize output, a flash dump without out-of-band data) is opened at the volume that holds
        the most data; add vol=<name or number> to any command to pick another one. "convert" writes the whole
        UBI image again with only that volume replaced, unless the output name or container=0 says otherwise.
        An output name ending in .ubifs makes a UBIFS image; one ending in .ubi wraps it into a new UBI image.

        options (key=value):
          container=0|1                                 write the filesystem alone / the whole disk image
          format=squashfs|erofs|cramfs|romfs|f2fs|ext2|ext3|ext4|ubifs|jffs2|fat|tar|cpio
                                                        default: by output extension, else the source format
          verify=none|structure|content                 default: structure
          threads=<n>                                   compression threads (SquashFS, EROFS, CramFS, tar.xz); default: one per processor
          squashfs: comp=gzip|xz|lz4|zstd block=<bytes> level=<n> effort=0..2 bcj=x86|arm|... nofrag=1 dedup=0 allroot=1
          erofs:    comp=none|lz4|lz4hc|lzma|deflate|zstd pcluster=<bytes> level=1..12 (lz4hc), 1..9 (deflate), 1..22 (zstd) effort=0..2 (lzma) dict=<bytes>
                    inline=0|1 dedup=0 xattrs=0|1 sharexattrs=0|1 allroot=1 label=<text> uuid=<uuid> time=<unix time>
          cramfs:   endian=little|big block=4096..65536 level=1..9 label=<text> edition=<n> pad=0|1 holes=0|1 allroot=1
          romfs:    label=<text> align=16..65536
          f2fs:     size=<MiB> free=<MiB> label=<text> uuid=<uuid> xattrs=0|1 allroot=1
          ext:      bs=1024|2048|4096 size=<MiB> free=<MiB> isize=128|256 inodes=<n> journal=0|1 label=<text> reserved=<percent>
          tar:      comp=none|gzip|xz|zstd|bzip2|lz4 dialect=pax|gnu prefix=<text> level=<n> root=0|1
          cpio:     comp=none|gzip|xz|zstd|bzip2|lz4 cpioformat=newc|crc prefix=|./ level=<n> effort=0..2 root=0|1 pad=0|1 allroot=1
          ubifs:    comp=lzo|zlib|zstd|none minio=<bytes> leb=<bytes> maxleb=<count> fanout=<n> jrn=<MiB> fixup=0|1 allroot=1
          jffs2:    comp=zlib|lzo|rtime|none endian=little|big erase=<bytes> cleanmarkers=0|1 cmsize=<bytes>
                    pagesize=<bytes> pad=0|1 xattrs=0|1 allroot=1
          fat:      fattype=12|16|32 cluster=<bytes> size=<MiB> free=<MiB> label=<text> serial=<hex>
                    (in a disk image the size is that of the partition)
          ubi:      ubi=0|1 peb=<bytes> minio=<bytes> subpage=<bytes> volname=<text> volid=<n> volsize=<MiB> autoresize=0|1
        """;

    /// <summary>Partition requested with part=N on the command line; 0 when the image should decide.</summary>
    private static int _partition;

    /// <summary>UBI volume requested with vol=NAME on the command line; null when the image should decide.</summary>
    private static string? _volume;

    /// <summary>File of a zip or 7z archive requested with entry=NAME on the command line; null when the archive should decide.</summary>
    private static string? _entry;

    private static ImageDocument OpenImage(string path, bool searchEmbedded = true) =>
        ImageDocument.Open(path, searchEmbedded: searchEmbedded, partition: _partition, volume: _volume, archiveEntry: _entry);

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
            if (Array.Find(args, x => x.StartsWith("vol=", StringComparison.OrdinalIgnoreCase)) is { } vol && args[0] != "pack")
            {
                if (vol.Length == 4) throw new FsException($"Invalid option '{vol}'.");
                _volume = vol[4..];
                args = args.Where(x => !ReferenceEquals(x, vol)).ToArray();
            }
            if (Array.Find(args, x => x.StartsWith("entry=", StringComparison.OrdinalIgnoreCase)) is { } entry && args[0] != "pack")
            {
                if (entry.Length == 6) throw new FsException($"Invalid option '{entry}'.");
                _entry = entry[6..];
                args = args.Where(x => !ReferenceEquals(x, entry)).ToArray();
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
                case "lz4test": return Lz4Test(args);
                case "blocktest": return BlockTest(args);
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
            // A failure the reader did not anticipate: its origin, for the one who looks into it.
            if (ex is FsUnexpectedException && Environment.GetEnvironmentVariable("FSFORGE_DEBUG") is not null)
                Console.Error.WriteLine(ex.InnerException);
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
        if (doc.Ubifs is { } u)
        {
            Console.WriteLine($"Blocks:      {u.LebCount} x {u.LebSize} (may grow to {u.MaxLebCount}), min. I/O unit {u.MinIoSize}");
            Console.WriteLine($"Space:       {u.UsedBytes} bytes used, {u.FreeBytes} free");
            Console.WriteLine($"Compression: {u.DefaultCompression.ToString().ToLowerInvariant()}, format version {u.FormatVersion}, key hash {u.KeyHash}");
            Console.WriteLine($"UUID:        {u.Uuid}");
            Console.WriteLine($"State:       {(u.ReplayedNodes > 0 ? $"not cleanly unmounted, {u.ReplayedNodes} journal nodes from {u.ReplayedBuds} erase blocks replayed" : u.Dirty ? "not cleanly unmounted, journal empty" : "clean")}" +
                              $"{(u.SpaceFixup ? ", space fixup pending" : "")}{(u.Authenticated ? ", authenticated" : "")}");
        }
        if (doc.Erofs is { } er)
        {
            Console.WriteLine($"Blocks:      {er.Blocks} x {er.BlockSize}, {er.InodeCount} inodes");
            Console.WriteLine($"Compression: {(er.Algorithms.Count == 0 ? "none" : string.Join(", ", er.Algorithms.Select(x => x.ToString().ToLowerInvariant())))}" +
                              $"{(er.Compression != ErofsCompression.None ? $", clusters of up to {er.PclusterSize} bytes" : "")}{(er.LzmaDictionarySize > 0 ? $", LZMA dictionary {er.LzmaDictionarySize}" : "")}");
            Console.WriteLine($"Label/UUID:  '{er.VolumeName}' {er.Uuid}, build time {er.BuildTime}");
            Console.WriteLine($"Features:    {string.Join(' ', er.FeatureNames)}");
        }
        if (doc.Fat is { } fat)
        {
            Console.WriteLine($"FAT:         {fat.TypeName}, {fat.Clusters} clusters of {fat.ClusterSize} bytes ({fat.FreeClusters} free), {fat.Fats} FATs of {fat.FatSectors} sectors");
            Console.WriteLine($"Layout:      {fat.TotalSectors} sectors of {fat.SectorSize}, {fat.ReservedSectors} reserved, {fat.RootEntries} root entries, hidden sectors {fat.HiddenSectors}");
            Console.WriteLine($"Label/ID:    '{fat.Label}' {fat.VolumeId >> 16:X4}-{fat.VolumeId & 0xFFFF:X4}, OEM '{fat.OemName}'{(fat.Dirty ? ", not cleanly unmounted" : "")}");
        }
        if (doc.Jffs2 is { } j)
        {
            Console.WriteLine($"Layout:      {(j.BigEndian ? "big" : "little")}-endian, erase block {j.EraseBlockSize}{(j.EraseBlockSizeKnown ? "" : " (estimated)")}, " +
                              $"{(j.Cleanmarkers ? $"clean markers of {j.CleanmarkerSize} bytes" : "no clean markers")}{(j.HasSummary ? ", summaries" : "")}");
            Console.WriteLine($"Nodes:       {j.Nodes} valid, {j.ObsoleteNodes} obsolete, {j.DamagedNodes} damaged; {j.UsedBytes} of {j.ImageBytes} bytes used");
            Console.WriteLine($"Compression: {string.Join(", ", j.CompressionCounts.OrderByDescending(p => p.Value).Select(p => $"{p.Key.ToString().ToLowerInvariant()} {p.Value}"))}");
        }
        if (doc.Cpio is { } cp)
            Console.WriteLine($"Archive:     {cp.Entries} entries, {cp.Header.ToString().ToLowerInvariant()} headers, compression {cp.Compression}, prefix '{cp.PathPrefix}'" +
                              $"{(cp.HasRootEntry ? ", root entry" : "")}{(cp.Parts > 1 ? $", {cp.Parts} archives one after another" : "")}{(cp.Truncated ? ", no trailer" : "")}");
        if (doc.Cramfs is { } cr)
            Console.WriteLine($"CramFS:      {(cr.BigEndian ? "big" : "little")}-endian, block {cr.BlockSize}, {cr.Files} inodes, {cr.Blocks} blocks, name '{cr.VolumeName}', edition {cr.Edition}" +
                              $"{(cr.Padded ? ", 512-byte boot pad" : "")}{(cr.Holes ? ", holes" : "")}{(cr.CrcOk is { } ok ? ok ? ", CRC ok" : ", CRC WRONG" : ", no CRC")}");
        if (doc.F2fs is { } f2)
        {
            Console.WriteLine($"F2FS:        version {f2.Version}, {f2.BlockCount} blocks of {f2.BlockSize}, {f2.MainSegments} main segments ({f2.FreeSegments} free), " +
                              $"{f2.ValidBlocks} of {f2.UserBlocks} user blocks used, {f2.ValidInodes} inodes");
            Console.WriteLine($"Label/UUID:  '{f2.VolumeName}' {f2.Uuid}, checkpoint {f2.CheckpointVersion}{(f2.NotCleanlyUnmounted ? ", not cleanly unmounted" : "")}");
            Console.WriteLine($"Features:    {string.Join(' ', f2.FeatureNames)}{(f2.CompressedFiles > 0 ? $"; {f2.CompressedFiles} compressed files" : "")}{(f2.EncryptedFiles > 0 ? $"; {f2.EncryptedFiles} encrypted files" : "")}");
        }
        if (doc.Romfs is { } ro)
            Console.WriteLine($"romfs:       name '{ro.VolumeName}', {ro.Size} bytes, data aligned to {ro.DataAlignment}, checksum {(ro.ChecksumOk ? "ok" : "WRONG")}");
        if (doc.Tar is { } t)
            Console.WriteLine($"Archive:     {t.Entries} entries, compression {t.Compression}, prefix '{t.PathPrefix}'{(t.HasPaxHeaders ? ", pax" : "")}{(t.HasGnuExtensions ? ", gnu" : "")}");
        if (doc.Archive is { } arc)
            Console.WriteLine($"Archive:     {ArchiveCodec.DisplayName(arc.Kind)} ({arc.Methods}), image '{arc.EntryName}'" +
                              (arc.OtherFiles.Count > 0 ? $", {arc.OtherFiles.Count} other: {string.Join(", ", arc.OtherFiles)}" : ""));
        else if (doc.OuterCompression is { } outer) Console.WriteLine($"Compressed:  {ArchiveCodec.DisplayName(outer)}");
        if (doc.Container is { } c)
        {
            Console.WriteLine($"Disk image:  {c.DiskLength} bytes, {c.Table.Scheme.ToString().ToUpperInvariant()} partition table");
            foreach (PartitionInfo p in c.Table.Partitions)
                Console.WriteLine($"  {(ReferenceEquals(p, c.Partition) ? '*' : ' ')} {p}  [offset 0x{p.Offset:X}{(p.StoredLength < p.Length ? $", {p.StoredLength} bytes in the file" : "")}]");
        }
        if (doc.Ubi is { } ubi)
        {
            Console.WriteLine($"UBI image:   {ubi.Image.PebCount} erase blocks of {ubi.Image.PebSize} bytes, logical block {ubi.Image.LebSize}");
            foreach (UbiVolumeInfo v in ubi.Image.Volumes)
                Console.WriteLine($"  {(ReferenceEquals(v, ubi.Volume) ? '*' : ' ')} {v}  [{v.StoredBlocks} blocks in the file]");
        }
        if (doc.Android is { } an)
        {
            Console.WriteLine($"Android:     {an.Image.Description}");
            if (an.Image is FsForge.Core.Android.SuperImage sup)
                Console.WriteLine($"             block device '{sup.BlockDevices[0].Name}' of {sup.DeviceSize} bytes, {sup.FreeBytes} free, metadata {sup.MetadataMaxSize} bytes x {sup.SlotCount} slot(s){(sup.Sparse ? ", sparse file" : "")}");
            if (an.Image is FsForge.Core.Android.BootImage boot)
                Console.WriteLine($"             cmdline '{boot.Cmdline}'{(boot.HasAvbFooter ? ", AVB footer" : "")}");
            foreach (FsForge.Core.Android.AndroidPart p in an.Image.Parts)
                Console.WriteLine($"  {(ReferenceEquals(p, an.Part) ? '*' : ' ')} {p}");
        }
        if (doc.Firmware is { } fw)
        {
            Console.WriteLine($"Firmware:    {fw.Image.Description}");
            if (fw.Image is FsForge.Core.Firmware.UImage ui)
                Console.WriteLine($"             {ui.OsName} {ui.ArchitectureName} {ui.TypeName}, compression {ui.CompressionName}, load 0x{ui.LoadAddress:X8}, entry 0x{ui.EntryPoint:X8}, " +
                                  $"{ui.DataSize} bytes of data, data CRC {(ui.DataCrcMatches ? "ok" : "wrong")}");
            if (fw.Image is FsForge.Core.Firmware.UImage { FwtoolChecksumsValid: { } fwOk } signed)
                Console.WriteLine($"             OpenWrt metadata (fwtool): checksums {(fwOk ? "ok" : "wrong")}{(signed.FwtoolSigned ? ", signed" : "")}");
            if (fw.Image is FsForge.Core.Firmware.TrxImage trx)
                Console.WriteLine($"             TRX version {trx.Version}, flags 0x{trx.Flags:X4}, {trx.Length} bytes at 0x{trx.TrxOffset:X}, CRC " +
                                  $"{(trx.CrcMatches ? $"ok ({trx.CrcScheme})" : "wrong")}{(trx.ChkBoard is { } board ? $", Netgear CHK board '{board}'" : "")}");
            if (fw.Image is FsForge.Core.Firmware.TpLinkImage tp)
                Console.WriteLine(tp.Safeloader
                    ? $"             safeloader, MD5 {(tp.Md5Matches is { } m ? m ? "ok" : "not matching" : "not checked")}, soft-version '{tp.FirmwareVersion}'" +
                      $"{(tp.SupportList.Length > 0 ? ", support-list: " + string.Join("; ", tp.SupportList.Split('\n').Where(l => l.Length > 0).Take(4)) : "")}"
                    : $"             header version {tp.HeaderVersion}, vendor '{tp.Vendor}', firmware '{tp.FirmwareVersion}', hardware 0x{tp.HardwareId:X8} rev {tp.HardwareRevision}, " +
                      $"MD5 {(tp.Md5Matches is { } m2 ? m2 ? "ok" : "not matching" : "not checked")}");
            if (fw.Image is FsForge.Core.Firmware.FitImage fit)
                Console.WriteLine($"             default configuration '{fit.DefaultConfiguration}', {fit.HashesChecked} hash(es) checked, {fit.HashesWrong} wrong" +
                                  $"{(fit.ExternalData ? ", external data" : "")}{(fit.Signed ? ", signed" : "")}");
            foreach (FsForge.Core.Firmware.FirmwarePart p in fw.Image.Parts)
                Console.WriteLine($"  {(ReferenceEquals(p, fw.Part) ? '*' : ' ')} {p}");
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
        if (doc.Firmware is { } fw)
            Console.Error.WriteLine($"firmware kind={fw.Image.Kind} part={fw.Part.Name} parts={string.Join(',', fw.Image.Parts.Select(p => p.Name + ":" + p.ContentName))}");
        if (doc.Android is { } an)
            Console.Error.WriteLine($"android kind={an.Image.Kind} part={an.Part.Name} parts={string.Join(',', an.Image.Parts.Select(p => p.Name + ":" + p.ContentName))}");
        if (doc.Ext is { } e)
            Console.Error.WriteLine($"{e.Kind} bs={e.BlockSize} blocks={e.BlocksCount} inodes={e.InodesCount} isize={e.InodeSize} journal={e.JournalBytes} features={string.Join(',', e.FeatureNames)}");
        if (doc.Ubifs is { } u)
            Console.Error.WriteLine($"ubifs leb={u.LebSize} lebs={u.LebCount} minio={u.MinIoSize} comp={u.DefaultCompression} version={u.FormatVersion} replayed={u.ReplayedNodes}");
        if (doc.Erofs is { } er)
            Console.Error.WriteLine($"erofs bs={er.BlockSize} blocks={er.Blocks} inodes={er.InodeCount} comp={er.Compression} pcluster={er.PclusterSize} " +
                                    $"algs={string.Join(',', er.Algorithms)} features={string.Join(',', er.FeatureNames)}");
        if (doc.Fat is { } fi)
            Console.Error.WriteLine($"fat type={fi.TypeName} cluster={fi.ClusterSize} clusters={fi.Clusters} size={fi.SizeBytes} label='{fi.Label}' hidden={fi.HiddenSectors}");
        if (doc.Jffs2 is { } j)
            Console.Error.WriteLine($"jffs2 endian={(j.BigEndian ? "big" : "little")} erase={j.EraseBlockSize} known={j.EraseBlockSizeKnown} cleanmarkers={j.Cleanmarkers} comp={j.MainCompression} nodes={j.Nodes}");
        if (doc.Cpio is { } cpi)
            Console.Error.WriteLine($"cpio header={cpi.Header} comp={cpi.Compression} entries={cpi.Entries} prefix='{cpi.PathPrefix}' root={cpi.HasRootEntry} parts={cpi.Parts}");
        if (doc.Cramfs is { } cri)
            Console.Error.WriteLine($"cramfs endian={(cri.BigEndian ? "big" : "little")} block={cri.BlockSize} files={cri.Files} blocks={cri.Blocks} name='{cri.VolumeName}' " +
                                    $"edition={cri.Edition} pad={cri.Padded} holes={cri.Holes} flags=0x{cri.Flags:X} crc={cri.CrcOk?.ToString() ?? "none"}");
        if (doc.F2fs is { } f2i)
            Console.Error.WriteLine($"f2fs bs={f2i.BlockSize} blocks={f2i.BlockCount} main={f2i.MainSegments} features={string.Join(',', f2i.FeatureNames)} " +
                                    $"compressed={f2i.CompressedFiles} encrypted={f2i.EncryptedFiles} cp={f2i.CheckpointVersion} label='{f2i.VolumeName}'");
        if (doc.Romfs is { } roi)
            Console.Error.WriteLine($"romfs name='{roi.VolumeName}' size={roi.Size} align={roi.DataAlignment} checksum={roi.ChecksumOk}");
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
            if (ArchiveCodec.CompressionFromFileName(name) is not null) (f, namedFilesystem) = ("tar", true);
            else if (CpioFormat.CompressionFromFileName(name) is not null) (f, namedFilesystem) = ("cpio", true);
            else
            {
                foreach (string z in new[] { ".gz", ".xz", ".zst", ".zstd" })
                    if (name.EndsWith(z)) name = name[..^z.Length];
                if (name.EndsWith(".sqfs") || name.EndsWith(".squashfs")) f = "squashfs";
                else if (name.EndsWith(".erofs")) f = "erofs";
                else if (name.EndsWith(".cramfs") || name.EndsWith(".cramfs.img")) f = "cramfs";
                else if (name.EndsWith(".romfs")) f = "romfs";
                else if (name.EndsWith(".f2fs")) f = "f2fs";
                else if (name.EndsWith(".ext4") || name.EndsWith(".ext3") || name.EndsWith(".ext2")) f = name[^4..];
                else if (name.EndsWith(".ubifs")) f = "ubifs";
                else if (name.EndsWith(".jffs2")) f = "jffs2";
                else if (name.EndsWith(".vfat") || name.EndsWith(".fat")) f = "fat";
                namedFilesystem = f is not null;
            }
        }
        switch (f?.ToLowerInvariant())
        {
            case null: break;
            case "ubifs": format = ImageFormat.Ubifs; break;
            case "jffs2": format = ImageFormat.Jffs2; break;
            case "fat" or "vfat" or "msdos": format = ImageFormat.Fat; break;
            case "squashfs" or "sqfs": format = ImageFormat.SquashFs; break;
            case "erofs": format = ImageFormat.Erofs; break;
            case "cramfs": format = ImageFormat.Cramfs; break;
            case "romfs": format = ImageFormat.Romfs; break;
            case "f2fs": format = ImageFormat.F2fs; break;
            case "tar": format = ImageFormat.Tar; break;
            case "cpio": format = ImageFormat.Cpio; break;
            case "ext2": format = ImageFormat.Ext; kind = ExtKind.Ext2; break;
            case "ext3": format = ImageFormat.Ext; kind = ExtKind.Ext3; break;
            case "ext4" or "ext": format = ImageFormat.Ext; kind = ExtKind.Ext4; break;
            default: throw new FsException($"Unknown format '{f}'.");
        }

        SaveOptions s = doc.CreateSaveOptions(format);
        s.KeepContainer = o.TryGetValue("container", out string? keep) ? keep != "0" : !namedFilesystem;
        if (format == ImageFormat.Ext && doc.Format != ImageFormat.Ext) s.Ext = new ExtOptions();
        // A name ending in .ubi asks for a UBI image: the one the source is, or a new one around UBIFS.
        string plainName = Path.GetFileName(output).ToLowerInvariant();
        foreach (string z in new[] { ".gz", ".xz", ".zst", ".zstd" })
            if (plainName.EndsWith(z)) plainName = plainName[..^z.Length];
        if (o.TryGetValue("ubi", out string? wrap)) s.NewUbi = wrap != "0";
        else if (plainName.EndsWith(".ubi") && doc.Ubi is null && doc.Container is null)
        {
            s.NewUbi = true;
            if (f is null) s.Format = ImageFormat.Ubifs;
        }
        if (s.NewUbi && doc.Ubi is not null && !o.ContainsKey("container")) s.KeepContainer = false;
        if (s.NewUbi && s.Format is ImageFormat.Ext or ImageFormat.Tar or ImageFormat.Cpio or ImageFormat.Cramfs or ImageFormat.Romfs)
            throw new FsException("Only UBIFS and SquashFS can be wrapped into a UBI image; add format=ubifs or format=squashfs.");
        if (kind is not null) s.Ext.Kind = kind.Value;
        if (s.Format == ImageFormat.Tar && doc.Format != ImageFormat.Tar && ArchiveCodec.CompressionFromFileName(output) is { } byName)
            s.Tar.Compression = byName;
        if (s.Format == ImageFormat.Cpio && doc.Format != ImageFormat.Cpio && CpioFormat.CompressionFromFileName(output) is { } cpioByName)
            s.Cpio.Compression = cpioByName;
        if (o.TryGetValue("verify", out string? v)) s.Verify = Enum.Parse<VerifyLevel>(v, true);

        foreach ((string key, string value) in o)
        {
            switch (key.ToLowerInvariant())
            {
                case "format" or "verify" or "container" or "ubi": break;
                case "threads": s.SquashFs.Threads = s.Tar.Threads = s.Erofs.Threads = s.Cpio.Threads = s.Cramfs.Threads = int.Parse(value); break;
                case "comp":
                    if (s.Format == ImageFormat.Tar) s.Tar.Compression = Enum.Parse<ArchiveCompression>(value, true);
                    else if (s.Format == ImageFormat.Cpio) s.Cpio.Compression = Enum.Parse<ArchiveCompression>(value, true);
                    else if (s.Format == ImageFormat.Ubifs) s.Ubifs.Compression = Enum.Parse<UbifsCompression>(value, true);
                    else if (s.Format == ImageFormat.Jffs2) s.Jffs2.Compression = Enum.Parse<Jffs2Compression>(value, true);
                    else if (s.Format == ImageFormat.Erofs) s.Erofs.Compression = Enum.Parse<ErofsCompression>(value, true);
                    else s.SquashFs.Compression = Enum.Parse<SquashCompression>(value, true);
                    break;
                case "block":
                    if (s.Format == ImageFormat.Cramfs) s.Cramfs.BlockSize = int.Parse(value);
                    else s.SquashFs.BlockSize = int.Parse(value);
                    break;
                case "level":
                    s.SquashFs.GzipLevel = s.Tar.GzipLevel = s.Cpio.GzipLevel = s.Cramfs.Level = Math.Clamp(int.Parse(value), 1, 9);
                    s.SquashFs.ZstdLevel = s.Tar.ZstdLevel = s.Ubifs.ZstdLevel = s.Cpio.ZstdLevel = int.Parse(value);
                    s.Erofs.Lz4Level = Math.Clamp(int.Parse(value), 1, 12);
                    s.Cpio.Lz4Level = s.Tar.Lz4Level = Math.Clamp(int.Parse(value), 1, 12);
                    s.Erofs.ZstdLevel = Math.Clamp(int.Parse(value), 1, 22);
                    s.Erofs.DeflateLevel = Math.Clamp(int.Parse(value), 1, 9);
                    break;
                case "effort": s.SquashFs.XzEffort = s.Tar.XzEffort = s.Cpio.XzEffort = s.Erofs.LzmaEffort = int.Parse(value); break;
                case "bcj": s.SquashFs.XzFilters.Add(Enum.Parse<BcjFilter>(value, true)); break;
                case "dict":
                    if (s.Format == ImageFormat.Erofs) s.Erofs.LzmaDictionarySize = int.Parse(value);
                    else s.SquashFs.XzDictionarySize = int.Parse(value);
                    break;
                case "pcluster": s.Erofs.PclusterSize = int.Parse(value); break;
                case "inline": s.Erofs.InlineData = value != "0"; break;
                case "sharexattrs": s.Erofs.ShareXattrs = value != "0"; break;
                case "uuid": s.Erofs.Uuid = Guid.Parse(value); s.F2fs.Uuid = Guid.Parse(value); break;
                case "time": s.Erofs.BuildTime = long.Parse(value); break;
                case "nofrag": s.SquashFs.NoFragments = value != "0"; break;
                case "dedup": s.SquashFs.Deduplicate = s.Erofs.Deduplicate = value != "0"; break;
                case "allroot": s.SquashFs.AllRoot = s.Ext.AllRoot = s.Tar.AllRoot = s.Ubifs.AllRoot = s.Jffs2.AllRoot = s.Erofs.AllRoot = s.Cpio.AllRoot = s.Cramfs.AllRoot = s.F2fs.AllRoot = value != "0"; break;
                case "endian": s.Jffs2.BigEndian = s.Cramfs.BigEndian = value.ToLowerInvariant() switch
                {
                    "big" or "be" => true,
                    "little" or "le" => false,
                    _ => throw new FsException("endian= takes little or big."),
                };
                    break;
                case "erase": s.Jffs2.EraseBlockSize = int.Parse(value); break;
                case "cleanmarkers": s.Jffs2.Cleanmarkers = value != "0"; break;
                case "cmsize": s.Jffs2.CleanmarkerSize = int.Parse(value); break;
                case "pagesize": s.Jffs2.PageSize = int.Parse(value); break;
                case "pad": s.Jffs2.PadToEraseBlock = s.Cpio.Pad512 = s.Cramfs.Pad = value != "0"; break;
                case "holes": s.Cramfs.Holes = value != "0"; break;
                case "edition": s.Cramfs.Edition = uint.Parse(value); break;
                case "align": s.Romfs.DataAlignment = int.Parse(value); break;
                case "xattrs": s.Jffs2.StoreXattrs = s.Erofs.StoreXattrs = s.F2fs.StoreXattrs = value != "0"; break;
                case "minio": s.Ubifs.MinIoSize = s.Ubi.MinIoSize = int.Parse(value); break;
                case "leb": s.Ubifs.LebSize = int.Parse(value); break;
                case "maxleb": s.Ubifs.MaxLebCount = int.Parse(value); break;
                case "fanout": s.Ubifs.Fanout = int.Parse(value); break;
                case "jrn": s.Ubifs.JournalBytes = long.Parse(value) << 20; s.Ubifs.LogLebs = 0; break;
                case "fixup": s.Ubifs.SpaceFixup = value != "0"; break;
                case "peb": s.Ubi.PebSize = int.Parse(value); break;
                case "subpage": s.Ubi.SubPageSize = int.Parse(value); break;
                case "volname": s.Ubi.VolumeName = value; break;
                case "volid": s.Ubi.VolumeId = int.Parse(value); break;
                case "volsize": s.Ubi.VolumeBytes = long.Parse(value) << 20; break;
                case "autoresize": s.Ubi.AutoResize = value != "0"; break;
                case "bs": s.Ext.BlockSize = int.Parse(value); break;
                case "size": s.Ext.SizeBytes = s.Fat.SizeBytes = s.F2fs.SizeBytes = long.Parse(value) << 20; break;
                case "free": s.Ext.ExtraFreeBytes = s.Fat.ExtraFreeBytes = s.F2fs.ExtraFreeBytes = long.Parse(value) << 20; break;
                case "isize": s.Ext.InodeSize = int.Parse(value); break;
                case "inodes": s.Ext.InodeCount = uint.Parse(value); break;
                case "journal": s.Ext.Journal = value != "0"; break;
                case "label":
                    s.Ext.Label = s.Fat.Label = value;
                    if (s.Format == ImageFormat.Erofs) s.Erofs.VolumeName = value;
                    s.Cramfs.VolumeName = s.Romfs.VolumeName = s.F2fs.VolumeName = value;
                    break;
                case "fattype": s.Fat.Type = value switch
                {
                    "12" => FatType.Fat12, "16" => FatType.Fat16, "32" => FatType.Fat32, "auto" => FatType.Auto,
                    _ => throw new FsException("fattype= takes 12, 16, 32 or auto."),
                };
                    break;
                case "cluster": s.Fat.ClusterSize = int.Parse(value); break;
                case "serial": s.Fat.VolumeId = System.Convert.ToUInt32(value.Replace("-", ""), 16); break;
                case "reserved": s.Ext.ReservedPercent = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "dialect": s.Tar.Dialect = Enum.Parse<TarDialect>(value, true); break;
                case "prefix": s.Tar.PathPrefix = s.Cpio.PathPrefix = value; break;
                case "root": s.Tar.IncludeRootEntry = s.Cpio.IncludeRootEntry = value != "0"; break;
                case "cpioformat": s.Cpio.Header = Enum.Parse<CpioHeader>(value, true); break;
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
            foreach (string u in r.UpdatedFiles ?? Array.Empty<string>()) Console.Error.WriteLine("updated: " + u);
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
            foreach (string u in r.UpdatedFiles ?? Array.Empty<string>()) Console.Error.WriteLine("updated: " + u);
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
            foreach (string u in r.UpdatedFiles ?? Array.Empty<string>()) Console.Error.WriteLine("updated: " + u);
            Console.Error.WriteLine($"packed {ir.Files} files, {ir.Directories} directories; written {r.BytesWritten} bytes in {sw.ElapsedMilliseconds} ms");
        }
        finally
        {
            doc.Dispose();
        }
        return 0;
    }

    // blocktest <file> <out> lzo|deflate [blocksize] : compresses a file block by block as UBIFS does; every block is
    // written as a 4-byte original size, a 4-byte packed size and the packed data, for a reference decompressor to check.
    private static int BlockTest(string[] a)
    {
        byte[] data = File.ReadAllBytes(a[1]);
        int block = a.Length > 4 ? int.Parse(a[4]) : 4096;
        var lzo = new LzoCodec();
        var deflate = new RawDeflate(11);
        using var o = File.Create(a[2]);
        byte[] dst = new byte[block * 2 + 64], back = new byte[block];
        long total = 0;
        for (int off = 0; off < data.Length; off += block)
        {
            ReadOnlySpan<byte> src = data.AsSpan(off, Math.Min(block, data.Length - off));
            int n = a[3] == "lzo" ? lzo.Compress(src, dst) : deflate.Compress(src, dst);
            if (n < 0) throw new FsException("the block did not fit");
            if (a[3] == "lzo" && (lzo.Decompress(dst.AsSpan(0, n), back.AsSpan(0, src.Length)) != src.Length || !back.AsSpan(0, src.Length).SequenceEqual(src)))
                throw new FsException($"LZO round trip failed at {off}");
            o.Write(BitConverter.GetBytes(src.Length));
            o.Write(BitConverter.GetBytes(n));
            o.Write(dst, 0, n);
            total += n;
        }
        Console.WriteLine($"{data.Length} -> {total} ({100.0 * total / Math.Max(1, data.Length):F2}%)");
        return 0;
    }

    // lz4test <file> [level] [cluster] : compresses a file into clusters of a fixed size as EROFS does (one thread)
    // and checks every cluster with the decoder; prints the ratio and the speed.
    private static int Lz4Test(string[] a)
    {
        byte[] data = File.ReadAllBytes(a[1]);
        int level = a.Length > 2 ? int.Parse(a[2]) : 9, cluster = a.Length > 3 ? int.Parse(a[3]) : 4096;
        var lz4 = new Lz4FixedEncoder(level);
        byte[] dst = new byte[cluster], back = new byte[4 << 20];
        var sw = Stopwatch.StartNew();
        long packed = 0, clusters = 0;
        for (int pos = 0; pos < data.Length;)
        {
            int window = Math.Min(data.Length - pos, cluster * 4);
            int n = lz4.Compress(data.AsSpan(pos, window), dst, out int taken);
            if (n <= 0 || taken <= 0) throw new FsException($"nothing was compressed at {pos}");
            if (Lz4FixedEncoder.Decompress(dst.AsSpan(0, n), back.AsSpan(0, taken)) != taken || !back.AsSpan(0, taken).SequenceEqual(data.AsSpan(pos, taken)))
                throw new FsException($"LZ4 round trip failed at {pos}");
            packed += (n + 4095) & ~4095;
            clusters++;
            pos += taken;
        }
        double s = sw.Elapsed.TotalSeconds;
        Console.WriteLine($"{data.Length} -> {packed} ({100.0 * packed / Math.Max(1, data.Length):F2}%) in {clusters} clusters, {s:F2} s, {data.Length / s / 1048576:F0} MiB/s");
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
