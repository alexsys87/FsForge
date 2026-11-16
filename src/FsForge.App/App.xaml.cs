using System.Windows;
using System.Windows.Threading;
using FsForge.App.Services;
using FsForge.App.Views;
using FsForge.Presentation.Mvvm;
using FsForge.Presentation.Services;
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
        // The language is fixed for the lifetime of the process: every window and list is built in it.
        L.Set(settings.Language);

        // Theme dictionaries are replaced before any control exists, not under a window that is already shown.
        Views.MainWindow.ApplyThemeResources(settings, Views.MainWindow.BackdropOf(settings));

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
        string? log = WriteCrashLog(ex);
        // The plain system message box works even when the themed windows are the problem.
        MessageBox.Show(
            L.T("Непредвиденная ошибка. Открытый образ не изменён на диске.") + "\n\n" + ex.GetType().Name + ": " + ex.Message + (log is null ? "" : "\n\n" + log),
            "FsForge", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>Writes the full exception with its stack trace so that a failure can be diagnosed later.</summary>
    private static string? WriteCrashLog(Exception ex)
    {
        try
        {
            string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FsForge");
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, "crash.log");
            System.IO.File.AppendAllText(path, DateTime.Now.ToString("u") + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
