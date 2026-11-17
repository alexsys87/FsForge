using FsForge.Core;
using FsForge.Core.Compression;
using FsForge.Core.Cpio;
using FsForge.Core.Erofs;
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
    Erofs,
    Ext4,
    Ext3,
    Ext2,
    Ubifs,
    Jffs2,
    Fat,
    Tar,
    Cpio,
    Cramfs,
    Romfs,
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
        new Choice<SaveFormat>(SaveFormat.Erofs, "EROFS"),
        new Choice<SaveFormat>(SaveFormat.Ext4, "ext4"),
        new Choice<SaveFormat>(SaveFormat.Ext3, "ext3"),
        new Choice<SaveFormat>(SaveFormat.Ext2, "ext2"),
        new Choice<SaveFormat>(SaveFormat.Ubifs, L.T("UBIFS (NAND- и NOR-флеш)")),
        new Choice<SaveFormat>(SaveFormat.Jffs2, L.T("JFFS2 (NOR-флеш)")),
        new Choice<SaveFormat>(SaveFormat.Fat, L.T("FAT (загрузочные разделы, флешки)")),
        new Choice<SaveFormat>(SaveFormat.Tar, L.T("Архив tar (rootfs.tar.gz)")),
        new Choice<SaveFormat>(SaveFormat.Cpio, L.T("Архив cpio (initramfs)")),
        new Choice<SaveFormat>(SaveFormat.Cramfs, L.T("CramFS (старые роутеры и встраиваемые системы)")),
        new Choice<SaveFormat>(SaveFormat.Romfs, L.T("romfs (uClinux, загрузчики)")),
    };

    public static IReadOnlyList<Choice<SquashCompression>> SquashCompressions { get; } = new[]
    {
        new Choice<SquashCompression>(SquashCompression.Xz, L.T("xz — лучшее сжатие")),
        new Choice<SquashCompression>(SquashCompression.Zstd, L.T("zstd — быстро и плотно")),
        new Choice<SquashCompression>(SquashCompression.Gzip, L.T("gzip — максимальная совместимость")),
        new Choice<SquashCompression>(SquashCompression.Lz4, L.T("lz4 — самая быстрая распаковка")),
    };

    public static IReadOnlyList<Choice<int>> SquashBlockSizes { get; } =
        new[] { 4, 8, 16, 32, 64, 128, 256, 512, 1024 }.Select(k => new Choice<int>(k * 1024, k < 1024 ? L.F("{0} КБ", k) : L.T("1 МБ"))).ToArray();

    public static IReadOnlyList<Choice<ErofsCompression>> ErofsCompressions { get; } = new[]
    {
        new Choice<ErofsCompression>(ErofsCompression.Lz4Hc, L.T("lz4hc — быстрая распаковка, сжатие плотнее lz4")),
        new Choice<ErofsCompression>(ErofsCompression.Lzma, L.T("lzma — лучшее сжатие (Linux 5.16 и новее)")),
        new Choice<ErofsCompression>(ErofsCompression.Zstd, L.T("zstd — плотно и быстро (Linux 6.10 и новее)")),
        new Choice<ErofsCompression>(ErofsCompression.Deflate, L.T("deflate — Linux 6.6 и новее")),
        new Choice<ErofsCompression>(ErofsCompression.Lz4, L.T("lz4 — самая быстрая запись")),
        new Choice<ErofsCompression>(ErofsCompression.None, L.T("без сжатия")),
    };

    public static IReadOnlyList<Choice<int>> ErofsPclusterSizes { get; } =
        new[] { 4, 8, 16, 32, 64, 128, 256, 512, 1024 }.Select(k => new Choice<int>(k * 1024, k < 1024 ? L.F("{0} КБ", k) : L.T("1 МБ"))).ToArray();

    public static IReadOnlyList<Choice<int>> ErofsLz4Levels { get; } =
        new[] { 3, 6, 9, 12 }.Select(l => new Choice<int>(l, l == 3 ? L.T("3 — быстрее") : l == 12 ? L.T("12 — плотнее") : l.ToString())).ToArray();

    public static IReadOnlyList<Choice<int>> XzEfforts { get; } = new[]
    {
        new Choice<int>(0, L.T("быстро")), new Choice<int>(1, L.T("обычно")), new Choice<int>(2, L.T("максимум (медленно)")),
    };

    public static IReadOnlyList<Choice<int>> GzipLevels { get; } =
        Enumerable.Range(1, 9).Select(l => new Choice<int>(l, l == 1 ? L.T("1 — быстрее") : l == 9 ? L.T("9 — плотнее") : l.ToString())).ToArray();

    public static IReadOnlyList<Choice<int>> ZstdLevels { get; } =
        new[] { 1, 3, 6, 10, 15, 19, 22 }.Select(l => new Choice<int>(l, l == 1 ? L.T("1 — быстрее") : l == 22 ? L.T("22 — плотнее") : l.ToString())).ToArray();

    /// <summary>Value -1 stands for "no filter".</summary>
    public static IReadOnlyList<Choice<int>> BcjFilters { get; } = new[]
    {
        new Choice<int>(-1, L.T("нет")),
        new Choice<int>((int)BcjFilter.Arm, "ARM"),
        new Choice<int>((int)BcjFilter.ArmThumb, "ARM Thumb"),
        new Choice<int>((int)BcjFilter.Arm64, "ARM64"),
        new Choice<int>((int)BcjFilter.X86, "x86 / x86-64"),
        new Choice<int>((int)BcjFilter.PowerPc, "PowerPC"),
        new Choice<int>((int)BcjFilter.Sparc, "SPARC"),
    };

    public static IReadOnlyList<Choice<int>> ExtBlockSizes { get; } = new[]
    {
        new Choice<int>(4096, L.T("4096 байт")), new Choice<int>(2048, L.T("2048 байт")), new Choice<int>(1024, L.T("1024 байта")),
    };

    public static IReadOnlyList<Choice<int>> ExtInodeSizes { get; } = new[]
    {
        new Choice<int>(256, L.T("256 байт (время после 2038 года)")), new Choice<int>(128, L.T("128 байт (компактнее)")),
    };

    public static IReadOnlyList<Choice<FsForge.Core.Ubifs.UbifsCompression>> UbifsCompressions { get; } = new[]
    {
        new Choice<FsForge.Core.Ubifs.UbifsCompression>(FsForge.Core.Ubifs.UbifsCompression.Lzo, L.T("lzo — как у mkfs.ubifs по умолчанию")),
        new Choice<FsForge.Core.Ubifs.UbifsCompression>(FsForge.Core.Ubifs.UbifsCompression.Zlib, L.T("zlib — плотнее, медленнее")),
        new Choice<FsForge.Core.Ubifs.UbifsCompression>(FsForge.Core.Ubifs.UbifsCompression.Zstd, L.T("zstd — нужно ядро Linux 5.3 или новее")),
        new Choice<FsForge.Core.Ubifs.UbifsCompression>(FsForge.Core.Ubifs.UbifsCompression.None, L.T("без сжатия")),
    };

    public static IReadOnlyList<Choice<FsForge.Core.Jffs2.Jffs2Compression>> Jffs2Compressions { get; } = new[]
    {
        new Choice<FsForge.Core.Jffs2.Jffs2Compression>(FsForge.Core.Jffs2.Jffs2Compression.Zlib, L.T("zlib — как у mkfs.jffs2 по умолчанию")),
        new Choice<FsForge.Core.Jffs2.Jffs2Compression>(FsForge.Core.Jffs2.Jffs2Compression.Lzo, L.T("lzo — быстрее, нужно ядро с CONFIG_JFFS2_LZO")),
        new Choice<FsForge.Core.Jffs2.Jffs2Compression>(FsForge.Core.Jffs2.Jffs2Compression.Rtime, L.T("rtime — простое и быстрое, сжимает слабо")),
        new Choice<FsForge.Core.Jffs2.Jffs2Compression>(FsForge.Core.Jffs2.Jffs2Compression.None, L.T("без сжатия")),
    };

    public static IReadOnlyList<Choice<bool>> Jffs2ByteOrders { get; } = new[]
    {
        new Choice<bool>(false, L.T("little-endian (ARM, x86, MIPS little-endian)")),
        new Choice<bool>(true, L.T("big-endian (MIPS, PowerPC)")),
    };

    public static IReadOnlyList<Choice<int>> CramfsBlockSizes { get; } = new[]
    {
        new Choice<int>(4096, L.T("4 КБ — страница x86, ARM, MIPS")),
        new Choice<int>(16384, L.T("16 КБ — страница некоторых ARM64 и MIPS")),
        new Choice<int>(65536, L.T("64 КБ — страница PowerPC64 и некоторых ARM64")),
    };

    public static IReadOnlyList<Choice<int>> RomfsAlignments { get; } = new[]
    {
        new Choice<int>(16, L.T("16 байт — как у genromfs")),
        new Choice<int>(512, L.T("512 байт — сектор")),
        new Choice<int>(4096, L.T("4 КБ — страница (выполнение на месте, XIP)")),
    };

    public static IReadOnlyList<Choice<int>> Jffs2EraseBlockSizes { get; } =
        new[] { 4, 8, 16, 32, 64, 128, 256, 512, 1024 }.Select(k => new Choice<int>(k * 1024, k < 1024 ? L.F("{0} КБ", k) : L.T("1 МБ"))).ToArray();

    public static IReadOnlyList<Choice<FsForge.Core.Fat.FatType>> FatTypes { get; } = new[]
    {
        new Choice<FsForge.Core.Fat.FatType>(FsForge.Core.Fat.FatType.Auto, L.T("по размеру (как mkfs.fat)")),
        new Choice<FsForge.Core.Fat.FatType>(FsForge.Core.Fat.FatType.Fat12, "FAT12"),
        new Choice<FsForge.Core.Fat.FatType>(FsForge.Core.Fat.FatType.Fat16, "FAT16"),
        new Choice<FsForge.Core.Fat.FatType>(FsForge.Core.Fat.FatType.Fat32, "FAT32"),
    };

    public static IReadOnlyList<Choice<int>> FatClusterSizes { get; } =
        new[] { new Choice<int>(0, L.T("автоматически")) }
            .Concat(new[] { 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536 }.Select(b => new Choice<int>(b, b < 1024 ? L.F("{0} байт", b) : L.F("{0} КБ", b / 1024))))
            .ToArray();

    public static IReadOnlyList<Choice<ArchiveCompression>> TarCompressions { get; } = new[]
    {
        new Choice<ArchiveCompression>(ArchiveCompression.Gzip, "gzip (.tar.gz)"),
        new Choice<ArchiveCompression>(ArchiveCompression.Xz, "xz (.tar.xz)"),
        new Choice<ArchiveCompression>(ArchiveCompression.Zstd, "zstd (.tar.zst)"),
        new Choice<ArchiveCompression>(ArchiveCompression.None, L.T("без сжатия (.tar)")),
    };

    public static IReadOnlyList<Choice<TarDialect>> TarDialects { get; } = new[]
    {
        new Choice<TarDialect>(TarDialect.Pax, L.T("pax (POSIX) — хранит расширенные атрибуты")),
        new Choice<TarDialect>(TarDialect.Gnu, L.T("GNU tar — без расширенных атрибутов")),
    };

    public static IReadOnlyList<Choice<ArchiveCompression>> CpioCompressions { get; } = new[]
    {
        new Choice<ArchiveCompression>(ArchiveCompression.Gzip, "gzip (.cpio.gz)"),
        new Choice<ArchiveCompression>(ArchiveCompression.Zstd, L.T("zstd (.cpio.zst) — Linux 5.9 и новее")),
        new Choice<ArchiveCompression>(ArchiveCompression.Xz, "xz (.cpio.xz)"),
        new Choice<ArchiveCompression>(ArchiveCompression.Lz4, L.T("lz4 (.cpio.lz4) — как у ramdisk Android")),
        new Choice<ArchiveCompression>(ArchiveCompression.None, L.T("без сжатия (.cpio)")),
    };

    public static IReadOnlyList<Choice<CpioHeader>> CpioHeaders { get; } = new[]
    {
        new Choice<CpioHeader>(CpioHeader.Newc, L.T("newc — как у initramfs")),
        new Choice<CpioHeader>(CpioHeader.Crc, L.T("crc — newc с контрольными суммами файлов")),
    };

    public static IReadOnlyList<Choice<string>> CpioPrefixes { get; } = new[]
    {
        new Choice<string>("", L.T("без префикса — как у gen_init_cpio и dracut")),
        new Choice<string>("./", L.T("./ — как у «find . | cpio»")),
    };

    public static IReadOnlyList<Choice<string>> TarPrefixes { get; } = new[]
    {
        new Choice<string>("./", L.T("./ — как у «tar -C rootfs .»")), new Choice<string>("", L.T("без префикса")),
    };

    public static IReadOnlyList<Choice<VerifyLevel>> VerifyLevels { get; } = new[]
    {
        new Choice<VerifyLevel>(VerifyLevel.Structure, L.T("структуру: имена, типы, размеры, права")),
        new Choice<VerifyLevel>(VerifyLevel.Content, L.T("структуру и содержимое каждого файла")),
        new Choice<VerifyLevel>(VerifyLevel.None, L.T("не проверять")),
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

    public SaveOptionsViewModel(ImageDocument doc, SaveOptions options, string? title = null, bool lockFormat = false)
    {
        _doc = doc;
        _o = options.Clone();
        Title = title ?? L.T("Параметры сохранения");
        AcceptText = L.T("Далее…");
        FormatLocked = lockFormat;
        _format = _o.Format switch
        {
            ImageFormat.SquashFs => SaveFormat.SquashFs,
            ImageFormat.Erofs => SaveFormat.Erofs,
            ImageFormat.Ubifs => SaveFormat.Ubifs,
            ImageFormat.Jffs2 => SaveFormat.Jffs2,
            ImageFormat.Fat => SaveFormat.Fat,
            ImageFormat.Tar => SaveFormat.Tar,
            ImageFormat.Cpio => SaveFormat.Cpio,
            ImageFormat.Cramfs => SaveFormat.Cramfs,
            ImageFormat.Romfs => SaveFormat.Romfs,
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
        ContentSummary = L.F("Содержимое: {0}, {1}, {2}", Fmt.Plural(st.Files, "файл", "файла", "файлов"), Fmt.Plural(st.Directories, "папка", "папки", "папок"), Fmt.Size(st.DataBytes));
        OriginalSizeText = CanKeepSize ? Fmt.Size(_o.Ext.SizeBytes) : "";

        var sizeModes = new List<Choice<ExtSizeMode>>();
        if (CanKeepSize) sizeModes.Add(new Choice<ExtSizeMode>(ExtSizeMode.Keep, L.F("как у исходного образа ({0})", OriginalSizeText)));
        sizeModes.Add(new Choice<ExtSizeMode>(ExtSizeMode.Minimal, L.T("минимальный по содержимому")));
        sizeModes.Add(new Choice<ExtSizeMode>(ExtSizeMode.Fixed, L.T("заданный, МиБ")));
        SizeModes = sizeModes;

        // An erase block size that is not in the list (a source image with 2 MiB blocks) is offered as well.
        var eraseSizes = OptionLists.Jffs2EraseBlockSizes.ToList();
        if (!eraseSizes.Any(c => c.Value == _o.Jffs2.EraseBlockSize))
            eraseSizes.Add(new Choice<int>(_o.Jffs2.EraseBlockSize, Fmt.Size(_o.Jffs2.EraseBlockSize)));
        Jffs2EraseBlockSizes = eraseSizes;

        // The cluster size of the source image is offered even when it is not one of the usual ones.
        var clusterSizes = OptionLists.ErofsPclusterSizes.ToList();
        if (!clusterSizes.Any(c => c.Value == _o.Erofs.PclusterSize))
            clusterSizes.Add(new Choice<int>(_o.Erofs.PclusterSize, Fmt.Size(_o.Erofs.PclusterSize)));
        ErofsPclusterSizes = clusterSizes.OrderBy(c => c.Value).ToList();
        if (!OptionLists.ErofsLz4Levels.Any(c => c.Value == _o.Erofs.Lz4Level))
            _o.Erofs.Lz4Level = OptionLists.ErofsLz4Levels.MinBy(c => Math.Abs(c.Value - _o.Erofs.Lz4Level))!.Value;
        _erofsName = _o.Erofs.VolumeName;

        _fatSize = _o.Fat.SizeBytes > 0 ? (_o.Fat.SizeBytes >> 20).ToString() : "";
        _fatLabel = _o.Fat.Label;

        _ubifsLeb = _o.Ubifs.LebSize.ToString();
        _ubifsMinIo = _o.Ubifs.MinIoSize.ToString();
        _ubifsMaxLeb = _o.Ubifs.MaxLebCount.ToString();
        _ubiPeb = _o.Ubi.PebSize.ToString();
        _ubiSubPage = _o.Ubi.SubPageSize.ToString();
        _ubiVolumeName = _o.Ubi.VolumeName;
        if (doc.Ubi is { } ubi)
            ContainerSummary = L.F("{0} — образ UBI (блоки стирания по {1}, томов: {2}). ", Path.GetFileName(doc.FilePath), Fmt.Size(ubi.Image.PebSize), ubi.Image.Volumes.Count) +
                               L.F("Открыт том {0}: {1}, {2}.", Fmt.VolumeName(ubi.Volume), Fmt.VolumeContent(ubi.Volume), Fmt.Size(ubi.Volume.Length));

        if (doc.Android is { } android)
            ContainerSummary = L.F("{0} — {1}. ", Path.GetFileName(doc.FilePath), Fmt.AndroidKind(android.Image)) +
                               L.F("Открыт {0}: {1}, {2}.", Fmt.AndroidPartName(android.Image, android.Part), Fmt.AndroidPartContent(android.Part), Fmt.Size(android.Part.Length));

        if (doc.Container is { } disk)
        {
            PartitionInfo p = disk.Partition;
            ContainerSummary = L.F("{0} — образ диска ({1}, разделов: {2}). ", Path.GetFileName(doc.FilePath), Fmt.PartitionScheme(disk.Table), disk.Table.Partitions.Count) +
                               L.F("Открыт раздел {0}: {1}, {2}.", p.Number, Fmt.PartitionContent(p), Fmt.Size(p.Length));
        }
    }

    // ---- disk image the filesystem was opened from
    /// <summary>The document came from a partition of a disk image or from a volume of a UBI image.</summary>
    public bool HasContainer => _doc.Container is not null || _doc.Ubi is not null || _doc.Android is not null;

    /// <summary>The container is an Android image: super.img (logical partitions) or a boot image (ramdisk).</summary>
    public bool IsAndroidContainer => _doc.Android is not null;

    private bool IsSuperContainer => _doc.Android?.Image.Kind == FsForge.Core.Android.AndroidImageKind.Super;

    /// <summary>The container is a UBI image (volumes) and not a disk image (partitions).</summary>
    public bool IsUbiContainer => _doc.Ubi is not null;

    public string ContainerTitle => IsUbiContainer ? L.T("Образ UBI") : IsAndroidContainer ? L.T("Образ Android") : L.T("Образ диска");

    public string ContainerToggleText => IsAndroidContainer
        ? IsSuperContainer
            ? L.T("Сохранить super.img целиком: остальные логические разделы остаются как были")
            : L.T("Сохранить загрузочный образ целиком: ядро, DTB и остальное остаются как были")
        : IsUbiContainer
        ? L.T("Сохранить образ UBI целиком: остальные тома остаются как были")
        : L.T("Сохранить образ диска целиком: таблица разделов и остальные разделы остаются как были");

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
    public bool WritesDisk => HasContainer && _o.KeepContainer && (IsAndroidContainer
        ? IsSuperContainer ? !IsTar && !IsCpio && !IsJffs2 && !IsUbifs : IsCpio
        : !IsTar && !IsCpio && !IsJffs2 && !(IsFat && IsUbiContainer));

    public string ContainerNote
    {
        get
        {
            if (_doc.Android is { } android)
            {
                string part = Fmt.AndroidPartName(android.Image, android.Part);
                if (!IsSuperContainer && !IsCpio)
                    return L.T("В загрузочном образе ramdisk — архив cpio; другой формат сохраняется отдельным файлом, образ при этом не меняется.");
                if (IsSuperContainer && (IsTar || IsCpio || IsJffs2 || IsUbifs))
                    return L.T("Этот формат сохраняется отдельным файлом: в логический раздел super.img он не записывается.");
                if (!_o.KeepContainer)
                    return IsSuperContainer
                        ? L.T("Будет записана только файловая система раздела — её можно прошить командой fastboot flash в раздел с тем же именем.")
                        : L.T("Будет записан только архив ramdisk, без ядра и заголовка загрузочного образа.");
                return IsSuperContainer
                    ? L.F("super.img записывается заново: заменяется только {0}, остальные разделы и группы остаются на своих местах. ", part) +
                      L.T("Если раздел перестанет помещаться на прежнее место, он займёт свободное место super. Метаданные записываются во все слоты; sparse-образ остаётся sparse.")
                    : L.F("Загрузочный образ записывается заново: заменяется только {0}, размеры и контрольная сумма в заголовке пересчитываются. ", part) +
                      L.T("Если образ подписан AVB, подпись перестанет совпадать: телефон с разблокированным загрузчиком его загрузит, с заблокированным — нет.");
            }
            if (IsJffs2 && HasContainer)
                return L.T("JFFS2 сохраняется отдельным файлом: в раздел образа диска или том UBI он не записывается.");
            if (IsFat && IsUbiContainer)
                return L.T("FAT сохраняется отдельным файлом: в том UBI он не записывается.");
            if (_doc.Ubi is { } ubi)
            {
                if (IsTar) return L.T("Архив tar сохраняется отдельным файлом; образ UBI при этом не меняется.");
                if (IsCpio) return L.T("Архив cpio сохраняется отдельным файлом; образ UBI при этом не меняется.");
                if (!_o.KeepContainer)
                    return L.T("Будет записана только файловая система — без образа UBI и остальных томов. Такой файл записывают в том командой ubiupdatevol или передают ubinize.");
                return L.F("Образ UBI записывается заново: том {0} заменяется, остальные тома переносятся без изменений. Пустые блоки и старые копии блоков в новый образ не попадают. ", Fmt.VolumeName(ubi.Volume)) +
                       (IsUbifs ? L.T("Размер блока стирания UBIFS берётся из образа UBI. ") : "") +
                       L.T("Если файловая система перестанет помещаться в том, том будет увеличен.");
            }
            if (_doc.Container is not { } disk) return "";
            PartitionInfo p = disk.Partition;
            if (IsTar) return L.T("Архив tar сохраняется отдельным файлом; образ диска при этом не меняется.");
            if (IsCpio) return L.T("Архив cpio сохраняется отдельным файлом; образ диска при этом не меняется.");
            if (!_o.KeepContainer)
                return L.T("Будет записана только файловая система — без таблицы разделов и остальных разделов. Такой файл не загрузится с карты памяти; он годится для sysupgrade или записи в раздел вручную.");
            string grow = disk.CanGrow
                ? L.F("Если файловая система перестанет помещаться в раздел {0}, раздел будет увеличен (он последний).", p.Number)
                : L.F("Файловая система должна поместиться в раздел {0} ({1}).", p.Number, Fmt.Size(p.Length));
            return L.F("Таблица разделов, загрузчик и остальные разделы копируются без изменений, заменяется только раздел {0}. {1} ", p.Number, grow) +
                   L.T("Имя файла с окончанием .gz, .xz или .zst означает сжатый образ.");
        }
    }

    /// <summary>How the ext size choices are read when the filesystem goes into a partition.</summary>
    public string ExtSizeNote => WritesDisk && _doc.Container is { } disk
        ? L.F("В образе диска «минимальный» означает размер раздела ({0}): файловая система занимает раздел целиком.", Fmt.Size(disk.Partition.Length))
        : "";

    public bool HasExtSizeNote => ExtSizeNote.Length > 0;

    private void RaiseContainer()
    {
        foreach (string p in new[] { nameof(WritesDisk), nameof(ContainerNote), nameof(ExtSizeNote), nameof(HasExtSizeNote), nameof(WritesUbi), nameof(ShowNewUbi),
                     nameof(ShowUbiOptions), nameof(CanEditUbifsGeometry), nameof(UbifsGeometryNote), nameof(Extension),
                     nameof(FatSizeEditable), nameof(FatSizeNote), nameof(HasFatSizeNote) }) OnPropertyChanged(p);
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
    public IReadOnlyList<Choice<ArchiveCompression>> CpioCompressions => OptionLists.CpioCompressions;
    public IReadOnlyList<Choice<CpioHeader>> CpioHeaders => OptionLists.CpioHeaders;
    public IReadOnlyList<Choice<string>> CpioPrefixes => OptionLists.CpioPrefixes;
    public IReadOnlyList<Choice<VerifyLevel>> VerifyLevels => OptionLists.VerifyLevels;

    public SaveFormat Format
    {
        get => _format;
        set
        {
            if (!SetProperty(ref _format, value)) return;
            Error = null;
            foreach (string p in new[] { nameof(IsSquash), nameof(IsErofs), nameof(IsExt), nameof(IsTar), nameof(IsCpio), nameof(IsCramfs), nameof(IsRomfs), nameof(IsUbifs), nameof(IsJffs2), nameof(IsFat), nameof(ShowCommonXattrs), nameof(ShowAllRoot), nameof(FatSizeEditable), nameof(FatSizeNote), nameof(HasFatSizeNote), nameof(HasJournalOption), nameof(FormatNote) }) OnPropertyChanged(p);
            RaiseCompression();
            RaiseContainer();
        }
    }

    public bool IsSquash => _format == SaveFormat.SquashFs;
    public bool IsErofs => _format == SaveFormat.Erofs;
    public bool IsExt => _format is SaveFormat.Ext2 or SaveFormat.Ext3 or SaveFormat.Ext4;
    public bool IsTar => _format == SaveFormat.Tar;
    public bool IsCpio => _format == SaveFormat.Cpio;
    public bool IsCramfs => _format == SaveFormat.Cramfs;
    public bool IsRomfs => _format == SaveFormat.Romfs;
    public bool IsUbifs => _format == SaveFormat.Ubifs;
    public bool IsJffs2 => _format == SaveFormat.Jffs2;
    public bool IsFat => _format == SaveFormat.Fat;
    public bool HasJournalOption => _format is SaveFormat.Ext3 or SaveFormat.Ext4;

    public string FormatNote => _format switch
    {
        SaveFormat.SquashFs => L.T("Сжатая файловая система только для чтения (версия 4.0)."),
        SaveFormat.Cramfs => L.T("Сжатая файловая система только для чтения из старых прошивок роутеров и встраиваемых систем: zlib, блоки размером со страницу памяти. Хранит права, владельца (16 бит) и группу (8 бит); не хранит время, жёсткие ссылки (одинаковые данные хранятся один раз) и расширенные атрибуты. Файл — до 16 МБ, образ — до 256 МБ."),
        SaveFormat.Romfs => L.T("Простейшая файловая система только для чтения (uClinux, загрузчики, прошивки без MMU): без сжатия, данные можно выполнять на месте. Хранит только тип, признак исполняемого файла, ссылки и устройства; владельцы, права и время не хранятся."),
        SaveFormat.Cpio => L.T("Архив cpio в формате newc — так устроен initramfs ядра Linux. Хранит права, владельцев, время, ссылки и устройства; расширенные атрибуты (метки SELinux, capabilities) не хранит."),
        SaveFormat.Erofs => L.T("Файловая система только для чтения со сжатием в кластеры фиксированного размера: быстрый доступ к любому месту файла. Linux 5.4 и новее (Android, образы контейнеров, live-системы)."),
        SaveFormat.Ext4 => L.T("ext4 с экстентами; без контрольных сумм метаданных, монтируется любым ядром Linux 2.6.28+."),
        SaveFormat.Ext3 => L.T("ext3: журнал и карты блоков."),
        SaveFormat.Ext2 => L.T("ext2: без журнала, карты блоков."),
        SaveFormat.Fat => L.T("FAT12/16/32 с длинными именами: загрузочные разделы карт памяти, раздел EFI, флешки. Хранит только файлы и папки — без владельцев, прав и ссылок; время с точностью до 2 секунд."),
        SaveFormat.Jffs2 => L.T("Журналируемая файловая система для NOR-флеш (и старых NAND). Порядок байтов и размер блока стирания должны совпадать с устройством; для NAND маркеры очистки выключают."),
        SaveFormat.Ubifs => L.T("Файловая система для «сырой» флеш-памяти. Образ делается под конкретную память: размер блока стирания и страницы должны совпадать с её параметрами."),
        _ => L.T("Архив с правами, владельцами, ссылками, устройствами и расширенными атрибутами."),
    };

    private void RaiseCompression()
    {
        foreach (string p in new[] { nameof(ShowXz), nameof(ShowGzip), nameof(ShowZstd), nameof(ShowBcj), nameof(TarStoresXattrs),
                     nameof(ShowErofsCluster), nameof(ShowErofsLz4Level), nameof(ShowErofsLzma), nameof(ShowErofsZstd), nameof(ShowErofsDeflate),
                     nameof(ErofsNote), nameof(HasErofsNote) }) OnPropertyChanged(p);
    }

    public bool ShowXz => (IsSquash && _o.SquashFs.Compression == SquashCompression.Xz) || (IsTar && _o.Tar.Compression == ArchiveCompression.Xz)
                          || (IsCpio && _o.Cpio.Compression == ArchiveCompression.Xz);
    public bool ShowGzip => (IsSquash && _o.SquashFs.Compression == SquashCompression.Gzip) || (IsTar && _o.Tar.Compression == ArchiveCompression.Gzip)
                            || (IsCpio && _o.Cpio.Compression == ArchiveCompression.Gzip);
    public bool ShowZstd => (IsSquash && _o.SquashFs.Compression == SquashCompression.Zstd) || (IsTar && _o.Tar.Compression == ArchiveCompression.Zstd)
                            || (IsCpio && _o.Cpio.Compression == ArchiveCompression.Zstd);
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

    // ---- EROFS
    private string _erofsName;

    public IReadOnlyList<Choice<ErofsCompression>> ErofsCompressions => OptionLists.ErofsCompressions;
    public IReadOnlyList<Choice<int>> ErofsPclusterSizes { get; }
    public IReadOnlyList<Choice<int>> ErofsLz4Levels => OptionLists.ErofsLz4Levels;

    public ErofsCompression ErofsCompression
    {
        get => _o.Erofs.Compression;
        set
        {
            if (_o.Erofs.Compression == value) return;
            _o.Erofs.Compression = value;
            OnPropertyChanged();
            RaiseCompression();
        }
    }

    public int ErofsPclusterSize
    {
        get => _o.Erofs.PclusterSize;
        set
        {
            _o.Erofs.PclusterSize = value;
            // A dictionary taken from the source image belongs to its cluster size.
            _o.Erofs.LzmaDictionarySize = 0;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ErofsNote));
            OnPropertyChanged(nameof(HasErofsNote));
        }
    }

    public int ErofsLz4Level { get => _o.Erofs.Lz4Level; set { _o.Erofs.Lz4Level = value; OnPropertyChanged(); } }
    public int ErofsLzmaEffort { get => _o.Erofs.LzmaEffort; set { _o.Erofs.LzmaEffort = value; OnPropertyChanged(); } }
    public int ErofsZstdLevel { get => _o.Erofs.ZstdLevel; set { _o.Erofs.ZstdLevel = value; OnPropertyChanged(); } }
    public int ErofsDeflateLevel { get => _o.Erofs.DeflateLevel; set { _o.Erofs.DeflateLevel = value; OnPropertyChanged(); } }
    public bool ErofsInlineData { get => _o.Erofs.InlineData; set { _o.Erofs.InlineData = value; OnPropertyChanged(); } }
    public bool ErofsDeduplicate { get => _o.Erofs.Deduplicate; set { _o.Erofs.Deduplicate = value; OnPropertyChanged(); } }
    public bool ErofsShareXattrs { get => _o.Erofs.ShareXattrs; set { _o.Erofs.ShareXattrs = value; OnPropertyChanged(); } }
    public string ErofsVolumeName { get => _erofsName; set { if (SetProperty(ref _erofsName, value)) Error = null; } }
    public bool ErofsKeepUuid { get => _o.Erofs.Uuid is not null; set { _o.Erofs.Uuid = value ? _doc.Erofs?.Uuid : null; OnPropertyChanged(); } }
    public bool CanKeepErofsUuid => _doc.Erofs is not null;

    public bool ShowErofsCluster => IsErofs && _o.Erofs.Compression != ErofsCompression.None;
    public bool ShowErofsLz4Level => IsErofs && _o.Erofs.Compression == ErofsCompression.Lz4Hc;
    public bool ShowErofsLzma => IsErofs && _o.Erofs.Compression == ErofsCompression.Lzma;
    public bool ShowErofsZstd => IsErofs && _o.Erofs.Compression == ErofsCompression.Zstd;
    public bool ShowErofsDeflate => IsErofs && _o.Erofs.Compression == ErofsCompression.Deflate;

    /// <summary>What the chosen method and cluster size ask of the kernel.</summary>
    public string ErofsNote
    {
        get
        {
            if (!ShowErofsCluster) return "";
            bool big = _o.Erofs.PclusterSize > 4096;
            if (_o.Erofs.Compression == ErofsCompression.Lzma)
                return L.T("Кластер распаковывается целиком: чем он больше, тем плотнее сжатие и медленнее чтение. LZMA читает ядро Linux 5.16 и новее с CONFIG_EROFS_FS_ZIP_LZMA.");
            if (_o.Erofs.Compression == ErofsCompression.Zstd)
                return L.T("Кластер распаковывается целиком: чем он больше, тем плотнее сжатие и медленнее чтение. Zstandard читает ядро Linux 6.10 и новее с CONFIG_EROFS_FS_ZIP_ZSTD; высокие уровни сжимают заметно медленнее.");
            if (_o.Erofs.Compression == ErofsCompression.Deflate)
                return L.T("Кластер распаковывается целиком: чем он больше, тем плотнее сжатие и медленнее чтение. DEFLATE читает ядро Linux 6.6 и новее с CONFIG_EROFS_FS_ZIP_DEFLATE.");
            return big
                ? L.T("Кластер распаковывается целиком: чем он больше, тем плотнее сжатие и медленнее чтение. Кластеры больше 4 КБ читает ядро Linux 5.13 и новее.")
                : L.T("Кластер распаковывается целиком: чем он больше, тем плотнее сжатие и медленнее чтение. Кластеры по 4 КБ читает любое ядро с поддержкой EROFS.");
        }
    }

    public bool HasErofsNote => ErofsNote.Length > 0;

    // ---- levels shared by SquashFS and tar (each keeps its own value)
    public int XzEffort
    {
        get => IsTar ? _o.Tar.XzEffort : IsCpio ? _o.Cpio.XzEffort : _o.SquashFs.XzEffort;
        set { if (IsTar) _o.Tar.XzEffort = value; else if (IsCpio) _o.Cpio.XzEffort = value; else _o.SquashFs.XzEffort = value; OnPropertyChanged(); }
    }

    public int GzipLevel
    {
        get => IsTar ? _o.Tar.GzipLevel : IsCpio ? _o.Cpio.GzipLevel : _o.SquashFs.GzipLevel;
        set { if (IsTar) _o.Tar.GzipLevel = value; else if (IsCpio) _o.Cpio.GzipLevel = value; else _o.SquashFs.GzipLevel = value; OnPropertyChanged(); }
    }

    public int ZstdLevel
    {
        get => IsTar ? _o.Tar.ZstdLevel : IsCpio ? _o.Cpio.ZstdLevel : _o.SquashFs.ZstdLevel;
        set { if (IsTar) _o.Tar.ZstdLevel = value; else if (IsCpio) _o.Cpio.ZstdLevel = value; else _o.SquashFs.ZstdLevel = value; OnPropertyChanged(); }
    }

    // ---- UBIFS and a new UBI image
    private string _ubifsLeb, _ubifsMinIo, _ubifsMaxLeb, _ubiPeb, _ubiSubPage, _ubiVolumeName;

    public IReadOnlyList<Choice<FsForge.Core.Ubifs.UbifsCompression>> UbifsCompressions => OptionLists.UbifsCompressions;
    public FsForge.Core.Ubifs.UbifsCompression UbifsCompression { get => _o.Ubifs.Compression; set { _o.Ubifs.Compression = value; OnPropertyChanged(); } }
    public string UbifsLebSize { get => _ubifsLeb; set { if (SetProperty(ref _ubifsLeb, value)) Error = null; } }
    public string UbifsMinIoSize { get => _ubifsMinIo; set { if (SetProperty(ref _ubifsMinIo, value)) Error = null; } }
    public string UbifsMaxLebCount { get => _ubifsMaxLeb; set { if (SetProperty(ref _ubifsMaxLeb, value)) Error = null; } }
    public bool SpaceFixup { get => _o.Ubifs.SpaceFixup; set { _o.Ubifs.SpaceFixup = value; OnPropertyChanged(); } }

    /// <summary>Wrap the filesystem into a new UBI image (offered for UBIFS and SquashFS that are not going into an existing container).</summary>
    public bool NewUbi
    {
        get => _o.NewUbi;
        set
        {
            if (_o.NewUbi == value) return;
            _o.NewUbi = value;
            Error = null;
            OnPropertyChanged();
            RaiseContainer();
        }
    }

    public bool ShowNewUbi => (IsUbifs || IsSquash) && !WritesDisk && !FormatLocked;
    public bool ShowUbiOptions => ShowNewUbi && _o.NewUbi;

    /// <summary>The result is a UBI image: the one the document came from, or a new one.</summary>
    public bool WritesUbi => IsUbiContainer && WritesDisk || ShowUbiOptions;

    /// <summary>Inside a UBI image the erase block of UBIFS is the one of the image, not a free choice.</summary>
    public bool CanEditUbifsGeometry => !WritesUbi;

    public string UbifsGeometryNote => IsUbiContainer && WritesDisk
        ? L.T("Размер блока стирания задаёт образ UBI, в который сохраняется файловая система.")
        : ShowUbiOptions ? L.T("Размер блока стирания и страницы берутся из параметров образа UBI ниже.")
        : L.T("NAND 128 КБ со страницей 2048: блок 126976 (с подстраницами 512 — 129024). NAND 256 КБ со страницей 4096: 253952. NOR 64 КБ: блок 65408, страница 1.");

    public string UbiPebSize { get => _ubiPeb; set { if (SetProperty(ref _ubiPeb, value)) Error = null; } }
    public string UbiSubPageSize { get => _ubiSubPage; set { if (SetProperty(ref _ubiSubPage, value)) Error = null; } }
    public string UbiVolumeName { get => _ubiVolumeName; set { if (SetProperty(ref _ubiVolumeName, value)) Error = null; } }
    public bool UbiAutoResize { get => _o.Ubi.AutoResize; set { _o.Ubi.AutoResize = value; OnPropertyChanged(); } }

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

    // ---- cpio
    public ArchiveCompression CpioCompression
    {
        get => _o.Cpio.Compression;
        set
        {
            if (_o.Cpio.Compression == value) return;
            _o.Cpio.Compression = value;
            OnPropertyChanged();
            RaiseCompression();
            OnPropertyChanged(nameof(XzEffort));
            OnPropertyChanged(nameof(GzipLevel));
            OnPropertyChanged(nameof(ZstdLevel));
            OnPropertyChanged(nameof(Extension));
        }
    }

    public CpioHeader CpioHeader { get => _o.Cpio.Header; set { _o.Cpio.Header = value; OnPropertyChanged(); } }
    public string CpioPrefix { get => _o.Cpio.PathPrefix; set { _o.Cpio.PathPrefix = value ?? ""; OnPropertyChanged(); } }
    public bool CpioRootEntry { get => _o.Cpio.IncludeRootEntry; set { _o.Cpio.IncludeRootEntry = value; OnPropertyChanged(); } }
    public bool CpioPad { get => _o.Cpio.Pad512; set { _o.Cpio.Pad512 = value; OnPropertyChanged(); } }

    /// <summary>Several archives one after another (early microcode in front of the initramfs) become one.</summary>
    public string CpioPartsNote => IsCpio && _doc.Cpio is { Parts: > 1 } c
        ? L.F("Исходный файл состоит из нескольких архивов cpio подряд ({0}): например, микрокод процессора перед initramfs. Они будут записаны одним архивом, и ядро уже не загрузит микрокод на раннем этапе.", c.Parts)
        : "";

    public bool HasCpioPartsNote => CpioPartsNote.Length > 0;

    // ---- CramFS
    public IReadOnlyList<Choice<int>> CramfsBlockSizes => OptionLists.CramfsBlockSizes;
    public bool CramfsBigEndian { get => _o.Cramfs.BigEndian; set { _o.Cramfs.BigEndian = value; OnPropertyChanged(); OnPropertyChanged(nameof(CramfsNote)); } }
    public int CramfsBlockSize { get => _o.Cramfs.BlockSize; set { _o.Cramfs.BlockSize = value; OnPropertyChanged(); OnPropertyChanged(nameof(CramfsNote)); } }
    public int CramfsLevel { get => _o.Cramfs.Level; set { _o.Cramfs.Level = value; OnPropertyChanged(); } }
    public string CramfsVolumeName { get => _o.Cramfs.VolumeName; set { _o.Cramfs.VolumeName = value ?? ""; OnPropertyChanged(); Error = null; } }
    public bool CramfsPad { get => _o.Cramfs.Pad; set { _o.Cramfs.Pad = value; OnPropertyChanged(); } }
    public bool CramfsHoles { get => _o.Cramfs.Holes; set { _o.Cramfs.Holes = value; OnPropertyChanged(); } }

    /// <summary>The kernel reads only images of its own byte order and page size.</summary>
    public string CramfsNote => L.F("Ядро читает только образ своего порядка байтов ({0}) с блоком, равным размеру страницы памяти ({1}).",
        _o.Cramfs.BigEndian ? "big-endian" : "little-endian", Fmt.Size(_o.Cramfs.BlockSize));

    // ---- romfs
    public IReadOnlyList<Choice<int>> RomfsAlignments => OptionLists.RomfsAlignments;
    public string RomfsVolumeName { get => _o.Romfs.VolumeName; set { _o.Romfs.VolumeName = value ?? ""; OnPropertyChanged(); Error = null; } }
    public int RomfsAlignment { get => _o.Romfs.DataAlignment; set { _o.Romfs.DataAlignment = value; OnPropertyChanged(); } }

    // ---- JFFS2
    public IReadOnlyList<Choice<FsForge.Core.Jffs2.Jffs2Compression>> Jffs2Compressions => OptionLists.Jffs2Compressions;
    public IReadOnlyList<Choice<bool>> Jffs2ByteOrders => OptionLists.Jffs2ByteOrders;
    public IReadOnlyList<Choice<int>> Jffs2EraseBlockSizes { get; }
    public FsForge.Core.Jffs2.Jffs2Compression Jffs2Compression { get => _o.Jffs2.Compression; set { _o.Jffs2.Compression = value; OnPropertyChanged(); } }
    public bool Jffs2BigEndian { get => _o.Jffs2.BigEndian; set { _o.Jffs2.BigEndian = value; OnPropertyChanged(); } }
    public int Jffs2EraseBlockSize { get => _o.Jffs2.EraseBlockSize; set { _o.Jffs2.EraseBlockSize = value; OnPropertyChanged(); } }
    public bool Jffs2Cleanmarkers { get => _o.Jffs2.Cleanmarkers; set { _o.Jffs2.Cleanmarkers = value; OnPropertyChanged(); } }
    public bool Jffs2Pad { get => _o.Jffs2.PadToEraseBlock; set { _o.Jffs2.PadToEraseBlock = value; OnPropertyChanged(); } }
    public bool Jffs2Xattrs { get => _o.Jffs2.StoreXattrs; set { _o.Jffs2.StoreXattrs = value; OnPropertyChanged(); } }

    /// <summary>Warns when the erase block size of the source was estimated and not read from clean markers.</summary>
    public string Jffs2Note => _doc.Jffs2 is { EraseBlockSizeKnown: false } j && _o.Jffs2.EraseBlockSize == j.EraseBlockSize
        ? L.F("Размер блока стирания исходного образа ({0}) определён по расположению узлов, а не по маркерам очистки. Проверьте, что он совпадает с размером блока флеш-памяти устройства.", Fmt.Size(j.EraseBlockSize))
        : "";

    public bool HasJffs2Note => Jffs2Note.Length > 0;

    // ---- common to all formats
    /// <summary>JFFS2 has its own switch for attributes (a kernel without xattr support refuses such an image).</summary>
    public bool ShowCommonXattrs => !IsJffs2 && !IsFat && !IsCpio && !IsCramfs && !IsRomfs;

    /// <summary>FAT and romfs have no owners: the "everything belongs to root" switch means nothing there.</summary>
    public bool ShowAllRoot => !IsFat && !IsRomfs;

    // ---- FAT
    private string _fatSize, _fatLabel;
    public IReadOnlyList<Choice<FsForge.Core.Fat.FatType>> FatTypes => OptionLists.FatTypes;
    public IReadOnlyList<Choice<int>> FatClusterSizes => OptionLists.FatClusterSizes;
    public FsForge.Core.Fat.FatType FatType { get => _o.Fat.Type; set { _o.Fat.Type = value; OnPropertyChanged(); Error = null; } }
    public int FatClusterSize { get => _o.Fat.ClusterSize; set { _o.Fat.ClusterSize = value; OnPropertyChanged(); Error = null; } }
    public string FatSizeMiB { get => _fatSize; set { if (SetProperty(ref _fatSize, value)) Error = null; } }
    public string FatLabel { get => _fatLabel; set { if (SetProperty(ref _fatLabel, value)) Error = null; } }

    /// <summary>In a disk image the filesystem fills its partition.</summary>
    public bool FatSizeEditable => !WritesDisk;

    public string FatSizeNote => WritesDisk && _doc.Container is { } disk
        ? L.F("В образе диска FAT занимает раздел целиком ({0}).", Fmt.Size(disk.Partition.Length))
        : L.T("Пусто — наименьший размер, в который помещается содержимое.");

    public bool HasFatSizeNote => FatSizeNote.Length > 0;

    public bool StoreXattrs
    {
        get => _o.SquashFs.StoreXattrs;
        set { _o.SquashFs.StoreXattrs = _o.Ext.StoreXattrs = _o.Tar.StoreXattrs = _o.Ubifs.StoreXattrs = _o.Erofs.StoreXattrs = value; OnPropertyChanged(); }
    }

    public bool AllRoot
    {
        get => _o.SquashFs.AllRoot;
        set { _o.SquashFs.AllRoot = _o.Ext.AllRoot = _o.Tar.AllRoot = _o.Ubifs.AllRoot = _o.Jffs2.AllRoot = _o.Erofs.AllRoot = _o.Cpio.AllRoot = _o.Cramfs.AllRoot = value; OnPropertyChanged(); }
    }

    public VerifyLevel Verify { get => _o.Verify; set { _o.Verify = value; OnPropertyChanged(); } }
    public bool KeepBackup { get => _o.KeepBackup; set { _o.KeepBackup = value; OnPropertyChanged(); } }

    /// <summary>The validated options; set by <see cref="Accept"/>.</summary>
    public SaveOptions? Result { get; private set; }

    /// <summary>File extension that fits the chosen format (".squashfs", ".ext4", ".tar.gz"...); for a disk image, the one it had.</summary>
    public string Extension => WritesDisk ? DiskExtension(_doc.FilePath) : ShowUbiOptions ? ".ubi" : _format switch
    {
        SaveFormat.SquashFs => ".squashfs",
        SaveFormat.Erofs => ".erofs",
        SaveFormat.Ubifs => ".ubifs",
        SaveFormat.Jffs2 => ".jffs2",
        SaveFormat.Fat => ".vfat",
        SaveFormat.Ext4 => ".ext4",
        SaveFormat.Ext3 => ".ext3",
        SaveFormat.Ext2 => ".ext2",
        SaveFormat.Cpio => CpioFormat.DefaultExtension(_o.Cpio.Compression),
        SaveFormat.Cramfs => ".cramfs",
        SaveFormat.Romfs => ".romfs",
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
            SaveFormat.Erofs => ImageFormat.Erofs,
            SaveFormat.Ubifs => ImageFormat.Ubifs,
            SaveFormat.Jffs2 => ImageFormat.Jffs2,
            SaveFormat.Fat => ImageFormat.Fat,
            SaveFormat.Tar => ImageFormat.Tar,
            SaveFormat.Cpio => ImageFormat.Cpio,
            SaveFormat.Cramfs => ImageFormat.Cramfs,
            SaveFormat.Romfs => ImageFormat.Romfs,
            _ => ImageFormat.Ext,
        };
        o.NewUbi = ShowUbiOptions;

        if (o.NewUbi)
        {
            if (!int.TryParse(_ubiPeb.Trim(), out int peb) || !int.TryParse(_ubiSubPage.Trim().Length == 0 ? "0" : _ubiSubPage.Trim(), out int subPage))
                return Fail(L.T("Размер блока стирания и подстраницы образа UBI — целые числа байт."));
            if (!int.TryParse(_ubifsMinIo.Trim(), out int page)) return Fail(L.T("Размер страницы (мин. единицы ввода-вывода) — целое число байт."));
            o.Ubi.PebSize = peb;
            o.Ubi.SubPageSize = subPage;
            o.Ubi.MinIoSize = page;
            o.Ubi.VolumeName = _ubiVolumeName.Trim();
            try { o.Ubi.Validate(); }
            catch (FsException ex) { return Fail(ex.Message); }
            o.Ubifs.LebSize = o.Ubi.LebSize;
            o.Ubifs.MinIoSize = page;
        }
        if (IsUbifs)
        {
            if (!o.NewUbi && CanEditUbifsGeometry)
            {
                if (!int.TryParse(_ubifsLeb.Trim(), out int leb) || !int.TryParse(_ubifsMinIo.Trim(), out int minIo))
                    return Fail(L.T("Размер блока стирания и страницы UBIFS — целые числа байт."));
                o.Ubifs.LebSize = leb;
                o.Ubifs.MinIoSize = minIo;
            }
            if (!int.TryParse(_ubifsMaxLeb.Trim().Length == 0 ? "0" : _ubifsMaxLeb.Trim(), out int maxLeb) || maxLeb < 0)
                return Fail(L.T("Максимальное число блоков стирания — целое неотрицательное число (0 — выбрать автоматически)."));
            o.Ubifs.MaxLebCount = maxLeb;
            // A changed geometry makes the journal and log of the source meaningless: let the writer size them again.
            if (_doc.Ubifs is not { } source || source.LebSize != o.Ubifs.LebSize || source.MaxLebCount != maxLeb)
            {
                o.Ubifs.JournalBytes = 0;
                o.Ubifs.LogLebs = 0;
            }
        }

        if (IsFat)
        {
            if (WritesDisk) o.Fat.SizeBytes = 0;          // the partition decides
            else if (_fatSize.Trim().Length == 0) o.Fat.SizeBytes = 0;
            else if (!long.TryParse(_fatSize.Trim(), out long mib) || mib < 1 || mib > 2L << 20)
                return Fail(L.T("Размер образа — целое число мегабайт (МиБ), не меньше 1."));
            else o.Fat.SizeBytes = mib << 20;
            o.Fat.Label = _fatLabel.Trim();
        }

        if (IsErofs)
        {
            if (System.Text.Encoding.UTF8.GetByteCount(_erofsName.Trim()) > 16) return Fail(L.T("Метка тома не длиннее 16 байт."));
            o.Erofs.VolumeName = _erofsName.Trim();
        }

        o.SquashFs.XzFilters.Clear();
        if (_bcj >= 0) o.SquashFs.XzFilters.Add((BcjFilter)_bcj);
        // The dictionary of a re-saved image must still fit the (possibly changed) block size.
        if (o.SquashFs.XzDictionarySize > o.SquashFs.BlockSize) o.SquashFs.XzDictionarySize = 0;

        if (IsExt)
        {
            o.Ext.Kind = _format switch { SaveFormat.Ext2 => ExtKind.Ext2, SaveFormat.Ext3 => ExtKind.Ext3, _ => ExtKind.Ext4 };
            if (o.Ext.Kind == ExtKind.Ext2) o.Ext.Journal = false;
            else if (o.Ext.Kind == ExtKind.Ext3) o.Ext.Journal = true;
            if (System.Text.Encoding.UTF8.GetByteCount(_label) > 16) return Fail(L.T("Метка тома не длиннее 16 байт."));
            o.Ext.Label = _label;
            if (!double.TryParse(_reserved.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double reserved)
                || reserved is < 0 or > 50)
                return Fail(L.T("Резерв для root — число от 0 до 50 процентов."));
            o.Ext.ReservedPercent = reserved;

            switch (_sizeMode)
            {
                case ExtSizeMode.Keep:
                    // Geometry of the source is kept as it is.
                    break;
                case ExtSizeMode.Fixed:
                    if (!long.TryParse(_sizeMiB.Trim(), out long mib) || mib < 1 || mib > 16L << 20)
                        return Fail(L.T("Размер образа — целое число мегабайт (МиБ), не меньше 1."));
                    if (WritesDisk && _doc.Container is { CanGrow: false } disk && mib << 20 > disk.Partition.Length)
                        return Fail(L.F("Раздел {0} вмещает {1} и не может быть увеличен — укажите размер не больше этого.", disk.Partition.Number, Fmt.Size(disk.Partition.Length)));
                    o.Ext.SizeBytes = mib << 20;
                    o.Ext.ExtraFreeBytes = 0;
                    o.Ext.InodeCount = 0;
                    o.Ext.JournalBytes = 0;
                    break;
                default:
                    if (!long.TryParse(_extraFreeMiB.Trim().Length == 0 ? "0" : _extraFreeMiB.Trim(), out long free) || free < 0 || free > 16L << 20)
                        return Fail(L.T("Запас свободного места — целое неотрицательное число мегабайт."));
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
                case ImageFormat.Ubifs: o.Ubifs.Validate(); break;
                case ImageFormat.Jffs2: o.Jffs2.Validate(); break;
                case ImageFormat.Fat: o.Fat.Validate(); break;
                case ImageFormat.Erofs: o.Erofs.Validate(); break;
                case ImageFormat.Cpio: o.Cpio.Validate(); break;
                case ImageFormat.Cramfs: o.Cramfs.Validate(); break;
                case ImageFormat.Romfs: o.Romfs.Validate(); break;
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
