using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ursa.Controls;
using VideoQuality.App.Services;
using VideoQuality.App.ViewModels;
using VideoQuality.App.Views;

namespace VideoQuality.App.Tests;

public class ServicesTests
{
    [AvaloniaFact]
    public async Task System_dialogs_answer_nothing_without_a_window_or_platform_picker()
    {
        var dialogs = new StorageDialogs();
        var loose = new Border();
        Assert.Empty(await dialogs.PickFilesAsync(loose, "t", true, []));
        Assert.Null(await dialogs.PickFolderAsync(loose, "t"));
        Assert.Null(await dialogs.PickSaveFileAsync(loose, "t", "a.mkv", "mkv"));
        await dialogs.RevealAsync(loose, Path.GetTempPath());

        // The headless platform's pickers return nothing, like a cancelled dialog.
        var shown = new Border();
        Window window = Ui.Show(shown, null);
        Assert.Empty(await dialogs.PickFilesAsync(shown, "t", true, [("Video", ["*.mkv"])]));
        Assert.Empty(await dialogs.PickFilesAsync(shown, "t", false, []));
        Assert.Null(await dialogs.PickFolderAsync(shown, "t"));
        Assert.Null(await dialogs.PickSaveFileAsync(shown, "t", "a.mkv", "mkv"));
        await dialogs.RevealAsync(shown, Path.GetTempPath());
        await dialogs.RevealAsync(shown, Path.Combine(Path.GetTempPath(), "no-such-folder", "x.mkv"));
        window.Close();
    }

    [Fact]
    public void Reveal_opens_the_folder_holding_a_file()
    {
        string file = Path.Combine(Path.GetTempPath(), $"vq-reveal-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, string.Empty);
        Assert.Equal(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StorageDialogs.FolderOf(file)!.TrimEnd(Path.DirectorySeparatorChar));
        Assert.Equal(Path.GetTempPath(), StorageDialogs.FolderOf(Path.GetTempPath()));
        Assert.Null(StorageDialogs.FolderOf(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid(), "x")));
        Assert.Null(StorageDialogs.FolderOf("x"));
        File.Delete(file);
    }

    [AvaloniaFact]
    public void Drops_highlight_the_page_and_deliver_local_paths()
    {
        string file = Path.Combine(Path.GetTempPath(), $"vq-drop-{Guid.NewGuid():N}.mkv");
        File.WriteAllText(file, string.Empty);
        var page = new Border { Background = Avalonia.Media.Brushes.Transparent };
        IReadOnlyList<string?>? dropped = null;
        PageIo.AcceptDrops(page, paths =>
        {
            dropped = paths;
            return Task.CompletedTask;
        });
        Window window = Ui.Show(page, null, 400, 300);

        Ui.Drop(window, page, file);
        Assert.Equal([file], dropped);
        Assert.DoesNotContain("dragging", page.Classes);

        // Entering highlights; leaving through a child (inside the bounds) keeps it; leaving the page ends it.
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateFile(Storage.File(file)));
        PageIo.OnDragEnter(page, data);
        Assert.Contains("dragging", page.Classes);
        PageIo.OnDragLeave(page, new Avalonia.Point(10, 10));
        Assert.Contains("dragging", page.Classes);
        foreach (Avalonia.Point outside in new Avalonia.Point[] { new(0, 10), new(10, 0), new(400, 10), new(10, 300) })
        {
            PageIo.OnDragEnter(page, data);
            PageIo.OnDragLeave(page, outside);
            Assert.DoesNotContain("dragging", page.Classes);
        }

        // Text is not a file: no highlight, no copy.
        var text = new DataTransfer();
        text.Add(DataTransferItem.CreateText("hello"));
        PageIo.OnDragEnter(page, text);
        Assert.DoesNotContain("dragging", page.Classes);
        Assert.Equal(DragDropEffects.None, PageIo.DragEffects(text));
        Assert.Equal(DragDropEffects.Copy, PageIo.DragEffects(data));
        _ = PageIo.OnDropAsync(page, text, paths =>
        {
            dropped = paths;
            return Task.CompletedTask;
        });
        Assert.Empty(dropped!);
        window.Close();
        File.Delete(file);
    }

    [AvaloniaFact]
    public async Task Toasts_and_questions_go_through_the_window()
    {
        Func<Window, string, string, Task<bool>> ask = ToastHost.Ask;
        try
        {
            var window = new MainWindow { DataContext = null };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            bool acted = false;
            foreach (ToastKind kind in Enum.GetValues<ToastKind>())
            {
                ToastHost.Show(new ToastRequest(kind, "message " + kind));
            }

            ToastHost.Show(new ToastRequest(ToastKind.Info, "with action", "Undo", () => acted = true));
            ToastHost.Show(new ToastRequest(ToastKind.Info, "label only", "Undo"));
            Dispatcher.UIThread.RunJobs();
            Button undo = window.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Undo"));
            undo.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.True(acted);
            Assert.False(undo.IsEnabled);

            ToastHost.Ask = (_, title, _) => Task.FromResult(title == "yes");
            Assert.True(await ToastHost.ConfirmAsync("yes", "m"));
            Assert.False(await ToastHost.ConfirmAsync("no", "m"));

            // The real message box: closing it without choosing counts as yes, "No" as no.
            Task<bool> closed = ToastHost.AskWithMessageBoxAsync(window, "Title", "Close me");
            await Ui.UntilAsync(() => window.OwnedWindows.Count > 0);
            window.OwnedWindows[0].Close();
            Assert.True(await closed);

            Task<bool> refused = ToastHost.AskWithMessageBoxAsync(window, "Title", "Say no");
            await Ui.UntilAsync(() => window.OwnedWindows.Count > 0);
            Window box = window.OwnedWindows[0];
            Button no = box.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_NoButton");
            no.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.False(await refused);
        }
        finally
        {
            ToastHost.Ask = ask;
        }
    }

    [Fact]
    public void Restart_runs_the_app_or_the_dotnet_host_with_its_dll()
    {
        (string file, IReadOnlyList<string> args) = AppServices.RestartCommand("/opt/vq/VideoQuality", "/opt/vq/VideoQuality.dll", ["--page=settings"]);
        Assert.Equal("/opt/vq/VideoQuality", file);
        Assert.Equal(["--page=settings"], args);
        (file, args) = AppServices.RestartCommand("/usr/bin/dotnet", "/opt/vq/VideoQuality.App.dll", ["--page=settings"]);
        Assert.Equal("/usr/bin/dotnet", file);
        Assert.Equal(["/opt/vq/VideoQuality.App.dll", "--page=settings"], args);
    }
}
