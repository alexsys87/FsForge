using FsForge.Core;
using FsForge.Core.Compression;
using FsForge.Core.Ext;
using FsForge.Core.Images;
using FsForge.Core.SquashFs;
using FsForge.Core.Tar;
using FsForge.Presentation.Services;

namespace FsForge.Presentation.ViewModels;

/// <summary>Target format as the user sees it (the three ext generations are separate choices).</summary>
public enum SaveFormat
{
    SquashFs,
    Ext4,
    Ext3,
    Ext2,
    Tar,
}

/// <summary>How the size of an ext image is chosen.</summary>
public enum ExtSizeMode
{
    /// <summary>Same size as the image that was opened.</summary>
    Keep,

    /// <summary>The smallest size that holds the content, plus the requested free space.</summary>
    Minimal,

    /// <summary>A size given in MiB.</summary>
    Fixed,
}

/// <summary>Lists of choices shared by the save dialog and the settings.</summary>
public static class OptionLists
{
    public static IReadOnlyList<Choice<SaveFormat>> Formats { get; } = new[]
    {
        new Choice<SaveFormat>(SaveFormat.SquashFs, "SquashFS"),
        new Choice<SaveFormat>(SaveFormat.Ext4, "ext4"),
        new Choice<SaveFormat>(SaveFormat.Ext3, "ext3"),
        new Choice<SaveFormat>(SaveFormat.Ext2, "ext2"),
        new Choice<SaveFormat>(SaveFormat.Tar, "Архив tar (rootfs.tar.gz)"),
    };

    public static IReadOnlyList<Choice<SquashCompression>> SquashCompressions { get; } = new[]
    {
        new Choice<SquashCompression>(SquashCompression.Xz, "xz — лучшее сжатие"),
        new Choice<SquashCompression>(SquashCompression.Zstd, "zstd — быстро и плотно"),
        new Choice<SquashCompression>(SquashCompression.Gzip, "gzip — максимальная совместимость"),
        new Choice<SquashCompression>(SquashCompression.Lz4, "lz4 — самая быстрая распаковка"),
    };

    public static IReadOnlyList<Choice<int>> SquashBlockSizes { get; } =
        new[] { 4, 8, 16, 32, 64, 128, 256, 512, 1024 }.Select(k => new Choice<int>(k * 1024, k < 1024 ? $"{k} КБ" : "1 МБ")).ToArray();

    public static IReadOnlyList<Choice<int>> XzEfforts { get; } = new[]
    {
        new Choice<int>(0, "быстро"), new Choice<int>(1, "обычно"), new Choice<int>(2, "максимум (медленно)"),
    };

    public static IReadOnlyList<Choice<int>> GzipLevels { get; } =
        Enumerable.Range(1, 9).Select(l => new Choice<int>(l, l == 1 ? "1 — быстрее" : l == 9 ? "9 — плотнее" : l.ToString())).ToArray();

    public static IReadOnlyList<Choice<int>> ZstdLevels { get; } =
        new[] { 1, 3, 6, 10, 15, 19, 22 }.Select(l => new Choice<int>(l, l == 1 ? "1 — быстрее" : l == 22 ? "22 — плотнее" : l.ToString())).ToArray();

    /// <summary>Value -1 stands for "no filter".</summary>
    public static IReadOnlyList<Choice<int>> BcjFilters { get; } = new[]
    {
        new Choice<int>(-1, "нет"),
        new Choice<int>((int)BcjFilter.Arm, "ARM"),
        new Choice<int>((int)BcjFilter.ArmThumb, "ARM Thumb"),
        new Choice<int>((int)BcjFilter.Arm64, "ARM64"),
        new Choice<int>((int)BcjFilter.X86, "x86 / x86-64"),
        new Choice<int>((int)BcjFilter.PowerPc, "PowerPC"),
        new Choice<int>((int)BcjFilter.Sparc, "SPARC"),
    };

    public static IReadOnlyList<Choice<int>> ExtBlockSizes { get; } = new[]
    {
        new Choice<int>(4096, "4096 байт"), new Choice<int>(2048, "2048 байт"), new Choice<int>(1024, "1024 байта"),
    };

    public static IReadOnlyList<Choice<int>> ExtInodeSizes { get; } = new[]
    {
        new Choice<int>(256, "256 байт (время после 2038 года)"), new Choice<int>(128, "128 байт (компактнее)"),
    };

    public static IReadOnlyList<Choice<ArchiveCompression>> TarCompressions { get; } = new[]
    {
        new Choice<ArchiveCompression>(ArchiveCompression.Gzip, "gzip (.tar.gz)"),
        new Choice<ArchiveCompression>(ArchiveCompression.Xz, "xz (.tar.xz)"),
        new Choice<ArchiveCompression>(ArchiveCompression.Zstd, "zstd (.tar.zst)"),
        new Choice<ArchiveCompression>(ArchiveCompression.None, "без сжатия (.tar)"),
    };

    public static IReadOnlyList<Choice<TarDialect>> TarDialects { get; } = new[]
    {
        new Choice<TarDialect>(TarDialect.Pax, "pax (POSIX) — хранит расширенные атрибуты"),
        new Choice<TarDialect>(TarDialect.Gnu, "GNU tar — без расширенных атрибутов"),
    };

    public static IReadOnlyList<Choice<string>> TarPrefixes { get; } = new[]
    {
        new Choice<string>("./", "./ — как у «tar -C rootfs .»"), new Choice<string>("", "без префикса"),
    };

    public static IReadOnlyList<Choice<VerifyLevel>> VerifyLevels { get; } = new[]
    {
        new Choice<VerifyLevel>(VerifyLevel.Structure, "структуру: имена, типы, размеры, права"),
        new Choice<VerifyLevel>(VerifyLevel.Content, "структуру и содержимое каждого файла"),
        new Choice<VerifyLevel>(VerifyLevel.None, "не проверять"),
    };
}

/// <summary>The "Save as" dialog: target format and its parameters.</summary>
public sealed class SaveOptionsViewModel : DialogViewModel
{
    private readonly SaveOptions _o;
    private readonly ImageDocument _doc;
    private SaveFormat _format;
    private ExtSizeMode _sizeMode;
    private string _sizeMiB, _extraFreeMiB, _label, _reserved;
    private int _bcj;

    public SaveOptionsViewModel(ImageDocument doc, SaveOptions options, string title = "Параметры сохранения", bool lockFormat = false)
    {
        _doc = doc;
        _o = options.Clone();
        Title = title;
        AcceptText = "Далее…";
        FormatLocked = lockFormat;
        _format = _o.Format switch
        {
            ImageFormat.SquashFs => SaveFormat.SquashFs,
            ImageFormat.Tar => SaveFormat.Tar,
            _ => _o.Ext.Kind switch { ExtKind.Ext2 => SaveFormat.Ext2, ExtKind.Ext3 => SaveFormat.Ext3, _ => SaveFormat.Ext4 },
        };
        CanKeepSize = doc.Ext is not null && _o.Ext.SizeBytes > 0;
        _sizeMode = CanKeepSize ? ExtSizeMode.Keep : _o.Ext.SizeBytes > 0 ? ExtSizeMode.Fixed : ExtSizeMode.Minimal;
        _sizeMiB = _o.Ext.SizeBytes > 0 ? Math.Ceiling(_o.Ext.SizeBytes / 1048576.0).ToString("0") : "";
        _extraFreeMiB = (_o.Ext.ExtraFreeBytes >> 20).ToString();
        _label = _o.Ext.Label;
        _reserved = _o.Ext.ReservedPercent.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        _bcj = _o.SquashFs.XzFilters.Count > 0 ? (int)_o.SquashFs.XzFilters[0] : -1;

        TreeStats st = doc.Tree.GetStats();
        ContentSummary = $"Содержимое: {Fmt.Plural(st.Files, "файл", "файла", "файлов")}, {Fmt.Plural(st.Directories, "папка", "папки", "папок")}, {Fmt.Size(st.DataBytes)}";
        OriginalSizeText = CanKeepSize ? Fmt.Size(_o.Ext.SizeBytes) : "";

        var sizeModes = new List<Choice<ExtSizeMode>>();
        if (CanKeepSize) sizeModes.Add(new Choice<ExtSizeMode>(ExtSizeMode.Keep, $"как у исходного образа ({OriginalSizeText})"));
        sizeModes.Add(new Choice<ExtSizeMode>(ExtSizeMode.Minimal, "минимальный по содержимому"));
        sizeModes.Add(new Choice<ExtSizeMode>(ExtSizeMode.Fixed, "заданный, МиБ"));
        SizeModes = sizeModes;

        if (doc.Container is { } disk)
        {
            PartitionInfo p = disk.Partition;
            ContainerSummary = $"{Path.GetFileName(doc.FilePath)} — образ диска ({Fmt.PartitionScheme(disk.Table)}, разделов: {disk.Table.Partitions.Count}). " +
                               $"Открыт раздел {p.Number}: {Fmt.PartitionContent(p)}, {Fmt.Size(p.Length)}.";
        }
    }

    // ---- disk image the filesystem was opened from
    /// <summary>The document came from a partition of a disk image.</summary>
    public bool HasContainer => _doc.Container is not null;

    /// <summary>The choice between the disk image and the filesystem alone is offered (not when exporting an archive).</summary>
    public bool ShowContainerOptions => HasContainer && !FormatLocked;

    public string ContainerSummary { get; } = "";

    /// <summary>Write the whole disk image again (true) or the filesystem alone (false).</summary>
    public bool KeepContainer
    {
        get => _o.KeepContainer;
        set
        {
            if (_o.KeepContainer == value) return;
            _o.KeepContainer = value;
            Error = null;
            OnPropertyChanged();
            RaiseContainer();
        }
    }

    /// <summary>The result is a disk image: the container is kept and the format can live in a partition.</summary>
    public bool WritesDisk => HasContainer && _o.KeepContainer && !IsTar;

    public string ContainerNote
    {
        get
        {
            if (_doc.Container is not { } disk) return "";
            PartitionInfo p = disk.Partition;
            if (IsTar) return "Архив tar сохраняется отдельным файлом; образ диска при этом не меняется.";
            if (!_o.KeepContainer)
                return "Будет записана только файловая система — без таблицы разделов и остальных разделов. Такой файл не загрузится с карты памяти; он годится для sysupgrade или записи в раздел вручную.";
            string grow = disk.CanGrow
                ? $"Если файловая система перестанет помещаться в раздел {p.Number}, раздел будет увеличен (он последний)."
                : $"Файловая система должна поместиться в раздел {p.Number} ({Fmt.Size(p.Length)}).";
            return $"Таблица разделов, загрузчик и остальные разделы копируются без изменений, заменяется только раздел {p.Number}. {grow} " +
                   "Имя файла с окончанием .gz, .xz или .zst означает сжатый образ.";
        }
    }

    /// <summary>How the ext size choices are read when the filesystem goes into a partition.</summary>
    public string ExtSizeNote => WritesDisk && _doc.Container is { } disk
        ? $"В образе диска «минимальный» означает размер раздела ({Fmt.Size(disk.Partition.Length)}): файловая система занимает раздел целиком."
        : "";

    public bool HasExtSizeNote => ExtSizeNote.Length > 0;

    private void RaiseContainer()
    {
        foreach (string p in new[] { nameof(WritesDisk), nameof(ContainerNote), nameof(ExtSizeNote), nameof(HasExtSizeNote) }) OnPropertyChanged(p);
    }

    public bool FormatLocked { get; }
    public bool FormatEditable => !FormatLocked;
    public string ContentSummary { get; }
    public string OriginalSizeText { get; }
    public bool CanKeepSize { get; }
    public IReadOnlyList<Choice<ExtSizeMode>> SizeModes { get; }

    // The option lists are exposed as instance properties so that XAML can bind to them directly.
    public IReadOnlyList<Choice<SaveFormat>> Formats => OptionLists.Formats;
    public IReadOnlyList<Choice<SquashCompression>> SquashCompressions => OptionLists.SquashCompressions;
    public IReadOnlyList<Choice<int>> SquashBlockSizes => OptionLists.SquashBlockSizes;
    public IReadOnlyList<Choice<int>> XzEfforts => OptionLists.XzEfforts;
    public IReadOnlyList<Choice<int>> GzipLevels => OptionLists.GzipLevels;
    public IReadOnlyList<Choice<int>> ZstdLevels => OptionLists.ZstdLevels;
    public IReadOnlyList<Choice<int>> BcjFilters => OptionLists.BcjFilters;
    public IReadOnlyList<Choice<int>> ExtBlockSizes => OptionLists.ExtBlockSizes;
    public IReadOnlyList<Choice<int>> ExtInodeSizes => OptionLists.ExtInodeSizes;
    public IReadOnlyList<Choice<ArchiveCompression>> TarCompressions => OptionLists.TarCompressions;
    public IReadOnlyList<Choice<TarDialect>> TarDialects => OptionLists.TarDialects;
    public IReadOnlyList<Choice<string>> TarPrefixes => OptionLists.TarPrefixes;
    public IReadOnlyList<Choice<VerifyLevel>> VerifyLevels => OptionLists.VerifyLevels;

    public SaveFormat Format
    {
        get => _format;
        set
        {
            if (!SetProperty(ref _format, value)) return;
            Error = null;
            foreach (string p in new[] { nameof(IsSquash), nameof(IsExt), nameof(IsTar), nameof(HasJournalOption), nameof(FormatNote) }) OnPropertyChanged(p);
            RaiseCompression();
            RaiseContainer();
        }
    }

    public bool IsSquash => _format == SaveFormat.SquashFs;
    public bool IsExt => _format is SaveFormat.Ext2 or SaveFormat.Ext3 or SaveFormat.Ext4;
    public bool IsTar => _format == SaveFormat.Tar;
    public bool HasJournalOption => _format is SaveFormat.Ext3 or SaveFormat.Ext4;

    public string FormatNote => _format switch
    {
        SaveFormat.SquashFs => "Сжатая файловая система только для чтения (версия 4.0).",
        SaveFormat.Ext4 => "ext4 с экстентами; без контрольных сумм метаданных, монтируется любым ядром Linux 2.6.28+.",
        SaveFormat.Ext3 => "ext3: журнал и карты блоков.",
        SaveFormat.Ext2 => "ext2: без журнала, карты блоков.",
        _ => "Архив с правами, владельцами, ссылками, устройствами и расширенными атрибутами.",
    };

    private void RaiseCompression()
    {
        foreach (string p in new[] { nameof(ShowXz), nameof(ShowGzip), nameof(ShowZstd), nameof(ShowBcj), nameof(TarStoresXattrs) }) OnPropertyChanged(p);
    }

    public bool ShowXz => (IsSquash && _o.SquashFs.Compression == SquashCompression.Xz) || (IsTar && _o.Tar.Compression == ArchiveCompression.Xz);
    public bool ShowGzip => (IsSquash && _o.SquashFs.Compression == SquashCompression.Gzip) || (IsTar && _o.Tar.Compression == ArchiveCompression.Gzip);
    public bool ShowZstd => (IsSquash && _o.SquashFs.Compression == SquashCompression.Zstd) || (IsTar && _o.Tar.Compression == ArchiveCompression.Zstd);
    public bool ShowBcj => IsSquash && _o.SquashFs.Compression == SquashCompression.Xz;

    // ---- SquashFS
    public SquashCompression SquashCompression
    {
        get => _o.SquashFs.Compression;
        set
        {
            if (_o.SquashFs.Compression == value) return;
            _o.SquashFs.Compression = value;
            OnPropertyChanged();
            RaiseCompression();
        }
    }

    public int SquashBlockSize { get => _o.SquashFs.BlockSize; set { _o.SquashFs.BlockSize = value; OnPropertyChanged(); } }
    public int BcjFilter { get => _bcj; set => SetProperty(ref _bcj, value); }
    public bool Fragments { get => !_o.SquashFs.NoFragments; set { _o.SquashFs.NoFragments = !value; OnPropertyChanged(); } }
    public bool Deduplicate { get => _o.SquashFs.Deduplicate; set { _o.SquashFs.Deduplicate = value; OnPropertyChanged(); } }
    public bool Exportable { get => _o.SquashFs.Exportable; set { _o.SquashFs.Exportable = value; OnPropertyChanged(); } }
    public bool Pad4K { get => _o.SquashFs.Pad4K; set { _o.SquashFs.Pad4K = value; OnPropertyChanged(); } }

    // ---- levels shared by SquashFS and tar (each keeps its own value)
    public int XzEffort
    {
        get => IsTar ? _o.Tar.XzEffort : _o.SquashFs.XzEffort;
        set { if (IsTar) _o.Tar.XzEffort = value; else _o.SquashFs.XzEffort = value; OnPropertyChanged(); }
    }

    public int GzipLevel
    {
        get => IsTar ? _o.Tar.GzipLevel : _o.SquashFs.GzipLevel;
        set { if (IsTar) _o.Tar.GzipLevel = value; else _o.SquashFs.GzipLevel = value; OnPropertyChanged(); }
    }

    public int ZstdLevel
    {
        get => IsTar ? _o.Tar.ZstdLevel : _o.SquashFs.ZstdLevel;
        set { if (IsTar) _o.Tar.ZstdLevel = value; else _o.SquashFs.ZstdLevel = value; OnPropertyChanged(); }
    }

    // ---- ext
    public ExtSizeMode SizeMode
    {
        get => _sizeMode;
        set
        {
            if (!SetProperty(ref _sizeMode, value)) return;
            Error = null;
            OnPropertyChanged(nameof(IsFixedSize));
            OnPropertyChanged(nameof(IsMinimalSize));
            OnPropertyChanged(nameof(CanChangeGeometry));
        }
    }

    public bool IsFixedSize => _sizeMode == ExtSizeMode.Fixed;
    public bool IsMinimalSize => _sizeMode == ExtSizeMode.Minimal;

    /// <summary>Block and inode sizes are part of the geometry that "keep" preserves.</summary>
    public bool CanChangeGeometry => _sizeMode != ExtSizeMode.Keep;

    public string SizeMiB { get => _sizeMiB; set { if (SetProperty(ref _sizeMiB, value)) Error = null; } }
    public string ExtraFreeMiB { get => _extraFreeMiB; set { if (SetProperty(ref _extraFreeMiB, value)) Error = null; } }
    public int ExtBlockSize { get => _o.Ext.BlockSize; set { _o.Ext.BlockSize = value; OnPropertyChanged(); } }
    public int ExtInodeSize { get => _o.Ext.InodeSize; set { _o.Ext.InodeSize = value; OnPropertyChanged(); } }
    public bool Journal { get => _o.Ext.Journal; set { _o.Ext.Journal = value; OnPropertyChanged(); } }
    public string Label { get => _label; set { if (SetProperty(ref _label, value)) Error = null; } }
    public string ReservedPercent { get => _reserved; set { if (SetProperty(ref _reserved, value)) Error = null; } }
    public bool KeepUuid { get => _o.Ext.Uuid is not null; set { _o.Ext.Uuid = value ? _doc.Ext?.Uuid : null; OnPropertyChanged(); } }
    public bool CanKeepUuid => _doc.Ext is not null;

    // ---- tar
    public ArchiveCompression TarCompression
    {
        get => _o.Tar.Compression;
        set
        {
            if (_o.Tar.Compression == value) return;
            _o.Tar.Compression = value;
            OnPropertyChanged();
            RaiseCompression();
            OnPropertyChanged(nameof(XzEffort));
            OnPropertyChanged(nameof(GzipLevel));
            OnPropertyChanged(nameof(ZstdLevel));
        }
    }

    public TarDialect TarDialect
    {
        get => _o.Tar.Dialect;
        set { _o.Tar.Dialect = value; OnPropertyChanged(); OnPropertyChanged(nameof(TarStoresXattrs)); }
    }

    public bool TarStoresXattrs => _o.Tar.Dialect == TarDialect.Pax;
    public string TarPrefix { get => _o.Tar.PathPrefix; set { _o.Tar.PathPrefix = value ?? ""; OnPropertyChanged(); } }
    public bool TarRootEntry { get => _o.Tar.IncludeRootEntry; set { _o.Tar.IncludeRootEntry = value; OnPropertyChanged(); } }

    // ---- common to all formats
    public bool StoreXattrs
    {
        get => _o.SquashFs.StoreXattrs;
        set { _o.SquashFs.StoreXattrs = _o.Ext.StoreXattrs = _o.Tar.StoreXattrs = value; OnPropertyChanged(); }
    }

    public bool AllRoot
    {
        get => _o.SquashFs.AllRoot;
        set { _o.SquashFs.AllRoot = _o.Ext.AllRoot = _o.Tar.AllRoot = value; OnPropertyChanged(); }
    }

    public VerifyLevel Verify { get => _o.Verify; set { _o.Verify = value; OnPropertyChanged(); } }
    public bool KeepBackup { get => _o.KeepBackup; set { _o.KeepBackup = value; OnPropertyChanged(); } }

    /// <summary>The validated options; set by <see cref="Accept"/>.</summary>
    public SaveOptions? Result { get; private set; }

    /// <summary>File extension that fits the chosen format (".squashfs", ".ext4", ".tar.gz"...); for a disk image, the one it had.</summary>
    public string Extension => WritesDisk ? DiskExtension(_doc.FilePath) : _format switch
    {
        SaveFormat.SquashFs => ".squashfs",
        SaveFormat.Ext4 => ".ext4",
        SaveFormat.Ext3 => ".ext3",
        SaveFormat.Ext2 => ".ext2",
        _ => ArchiveCodec.DefaultExtension(_o.Tar.Compression),
    };

    /// <summary>".img.gz" for "openwrt-sdcard.img.gz", ".img" when the name has no usable extension.</summary>
    public static string DiskExtension(string? path)
    {
        string name = Path.GetFileName(path ?? "");
        string outer = Path.GetExtension(name).ToLowerInvariant();
        if (outer is ".gz" or ".xz" or ".zst" or ".zstd")
        {
            string inner = Path.GetExtension(name[..^outer.Length]).ToLowerInvariant();
            return (inner.Length is > 1 and <= 6 ? inner : ".img") + outer;
        }
        return outer.Length is > 1 and <= 6 ? outer : ".img";
    }

    public override bool Accept()
    {
        SaveOptions o = _o.Clone();
        o.Format = _format switch
        {
            SaveFormat.SquashFs => ImageFormat.SquashFs,
            SaveFormat.Tar => ImageFormat.Tar,
            _ => ImageFormat.Ext,
        };

        o.SquashFs.XzFilters.Clear();
        if (_bcj >= 0) o.SquashFs.XzFilters.Add((BcjFilter)_bcj);
        // The dictionary of a re-saved image must still fit the (possibly changed) block size.
        if (o.SquashFs.XzDictionarySize > o.SquashFs.BlockSize) o.SquashFs.XzDictionarySize = 0;

        if (IsExt)
        {
            o.Ext.Kind = _format switch { SaveFormat.Ext2 => ExtKind.Ext2, SaveFormat.Ext3 => ExtKind.Ext3, _ => ExtKind.Ext4 };
            if (o.Ext.Kind == ExtKind.Ext2) o.Ext.Journal = false;
            else if (o.Ext.Kind == ExtKind.Ext3) o.Ext.Journal = true;
            if (System.Text.Encoding.UTF8.GetByteCount(_label) > 16) return Fail("Метка тома не длиннее 16 байт.");
            o.Ext.Label = _label;
            if (!double.TryParse(_reserved.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double reserved)
                || reserved is < 0 or > 50)
                return Fail("Резерв для root — число от 0 до 50 процентов.");
            o.Ext.ReservedPercent = reserved;

            switch (_sizeMode)
            {
                case ExtSizeMode.Keep:
                    // Geometry of the source is kept as it is.
                    break;
                case ExtSizeMode.Fixed:
                    if (!long.TryParse(_sizeMiB.Trim(), out long mib) || mib < 1 || mib > 16L << 20)
                        return Fail("Размер образа — целое число мегабайт (МиБ), не меньше 1.");
                    if (WritesDisk && _doc.Container is { CanGrow: false } disk && mib << 20 > disk.Partition.Length)
                        return Fail($"Раздел {disk.Partition.Number} вмещает {Fmt.Size(disk.Partition.Length)} и не может быть увеличен — укажите размер не больше этого.");
                    o.Ext.SizeBytes = mib << 20;
                    o.Ext.ExtraFreeBytes = 0;
                    o.Ext.InodeCount = 0;
                    o.Ext.JournalBytes = 0;
                    break;
                default:
                    if (!long.TryParse(_extraFreeMiB.Trim().Length == 0 ? "0" : _extraFreeMiB.Trim(), out long free) || free < 0 || free > 16L << 20)
                        return Fail("Запас свободного места — целое неотрицательное число мегабайт.");
                    o.Ext.SizeBytes = 0;
                    o.Ext.ExtraFreeBytes = free << 20;
                    o.Ext.InodeCount = 0;
                    o.Ext.JournalBytes = 0;
                    break;
            }
        }

        try
        {
            switch (o.Format)
            {
                case ImageFormat.SquashFs: o.SquashFs.Validate(); break;
                case ImageFormat.Ext: o.Ext.Validate(); break;
                default: o.Tar.Validate(); break;
            }
        }
        catch (FsException ex)
        {
            return Fail(ex.Message);
        }
        Result = o;
        return true;
    }

    private bool Fail(string message)
    {
        Error = message;
        return false;
    }
}
