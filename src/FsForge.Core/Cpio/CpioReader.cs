using FsForge.Core.Tar;
using static FsForge.Core.Cpio.CpioFormat;

namespace FsForge.Core.Cpio;

/// <summary>
/// Reads cpio archives (newc, crc and odc headers; gzip, xz or zstd compressed or not) into a tree, the way
/// the Linux kernel unpacks an initramfs: owners, modes, times, devices and hard links are kept, later
/// entries replace earlier ones, and archives stored one after another are read as one.
/// </summary>
public sealed class CpioReader
{
    private const int MaxNameSize = 1 << 16;
    private const int MaxLinkSize = 1 << 16;

    private readonly FsTree _tree = new();
    private readonly List<string> _warnings = new();
    private readonly HashSet<FsNode> _implicitDirs = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(long Dev, long Ino), FsInode> _links = new();
    private readonly string? _tempDirectory;
    private TempSpool? _spool;
    private long _entries, _badChecksums;
    // Checked once the whole archive is read: the spool cannot be read while it still grows.
    private readonly List<(IFileData Data, uint Expected)> _checksums = new();
    private bool _rootEntry, _prefixKnown, _truncated;
    private string _prefix = "";
    private CpioHeader? _header;
    private int _parts;

    private CpioReader(string? tempDirectory) => _tempDirectory = tempDirectory;

    /// <summary>Result of reading an archive.</summary>
    public sealed record Result(FsTree Tree, CpioInfo Info, IReadOnlyList<string> Warnings);

    /// <summary>Quick check: does the source start with a (possibly compressed) cpio archive?</summary>
    public static bool IsCpio(IBlockSource source, out ArchiveCompression compression)
    {
        compression = ArchiveCompression.None;
        byte[] head = new byte[8];
        int n = source.Read(0, head);
        if (Detect(head.AsSpan(0, n)) is not null) return true;
        if (ArchiveCodec.DetectCompression(head.AsSpan(0, n)) is not { } c) return false;
        try
        {
            using Stream s = ArchiveCodec.OpenDecompressor(new BlockSourceStream(source, 0, source.Length), c);
            int got = s.ReadAtLeast(head, 6, throwOnEndOfStream: false);
            if (got < 6 || Detect(head) is null) return false;
            compression = c;
            return true;
        }
        catch (Exception ex) when (ex is FsException or InvalidDataException or IOException or ZstdSharp.ZstdException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the whole archive. File contents of uncompressed parts are referenced in place, those of
    /// compressed parts are kept in a temporary spool; either way the tree takes ownership of
    /// <paramref name="source"/>.
    /// </summary>
    public static Result Read(IBlockSource source, string? tempDirectory = null,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var r = new CpioReader(tempDirectory);
        ArchiveCompression compression = ArchiveCompression.None;
        Stream? stream = null;
        try
        {
            long pos = 0;
            while (pos < source.Length)
            {
                byte[] head = new byte[8];
                int n = source.Read(pos, head);
                if (Detect(head.AsSpan(0, n)) is not null)
                {
                    // An uncompressed archive: read in place; what follows it may be another one.
                    var input = new SourceInput(source, pos);
                    if (!r.ReadParts(input, progress, ct)) break;
                    pos = input.Offset;
                }
                else if (ArchiveCodec.DetectCompression(head.AsSpan(0, n)) is { } c)
                {
                    // A compressed archive (possibly several in one stream) runs to the end of the file.
                    compression = c;
                    r._spool ??= new TempSpool(tempDirectory);
                    var raw = new BlockSourceStream(source, pos, source.Length - pos);
                    stream = new BufferedStream(ArchiveCodec.OpenDecompressor(raw, c), 1 << 16);
                    r.ReadParts(new StreamInput(stream, r._spool, raw), progress, ct);
                    break;
                }
                else
                {
                    if (r._parts == 0)
                    {
                        if (IsBinary(head.AsSpan(0, n)))
                            throw new FsException("The archive uses the old binary cpio format, which is not supported. Only newc, crc and odc archives are read.");
                        if (ArchiveCodec.DetectUnsupported(head.AsSpan(0, n)) is { } name)
                            throw new FsException($"The cpio archive is compressed with {name}, which is not supported. Use gzip, xz or zstd.");
                        throw new FsException("Not a cpio archive (no newc, crc or odc header).");
                    }
                    string what = ArchiveCodec.DetectUnsupported(head.AsSpan(0, n)) is { } other ? $"data compressed with {other}" : "data that is not a cpio archive";
                    r._warnings.Add($"The file goes on after the cpio archive with {what} (from offset 0x{pos:X}); it was ignored.");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            stream?.Dispose();
            r._spool?.Dispose();
            if (ex is InvalidDataException or ZstdSharp.ZstdException or EndOfStreamException)
                throw new FsException("The cpio archive is corrupt: " + ex.Message, ex);
            throw;
        }

        stream?.Dispose();
        try
        {
            foreach ((IFileData data, uint expected) in r._checksums)
                if (Sum(data) != expected) r._badChecksums++;
        }
        catch
        {
            r._spool?.Dispose();
            throw;
        }
        // The tree keeps the file (for uncompressed parts) and the spool (for compressed ones).
        if (r._spool is not null) r._tree.AttachResource(r._spool);
        r._tree.AttachResource(source);

        if (r._parts > 1)
            r._warnings.Add($"The file holds {r._parts} cpio archives one after another (for example CPU microcode in front of the initramfs). " +
                            "They are shown together; saving writes a single archive, which the kernel then no longer reads as early microcode.");
        if (r._truncated) r._warnings.Add("The cpio archive ends without its trailer: it may be truncated.");
        if (r._badChecksums > 0)
            r._warnings.Add($"{r._badChecksums} file(s) do not match the checksum stored in the archive: their content is damaged.");

        var info = new CpioInfo
        {
            Header = r._header ?? CpioHeader.Newc,
            Compression = compression,
            Entries = r._entries,
            PathPrefix = r._prefix,
            HasRootEntry = r._rootEntry,
            Parts = Math.Max(1, r._parts),
            Truncated = r._truncated,
        };
        return new Result(r._tree, info, r._warnings);
    }

    // ---------------------------------------------------------------- input

    private abstract class Input
    {
        /// <summary>Bytes read in the current archive: header and data are aligned relative to its start.</summary>
        public long Position;

        /// <summary>Reads exactly <paramref name="buffer"/>.Length bytes; false when the data ends before the first one.</summary>
        public abstract bool Read(Span<byte> buffer);

        public abstract IFileData? TakeData(long size);

        public abstract void Skip(long count);

        /// <summary>Skips zero bytes; false when the data ends.</summary>
        public abstract bool SkipZeros();

        public abstract long Done { get; }
        public abstract long Total { get; }
    }

    private sealed class SourceInput(IBlockSource source, long start) : Input
    {
        public long Offset = start;

        public override long Done => Offset;
        public override long Total => source.Length;

        public override bool Read(Span<byte> buffer)
        {
            if (Offset >= source.Length) return false;
            if (source.Length - Offset < buffer.Length) throw new FsException("The cpio archive is truncated.");
            source.ReadExactly(Offset, buffer);
            Offset += buffer.Length;
            Position += buffer.Length;
            return true;
        }

        public override IFileData? TakeData(long size)
        {
            if (size > source.Length - Offset) throw new FsException("The cpio archive is truncated.");
            IFileData? data = size == 0 ? null : new SliceFileData(source, Offset, size);
            Offset += size;
            Position += size;
            return data;
        }

        public override void Skip(long count)
        {
            if (count > source.Length - Offset) throw new FsException("The cpio archive is truncated.");
            Offset += count;
            Position += count;
        }

        public override bool SkipZeros()
        {
            byte[] buffer = new byte[1 << 16];
            while (Offset < source.Length)
            {
                int n = source.Read(Offset, buffer);
                if (n <= 0) break;
                int nonZero = buffer.AsSpan(0, n).IndexOfAnyExcept((byte)0);
                if (nonZero >= 0)
                {
                    Offset += nonZero;
                    return true;
                }
                Offset += n;
            }
            return false;
        }
    }

    private sealed class StreamInput(Stream stream, TempSpool spool, Stream raw) : Input
    {
        private readonly byte[] _scratch = new byte[1 << 16];
        private int _pushedBack = -1;

        public override long Done => raw.Position;
        public override long Total => raw.Length;

        private int ReadSome(Span<byte> buffer)
        {
            if (buffer.Length == 0) return 0;
            if (_pushedBack >= 0)
            {
                buffer[0] = (byte)_pushedBack;
                _pushedBack = -1;
                return 1 + stream.ReadAtLeast(buffer[1..], buffer.Length - 1, throwOnEndOfStream: false);
            }
            return stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        }

        public override bool Read(Span<byte> buffer)
        {
            int got = ReadSome(buffer);
            if (got == 0) return false;
            if (got < buffer.Length) throw new FsException("The cpio archive is truncated.");
            Position += got;
            return true;
        }

        public override IFileData? TakeData(long size)
        {
            if (size == 0) return null;
            IFileData data;
            // A byte is pushed back only between archives, never in front of file data.
            if (_pushedBack >= 0) throw new FsException("Corrupt cpio archive: file data where a header should be.");
            try
            {
                data = spool.Append(stream, size);
            }
            catch (EndOfStreamException)
            {
                throw new FsException("The cpio archive is truncated.");
            }
            Position += size;
            return data;
        }

        public override void Skip(long count)
        {
            while (count > 0)
            {
                int n = ReadSome(_scratch.AsSpan(0, (int)Math.Min(_scratch.Length, count)));
                if (n <= 0) throw new FsException("The cpio archive is truncated.");
                count -= n;
                Position += n;
            }
        }

        public override bool SkipZeros()
        {
            Span<byte> one = stackalloc byte[1];
            while (true)
            {
                if (ReadSome(one) == 0) return false;
                if (one[0] == 0) continue;
                _pushedBack = one[0];
                return true;
            }
        }
    }

    // ------------------------------------------------------------ main loop

    /// <summary>
    /// Reads archives until the data ends or something else follows. Returns false when the data has ended,
    /// true when an uncompressed archive was followed by more data, which the caller looks at.
    /// </summary>
    private bool ReadParts(Input input, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        byte[]? magic = null;
        while (true)
        {
            if (magic is null) input.Position = 0;
            _parts++;
            // The header and the form of the names are those of the last archive: the system itself, which comes
            // after the microcode in an initramfs. (An entry for the root in any of them sets its attributes.)
            (_header, _prefixKnown, _prefix) = (null, false, "");
            if (!ReadArchive(input, progress, ct, magic)) return false;
            // Between archives: zeros (padding), then possibly the next one.
            if (!input.SkipZeros()) return false;
            if (input is SourceInput) return true;
            input.Position = 0;
            magic = new byte[6];
            if (!input.Read(magic) || Detect(magic) is null)
            {
                _warnings.Add("The compressed data goes on after the cpio archive with something else; it was ignored.");
                return false;
            }
        }
    }

    /// <summary>Reads the entries up to the trailer; false when the data ended before it.</summary>
    private bool ReadArchive(Input input, IProgress<ProgressInfo>? progress, CancellationToken ct, byte[]? magicRead = null)
    {
        byte[] hdr = new byte[NewcHeaderSize];
        byte[] nameBuf = new byte[256];
        bool first = true;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (magicRead is not null && first)
            {
                magicRead.CopyTo(hdr, 0);
            }
            else if (!input.Read(hdr.AsSpan(0, 6)))
            {
                _truncated = true;
                return false;
            }
            first = false;
            CpioHeader format = Detect(hdr.AsSpan(0, 6))
                ?? throw new FsException($"Corrupt cpio archive: no header where entry {_entries + 1} should start.");
            _header ??= format;
            bool odc = format == CpioHeader.Odc;
            int size = odc ? OdcHeaderSize : NewcHeaderSize;
            if (!input.Read(hdr.AsSpan(6, size - 6))) throw new FsException("The cpio archive is truncated.");

            long ino, mode, uid, gid, nlink, mtime, fileSize, dev, rdevMajor, rdevMinor, nameSize, check;
            if (odc)
            {
                ReadOnlySpan<byte> h = hdr;
                dev = Octal(h.Slice(6, 6), "dev");
                ino = Octal(h.Slice(12, 6), "ino");
                mode = Octal(h.Slice(18, 6), "mode");
                uid = Octal(h.Slice(24, 6), "uid");
                gid = Octal(h.Slice(30, 6), "gid");
                nlink = Octal(h.Slice(36, 6), "nlink");
                long rdev = Octal(h.Slice(42, 6), "rdev");
                (rdevMajor, rdevMinor) = (rdev >> 8, rdev & 0xFF);
                mtime = Octal(h.Slice(48, 11), "mtime");
                nameSize = Octal(h.Slice(59, 6), "namesize");
                fileSize = Octal(h.Slice(65, 11), "filesize");
                check = 0;
            }
            else
            {
                ReadOnlySpan<byte> h = hdr;
                ino = Hex(h.Slice(6, 8), "ino");
                mode = Hex(h.Slice(14, 8), "mode");
                uid = Hex(h.Slice(22, 8), "uid");
                gid = Hex(h.Slice(30, 8), "gid");
                nlink = Hex(h.Slice(38, 8), "nlink");
                mtime = Hex(h.Slice(46, 8), "mtime");
                fileSize = Hex(h.Slice(54, 8), "filesize");
                dev = Hex(h.Slice(62, 8), "devmajor") << 32 | Hex(h.Slice(70, 8), "devminor");
                rdevMajor = Hex(h.Slice(78, 8), "rdevmajor");
                rdevMinor = Hex(h.Slice(86, 8), "rdevminor");
                nameSize = Hex(h.Slice(94, 8), "namesize");
                check = Hex(h.Slice(102, 8), "check");
            }
            if (nameSize is < 1 or > MaxNameSize) throw new FsException("Corrupt cpio header: invalid name size.");
            if (nameBuf.Length < nameSize) nameBuf = new byte[nameSize];
            if (!input.Read(nameBuf.AsSpan(0, (int)nameSize))) throw new FsException("The cpio archive is truncated.");
            if (!odc) input.Skip(Pad4(input.Position));
            ReadOnlySpan<byte> rawName = nameBuf.AsSpan(0, (int)nameSize);
            int nul = rawName.IndexOf((byte)0);
            if (nul >= 0) rawName = rawName[..nul];
            string name = PosixName.Decode(rawName);

            if (name == Trailer)
            {
                input.Skip(fileSize);
                if (!odc) input.Skip(Pad4(input.Position));
                return true;
            }
            _entries++;
            if ((_entries & 0x3FF) == 0) progress?.Report(new ProgressInfo("read", input.Done, input.Total, name));

            FsNodeType? type = UnixMode.TypeFromMode((int)(mode & 0xFFFF));
            IFileData? data = null;
            string? target = null;
            if (type == FsNodeType.Symlink)
            {
                if (fileSize is < 1 or > MaxLinkSize) throw new FsException($"Corrupt cpio archive: the symbolic link '{name}' has an invalid length.");
                byte[] t = new byte[fileSize];
                if (!input.Read(t)) throw new FsException("The cpio archive is truncated.");
                int end = Array.IndexOf(t, (byte)0);
                target = PosixName.Decode(end < 0 ? t : t.AsSpan(0, end));
            }
            else if (type == FsNodeType.File)
            {
                data = input.TakeData(fileSize);
                if (format == CpioHeader.Crc && data is not null) _checksums.Add((data, (uint)check));
            }
            else
            {
                input.Skip(fileSize);
            }
            if (!odc) input.Skip(Pad4(input.Position));

            if (type is null)
            {
                _warnings.Add($"'{name}' has an unknown file type (mode {Convert.ToString(mode, 8)}) and was skipped.");
                continue;
            }
            if (type == FsNodeType.Symlink && string.IsNullOrEmpty(target))
            {
                _warnings.Add($"'{name}': the symbolic link has an empty target and was skipped.");
                continue;
            }

            // Names of a hard-linked inode share one inode object; its data may come with any of them.
            FsInode? inode = null;
            bool linked = type != FsNodeType.Directory && nlink > 1;
            if (linked && _links.TryGetValue((dev, ino), out FsInode? known) && known.Type == type)
            {
                inode = known;
                if (inode.Data is null && data is not null) inode.Data = data;
            }
            if (inode is null)
            {
                inode = new FsInode
                {
                    Type = type.Value,
                    Mode = type == FsNodeType.Symlink ? 0x1FF : (int)(mode & 0xFFF),
                    Uid = (uint)uid,
                    Gid = (uint)gid,
                    MTime = mtime,
                    LinkTarget = target,
                    DevMajor = (uint)rdevMajor,
                    DevMinor = (uint)rdevMinor,
                    Data = data,
                };
                if (linked) _links[(dev, ino)] = inode;
            }
            Insert(name, inode);
        }
    }

    /// <summary>The checksum of the crc format: the sum of all bytes of the file.</summary>
    private static uint Sum(IFileData data)
    {
        uint sum = 0;
        byte[] buffer = new byte[1 << 16];
        using Stream s = data.OpenRead();
        int n;
        while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
            for (int i = 0; i < n; i++) sum += buffer[i];
        return sum;
    }

    // ------------------------------------------------------- tree insertion

    private void TrackPrefix(string rawName)
    {
        string prefix = rawName.StartsWith("./", StringComparison.Ordinal) || rawName == "." ? "./" : "";
        if (!_prefixKnown)
        {
            _prefix = prefix;
            _prefixKnown = true;
        }
        else if (_prefix != prefix && rawName != ".")
        {
            _prefix = "";
        }
    }

    private void Insert(string rawName, FsInode inode)
    {
        var parts = new List<string>();
        foreach (string p in rawName.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (p == ".") continue;
            if (p == "..")
            {
                _warnings.Add($"'{rawName}': the path contains '..' and was skipped.");
                return;
            }
            parts.Add(p);
        }
        // "." alone says nothing about the prefix of the other names.
        if (parts.Count > 0) TrackPrefix(rawName);

        if (parts.Count == 0)
        {
            // The root directory itself.
            if (inode.Type == FsNodeType.Directory)
            {
                FsInode root = _tree.Root.Inode;
                (root.Mode, root.Uid, root.Gid, root.MTime) = (inode.Mode, inode.Uid, inode.Gid, inode.MTime);
                _rootEntry = true;
            }
            return;
        }

        FsNode parent = _tree.Root;
        for (int i = 0; i < parts.Count - 1; i++)
        {
            FsNode? next = parent.Find(parts[i]);
            if (next is null || !next.IsDirectory)
            {
                if (next is not null)
                {
                    _warnings.Add($"'{next.Path}' is replaced by a directory required by '{rawName}'.");
                    _tree.Remove(next);
                }
                next = _tree.CreateDirectory(parent, parts[i], 0x1ED, 0, 0, inode.MTime);
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
        if (existing is not null)
        {
            if (existing.IsDirectory && inode.Type == FsNodeType.Directory)
            {
                // A directory named twice (or created for a file in it first): the entry gives its attributes.
                FsInode d = existing.Inode;
                (d.Mode, d.Uid, d.Gid, d.MTime) = (inode.Mode, inode.Uid, inode.Gid, inode.MTime);
                _implicitDirs.Remove(existing);
                return;
            }
            if (ReferenceEquals(existing.Inode, inode)) return;
            // A later entry replaces an earlier one, as when the kernel unpacks an initramfs.
            _tree.Remove(existing);
        }
        _tree.Add(parent, leaf, inode);
    }
}
