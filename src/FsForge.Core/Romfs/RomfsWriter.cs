using System.Buffers.Binary;
using static FsForge.Core.Romfs.RomfsFormat;

namespace FsForge.Core.Romfs;

/// <summary>
/// Writes a tree as a romfs image the way genromfs does: the volume header, then every directory as a chain of
/// 16-byte aligned entries that starts with "." and "..", a subdirectory's entries right after its own header,
/// file data right after each file's header (aligned as asked), hard links as entries that point at the first
/// name. Every header carries its checksum, the first 512 bytes the checksum the kernel verifies, and the image
/// is padded to a multiple of 1 KiB.
/// </summary>
public sealed class RomfsWriter
{
    private readonly RomfsOptions _o;
    private readonly List<string> _warnings = new();
    private readonly List<Rec> _recs = new();
    private readonly Dictionary<FsInode, Rec> _firstName = new(ReferenceEqualityComparer.Instance);
    private long _pos;
    private long _owners, _modes, _xattrs, _bigDev;

    private RomfsWriter(RomfsOptions options) => _o = options;

    public static IReadOnlyList<string> Write(FsTree tree, Stream output, RomfsOptions options,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        options.Validate();
        var w = new RomfsWriter(options);
        w.Run(tree, output, progress, ct);
        return w._warnings;
    }

    private sealed class Rec
    {
        public long At;
        public required byte[] Name;
        public int Type;
        public bool Exec;
        public uint Spec, Size;
        public long Next;
        public FsNode? Node;            // a file whose data follows, or a symbolic link
        public long DataAt => At + HeaderSize + Align16(Name.Length + 1);
    }

    private void Run(FsTree tree, Stream output, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        byte[] volume = PosixName.Encode(_o.VolumeName);
        _pos = Align16(16 + volume.Length + 1);

        // Layout: the root is the entry "." of its own list, followed by "..", which points back at it.
        FsInode rootIno = tree.Root.Inode;
        Note(rootIno, TypeDirectory, IsExec(rootIno));
        Rec root = Place(".", TypeDirectory, IsExec(rootIno), 0, 0);
        root.Spec = (uint)root.At;
        Rec dotdot = Place("..", TypeHardLink, false, (uint)root.At, 0);
        root.Next = dotdot.At;
        LayoutChildren(tree.Root, dotdot, root, ct);

        long size = Align16(_pos);
        if (size > uint.MaxValue) throw new FsException("The image is too large for romfs (4 GiB).");
        long padded = (size + 1023) & ~1023L;

        // Write, keeping the first 512 bytes back until their checksum is known.
        var head = new MemoryStream();
        long written = 0;
        void Emit(ReadOnlySpan<byte> b)
        {
            if (written < 512)
            {
                int n = (int)Math.Min(b.Length, 512 - written);
                head.Write(b[..n]);
                written += n;
                b = b[n..];
                if (written == 512 || written == padded) FlushHead();
            }
            if (b.Length > 0)
            {
                output.Write(b);
                written += b.Length;
            }
        }
        void FlushHead()
        {
            byte[] h = head.ToArray();
            int span = (int)Math.Min(512, size) & ~3;
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(12), 0);
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(12), unchecked(0u - Sum(h.AsSpan(0, span))));
            output.Write(h);
        }

        byte[] sb = new byte[Align16(16 + volume.Length + 1)];
        Magic.CopyTo(sb);
        BinaryPrimitives.WriteUInt32BigEndian(sb.AsSpan(8), (uint)size);
        volume.CopyTo(sb, 16);
        Emit(sb);

        long total = tree.GetStats().DataBytes, done = 0;
        byte[] buffer = new byte[1 << 16];
        foreach (Rec r in _recs)
        {
            ct.ThrowIfCancellationRequested();
            if (r.At > written) Emit(new byte[r.At - written]);
            byte[] h = new byte[HeaderSize + Align16(r.Name.Length + 1)];
            BinaryPrimitives.WriteUInt32BigEndian(h, (uint)r.Next | (uint)r.Type | (r.Exec ? ExecBit : 0u));
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(4), r.Spec);
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(8), r.Size);
            r.Name.CopyTo(h, HeaderSize);
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(12), unchecked(0u - Sum(h)));
            Emit(h);
            if (r.Node is null || r.Size == 0) continue;
            if (r.Type == TypeSymlink) Emit(PosixName.Encode(r.Node.Inode.LinkTarget ?? ""));
            else
            {
                using Stream s = r.Node.Inode.Data!.OpenRead();
                long left = r.Size;
                while (left > 0)
                {
                    int n = s.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (n <= 0) throw new FsException($"'{r.Node.Path}' became shorter while it was being written.");
                    Emit(buffer.AsSpan(0, n));
                    left -= n;
                    done += n;
                    progress?.Report(new ProgressInfo("write", done, total, r.Node.Path));
                }
            }
        }
        if (padded > written) Emit(new byte[padded - written]);
        output.Flush();

        if (_owners > 0) _warnings.Add($"romfs stores no owners: {_owners} object(s) not owned by root will belong to root.");
        if (_modes > 0) _warnings.Add($"romfs stores only whether a file is executable: the permissions of {_modes} object(s) (for example setuid or a mode other than 644/755) were not kept.");
        if (_bigDev > 0) _warnings.Add($"{_bigDev} device(s) have a major or minor number above 65535, which romfs cannot store: it was cut to 16 bits.");
        if (_xattrs > 0) _warnings.Add($"{_xattrs} object(s) have extended attributes (for example SELinux labels or capabilities), which romfs does not store: they were left out.");
    }

    /// <summary>Lays out the entries of a directory after <paramref name="last"/> (its "..").</summary>
    private void LayoutChildren(FsNode dir, Rec last, Rec dirRec, CancellationToken ct)
    {
        foreach (FsNode k in Sorted(dir))
        {
            ct.ThrowIfCancellationRequested();
            FsInode ino = k.Inode;
            Rec rec;
            if (ino.Type != FsNodeType.Directory && ino.LinkCount > 1 && _firstName.TryGetValue(ino, out Rec? first))
            {
                rec = Place(k.Name, TypeHardLink, false, (uint)first.At, 0, k);
            }
            else
            {
                int type = ino.Type switch
                {
                    FsNodeType.Directory => TypeDirectory,
                    FsNodeType.File => TypeFile,
                    FsNodeType.Symlink => TypeSymlink,
                    FsNodeType.BlockDevice => TypeBlock,
                    FsNodeType.CharDevice => TypeChar,
                    FsNodeType.Socket => TypeSocket,
                    _ => TypeFifo,
                };
                bool exec = type is TypeDirectory or TypeFile && IsExec(ino);
                Note(ino, type, exec);
                uint spec = 0, size = 0;
                switch (type)
                {
                    case TypeFile:
                        if (ino.Size > uint.MaxValue) throw new FsException($"'{k.Path}' is larger than 4 GiB, the limit of a file in romfs.");
                        size = (uint)ino.Size;
                        break;
                    case TypeSymlink:
                        size = (uint)PosixName.Encode(ino.LinkTarget ?? "").Length;
                        break;
                    case TypeBlock or TypeChar:
                        if (ino.DevMajor > 0xFFFF || ino.DevMinor > 0xFFFF) _bigDev++;
                        spec = ((ino.DevMajor & 0xFFFF) << 16) | (ino.DevMinor & 0xFFFF);
                        break;
                }
                rec = Place(k.Name, type, exec, spec, size, k);
                if (type is TypeFile or TypeSymlink) rec.Node = k;
                if (ino.LinkCount > 1 && type != TypeDirectory) _firstName[ino] = rec;
            }
            last.Next = rec.At;
            last = rec;
            if (k.IsDirectory)
            {
                // The entries of a subdirectory follow its header: ".", "..", then what it holds.
                Rec dot = Place(".", TypeHardLink, false, (uint)rec.At, 0);
                rec.Spec = (uint)dot.At;
                Rec up = Place("..", TypeHardLink, false, (uint)dirRec.At, 0);
                dot.Next = up.At;
                LayoutChildren(k, up, rec, ct);
            }
        }
    }

    private Rec Place(string name, int type, bool exec, uint spec, uint size, FsNode? node = null)
    {
        byte[] bytes = PosixName.Encode(name);
        if (bytes.Length > MaxNameLength)
            throw new FsException($"'{node?.Path ?? name}': the name is longer than {MaxNameLength} bytes, the limit of romfs.");
        long header = HeaderSize + Align16(bytes.Length + 1);
        long at = Align16(_pos);
        if (type == TypeFile && size > 0 && _o.DataAlignment > 16)
        {
            long a = _o.DataAlignment;
            at = (at + header + a - 1) / a * a - header;            // so that the data starts aligned
        }
        if (at > uint.MaxValue) throw new FsException("The image is too large for romfs (4 GiB).");
        var rec = new Rec { At = at, Name = bytes, Type = type, Exec = exec, Spec = spec, Size = size };
        _recs.Add(rec);
        _pos = at + header + (type is TypeFile or TypeSymlink ? Align16(size) : 0);
        return rec;
    }

    private void Note(FsInode ino, int type, bool exec)
    {
        if (ino.Uid != 0 || ino.Gid != 0) _owners++;
        if (ino.Type != FsNodeType.Symlink && (ino.Mode & 0xFFF) != ModeOf(type, exec)) _modes++;
        if (ino.HasXAttrs) _xattrs++;
    }

    private static bool IsExec(FsInode ino) => (ino.Mode & 0x49) != 0;

    private static List<FsNode> Sorted(FsNode dir)
    {
        var list = dir.Children.ToList();
        list.Sort((a, b) => PosixName.Encode(a.Name).AsSpan().SequenceCompareTo(PosixName.Encode(b.Name)));
        return list;
    }
}
