using System.Text;
using FsForge.Core.Compression;
using static FsForge.Core.Jffs2.Jffs2Format;

namespace FsForge.Core.Jffs2;

/// <summary>
/// Writes a tree as a JFFS2 image the way mkfs.jffs2 does: a clean marker at the start of every erase
/// block, then for each directory entry a dirent node followed by the inode nodes of the object, with
/// file data cut into pages. No node crosses an erase block; the rest of a block that cannot hold the
/// next node is left erased (0xFF).
/// </summary>
public static class Jffs2Writer
{
    public static IReadOnlyList<string> Write(FsTree tree, Stream output, Jffs2Options options,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        options.Validate();
        var w = new Writer(output, options);
        w.Run(tree, progress, ct);
        return w.Warnings;
    }

    private sealed class Writer(Stream output, Jffs2Options o)
    {
        private readonly bool _be = o.BigEndian;
        private readonly int _ebs = o.EraseBlockSize;
        private readonly byte[] _ff = Enumerable.Repeat((byte)0xFF, 4096).ToArray();
        private readonly ZlibCodec _zlib = new(9);
        private readonly LzoCodec _lzo = new();
        private readonly Dictionary<FsInode, uint> _inos = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, uint> _xids = new(StringComparer.Ordinal);
        private long _pos;
        private uint _nextIno = 2, _direntVersion, _nextXid = 1, _xrefSeq;
        private int _droppedXattrs;
        private long _done, _total;

        public List<string> Warnings { get; } = new();

        public void Run(FsTree tree, IProgress<ProgressInfo>? progress, CancellationToken ct)
        {
            _total = Math.Max(1, FsTree.GetStats(tree.Root).DataBytes);
            // The root gets a node of its own so that its mode, owner and time survive (mkfs.jffs2 leaves it out).
            _inos[tree.Root.Inode] = 1;
            WriteInode(tree.Root.Inode, 1, progress, ct);
            WriteDirectory(tree.Root, 1, progress, ct);
            if (o.PadToEraseBlock) PadToBoundary();
            if (_droppedXattrs > 0)
                Warnings.Add($"Extended attributes of {_droppedXattrs} object(s) were not written: JFFS2 attributes need a kernel built with " +
                             "CONFIG_JFFS2_FS_XATTR, so they are written only when the option to store them is on.");
            progress?.Report(new ProgressInfo("done", _total, _total));
        }

        private void WriteDirectory(FsNode dir, uint dirIno, IProgress<ProgressInfo>? progress, CancellationToken ct)
        {
            foreach (FsNode child in dir.SortedChildren())
            {
                ct.ThrowIfCancellationRequested();
                byte[] name = PosixName.Encode(child.Name);
                if (name.Length > MaxNameLength)
                    throw new FsException($"'{child.Path}': JFFS2 names are at most {MaxNameLength} bytes long.");
                bool known = _inos.TryGetValue(child.Inode, out uint ino);
                if (!known)
                {
                    if (_nextIno == uint.MaxValue) throw new FsException("Too many objects for JFFS2.");
                    ino = _nextIno++;
                    _inos[child.Inode] = ino;
                }
                Emit(Dirent(dirIno, ino, child, name, dir.Inode.MTime));
                if (known) continue;          // another name of a file already written
                WriteInode(child.Inode, ino, progress, ct, child.Path);
                if (child.IsDirectory) WriteDirectory(child, ino, progress, ct);
            }
        }

        // ------------------------------------------------------------ inodes

        private void WriteInode(FsInode inode, uint ino, IProgress<ProgressInfo>? progress, CancellationToken ct, string path = "/")
        {
            uint uid = o.AllRoot ? 0 : inode.Uid, gid = o.AllRoot ? 0 : inode.Gid;
            if (uid > ushort.MaxValue || gid > ushort.MaxValue)
                throw new FsException($"'{path}': owner {uid}:{gid} cannot be stored in JFFS2, which keeps 16-bit user and group ids.");
            uint mode = (uint)(UnixMode.TypeBits(inode.Type) | (inode.Mode & 0xFFF));
            uint mtime = (uint)Math.Clamp(inode.MTime, 0, uint.MaxValue);
            var meta = new Meta(ino, mode, (ushort)uid, (ushort)gid, mtime);

            switch (inode.Type)
            {
                case FsNodeType.File:
                    WriteFileData(inode, meta, progress, ct, path);
                    break;
                case FsNodeType.Symlink:
                {
                    byte[] target = PosixName.Encode(inode.LinkTarget ?? "");
                    if (target.Length == 0) throw new FsException($"'{path}': a symbolic link needs a target.");
                    if (target.Length > o.PageSize) throw new FsException($"'{path}': the link target is longer than {o.PageSize} bytes.");
                    Emit(Inode(meta, 1, (uint)target.Length, 0, target, (uint)target.Length, Jffs2Compression.None));
                    break;
                }
                case FsNodeType.CharDevice or FsNodeType.BlockDevice:
                {
                    // Like the kernel: the old 16-bit form when it is enough, the 32-bit one otherwise.
                    byte[] dev;
                    if (inode.DevMajor < 256 && inode.DevMinor < 256)
                    {
                        dev = new byte[2];
                        W16(dev, 0, (int)((inode.DevMajor << 8) | inode.DevMinor), _be);
                    }
                    else
                    {
                        dev = new byte[4];
                        W32(dev, 0, (inode.DevMinor & 0xFF) | (inode.DevMajor << 8) | ((inode.DevMinor & ~0xFFu) << 12), _be);
                    }
                    Emit(Inode(meta, 1, 0, 0, dev, (uint)dev.Length, Jffs2Compression.None));
                    break;
                }
                default:
                    // Directories, FIFOs and sockets: just the metadata.
                    Emit(Inode(meta, 1, 0, 0, Array.Empty<byte>(), 0, Jffs2Compression.None));
                    break;
            }
            WriteXattrs(inode, ino, path);
        }

        private readonly record struct Meta(uint Ino, uint Mode, ushort Uid, ushort Gid, uint MTime);

        private void WriteFileData(FsInode inode, Meta meta, IProgress<ProgressInfo>? progress, CancellationToken ct, string path)
        {
            long size = inode.Data?.Length ?? 0;
            if (size > uint.MaxValue) throw new FsException($"'{path}' is larger than 4 GiB, the limit of JFFS2.");
            if (size == 0 || inode.Data is null)
            {
                Emit(Inode(meta, 1, 0, 0, Array.Empty<byte>(), 0, Jffs2Compression.None));
                return;
            }
            byte[] page = new byte[o.PageSize];
            byte[] packed = new byte[o.PageSize];
            uint version = 1;
            using Stream s = inode.Data.OpenRead();
            for (long offset = 0; offset < size; offset += o.PageSize)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(o.PageSize, size - offset);
                s.ReadExactly(page, 0, n);
                ReadOnlySpan<byte> plain = page.AsSpan(0, n);
                (Jffs2Compression compr, int csize) = Compress(plain, packed);
                ReadOnlySpan<byte> data = compr == Jffs2Compression.None ? plain : packed.AsSpan(0, csize);
                Emit(Inode(meta, version++, (uint)size, (uint)offset, data, (uint)n, compr));
                _done += n;
                progress?.Report(new ProgressInfo("data", _done, _total, path));
            }
            if (s.ReadByte() >= 0) throw new FsException($"'{path}' changed while it was being written.");
        }

        /// <summary>The chosen compressor, then rtime (as mkfs.jffs2 does after zlib), then none.</summary>
        private (Jffs2Compression, int) Compress(ReadOnlySpan<byte> plain, byte[] packed)
        {
            Span<byte> room = packed.AsSpan(0, Math.Max(0, plain.Length - 1));
            int n = -1;
            switch (o.Compression)
            {
                case Jffs2Compression.Zlib:
                    n = _zlib.Compress(plain, room);
                    if (n > 0 && n < plain.Length) return (Jffs2Compression.Zlib, n);
                    n = RtimeCompress(plain, room);
                    if (n > 0 && n < plain.Length) return (Jffs2Compression.Rtime, n);
                    break;
                case Jffs2Compression.Lzo:
                    // The LZO encoder may need more room than the input for data that does not shrink.
                    byte[] big = new byte[plain.Length + plain.Length / 16 + 64 + 3];
                    n = _lzo.Compress(plain, big);
                    if (n > 0 && n < plain.Length)
                    {
                        big.AsSpan(0, n).CopyTo(packed);
                        return (Jffs2Compression.Lzo, n);
                    }
                    break;
                case Jffs2Compression.Rtime:
                    n = RtimeCompress(plain, room);
                    if (n > 0 && n < plain.Length) return (Jffs2Compression.Rtime, n);
                    break;
            }
            return (Jffs2Compression.None, plain.Length);
        }

        // ------------------------------------------------------------ xattrs

        private void WriteXattrs(FsInode inode, uint ino, string path)
        {
            if (inode.XAttrs is not { Count: > 0 } xattrs) return;
            if (!o.StoreXattrs)
            {
                _droppedXattrs++;
                return;
            }
            foreach ((string full, byte[] value) in xattrs)
            {
                (byte prefix, string name)? split = full switch
                {
                    "system.posix_acl_access" => (XprefixAclAccess, ""),
                    "system.posix_acl_default" => (XprefixAclDefault, ""),
                    _ when full.StartsWith("user.", StringComparison.Ordinal) => (XprefixUser, full[5..]),
                    _ when full.StartsWith("security.", StringComparison.Ordinal) => (XprefixSecurity, full[9..]),
                    _ when full.StartsWith("trusted.", StringComparison.Ordinal) => (XprefixTrusted, full[8..]),
                    _ => null,
                };
                if (split is not { } x)
                {
                    Warnings.Add($"{path}: extended attribute '{full}' cannot be stored in JFFS2 and was skipped.");
                    continue;
                }
                byte[] stored = value;
                if (x.prefix is XprefixAclAccess or XprefixAclDefault)
                {
                    if (AclFromXattr(value, _be) is not { } acl)
                    {
                        Warnings.Add($"{path}: malformed POSIX ACL '{full}' was skipped.");
                        continue;
                    }
                    stored = acl;
                }
                byte[] name = PosixName.Encode(x.name);
                if (name.Length > 255 || stored.Length > ushort.MaxValue || XattrSize + name.Length + 1 + stored.Length > _ebs / 2)
                {
                    Warnings.Add($"{path}: extended attribute '{full}' is too large for JFFS2 and was skipped.");
                    continue;
                }
                // Equal attributes are stored once and referenced from every inode that has them.
                string key = x.prefix + "\0" + Convert.ToHexString(name) + "\0" + Convert.ToHexString(stored);
                if (!_xids.TryGetValue(key, out uint xid))
                {
                    xid = _nextXid++;
                    _xids[key] = xid;
                    Emit(Xattr(xid, x.prefix, name, stored));
                }
                _xrefSeq += 2;               // even: a live reference
                Emit(Xref(ino, xid, _xrefSeq));
            }
        }

        // ------------------------------------------------------------- nodes

        private void Header(Span<byte> b, ushort type, int totlen)
        {
            W16(b, 0, Magic, _be);
            W16(b, 2, type, _be);
            W32(b, 4, (uint)totlen, _be);
            W32(b, 8, Crc(b[..8]), _be);
        }

        private byte[] Inode(Meta m, uint version, uint isize, uint offset, ReadOnlySpan<byte> data, uint dsize, Jffs2Compression compr)
        {
            byte[] b = new byte[InodeSize + data.Length];
            Header(b, NodeInode, b.Length);
            W32(b, 12, m.Ino, _be);
            W32(b, 16, version, _be);
            W32(b, 20, m.Mode, _be);
            W16(b, 24, m.Uid, _be);
            W16(b, 26, m.Gid, _be);
            W32(b, 28, isize, _be);
            W32(b, 32, m.MTime, _be);          // atime
            W32(b, 36, m.MTime, _be);
            W32(b, 40, m.MTime, _be);          // ctime
            W32(b, 44, offset, _be);
            W32(b, 48, (uint)data.Length, _be);
            W32(b, 52, dsize, _be);
            b[56] = (byte)compr;
            W32(b, 60, Crc(data), _be);
            W32(b, 64, Crc(b.AsSpan(0, 60)), _be);
            data.CopyTo(b.AsSpan(InodeSize));
            return b;
        }

        private byte[] Dirent(uint pino, uint ino, FsNode child, byte[] name, long dirTime)
        {
            byte[] b = new byte[DirentSize + name.Length];
            Header(b, NodeDirent, b.Length);
            W32(b, 12, pino, _be);
            W32(b, 16, _direntVersion++, _be);
            W32(b, 20, ino, _be);
            W32(b, 24, (uint)Math.Clamp(dirTime, 0, uint.MaxValue), _be);
            b[28] = (byte)name.Length;
            b[29] = (byte)(child.Type switch
            {
                FsNodeType.Directory => DtDir,
                FsNodeType.File => DtReg,
                FsNodeType.Symlink => DtLnk,
                FsNodeType.CharDevice => DtChr,
                FsNodeType.BlockDevice => DtBlk,
                FsNodeType.Fifo => DtFifo,
                _ => DtSock,
            });
            W32(b, 32, Crc(b.AsSpan(0, 32)), _be);
            W32(b, 36, Crc(name), _be);
            name.CopyTo(b, DirentSize);
            return b;
        }

        private byte[] Xattr(uint xid, byte prefix, byte[] name, byte[] value)
        {
            byte[] b = new byte[XattrSize + name.Length + 1 + value.Length];
            Header(b, NodeXattr, b.Length);
            W32(b, 12, xid, _be);
            W32(b, 16, 1, _be);
            b[20] = prefix;
            b[21] = (byte)name.Length;
            W16(b, 22, value.Length, _be);
            name.CopyTo(b, XattrSize);
            value.CopyTo(b, XattrSize + name.Length + 1);
            W32(b, 24, Crc(b.AsSpan(XattrSize)), _be);
            W32(b, 28, Crc(b.AsSpan(0, 28)), _be);
            return b;
        }

        private byte[] Xref(uint ino, uint xid, uint seq)
        {
            byte[] b = new byte[XrefSize];
            Header(b, NodeXref, b.Length);
            W32(b, 12, ino, _be);
            W32(b, 16, xid, _be);
            W32(b, 20, seq, _be);
            W32(b, 24, Crc(b.AsSpan(0, 24)), _be);
            return b;
        }

        // ----------------------------------------------------------- layout

        private void Emit(byte[] node)
        {
            int room = _ebs - (o.Cleanmarkers ? o.CleanmarkerSize : 0);
            if (node.Length > room) throw new FsException($"A JFFS2 node of {node.Length} bytes does not fit into an erase block of {_ebs} bytes.");
            StartBlock();
            if (_pos % _ebs + node.Length > _ebs)
            {
                PadToBoundary();
                StartBlock();
            }
            output.Write(node);
            _pos += node.Length;
            Fill((int)(Pad4(_pos) - _pos));
        }

        private void StartBlock()
        {
            if (!o.Cleanmarkers || _pos % _ebs != 0) return;
            byte[] c = new byte[HeaderSize];
            Header(c, NodeCleanmarker, o.CleanmarkerSize);
            output.Write(c);
            _pos += c.Length;
            Fill(o.CleanmarkerSize - HeaderSize);
        }

        private void PadToBoundary()
        {
            long rest = (_ebs - _pos % _ebs) % _ebs;
            Fill(rest);
        }

        private void Fill(long count)
        {
            while (count > 0)
            {
                int n = (int)Math.Min(count, _ff.Length);
                output.Write(_ff, 0, n);
                _pos += n;
                count -= n;
            }
        }
    }
}
