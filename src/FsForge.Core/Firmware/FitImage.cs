using System.Buffers.Binary;
using System.Security.Cryptography;
using FsForge.Core.Compression;
using FsForge.Core.Images;

namespace FsForge.Core.Firmware;

/// <summary>
/// A U-Boot FIT image (Flattened Image Tree: <c>.itb</c>, <c>.ub</c>, <c>.fit</c>): a device tree whose node
/// <c>/images</c> holds the kernel, device trees, ramdisks and filesystems, each with its type, compression and
/// hashes. The data of an image lies in its <c>data</c> property, or outside the tree (<c>mkimage -E</c>) at
/// <c>data-offset</c> behind it or at <c>data-position</c> from its start. PetaLinux's image.ub wraps its ramdisk
/// once more in a legacy uImage; OpenWrt can put the root filesystem behind the FIT image, as behind a uImage.
/// </summary>
public sealed class FitImage : FirmwareImage
{
    private FitImage(IBlockSource source) : base(source) { }

    /// <summary>The <c>description</c> of the root node.</summary>
    public string Name { get; private init; } = "";

    /// <summary>The configuration U-Boot boots when none is named, and what it takes.</summary>
    public string DefaultConfiguration { get; private set; } = "";

    public string DefaultConfigurationText { get; private set; } = "";

    /// <summary>Images or configurations carry signature nodes (verified boot).</summary>
    public bool Signed { get; private set; }

    /// <summary>Hashes of image data checked, and how many of them did not match.</summary>
    public int HashesChecked { get; private set; }
    public int HashesWrong { get; private set; }

    /// <summary>The data of the images lies outside the tree (mkimage -E).</summary>
    public bool ExternalData { get; private set; }

    public override string Kind => "U-Boot FIT image";
    public override string ShortKind => "FIT";
    public override string Description => Name.Length > 0 ? $"{Kind} '{Name}'" : Kind;

    /// <summary>The file starts with a device tree that has an /images node: a FIT image, not a plain DTB.</summary>
    public static bool IsFit(IBlockSource source)
    {
        if (!Fdt.HasHeader(source)) return false;
        try
        {
            return Fdt.Read(source).Root.Child("images") is { Children.Count: > 0 };
        }
        catch (FsException)
        {
            return false;
        }
    }

    public static FitImage Read(IBlockSource source, CancellationToken ct = default)
    {
        Fdt tree = Fdt.Read(source);
        FdtNode images = tree.Root.Child("images") ?? throw new FsException("The device tree has no /images node: it is not a FIT image.");
        var fit = new FitImage(source) { Name = tree.Root["description"]?.Text ?? "" };
        fit.ReadParts(tree, images, ct);
        return fit;
    }

    private void ReadParts(Fdt tree, FdtNode images, CancellationToken ct)
    {
        // External data starts behind the tree, at the next 4-byte boundary.
        long dataBase = (tree.TotalSize + 3L) & ~3L, end = dataBase;
        var parts = new List<FirmwarePart>();
        foreach (FdtNode image in images.Children)
        {
            ct.ThrowIfCancellationRequested();
            string type = image["type"]?.Text ?? "";
            string compression = image["compression"]?.Text ?? "none";
            if (image.Children.Any(c => c.Name.StartsWith("signature", StringComparison.Ordinal))) Signed = true;

            long offset, length;
            if (image["data"] is { } data)
                (offset, length) = (data.Offset, data.Length);
            else if (image["data-size"]?.Number is { } size && (image["data-offset"]?.Number is { } rel ? dataBase + (long)rel : (long?)image["data-position"]?.Number) is { } at)
            {
                (offset, length) = (at, (long)size);
                ExternalData = true;
            }
            else
            {
                Warnings.Add($"Image '{image.Name}' of the FIT image has no data.");
                continue;
            }
            if (offset < 0 || offset + length > Source.Length)
            {
                Warnings.Add($"The data of image '{image.Name}' of the FIT image lies beyond the end of the file: the file is truncated.");
                length = Math.Max(0, Source.Length - offset);
            }
            end = Math.Max(end, offset + length);

            string hashes = CheckHashes(image, offset, length, ct);
            string detail = $"type {(type.Length > 0 ? type : "unknown")}, compression {compression}" + hashes;
            parts.Add(MakePart(parts.Count, image.Name, offset, length, type, compression, detail));
        }

        FdtNode? configurations = tree.Root.Child("configurations");
        if (configurations?["default"]?.Text is { Length: > 0 } dflt && configurations.Child(dflt) is { } conf)
        {
            DefaultConfiguration = dflt;
            DefaultConfigurationText = conf["description"]?.Text ?? "";
            if (conf.Children.Any(c => c.Name.StartsWith("signature", StringComparison.Ordinal))) Signed = true;
        }
        if (configurations is not null && configurations.Children.Any(c => c.Children.Any(s => s.Name.StartsWith("signature", StringComparison.Ordinal))))
            Signed = true;

        if (end < Source.Length && FindTrailing(end, ct) is { } rootfs)
        {
            (PartitionContent content, string contentName, var packed) = Identify(new SubBlockSource(Source, rootfs, Source.Length - rootfs, ownsInner: false));
            if (content is not (PartitionContent.Unknown or PartitionContent.Empty))
                parts.Add(new FirmwarePart
                {
                    Index = parts.Count, Name = parts.Any(p => p.Name == "rootfs") ? "trailing" : "rootfs", Offset = rootfs, Length = Source.Length - rootfs,
                    Content = content, ContentName = contentName, Packed = packed, BehindImage = true,
                    Detail = rootfs == end ? "right after the FIT image, outside it" : $"{rootfs - end} bytes after the end of the FIT image, outside it",
                });
        }
        Parts = parts;
    }

    private FirmwarePart MakePart(int index, string name, long offset, long length, string type, string compression, string detail)
    {
        using var data = new SubBlockSource(Source, offset, length, ownsInner: false);
        (PartitionContent content, string contentName, var packed) = Identify(data);

        // PetaLinux and older builds wrap the ramdisk in a legacy uImage (rootfs.cpio.gz.u-boot) before putting it here.
        if (content is PartitionContent.Unknown && UImage.IsUImage(data))
        {
            try
            {
                UImage inner = UImage.Read(data);
                if (inner.Parts is [{ IsReadable: true } only] && inner.Type != 4)
                    return new FirmwarePart
                    {
                        Index = index, Name = name, Offset = offset + only.Offset, Length = only.Length,
                        Content = only.Content, ContentName = only.ContentName, Packed = only.Packed,
                        Detail = detail + $", inside a uImage{(inner.Name.Length > 0 ? $" '{inner.Name}'" : "")}",
                    };
            }
            catch (FsException)
            {
            }
        }
        if (content is PartitionContent.Unknown)
        {
            if (type is "kernel" or "kernel_noload")
                contentName = compression == "none" ? "kernel" : $"kernel ({compression})";
            else if (type == "flat_dt" || length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(data.ReadBytes(0, 4)) == Fdt.Magic)
                contentName = "device tree";
            else if (compression != "none" && contentName.Length == 0)
                contentName = $"{type} ({compression})";
        }
        return new FirmwarePart
        {
            Index = index, Name = name, Offset = offset, Length = length,
            Content = content, ContentName = contentName, Packed = packed, Detail = detail,
        };
    }

    /// <summary>Checks the hash nodes of an image (crc32, md5, sha1, sha256, sha384, sha512); returns what to show for them.</summary>
    private string CheckHashes(FdtNode image, long offset, long length, CancellationToken ct)
    {
        var results = new List<string>();
        foreach (FdtNode hash in image.Children.Where(c => c.Name.StartsWith("hash", StringComparison.Ordinal)))
        {
            string algo = hash["algo"]?.Text ?? "";
            byte[]? expected = hash["value"]?.Value;
            if (expected is null) continue;
            byte[]? actual = Hash(algo, offset, length, ct);
            if (actual is null)
            {
                results.Add($"{algo} not checked");
                continue;
            }
            HashesChecked++;
            bool ok = actual.AsSpan().SequenceEqual(expected);
            if (!ok)
            {
                HashesWrong++;
                Warnings.Add($"The {algo} hash of image '{image.Name}' of the FIT image does not match its data: the image was changed or damaged, and U-Boot would refuse to boot it.");
            }
            results.Add($"{algo} {(ok ? "ok" : "wrong")}");
        }
        return results.Count > 0 ? ", hash " + string.Join(", ", results) : "";
    }

    private byte[]? Hash(string algo, long offset, long length, CancellationToken ct)
    {
        IncrementalHash? h = algo switch
        {
            "md5" => IncrementalHash.CreateHash(HashAlgorithmName.MD5),
            "sha1" => IncrementalHash.CreateHash(HashAlgorithmName.SHA1),
            "sha256" => IncrementalHash.CreateHash(HashAlgorithmName.SHA256),
            "sha384" => IncrementalHash.CreateHash(HashAlgorithmName.SHA384),
            "sha512" => IncrementalHash.CreateHash(HashAlgorithmName.SHA512),
            _ => null,
        };
        if (h is null && algo != "crc32") return null;
        byte[] buf = new byte[1 << 20];
        uint crc = 0;
        for (long done = 0; done < length;)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(buf.Length, length - done);
            Source.ReadExactly(offset + done, buf.AsSpan(0, n));
            if (h is null) crc = Crc32.Update(crc, buf.AsSpan(0, n));
            else h.AppendData(buf, 0, n);
            done += n;
        }
        if (h is null)
        {
            byte[] v = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(v, crc);
            return v;
        }
        using (h) return h.GetHashAndReset();
    }

    // ---------------------------------------------------------- writing

    public override bool CanRebuild(FirmwarePart part, out string reason)
    {
        reason = "Rebuilding a FIT image is not supported yet.";
        return false;
    }

    public override void Write(Stream output, FirmwarePart part, IBlockSource data, List<string> warnings,
        IProgress<ProgressInfo>? progress, CancellationToken ct) =>
        throw new FsException("Rebuilding a FIT image is not supported yet.");

    public override IEnumerable<(string Name, IBlockSource Data)> FixedSections(FirmwarePart open) => Array.Empty<(string, IBlockSource)>();
}
