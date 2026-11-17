using System.IO;
using System.Windows;
using FsForge.App.Views;
using FsForge.Core.Images;
using FsForge.Presentation.Services;
using FsForge.Presentation.ViewModels;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace FsForge.App.Services;

/// <summary>File pickers, message boxes, modal dialogs and notifications of the application.</summary>
public sealed class DialogService : IDialogService
{
    // File dialog filters: only the descriptions are translated, never the patterns.
    private static string AllFiles => L.T("Все файлы") + "|*.*";

    private static string ImageFilter =>
        L.T("Образы и архивы") + "|*.squashfs;*.sqfs;*.sfs;*.erofs;*.cramfs;*.romfs;*.img;*.ext2;*.ext3;*.ext4;*.ubi;*.ubifs;*.jffs2;*.vfat;*.fat;*.bin;*.tar;*.tar.gz;*.tgz;*.tar.xz;*.txz;*.tar.zst;*.cpio;*.cpio.gz;*.cpio.xz;*.cpio.zst;*.cpio.lz4;*.cpgz;*.gz;*.xz;*.zst;*.lz4|" +
        L.T("Образы дисков (SD-карты)") + "|*.img;*.img.gz;*.img.xz;*.img.zst|" +
        "SquashFS|*.squashfs;*.sqfs;*.sfs|EROFS|*.erofs;*.erofs.img;*.img|ext2/3/4|*.img;*.ext2;*.ext3;*.ext4|" +
        "CramFS|*.cramfs;*.img;*.bin|romfs|*.romfs;*.img;*.bin|" +
        L.T("UBI и UBIFS") + "|*.ubi;*.ubifs;*.ubi.gz;*.ubi.xz;*.ubi.zst;*.bin;*.img|" +
        "JFFS2|*.jffs2;*.jffs2.gz;*.bin;*.img|" +
        "FAT|*.vfat;*.fat;*.img;*.bin|" +
        L.T("Архивы tar") + "|*.tar;*.tar.gz;*.tgz;*.tar.xz;*.txz;*.tar.zst|" +
        L.T("Архивы cpio (initramfs)") + "|*.cpio;*.cpio.gz;*.cpio.xz;*.cpio.zst;*.cpio.lz4;*.cpgz;*.img;*.gz;*.xz;*.zst;*.lz4|" +
        L.T("Образы Android (super, boot, vendor_boot)") + "|super*.img;*boot*.img;recovery*.img;*.img|" + AllFiles;

    private readonly Func<Window?> _owner;
    private readonly Func<SnackbarPresenter?> _snackbar;

    public DialogService(Func<Window?> owner, Func<SnackbarPresenter?> snackbar)
    {
        _owner = owner;
        _snackbar = snackbar;
    }

    private static string? ExistingDirectory(string? path) => !string.IsNullOrEmpty(path) && Directory.Exists(path) ? path : null;

    public string? PickImageToOpen(string? initialDirectory)
    {
        var dialog = new OpenFileDialog { Title = L.T("Открыть образ или архив"), Filter = ImageFilter, CheckFileExists = true };
        if (ExistingDirectory(initialDirectory) is { } dir) dialog.InitialDirectory = dir;
        return dialog.ShowDialog(_owner()) == true ? dialog.FileName : null;
    }

    public IReadOnlyList<string> PickFilesToAdd(string? initialDirectory)
    {
        var dialog = new OpenFileDialog { Title = L.T("Выберите файлы"), Filter = AllFiles, Multiselect = true, CheckFileExists = true };
        if (ExistingDirectory(initialDirectory) is { } dir) dialog.InitialDirectory = dir;
        return dialog.ShowDialog(_owner()) == true ? dialog.FileNames : Array.Empty<string>();
    }

    public string? PickFolder(string title, string? initialDirectory)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (ExistingDirectory(initialDirectory) is { } dir) dialog.InitialDirectory = dir;
        return dialog.ShowDialog(_owner()) == true ? dialog.FolderName : null;
    }

    public string? PickSaveFile(string title, string suggestedName, ImageFormat format, string extension, string? initialDirectory, bool diskImage = false)
    {
        string filter = (diskImage ? L.T("Образ диска") + "|*.img;*.img.gz;*.img.xz;*.img.zst;*.bin" : format switch
        {
            ImageFormat.SquashFs => "SquashFS|*.squashfs;*.sqfs;*.sfs;*.img;*.bin",
            ImageFormat.Ext => L.T("Образ ext2/3/4") + "|*.ext4;*.ext3;*.ext2;*.img;*.bin",
            ImageFormat.Ubifs => L.T("UBI и UBIFS") + "|*.ubi;*.ubifs;*.img;*.bin",
            ImageFormat.Jffs2 => "JFFS2|*.jffs2;*.img;*.bin",
            ImageFormat.Fat => "FAT|*.vfat;*.fat;*.img;*.bin",
            ImageFormat.Erofs => "EROFS|*.erofs;*.img;*.bin",
            ImageFormat.Cramfs => "CramFS|*.cramfs;*.img;*.bin",
            ImageFormat.Romfs => "romfs|*.romfs;*.img;*.bin",
            ImageFormat.Cpio => L.T("Архив cpio") + "|*" + extension + ";*.img",
            _ => L.T("Архив tar") + "|*" + extension,
        }) + "|" + AllFiles;
        var dialog = new SaveFileDialog
        {
            Title = title,
            FileName = suggestedName,
            Filter = filter,
            OverwritePrompt = true,
            AddExtension = false,          // names such as "rootfs.tar.gz" must be kept exactly as typed
        };
        if (ExistingDirectory(initialDirectory) is { } dir) dialog.InitialDirectory = dir;
        return dialog.ShowDialog(_owner()) == true ? dialog.FileName : null;
    }

    public bool ShowDialog(object viewModel)
    {
        if (viewModel is not DialogViewModel vm) throw new ArgumentException("Unknown dialog view model.", nameof(viewModel));
        var window = new DialogWindow(vm);
        if (_owner() is { IsLoaded: true } owner) window.Owner = owner;
        return window.ShowDialog() == true;
    }

    public async Task<DialogAnswer> AskAsync(string title, string message, string primary, string? secondary = null, string? cancel = null)
    {
        var box = new MessageBox
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 },
            PrimaryButtonText = primary,
            CloseButtonText = cancel ?? L.T("Отмена"),
            MinWidth = 380,
        };
        if (secondary is not null) box.SecondaryButtonText = secondary;
        if (_owner() is { IsLoaded: true } owner)
        {
            box.Owner = owner;
            box.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        MessageBoxResult result = await box.ShowDialogAsync();
        return result switch
        {
            MessageBoxResult.Primary => DialogAnswer.Primary,
            MessageBoxResult.Secondary => DialogAnswer.Secondary,
            _ => DialogAnswer.Cancel,
        };
    }

    public async Task ShowMessageAsync(string title, string message, MessageKind kind = MessageKind.Info)
    {
        var text = new System.Windows.Controls.TextBox
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            MaxWidth = 560,
            MaxHeight = 360,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
        };
        var box = new MessageBox { Title = title, Content = text, CloseButtonText = L.T("Закрыть"), MinWidth = 380 };
        if (_owner() is { IsLoaded: true } owner)
        {
            box.Owner = owner;
            box.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        await box.ShowDialogAsync();
    }

    public void Notify(string title, string message, MessageKind kind = MessageKind.Info)
    {
        if (_snackbar() is not { } presenter) return;
        var snackbar = new Snackbar(presenter)
        {
            Title = title,
            Content = message,
            Timeout = TimeSpan.FromSeconds(kind is MessageKind.Warning or MessageKind.Error ? 6 : 3.5),
            Appearance = kind switch
            {
                MessageKind.Success => ControlAppearance.Success,
                MessageKind.Warning => ControlAppearance.Caution,
                MessageKind.Error => ControlAppearance.Danger,
                _ => ControlAppearance.Secondary,
            },
            Icon = new SymbolIcon(kind switch
            {
                MessageKind.Success => SymbolRegular.CheckmarkCircle24,
                MessageKind.Warning => SymbolRegular.Warning24,
                MessageKind.Error => SymbolRegular.ErrorCircle24,
                _ => SymbolRegular.Info24,
            }),
        };
        snackbar.Show();
    }
}
