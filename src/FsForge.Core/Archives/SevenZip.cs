using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FsForge.Core.Compression;

namespace FsForge.Core.Archives;

/// <summary>A file or folder inside a zip or 7z archive.</summary>
public sealed class ArchiveEntry
{
    public string Name { get; init; } = "";
    public long Size { get; init; }
    public bool IsDirectory { get; init; }
    public DateTime? LastWriteTimeUtc { get; init; }
    public uint? Attributes { get; init; }

    /// <summary>The compressed size where the format records it per entry, else null.</summary>
    public long? CompressedSize { get; init; }

    internal int Index { get; init; }
    internal int Folder { get; init; } = -1;
    internal long FolderOffset { get; init; }
    internal uint? Crc { get; init; }

    public override string ToString() => IsDirectory ? Name + "/" : $"{Name} ({Size} bytes)";
}

/// <summary>
/// Reader of 7z archives: plain and compressed headers, solid and non-solid folders, the coders 7-Zip uses
/// for data (LZMA2, LZMA, Deflate, BZip2, Copy, Delta, the branch converters for executables) and the
/// Zstandard coder of the 7-Zip ZS fork. CRCs of the headers and of every file are checked.
/// </summary>
public sealed class SevenZipArchive : IDisposable
{
    public static readonly byte[] Signature = { (byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C };

    // Property identifiers of the 7z header.
    private const int KEnd = 0, KHeader = 1, KArchiveProperties = 2, KAdditionalStreamsInfo = 3, KMainStreamsInfo = 4,
        KFilesInfo = 5, KPackInfo = 6, KUnpackInfo = 7, KSubStreamsInfo = 8, KSize = 9, KCrc = 10, KFolder = 11,
        KCodersUnpackSize = 12, KNumUnpackStream = 13, KEmptyStream = 14, KEmptyFile = 15, KAnti = 16, KName = 17,
        KMTime = 20, KWinAttributes = 21, KEncodedHeader = 23, KDummy = 25;

    /// <summary>Largest stream decoded in memory (LZMA with a header, branch converters).</summary>
    private const long MemoryLimit = 512L << 20;

    private readonly Stream _file;
    private readonly bool _leaveOpen;
    private StreamsInfo _main = new();
    private readonly List<ArchiveEntry> _entries = new();

    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    /// <summary>Names of the coders used for file data, for display ("LZMA2", "BCJ + LZMA").</summary>
    public string Methods { get; private set; } = "";

    /// <summary>True when the archive keeps several files in one compressed stream.</summary>
    public bool IsSolid { get; private set; }

    private SevenZipArchive(Stream file, bool leaveOpen)
    {
        _file = file;
        _leaveOpen = leaveOpen;
    }

    public static bool IsSevenZip(ReadOnlySpan<byte> head) => head.Length >= 6 && head[..6].SequenceEqual(Signature);

    public static SevenZipArchive Open(Stream seekable, bool leaveOpen = false)
    {
        var a = new SevenZipArchive(seekable, leaveOpen);
        try
        {
            a.ReadArchive();
            return a;
        }
        catch (Exception ex) when (ex is EndOfStreamException or IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            a.Dispose();
            throw new FsException("Corrupt 7z archive: the header is damaged.", ex);
        }
        catch
        {
            a.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (!_leaveOpen) _file.Dispose();
    }

    // ------------------------------------------------------------ structures

    private sealed class Coder
    {
        public byte[] Id = Array.Empty<byte>();
        public int NumIn = 1, NumOut = 1;
        public byte[] Props = Array.Empty<byte>();
        public ulong MethodId
        {
            get
            {
                ulong v = 0;
                foreach (byte b in Id) v = (v << 8) | b;
                return v;
            }
        }
    }

    private sealed class Folder
    {
        public readonly List<Coder> Coders = new();
        public readonly List<(int In, int Out)> Bind = new();
        public readonly List<int> Packed = new();
        public readonly List<long> UnpackSizes = new();
        public uint? Crc;
        public int FirstPack;

        public int MainOut
        {
            get
            {
                int total = Coders.Sum(c => c.NumOut);
                for (int i = 0; i < total; i++)
                    if (!Bind.Any(b => b.Out == i)) return i;
                throw new FsException("Corrupt 7z archive: a folder has no output.");
            }
        }

        public long UnpackSize => UnpackSizes[MainOut];
    }

    private sealed class StreamsInfo
    {
        public long PackPos;
        public readonly List<long> PackSizes = new();
        public readonly List<Folder> Folders = new();
        public readonly List<int> NumStreams = new();
        public readonly List<long> SubSizes = new();
        public readonly List<uint?> SubCrcs = new();
    }

    /// <summary>Cursor over a header buffer.</summary>
    private sealed class Reader(byte[] data, int pos = 0, int end = -1)
    {
        public readonly byte[] Data = data;
        public int Pos = pos;
        public readonly int End = end < 0 ? data.Length : end;

        public byte Byte()
        {
            if (Pos >= End) throw new FsException("Corrupt 7z archive: the header is truncated.");
            return Data[Pos++];
        }

        public ulong Number()
        {
            int first = Byte();
            int mask = 0x80;
            ulong value = 0;
            for (int i = 0; i < 8; i++)
            {
                if ((first & mask) == 0)
                {
                    ulong high = (ulong)(first & (mask - 1));
                    return value | (high << (8 * i));
                }
                value |= (ulong)Byte() << (8 * i);
                mask >>= 1;
            }
            return value;
        }

        public int Int(int max = int.MaxValue)
        {
            ulong v = Number();
            if (v > (ulong)max) throw new FsException("Corrupt 7z archive: a count in the header is too large.");
            return (int)v;
        }

        public long Long()
        {
            ulong v = Number();
            if (v > long.MaxValue / 2) throw new FsException("Corrupt 7z archive: a size in the header is too large.");
            return (long)v;
        }

        public uint UInt32()
        {
            if (Pos + 4 > End) throw new FsException("Corrupt 7z archive: the header is truncated.");
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(Pos));
            Pos += 4;
            return v;
        }

        public ulong UInt64()
        {
            if (Pos + 8 > End) throw new FsException("Corrupt 7z archive: the header is truncated.");
            ulong v = BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan(Pos));
            Pos += 8;
            return v;
        }

        public ReadOnlySpan<byte> Bytes(int n)
        {
            if (n < 0 || Pos + n > End) throw new FsException("Corrupt 7z archive: the header is truncated.");
            var s = Data.AsSpan(Pos, n);
            Pos += n;
            return s;
        }

        public bool[] Bits(int n)
        {
            var v = new bool[n];
            int b = 0, mask = 0;
            for (int i = 0; i < n; i++)
            {
                if (mask == 0) { b = Byte(); mask = 0x80; }
                v[i] = (b & mask) != 0;
                mask >>= 1;
            }
            return v;
        }

        public bool[] Defined(int n)
        {
            int all = Byte();
            if (all != 0) return Enumerable.Repeat(true, n).ToArray();
            return Bits(n);
        }

        public uint?[] Digests(int n)
        {
            bool[] def = Defined(n);
            var d = new uint?[n];
            for (int i = 0; i < n; i++) if (def[i]) d[i] = UInt32();
            return d;
        }

        public void Expect(int id)
        {
            ulong v = Number();
            if (v != (ulong)id) throw new FsException($"Corrupt 7z archive: unexpected property {v} in the header.");
        }
    }

    // ------------------------------------------------------------ header

    private void ReadArchive()
    {
        byte[] sig = new byte[32];
        _file.Position = 0;
        _file.ReadExactly(sig);
        if (!IsSevenZip(sig)) throw new FsException("Not a 7z archive.");
        if (sig[6] != 0) throw new FsException($"The 7z archive has format version {sig[6]}.{sig[7]}, which is not supported.");
        uint startCrc = BinaryPrimitives.ReadUInt32LittleEndian(sig.AsSpan(8));
        if (Crc32.Compute(sig.AsSpan(12, 20)) != startCrc)
            throw new FsException("Corrupt 7z archive: the start header is damaged (CRC mismatch).");
        long nextOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(sig.AsSpan(12));
        long nextSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(sig.AsSpan(20));
        uint nextCrc = BinaryPrimitives.ReadUInt32LittleEndian(sig.AsSpan(28));
        if (nextSize == 0) return;      // an empty archive
        if (nextOffset < 0 || nextSize < 0 || nextSize > 1 << 30 || 32 + nextOffset + nextSize > _file.Length)
            throw new FsException("Corrupt 7z archive: the header lies outside the file (the archive may be truncated).");
        byte[] header = new byte[nextSize];
        _file.Position = 32 + nextOffset;
        _file.ReadExactly(header);
        if (Crc32.Compute(header) != nextCrc) throw new FsException("Corrupt 7z archive: the header CRC does not match.");

        for (int round = 0; round < 4; round++)
        {
            var r = new Reader(header);
            ulong id = r.Number();
            if (id == KHeader)
            {
                ReadHeader(r);
                return;
            }
            if (id != KEncodedHeader) throw new FsException("Corrupt 7z archive: unknown header type.");
            StreamsInfo si = ReadStreamsInfo(r);
            if (si.Folders.Count == 0) throw new FsException("Corrupt 7z archive: the compressed header has no data.");
            Folder f = si.Folders[0];
            if (f.UnpackSize > 1 << 30) throw new FsException("Corrupt 7z archive: the compressed header is too large.");
            byte[] decoded = new byte[f.UnpackSize];
            using (Stream s = OpenFolder(si, 0)) ReadAll(s, decoded);
            if (f.Crc is { } crc && Crc32.Compute(decoded) != crc)
                throw new FsException("Corrupt 7z archive: the CRC of the compressed header does not match.");
            header = decoded;
        }
        throw new FsException("Corrupt 7z archive: the header is compressed too many times.");
    }

    private void ReadHeader(Reader r)
    {
        ulong id = r.Number();
        if (id == KArchiveProperties)
        {
            while (true)
            {
                ulong t = r.Number();
                if (t == KEnd) break;
                r.Bytes(r.Int());
            }
            id = r.Number();
        }
        if (id == KAdditionalStreamsInfo)
        {
            ReadStreamsInfo(r);
            id = r.Number();
        }
        if (id == KMainStreamsInfo)
        {
            _main = ReadStreamsInfo(r);
            id = r.Number();
        }
        if (id == KFilesInfo)
        {
            ReadFiles(r);
            id = r.Number();
        }
        if (id != KEnd) throw new FsException("Corrupt 7z archive: unexpected data at the end of the header.");
        var methods = _main.Folders.Select(f => string.Join(" + ", f.Coders.Select(c => MethodName(c.MethodId)))).Distinct();
        Methods = string.Join(", ", methods);
        IsSolid = _main.NumStreams.Any(n => n > 1);
    }

    private StreamsInfo ReadStreamsInfo(Reader r)
    {
        var si = new StreamsInfo();
        while (true)
        {
            ulong id = r.Number();
            switch (id)
            {
                case KEnd:
                    if (si.NumStreams.Count == 0) FillDefaultSubStreams(si);
                    return si;
                case KPackInfo:
                    si.PackPos = r.Long();
                    int numPack = r.Int(1 << 24);
                    while (true)
                    {
                        ulong t = r.Number();
                        if (t == KEnd) break;
                        if (t == KSize) for (int i = 0; i < numPack; i++) si.PackSizes.Add(r.Long());
                        else if (t == KCrc) r.Digests(numPack);
                        else throw new FsException("Corrupt 7z archive: unknown property in the pack information.");
                    }
                    if (si.PackSizes.Count != numPack) throw new FsException("Corrupt 7z archive: the pack sizes are missing.");
                    break;
                case KUnpackInfo:
                    ReadUnpackInfo(r, si);
                    break;
                case KSubStreamsInfo:
                    ReadSubStreams(r, si);
                    break;
                default:
                    throw new FsException("Corrupt 7z archive: unknown property in the stream information.");
            }
        }
    }

    private static void ReadUnpackInfo(Reader r, StreamsInfo si)
    {
        r.Expect(KFolder);
        int numFolders = r.Int(1 << 24);
        if (r.Byte() != 0) throw new FsException("The 7z archive keeps its folder list outside the header, which is not supported.");
        int packIndex = 0;
        for (int i = 0; i < numFolders; i++)
        {
            var f = new Folder { FirstPack = packIndex };
            int numCoders = r.Int(64);
            if (numCoders == 0) throw new FsException("Corrupt 7z archive: a folder has no coders.");
            for (int c = 0; c < numCoders; c++)
            {
                int flags = r.Byte();
                if ((flags & 0x80) != 0) throw new FsException("The 7z archive uses alternative coder methods, which are not supported.");
                var coder = new Coder { Id = r.Bytes(flags & 0x0F).ToArray() };
                if ((flags & 0x10) != 0)
                {
                    coder.NumIn = r.Int(64);
                    coder.NumOut = r.Int(64);
                }
                if ((flags & 0x20) != 0) coder.Props = r.Bytes(r.Int(1 << 16)).ToArray();
                f.Coders.Add(coder);
            }
            int totalOut = f.Coders.Sum(c => c.NumOut), totalIn = f.Coders.Sum(c => c.NumIn);
            for (int b = 0; b < totalOut - 1; b++) f.Bind.Add((r.Int(totalIn - 1), r.Int(totalOut - 1)));
            int numPacked = totalIn - (totalOut - 1);
            if (numPacked < 1) throw new FsException("Corrupt 7z archive: a folder has no packed stream.");
            if (numPacked == 1)
            {
                for (int k = 0; k < totalIn; k++)
                    if (!f.Bind.Any(b => b.In == k)) { f.Packed.Add(k); break; }
            }
            else
            {
                for (int k = 0; k < numPacked; k++) f.Packed.Add(r.Int(totalIn - 1));
            }
            packIndex += f.Packed.Count;
            si.Folders.Add(f);
        }
        r.Expect(KCodersUnpackSize);
        foreach (Folder f in si.Folders)
            for (int k = 0, n = f.Coders.Sum(c => c.NumOut); k < n; k++) f.UnpackSizes.Add(r.Long());
        while (true)
        {
            ulong t = r.Number();
            if (t == KEnd) break;
            if (t != KCrc) throw new FsException("Corrupt 7z archive: unknown property in the folder information.");
            uint?[] d = r.Digests(numFolders);
            for (int i = 0; i < numFolders; i++) si.Folders[i].Crc = d[i];
        }
    }

    private static void FillDefaultSubStreams(StreamsInfo si)
    {
        foreach (Folder f in si.Folders)
        {
            si.NumStreams.Add(1);
            si.SubSizes.Add(f.UnpackSize);
            si.SubCrcs.Add(f.Crc);
        }
    }

    private static void ReadSubStreams(Reader r, StreamsInfo si)
    {
        foreach (Folder _ in si.Folders) si.NumStreams.Add(1);
        ulong t = r.Number();
        if (t == KNumUnpackStream)
        {
            for (int i = 0; i < si.Folders.Count; i++) si.NumStreams[i] = r.Int(1 << 24);
            t = r.Number();
        }
        bool sizes = t == KSize;
        for (int i = 0; i < si.Folders.Count; i++)
        {
            int n = si.NumStreams[i];
            if (n == 0) continue;
            long sum = 0;
            if (sizes)
                for (int k = 0; k < n - 1; k++)
                {
                    long s = r.Long();
                    si.SubSizes.Add(s);
                    sum += s;
                }
            else if (n > 1) throw new FsException("Corrupt 7z archive: the sizes of the files in a folder are missing.");
            long last = si.Folders[i].UnpackSize - sum;
            if (last < 0) throw new FsException("Corrupt 7z archive: the files are larger than their folder.");
            si.SubSizes.Add(last);
        }
        if (sizes) t = r.Number();

        // CRCs: the folder CRC stands for a single file; the others are listed here.
        int unknown = 0;
        for (int i = 0; i < si.Folders.Count; i++)
            if (si.NumStreams[i] != 1 || si.Folders[i].Crc is null) unknown += si.NumStreams[i];
        uint?[] digests = Array.Empty<uint?>();
        while (t != KEnd)
        {
            if (t == KCrc) digests = r.Digests(unknown);
            else throw new FsException("Corrupt 7z archive: unknown property in the file stream information.");
            t = r.Number();
        }
        int di = 0;
        for (int i = 0; i < si.Folders.Count; i++)
        {
            int n = si.NumStreams[i];
            if (n == 1 && si.Folders[i].Crc is { } c) si.SubCrcs.Add(c);
            else for (int k = 0; k < n; k++) si.SubCrcs.Add(di < digests.Length ? digests[di++] : null);
        }
    }

    private void ReadFiles(Reader r)
    {
        int numFiles = r.Int(1 << 24);
        bool[] emptyStream = new bool[numFiles], emptyFile = Array.Empty<bool>();
        string[] names = new string[numFiles];
        var mtimes = new DateTime?[numFiles];
        var attrs = new uint?[numFiles];
        while (true)
        {
            ulong type = r.Number();
            if (type == KEnd) break;
            long size = r.Long();
            if (size > r.End - r.Pos) throw new FsException("Corrupt 7z archive: a file property is truncated.");
            var p = new Reader(r.Data, r.Pos, r.Pos + (int)size);
            r.Pos += (int)size;
            switch (type)
            {
                case KEmptyStream:
                    emptyStream = p.Bits(numFiles);
                    break;
                case KEmptyFile:
                    emptyFile = p.Bits(emptyStream.Count(x => x));
                    break;
                case KAnti:
                    if (p.Bits(emptyStream.Count(x => x)).Any(x => x))
                        throw new FsException("The 7z archive is an update that deletes files (anti-items), which is not supported.");
                    break;
                case KName:
                    if (p.Byte() != 0) throw new FsException("The 7z archive keeps its file names outside the header, which is not supported.");
                    for (int i = 0; i < numFiles; i++)
                    {
                        int start = p.Pos;
                        while (true)
                        {
                            ReadOnlySpan<byte> ch = p.Bytes(2);
                            if (ch[0] == 0 && ch[1] == 0) break;
                        }
                        names[i] = Encoding.Unicode.GetString(p.Data, start, p.Pos - 2 - start).Replace('\\', '/');
                    }
                    break;
                case KMTime:
                    {
                        bool[] def = p.Defined(numFiles);
                        if (p.Byte() != 0) throw new FsException("The 7z archive keeps file times outside the header, which is not supported.");
                        for (int i = 0; i < numFiles; i++)
                            if (def[i])
                            {
                                ulong ft = p.UInt64();
                                mtimes[i] = ft < 2650467743999999999UL ? DateTime.FromFileTimeUtc((long)ft) : null;
                            }
                        break;
                    }
                case KWinAttributes:
                    {
                        bool[] def = p.Defined(numFiles);
                        if (p.Byte() != 0) throw new FsException("The 7z archive keeps file attributes outside the header, which is not supported.");
                        for (int i = 0; i < numFiles; i++) if (def[i]) attrs[i] = p.UInt32();
                        break;
                    }
                default:
                    break;      // creation and access times, comments, padding (kDummy) and the start positions
            }
        }

        int stream = 0, folder = 0, inFolder = 0, empty = 0;
        long offset = 0;
        for (int i = 0; i < numFiles; i++)
        {
            string name = names[i] ?? $"file{i}";
            if (emptyStream[i])
            {
                bool isFile = empty < emptyFile.Length && emptyFile[empty];
                empty++;
                bool dir = !isFile || (attrs[i] is { } a && (a & 0x10) != 0);
                _entries.Add(new ArchiveEntry { Index = i, Name = name, IsDirectory = dir, LastWriteTimeUtc = mtimes[i], Attributes = attrs[i] });
                continue;
            }
            while (folder < _main.NumStreams.Count && inFolder >= _main.NumStreams[folder])
            {
                folder++;
                inFolder = 0;
                offset = 0;
            }
            if (folder >= _main.Folders.Count || stream >= _main.SubSizes.Count)
                throw new FsException("Corrupt 7z archive: there are more files than data streams.");
            long size = _main.SubSizes[stream];
            _entries.Add(new ArchiveEntry
            {
                Index = i, Name = name, Size = size, LastWriteTimeUtc = mtimes[i], Attributes = attrs[i],
                Folder = folder, FolderOffset = offset, Crc = _main.SubCrcs[stream],
                CompressedSize = _main.NumStreams[folder] == 1 ? _main.Folders[folder].Packed.Count == 1 ? _main.PackSizes[_main.Folders[folder].FirstPack] : null : null,
            });
            offset += size;
            inFolder++;
            stream++;
        }
    }

    // ------------------------------------------------------------ data

    /// <summary>Opens the contents of one entry; the CRC is checked when the end is read.</summary>
    public Stream OpenEntry(ArchiveEntry entry)
    {
        if (entry.Folder < 0) return new MemoryStream(Array.Empty<byte>(), false);
        Stream folder = OpenFolder(_main, entry.Folder);
        try
        {
            Skip(folder, entry.FolderOffset);
            return new CheckedStream(folder, entry.Size, entry.Crc, entry.Name, ownsInner: true);
        }
        catch
        {
            folder.Dispose();
            throw;
        }
    }

    /// <summary>Hands every entry and its contents to <paramref name="visit"/> in archive order, decoding each folder once.</summary>
    public void ReadAll(Action<ArchiveEntry, Stream> visit, CancellationToken ct = default)
    {
        Stream? folder = null;
        int current = -1;
        long position = 0;
        try
        {
            foreach (ArchiveEntry e in _entries)
            {
                ct.ThrowIfCancellationRequested();
                if (e.Folder < 0)
                {
                    using var none = new MemoryStream(Array.Empty<byte>(), false);
                    visit(e, none);
                    continue;
                }
                if (e.Folder != current)
                {
                    folder?.Dispose();
                    folder = OpenFolder(_main, e.Folder);
                    current = e.Folder;
                    position = 0;
                }
                Skip(folder!, e.FolderOffset - position);
                using (var data = new CheckedStream(folder!, e.Size, e.Crc, e.Name, ownsInner: false))
                {
                    visit(e, data);
                    data.Drain();
                }
                position = e.FolderOffset + e.Size;
            }
        }
        finally
        {
            folder?.Dispose();
        }
    }

    private static void Skip(Stream s, long count)
    {
        if (count < 0) throw new FsException("Corrupt 7z archive: the files of a folder overlap.");
        byte[] buf = new byte[1 << 16];
        while (count > 0)
        {
            int n = s.Read(buf, 0, (int)Math.Min(buf.Length, count));
            if (n <= 0) throw new FsException("Corrupt 7z archive: a folder ends early.");
            count -= n;
        }
    }

    private static void ReadAll(Stream s, byte[] dst)
    {
        int done = 0;
        while (done < dst.Length)
        {
            int n = s.Read(dst, done, dst.Length - done);
            if (n <= 0) throw new FsException("Corrupt 7z archive: a stream ends early.");
            done += n;
        }
    }

    private Stream OpenFolder(StreamsInfo si, int index)
    {
        Folder f = si.Folders[index];
        long start = 32 + si.PackPos;
        for (int i = 0; i < f.FirstPack; i++) start += si.PackSizes[i];
        var packs = new List<Stream>();
        long pos = start;
        for (int i = 0; i < f.Packed.Count; i++)
        {
            int pi = f.FirstPack + i;
            if (pi >= si.PackSizes.Count) throw new FsException("Corrupt 7z archive: a packed stream is missing.");
            long size = si.PackSizes[pi];
            if (pos + size > _file.Length) throw new FsException("The 7z archive is truncated: its data lies beyond the end of the file.");
            packs.Add(new WindowStream(_file, pos, size));
            pos += size;
        }
        return OpenOut(f, f.MainOut, packs, 0);
    }

    private Stream OpenOut(Folder f, int outIndex, List<Stream> packs, int depth)
    {
        if (depth > 8) throw new FsException("Corrupt 7z archive: the coders of a folder form a loop.");
        int outBase = 0, inBase = 0;
        foreach (Coder c in f.Coders)
        {
            if (outIndex < outBase + c.NumOut)
            {
                if (c.NumIn != 1 || c.NumOut != 1)
                    throw new FsException($"The 7z archive uses the coder {MethodName(c.MethodId)}, which is not supported.");
                int inIndex = inBase;
                Stream src;
                int bound = f.Bind.FindIndex(b => b.In == inIndex);
                if (bound >= 0) src = OpenOut(f, f.Bind[bound].Out, packs, depth + 1);
                else
                {
                    int p = f.Packed.IndexOf(inIndex);
                    if (p < 0) throw new FsException("Corrupt 7z archive: a coder has no input.");
                    src = packs[p];
                }
                long size = f.UnpackSizes[outIndex];
                return new CheckedStream(Decoder(c, src, size), size, null, null, ownsInner: true);
            }
            outBase += c.NumOut;
            inBase += c.NumIn;
        }
        throw new FsException("Corrupt 7z archive: bad coder binding.");
    }

    private static Stream Decoder(Coder c, Stream src, long size)
    {
        switch (c.MethodId)
        {
            case 0x00: return src;
            case 0x21:
                if (c.Props.Length < 1) throw new FsException("Corrupt 7z archive: the LZMA2 properties are missing.");
                return new Lzma2InputStream(src, Lzma2InputStream.DictionarySize(c.Props[0]));
            case 0x030101: return new LzmaInputStream(src, c.Props, size);
            case 0x040108: return new DeflateStream(src, CompressionMode.Decompress);
            case 0x040202: return new Bzip2InputStream(src);
            case 0x04F71101: return new ZstdSharp.DecompressionStream(src);
            case 0x03: return new DeltaStream(src, c.Props.Length > 0 ? c.Props[0] + 1 : 1);
            case 0x03030103: return Branch(src, size, BcjFilter.X86, c);
            case 0x03030205: return Branch(src, size, BcjFilter.PowerPc, c);
            case 0x03030501: return Branch(src, size, BcjFilter.Arm, c);
            case 0x03030701: return Branch(src, size, BcjFilter.ArmThumb, c);
            case 0x03030805: return Branch(src, size, BcjFilter.Sparc, c);
            case 0x0A: return Branch(src, size, BcjFilter.Arm64, c);
            case 0x06F10701: throw new FsException("The 7z archive is encrypted (AES), which is not supported.");
            default: throw new FsException($"The 7z archive uses the compression method {MethodName(c.MethodId)}, which is not supported.");
        }
    }

    /// <summary>The branch converters work on whole buffers: the stream is decoded in memory.</summary>
    private static Stream Branch(Stream src, long size, BcjFilter filter, Coder c)
    {
        if (c.Props.Any(b => b != 0)) throw new FsException($"The 7z archive uses the {MethodName(c.MethodId)} filter with a start offset, which is not supported.");
        if (size > MemoryLimit) throw new FsException($"The 7z archive applies the {MethodName(c.MethodId)} filter to {size >> 20} MiB of data; at most {MemoryLimit >> 20} MiB are supported.");
        byte[] buf = new byte[size];
        using (src) ReadAll(src, buf);
        Bcj.Apply(filter, buf, encode: false);
        return new MemoryStream(buf, false);
    }

    public static string MethodName(ulong id) => id switch
    {
        0x00 => "Copy",
        0x21 => "LZMA2",
        0x030101 => "LZMA",
        0x040108 => "Deflate",
        0x040109 => "Deflate64",
        0x040202 => "BZip2",
        0x030401 => "PPMd",
        0x04F71101 => "Zstandard",
        0x03 => "Delta",
        0x03030103 => "BCJ",
        0x0303011B => "BCJ2",
        0x03030205 => "PPC",
        0x03030401 => "IA64",
        0x03030501 => "ARM",
        0x03030701 => "ARMT",
        0x03030805 => "SPARC",
        0x0A => "ARM64",
        0x0B => "RISCV",
        0x06F10701 => "AES-256",
        _ => $"0x{id:X}",
    };

    // ------------------------------------------------------------ helper streams

    /// <summary>A read-only window of a seekable stream; positions the base stream before every read.</summary>
    private sealed class WindowStream(Stream inner, long start, long length) : Stream
    {
        private long _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = (int)Math.Min(buffer.Length, length - _pos);
            if (n <= 0) return 0;
            lock (inner)
            {
                inner.Position = start + _pos;
                n = inner.Read(buffer[..n]);
            }
            _pos += n;
            return n;
        }
    }

    /// <summary>Delta filter decoder.</summary>
    private sealed class DeltaStream(Stream inner, int distance) : Stream
    {
        private readonly byte[] _hist = new byte[256];
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = inner.Read(buffer);
            for (int i = 0; i < n; i++)
            {
                byte b = (byte)(buffer[i] + _hist[(_pos - distance) & 0xFF]);
                buffer[i] = b;
                _hist[_pos & 0xFF] = b;
                _pos++;
            }
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>Reads exactly <c>length</c> bytes of a stream and checks their CRC32 at the end.</summary>
internal sealed class CheckedStream : Stream
{
    private readonly Stream _inner;
    private readonly long _length;
    private readonly uint? _crc;
    private readonly string? _name;
    private readonly bool _owns;
    private long _pos;
    private uint _running;

    public CheckedStream(Stream inner, long length, uint? crc, string? name, bool ownsInner)
    {
        _inner = inner;
        _length = length;
        _crc = crc;
        _name = name;
        _owns = ownsInner;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _pos; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int want = (int)Math.Min(buffer.Length, _length - _pos);
        if (want <= 0) return 0;
        int n;
        try
        {
            n = _inner.Read(buffer[..want]);
        }
        catch (InvalidDataException ex)
        {
            throw new FsException($"Corrupt compressed data in the archive{(_name is null ? "" : $" ('{_name}')")}: {ex.Message}", ex);
        }
        catch (ZstdSharp.ZstdException ex)
        {
            throw new FsException($"Corrupt compressed data in the archive{(_name is null ? "" : $" ('{_name}')")}: {ex.Message}", ex);
        }
        if (n <= 0) throw new FsException($"The archive is truncated or damaged: {(_name is null ? "a stream" : $"'{_name}'")} ends after {_pos} of {_length} bytes.");
        if (_crc is not null) _running = Crc32.Update(_running, buffer[..n]);
        _pos += n;
        if (_pos == _length && _crc is { } expected && _running != expected)
            throw new FsException($"The CRC of '{_name}' in the archive does not match: the archive is damaged.");
        return n;
    }

    /// <summary>Reads what the consumer left, so that the CRC is checked and the next entry starts at the right place.</summary>
    public void Drain()
    {
        byte[] buf = new byte[1 << 16];
        while (_pos < _length && Read(buf, 0, buf.Length) > 0) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _owns) _inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>An entry to put into a new archive.</summary>
public sealed class ArchiveWriteEntry
{
    public string Name { get; init; } = "";
    public bool IsDirectory { get; init; }
    public DateTime? LastWriteTimeUtc { get; init; }
    public uint? Attributes { get; init; }
    public long Length { get; init; }

    /// <summary>Writes the contents (exactly <see cref="Length"/> bytes) into the given stream.</summary>
    public Action<Stream>? WriteData { get; init; }
}

/// <summary>
/// Writes 7z archives as 7-Zip does with LZMA2 and several threads: one solid folder for all files, the
/// data compressed in parallel blocks; a plain header with names, times, attributes and CRCs.
/// </summary>
public static class SevenZipWriter
{
    public static void Write(Stream output, IReadOnlyList<ArchiveWriteEntry> entries, int effort, int threads,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        long baseOffset = output.Position;
        output.Write(new byte[32]);
        var sizes = new List<long>();
        var crcs = new List<uint>();
        long packSize = 0;
        long total = entries.Sum(e => e.IsDirectory ? 0 : e.Length), done = 0;
        byte dictProp = 0;
        if (entries.Any(e => !e.IsDirectory && e.Length > 0))
        {
            var counter = new CountingStream(output);
            long block = Math.Min(1 << 22, Math.Max(1 << 16, total));
            using (var lzma = new Lzma2OutputStream(counter, effort, (int)block, threads, leaveOpen: true))
            {
                dictProp = lzma.PropertiesByte;
                foreach (ArchiveWriteEntry e in entries)
                {
                    if (e.IsDirectory || e.Length == 0) continue;
                    var tap = new TapStream(lzma, ct, n => progress?.Report(new ProgressInfo("compress", done += n, total)));
                    e.WriteData!(tap);
                    if (tap.Count != e.Length)
                        throw new FsException($"'{e.Name}' changed its size while it was being packed ({tap.Count} instead of {e.Length} bytes).");
                    sizes.Add(tap.Count);
                    crcs.Add(tap.Crc);
                }
            }
            packSize = counter.Count;
        }

        byte[] header = BuildHeader(entries, sizes, crcs, packSize, dictProp);
        long headerOffset = output.Position - baseOffset - 32;
        output.Write(header);
        long end = output.Position;

        byte[] start = new byte[32];
        SevenZipArchive.Signature.CopyTo(start, 0);
        start[6] = 0;
        start[7] = 4;
        BinaryPrimitives.WriteUInt64LittleEndian(start.AsSpan(12), (ulong)headerOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(start.AsSpan(20), (ulong)header.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(start.AsSpan(28), Crc32.Compute(header));
        BinaryPrimitives.WriteUInt32LittleEndian(start.AsSpan(8), Crc32.Compute(start.AsSpan(12, 20)));
        output.Position = baseOffset;
        output.Write(start);
        output.Position = end;
        output.Flush();
    }

    private static byte[] BuildHeader(IReadOnlyList<ArchiveWriteEntry> entries, List<long> sizes, List<uint> crcs, long packSize, byte dictProp)
    {
        var h = new MemoryStream();
        void Num(ulong v)
        {
            int extra = 0;
            while (extra < 8 && v >= 1UL << (7 * (extra + 1))) extra++;
            if (extra == 8)
            {
                h.WriteByte(0xFF);
                for (int i = 0; i < 8; i++) h.WriteByte((byte)(v >> (8 * i)));
                return;
            }
            int first = (0xFF00 >> extra) & 0xFF;          // "extra" leading one bits
            first |= (int)(v >> (8 * extra));
            h.WriteByte((byte)first);
            for (int i = 0; i < extra; i++) h.WriteByte((byte)(v >> (8 * i)));
        }
        void Bits(IEnumerable<bool> bits)
        {
            int b = 0, mask = 0x80;
            foreach (bool v in bits)
            {
                if (v) b |= mask;
                mask >>= 1;
                if (mask == 0) { h.WriteByte((byte)b); b = 0; mask = 0x80; }
            }
            if (mask != 0x80) h.WriteByte((byte)b);
        }
        byte[] Sub(Action body)
        {
            var saved = h;
            h = new MemoryStream();
            body();
            byte[] r = h.ToArray();
            h = saved;
            return r;
        }

        Num(1);                                           // kHeader
        if (sizes.Count > 0)
        {
            Num(4);                                       // kMainStreamsInfo
            Num(6); Num(0); Num(1); Num(9); Num((ulong)packSize); Num(0);           // kPackInfo
            Num(7); Num(11); Num(1); h.WriteByte(0);                                // kUnpackInfo, kFolder, 1 folder
            Num(1); h.WriteByte(0x21); h.WriteByte(0x21); Num(1); h.WriteByte(dictProp);   // 1 coder: LZMA2 with its property
            Num(12); Num((ulong)sizes.Sum());                                       // kCodersUnpackSize
            Num(0);
            Num(8);                                                                 // kSubStreamsInfo
            if (sizes.Count != 1) { Num(13); Num((ulong)sizes.Count); }
            if (sizes.Count > 1) { Num(9); foreach (long s in sizes.Take(sizes.Count - 1)) Num((ulong)s); }
            Num(10); h.WriteByte(1);
            byte[] b4 = new byte[4];
            foreach (uint c in crcs) { BinaryPrimitives.WriteUInt32LittleEndian(b4, c); h.Write(b4); }
            Num(0);
            Num(0);
        }

        Num(5);                                           // kFilesInfo
        Num((ulong)entries.Count);
        bool[] emptyStream = entries.Select(e => e.IsDirectory || e.Length == 0).ToArray();
        if (emptyStream.Any(x => x))
        {
            byte[] es = Sub(() => Bits(emptyStream));
            Num(14); Num((ulong)es.Length); h.Write(es);
            bool[] emptyFile = entries.Where(e => e.IsDirectory || e.Length == 0).Select(e => !e.IsDirectory).ToArray();
            if (emptyFile.Any(x => x))
            {
                byte[] ef = Sub(() => Bits(emptyFile));
                Num(15); Num((ulong)ef.Length); h.Write(ef);
            }
        }
        byte[] names = Sub(() =>
        {
            h.WriteByte(0);
            foreach (ArchiveWriteEntry e in entries)
            {
                h.Write(Encoding.Unicode.GetBytes(e.Name));
                h.WriteByte(0);
                h.WriteByte(0);
            }
        });
        Num(17); Num((ulong)names.Length); h.Write(names);
        if (entries.Any(e => e.LastWriteTimeUtc is not null))
        {
            byte[] t = Sub(() =>
            {
                bool all = entries.All(e => e.LastWriteTimeUtc is not null);
                h.WriteByte((byte)(all ? 1 : 0));
                if (!all) Bits(entries.Select(e => e.LastWriteTimeUtc is not null));
                h.WriteByte(0);
                byte[] b = new byte[8];
                foreach (ArchiveWriteEntry e in entries)
                    if (e.LastWriteTimeUtc is { } d)
                    {
                        BinaryPrimitives.WriteInt64LittleEndian(b, DateTime.SpecifyKind(d, DateTimeKind.Utc).ToFileTimeUtc());
                        h.Write(b);
                    }
            });
            Num(20); Num((ulong)t.Length); h.Write(t);
        }
        if (entries.Any(e => e.Attributes is not null))
        {
            byte[] a = Sub(() =>
            {
                bool all = entries.All(e => e.Attributes is not null);
                h.WriteByte((byte)(all ? 1 : 0));
                if (!all) Bits(entries.Select(e => e.Attributes is not null));
                h.WriteByte(0);
                byte[] b = new byte[4];
                foreach (ArchiveWriteEntry e in entries)
                    if (e.Attributes is { } v)
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
                        h.Write(b);
                    }
            });
            Num(21); Num((ulong)a.Length); h.Write(a);
        }
        Num(0);                                           // end of kFilesInfo
        Num(0);                                           // end of kHeader
        return h.ToArray();
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long Count;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            Count += buffer.Length;
        }
    }

    /// <summary>Counts and checksums what passes into the compressor.</summary>
    private sealed class TapStream(Stream inner, CancellationToken ct, Action<int> report) : Stream
    {
        public long Count;
        public uint Crc;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ct.ThrowIfCancellationRequested();
            inner.Write(buffer);
            Crc = Crc32.Update(Crc, buffer);
            Count += buffer.Length;
            report(buffer.Length);
        }
    }
}
