using System.Globalization;
using System.Windows;
using System.Windows.Data;
using FsForge.Presentation.Services;
using FsForge.Presentation.ViewModels;
using Wpf.Ui.Controls;

namespace FsForge.App.Converters;

/// <summary>true gives Collapsed, false gives Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Visible for a non-empty string, Collapsed otherwise.</summary>
public sealed class TextToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Picks the Fluent icon for a kind of filesystem object.</summary>
public sealed class NodeIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        NodeIcon.Folder => SymbolRegular.Folder20,
        NodeIcon.Executable => SymbolRegular.DocumentLightning20,
        NodeIcon.Symlink => SymbolRegular.Link20,
        NodeIcon.CharDevice => SymbolRegular.Connector20,
        NodeIcon.BlockDevice => SymbolRegular.HardDrive20,
        NodeIcon.Fifo => SymbolRegular.Pipeline20,
        NodeIcon.Socket => SymbolRegular.Connector20,
        _ => SymbolRegular.Document20,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class MessageKindToSeverityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        MessageKind.Success => InfoBarSeverity.Success,
        MessageKind.Warning => InfoBarSeverity.Warning,
        MessageKind.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Folders are shown a little heavier than files.</summary>
public sealed class FolderEmphasisConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? FontWeights.SemiBold : FontWeights.Normal;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
