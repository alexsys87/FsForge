using System.Windows;
using System.Windows.Threading;
using FsForge.App.Services;
using FsForge.App.Views;
using FsForge.Presentation.Mvvm;
using FsForge.Presentation.Settings;
using FsForge.Presentation.ViewModels;

namespace FsForge.App;

/// <summary>Application entry point: wires the view model to the WPF services.</summary>
public partial class App : Application
{
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        AsyncRelayCommand.UnhandledException = ex => ReportCrash(ex);

        var store = new SettingsStore();
        AppSettings settings = store.Load();

        MainWindow? window = null;
        var dialogs = new DialogService(() => window, () => window?.SnackbarHost);
        _viewModel = new MainViewModel(dialogs, new ShellService(), new UiDispatcher(Dispatcher), store, settings);
        window = new MainWindow(_viewModel);
        MainWindow = window;
        window.Show();

        // "Open with" and drag onto the executable pass the file as the first argument.
        if (e.Args.Length > 0 && !string.IsNullOrWhiteSpace(e.Args[0]))
        {
            string path = e.Args[0];
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, async () => await _viewModel.OpenPathAsync(path));
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ReportCrash(e.Exception);
    }

    private static void ReportCrash(Exception ex)
    {
        // The plain system message box works even when the themed windows are the problem.
        MessageBox.Show(
            "Непредвиденная ошибка. Открытый образ не изменён на диске.\n\n" + ex.GetType().Name + ": " + ex.Message,
            "FsForge", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
