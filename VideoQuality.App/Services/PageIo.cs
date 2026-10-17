using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace VideoQuality.App.Services;

/// <summary>Drag-and-drop and file pickers for the tool pages.</summary>
public static class PageIo
{
    /// <summary>
    /// Makes a page accept dropped files and folders. While a drag with files is over it the page
    /// gets the <c>dragging</c> class (highlighted drop target); non-local items arrive as null.
    /// </summary>
    public static void AcceptDrops(Control page, Func<IReadOnlyList<string?>, Task> onDrop)
    {
        DragDrop.SetAllowDrop(page, true);
        page.AddHandler(DragDrop.DragEnterEvent, (_, e) =>
        {
            if (e.DataTransfer.Contains(DataFormat.File))
            {
                page.Classes.Add("dragging");
            }
        });
        page.AddHandler(DragDrop.DragLeaveEvent, (_, e) =>
        {
            Avalonia.Point p = e.GetPosition(page);
            if (p.X <= 0 || p.Y <= 0 || p.X >= page.Bounds.Width || p.Y >= page.Bounds.Height)
            {
                page.Classes.Remove("dragging");
            }
        });
        page.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        });
        page.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            e.Handled = true;
            page.Classes.Remove("dragging");
            var paths = (e.DataTransfer.TryGetFiles() ?? []).Select(item => item.TryGetLocalPath()).ToList();
            await onDrop(paths);
        });
    }

    public static async Task<IReadOnlyList<string>> PickFilesAsync(Control owner, string title, bool multiple, params (string Name, string[] Patterns)[] types)
    {
        if (TopLevel.GetTopLevel(owner) is not { } top)
        {
            return [];
        }

        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = multiple,
            FileTypeFilter = types.Length == 0 ? null : types.Select(t => new FilePickerFileType(t.Name) { Patterns = t.Patterns }).ToList(),
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public static async Task<string?> PickFolderAsync(Control owner, string title)
    {
        if (TopLevel.GetTopLevel(owner) is not { } top)
        {
            return null;
        }

        IReadOnlyList<IStorageFolder> folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public static async Task<string?> PickSaveFileAsync(Control owner, string title, string suggestedName, string extension)
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
        return file?.TryGetLocalPath();
    }

    public static async Task RevealAsync(Control owner, string path)
    {
        string? folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (folder is not null && Directory.Exists(folder) && TopLevel.GetTopLevel(owner) is { } top)
        {
            await top.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
        }
    }
}
