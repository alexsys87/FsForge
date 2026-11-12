namespace FsForge.Core.Ext;

public enum ExtKind
{
    /// <summary>No journal, classic block maps.</summary>
    Ext2,

    /// <summary>Journal, classic block maps.</summary>
    Ext3,

    /// <summary>Extents and the other ext4 on-disk features.</summary>
    Ext4,
}

/// <summary>Facts about an opened ext2/ext3/ext4 image.</summary>
public sealed class ExtInfo
{
    public ExtKind Kind { get; init; }
    public int BlockSize { get; init; }
    public long BlocksCount { get; init; }
    public long FreeBlocks { get; init; }
    public long ReservedBlocks { get; init; }
    public uint InodesCount { get; init; }
    public uint FreeInodes { get; init; }
    public int InodeSize { get; init; }
    public uint BlocksPerGroup { get; init; }
    public uint InodesPerGroup { get; init; }
    public uint FeatureCompat { get; init; }
    public uint FeatureIncompat { get; init; }
    public uint FeatureRoCompat { get; init; }
    public string Label { get; init; } = "";
    public Guid Uuid { get; init; }
    public bool HasJournal { get; init; }
    public long JournalBytes { get; init; }
    public bool NeedsRecovery { get; init; }
    public bool Clean { get; init; }
    public long MkfsTime { get; init; }
    public long WriteTime { get; init; }

    public long SizeBytes => BlocksCount * BlockSize;

    public IEnumerable<string> FeatureNames => ExtFormat.FeatureNames(FeatureCompat, FeatureIncompat, FeatureRoCompat);
}

/// <summary>Parameters of a new ext2/ext3/ext4 image.</summary>
public sealed class ExtOptions
{
    public ExtKind Kind { get; set; } = ExtKind.Ext4;

    /// <summary>1024, 2048 or 4096 bytes.</summary>
    public int BlockSize { get; set; } = 4096;

    /// <summary>Image size in bytes; 0 selects the smallest size that fits the content plus <see cref="ExtraFreeBytes"/>.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Free space to leave when the size is chosen automatically.</summary>
    public long ExtraFreeBytes { get; set; }

    /// <summary>128 or 256 bytes. 256 is required for timestamps past 2038 and in-inode xattrs.</summary>
    public int InodeSize { get; set; } = 256;

    /// <summary>Total number of inodes; 0 selects a value from the content and the image size.</summary>
    public uint InodeCount { get; set; }

    /// <summary>Create a journal (ignored for ext2, forced for ext3).</summary>
    public bool Journal { get; set; } = true;

    /// <summary>Journal size in bytes; 0 selects the mke2fs default for the image size.</summary>
    public long JournalBytes { get; set; }

    public string Label { get; set; } = "";
    public Guid? Uuid { get; set; }

    /// <summary>Percentage of blocks reserved for root (mke2fs -m).</summary>
    public double ReservedPercent { get; set; }

    public bool StoreXattrs { get; set; } = true;
    public bool AllRoot { get; set; }

    /// <summary>Create /lost+found when the tree does not contain it (e2fsck expects it).</summary>
    public bool CreateLostFound { get; set; } = true;

    /// <summary>Store runs of zero blocks as holes.</summary>
    public bool SparseFiles { get; set; } = true;

    /// <summary>Filesystem creation time; null means "now".</summary>
    public long? Timestamp { get; set; }

    public ExtOptions Clone() => (ExtOptions)MemberwiseClone();

    /// <summary>Options that reproduce the geometry of an existing image.</summary>
    public static ExtOptions FromInfo(ExtInfo info) => new()
    {
        Kind = info.Kind,
        BlockSize = info.BlockSize is 1024 or 2048 or 4096 ? info.BlockSize : 4096,
        SizeBytes = info.SizeBytes,
        InodeSize = info.InodeSize >= 256 ? 256 : 128,
        InodeCount = info.InodesCount,
        Journal = info.HasJournal,
        JournalBytes = info.JournalBytes,
        Label = info.Label,
        Uuid = info.Uuid,
        ReservedPercent = info.BlocksCount > 0 ? Math.Round(100.0 * info.ReservedBlocks / info.BlocksCount, 2) : 0,
    };

    public void Validate()
    {
        if (BlockSize is not (1024 or 2048 or 4096)) throw new FsException("The ext block size must be 1024, 2048 or 4096 bytes.");
        if (InodeSize is not (128 or 256)) throw new FsException("The inode size must be 128 or 256 bytes.");
        if (SizeBytes < 0 || ExtraFreeBytes < 0) throw new FsException("The image size cannot be negative.");
        if (ReservedPercent is < 0 or > 50) throw new FsException("The reserved block percentage must be between 0 and 50.");
        if (System.Text.Encoding.UTF8.GetByteCount(Label) > 16) throw new FsException("The volume label is longer than 16 bytes.");
    }
}

internal static class ExtFormat
{
    public const int SuperblockOffset = 1024;
    public const ushort Magic = 0xEF53;
    public const int RootInode = 2;
    public const int JournalInode = 8;
    public const int FirstInode = 11;

    public const uint CompatHasJournal = 0x4, CompatExtAttr = 0x8, CompatResizeInode = 0x10, CompatDirIndex = 0x20;

    public const uint IncompatCompression = 0x1, IncompatFiletype = 0x2, IncompatRecover = 0x4, IncompatJournalDev = 0x8,
        IncompatMetaBg = 0x10, IncompatExtents = 0x40, Incompat64Bit = 0x80, IncompatMmp = 0x100, IncompatFlexBg = 0x200,
        IncompatEaInode = 0x400, IncompatDirData = 0x1000, IncompatCsumSeed = 0x2000, IncompatLargeDir = 0x4000,
        IncompatInlineData = 0x8000, IncompatEncrypt = 0x10000, IncompatCasefold = 0x20000;

    public const uint RoSparseSuper = 0x1, RoLargeFile = 0x2, RoHugeFile = 0x8, RoGdtCsum = 0x10, RoDirNlink = 0x20,
        RoExtraIsize = 0x40, RoQuota = 0x100, RoBigalloc = 0x200, RoMetadataCsum = 0x400, RoReadOnly = 0x1000,
        RoProject = 0x2000, RoSharedBlocks = 0x4000, RoVerity = 0x8000;

    public const uint FlagIndex = 0x1000, FlagHugeFile = 0x40000, FlagExtents = 0x80000, FlagEaInode = 0x200000,
        FlagInlineData = 0x10000000, FlagEncrypt = 0x800;

    public const ushort ExtentMagic = 0xF30A;
    public const uint XattrMagic = 0xEA020000;
    public const uint JournalMagic = 0xC03B3998;

    public const int FtRegular = 1, FtDir = 2, FtChar = 3, FtBlock = 4, FtFifo = 5, FtSocket = 6, FtSymlink = 7;

    public static int FileType(FsNodeType t) => t switch
    {
        FsNodeType.File => FtRegular,
        FsNodeType.Directory => FtDir,
        FsNodeType.CharDevice => FtChar,
        FsNodeType.BlockDevice => FtBlock,
        FsNodeType.Fifo => FtFifo,
        FsNodeType.Socket => FtSocket,
        FsNodeType.Symlink => FtSymlink,
        _ => 0,
    };

    /// <summary>Attribute name prefixes by on-disk name index.</summary>
    public static string? XattrPrefix(int index) => index switch
    {
        1 => "user.",
        2 => "system.posix_acl_access",
        3 => "system.posix_acl_default",
        4 => "trusted.",
        6 => "security.",
        7 => "system.",
        8 => "system.richacl",
        _ => null,
    };

    /// <summary>Splits a full attribute name into the on-disk name index and suffix.</summary>
    public static bool SplitXattrName(string full, out int index, out string suffix)
    {
        if (full == "system.posix_acl_access") { index = 2; suffix = ""; return true; }
        if (full == "system.posix_acl_default") { index = 3; suffix = ""; return true; }
        if (full == "system.richacl") { index = 8; suffix = ""; return true; }
        (string Prefix, int Index)[] map = { ("user.", 1), ("trusted.", 4), ("security.", 6), ("system.", 7) };
        foreach ((string prefix, int idx) in map)
        {
            if (full.StartsWith(prefix, StringComparison.Ordinal) && full.Length > prefix.Length)
            {
                index = idx;
                suffix = full[prefix.Length..];
                return true;
            }
        }
        index = 0;
        suffix = "";
        return false;
    }

    public static bool GroupHasSuper(uint group, bool sparseSuper)
    {
        if (group <= 1 || !sparseSuper) return true;
        if ((group & 1) == 0) return false;
        foreach (uint b in stackalloc uint[] { 3, 5, 7 })
        {
            uint n = b;
            while (n < group) n *= b;
            if (n == group) return true;
        }
        return false;
    }

    public static uint EntryHash(ReadOnlySpan<byte> name, ReadOnlySpan<byte> paddedValue)
    {
        uint hash = 0;
        foreach (byte c in name) hash = (hash << 5) ^ (hash >> 27) ^ c;
        for (int i = 0; i + 4 <= paddedValue.Length; i += 4)
            hash = (hash << 16) ^ (hash >> 16) ^ Le.U32(paddedValue, i);
        return hash;
    }

    // POSIX ACL conversion between the ext4 on-disk form and the xattr (getfacl) form.
    private const int AclUserObj = 1, AclUser = 2, AclGroupObj = 4, AclGroup = 8, AclMask = 0x10, AclOther = 0x20;

    public static byte[]? AclDiskToXattr(ReadOnlySpan<byte> disk)
    {
        if (disk.Length < 4 || Le.U32(disk, 0) != 1) return null;
        var w = new ByteWriter(disk.Length * 2);
        w.U32(2);
        int p = 4;
        while (p + 4 <= disk.Length)
        {
            int tag = Le.U16(disk, p);
            int perm = Le.U16(disk, p + 2);
            uint id = 0xFFFFFFFF;
            if (tag is AclUser or AclGroup)
            {
                if (p + 8 > disk.Length) return null;
                id = Le.U32(disk, p + 4);
                p += 8;
            }
            else if (tag is AclUserObj or AclGroupObj or AclMask or AclOther)
            {
                p += 4;
            }
            else
            {
                return null;
            }
            w.U16(tag);
            w.U16(perm);
            w.U32(id);
        }
        return w.ToArray();
    }

    public static byte[]? AclXattrToDisk(ReadOnlySpan<byte> xattr)
    {
        if (xattr.Length < 4 || Le.U32(xattr, 0) != 2 || (xattr.Length - 4) % 8 != 0) return null;
        var w = new ByteWriter(xattr.Length);
        w.U32(1);
        for (int p = 4; p + 8 <= xattr.Length; p += 8)
        {
            int tag = Le.U16(xattr, p);
            w.U16(tag);
            w.U16(Le.U16(xattr, p + 2));
            if (tag is AclUser or AclGroup) w.U32(Le.U32(xattr, p + 4));
            else if (tag is not (AclUserObj or AclGroupObj or AclMask or AclOther)) return null;
        }
        return w.ToArray();
    }

    public static IEnumerable<string> FeatureNames(uint compat, uint incompat, uint ro)
    {
        (uint Bit, string Name)[] c =
        {
            (0x1, "dir_prealloc"), (0x2, "imagic_inodes"), (0x4, "has_journal"), (0x8, "ext_attr"), (0x10, "resize_inode"),
            (0x20, "dir_index"), (0x200, "sparse_super2"), (0x400, "fast_commit"), (0x1000, "orphan_file"),
        };
        (uint Bit, string Name)[] i =
        {
            (0x1, "compression"), (0x2, "filetype"), (0x4, "needs_recovery"), (0x8, "journal_dev"), (0x10, "meta_bg"),
            (0x40, "extent"), (0x80, "64bit"), (0x100, "mmp"), (0x200, "flex_bg"), (0x400, "ea_inode"), (0x1000, "dirdata"),
            (0x2000, "metadata_csum_seed"), (0x4000, "large_dir"), (0x8000, "inline_data"), (0x10000, "encrypt"), (0x20000, "casefold"),
        };
        (uint Bit, string Name)[] r =
        {
            (0x1, "sparse_super"), (0x2, "large_file"), (0x8, "huge_file"), (0x10, "uninit_bg"), (0x20, "dir_nlink"),
            (0x40, "extra_isize"), (0x100, "quota"), (0x200, "bigalloc"), (0x400, "metadata_csum"), (0x1000, "read-only"),
            (0x2000, "project"), (0x4000, "shared_blocks"), (0x8000, "verity"), (0x10000, "orphan_present"),
        };
        foreach ((uint bit, string name) in c) if ((compat & bit) != 0) yield return name;
        foreach ((uint bit, string name) in i) if ((incompat & bit) != 0) yield return name;
        foreach ((uint bit, string name) in r) if ((ro & bit) != 0) yield return name;
    }
}
