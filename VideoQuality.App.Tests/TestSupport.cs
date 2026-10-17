using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VideoQuality.App.Services;

namespace VideoQuality.App.Tests;

/// <summary>File dialogs that answer from a script; unanswered questions are cancelled.</summary>
public sealed class ScriptedDialogs : IFileDialogs
{
    private readonly Queue<object?> _answers = new();

    public List<string> Titles { get; } = [];

    public List<string> Revealed { get; } = [];

    public ScriptedDialogs Answer(params object?[] answers)
    {
        foreach (object? answer in answers)
        {
            _answers.Enqueue(answer);
        }

        return this;
    }

    private T? Next<T>(string title)
    {
        Titles.Add(title);
        return _answers.Count > 0 ? (T?)_answers.Dequeue() : default;
    }

    public Task<IReadOnlyList<string>> PickFilesAsync(Control owner, string title, bool multiple, (string Name, string[] Patterns)[] types) =>
        Task.FromResult(Next<IReadOnlyList<string>>(title) ?? []);

    public Task<string?> PickFolderAsync(Control owner, string title) => Task.FromResult(Next<string>(title));

    public Task<string?> PickSaveFileAsync(Control owner, string title, string suggestedName, string extension) =>
        Task.FromResult(Next<string>(title + "|" + suggestedName));

    public Task RevealAsync(Control owner, string path)
    {
        Revealed.Add(path);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Local files and folders as storage items. The item interfaces cannot be implemented outside
/// Avalonia, so these are Avalonia's own file-system items, created by reflection.
/// </summary>
public static class Storage
{
    private static readonly Assembly Base = typeof(IStorageItem).Assembly;

    public static IStorageItem File(string path) =>
        (IStorageItem)Activator.CreateInstance(Base.GetType("Avalonia.Platform.Storage.FileIO.BclStorageFile", true)!, new FileInfo(path))!;

    public static IStorageItem Folder(string path) =>
        (IStorageItem)Activator.CreateInstance(Base.GetType("Avalonia.Platform.Storage.FileIO.BclStorageFolder", true)!, new DirectoryInfo(path))!;
}

public static class Ui
{
    /// <summary>Shows <paramref name="content"/> in a window with the real app styles.</summary>
    public static Window Show(Control content, object? dataContext, double width = 1400, double height = 900)
    {
        content.DataContext = dataContext;
        var window = new Window { Width = width, Height = height, Content = content };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>Calls a private event handler of a view (code-behind) as its control would.</summary>
    public static void Invoke(object target, string handler)
    {
        MethodInfo method = target.GetType().GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(target.GetType().Name, handler);
        object?[] args = method.GetParameters().Length switch
        {
            0 => [],
            _ => [null, new RoutedEventArgs()],
        };
        method.Invoke(target, args);
    }

    /// <summary>Runs the dispatcher (and lets background work finish) until the condition holds.</summary>
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 20_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition was not met in time");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Lets queued UI work and short background tasks run.</summary>
    public static async Task SettleAsync(int ms = 50)
    {
        for (int i = 0; i < ms / 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Drags files over <paramref name="window"/> at its centre and drops them.</summary>
    public static void Drop(Window window, Control target, params IStorageItem[] items)
    {
        var data = new DataTransfer();
        foreach (IStorageItem item in items)
        {
            data.Add(DataTransferItem.CreateFile(item));
        }

        Point centre = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window) ?? default;
        window.DragDrop(centre, RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.None);
        window.DragDrop(centre, RawDragEventType.DragOver, data, DragDropEffects.Copy, RawInputModifiers.None);
        window.DragDrop(centre, RawDragEventType.Drop, data, DragDropEffects.Copy, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    public static void Drop(Window window, Control target, params string[] paths) =>
        Drop(window, target, [.. paths.Select(p => Directory.Exists(p) ? Storage.Folder(p) : Storage.File(p))]);
}

/// <summary>Installs scripted dialogs for one test and restores the system ones after it.</summary>
public sealed class DialogScope : IDisposable
{
    private readonly IFileDialogs _previous = PageIo.Dialogs;

    public DialogScope(ScriptedDialogs dialogs)
    {
        Dialogs = dialogs;
        PageIo.Dialogs = dialogs;
    }

    public ScriptedDialogs Dialogs { get; }

    public void Dispose() => PageIo.Dialogs = _previous;
}
