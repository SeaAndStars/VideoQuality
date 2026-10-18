using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VideoQuality.App.Controls;
using VideoQuality.App.Services;
using VideoQuality.App.ViewModels;
using VideoQuality.App.Views;
using VideoQuality.Core.Ingest;
using VideoQuality.Tests;

namespace VideoQuality.App.Tests;

public sealed class CompareViewTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("vq-app-compareview-").FullName;

    public void Dispose() => Directory.Delete(_work, true);

    private static MainViewModel Compare(ScoringTools tools) =>
        new(new AppServices { Processes = tools.Runner, Settings = new MemorySettings(), Environment = _ => null, DescribeLibav = () => "libav" })
        {
            FfmpegPath = "/bin/vmaf-" + Guid.NewGuid().ToString("N")[..8],
        };

    private string Copy(string source, string name)
    {
        string path = Path.Combine(_work, name);
        File.Copy(source, path, true);
        return path;
    }

    private static T Named<T>(Control view, string name) where T : Control => view.FindControl<T>(name)!;

    private static Point Centre(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static void Call(object target, string method, object? sender, object args) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, [sender, args]);

    private async Task<(Window Window, CompareView View, MainViewModel Vm)> LoadedAsync(ScoringTools? tools = null, int encodes = 1)
    {
        MainViewModel vm = Compare(tools ?? new ScoringTools());
        var view = new CompareView();
        Window window = Ui.Show(view, vm, 1400, 1000);
        string reference = Copy(await TestMedia.ClipAsync(), "ref.mkv");
        List<string> files = [reference, .. Enumerable.Range(0, encodes).Select(i => Copy(TestMedia.ClipAsync().GetAwaiter().GetResult(), $"enc{i}.mkv"))];
        await vm.HandleDropAsync([.. files], DropZone.Auto);
        await Ui.UntilAsync(() => vm.Encodes.All(e => e.IsDone) && vm.DistortedBitmap is not null && !vm.IsBusy);
        await Ui.SettleAsync(100);
        return (window, view, vm);
    }

    [AvaloniaFact]
    public async Task The_plot_follows_the_encodes_and_its_options()
    {
        (Window window, CompareView view, MainViewModel vm) = await LoadedAsync(encodes: 2);
        TextBlock subtitle = Named<TextBlock>(view, "GraphSubtitle");
        Assert.Contains("2", subtitle.Text, StringComparison.Ordinal);

        vm.FixedScale = false;
        vm.ShowSmoothed = true;
        vm.ShowDelta = true;
        Assert.Contains("enc0.mkv", subtitle.Text, StringComparison.Ordinal); // the delta baseline
        vm.ShowDelta = false;
        vm.FixedScale = true;
        vm.ShowSmoothed = false;
        vm.CurrentFrame = 10; // moves the marker

        // Hiding curves: the swatch buttons.
        foreach (EncodeEntry entry in vm.Encodes.ToList())
        {
            Call(view, "OnToggleEncodeVisibility", new Button { DataContext = entry }, new RoutedEventArgs());
        }

        Assert.Equal(VideoQuality.Core.Localization.Loc.T("compare.graph.allHidden"), subtitle.Text);
        Call(view, "OnToggleEncodeVisibility", new Button { DataContext = vm.Encodes[1] }, new RoutedEventArgs());
        vm.SelectedEncode = vm.Encodes[0]; // hidden but selected: no marker
        vm.CurrentFrame = 3;

        // Row buttons.
        using var scope = new DialogScope(new ScriptedDialogs());
        Call(view, "OnRevealEncode", new Button { DataContext = vm.Encodes[0] }, new RoutedEventArgs());
        Call(view, "OnRescoreEncode", new Button { DataContext = vm.Encodes[0] }, new RoutedEventArgs());
        await Ui.UntilAsync(() => vm.Encodes[0].IsDone);
        Call(view, "OnRemoveEncode", new Button { DataContext = vm.Encodes[0] }, new RoutedEventArgs());
        Assert.Single(vm.Encodes);
        Assert.Equal([Path.Combine(_work, "enc0.mkv")], scope.Dialogs.Revealed);

        vm.RemoveEncode(vm.Encodes[0]);
        Assert.Equal(string.Empty, subtitle.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Clicking_the_plot_picks_a_frame_and_dragging_does_not()
    {
        (Window window, CompareView view, MainViewModel vm) = await LoadedAsync();
        Control plot = Named<Control>(view, "PlotView");
        Point centre = Centre(window, plot);
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Assert.InRange(vm.CurrentFrame, 1, 46);

        int before = vm.CurrentFrame;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseMove(centre + new Point(60, 0));
        window.MouseUp(centre + new Point(60, 0), MouseButton.Left);
        Assert.Equal(before, vm.CurrentFrame);
        window.MouseUp(centre, MouseButton.Left); // a release without a press
        window.Close();
    }

    [AvaloniaFact]
    public async Task Wheel_zoom_pan_and_the_wipe_divider()
    {
        (Window window, CompareView view, MainViewModel vm) = await LoadedAsync();
        TextBlock zoom = Named<TextBlock>(view, "ZoomLabel");
        FrameView reference = Named<FrameView>(view, "ReferenceView");
        Point centre = Centre(window, reference);

        window.MouseWheel(centre, new Vector(0, 1));
        Assert.False(vm.Viewport.IsFit);
        window.MouseWheel(centre, new Vector(0, -1));
        Assert.DoesNotContain("(", zoom.Text, StringComparison.Ordinal);

        window.MouseDown(centre, MouseButton.Left);
        window.MouseMove(centre + new Point(30, 20));
        window.MouseUp(centre + new Point(30, 20), MouseButton.Left);
        Assert.NotEqual(0.5, vm.Viewport.CenterX);
        window.MouseMove(centre); // not panning
        window.MouseDown(centre, MouseButton.Right); // only the left button pans
        window.MouseUp(centre, MouseButton.Right);

        Call(view, "OnActualSize", null, new RoutedEventArgs());
        Assert.Equal(1, vm.Viewport.Zoom);
        Call(view, "OnFit", null, new RoutedEventArgs());
        Assert.True(vm.Viewport.IsFit);

        vm.CompareMode = CompareMode.Wipe;
        await Ui.SettleAsync(100);
        WipeView wipe = Named<WipeView>(view, "Wipe");
        Point divider = wipe.TranslatePoint(new Point(wipe.Bounds.Width * vm.WipeSplit, wipe.Bounds.Height / 2), window)!.Value;
        window.MouseDown(divider, MouseButton.Left);
        window.MouseMove(divider + new Point(-80, 0));
        window.MouseUp(divider + new Point(-80, 0), MouseButton.Left);
        Assert.True(vm.WipeSplit < 0.5);
        Point away = wipe.TranslatePoint(new Point(10, 10), window)!.Value;
        window.MouseDown(away, MouseButton.Left); // away from the divider: pans
        window.MouseMove(away + new Point(5, 5));
        window.MouseUp(away + new Point(5, 5), MouseButton.Left);
        window.MouseWheel(away, new Vector(0, 1));

        vm.CompareMode = CompareMode.Flicker;
        await Ui.SettleAsync(100);
        Assert.Contains("%", zoom.Text, StringComparison.Ordinal);

        vm.ImageWidth = 0; // nothing to zoom
        window.MouseWheel(Centre(window, Named<FrameView>(view, "FlickerView")), new Vector(0, 1));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Keyboard_shortcuts_drive_the_viewer()
    {
        (Window window, CompareView view, MainViewModel vm) = await LoadedAsync();
        using var scope = new DialogScope(new ScriptedDialogs().Answer(Array.Empty<string>(), new[] { Path.Combine(_work, "enc0.mkv") }));

        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.Equal(1, vm.CurrentFrame);
        window.KeyPress(Key.Right, RawInputModifiers.Shift, PhysicalKey.ArrowRight, null);
        Assert.Equal(11, vm.CurrentFrame);
        window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        Assert.Equal(10, vm.CurrentFrame);
        window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
        Assert.Equal(47, vm.CurrentFrame);
        window.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.Home, null);
        Assert.Equal(0, vm.CurrentFrame);
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        Assert.True(vm.IsPlaying);
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        vm.CompareMode = CompareMode.Flicker;
        window.KeyPress(Key.F, RawInputModifiers.None, PhysicalKey.F, "f");
        Assert.True(vm.FlickerShowsDistorted);
        window.KeyRelease(Key.F, RawInputModifiers.None, PhysicalKey.F, "f");
        Assert.False(vm.FlickerShowsDistorted);
        window.KeyRelease(Key.G, RawInputModifiers.None, PhysicalKey.G, "g"); // other keys up: nothing
        window.KeyPress(Key.G, RawInputModifiers.None, PhysicalKey.G, "g"); // not a shortcut

        window.KeyPress(Key.O, RawInputModifiers.Control, PhysicalKey.O, null); // reference picker (cancelled)
        window.KeyPress(Key.O, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.O, null); // encodes picker: a duplicate
        window.KeyPress(Key.R, RawInputModifiers.Control, PhysicalKey.R, null); // rescore
        window.KeyPress(Key.X, RawInputModifiers.Control, PhysicalKey.X, null); // not a shortcut
        await Ui.UntilAsync(() => vm.Encodes[0].IsDone);
        Assert.Equal(2, scope.Dialogs.Titles.Count);

        // Paste: text paths (quoted, file:// and remote) and then files.
        await window.Clipboard!.SetTextAsync($"\"{Path.Combine(_work, "enc0.mkv")}\"\nfile://{Path.Combine(_work, "ref.mkv")}\nsmb://server/share/x.mkv\n");
        window.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, null);
        await Ui.SettleAsync(200);
        await window.Clipboard.SetTextAsync("   ");
        window.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, null);
        var files = new DataTransfer();
        files.Add(DataTransferItem.CreateFile(Storage.File(Path.Combine(_work, "enc0.mkv"))));
        await window.Clipboard.SetDataAsync(files);
        window.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, null);
        await Ui.SettleAsync(200);

        // Typing in a text box, a hidden page, or no model: shortcuts stay out of the way.
        var box = new TextBox();
        ((Panel)Named<Control>(view, "PageRoot")).Children.Add(box);
        box.Focus();
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.Equal(0, vm.CurrentFrame);
        ((Panel)Named<Control>(view, "PageRoot")).Children.Remove(box);
        view.IsVisible = false;
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.Equal(0, vm.CurrentFrame);
        view.IsVisible = true;
        view.DataContext = null;
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        window.KeyRelease(Key.F, RawInputModifiers.None, PhysicalKey.F, "f");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Pickers_and_drops_onto_the_zones()
    {
        MainViewModel vm = Compare(new ScoringTools());
        var view = new CompareView();
        Window window = Ui.Show(view, vm, 1400, 1000);
        string reference = Copy(await TestMedia.ClipAsync(), "ref.mkv");
        string encode = Copy(await TestMedia.ClipAsync(), "enc.mkv");
        using var scope = new DialogScope(new ScriptedDialogs().Answer(new[] { reference }, new[] { encode }));

        Call(view, "OnPickReference", null, new RoutedEventArgs());
        await Ui.UntilAsync(() => vm.HasReference);
        Call(view, "OnPickEncodes", null, new RoutedEventArgs());
        await Ui.UntilAsync(() => vm.Encodes.Count == 1 && vm.Encodes[0].IsDone);
        Call(view, "OnStepForward", null, new RoutedEventArgs());
        Assert.Equal(1, vm.CurrentFrame);
        Call(view, "OnStepBack", null, new RoutedEventArgs());
        Assert.Equal(0, vm.CurrentFrame);

        // Drops on the empty screen's zones.
        var fresh = Compare(new ScoringTools());
        var empty = new CompareView();
        Window second = Ui.Show(empty, fresh, 1400, 1000);
        Border referenceZone = empty.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("zone") && Equals(b.Tag, DropZone.Reference));
        Border encodesZone = empty.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("zone") && Equals(b.Tag, DropZone.Encodes));
        Ui.Drop(second, referenceZone, reference);
        await Ui.UntilAsync(() => fresh.HasReference);
        Ui.Drop(second, encodesZone, encode);
        await Ui.UntilAsync(() => fresh.Encodes.Count == 1);

        // Dragging text is not a drop; leaving the window ends the overlay.
        var text = new DataTransfer();
        text.Add(DataTransferItem.CreateText("x"));
        Point inside = Centre(second, empty);
        second.DragDrop(inside, Avalonia.Input.Raw.RawDragEventType.DragEnter, text, DragDropEffects.Copy, RawInputModifiers.None);
        Assert.False(fresh.IsDragging);
        second.DragDrop(inside, Avalonia.Input.Raw.RawDragEventType.DragOver, text, DragDropEffects.Copy, RawInputModifiers.None);
        second.DragDrop(inside, Avalonia.Input.Raw.RawDragEventType.Drop, text, DragDropEffects.Copy, RawInputModifiers.None);
        var files = new DataTransfer();
        files.Add(DataTransferItem.CreateFile(Storage.File(encode)));
        second.DragDrop(inside, Avalonia.Input.Raw.RawDragEventType.DragEnter, files, DragDropEffects.Copy, RawInputModifiers.None);
        Assert.True(fresh.IsDragging);
        second.DragDrop(inside, Avalonia.Input.Raw.RawDragEventType.DragLeave, files, DragDropEffects.Copy, RawInputModifiers.None);
        Assert.True(fresh.IsDragging); // still inside
        empty.RaiseEvent(new DragEventArgs(DragDrop.DragLeaveEvent, files, empty, new Point(-5, -5), KeyModifiers.None));
        Assert.False(fresh.IsDragging);
        second.Close();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Layout_follows_the_splitter_and_the_window()
    {
        (Window window, CompareView view, MainViewModel vm) = await LoadedAsync();
        GridSplitter splitter = Named<GridSplitter>(view, "ViewerSplitter");
        Point centre = Centre(window, splitter);
        window.MouseDown(centre, MouseButton.Left);
        window.MouseMove(centre + new Point(0, 60));
        window.MouseUp(centre + new Point(0, 60), MouseButton.Left);
        await Ui.SettleAsync(100);
        centre = Centre(window, splitter);
        for (int click = 0; click < 2; click++) // a double click resets the split
        {
            window.MouseDown(centre, MouseButton.Left);
            window.MouseUp(centre, MouseButton.Left);
        }

        window.Width = 800;
        window.Height = 600;
        await Ui.SettleAsync(100);
        window.Width = 2;
        await Ui.SettleAsync(100);
        window.Width = 1400;
        vm.CompareMode = CompareMode.Wipe;
        vm.ImageWidth = 0;
        await Ui.SettleAsync(100);

        // Shown without a model, then given one and another.
        var bare = new CompareView();
        Window other = Ui.Show(bare, null);
        bare.DataContext = vm;
        bare.DataContext = Compare(new ScoringTools());
        bare.DataContext = null;
        other.Close();
        window.Close();
    }
}
