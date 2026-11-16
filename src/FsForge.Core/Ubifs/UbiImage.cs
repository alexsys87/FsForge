using System.Buffers.Binary;
using System.Text;
using FsForge.Core.Compression;
using FsForge.Core.Images;

namespace FsForge.Core.Ubifs;

/// <summary>One volume of a UBI image.</summary>
public sealed class UbiVolumeInfo
{
    public int Id { get; init; }

    /// <summary>Name from the volume table ("rootfs", "rootfs_data", "kernel"); empty when the table has no record for it.</summary>
    public string Name { get; init; } = "";

    /// <summary>A static volume (read-only data with a checksum per block) as opposed to a dynamic one.</summary>
    public bool IsStatic { get; init; }

    /// <summary>Size the volume table reserves for the volume, in bytes.</summary>
    public long ReservedBytes { get; init; }

    /// <summary>Bytes of the volume that are present in the image: up to the end of its last stored block.</summary>
    public long Length { get; init; }

    /// <summary>Number of logical erase blocks that are present in the image.</summary>
    public int StoredBlocks { get; init; }

    /// <summary>The volume grows to fill the flash on first attach (the "autoresize" flag).</summary>
    public bool AutoResize { get; init; }

    /// <summary>Number of logical erase blocks the volume table reserves for the volume; 0 when the table has no record for it.</summary>
    public int ReservedLebs { get; init; }

    /// <summary>Usable size of a logical erase block of this volume (the erase block without headers and alignment padding).</summary>
    public int LebSize { get; init; }

    internal int Alignment { get; init; } = 1;
    internal byte Flags { get; init; }

    public PartitionContent Content { get; init; }

    /// <summary>Exact kind of the content when known ("UBIFS", "SquashFS"...).</summary>
    public string ContentName { get; init; } = "";

    /// <summary>The volume holds a filesystem this program can read.</summary>
    public bool IsReadable => Content is PartitionContent.SquashFs or PartitionContent.Ext or PartitionContent.Ubifs or PartitionContent.Erofs
        or PartitionContent.Cramfs or PartitionContent.Romfs;

    internal int DataPad { get; init; }
    internal int[] Map { get; init; } = Array.Empty<int>();     // logical block -> physical block, -1 when not stored
    internal int LastBlockBytes { get; init; }                  // static volumes: bytes used in the last block

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(Id).Append(": ");
        if (Name.Length > 0) sb.Append('"').Append(Name).Append("\", ");
        sb.Append(ContentName.Length > 0 ? ContentName : "unknown content");
        sb.Append(", ").Append(DiskText.Size(Length));
        sb.Append(IsStatic ? ", static" : ", dynamic");
        if (AutoResize) sb.Append(", autoresize");
        return sb.ToString();
    }
}

/// <summary>
/// A UBI image (the output of ubinize, or a dump of a NAND/NOR partition without out-of-band data):
/// physical erase blocks with erase-counter and volume-identifier headers, which map to the
/// logical erase blocks of one or more volumes.
/// </summary>
public sealed class UbiImage
{
    private const uint EcMagic = 0x55424923;        // "UBI#"
    private const uint VidMagic = 0x55424921;       // "UBI!"
    private const int HeaderSize = 64;
    private const int LayoutVolumeId = 0x7FFFEFFF;
    private const int InternalVolumeStart = 0x7FFFEFFF;
    private const int VtblRecordSize = 172;
    private const int MaxVolumes = 128;
    private const int MinPebSize = 4096, MaxPebSize = 16 << 20, MaxLebNumber = 1 << 22;

    private readonly IBlockSource _source;
    private readonly int _dataOffset;

    private UbiImage(IBlockSource source, int pebSize, int dataOffset)
    {
        _source = source;
        PebSize = pebSize;
        _dataOffset = dataOffset;
    }

    /// <summary>Offset of the volume identifier header in an erase block (the sub-page size on NAND with sub-pages).</summary>
    public int VidHeaderOffset { get; private set; }

    /// <summary>Offset of the data in an erase block.</summary>
    public int DataOffset => _dataOffset;

    /// <summary>Number that all erase blocks of one image share.</summary>
    public uint ImageSequence { get; private set; }

    /// <summary>
    /// The min. I/O unit a filesystem in this image has to be made for, as far as the header offsets tell:
    /// the page size of NAND, 1 for NOR flash.
    /// </summary>
    public int GuessMinIoSize()
    {
        if (VidHeaderOffset == HeaderSize) return 1;                            // NOR: the headers follow each other
        return _dataOffset == 2 * VidHeaderOffset ? VidHeaderOffset : _dataOffset;   // without and with sub-pages
    }

    /// <summary>Size of a physical erase block.</summary>
    public int PebSize { get; }

    /// <summary>Number of physical erase blocks in the image.</summary>
    public int PebCount { get; private set; }

    /// <summary>Size of a logical erase block of a volume without alignment padding.</summary>
    public int LebSize => PebSize - _dataOffset;

    public IReadOnlyList<UbiVolumeInfo> Volumes { get; private set; } = Array.Empty<UbiVolumeInfo>();

    /// <summary>Non-fatal problems found while scanning.</summary>
    public List<string> Warnings { get; } = new();

    public IReadOnlyList<UbiVolumeInfo> Readable => Volumes.Where(v => v.IsReadable).ToList();

    /// <summary>The volume to open when nobody was asked: the readable one that holds the most data (the root filesystem, as a rule).</summary>
    public UbiVolumeInfo? PickDefault() => Volumes.Where(v => v.IsReadable).MaxBy(v => v.Length);

    /// <summary>Finds a volume by name or by number.</summary>
    public UbiVolumeInfo? Find(string nameOrId) =>
        Volumes.FirstOrDefault(v => v.Name.Length > 0 && v.Name == nameOrId) ??
        (int.TryParse(nameOrId, out int id) ? Volumes.FirstOrDefault(v => v.Id == id) : null);

    private static uint HeaderCrc(ReadOnlySpan<byte> data) => ~Crc32.Compute(data);

    private static bool ValidEc(ReadOnlySpan<byte> h) =>
        BinaryPrimitives.ReadUInt32BigEndian(h) == EcMagic &&
        HeaderCrc(h[..60]) == BinaryPrimitives.ReadUInt32BigEndian(h[60..]);

    /// <summary>Quick check: the data starts with a valid erase-counter header.</summary>
    public static bool IsUbi(IBlockSource source)
    {
        if (source.Length < 2 * MinPebSize) return false;
        Span<byte> h = stackalloc byte[HeaderSize];
        source.ReadExactly(0, h);
        return ValidEc(h);
    }

    /// <summary>
    /// Searches a firmware file for the beginning of a UBI image (factory images put a kernel in front of it);
    /// returns its offset or -1.
    /// </summary>
    public static long FindEmbedded(IBlockSource source, long startOffset, long maxScan, CancellationToken ct = default)
    {
        // UBI lives in whole erase blocks, so it starts at a multiple of the smallest sensible block size.
        const int step = 1024;
        long end = Math.Min(source.Length - 2 * MinPebSize, startOffset + maxScan);
        byte[] buf = new byte[1 << 20];
        Span<byte> h = stackalloc byte[HeaderSize];
        for (long pos = (startOffset + step - 1) / step * step; pos < end;)
        {
            ct.ThrowIfCancellationRequested();
            int n = source.Read(pos, buf);
            if (n < HeaderSize) break;
            for (int i = 0; i + HeaderSize <= n; i += step)
            {
                if (buf[i] != 0x55 || buf[i + 1] != 0x42 || buf[i + 2] != 0x49 || buf[i + 3] != 0x23) continue;
                if (ValidEc(buf.AsSpan(i, HeaderSize))) return pos + i;
            }
            pos += n / step * step;
            if (n < step) break;
        }
        return -1;
    }

    /// <summary>Scans the erase blocks of a UBI image and reads its volume table.</summary>
    public static UbiImage Read(IBlockSource source, CancellationToken ct = default)
    {
        byte[] ec = source.ReadBytes(0, HeaderSize);
        if (!ValidEc(ec)) throw new FsException("Not a UBI image (bad erase-counter header).");
        if (ec[4] != 1) throw new FsException($"UBI version {ec[4]} is not supported (only 1).");
        int vidOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(ec.AsSpan(16));
        int dataOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(ec.AsSpan(20));
        if (vidOffset < HeaderSize || dataOffset < vidOffset + HeaderSize || dataOffset > MaxPebSize / 2)
            throw new FsException("Corrupt UBI image: invalid header offsets.");

        int pebSize = FindPebSize(source, dataOffset);
        var ubi = new UbiImage(source, pebSize, dataOffset);
        ubi.Scan(vidOffset, ct);
        return ubi;
    }

    /// <summary>The erase block size is not recorded anywhere: it is the distance to the next erase-counter header.</summary>
    private static int FindPebSize(IBlockSource source, int dataOffset)
    {
        byte[] h = new byte[HeaderSize];
        // Erase blocks are powers of two on NAND and NOR alike; data flash can have other sizes.
        for (int size = MinPebSize; size <= MaxPebSize; size <<= 1)
        {
            if (size <= dataOffset || source.Length < size + (long)HeaderSize) continue;
            source.ReadExactly(size, h);
            if (ValidEc(h)) return size;
        }
        for (long size = Math.Max(MinPebSize, (dataOffset + 511) / 512 * 512); size <= MaxPebSize && size + HeaderSize <= source.Length; size += 512)
        {
            source.ReadExactly(size, h);
            if (ValidEc(h)) return (int)size;
        }
        throw new FsException("The size of the erase blocks of this UBI image cannot be determined: no second erase-counter header was found. " +
                              "A flash dump that includes out-of-band (spare) data is not supported.");
    }

    private sealed class RawVolume
    {
        public readonly Dictionary<int, (int Peb, ulong Sqnum)> Blocks = new();
        public bool Static;
        public int DataPad, UsedBlocks, LastBlockBytes;
    }

    private void Scan(int vidOffset, CancellationToken ct)
    {
        VidHeaderOffset = vidOffset;
        PebCount = (int)Math.Min(int.MaxValue, _source.Length / PebSize);
        if (_source.Length % PebSize != 0)
            Warnings.Add($"The size of the UBI image is not a multiple of its erase block size ({PebSize} bytes); the incomplete last block was ignored.");

        var volumes = new Dictionary<int, RawVolume>();
        int badBlocks = 0, foreign = 0;
        byte[] head = new byte[vidOffset + HeaderSize];
        uint imageSeq = 0;
        for (int peb = 0; peb < PebCount; peb++)
        {
            if ((peb & 0xFF) == 0) ct.ThrowIfCancellationRequested();
            _source.ReadExactly((long)peb * PebSize, head);
            Span<byte> ec = head.AsSpan(0, HeaderSize), vid = head.AsSpan(vidOffset, HeaderSize);
            if (!ValidEc(ec))
            {
                // Erased blocks of a flash dump carry no headers at all.
                if (ec.IndexOfAnyExcept((byte)0xFF) >= 0) badBlocks++;
                continue;
            }
            // Every block of one UBI image carries the same sequence number. Blocks with another one belong
            // to a different image in the same dump (a second firmware slot) and must not be mixed in.
            uint seq = BinaryPrimitives.ReadUInt32BigEndian(ec[24..]);
            if (peb == 0) ImageSequence = imageSeq = seq;
            else if (seq != imageSeq)
            {
                foreign++;
                continue;
            }
            if (BinaryPrimitives.ReadUInt32BigEndian(vid) != VidMagic)
            {
                if (vid.IndexOfAnyExcept((byte)0xFF) >= 0) badBlocks++;
                continue;                                    // a free block
            }
            if (HeaderCrc(vid[..60]) != BinaryPrimitives.ReadUInt32BigEndian(vid[60..]))
            {
                badBlocks++;
                continue;
            }

            int volId = (int)BinaryPrimitives.ReadUInt32BigEndian(vid[8..]);
            int lnum = (int)BinaryPrimitives.ReadUInt32BigEndian(vid[12..]);
            ulong sqnum = BinaryPrimitives.ReadUInt64BigEndian(vid[40..]);
            // A volume can have more logical blocks than the image has physical ones: blank blocks are not stored.
            if (lnum < 0 || lnum >= MaxLebNumber) { badBlocks++; continue; }
            if (!volumes.TryGetValue(volId, out RawVolume? v)) volumes[volId] = v = new RawVolume();
            v.Static = vid[5] == 2;
            v.DataPad = (int)BinaryPrimitives.ReadUInt32BigEndian(vid[28..]);
            int dataSize = (int)BinaryPrimitives.ReadUInt32BigEndian(vid[20..]);
            if (v.Static)
            {
                int used = (int)BinaryPrimitives.ReadUInt32BigEndian(vid[24..]);
                v.UsedBlocks = Math.Max(v.UsedBlocks, used);
                if (lnum == used - 1) v.LastBlockBytes = dataSize;
            }

            if (v.Blocks.TryGetValue(lnum, out var known))
            {
                // Two copies of a block: wear levelling was interrupted. The newer one counts, unless it
                // is an unfinished copy (its data checksum does not match).
                bool newer = sqnum > known.Sqnum;
                if (newer && vid[6] != 0 && !CopyIsComplete(peb, dataSize, BinaryPrimitives.ReadUInt32BigEndian(vid[32..]))) newer = false;
                if (!newer) continue;
            }
            v.Blocks[lnum] = (peb, sqnum);
        }
        if (foreign > 0)
            Warnings.Add($"{foreign} erase block(s) in the file belong to another UBI image and were ignored: only the first UBI image of the file is read.");
        if (badBlocks > 0)
            Warnings.Add($"{badBlocks} erase block(s) of the UBI image have damaged headers and were ignored.");

        Dictionary<int, (string Name, long Reserved, bool AutoResize, bool Static, int Alignment, byte Flags)> table = ReadVolumeTable(volumes);
        var list = new List<UbiVolumeInfo>();
        foreach (int id in volumes.Keys.Where(k => k < InternalVolumeStart).Union(table.Keys).Order())
        {
            volumes.TryGetValue(id, out RawVolume? raw);
            table.TryGetValue(id, out var rec);
            raw ??= new RawVolume { Static = rec.Static };
            int lebSize = LebSize - raw.DataPad;
            int count = raw.Blocks.Count == 0 ? 0 : raw.Blocks.Keys.Max() + 1;
            if (raw.Static && raw.UsedBlocks > 0) count = Math.Min(count, raw.UsedBlocks);
            int[] map = new int[count];
            Array.Fill(map, -1);
            foreach ((int lnum, (int peb, ulong _)) in raw.Blocks)
                if (lnum < count) map[lnum] = peb;
            int last = raw.Static && raw.LastBlockBytes > 0 && count == raw.UsedBlocks ? Math.Min(raw.LastBlockBytes, lebSize) : lebSize;
            long length = count == 0 ? 0 : (long)(count - 1) * lebSize + last;

            var probe = new UbiVolumeInfo { Id = id, DataPad = raw.DataPad, Map = map, LastBlockBytes = last, Length = length };
            (PartitionContent content, string contentName) = Identify(OpenVolume(probe));
            list.Add(new UbiVolumeInfo
            {
                Id = id,
                Name = rec.Name ?? "",
                IsStatic = raw.Static,
                ReservedBytes = rec.Reserved * lebSize,
                ReservedLebs = (int)rec.Reserved,
                LebSize = lebSize,
                Alignment = Math.Max(1, rec.Alignment),
                Flags = rec.Flags,
                Length = length,
                StoredBlocks = raw.Blocks.Count,
                AutoResize = rec.AutoResize,
                Content = content,
                ContentName = contentName,
                DataPad = raw.DataPad,
                Map = map,
                LastBlockBytes = last,
            });
        }
        Volumes = list;
    }

    private bool CopyIsComplete(int peb, int dataSize, uint dataCrc)
    {
        if (dataSize <= 0 || dataSize > LebSize) return false;
        byte[] data = _source.ReadBytes((long)peb * PebSize + _dataOffset, dataSize);
        return HeaderCrc(data) == dataCrc;
    }

    private Dictionary<int, (string Name, long Reserved, bool AutoResize, bool Static, int Alignment, byte Flags)> ReadVolumeTable(Dictionary<int, RawVolume> volumes)
    {
        var result = new Dictionary<int, (string, long, bool, bool, int, byte)>();
        if (!volumes.TryGetValue(LayoutVolumeId, out RawVolume? layout) || layout.Blocks.Count == 0)
        {
            Warnings.Add("The UBI image has no volume table; the names of its volumes are unknown.");
            return result;
        }
        int slots = Math.Min(MaxVolumes, LebSize / VtblRecordSize);
        // The table is kept in two copies; take the first one whose records all check out.
        foreach ((int _, (int peb, ulong _)) in layout.Blocks.OrderBy(b => b.Key))
        {
            byte[] data = _source.ReadBytes((long)peb * PebSize + _dataOffset, slots * VtblRecordSize);
            bool valid = true;
            result.Clear();
            for (int i = 0; i < slots && valid; i++)
            {
                ReadOnlySpan<byte> r = data.AsSpan(i * VtblRecordSize, VtblRecordSize);
                if (HeaderCrc(r[..168]) != BinaryPrimitives.ReadUInt32BigEndian(r[168..])) { valid = false; break; }
                uint reserved = BinaryPrimitives.ReadUInt32BigEndian(r);
                if (reserved == 0) continue;                 // an empty slot
                int nameLength = Math.Min(127, (int)BinaryPrimitives.ReadUInt16BigEndian(r[14..]));
                string name = PosixName.Decode(r.Slice(16, nameLength));
                result[i] = (name, reserved, (r[144] & 1) != 0, r[12] == 2, (int)BinaryPrimitives.ReadUInt32BigEndian(r[4..]), r[144]);
            }
            if (valid) return result;
        }
        result.Clear();
        Warnings.Add("The volume table of the UBI image is damaged; the names of its volumes are unknown.");
        return result;
    }

    private static (PartitionContent, string) Identify(IBlockSource volume)
    {
        if (volume.Length == 0) return (PartitionContent.Empty, "empty");
        if (UbifsReader.IsUbifs(volume)) return (PartitionContent.Ubifs, "UBIFS");
        (PartitionContent content, string name, string _) = PartitionTable.Identify(volume, 0, volume.Length);
        if (content == PartitionContent.Unknown)
        {
            byte[] head = new byte[Math.Min(4096, volume.Length)];
            volume.ReadExactly(0, head);
            if (head.AsSpan().IndexOfAnyExcept((byte)0xFF) < 0) return (PartitionContent.Empty, "empty");
            if (BinaryPrimitives.ReadUInt32BigEndian(head) == 0x27051956) return (PartitionContent.Unknown, "U-Boot image");
            if (BinaryPrimitives.ReadUInt32BigEndian(head) == 0xD00DFEED) return (PartitionContent.Unknown, "FIT image");
        }
        return (content, name);
    }

    /// <summary>True when the erase block of a volume is present in the image (false: it reads as erased flash).</summary>
    public bool IsStored(UbiVolumeInfo volume, int lnum) => lnum >= 0 && lnum < volume.Map.Length && volume.Map[lnum] >= 0;

    /// <summary>The content of a volume as one run of bytes. Blocks that are not in the image read as erased flash (0xFF).</summary>
    public IBlockSource OpenVolume(UbiVolumeInfo volume) => new VolumeSource(this, volume);

    private sealed class VolumeSource(UbiImage ubi, UbiVolumeInfo volume) : IBlockSource
    {
        private readonly int _lebSize = ubi.LebSize - volume.DataPad;

        public long Length => volume.Length;

        public int Read(long offset, Span<byte> buffer)
        {
            if (offset < 0 || offset >= Length) return 0;
            if (buffer.Length > Length - offset) buffer = buffer[..(int)(Length - offset)];
            int total = 0;
            while (buffer.Length > 0)
            {
                int lnum = (int)(offset / _lebSize), inBlock = (int)(offset % _lebSize);
                int n = Math.Min(buffer.Length, _lebSize - inBlock);
                Span<byte> part = buffer[..n];
                int peb = volume.Map[lnum];
                if (peb < 0) part.Fill(0xFF);
                else ubi._source.ReadExactly((long)peb * ubi.PebSize + ubi._dataOffset + inBlock, part);
                buffer = buffer[n..];
                offset += n;
                total += n;
            }
            return total;
        }

        public void Dispose() { }
    }
}
