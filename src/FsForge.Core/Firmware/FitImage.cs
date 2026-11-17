using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FsForge.Core.Compression;
using FsForge.Core.Android;
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

    /// <summary>Where the data of an image node lies, and the uImage header it is wrapped in (PetaLinux), if any.</summary>
    private sealed record ImageData(FdtNode Node, long Offset, long Length, bool External, byte[]? WrapHeader);

    private Fdt _tree = null!;

    /// <summary>The image nodes with data, by the name of the part made of each.</summary>
    private readonly Dictionary<string, ImageData> _images = new(StringComparer.Ordinal);

    /// <summary>Where external data starts (behind the tree, at a 4-byte boundary) and where the FIT image ends.</summary>
    private long _dataBase, _end;

    private FirmwarePart? _trailing;

    /// <summary>Why the image cannot be rebuilt (signed, truncated, a hash of an unknown kind); empty when it can.</summary>
    private string _cannotRebuild = "";

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

    public override bool ChecksumsValid => HashesWrong == 0;
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
        fit._tree = tree;
        fit.ReadParts(tree, images, ct);
        return fit;
    }

    private void ReadParts(Fdt tree, FdtNode images, CancellationToken ct)
    {
        // External data starts behind the tree, at the next 4-byte boundary.
        long dataBase = (tree.TotalSize + 3L) & ~3L, end = dataBase;
        _dataBase = dataBase;
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
                _cannotRebuild = "The FIT image is truncated: the data of an image reaches beyond the end of the file.";
            }
            end = Math.Max(end, offset + length);

            string hashes = CheckHashes(image, offset, length, ct);
            string detail = $"type {(type.Length > 0 ? type : "unknown")}, compression {compression}" + hashes;
            FirmwarePart part = MakePart(parts.Count, image.Name, offset, length, type, compression, detail);
            parts.Add(part);
            _images[part.Name] = new ImageData(image, offset, length, image["data"] is null,
                part.Offset != offset ? Source.ReadBytes(offset, UImage.HeaderSize) : null);
        }
        _end = end;

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
                parts.Add(_trailing = new FirmwarePart
                {
                    Index = parts.Count, Name = parts.Any(p => p.Name == "rootfs") ? "trailing" : "rootfs", Offset = rootfs, Length = Source.Length - rootfs,
                    Content = content, ContentName = contentName, Packed = packed, BehindImage = true,
                    Detail = rootfs == end ? "right after the FIT image, outside it" : $"{rootfs - end} bytes after the end of the FIT image, outside it",
                });
        }
        Parts = parts;
        ReadFwtool(end, ct);
        if (Signed && _cannotRebuild.Length == 0)
            _cannotRebuild = "The FIT image is signed (U-Boot verified boot), and a changed image cannot be signed again without the key.";
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
            byte[]? actual = Hash(algo, new[] { Slice(offset, offset + length) }, ct);
            if (actual is null)
            {
                results.Add($"{algo} not checked");
                _cannotRebuild = $"Image '{image.Name}' of the FIT image has a hash of a kind this program does not compute ({algo}).";
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

    private static byte[]? Hash(string algo, IEnumerable<IBlockSource> data, CancellationToken ct)
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
        foreach (IBlockSource piece in data)
            for (long done = 0; done < piece.Length;)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buf.Length, piece.Length - done);
                piece.ReadExactly(done, buf.AsSpan(0, n));
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
        reason = _cannotRebuild;
        if (reason.Length == 0 && !Parts.Contains(part)) reason = "The part does not belong to this image.";
        if (reason.Length == 0 && part.Content == PartitionContent.Unknown) reason = $"'{part.Name}' holds nothing this program writes.";
        return reason.Length == 0;
    }

    /// <summary>
    /// Writes the FIT image again with <paramref name="data"/> as the new content of <paramref name="part"/>. The tree is
    /// written again with the nodes and properties in their order: the image gets its new data (wrapped in its uImage
    /// again, with a new size and CRC, when it was so) and new hash values; data outside the tree is laid out again
    /// behind it, with new data-offset, data-position and data-size. A root filesystem behind the FIT image leaves the
    /// image as it is, and OpenWrt's markers and metadata behind it are kept as behind a uImage.
    /// </summary>
    public override void Write(Stream output, FirmwarePart part, IBlockSource data, List<string> warnings,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        if (!CanRebuild(part, out string reason)) throw new FsException(reason);
        if (ReferenceEquals(part, _trailing))
        {
            WritePieces(output, TrailingPieces(part, data), warnings, progress, ct);
            return;
        }

        ImageData changed = _images[part.Name];
        // The new data of the image node: the content, in its uImage again when it was wrapped in one.
        var content = new List<IBlockSource>();
        if (changed.WrapHeader is { } wrap)
        {
            if (data.Length > uint.MaxValue) throw new FsException("The new content is too large for a uImage (4 GiB at most).");
            byte[] header = (byte[])wrap.Clone();
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), (uint)data.Length);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), (uint)Crc32HashOf(data, ct));
            header.AsSpan(4, 4).Clear();
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), Crc32.Compute(header));
            content.Add(new MemoryBlockSource(header));
        }
        content.Add(data);
        long newLength = content.Sum(c => c.Length);

        var replace = new Dictionary<FdtProperty, IBlockSource>();
        foreach (FdtNode hash in changed.Node.Children.Where(c => c.Name.StartsWith("hash", StringComparison.Ordinal)))
            if (hash["value"] is { } value && Hash(hash["algo"]?.Text ?? "", content, ct) is { } digest)
                replace[value] = new MemoryBlockSource(digest);

        // External data: laid out again in its old order, each image at a 4-byte boundary.
        var external = _images.Values.Where(i => i.External).OrderBy(i => i.Offset).ToList();
        if (changed.External)
        {
            if (newLength > uint.MaxValue) throw new FsException("The new content is too large for a FIT image with external data (4 GiB at most).");
            replace[changed.Node["data-size"]!] = Cell(changed.Node["data-size"]!, (ulong)newLength);
        }
        else
        {
            if (newLength > int.MaxValue) throw new FsException("The new content is too large for the device tree of a FIT image (2 GiB at most).");
            replace[changed.Node["data"]!] = new ConcatSource(content.Select(c => ((IBlockSource?)c, 0L, c.Length)));
        }
        // The size of the tree does not depend on the offsets (cells of a fixed size): learn it, then place the data.
        long treeSize = _tree.Write(p => replace.GetValueOrDefault(p)).Sum(p => p.Length);
        long dataBase = (treeSize + 3) & ~3L;
        long start = external.Count == 0 ? dataBase
            : external.Any(i => i.Node["data-position"] is not null) ? Math.Max(external[0].Offset, dataBase)
            : dataBase + (external[0].Offset - _dataBase);
        var placed = new List<(ImageData Image, long At, long Length)>();
        long pos = start;
        foreach (ImageData image in external)
        {
            long length = ReferenceEquals(image, changed) ? newLength : image.Length;
            placed.Add((image, pos, length));
            if (image.Node["data-offset"] is { } rel) replace[rel] = Cell(rel, (ulong)(pos - dataBase));
            else if (image.Node["data-position"] is { } abs) replace[abs] = Cell(abs, (ulong)pos);
            pos += (length + 3) & ~3L;
        }

        var pieces = _tree.Write(p => replace.GetValueOrDefault(p));
        long at = treeSize;
        foreach ((ImageData image, long where, long length) in placed)
        {
            pieces.Add(Fill(where - at, 0));
            if (ReferenceEquals(image, changed)) pieces.AddRange(content);
            else pieces.Add(Slice(image.Offset, image.Offset + image.Length));
            at = where + length;
            long padded = (length + 3) & ~3L;
            pieces.Add(Fill(padded - length, 0));
            at = where + padded;
        }

        // What followed the FIT image: a root filesystem keeps its kind of boundary (an erase block), anything else follows.
        long newEnd = at;
        if (_trailing is not null)
        {
            long trailingAt = Shifted(_trailing.Offset, newEnd, Alignment(_trailing.Offset));
            byte fill = _trailing.Offset > _end ? Source.ReadBytes(_end, 1)[0] : (byte)0xFF;
            pieces.Add(Fill(trailingAt - newEnd, fill));
            pieces.Add(Slice(_trailing.Offset, ContentEnd));
        }
        else if (_end < ContentEnd) pieces.Add(Slice(_end, ContentEnd));
        WritePieces(output, pieces, warnings, progress, ct);
    }

    /// <summary>A number in a property cell of the same size as the old one (32 or 64 bits).</summary>
    private static IBlockSource Cell(FdtProperty p, ulong value)
    {
        byte[] v = new byte[p.Length == 8 ? 8 : 4];
        if (v.Length == 8) BinaryPrimitives.WriteUInt64BigEndian(v, value);
        else
        {
            if (value > uint.MaxValue) throw new FsException($"'{p.Name}' does not fit into its 32-bit cell.");
            BinaryPrimitives.WriteUInt32BigEndian(v, (uint)value);
        }
        return new MemoryBlockSource(v);
    }

    private static uint Crc32HashOf(IBlockSource data, CancellationToken ct) =>
        BinaryPrimitives.ReadUInt32BigEndian(Hash("crc32", new[] { data }, ct));

    /// <summary>
    /// What a rewrite keeps: the other images, and the tree itself apart from the data, sizes, offsets and hash values
    /// of the rewritten image (and the offsets of external data, which move).
    /// </summary>
    public override IEnumerable<(string Name, IBlockSource Data)> FixedSections(FirmwarePart open)
    {
        foreach (FirmwarePart p in Parts)
            if (p.Name != open.Name)
                yield return (p.Name, ReferenceEquals(p, _trailing) ? Slice(p.Offset, ContentEnd) : Open(p));
        var text = new StringBuilder();
        void Dump(FdtNode node, string path, bool changed)
        {
            text.Append(path).Append('\n');
            foreach (FdtProperty prop in node.PropertyList)
            {
                if (prop.Name is "data-offset" or "data-position") continue;
                if (changed && prop.Name is "data" or "data-size" or "value") continue;
                text.Append("  ").Append(prop.Name).Append(" = ");
                text.Append(prop.Value is null ? $"<{prop.Length} bytes>" : prop.Name == "data" ? $"<{prop.Length} bytes>" : Convert.ToHexString(prop.Value)).Append('\n');
            }
            foreach (FdtNode child in node.Children) Dump(child, path + "/" + child.Name, changed);
        }
        string? changedNode = _images.GetValueOrDefault(open.Name)?.Node.Name;
        foreach (FdtNode child in _tree.Root.Children)
            if (child.Name != "images") Dump(child, "/" + child.Name, false);
        foreach (FdtNode image in _tree.Root.Child("images")?.Children ?? new List<FdtNode>())
            Dump(image, "/images/" + image.Name, image.Name == changedNode);
        foreach (FdtProperty prop in _tree.Root.PropertyList)
            text.Append(prop.Name).Append(" = ").Append(prop.Value is null ? "" : Convert.ToHexString(prop.Value)).Append('\n');
        yield return ("device tree", new MemoryBlockSource(Encoding.UTF8.GetBytes(text.ToString())));
        foreach ((string name, IBlockSource data) in BehindSections(_trailing, open)) yield return (name, data);
    }
}
