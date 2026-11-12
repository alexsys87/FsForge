using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using FsForge.Presentation.Services;

namespace FsForge.App.Services;

/// <summary>Opens files and folders with the Windows shell.</summary>
public sealed class ShellService : IShellService
{
    public void OpenFolder(string path)
    {
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true })?.Dispose();
    }

    public void OpenFile(string path, string? program)
    {
        if (!string.IsNullOrWhiteSpace(program))
        {
            try
            {
                var psi = new ProcessStartInfo { FileName = program, UseShellExecute = false };
                psi.ArgumentList.Add(path);
                Process.Start(psi)?.Dispose();
                return;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // The configured editor is missing: fall back to the file association.
            }
        }
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true })?.Dispose();
    }

    public void SetClipboardText(string text)
    {
        try
        {
            Clipboard.SetDataObject(text, copy: true);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another program holds the clipboard open; a retry by the user will work.
        }
    }
}

/// <summary>Marshals work to the WPF dispatcher thread.</summary>
public sealed class UiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action)
    {
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
