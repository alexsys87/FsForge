using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FsForge.Core;
using FsForge.Presentation.Services;
using FsForge.Presentation.Settings;
using FsForge.Presentation.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using GridViewColumn = System.Windows.Controls.GridViewColumn;

namespace FsForge.App.Views;

/// <summary>
/// The main window. Everything with meaning lives in <see cref="MainViewModel"/>; this class only
/// translates WPF events (selection, drag and drop, column clicks, window placement) for it.
/// </summary>
public partial class MainWindow
{
    private readonly MainViewModel _vm;
    private readonly Dictionary<GridViewColumn, (string Key, string Title)> _columns = new();
    private bool _closeConfirmed, _applyingSelection, _internalDrag;
    private Point _dragStart;
    private bool _dragArmed;
    private double _locationWidth = 220;

    public MainWindow(MainViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        _columns[ColName] = ("Name", L.T("Имя"));
        _columns[ColLocation] = ("Location", L.T("Папка"));
        _columns[ColSize] = ("Size", L.T("Размер"));
        _columns[ColMode] = ("Mode", L.T("Права"));
        _columns[ColOwner] = ("Owner", L.T("Владелец : группа"));
        _columns[ColModified] = ("Modified", L.T("Изменён"));
        _columns[ColType] = ("Type", L.T("Тип"));

        RestorePlacement();
        ApplyTreePane();
        UpdateSortIndicators();

        _vm.SelectionRequested += OnSelectionRequested;
        _vm.AppearanceChanged += () => ApplyTheme(reapplyTheme: true);
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        // The theme itself is applied before the window is created (see App.OnStartup); swapping the theme
        // dictionaries under live controls can fail inside WPF with a Freezable context error.
        Loaded += (_, _) => ApplyTheme(reapplyTheme: false);
        Activated += async (_, _) => await _vm.CheckExternalEditsAsync();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Where short notifications are shown.</summary>
    public SnackbarPresenter SnackbarHost => SnackbarPresenter;

    // ----------------------------------------------------------- appearance

    /// <summary>Applies the theme from the settings. Called before the first window exists, and again when the user changes it.</summary>
    internal static void ApplyThemeResources(AppSettings s, WindowBackdropType backdrop)
    {
        if (s.Theme == AppTheme.System)
            ApplicationThemeManager.ApplySystemTheme();
        else
            ApplicationThemeManager.Apply(s.Theme == AppTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light, backdrop);
    }

    internal static WindowBackdropType BackdropOf(AppSettings s) => s.Backdrop switch
    {
        BackdropKind.Acrylic => WindowBackdropType.Acrylic,
        BackdropKind.Tabbed => WindowBackdropType.Tabbed,
        BackdropKind.None => WindowBackdropType.None,
        _ => WindowBackdropType.Mica,
    };

    private bool _watchingSystemTheme;

    private void ApplyTheme(bool reapplyTheme)
    {
        AppSettings s = _vm.Settings;
        WindowBackdropType backdrop = BackdropOf(s);
        // Start from a clean state: watching the same window twice would install two hooks.
        // UnWatch on a window that was never watched fails inside WPF with a Freezable context error.
        if (_watchingSystemTheme)
        {
            SystemThemeWatcher.UnWatch(this);
            _watchingSystemTheme = false;
        }
        WindowBackdropType = backdrop;
        if (reapplyTheme) ApplyThemeResources(s, backdrop);
        if (s.Theme == AppTheme.System)
        {
            SystemThemeWatcher.Watch(this, backdrop);
            _watchingSystemTheme = true;
        }
    }

    private void RestorePlacement()
    {
        AppSettings s = _vm.Settings;
        if (!s.RememberWindow) return;
        Width = Math.Max(MinWidth, s.WindowWidth);
        Height = Math.Max(MinHeight, s.WindowHeight);
        if (s.WindowLeft is { } left && s.WindowTop is { } top)
        {
            // Only reuse a position that is still on a connected screen.
            bool visible = left + 100 < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
                           && left + Width - 100 > SystemParameters.VirtualScreenLeft
                           && top >= SystemParameters.VirtualScreenTop
                           && top + 60 < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
            if (visible)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        AppSettings s = _vm.Settings;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!bounds.IsEmpty && bounds.Width >= MinWidth && bounds.Height >= MinHeight)
        {
            s.WindowWidth = bounds.Width;
            s.WindowHeight = bounds.Height;
            s.WindowLeft = bounds.Left;
            s.WindowTop = bounds.Top;
        }
        if (_vm.ShowTreePane && TreeColumn.ActualWidth >= 120) s.TreePaneWidth = TreeColumn.ActualWidth;
        _vm.SaveSettings();
    }

    private void ApplyTreePane()
    {
        if (_vm.ShowTreePane)
        {
            TreeColumn.Width = new GridLength(Math.Max(120, _vm.Settings.TreePaneWidth));
            SplitterColumn.Width = new GridLength(6);
        }
        else
        {
            if (TreeColumn.ActualWidth >= 120) _vm.Settings.TreePaneWidth = TreeColumn.ActualWidth;
            TreeColumn.Width = new GridLength(0);
            SplitterColumn.Width = new GridLength(0);
        }
    }

    private void UpdateSortIndicators()
    {
        foreach ((GridViewColumn column, (string key, string title)) in _columns)
        {
            string arrow = key == _vm.SortColumn ? (_vm.SortDescending ? "  ▼" : "  ▲") : "";
            column.Header = title + arrow;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.ShowTreePane):
                ApplyTreePane();
                break;
            case nameof(MainViewModel.SortColumn) or nameof(MainViewModel.SortDescending):
                UpdateSortIndicators();
                break;
            case nameof(MainViewModel.ShowLocationColumn):
                // The "folder" column only makes sense for results gathered from several folders.
                if (_vm.ShowLocationColumn)
                {
                    ColLocation.Width = _locationWidth;
                }
                else
                {
                    if (ColLocation.ActualWidth > 40) _locationWidth = ColLocation.ActualWidth;
                    ColLocation.Width = 0;
                }
                break;
        }
    }

    // ------------------------------------------------------------- closing

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        if (_closeConfirmed)
        {
            SavePlacement();
            return;
        }
        if (_vm.IsBusy)
        {
            e.Cancel = true;
            _vm.CancelCommand.Execute(null);
            return;
        }
        if (_vm.Document is { IsModified: true })
        {
            // The question is asynchronous: cancel this close and repeat it once the user has answered.
            e.Cancel = true;
            if (await _vm.ConfirmDiscardAsync())
            {
                _closeConfirmed = true;
                Close();
            }
            return;
        }
        SavePlacement();
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------ keyboard

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm.IsBusy)
        {
            if (e.Key == Key.Escape) _vm.CancelCommand.Execute(null);
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F && _vm.HasDocument)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.L && _vm.HasDocument)
        {
            ShowPathBox();
            e.Handled = true;
        }
    }

    // -------------------------------------------------------------- address

    private void OnAddressAreaClick(object sender, MouseButtonEventArgs e)
    {
        // Clicks on a breadcrumb item are handled by the breadcrumb itself.
        if (FindAncestor<BreadcrumbBarItem>(e.OriginalSource as DependencyObject) is not null) return;
        ShowPathBox();
        e.Handled = true;
    }

    private void ShowPathBox()
    {
        PathBox.Text = _vm.CurrentPath;
        Breadcrumb.Visibility = Visibility.Collapsed;
        PathBox.Visibility = Visibility.Visible;
        PathBox.Focus();
        PathBox.SelectAll();
    }

    private void HidePathBox()
    {
        PathBox.Visibility = Visibility.Collapsed;
        Breadcrumb.Visibility = Visibility.Visible;
    }

    private void OnPathBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            string path = PathBox.Text;
            HidePathBox();
            _vm.CurrentPath = path;
            FileList.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HidePathBox();
            FileList.Focus();
            e.Handled = true;
        }
    }

    private void OnPathBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e) => HidePathBox();

    // ------------------------------------------------------- tree and list

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeItem item && !ReferenceEquals(_vm.CurrentDirectory, item.Node) && !_vm.IsBusy)
            _vm.NavigateTo(item.Node);
    }

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingSelection) return;
        _vm.SetSelection(FileList.SelectedItems.OfType<NodeItem>());
    }

    private void OnSelectionRequested(IReadOnlyList<NodeItem> items)
    {
        _applyingSelection = true;
        try
        {
            FileList.SelectedItems.Clear();
            foreach (NodeItem item in items) FileList.SelectedItems.Add(item);
        }
        finally
        {
            _applyingSelection = false;
        }
        _vm.SetSelection(FileList.SelectedItems.OfType<NodeItem>());
        if (items.Count > 0) FileList.ScrollIntoView(items[0]);
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (FindAncestor<System.Windows.Controls.ListViewItem>(e.OriginalSource as DependencyObject) is { DataContext: NodeItem item })
        {
            _vm.OpenItemCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Column: { } column } && _columns.TryGetValue(column, out var info))
            _vm.SortBy(info.Key);
    }

    private void OnRecentClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.MenuItem { DataContext: RecentItem item })
            _vm.OpenRecentCommand.Execute(item);
    }

    private void OnRecentButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RecentItem item })
            _vm.OpenRecentCommand.Execute(item);
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    // -------------------------------------------------------- drag and drop

    /// <summary>The directory a drop at this element goes to: a folder row, a tree item or the folder being shown.</summary>
    private FsNode? DropTarget(DependencyObject? source)
    {
        if (FindAncestor<System.Windows.Controls.ListViewItem>(source) is { DataContext: NodeItem { IsDirectory: true } row }) return row.Node;
        if (FindAncestor<System.Windows.Controls.TreeViewItem>(source) is { DataContext: TreeItem tree }) return tree.Node;
        return _vm.CurrentDirectory;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool acceptable = !_internalDrag && !_vm.IsBusy && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = acceptable ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_internalDrag || _vm.IsBusy || e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        FsNode? target = _vm.HasDocument ? DropTarget(e.OriginalSource as DependencyObject) : null;
        Activate();
        try
        {
            await _vm.HandleDropAsync(paths, target);
        }
        catch (Exception ex) when (ex is FsException or System.IO.IOException or UnauthorizedAccessException)
        {
            // HandleDropAsync reports its own failures; this only keeps an "async void" from crashing the app.
        }
    }

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        // A drag may start only from a row that is already selected, so that a plain click still selects.
        _dragArmed = FindAncestor<System.Windows.Controls.ListViewItem>(e.OriginalSource as DependencyObject) is { IsSelected: true }
                     && FindAncestor<GridViewColumnHeader>(e.OriginalSource as DependencyObject) is null;
        _dragStart = e.GetPosition(this);
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed || _vm.IsBusy) return;
        Vector delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragArmed = false;

        List<FsNode> nodes = FileList.SelectedItems.OfType<NodeItem>().Select(i => i.Node).ToList();
        if (nodes.Count == 0) return;
        Mouse.OverrideCursor = Cursors.Wait;
        IReadOnlyList<string> paths;
        try
        {
            paths = _vm.ExtractForDrag(nodes);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
        if (paths.Count == 0 || e.LeftButton != MouseButtonState.Pressed) return;

        var data = new DataObject(DataFormats.FileDrop, paths.ToArray());
        _internalDrag = true;
        try
        {
            DragDrop.DoDragDrop(FileList, data, DragDropEffects.Copy);
        }
        finally
        {
            _internalDrag = false;
        }
    }
}
