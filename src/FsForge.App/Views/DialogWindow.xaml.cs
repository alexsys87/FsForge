using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FsForge.Presentation.ViewModels;
using Wpf.Ui.Appearance;

namespace FsForge.App.Views;

/// <summary>
/// The one modal window used for every dialog: title bar, a body picked by the type of the view
/// model, an error line and the accept / cancel buttons.
/// </summary>
public partial class DialogWindow
{
    private readonly DialogViewModel _vm;

    public DialogWindow(DialogViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        Width = viewModel switch
        {
            PromptViewModel => 460,
            NewItemViewModel => 480,
            PropertiesViewModel => 580,
            SaveOptionsViewModel => 620,
            SettingsViewModel => 760,
            ImageInfoViewModel => 680,
            MessagesViewModel => 760,
            _ => 520,
        };
        // Dialogs that only show information have nothing to cancel.
        if (viewModel is ImageInfoViewModel or MessagesViewModel)
        {
            CancelButton.Visibility = Visibility.Collapsed;
            AcceptButton.IsCancel = true;
        }
        MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height * 0.9);
        ContentRendered += OnContentRendered;
        // Secondary windows do not follow the application theme by themselves (dark title bar and backdrop).
        Loaded += (_, _) => WindowBackgroundManager.UpdateBackground(this, ApplicationThemeManager.GetAppTheme(), WindowBackdropType);
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        // Put the caret into the first input so that typing works immediately.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            Body.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            if (Keyboard.FocusedElement is TextBox box && _vm is PromptViewModel)
            {
                // Rename: preselect the name without its extension, as Explorer does.
                int dot = box.Text.LastIndexOf('.');
                if (dot > 0) box.Select(0, dot);
                else box.SelectAll();
            }
        });
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        // Bindings that update on focus loss must be flushed before the view model validates.
        if (Keyboard.FocusedElement is TextBox box)
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (_vm.Accept()) DialogResult = true;
    }
}
