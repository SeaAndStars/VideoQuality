using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using VideoQuality.App.Services;
using VideoQuality.App.ViewModels;
using VideoQuality.App.Views;
using VideoQuality.Core.Decoding;
using VideoQuality.Core.Ingest;
using VideoQuality.Core.Scoring;
using VideoQuality.Core.Tools;
using VideoQuality.Tests;

namespace VideoQuality.App.Tests;

/// <summary>The less common paths of the Compare page.</summary>
public sealed class CompareEdgeTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("vq-app-edges-").FullName;

    public void Dispose() => Directory.Delete(_work, true);

    private static MainViewModel Compare(ScoringTools tools, MemorySettings? settings = null, bool probe = true)
    {
        var vm = new MainViewModel(new AppServices { Processes = tools.Runner, Settings = settings ?? new MemorySettings(), Environment = _ => null, DescribeLibav = () => "libav" });
        if (probe)
        {
            vm.FfmpegPath = "/bin/vmaf-" + Guid.NewGuid().ToString("N")[..8];
        }

        return vm;
    }

    private string Copy(string source, string name)
    {
        string path = Path.Combine(_work, name);
        File.Copy(source, path, true);
        return path;
    }

    /// <summary>A clip made for one test with the encoder ffmpeg.</summary>
    private async Task<string> MakeAsync(string name, params string[] arguments)
    {
        string path = Path.Combine(_work, name);
        ProcessResult result = await SystemProcessRunner.Instance.RunAsync(new ProcessRequest
        {
            FileName = TestMedia.RequireEncoder(),
            Arguments = ["-hide_banner", "-loglevel", "error", "-y", .. arguments, path],
        });
        Assert.Equal(0, result.ExitCode);
        return path;
    }

    private static Task Settled(MainViewModel vm, int count = 1) =>
        Ui.UntilAsync(() => vm.Encodes.Count(e => e.IsDone || e.IsFailed) >= count && !vm.IsBusy && vm.Encodes.All(e => !e.IsBusy));

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    [AvaloniaFact]
    public void Saved_choices_come_back_and_startup_modes_are_not_saved()
    {
        var settings = new MemorySettings("""{"Model":"vmaf_v0.6.1","CompareMode":2,"FfmpegPath":"/no/such/ffmpeg"}""");
        MainViewModel vm = Compare(new ScoringTools(), settings, probe: false);
        Assert.Equal(VmafModels.Standard, vm.SelectedModel);
        Assert.Equal(CompareMode.Flicker, vm.CompareMode);
        Assert.Equal("ffmpeg", vm.FfmpegPath); // a saved path that no longer exists is ignored
        Assert.False(vm.IsAutoFlicker);

        vm.FlushSettings(); // nothing changed: nothing written
        Assert.Equal(0, settings.Saves);
        vm.ApplyStartupMode(CompareMode.Difference);
        Assert.Equal(CompareMode.Difference, vm.CompareMode);
        vm.FlushSettings();
        Assert.Equal(0, settings.Saves);
        vm.StopPlayback();
        vm.Dispose();
        Assert.Equal("a", MainViewModel.FirstLine("a\nb"));
        Assert.Equal("a", MainViewModel.FirstLine("a"));
    }

    [AvaloniaFact]
    public async Task Colours_run_out_and_raw_streams_get_a_bitrate_from_their_size()
    {
        var tools = new ScoringTools();
        MainViewModel vm = Compare(tools);
        string raw = Copy(await TestMedia.RawH264Async(), "raw.h264");
        await vm.HandleDropAsync([raw], DropZone.Encodes); // no reference: no duration to divide by
        await Ui.UntilAsync(() => vm.Encodes[0].State == EncodeState.WaitingForReference);
        Assert.Equal(0, vm.Encodes[0].BitrateKbps);

        string[] more = [.. Enumerable.Range(0, 9).Select(i => Copy(raw, $"raw{i}.h264"))];
        await vm.HandleDropAsync(more, DropZone.Encodes);
        Assert.Equal(10, vm.Encodes.Count);
        Assert.Equal(vm.Encodes[0].ColorHex, vm.Encodes[^2].ColorHex); // the palette wraps around

        // A raw reference has no duration either.
        await vm.HandleDropAsync([Copy(raw, "ref.h264")], DropZone.Reference);
        await Settled(vm, 10);
        string mkv = Copy(await TestMedia.RawH264Async(), "late.h264");
        await vm.HandleDropAsync([mkv], DropZone.Encodes);
        await Settled(vm, 11);
        Assert.Equal(0, vm.Encodes[^1].BitrateKbps);
    }

    [AvaloniaFact]
    public async Task Undo_restores_waiting_rows_and_survives_a_broken_reference()
    {
        var tools = new ScoringTools();
        MainViewModel vm = Compare(tools);
        var toasts = new List<ToastRequest>();
        vm.ToastRequested += (_, t) => toasts.Add(t);
        string waiting = Copy(await TestMedia.ClipAsync(), "waiting.mkv");
        await vm.HandleDropAsync([waiting], DropZone.Encodes);
        await Ui.UntilAsync(() => vm.Encodes[0].State == EncodeState.WaitingForReference);

        string reference = Copy(await TestMedia.ClipAsync(), "ref.mkv");
        string extra = Copy(await TestMedia.ClipAsync(), "extra.mkv");
        await vm.HandleDropAsync([reference, extra], DropZone.Reference);
        string junk = Path.Combine(_work, "junk.mkv");
        File.WriteAllText(junk, "x");
        await vm.HandleDropAsync([junk], DropZone.Reference); // the reference breaks before Undo
        toasts.Last(t => t.Action is not null).Action!();
        await Ui.UntilAsync(() => !vm.HasReference);
        Assert.Equal(EncodeState.WaitingForReference, Assert.Single(vm.Encodes).State);
    }

    [AvaloniaFact]
    public async Task Reference_counts_that_finish_late_or_fail_are_ignored()
    {
        MainViewModel vm = Compare(new ScoringTools());
        string first = Copy(await TestMedia.LongAsync(), "first.mkv");
        await vm.SetReferenceAsync(first);
        vm.ReferencePath = Path.Combine(_work, "replaced.mkv"); // the count arrives for a reference no longer shown
        await Ui.SettleAsync(500);

        string doomed = Copy(await TestMedia.LongAsync(), "doomed.mkv");
        await vm.SetReferenceAsync(doomed);
        File.Delete(doomed); // the packet walk cannot open it
        await Ui.SettleAsync(500);
    }

    [AvaloniaFact]
    public async Task Scoring_lanes_cpu_gate_superseded_runs_and_two_at_once()
    {
        var tools = new ScoringTools { Gpu = true };
        MainViewModel vm = Compare(tools);
        await vm.InitializeAsync();
        await Ui.UntilAsync(() => vm.BackendChip == "CUDA");
        string reference = Copy(await TestMedia.ClipAsync(), "ref.mkv");
        string a = Copy(await TestMedia.ClipAsync(), "a.mkv");
        string b = Copy(await TestMedia.ClipAsync(), "b.mkv");
        tools.Gate = new TaskCompletionSource();
        await vm.HandleDropAsync([reference, a, b], DropZone.Auto);
        await Ui.UntilAsync(() => vm.Encodes.All(e => e.State == EncodeState.Scoring), message: () => string.Join(",", vm.Encodes.Select(e => e.State)));
        Assert.Contains("2", vm.ProgressDetail, StringComparison.Ordinal); // two CUDA scores at once

        Task again = vm.RescoreAsync(vm.Encodes[0]); // supersedes the running score
        tools.Gate.SetResult();
        await again;
        await Settled(vm, 2);

        // CPU scores take the gate; a model that was never probed counts as not CPU-bound.
        vm.SelectedBackend = VmafBackend.Cpu;
        await vm.RescoreAsync(vm.Encodes[0]);
        MainViewModel unprobed = Compare(tools, probe: false);
        unprobed.FfmpegPath = "ffmpeg"; // unchanged: no probe
        await unprobed.HandleDropAsync([reference, a], DropZone.Auto);
        await Settled(unprobed);
    }

    [AvaloniaFact]
    public async Task Summaries_without_a_selection_and_with_a_failed_selection()
    {
        var tools = new ScoringTools();
        MainViewModel vm = Compare(tools);
        string reference = Copy(await TestMedia.ClipAsync(), "ref.mkv");
        string good = Copy(await TestMedia.ClipAsync(), "good.mkv");
        await vm.HandleDropAsync([reference, good], DropZone.Auto);
        await Settled(vm);
        tools.Exit = 1;
        string bad = Copy(await TestMedia.ClipAsync(), "bad.mkv");
        await vm.HandleDropAsync([bad], DropZone.Encodes);
        await Settled(vm, 2);
        vm.SelectedEncode = vm.Encodes[1]; // the failed one
        vm.RemoveEncode(new EncodeEntry(Path.Combine(_work, "x.mkv"), "#000000"));
        tools.Exit = 0;
        string third = Copy(await TestMedia.ClipAsync(), "third.mkv");
        await vm.HandleDropAsync([third], DropZone.Encodes);
        await Settled(vm, 3);
        vm.SelectedEncode = null;
        string fourth = Copy(await TestMedia.ClipAsync(), "fourth.mkv");
        await vm.HandleDropAsync([fourth], DropZone.Encodes);
        await Settled(vm, 4);
        Assert.Contains("ref.mkv", vm.Status, StringComparison.Ordinal);

        // A shorter report while far into a longer one: back to the first frame.
        vm.SelectedEncode = vm.Encodes[0];
        vm.GoToFrame(40);
        tools.Frames = 10;
        await vm.RescoreAsync(vm.Encodes[0]);
        await Settled(vm, 4);
        Assert.Equal(0, vm.CurrentFrame);
    }

    [AvaloniaFact]
    public async Task Difference_of_other_sizes_auto_gain_and_decode_failures()
    {
        var tools = new ScoringTools();
        MainViewModel vm = Compare(tools);
        string reference = Copy(await TestMedia.ClipAsync(), "ref.mkv");
        await vm.HandleDropAsync([reference, Copy(reference, "same.mkv")], DropZone.Auto);
        await Settled(vm);
        await Ui.UntilAsync(() => vm.DistortedBitmap is not null);
        vm.CompareMode = CompareMode.Difference;
        vm.AutoGainCommand.Execute(null); // identical frames: the strongest gain
        Assert.Equal(16, vm.DifferenceGain);

        string small = await MakeAsync("small.mkv", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=24000/1001:duration=2", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p");
        await vm.HandleDropAsync([small], DropZone.Encodes);
        await Settled(vm, 2);
        vm.SelectedEncode = vm.Encodes[^1];
        await Ui.UntilAsync(() => vm.EncodeName == "small.mkv");
        await Ui.SettleAsync(200);
        Assert.Null(vm.DifferenceBitmap); // sizes differ: no difference image

        // A decoder that broke: the frame cannot be shown.
        Field<FrameDecoder>(vm, "_distortedDecoder").Dispose();
        vm.StepFrame(1);
        await Ui.SettleAsync(200);
        Assert.Contains(VideoQuality.Core.Localization.Loc.T("compare.status.decodeFailed", string.Empty)[..6], vm.Status, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Colour_range_differences_are_reported()
    {
        MainViewModel vm = Compare(new ScoringTools());
        var statuses = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Status))
            {
                statuses.Add(vm.Status);
            }
        };
        string limited = Copy(await TestMedia.Mp4Async(), "limited.mp4");
        string full = await MakeAsync("full.mp4", "-i", limited, "-c", "copy", "-bsf:v", "h264_metadata=video_full_range_flag=1");
        await vm.HandleDropAsync([limited, full], DropZone.Auto);
        await Ui.UntilAsync(() => vm.EncodeName == "full.mp4");
        await Ui.SettleAsync(100);
        using (FrameDecoder a = FrameDecoder.Open(limited))
        using (FrameDecoder b = FrameDecoder.Open(full))
        {
            Assert.Equal(a.Info.ColorSpace, b.Info.ColorSpace);
            Assert.NotEqual(a.Info.ColorRange, b.Info.ColorRange);
        }

        Assert.Contains(statuses, s => s.Contains("pc", StringComparison.Ordinal));
    }

    [Fact]
    public void Encoder_lists_sync_in_catalog_order()
    {
        var target = new System.Collections.ObjectModel.ObservableCollection<int> { 1 };
        PresetEditor.Sync(target, [1, 2, 3], 1, [1, 2, 3]);
        Assert.Equal([1, 2, 3], target);
        PresetEditor.Sync(target, [3], 2, [1, 2, 3]);
        Assert.Equal([2, 3], target);
    }

    [AvaloniaFact]
    public async Task View_paths_for_short_reports_hidden_selections_and_drag_zones()
    {
        var tools = new ScoringTools { Frames = 3 };
        MainViewModel vm = Compare(tools);
        var view = new CompareView();
        Window window = Ui.Show(view, vm, 1400, 1000);

        // Drag over and drop on a zone's label (the zones are hit-tested through their text).
        string reference = Copy(await TestMedia.ClipAsync(), "ref.mkv");
        Border zone = view.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("zone") && Equals(b.Tag, DropZone.Reference) && b.IsEffectivelyVisible);
        TextBlock label = zone.GetVisualDescendants().OfType<TextBlock>().First();
        Ui.Drop(window, label, reference);
        await Ui.UntilAsync(() => vm.HasReference);

        string a = Copy(await TestMedia.ClipAsync(), "a.mkv");
        await vm.HandleDropAsync([a], DropZone.Encodes);
        await Settled(vm);
        vm.ShowSmoothed = true; // three frames: too few to smooth
        vm.ImageHeight = 200;

        // Mixed models, and a selected row without a report next to a plotted one.
        vm.SelectedModel = VmafModels.Standard;
        tools.Frames = 48;
        await vm.HandleDropAsync([Copy(a, "b.mkv")], DropZone.Encodes);
        await Settled(vm, 2);
        Assert.True(vm.HasMixedModels);
        tools.Exit = 1;
        await vm.HandleDropAsync([Copy(a, "c.mkv")], DropZone.Encodes);
        await Settled(vm, 3);
        vm.SelectedEncode = vm.Encodes[^1];

        // The splitter: a drag, a pixel-sized graph row that gets its star back, a tiny window.
        GridSplitter splitter = view.FindControl<GridSplitter>("ViewerSplitter")!;
        Grid layout = view.FindControl<Grid>("Layout")!;
        splitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragStartedEvent });
        layout.RowDefinitions[4].Height = new GridLength(200);
        splitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent });
        Assert.True(layout.RowDefinitions[4].Height.IsStar);
        window.Height = 120;
        await Ui.SettleAsync(100);
        splitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragStartedEvent });
        splitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent });

        // Drag-over without files, and a file drop away from the zones.
        var text = new DataTransfer();
        text.Add(DataTransferItem.CreateText("x"));
        view.RaiseEvent(new DragEventArgs(DragDrop.DragOverEvent, text, view, new Avalonia.Point(5, 5), KeyModifiers.None));
        window.Height = 1000;
        await Ui.SettleAsync(100);
        var files = new DataTransfer();
        files.Add(DataTransferItem.CreateFile(Storage.File(Copy(a, "d.mkv"))));
        view.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, files, view, new Avalonia.Point(5, 5), KeyModifiers.None));
        await Ui.UntilAsync(() => vm.Encodes.Count == 4);
        window.Close();
    }
}
