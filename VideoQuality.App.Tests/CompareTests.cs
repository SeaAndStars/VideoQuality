using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VideoQuality.App.Services;
using VideoQuality.App.ViewModels;
using VideoQuality.Core.Diagnostics;
using VideoQuality.Core.Imaging;
using VideoQuality.Core.Ingest;
using VideoQuality.Core.Localization;
using VideoQuality.Core.Scoring;
using VideoQuality.Core.Tools;
using VideoQuality.Tests;

namespace VideoQuality.App.Tests;

/// <summary>A scripted ffmpeg for scoring: VMAF filters, an optional GPU, and logs with per-frame scores.</summary>
public sealed class ScoringTools
{
    public bool Gpu { get; set; }

    public bool CudaFails { get; set; }

    public int Exit { get; set; }

    /// <summary>Blocks every score until released (for cancelling mid-run).</summary>
    public TaskCompletionSource? Gate { get; set; }

    public int Frames { get; set; } = 48;

    public double Score { get; set; } = 90;

    public string? Model { get; set; }

    public ScriptedRunner Runner => new(async (request, token) =>
    {
        if (request.FileName.Contains("nvidia-smi", StringComparison.Ordinal))
        {
            return ScriptedRunner.Ok(Gpu ? "GPU 0: NVIDIA Test (UUID: x)\n" : "No devices were found\n");
        }

        if (request.Arguments.Contains("-filters"))
        {
            return ScriptedRunner.Ok(" ... libvmaf          VV->V      x\n ... libvmaf_cuda     VV->V      x\n");
        }

        if (request.Arguments.Contains("-version"))
        {
            return ScriptedRunner.Ok("ffmpeg version 9.0 scoring\n");
        }

        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(token);
        }

        bool cuda = request.Arguments.Any(a => a.Contains("libvmaf_cuda", StringComparison.Ordinal));
        if (Exit != 0 || (cuda && CudaFails))
        {
            return new ProcessResult(Exit != 0 ? Exit : 1, string.Empty, "Error: scoring broke\nsecond line");
        }

        ScriptedRunner.Emit(request, "frame=0", "progress=continue", "frame=24", "progress=continue", "progress=end");
        var log = new StringBuilder("{\"frames\":[");
        for (int i = 0; i < Frames; i++)
        {
            log.Append(i > 0 ? "," : string.Empty).Append($"{{\"frameNum\":{i},\"metrics\":{{\"vmaf\":{Score + (i % 5)}}}}}");
        }

        File.WriteAllText(ScriptedRunner.LogPath(request), log.Append("]}").ToString());
        return ScriptedRunner.Ok();
    });
}

public sealed class CompareTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("vq-app-compare-").FullName;

    public void Dispose() => Directory.Delete(_work, true);

    private static MainViewModel Compare(ScoringTools tools, Func<string, string?>? environment = null, Func<string>? libav = null)
    {
        var services = new AppServices
        {
            Processes = tools.Runner,
            Settings = new MemorySettings(),
            Environment = environment ?? (_ => null),
            DescribeLibav = libav ?? (() => "libav test"),
        };
        return new MainViewModel(services) { FfmpegPath = "/bin/vmaf-" + Guid.NewGuid().ToString("N")[..8] };
    }

    private string Copy(string source, string name)
    {
        string path = Path.Combine(_work, name);
        File.Copy(source, path, true);
        return path;
    }

    private static Task Scored(MainViewModel vm, int count = 1) =>
        Ui.UntilAsync(() => vm.Encodes.Count(e => e.IsDone || e.IsFailed) >= count && !vm.IsBusy);

    [AvaloniaFact]
    public async Task A_reference_and_encodes_are_scored_and_shown()
    {
        var tools = new ScoringTools();
        MainViewModel vm = Compare(tools);
        var toasts = new List<ToastRequest>();
        vm.ToastRequested += (_, t) => toasts.Add(t);
        Assert.True(vm.IsEmpty);
        Assert.Equal(string.Empty, vm.ComparisonTitle);
        Assert.Equal("VideoQuality", vm.WindowTitle);
        Assert.Equal(string.Empty, vm.StatsText);
        Assert.Contains(Loc.T("compare.frameText.empty", "VMAF NEG")[..5], vm.FrameText, StringComparison.Ordinal);

        string reference = Copy(await TestMedia.ClipAsync(), "reference.mkv");
        string encode = Copy(await TestMedia.ClipAsync(), "encode.mkv");
        await vm.HandleDropAsync([reference, encode], DropZone.Auto);
        await Scored(vm);

        EncodeEntry entry = Assert.Single(vm.Encodes);
        Assert.True(entry.IsDone, entry.StatusText);
        Assert.Same(entry, vm.SelectedEncode);
        Assert.Equal(48, vm.FrameCount);
        Assert.True(vm.HasFrames && vm.CanPlay && vm.HasReference && vm.HasEncodes);
        Assert.Equal(47, vm.MaxFrame);
        Assert.Contains("reference.mkv", vm.ComparisonTitle, StringComparison.Ordinal);
        Assert.Equal("reference.mkv — VideoQuality", vm.WindowTitle);
        Assert.NotEmpty(vm.StatsText);
        Assert.Equal(90, vm.CurrentScore);
        await Ui.UntilAsync(() => vm.ReferenceBitmap is not null && vm.DistortedBitmap is not null); // frames decode in the background
        Assert.True(entry.BitrateKbps > 0);
        Assert.Equal("CPU", vm.BackendChip); // no GPU
        Assert.Equal("NEG", vm.ModelChip);

        vm.StepFrame(5);
        Assert.Equal(5, vm.CurrentFrame);
        Assert.Equal(5, vm.CurrentFrameValue);
        vm.CurrentFrameValue = 7.4;
        Assert.Equal(7, vm.CurrentFrame);
        vm.CurrentFrameValue = 7;
        vm.GoToFrame(int.MaxValue);
        Assert.Equal(47, vm.CurrentFrame);
        vm.StepFrame(-100);
        Assert.Equal(0, vm.CurrentFrame);
        Assert.Contains("1", vm.FrameText, StringComparison.Ordinal);
        await Ui.SettleAsync();

        // A second encode of the same file: a duplicate; two more encodes.
        await vm.HandleDropAsync([encode], DropZone.Encodes);
        Assert.Equal(ToastKind.Info, toasts[^1].Kind);
        string second = Copy(await TestMedia.ClipAsync(), "second.mkv");
        string third = Copy(await TestMedia.ClipAsync(), "third.mkv");
        await vm.HandleDropAsync([encode, second, third, encode], DropZone.Encodes);
        await Scored(vm, 3);
        Assert.Equal(3, vm.Encodes.Count);
        Assert.Contains("3", vm.ComparisonTitle, StringComparison.Ordinal);

        vm.RemoveEncode(vm.Encodes[0]); // the selected one: the next is selected
        Assert.Same(vm.Encodes[0], vm.SelectedEncode);
        vm.RemoveEncode(new EncodeEntry("/elsewhere.mkv", "#000000")); // not in the table
        vm.RemoveEncode(vm.Encodes[^1]);
        vm.RemoveEncode(vm.Encodes[0]);
        Assert.Null(vm.SelectedEncode);
        Assert.Equal(0, vm.FrameCount);
        vm.StepFrame(1);
        vm.GoToFrame(3);
        Assert.Equal(0, vm.CurrentFrame);
        vm.Dispose();
    }

    [AvaloniaFact]
    public async Task Rejected_files_duplicates_and_encodes_waiting_for_a_reference()
    {
        var tools = new ScoringTools();
        MainViewModel vm = Compare(tools);
        var toasts = new List<ToastRequest>();
        vm.ToastRequested += (_, t) => toasts.Add(t);
        await vm.HandleDropAsync([], DropZone.Auto);
        Assert.Empty(toasts);

        string[] junk = [.. Enumerable.Range(0, 6).Select(i => Path.Combine(_work, $"notes{i}.txt"))];
        foreach (string file in junk)
        {
            File.WriteAllText(file, "x");
        }

        await vm.HandleDropAsync([.. junk, null], DropZone.Auto);
        Assert.Contains(Loc.T("compare.drop.more", 3), toasts[^1].Message, StringComparison.Ordinal);
        Assert.Contains(Loc.T("compare.drop.item"), toasts[^1].Message, StringComparison.Ordinal);

        string a = Copy(await TestMedia.ClipAsync(), "a.mkv");
        string b = Copy(await TestMedia.ClipAsync(), "b.mkv");
        await vm.HandleDropAsync([a, b], DropZone.Encodes);
        Assert.Equal(Loc.T("compare.status.encodesQueued"), vm.Status);
        await Ui.UntilAsync(() => vm.Encodes.All(e => e.State == EncodeState.WaitingForReference));
        await vm.HandleDropAsync([a, b], DropZone.Encodes);
        Assert.Equal(Loc.T("compare.drop.duplicates", 2), toasts[^1].Message);

        await vm.RescoreAsync(vm.Encodes[0]);
        Assert.Equal(Loc.T("compare.status.needReference"), vm.Status);

        // The reference arrives: the waiting rows score.
        string reference = Copy(await TestMedia.ClipAsync(), "ref.mkv");
        await vm.HandleDropAsync([reference], DropZone.Reference);
        await Scored(vm, 2);
        Assert.All(vm.Encodes, e => Assert.True(e.IsDone, e.StatusText));
        Assert.Contains(Loc.T("compare.status.scored", 2, "ref.mkv")[..6], vm.Status, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_new_reference_asks_before_rescoring_and_can_be_undone()
    {
        var tools = new ScoringTools();
        MainViewModel vm = Compare(tools);
        string reference = Copy(await TestMedia.ClipAsync(), "ref.mkv");
        string encode = Copy(await TestMedia.ClipAsync(), "enc.mkv");
        await vm.HandleDropAsync([reference, encode], DropZone.Auto);
        await Scored(vm);

        // Declined: rows go stale.
        string other = Copy(await TestMedia.ClipAsync(), "other.mkv");
        vm.ConfirmAsync = (_, _) => Task.FromResult(false);
        await vm.HandleDropAsync([other], DropZone.Reference);
        Assert.True(vm.HasStaleEncodes);
        Assert.True(vm.Encodes[0].IsStale);
        vm.RescoreStaleCommand.Execute(null);
        await Scored(vm);
        Assert.False(vm.HasStaleEncodes);

        // Accepted: rows rescore against the new reference.
        vm.ConfirmAsync = (_, _) => Task.FromResult(true);
        await vm.HandleDropAsync([reference], DropZone.Reference);
        await Scored(vm);
        Assert.Equal(reference, vm.ReferencePath);

        // An encode dropped as the reference leaves the table.
        await vm.HandleDropAsync([encode], DropZone.Reference);
        Assert.Empty(vm.Encodes);
        Assert.Equal(encode, vm.ReferencePath);

        // Several files on the reference zone: the first is the reference, the rest encodes; Undo.
        var toasts = new List<ToastRequest>();
        vm.ToastRequested += (_, t) => toasts.Add(t);
        await vm.HandleDropAsync([reference, other], DropZone.Reference);
        ToastRequest overflow = toasts.Last(t => t.Action is not null);
        bool undone = false;
        vm.PropertyChanged += (_, e) => undone |= e.PropertyName == nameof(MainViewModel.Status) && vm.Status == Loc.T("compare.status.dropUndone");
        overflow.Action!();
        await Ui.UntilAsync(() => undone); // the previous reference is open again
        Assert.Equal(encode, vm.ReferencePath);
        Assert.Empty(vm.Encodes);

        // Undo back to no reference at all.
        var fresh = Compare(tools);
        fresh.ToastRequested += (_, t) => toasts.Add(t);
        await fresh.HandleDropAsync([reference, other], DropZone.Reference);
        toasts.Last(t => t.Action is not null).Action!();
        await Ui.UntilAsync(() => !fresh.HasReference);
        Assert.True(fresh.IsEmpty);
    }

    [AvaloniaFact]
    public async Task Unreadable_files_failed_and_cancelled_scores()
    {
        var tools = new ScoringTools();
        MainViewModel vm = Compare(tools);
        var toasts = new List<ToastRequest>();
        vm.ToastRequested += (_, t) => toasts.Add(t);

        string junk = Path.Combine(_work, "junk.mkv");
        File.WriteAllText(junk, "not a video");
        await vm.HandleDropAsync([junk], DropZone.Reference);
        Assert.Contains(Loc.T("compare.status.referenceFailed", string.Empty)[..8], vm.Status, StringComparison.Ordinal);
        Assert.Equal(Loc.T("compare.noReference"), vm.ReferenceInfo);

        string reference = Copy(await TestMedia.ClipAsync(), "ref.mkv");
        await vm.HandleDropAsync([reference], DropZone.Reference);
        await vm.HandleDropAsync([junk], DropZone.Encodes);
        await Ui.UntilAsync(() => vm.Encodes[0].IsFailed);
        Assert.Contains(Loc.T("compare.status.encodeFailed", string.Empty)[..5], vm.Status, StringComparison.Ordinal);
        vm.RemoveEncode(vm.Encodes[0]);

        tools.Exit = 2;
        string encode = Copy(await TestMedia.ClipAsync(), "enc.mkv");
        await vm.HandleDropAsync([encode], DropZone.Encodes);
        await Scored(vm);
        Assert.True(vm.Encodes[0].IsFailed);
        Assert.Contains("2", vm.Encodes[0].Error, StringComparison.Ordinal); // the first line only
        Assert.Equal(ToastKind.Error, toasts[^1].Kind);
        Assert.Contains(Loc.T("compare.status.failedCount", 1)[..3], vm.Status, StringComparison.Ordinal);

        // Rescore from the toolbar, then cancel it.
        tools.Exit = 0;
        tools.Gate = new TaskCompletionSource();
        Assert.True(vm.AnalyzeCommand.CanExecute(null));
        Task rescore = vm.AnalyzeCommand.ExecuteAsync(null);
        await Ui.UntilAsync(() => vm.Encodes[0].State == EncodeState.Scoring && vm.IsBusy, message: () => vm.Encodes[0].StatusText + " busy=" + vm.IsBusy);
        Assert.False(vm.AnalyzeCommand.CanExecute(null));
        Assert.Contains("enc.mkv", vm.ProgressDetail, StringComparison.Ordinal);
        vm.CancelAnalysisCommand.Execute(null);
        await rescore;
        Assert.Equal(EncodeState.Cancelled, vm.Encodes[0].State);

        // Cancelled while indexing.
        tools.Gate = null;
        vm.SelectedEncode = null;
        await vm.AnalyzeCommand.ExecuteAsync(null); // nothing selected: nothing to do
        string late = Copy(await TestMedia.LongAsync(), "late.mkv");
        await vm.HandleDropAsync([late], DropZone.Encodes);
        vm.CancelAnalysisCommand.Execute(null);
        await Ui.UntilAsync(() => vm.Encodes[^1].IsFailed || vm.Encodes[^1].IsDone, message: () => vm.Encodes[^1].StatusText);
    }

    [AvaloniaFact]
    public async Task Cuda_scores_note_fallbacks_and_the_8_bit_conversion()
    {
        var tools = new ScoringTools { Gpu = true, CudaFails = true };
        MainViewModel vm = Compare(tools);
        vm.SelectedBackend = VmafBackend.Auto;
        await vm.InitializeAsync();
        await Ui.UntilAsync(() => vm.BackendChip == "CUDA");

        string reference = Copy(await TestMedia.Hevc10Async(), "ref.mkv");
        string encode = Copy(await TestMedia.Hevc10Async(), "enc.mkv");
        await vm.HandleDropAsync([reference, encode], DropZone.Auto);
        await Scored(vm);
        Assert.Contains(Loc.T("compare.note.cudaFallback", "Error: scoring broke")[..4], vm.Status, StringComparison.Ordinal);
        Assert.Equal("10-bit", vm.EncodeBadge);
        Assert.True(vm.HasEncodeBadge && vm.HasReferenceBadge);

        tools.CudaFails = false;
        vm.SelectedBackend = VmafBackend.Cuda;
        Assert.Equal("CUDA", vm.BackendChip);
        await vm.RescoreAsync(vm.Encodes[0]);
        await Scored(vm);
        Assert.Contains(Loc.T("compare.note.converted8Bit"), vm.Status, StringComparison.Ordinal);

        // A different model on another row: the plot says so.
        vm.SelectedModel = VmafModels.Standard;
        string other = Copy(await TestMedia.Hevc10Async(), "other.mkv");
        await vm.HandleDropAsync([other], DropZone.Encodes);
        await Scored(vm, 2);
        Assert.True(vm.HasMixedModels);
        vm.RemoveEncode(vm.Encodes[^1]);
        Assert.False(vm.HasMixedModels);

        vm.SelectedBackend = VmafBackend.Cpu;
        Assert.Equal("CPU", vm.BackendChip);
    }

    [AvaloniaFact]
    public async Task Startup_resolves_ffmpeg_probes_and_logs_the_session()
    {
        VmafRunner.ClearCapabilityCache(); // "ffmpeg" may have been probed by another test's tools
        var tools = new ScoringTools();
        var services = new AppServices { Processes = tools.Runner, Settings = new MemorySettings(), Environment = _ => null, DescribeLibav = () => throw new InvalidOperationException("no libav") };
        var vm = new MainViewModel(services);
        Assert.Equal("…", vm.BackendChip); // not probed yet
        await vm.InitializeAsync(); // "ffmpeg" resolves to itself here: probe and log
        Assert.Contains("libvmaf", vm.BackendStatus, StringComparison.Ordinal);
        Assert.Equal("CPU", vm.BackendChip);

        vm.FfmpegPath = " ";
        await vm.InitializeAsync();

        var without = new ScoringTools();
        var none = new MainViewModel(new AppServices
        {
            Processes = new ScriptedRunner(r => r.Arguments.Contains("-filters") ? ScriptedRunner.Ok(string.Empty) : without.Runner.RunAsync(r).GetAwaiter().GetResult()),
            Settings = new MemorySettings(),
            Environment = name => name == "VQ_FFMPEG" ? "/opt/plain/ffmpeg" : null,
        });
        Assert.Equal("/opt/plain/ffmpeg", none.FfmpegPath);
        await none.InitializeAsync();
        Assert.Contains(Loc.T("compare.backend.noVmaf"), none.BackendStatus, StringComparison.Ordinal);

        VqLog.Disable();
        await Compare(tools).InitializeAsync(); // logging off: the session still starts
        VqLog.Reset();
    }

    [AvaloniaFact]
    public async Task Viewer_modes_difference_flicker_and_playback()
    {
        var tools = new ScoringTools();
        MainViewModel vm = Compare(tools);
        string reference = Copy(await TestMedia.ClipAsync(), "ref.mkv");
        string encode = Copy(await TestMedia.Mp4Async(), "enc.mp4");
        await vm.HandleDropAsync([reference, encode], DropZone.Auto);
        await Scored(vm);
        await Ui.SettleAsync();

        Assert.True(vm.IsTwoPane);
        vm.CompareMode = CompareMode.Difference;
        await Ui.UntilAsync(() => vm.DifferenceBitmap is not null);
        Assert.True(vm.IsDifference && vm.IsTwoPane);
        Assert.Contains(vm.ChannelLabel, vm.RightPaneTitle, StringComparison.Ordinal);
        foreach (DifferenceChannels channels in Enum.GetValues<DifferenceChannels>())
        {
            vm.DifferenceChannels = channels;
            Assert.False(string.IsNullOrEmpty(vm.DifferenceLegend));
            Assert.False(string.IsNullOrEmpty(vm.ChannelLabel));
        }

        vm.DifferenceGain = 8;
        vm.AutoGainCommand.Execute(null);
        Assert.InRange(vm.DifferenceGain, 1, 32);
        vm.StepFrame(3); // the difference is built with the frames
        await Ui.SettleAsync(200);
        Assert.NotNull(vm.DifferenceBitmap);

        vm.CompareMode = CompareMode.Wipe;
        Assert.True(vm.IsWipe && vm.IsSinglePane);
        Assert.Equal(Loc.T("compare.pane.wipe"), vm.SinglePaneTitle);
        Assert.Equal(Loc.T("compare.pane.encode"), vm.RightPaneTitle);
        vm.SetFlicker(true); // only in flicker mode
        Assert.False(vm.FlickerShowsDistorted);

        vm.CompareMode = CompareMode.Flicker;
        Assert.Equal(Loc.T("compare.pane.flicker"), vm.SinglePaneTitle);
        vm.SetFlicker(true);
        Assert.Same(vm.DistortedBitmap, vm.SingleBitmap);
        vm.SetFlicker(false);
        Assert.Same(vm.ReferenceBitmap, vm.SingleBitmap);
        vm.AutoFlicker = true;
        Assert.True(vm.IsAutoFlicker);
        await Ui.UntilAsync(() => vm.FlickerShowsDistorted);
        vm.AutoFlicker = false;
        vm.AutoFlicker = true; // the timer is reused
        vm.CompareMode = CompareMode.SideBySide; // leaving flicker stops it
        Assert.False(vm.AutoFlicker);

        // Playback: the clock picks frames; it wraps at the end.
        vm.GoToFrame(46);
        vm.TogglePlaybackCommand.Execute(null);
        Assert.True(vm.IsPlaying);
        Assert.Equal(Loc.T("compare.pause"), vm.PlayPauseHint);
        await Ui.UntilAsync(() => vm.CurrentFrame < 46);
        vm.TogglePlayPause();
        Assert.False(vm.IsPlaying);
        vm.StartPlayback();
        vm.StartPlayback(); // the timer is reused
        vm.SelectedEncode = null; // no frames: the next tick stops playback
        await Ui.UntilAsync(() => !vm.IsPlaying);
        Assert.False(vm.TogglePlaybackCommand.CanExecute(null));
        vm.StartPlayback(); // nothing to play
        Assert.False(vm.IsPlaying);

        vm.ShowDelta = true;
        vm.FixedScale = true;
        vm.ShowSmoothed = true;
        vm.WipeSplit = 0.3;
        vm.IsDragging = true;
        vm.FlushSettings();
        vm.FlushSettings(); // nothing pending
        Assert.True(vm.PersistedSettings.FixedScale);
        Assert.True(vm.PersistedSettings.ShowSmoothed);
    }

    [AvaloniaFact]
    public async Task Difference_needs_both_frames_and_matching_sizes()
    {
        var tools = new ScoringTools();
        MainViewModel vm = Compare(tools);
        vm.CompareMode = CompareMode.Difference;
        vm.AutoGainCommand.Execute(null); // no frames yet
        Assert.Null(vm.DifferenceBitmap);
        Assert.Equal(4.0, vm.DifferenceGain);

        string reference = Copy(await TestMedia.Mp4Async(), "ref.mp4");
        string bigger = Copy(await TestMedia.LongAsync(), "long.mkv"); // same size: fine
        await vm.HandleDropAsync([reference, bigger], DropZone.Auto);
        await Scored(vm);
        string other = Copy(await TestMedia.Av1Async(), "av1.mkv");
        await vm.HandleDropAsync([other], DropZone.Encodes);
        await Scored(vm, 2);
        vm.SelectedEncode = vm.Encodes[0];
        await Ui.SettleAsync(100);
        vm.SelectedEncode = vm.Encodes[^1];
        await Ui.UntilAsync(() => vm.EncodeName == "av1.mkv", message: () => vm.EncodeName + " | " + vm.Status + " | " + string.Join(",", vm.Encodes.Select(e => e.Name)));
        Assert.Contains(Loc.T("compare.warning.colorMatrix", "x", "y")[..5], vm.Status, StringComparison.Ordinal);

        string raw = Copy(await TestMedia.RawH264Async(), "raw.h264"); // no duration: bitrate from the file size
        await vm.HandleDropAsync([raw], DropZone.Encodes);
        await Scored(vm, 3);
        Assert.True(vm.Encodes[^1].BitrateKbps > 0);
    }
}
