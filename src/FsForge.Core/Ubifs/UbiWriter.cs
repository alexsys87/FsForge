using System.Buffers.Binary;
using System.Text;
using FsForge.Core.Compression;

namespace FsForge.Core.Ubifs;

/// <summary>Parameters of a new UBI image; the counterparts of the ubinize options and of its configuration file.</summary>
public sealed class UbiOptions
{
    /// <summary>Size of a physical erase block of the flash (ubinize -p).</summary>
    public int PebSize { get; set; } = 128 * 1024;

    /// <summary>Minimal input/output unit of the flash (ubinize -m): the page size of NAND, 1 for NOR.</summary>
    public int MinIoSize { get; set; } = 2048;

    /// <summary>Sub-page size of NAND (ubinize -s); 0 when the flash has no sub-pages (the same as the min. I/O unit).</summary>
    public int SubPageSize { get; set; }

    /// <summary>Name of the volume the filesystem goes to (vol_name).</summary>
    public string VolumeName { get; set; } = "rootfs";

    /// <summary>Number of the volume (vol_id).</summary>
    public int VolumeId { get; set; }

    /// <summary>Size to reserve for the volume, in bytes (vol_size); 0 reserves what the filesystem needs.</summary>
    public long VolumeBytes { get; set; }

    /// <summary>Let the volume take all free space of the flash on first attach (vol_flags=autoresize).</summary>
    public bool AutoResize { get; set; } = true;

    public UbiOptions Clone() => (UbiOptions)MemberwiseClone();

    /// <summary>Offset of the volume identifier header in an erase block.</summary>
    public int VidHeaderOffset
    {
        get
        {
            int unit = SubPageSize > 0 ? SubPageSize : Math.Max(1, MinIoSize);
            return (UbiWriter.HeaderSize + unit - 1) / unit * unit;
        }
    }

    /// <summary>Offset of the data in an erase block.</summary>
    public int DataOffset
    {
        get
        {
            int unit = Math.Max(1, MinIoSize);
            return (VidHeaderOffset + UbiWriter.HeaderSize + unit - 1) / unit * unit;
        }
    }

    /// <summary>Size of a logical erase block: what a filesystem in a volume of this image gets.</summary>
    public int LebSize => PebSize - DataOffset;

    public void Validate()
    {
        if (MinIoSize < 1 || (MinIoSize & (MinIoSize - 1)) != 0) throw new FsException("The UBI min. I/O unit size must be a power of two.");
        if (SubPageSize < 0 || SubPageSize > 0 && ((SubPageSize & (SubPageSize - 1)) != 0 || SubPageSize > MinIoSize))
            throw new FsException("The UBI sub-page size must be a power of two that is not larger than the min. I/O unit.");
        if (PebSize < 4096 || PebSize > 16 << 20 || PebSize % MinIoSize != 0)
            throw new FsException("The UBI erase block size must be between 4 KiB and 16 MiB and a multiple of the min. I/O unit.");
        if (LebSize < 2048) throw new FsException("The UBI erase block is too small for the headers.");
        int nameBytes = Encoding.UTF8.GetByteCount(VolumeName);
        if (nameBytes is 0 or > 127 || VolumeName.Contains('\0')) throw new FsException("The UBI volume name must be 1 to 127 bytes long.");
        if (VolumeId < 0 || VolumeId >= Math.Min(128, LebSize / UbiWriter.VtblRecordSize)) throw new FsException("The UBI volume number is out of range.");
        if (VolumeBytes < 0) throw new FsException("The UBI volume size cannot be negative.");
    }
}

/// <summary>One volume of a UBI image to be written.</summary>
public sealed class UbiVolumeSpec
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public bool IsStatic { get; init; }

    /// <summary>The content; its length is the amount of data in the volume.</summary>
    public required IBlockSource Data { get; init; }

    /// <summary>Number of erase blocks to reserve; raised to what the content needs.</summary>
    public int ReservedLebs { get; init; }

    public bool AutoResize { get; init; }

    /// <summary>Tells which erase blocks to leave out (they read as erased flash); null keeps all that are not blank.</summary>
    public Func<int, bool>? IsStored { get; init; }

    internal int Alignment { get; init; } = 1;
    internal byte ExtraFlags { get; init; }
}

/// <summary>
/// Writer of UBI images, the counterpart of ubinize: an erase-counter and a volume-identifier header
/// in front of every block, and the volume table in the two blocks of the layout volume.
/// </summary>
public static class UbiWriter
{
    internal const int HeaderSize = 64, VtblRecordSize = 172;
    private const uint EcMagic = 0x55424923, VidMagic = 0x55424921;
    private const int LayoutVolumeId = 0x7FFFEFFF, MaxVolumes = 128;

    private static uint Crc(ReadOnlySpan<byte> data) => ~Crc32.Compute(data);

    /// <summary>Number of erase blocks the volume table will reserve for a volume.</summary>
    public static int ReservedLebsFor(UbiVolumeSpec v, int lebSize)
    {
        int usable = lebSize - lebSize % Math.Max(1, v.Alignment);
        return (int)Math.Max(v.ReservedLebs, (v.Data.Length + usable - 1) / usable);
    }

    /// <summary>Writes a UBI image with the given volumes at the current position of the stream.</summary>
    public static void Write(Stream output, int pebSize, int vidHeaderOffset, int dataOffset, uint imageSequence,
        IReadOnlyList<UbiVolumeSpec> volumes, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        int lebSize = pebSize - dataOffset;
        if (vidHeaderOffset < HeaderSize || dataOffset < vidHeaderOffset + HeaderSize || lebSize < VtblRecordSize)
            throw new FsException("Invalid UBI geometry.");
        int slots = Math.Min(MaxVolumes, lebSize / VtblRecordSize);
        byte[] peb = new byte[pebSize];
        ulong sqnum = 0;

        void WritePeb(int volumeId, int lnum, bool isStatic, int dataPad, int compat, int dataLength, int usedLebs)
        {
            Span<byte> p = peb;
            p[..dataOffset].Fill(0xFF);
            p[(dataOffset + dataLength)..].Fill(0xFF);
            Span<byte> ec = p[..HeaderSize];
            ec.Clear();
            BinaryPrimitives.WriteUInt32BigEndian(ec, EcMagic);
            ec[4] = 1;
            BinaryPrimitives.WriteUInt32BigEndian(ec[16..], (uint)vidHeaderOffset);
            BinaryPrimitives.WriteUInt32BigEndian(ec[20..], (uint)dataOffset);
            BinaryPrimitives.WriteUInt32BigEndian(ec[24..], imageSequence);
            BinaryPrimitives.WriteUInt32BigEndian(ec[60..], Crc(ec[..60]));

            Span<byte> vid = p.Slice(vidHeaderOffset, HeaderSize);
            vid.Clear();
            BinaryPrimitives.WriteUInt32BigEndian(vid, VidMagic);
            vid[4] = 1;
            vid[5] = (byte)(isStatic ? 2 : 1);
            vid[7] = (byte)compat;
            BinaryPrimitives.WriteUInt32BigEndian(vid[8..], (uint)volumeId);
            BinaryPrimitives.WriteUInt32BigEndian(vid[12..], (uint)lnum);
            if (isStatic)
            {
                // A static volume is checked when it is opened: every block carries its length and checksum.
                BinaryPrimitives.WriteUInt32BigEndian(vid[20..], (uint)dataLength);
                BinaryPrimitives.WriteUInt32BigEndian(vid[24..], (uint)usedLebs);
                BinaryPrimitives.WriteUInt32BigEndian(vid[32..], Crc(p.Slice(dataOffset, dataLength)));
            }
            BinaryPrimitives.WriteUInt32BigEndian(vid[28..], (uint)dataPad);
            BinaryPrimitives.WriteUInt64BigEndian(vid[40..], sqnum++);
            BinaryPrimitives.WriteUInt32BigEndian(vid[60..], Crc(vid[..60]));
            output.Write(peb, 0, pebSize);
        }

        // The volume table: a record per possible volume, empty ones included, each with its checksum.
        byte[] table = new byte[slots * VtblRecordSize];
        var seen = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (UbiVolumeSpec v in volumes)
        {
            if (v.Id < 0 || v.Id >= slots) throw new FsException($"The UBI volume number {v.Id} is out of range (0 to {slots - 1}).");
            if (!seen.Add(v.Id)) throw new FsException($"The UBI volume number {v.Id} is used twice.");
            if (v.Name.Length > 0 && !names.Add(v.Name)) throw new FsException($"The UBI volume name '{v.Name}' is used twice.");
            byte[] name = Encoding.UTF8.GetBytes(v.Name);
            if (name.Length > 127) throw new FsException("A UBI volume name is longer than 127 bytes.");
            Span<byte> r = table.AsSpan(v.Id * VtblRecordSize, VtblRecordSize);
            int alignment = Math.Max(1, v.Alignment);
            BinaryPrimitives.WriteUInt32BigEndian(r, (uint)ReservedLebsFor(v, lebSize));
            BinaryPrimitives.WriteUInt32BigEndian(r[4..], (uint)alignment);
            BinaryPrimitives.WriteUInt32BigEndian(r[8..], (uint)(lebSize % alignment));
            r[12] = (byte)(v.IsStatic ? 2 : 1);
            BinaryPrimitives.WriteUInt16BigEndian(r[14..], (ushort)name.Length);
            name.CopyTo(r[16..]);
            r[144] = (byte)((v.ExtraFlags & ~1) | (v.AutoResize ? 1 : 0));
        }
        for (int i = 0; i < slots; i++)
        {
            Span<byte> r = table.AsSpan(i * VtblRecordSize, VtblRecordSize);
            BinaryPrimitives.WriteUInt32BigEndian(r[168..], Crc(r[..168]));
        }
        for (int copy = 0; copy < 2; copy++)
        {
            table.CopyTo(peb.AsSpan(dataOffset));
            WritePeb(LayoutVolumeId, copy, false, 0, compat: 5, table.Length, 0);
        }

        long total = volumes.Sum(v => v.Data.Length), done = 0;
        foreach (UbiVolumeSpec v in volumes.OrderBy(v => v.Id))
        {
            int dataPad = lebSize % Math.Max(1, v.Alignment), usable = lebSize - dataPad;
            long length = v.Data.Length;
            int lebs = (int)((length + usable - 1) / usable);
            for (int lnum = 0; lnum < lebs; lnum++)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(usable, length - (long)lnum * usable);
                done += n;
                if (v.IsStored is not null && !v.IsStored(lnum) && !v.IsStatic) continue;
                Span<byte> data = peb.AsSpan(dataOffset, n);
                v.Data.ReadExactly((long)lnum * usable, data);
                // A blank block of a dynamic volume needs no place on the flash: an unmapped block reads the same.
                if (!v.IsStatic && data.IndexOfAnyExcept((byte)0xFF) < 0) continue;
                WritePeb(v.Id, lnum, v.IsStatic, dataPad, 0, n, lebs);
                progress?.Report(new ProgressInfo("data", done, total, v.Name));
            }
        }
    }
}
