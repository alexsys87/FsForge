using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FsForge.Core.Images;

namespace FsForge.Core.Android;

/// <summary>
/// Android boot images (system/tools/mkbootimg): boot.img, recovery.img and init_boot.img with the "ANDROID!"
/// header of versions 0 to 4, and vendor_boot.img with the "VNDRBOOT" header of versions 3 and 4. They are
/// wrappers: a header page, then the kernel, the ramdisk (a compressed cpio archive), the second stage loader,
/// the recovery DTBO, the DTB, the vendor ramdisk table and the bootconfig, each starting on a page.
/// <para>
/// The ramdisk (for vendor_boot v4 every vendor ramdisk fragment) is what can be opened. Writing keeps every
/// other section byte for byte, updates the sizes and offsets in the header and the ramdisk table, computes
/// the SHA-1 id of versions 0 to 2 as mkbootimg does, and keeps an AVB footer at the end of the partition
/// (the signature no longer matches; a device with an unlocked boot loader still boots the image).
/// </para>
/// </summary>
public sealed class BootImage : AndroidImage
{
    public static ReadOnlySpan<byte> BootMagic => "ANDROID!"u8;
    public static ReadOnlySpan<byte> VendorMagic => "VNDRBOOT"u8;
    private const int VendorTableEntry = 108;

    private IBlockSource _source = null!;
    private bool _vendor;
    private byte[] _header = Array.Empty<byte>();
    private uint _page;
    private int _headerVersion;

    /// <summary>Old boot images keep a device tree ("dt", Qualcomm) in the field later used for the header version.</summary>
    private uint _qcomDtSize;

    /// <summary>Sections in file order: name, offset, size.</summary>
    private readonly List<(string Name, long Offset, long Size)> _sections = new();

    /// <summary>Vendor ramdisk table of vendor_boot v4: the raw entries.</summary>
    private readonly List<byte[]> _table = new();

    /// <summary>End of the image proper; what follows (padding, an AVB footer, a vendor trailer) is kept.</summary>
    private long _end;

    private AvbFooter? _avb;

    public override AndroidImageKind Kind => _vendor ? AndroidImageKind.VendorBoot : AndroidImageKind.Boot;
    public override string Description => $"{(_vendor ? "vendor_boot" : "boot")} (header v{_headerVersion}, page {_page})";

    public int HeaderVersion => _headerVersion;
    public uint PageSize => _page;
    public string Cmdline { get; private set; } = "";

    /// <summary>The partition image ends with an AVB footer (Android Verified Boot).</summary>
    public bool HasAvbFooter => _avb is not null;

    private sealed record AvbFooter(long OriginalSize, long VbmetaOffset, long VbmetaSize);

    public static bool IsBoot(IBlockSource source)
    {
        if (source.Length < 2048) return false;
        Span<byte> m = stackalloc byte[8];
        source.ReadExactly(0, m);
        return m.SequenceEqual(BootMagic) || m.SequenceEqual(VendorMagic);
    }

    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));

    private static string Text(byte[] b, int at, int len)
    {
        int n = Array.IndexOf(b, (byte)0, at, len);
        return Encoding.ASCII.GetString(b, at, (n < 0 ? at + len : n) - at);
    }

    private static long Pages(long size, uint page) => (size + page - 1) / page * page;

    public static BootImage Read(IBlockSource source)
    {
        var b = new BootImage { _source = source };
        b._avb = ReadAvbFooter(source);
        byte[] h = source.ReadBytes(0, (int)Math.Min(source.Length, 4096));
        b._vendor = h.AsSpan(0, 8).SequenceEqual(VendorMagic);
        long pos;
        if (b._vendor)
        {
            b._headerVersion = (int)U32(h, 8);
            if (b._headerVersion is < 3 or > 4) throw new FsException($"The vendor boot image has header version {b._headerVersion}; versions 3 and 4 are known.");
            b._page = U32(h, 12);
            uint headerSize = U32(h, 2096);
            if (!ValidPage(b._page) || headerSize is < 2112 or > 4096) throw new FsException("Corrupt vendor boot image: invalid header.");
            b._header = h.AsSpan(0, (int)headerSize).ToArray();
            b.Cmdline = Text(h, 28, 2048);
            pos = Pages(headerSize, b._page);
            pos = b.Section("vendor_ramdisk", pos, U32(h, 24));
            pos = b.Section("dtb", pos, U32(h, 2100));
            if (b._headerVersion >= 4)
            {
                uint tableSize = U32(h, 2112), entries = U32(h, 2116), entrySize = U32(h, 2120);
                long tableAt = pos;
                pos = b.Section("vendor_ramdisk_table", pos, tableSize);
                pos = b.Section("bootconfig", pos, U32(h, 2124));
                if (entrySize < VendorTableEntry || (long)entries * entrySize > tableSize || tableAt + tableSize > source.Length)
                    throw new FsException("Corrupt vendor boot image: invalid vendor ramdisk table.");
                for (int i = 0; i < entries; i++) b._table.Add(source.ReadBytes(tableAt + (long)i * entrySize, (int)entrySize));
            }
        }
        else
        {
            uint version = U32(h, 40);
            if (version is >= 3 and <= 4)
            {
                b._headerVersion = (int)version;
                b._page = 4096;
                uint headerSize = U32(h, 20);
                if (headerSize is < 1580 or > 4096) throw new FsException("Corrupt boot image: invalid header size.");
                b._header = h.AsSpan(0, (int)headerSize).ToArray();
                b.Cmdline = Text(h, 44, 1536);
                pos = 4096;
                pos = b.Section("kernel", pos, U32(h, 8));
                pos = b.Section("ramdisk", pos, U32(h, 12));
                if (version == 4 && headerSize >= 1584) pos = b.Section("boot_signature", pos, U32(h, 1580));
            }
            else
            {
                // Versions 0 to 2; a large value in the version field is the "dt" size of old Qualcomm images.
                b._headerVersion = version <= 2 ? (int)version : 0;
                if (version > 2) b._qcomDtSize = version;
                b._page = U32(h, 36);
                if (!ValidPage(b._page)) throw new FsException("Corrupt boot image: invalid page size.");
                int headerSize = b._headerVersion switch { 1 => (int)U32(h, 1644), 2 => (int)U32(h, 1644), _ => 1632 };
                if (headerSize is < 1632 or > 4096) throw new FsException("Corrupt boot image: invalid header size.");
                b._header = h.AsSpan(0, headerSize).ToArray();
                b.Cmdline = Text(h, 64, 512) + Text(h, 608, 1024);
                pos = b._page;
                pos = b.Section("kernel", pos, U32(h, 8));
                pos = b.Section("ramdisk", pos, U32(h, 16));
                pos = b.Section("second", pos, U32(h, 24));
                if (b._qcomDtSize > 0) pos = b.Section("dt", pos, b._qcomDtSize);
                if (b._headerVersion >= 1) pos = b.Section("recovery_dtbo", pos, U32(h, 1632));
                if (b._headerVersion >= 2) pos = b.Section("dtb", pos, U32(h, 1648));
            }
        }
        b._end = pos;
        long limit = b._avb?.OriginalSize ?? source.Length;
        if (pos > limit) throw new FsException($"The boot image is truncated: its header describes {pos} bytes, the file has {limit}.");
        b.BuildParts();
        return b;
    }

    private static bool ValidPage(uint page) => page is >= 2048 and <= 65536 && (page & (page - 1)) == 0;

    private long Section(string name, long pos, long size)
    {
        _sections.Add((name, pos, size));
        return pos + Pages(size, _page);
    }

    private (string Name, long Offset, long Size) SectionOf(string name) => _sections.First(s => s.Name == name);

    private static AvbFooter? ReadAvbFooter(IBlockSource source)
    {
        if (source.Length < 64) return null;
        byte[] f = source.ReadBytes(source.Length - 64, 64);
        if (!f.AsSpan(0, 4).SequenceEqual("AVBf"u8)) return null;
        long original = (long)BinaryPrimitives.ReadUInt64BigEndian(f.AsSpan(12));
        long vbmeta = (long)BinaryPrimitives.ReadUInt64BigEndian(f.AsSpan(20));
        long vbmetaSize = (long)BinaryPrimitives.ReadUInt64BigEndian(f.AsSpan(28));
        if (original <= 0 || original > vbmeta || vbmeta + vbmetaSize > source.Length - 64) return null;
        return new AvbFooter(original, vbmeta, vbmetaSize);
    }

    private void BuildParts()
    {
        var parts = new List<AndroidPart>();
        if (_vendor && _table.Count > 0)
        {
            (_, long at, long size) = SectionOf("vendor_ramdisk");
            for (int i = 0; i < _table.Count; i++)
            {
                byte[] e = _table[i];
                uint len = U32(e, 0), off = U32(e, 4), type = U32(e, 8);
                string name = Text(e, 12, 32);
                if ((long)off + len > size) throw new FsException("Corrupt vendor boot image: a vendor ramdisk lies outside its section.");
                var data = new SubBlockSource(_source, at + off, len, ownsInner: false);
                (PartitionContent content, string what) = Identify(data);
                parts.Add(new AndroidPart
                {
                    Index = i, Name = name.Length > 0 ? name : $"vendor_ramdisk{i}", Length = len, Content = content, ContentName = what,
                    Detail = type switch { 1 => "platform", 2 => "recovery", 3 => "dlkm", 0 => "", _ => $"type {type}" },
                });
            }
        }
        else
        {
            string name = _vendor ? "vendor_ramdisk" : "ramdisk";
            (_, long at, long size) = SectionOf(name);
            var data = new SubBlockSource(_source, at, size, ownsInner: false);
            (PartitionContent content, string what) = Identify(data);
            parts.Add(new AndroidPart { Index = 0, Name = name, Length = size, Content = content, ContentName = what });
        }
        Parts = parts;
    }

    public override IBlockSource Open(AndroidPart part)
    {
        if (_vendor && _table.Count > 0)
        {
            (_, long at, _) = SectionOf("vendor_ramdisk");
            byte[] e = _table[part.Index];
            return new SubBlockSource(_source, at + U32(e, 4), U32(e, 0), ownsInner: false);
        }
        (_, long offset, long size) = SectionOf(_vendor ? "vendor_ramdisk" : "ramdisk");
        return new SubBlockSource(_source, offset, size, ownsInner: false);
    }

    public override IEnumerable<(string Name, IBlockSource Data)> FixedSections()
    {
        foreach ((string name, long offset, long size) in _sections)
            if (name is not ("ramdisk" or "vendor_ramdisk" or "vendor_ramdisk_table"))
                yield return (name, new SubBlockSource(_source, offset, size, ownsInner: false));
        foreach (AndroidPart p in Parts) yield return ("ramdisk:" + p.Name, Open(p));
    }

    public override void Write(Stream output, AndroidPart part, IBlockSource data, List<string> warnings,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        if (data.Length > uint.MaxValue) throw new FsException("The ramdisk is too large for a boot image.");
        byte[] header = (byte[])_header.Clone();
        var sections = new List<(string Name, IBlockSource Data)>();
        IBlockSource Old(string name) { var s = SectionOf(name); return new SubBlockSource(_source, s.Offset, s.Size, ownsInner: false); }

        byte[]? table = null;
        foreach ((string name, _, _) in _sections)
        {
            IBlockSource content = Old(name);
            if (name == (_vendor ? "vendor_ramdisk" : "ramdisk"))
            {
                if (_vendor && _table.Count > 0)
                {
                    // The fragments one after another, the replaced one new; the table follows them.
                    var pieces = new List<(IBlockSource?, long, long)>();
                    var ms = new MemoryStream();
                    long off = 0;
                    for (int i = 0; i < _table.Count; i++)
                    {
                        IBlockSource frag = i == part.Index ? data : Open(Parts[i]);
                        pieces.Add((frag, 0, frag.Length));
                        byte[] e = (byte[])_table[i].Clone();
                        BinaryPrimitives.WriteUInt32LittleEndian(e, (uint)frag.Length);
                        BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(4), (uint)off);
                        ms.Write(e);
                        off += frag.Length;
                    }
                    content = new ConcatSource(pieces);
                    table = ms.ToArray();
                }
                else content = data;
            }
            else if (name == "vendor_ramdisk_table" && table is not null) content = new MemoryBlockSource(table);
            sections.Add((name, content));
        }

        // Sizes in the header.
        void Size(int at, string name) => BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(at), (uint)sections.First(s => s.Name == name).Data.Length);
        if (_vendor)
        {
            Size(24, "vendor_ramdisk");
            if (_headerVersion >= 4) Size(2112, "vendor_ramdisk_table");
        }
        else if (_headerVersion >= 3) Size(12, "ramdisk");
        else
        {
            Size(16, "ramdisk");
            if (_headerVersion >= 1)
            {
                // The recovery DTBO is located by its offset in the image.
                long at = _page;
                foreach ((string name, IBlockSource d) in sections)
                {
                    if (name == "recovery_dtbo") break;
                    at += Pages(d.Length, _page);
                }
                if (sections.First(s => s.Name == "recovery_dtbo").Data.Length > 0)
                    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(1636), (ulong)at);
            }
            // The id: SHA-1 of every section followed by its size, as mkbootimg computes it.
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            byte[] buf = new byte[1 << 16];
            Span<byte> len = stackalloc byte[4];
            foreach ((string name, IBlockSource d) in sections)
            {
                if (name == "dt") continue;
                for (long o = 0; o < d.Length; o += buf.Length)
                {
                    int n = (int)Math.Min(buf.Length, d.Length - o);
                    d.ReadExactly(o, buf.AsSpan(0, n));
                    sha.AppendData(buf, 0, n);
                }
                BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)d.Length);
                sha.AppendData(len);
            }
            header.AsSpan(576, 32).Clear();
            sha.GetHashAndReset().CopyTo(header, 576);
        }

        var layout = new ImageLayout(0);
        layout.Add(0, header);
        long pos = Pages(header.Length, _page);
        if (!_vendor && _headerVersion >= 3) pos = 4096;
        foreach ((string _, IBlockSource d) in sections)
        {
            layout.Add(pos, d);
            pos += Pages(d.Length, _page);
        }
        long end = pos;
        layout.Extend(end);                                 // the last section is padded to a whole page too

        // What followed the image: an AVB footer and its vbmeta at the end of the partition, or a vendor trailer.
        if (_avb is { } avb)
        {
            if (end > avb.VbmetaOffset)
                throw new FsException($"The new image is {DiskText.Size(end)}, but the boot partition has room for {DiskText.Size(avb.VbmetaOffset)} " +
                                      "before its AVB signature data. Make the ramdisk smaller (a stronger compression).");
            byte[] tail = _source.ReadBytes(avb.VbmetaOffset, (int)(_source.Length - avb.VbmetaOffset));
            BinaryPrimitives.WriteUInt64BigEndian(tail.AsSpan(tail.Length - 64 + 12), (ulong)end);
            layout.Add(avb.VbmetaOffset, tail);
            warnings.Add("The boot image is signed with Android Verified Boot: the signature no longer matches the changed image. " +
                         "A device with a locked boot loader will refuse to boot it; with an unlocked one it boots with a warning.");
        }
        else if (_source.Length > _end)
        {
            byte[] tail = _source.ReadBytes(_end, (int)Math.Min(int.MaxValue, _source.Length - _end));
            int keep = tail.AsSpan().LastIndexOfAnyExcept((byte)0) + 1;
            if (keep > 0) layout.Add(end, tail.AsSpan(0, keep).ToArray());
            // A partition image padded to its partition size keeps that size when the new image fits.
            if (end + keep <= _source.Length) layout.Extend(_source.Length);
            else warnings.Add($"The boot image grew from {DiskText.Size(_source.Length)} to {DiskText.Size(end + keep)}: the partition must have room for it.");
        }
        if (_headerVersion == 4 && !_vendor && SectionOf("boot_signature").Size > 0)
            warnings.Add("The boot signature of the image (GKI) no longer matches the changed ramdisk.");
        layout.WriteRaw(output, progress, ct);
    }

}
