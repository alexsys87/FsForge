using System.Globalization;
using System.Text;

namespace FsForge.Core.Tar;

/// <summary>
/// Reads tar archives (v7, ustar, pax, GNU) into a tree, the way "tar -xp --numeric-owner" would
/// unpack them: owners, modes, devices, hard links, sparse files and extended attributes are kept.
/// </summary>
public sealed class TarReader
{
    private const int Block = 512;
    private const int MaxMetaSize = 1 << 26;

    private readonly Input _in;
    private readonly FsTree _tree = new();
    private readonly List<string> _warnings = new();
    private readonly HashSet<FsNode> _implicitDirs = new(ReferenceEqualityComparer.Instance);
    private long _entries;
    private bool _sawPax, _sawGnu, _rootEntry;
    private string? _commonPrefix;
    private bool _prefixKnown;

    private TarReader(Input input) => _in = input;

    /// <summary>Result of reading an archive.</summary>
    public sealed record Result(FsTree Tree, TarInfo Info, IReadOnlyList<string> Warnings);

    /// <summary>Quick check: is the source a (possibly compressed) tar archive?</summary>
    public static bool IsTar(IBlockSource source, out ArchiveCompression compression)
    {
        compression = ArchiveCompression.None;
        byte[] head = new byte[Block];
        int n = source.Read(0, head);
        if (ArchiveCodec.DetectStreamCompression(head.AsSpan(0, n)) is { } c)
        {
            compression = c;
            try
            {
                using Stream s = ArchiveCodec.OpenDecompressor(new BlockSourceStream(source, 0, source.Length), c);
                int got = s.ReadAtLeast(head, Block, throwOnEndOfStream: false);
                if (got != Block) return false;
                if (ArchiveCodec.IsTarHeader(head)) return true;
                if (head.AsSpan().IndexOfAnyExcept((byte)0) >= 0) return false;
                // Zeros: an archive without members is nothing else, but so is the start of an ext image.
                byte[] rest = new byte[20 * Block];
                int more = s.ReadAtLeast(rest, rest.Length, throwOnEndOfStream: false);
                return more < rest.Length && more % Block == 0 && rest.AsSpan(0, more).IndexOfAnyExcept((byte)0) < 0;
            }
            catch (Exception ex) when (ex is FsException or InvalidDataException or IOException or ZstdSharp.ZstdException)
            {
                return false;
            }
        }
        if (n == Block && ArchiveCodec.IsTarHeader(head)) return true;

        // An archive without members is nothing but zero blocks.
        if (n == Block && source.Length % Block == 0 && source.Length <= 20 * Block)
        {
            byte[] all = source.ReadBytes(0, (int)source.Length);
            return all.AsSpan().IndexOfAnyExcept((byte)0) < 0;
        }
        return false;
    }

    /// <summary>
    /// Reads the whole archive. For compressed archives the file contents are kept in a temporary
    /// spool file owned by the tree; uncompressed archives are referenced in place, so
    /// <paramref name="source"/> must stay open as long as the tree is used (the tree takes ownership).
    /// </summary>
    public static Result Read(IBlockSource source, string? tempDirectory = null,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        byte[] head = new byte[16];
        int n = source.Read(0, head);
        ArchiveCompression? detected = ArchiveCodec.DetectStreamCompression(head.AsSpan(0, n));
        if (detected is null && ArchiveCodec.DetectUnsupported(head.AsSpan(0, n)) is { } name)
        {
            byte[] first = new byte[Block];
            if (source.Read(0, first) < Block || !ArchiveCodec.IsTarHeader(first))
                throw new FsException($"The archive is compressed with {name}, which is not supported. Use gzip, xz, zstd or bzip2.");
        }

        Input input;
        TempSpool? spool = null;
        Stream? stream = null;
        ArchiveCompression compression = detected ?? ArchiveCompression.None;
        if (compression == ArchiveCompression.None)
        {
            input = new SourceInput(source);
        }
        else
        {
            spool = new TempSpool(tempDirectory);
            var raw = new BlockSourceStream(source, 0, source.Length);
            stream = new BufferedStream(ArchiveCodec.OpenDecompressor(raw, compression), 1 << 16);
            input = new StreamInput(stream, spool, raw);
        }

        var reader = new TarReader(input);
        try
        {
            reader.ReadAll(progress, ct);
        }
        catch (Exception ex)
        {
            stream?.Dispose();
            spool?.Dispose();
            if (ex is InvalidDataException or ZstdSharp.ZstdException or EndOfStreamException)
                throw new FsException("The archive is corrupt: " + ex.Message, ex);
            throw;
        }

        if (spool is not null)
        {
            // Everything needed now lives in the spool; the compressed file can be released.
            stream!.Dispose();
            source.Dispose();
            reader._tree.AttachResource(spool);
        }
        else
        {
            reader._tree.AttachResource(source);
        }

        var info = new TarInfo
        {
            Compression = compression,
            Entries = reader._entries,
            HasPaxHeaders = reader._sawPax,
            HasGnuExtensions = reader._sawGnu,
            PathPrefix = reader._commonPrefix ?? "./",
            HasRootEntry = reader._rootEntry,
        };
        return new Result(reader._tree, info, reader._warnings);
    }

    // ---------------------------------------------------------------- input

    private abstract class Input
    {
        /// <summary>Reads one 512-byte block; false at a clean end of the data.</summary>
        public abstract bool ReadBlock(Span<byte> block);

        /// <summary>Reads <paramref name="size"/> bytes of member data and skips the padding.</summary>
        public abstract byte[] ReadBytes(long size);

        /// <summary>Captures member data without loading it and skips the padding.</summary>
        public abstract IFileData? TakeData(long size);

        public abstract void Skip(long size);

        /// <summary>Position in the (compressed) archive file, for progress reports.</summary>
        public abstract long Position { get; }
        public abstract long Length { get; }

        protected static long Padded(long size) => (size + Block - 1) & ~(long)(Block - 1);
    }

    private sealed class SourceInput(IBlockSource source) : Input
    {
        private long _pos;

        public override long Position => _pos;
        public override long Length => source.Length;

        public override bool ReadBlock(Span<byte> block)
        {
            if (_pos >= source.Length) return false;
            if (source.Length - _pos < Block) throw new FsException("The tar archive is truncated.");
            source.ReadExactly(_pos, block);
            _pos += Block;
            return true;
        }

        private void Check(long size)
        {
            if (size < 0 || size > source.Length - _pos) throw new FsException("The tar archive is truncated.");
        }

        public override byte[] ReadBytes(long size)
        {
            Check(size);
            byte[] data = source.ReadBytes(_pos, checked((int)size));
            _pos += Padded(size);
            return data;
        }

        public override IFileData? TakeData(long size)
        {
            Check(size);
            IFileData? data = size == 0 ? null : new SliceFileData(source, _pos, size);
            _pos += Padded(size);
            return data;
        }

        public override void Skip(long size)
        {
            Check(size);
            _pos += Padded(size);
        }
    }

    private sealed class StreamInput(Stream stream, TempSpool spool, Stream raw) : Input
    {
        private readonly byte[] _scratch = new byte[1 << 16];

        public override long Position => raw.Position;
        public override long Length => raw.Length;

        public override bool ReadBlock(Span<byte> block)
        {
            int got = stream.ReadAtLeast(block, Block, throwOnEndOfStream: false);
            if (got == 0) return false;
            if (got < Block) throw new FsException("The tar archive is truncated.");
            return true;
        }

        private void SkipExact(long count)
        {
            while (count > 0)
            {
                int n = stream.Read(_scratch, 0, (int)Math.Min(_scratch.Length, count));
                if (n <= 0) throw new FsException("The tar archive is truncated.");
                count -= n;
            }
        }

        public override byte[] ReadBytes(long size)
        {
            byte[] data = new byte[checked((int)size)];
            if (stream.ReadAtLeast(data, data.Length, throwOnEndOfStream: false) < data.Length)
                throw new FsException("The tar archive is truncated.");
            SkipExact(Padded(size) - size);
            return data;
        }

        public override IFileData? TakeData(long size)
        {
            if (size == 0) return null;
            IFileData data;
            try
            {
                data = spool.Append(stream, size);
            }
            catch (EndOfStreamException)
            {
                throw new FsException("The tar archive is truncated.");
            }
            SkipExact(Padded(size) - size);
            return data;
        }

        public override void Skip(long size) => SkipExact(Padded(size));
    }

    // ----------------------------------------------------------- pax records

    private sealed class Pax
    {
        public readonly Dictionary<string, byte[]> Records = new(StringComparer.Ordinal);
        public List<(long Offset, long Length)>? SparseMap;      // GNU sparse 0.0
        private long _pendingOffset = -1;

        public string? Text(string key) => Records.TryGetValue(key, out byte[]? v) ? Encoding.UTF8.GetString(v) : null;

        public long? Integer(string key)
        {
            string? t = Text(key);
            if (t is null) return null;
            // Times may carry a fraction ("1700000000.123456789"); the tree keeps whole seconds.
            int dot = t.IndexOf('.');
            bool negativeFraction = false;
            if (dot >= 0)
            {
                negativeFraction = t.StartsWith('-') && t.AsSpan(dot + 1).IndexOfAnyExcept('0') >= 0;
                t = t[..dot];
            }
            if (!long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v)) return null;
            return negativeFraction ? v - 1 : v;                // floor for times before the epoch
        }

        public void Add(string key, byte[] value)
        {
            if (key == "GNU.sparse.offset")
            {
                _pendingOffset = ParseLong(value);
                return;
            }
            if (key == "GNU.sparse.numbytes")
            {
                if (_pendingOffset >= 0) (SparseMap ??= new()).Add((_pendingOffset, ParseLong(value)));
                _pendingOffset = -1;
                return;
            }
            Records[key] = value;
        }

        private static long ParseLong(byte[] v) =>
            long.TryParse(Encoding.ASCII.GetString(v), NumberStyles.None, CultureInfo.InvariantCulture, out long r) ? r : 0;
    }

    private Pax ParsePax(byte[] data)
    {
        var pax = new Pax();
        int p = 0;
        while (p < data.Length)
        {
            // "<length> <key>=<value>\n", the length counts the whole record.
            int sp = Array.IndexOf(data, (byte)' ', p);
            if (sp < 0 || sp - p > 12) break;
            if (!int.TryParse(Encoding.ASCII.GetString(data, p, sp - p), NumberStyles.None, CultureInfo.InvariantCulture, out int len)
                || len < sp - p + 3 || p + len > data.Length)
            {
                _warnings.Add("A malformed pax extended header was ignored.");
                break;
            }
            int end = p + len - 1;                              // position of the trailing newline
            int eq = Array.IndexOf(data, (byte)'=', sp + 1, end - sp - 1);
            if (eq > 0)
            {
                string key = Encoding.UTF8.GetString(data, sp + 1, eq - sp - 1);
                pax.Add(key, data.AsSpan(eq + 1, end - eq - 1).ToArray());
            }
            p += len;
        }
        return pax;
    }

    // ------------------------------------------------------------ main loop

    private static string CString(ReadOnlySpan<byte> field)
    {
        int n = field.IndexOf((byte)0);
        return PosixName.Decode(n < 0 ? field : field[..n]);
    }

    private static string TrimNul(byte[] data)
    {
        int n = Array.IndexOf(data, (byte)0);
        return PosixName.Decode(n < 0 ? data : data.AsSpan(0, n));
    }

    private void ReadAll(IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        byte[] hdr = new byte[Block];
        Pax? pax = null;
        string? longName = null, longLink = null;
        int zeroBlocks = 0;
        long counter = 0;
        bool any = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!_in.ReadBlock(hdr)) break;
            if (hdr.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            {
                if (++zeroBlocks >= 2) break;                   // regular end-of-archive marker
                continue;
            }
            zeroBlocks = 0;
            if (!ArchiveCodec.IsTarHeader(hdr))
            {
                if (!any) throw new FsException("The file is not a tar archive.");
                throw new FsException($"Corrupt tar archive: bad header checksum after {_entries} entries.");
            }
            any = true;

            char type = (char)hdr[156];
            long size = TarNumbers.Parse(hdr.AsSpan(124, 12), "size");
            bool gnuMagic = hdr.AsSpan(257, 8).SequenceEqual("ustar  \0"u8);
            bool posixMagic = hdr.AsSpan(257, 6).SequenceEqual("ustar\0"u8);

            switch (type)
            {
                case 'x' or 'X':
                    if (size > MaxMetaSize) throw new FsException("Corrupt tar archive: oversized extended header.");
                    pax = ParsePax(_in.ReadBytes(size));
                    _sawPax = true;
                    continue;
                case 'g':
                    _in.Skip(size);                             // global headers carry no per-file data we need
                    _sawPax = true;
                    continue;
                case 'L':
                    if (size > MaxMetaSize) throw new FsException("Corrupt tar archive: oversized long name.");
                    longName = TrimNul(_in.ReadBytes(size));
                    _sawGnu = true;
                    continue;
                case 'K':
                    if (size > MaxMetaSize) throw new FsException("Corrupt tar archive: oversized long link name.");
                    longLink = TrimNul(_in.ReadBytes(size));
                    _sawGnu = true;
                    continue;
                case 'V' or 'M' or 'N':
                    _in.Skip(size);                             // volume label, multi-volume continuation, old long names
                    pax = null;
                    longName = longLink = null;
                    continue;
            }

            // ---- a real member ----
            string name;
            if (pax?.Text("GNU.sparse.name") is { } sparseName) name = sparseName;
            else if (pax is not null && pax.Records.TryGetValue("path", out byte[]? pathBytes)) name = PosixName.Decode(pathBytes);
            else if (longName is not null) name = longName;
            else
            {
                name = CString(hdr.AsSpan(0, 100));
                if (posixMagic && hdr[345] != 0) name = CString(hdr.AsSpan(345, 155)) + "/" + name;
            }

            string link;
            if (pax is not null && pax.Records.TryGetValue("linkpath", out byte[]? linkBytes)) link = PosixName.Decode(linkBytes);
            else link = longLink ?? CString(hdr.AsSpan(157, 100));

            int mode = (int)(TarNumbers.Parse(hdr.AsSpan(100, 8), "mode") & 0xFFF);
            long uid = pax?.Integer("uid") ?? TarNumbers.Parse(hdr.AsSpan(108, 8), "uid");
            long gid = pax?.Integer("gid") ?? TarNumbers.Parse(hdr.AsSpan(116, 8), "gid");
            long mtime = pax?.Integer("mtime") ?? TarNumbers.Parse(hdr.AsSpan(136, 12), "mtime");
            if (pax?.Integer("size") is { } paxSize) size = paxSize;
            if (size < 0) throw new FsException("Corrupt tar archive: negative member size.");
            if (gnuMagic) _sawGnu = true;

            FsNodeType? nodeType = type switch
            {
                '0' or '\0' or '7' => name.EndsWith('/') ? FsNodeType.Directory : FsNodeType.File,
                'S' => FsNodeType.File,
                '2' => FsNodeType.Symlink,
                '3' => FsNodeType.CharDevice,
                '4' => FsNodeType.BlockDevice,
                '5' or 'D' => FsNodeType.Directory,
                '6' => FsNodeType.Fifo,
                '1' => null,
                _ => FsNodeType.File,                           // unknown types are treated as regular files (POSIX)
            };

            IFileData? data = null;
            bool hasBody = nodeType == FsNodeType.File || (type == '1' && posixMagic && size > 0);
            if (type == 'D') hasBody = true;                    // GNU dump directory: the body is a file list
            if (!hasBody) size = 0;

            if (nodeType == FsNodeType.File)
            {
                data = ReadFileBody(hdr, type, size, pax);
            }
            else if (hasBody)
            {
                _in.Skip(size);
            }

            var inode = nodeType is null ? null : new FsInode
            {
                Type = nodeType.Value,
                Mode = nodeType == FsNodeType.Symlink ? 0x1FF : mode,
                Uid = unchecked((uint)Math.Clamp(uid, 0, uint.MaxValue)),
                Gid = unchecked((uint)Math.Clamp(gid, 0, uint.MaxValue)),
                MTime = mtime,
                Data = data,
            };
            if (inode is not null)
            {
                if (nodeType == FsNodeType.Symlink) inode.LinkTarget = link;
                if (nodeType is FsNodeType.CharDevice or FsNodeType.BlockDevice)
                {
                    inode.DevMajor = (uint)(pax?.Integer("SCHILY.devmajor") ?? TarNumbers.Parse(hdr.AsSpan(329, 8), "device major"));
                    inode.DevMinor = (uint)(pax?.Integer("SCHILY.devminor") ?? TarNumbers.Parse(hdr.AsSpan(337, 8), "device minor"));
                }
                if (pax is not null) inode.XAttrs = ReadXattrs(pax);
            }

            Insert(name, inode, link);
            _entries++;
            pax = null;
            longName = longLink = null;
            if ((++counter & 0xFF) == 0) progress?.Report(new ProgressInfo("read", _in.Position, _in.Length, name));
        }

        if (!any && _in.Length > 0 && zeroBlocks == 0) throw new FsException("The file is not a tar archive.");
        if (!_rootEntry)
        {
            // No entry for the root itself: give it the time of its newest child instead of 1970.
            long newest = 0;
            foreach (FsNode c in _tree.Root.Children) newest = Math.Max(newest, c.Inode.MTime);
            _tree.Root.Inode.MTime = newest;
        }
        progress?.Report(new ProgressInfo("read", _in.Length, _in.Length));
    }

    // -------------------------------------------------------- sparse members

    private IFileData? ReadFileBody(byte[] hdr, char type, long size, Pax? pax)
    {
        List<(long Offset, long Length)>? map = null;
        long realSize = -1;

        if (type == 'S')
        {
            // Old GNU format: four map entries in the header, more in extension blocks.
            map = new();
            for (int i = 0; i < 4; i++) AddSparse(map, hdr.AsSpan(386 + i * 24, 24));
            realSize = TarNumbers.Parse(hdr.AsSpan(483, 12), "sparse size");
            bool extended = hdr[482] != 0;
            byte[] ext = new byte[Block];
            while (extended)
            {
                if (!_in.ReadBlock(ext)) throw new FsException("The tar archive is truncated.");
                for (int i = 0; i < 21; i++) AddSparse(map, ext.AsSpan(i * 24, 24));
                extended = ext[504] != 0;
            }
            _sawGnu = true;
        }
        else if (pax is not null)
        {
            if (pax.Text("GNU.sparse.major") == "1" && pax.Text("GNU.sparse.minor") == "0")
            {
                // Format 1.0: the map is stored as decimal lines in front of the data, padded to a block.
                realSize = pax.Integer("GNU.sparse.realsize") ?? -1;
                map = new();
                long consumed = 0;
                var numbers = new List<long>();
                long expected = -1;
                byte[] blk = new byte[Block];
                long current = 0;
                bool inNumber = false;
                while (expected < 0 || numbers.Count < expected * 2)
                {
                    if (consumed + Block > size || !_in.ReadBlock(blk)) throw new FsException("Corrupt tar archive: bad sparse map.");
                    consumed += Block;
                    foreach (byte b in blk)
                    {
                        if (b is >= (byte)'0' and <= (byte)'9')
                        {
                            current = current * 10 + (b - '0');
                            inNumber = true;
                        }
                        else if (b == '\n' && inNumber)
                        {
                            if (expected < 0) expected = current;
                            else numbers.Add(current);
                            current = 0;
                            inNumber = false;
                            if (expected >= 0 && numbers.Count >= expected * 2) break;
                        }
                        else if (b != 0)
                        {
                            throw new FsException("Corrupt tar archive: bad sparse map.");
                        }
                    }
                    if (expected == 0) break;
                }
                for (int i = 0; i + 1 < numbers.Count; i += 2) map.Add((numbers[i], numbers[i + 1]));
                size -= consumed;
            }
            else if (pax.Text("GNU.sparse.map") is { } text)
            {
                // Format 0.1: "offset,length,offset,length,..."
                realSize = pax.Integer("GNU.sparse.size") ?? -1;
                map = new();
                string[] parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i + 1 < parts.Length; i += 2)
                    if (long.TryParse(parts[i], out long o) && long.TryParse(parts[i + 1], out long l)) map.Add((o, l));
            }
            else if (pax.SparseMap is not null)
            {
                realSize = pax.Integer("GNU.sparse.size") ?? -1;
                map = pax.SparseMap;
            }
            if (map is not null) _sawGnu = true;
        }

        IFileData? stored = _in.TakeData(size);
        if (map is null) return stored;

        long dataBytes = 0, end = 0;
        foreach ((long offset, long length) in map)
        {
            if (offset < end || length < 0) throw new FsException("Corrupt tar archive: bad sparse map.");
            dataBytes += length;
            end = offset + length;
        }
        if (realSize < 0) realSize = end;
        if (dataBytes > size || end > realSize) throw new FsException("Corrupt tar archive: bad sparse map.");
        if (realSize == 0) return null;
        return new SparseFileData(stored, map, realSize, dataBytes);
    }

    private static void AddSparse(List<(long Offset, long Length)> map, ReadOnlySpan<byte> entry)
    {
        if (entry[0] == 0 && entry[12] == 0) return;
        long offset = TarNumbers.Parse(entry[..12], "sparse offset");
        long length = TarNumbers.Parse(entry.Slice(12, 12), "sparse length");
        map.Add((offset, length));
    }

    /// <summary>A file made of stored data segments separated by holes.</summary>
    private sealed class SparseFileData(IFileData? stored, List<(long Offset, long Length)> map, long realSize, long dataBytes) : ISparseFileData
    {
        public long Length => realSize;
        public long DataBytesHint => dataBytes;
        public Stream OpenRead() => new SparseStream(stored?.OpenRead(), map, realSize);
    }

    private sealed class SparseStream(Stream? inner, List<(long Offset, long Length)> map, long length) : Stream
    {
        private long _position;
        private int _index;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= length || buffer.Length == 0) return 0;
            if (buffer.Length > length - _position) buffer = buffer[..(int)(length - _position)];
            while (_index < map.Count && _position >= map[_index].Offset + map[_index].Length) _index++;

            if (_index < map.Count && _position >= map[_index].Offset)
            {
                // Inside a data segment: the stored bytes are consumed strictly in order.
                long left = map[_index].Offset + map[_index].Length - _position;
                if (buffer.Length > left) buffer = buffer[..(int)left];
                int n = inner?.Read(buffer) ?? 0;
                if (n <= 0) throw new FsException("Corrupt tar archive: sparse member data is truncated.");
                _position += n;
                return n;
            }

            long holeEnd = _index < map.Count ? map[_index].Offset : length;
            int zeros = (int)Math.Min(buffer.Length, holeEnd - _position);
            buffer[..zeros].Clear();
            _position += zeros;
            return zeros;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner?.Dispose();
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // --------------------------------------------------------------- xattrs

    private static SortedDictionary<string, byte[]>? ReadXattrs(Pax pax)
    {
        SortedDictionary<string, byte[]>? result = null;
        foreach ((string key, byte[] value) in pax.Records)
        {
            if (key.StartsWith("SCHILY.xattr.", StringComparison.Ordinal) && key.Length > 13)
            {
                (result ??= new(StringComparer.Ordinal))[key[13..]] = value;
            }
            else if (key.StartsWith("LIBARCHIVE.xattr.", StringComparison.Ordinal) && key.Length > 17)
            {
                string attr = Uri.UnescapeDataString(key[17..]);
                result ??= new(StringComparer.Ordinal);
                if (result.ContainsKey(attr)) continue;         // the SCHILY form wins
                try
                {
                    result[attr] = Convert.FromBase64String(PadBase64(Encoding.ASCII.GetString(value)));
                }
                catch (FormatException)
                {
                    // Not valid base64: ignore the attribute.
                }
            }
        }
        if (pax.Records.TryGetValue("RHT.security.selinux", out byte[]? ctx))
        {
            result ??= new(StringComparer.Ordinal);
            if (!result.ContainsKey("security.selinux"))
            {
                // The kernel stores SELinux contexts with a trailing NUL.
                byte[] v = new byte[ctx.Length + 1];
                ctx.CopyTo(v, 0);
                result["security.selinux"] = v;
            }
        }
        return result;
    }

    private static string PadBase64(string s) => (s.Length % 4) switch
    {
        2 => s + "==",
        3 => s + "=",
        _ => s,
    };

    // ------------------------------------------------------- tree insertion

    private void TrackPrefix(string rawName)
    {
        // Remember whether every member starts with "./" (or with nothing) to reproduce it on export.
        string prefix = rawName.StartsWith("./", StringComparison.Ordinal) || rawName == "." ? "./" : "";
        if (!_prefixKnown)
        {
            _commonPrefix = prefix;
            _prefixKnown = true;
        }
        else if (_commonPrefix != prefix)
        {
            _commonPrefix = "";
        }
    }

    private void Insert(string rawName, FsInode? inode, string link)
    {
        string[] parts = Normalize(rawName, out bool bad);
        if (bad)
        {
            _warnings.Add($"'{rawName}': the path contains '..' and was skipped.");
            return;
        }
        TrackPrefix(rawName);

        if (parts.Length == 0)
        {
            // The root directory itself.
            if (inode is { Type: FsNodeType.Directory })
            {
                CopyAttributes(inode, _tree.Root.Inode);
                _rootEntry = true;
            }
            return;
        }

        FsNode parent = _tree.Root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            FsNode? next = parent.Find(parts[i]);
            if (next is null)
            {
                next = _tree.CreateDirectory(parent, parts[i], 0x1ED, 0, 0, inode?.MTime ?? 0);
                _implicitDirs.Add(next);
            }
            else if (!next.IsDirectory)
            {
                // A file was later used as a directory: the newer entry wins, as with tar itself.
                _warnings.Add($"'{next.Path}' is replaced by a directory required by '{rawName}'.");
                _tree.Remove(next);
                next = _tree.CreateDirectory(parent, parts[i], 0x1ED, 0, 0, inode?.MTime ?? 0);
                _implicitDirs.Add(next);
            }
            parent = next;
        }

        string leaf = parts[^1];
        try
        {
            FsTree.ValidateName(leaf);
        }
        catch (FsException ex)
        {
            _warnings.Add($"'{rawName}' was skipped: {ex.Message}");
            return;
        }
        FsNode? existing = parent.Find(leaf);

        if (inode is null)
        {
            // Hard link to a member that came earlier in the archive.
            string[] targetParts = Normalize(link, out bool badLink);
            FsNode? target = badLink ? null : _tree.Resolve(string.Join('/', targetParts));
            if (target is null || target.IsDirectory)
            {
                _warnings.Add($"'{rawName}': the hard link target '{link}' is not in the archive; the link was skipped.");
                return;
            }
            if (existing is not null)
            {
                if (ReferenceEquals(existing.Inode, target.Inode)) return;
                _tree.Remove(existing);
            }
            _tree.CreateHardLink(parent, leaf, target);
            return;
        }

        if (existing is not null)
        {
            if (existing.IsDirectory && inode.Type == FsNodeType.Directory)
            {
                CopyAttributes(inode, existing.Inode);
                _implicitDirs.Remove(existing);
                return;
            }
            _tree.Remove(existing);
        }
        _tree.Add(parent, leaf, inode);
    }

    private static void CopyAttributes(FsInode from, FsInode to)
    {
        to.Mode = from.Mode;
        to.Uid = from.Uid;
        to.Gid = from.Gid;
        to.MTime = from.MTime;
        to.XAttrs = from.XAttrs;
    }

    /// <summary>Splits a member name into clean components ("./a//b/" gives a, b).</summary>
    private static string[] Normalize(string name, out bool hasDotDot)
    {
        hasDotDot = false;
        var parts = new List<string>();
        foreach (string p in name.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (p == ".") continue;
            if (p == "..")
            {
                hasDotDot = true;
                return Array.Empty<string>();
            }
            parts.Add(p);
        }
        return parts.ToArray();
    }
}
