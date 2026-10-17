using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace VideoQuality.App.Services;

/// <summary>File pickers and Reveal-in-folder, as the pages use them.</summary>
public interface IFileDialogs
{
    Task<IReadOnlyList<string>> PickFilesAsync(Control owner, string title, bool multiple, (string Name, string[] Patterns)[] types);

    Task<string?> PickFolderAsync(Control owner, string title);

    Task<string?> PickSaveFileAsync(Control owner, string title, string suggestedName, string extension);

    Task RevealAsync(Control owner, string path);
}

/// <summary>Drag-and-drop and file pickers for the tool pages.</summary>
public static class PageIo
{
    /// <summary>The system dialogs; tests replace them with scripted answers.</summary>
    public static IFileDialogs Dialogs { get; set; } = new StorageDialogs();

    /// <summary>
    /// Makes a page accept dropped files and folders. While a drag with files is over it the page
    /// gets the <c>dragging</c> class (highlighted drop target); non-local items arrive as null.
    /// </summary>
    public static void AcceptDrops(Control page, Func<IReadOnlyList<string?>, Task> onDrop)
    {
        DragDrop.SetAllowDrop(page, true);
        page.AddHandler(DragDrop.DragEnterEvent, (_, e) => OnDragEnter(page, e.DataTransfer));
        page.AddHandler(DragDrop.DragLeaveEvent, (_, e) => OnDragLeave(page, e.GetPosition(page)));
        page.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = DragEffects(e.DataTransfer);
            e.Handled = true;
        });
        page.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            e.Handled = true;
            await OnDropAsync(page, e.DataTransfer, onDrop);
        });
    }

    internal static void OnDragEnter(Control page, IDataTransfer data)
    {
        if (data.Contains(DataFormat.File))
        {
            page.Classes.Add("dragging");
        }
    }

    /// <summary>Leaving into a child raises DragLeave too; only leaving the page's bounds ends the highlight.</summary>
    internal static void OnDragLeave(Control page, Avalonia.Point p)
    {
        if (p.X <= 0 || p.Y <= 0 || p.X >= page.Bounds.Width || p.Y >= page.Bounds.Height)
        {
            page.Classes.Remove("dragging");
        }
    }

    internal static DragDropEffects DragEffects(IDataTransfer data) =>
        data.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    internal static Task OnDropAsync(Control page, IDataTransfer data, Func<IReadOnlyList<string?>, Task> onDrop)
    {
        page.Classes.Remove("dragging");
        return onDrop([.. (data.TryGetFiles() ?? []).Select(item => item.TryGetLocalPath())]);
    }

    public static Task<IReadOnlyList<string>> PickFilesAsync(Control owner, string title, bool multiple, params (string Name, string[] Patterns)[] types) =>
        Dialogs.PickFilesAsync(owner, title, multiple, types);

    public static Task<string?> PickFolderAsync(Control owner, string title) => Dialogs.PickFolderAsync(owner, title);

    public static Task<string?> PickSaveFileAsync(Control owner, string title, string suggestedName, string extension) =>
        Dialogs.PickSaveFileAsync(owner, title, suggestedName, extension);

    public static Task RevealAsync(Control owner, string path) => Dialogs.RevealAsync(owner, path);
}

/// <summary>The window's storage provider and launcher. A control outside a window picks nothing.</summary>
public sealed class StorageDialogs : IFileDialogs
{
    public async Task<IReadOnlyList<string>> PickFilesAsync(Control owner, string title, bool multiple, (string Name, string[] Patterns)[] types)
    {
        if (TopLevel.GetTopLevel(owner) is not { } top)
        {
            return [];
        }

        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = multiple,
            FileTypeFilter = types.Length == 0 ? null : [.. types.Select(t => new FilePickerFileType(t.Name) { Patterns = t.Patterns })],
        });
        return LocalPaths(files);
    }

    public async Task<string?> PickFolderAsync(Control owner, string title)
    {
        if (TopLevel.GetTopLevel(owner) is not { } top)
        {
            return null;
        }

        IReadOnlyList<IStorageFolder> folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return LocalPaths(folders).FirstOrDefault();
    }

    public async Task<string?> PickSaveFileAsync(Control owner, string title, string suggestedName, string extension)
    {
        if (TopLevel.GetTopLevel(owner) is not { } top)
        {
            return null;
        }

        IStorageFile? file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
        });
        return LocalPaths(file is null ? [] : [file]).FirstOrDefault();
    }

    public async Task RevealAsync(Control owner, string path)
    {
        if (FolderOf(path) is { } folder && TopLevel.GetTopLevel(owner) is { } top)
        {
            await top.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
        }
    }

    /// <summary>The folder to open for a path: itself, or the folder holding it; null when neither exists.</summary>
    internal static string? FolderOf(string path)
    {
        string? folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        return folder is not null && Directory.Exists(folder) ? folder : null;
    }

    internal static IReadOnlyList<string> LocalPaths(IEnumerable<IStorageItem> items) =>
        [.. items.Select(item => item.TryGetLocalPath()).OfType<string>()];
}
