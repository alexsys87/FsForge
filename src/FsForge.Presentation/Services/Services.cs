using FsForge.Core.Images;

namespace FsForge.Presentation.Services;

public enum MessageKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>Answer to a question with up to two action buttons and "Cancel".</summary>
public enum DialogAnswer
{
    Cancel,
    Primary,
    Secondary,
}

/// <summary>
/// Everything the view models need from the user interface. The WPF application implements it;
/// tests use a scripted fake.
/// </summary>
public interface IDialogService
{
    /// <summary>Asks for an image or archive to open; null when cancelled.</summary>
    string? PickImageToOpen(string? initialDirectory);

    /// <summary>Asks for files to add to the image; empty when cancelled.</summary>
    IReadOnlyList<string> PickFilesToAdd(string? initialDirectory);

    string? PickFolder(string title, string? initialDirectory);

    /// <summary>Asks where to save an image of the given format; null when cancelled.</summary>
    /// <param name="diskImage">The file is a whole disk image (partition table and partitions), not a filesystem alone.</param>
    string? PickSaveFile(string title, string suggestedName, ImageFormat format, string extension, string? initialDirectory, bool diskImage = false);

    /// <summary>Shows the modal dialog that belongs to a view model; true when the user accepted it.</summary>
    bool ShowDialog(object viewModel);

    Task<DialogAnswer> AskAsync(string title, string message, string primary, string? secondary = null, string? cancel = null);

    Task ShowMessageAsync(string title, string message, MessageKind kind = MessageKind.Info);

    /// <summary>Short, self-dismissing notification.</summary>
    void Notify(string title, string message, MessageKind kind = MessageKind.Info);
}

/// <summary>Operating system integration.</summary>
public interface IShellService
{
    /// <summary>Opens a folder in the file manager.</summary>
    void OpenFolder(string path);

    /// <summary>Opens a file with the given program, or with the associated one when no program is given.</summary>
    void OpenFile(string path, string? program);

    void SetClipboardText(string text);
}

/// <summary>Runs code on the user interface thread.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>Dispatcher for tests and console use: runs everything inline.</summary>
public sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}
