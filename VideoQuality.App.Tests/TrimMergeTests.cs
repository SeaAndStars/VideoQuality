using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using VideoQuality.App.Services;
using VideoQuality.App.ViewModels;
using VideoQuality.App.Views;
using VideoQuality.Core.Encode;
using VideoQuality.Core.Localization;
using VideoQuality.Core.Media;
using VideoQuality.Core.Tools;
using VideoQuality.Tests;

namespace VideoQuality.App.Tests;

public sealed class TrimMergeTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("vq-app-trim-").FullName;

    public void Dispose() => Directory.Delete(_work, true);

    private string File_(string name)
    {
        string path = Path.Combine(_work, name);
        File.WriteAllText(path, "video");
        return path;
    }

    private static MediaInfo Info(string path) => new()
    {
        Path = path,
        Duration = TimeSpan.FromSeconds(60),
        Size = 10L * 1024 * 1024,
        Streams = [new MediaStream { Index = 0, Kind = StreamKind.Video, Codec = "h264", Width = 1280, Height = 720, FrameRate = 24 }],
    };

    /// <summary>A page whose external tools answer from <paramref name="runner"/>.</summary>
    private static async Task<TrimMergeViewModel> PageAsync(ScriptedRunner runner, bool encoder = true, bool mkv = true)
    {
        var settings = new SettingsViewModel(new MainViewModel(Pages.Isolated()), Pages.Isolated(runner));
        await Ui.UntilAsync(() => settings.EncoderStatus != Loc.T("settings.encoder.looking"));
        settings.Encoder = encoder ? new EncoderTool("/bin/ffmpeg", "ffmpeg version x", new HashSet<string> { "libx264" }, new HashSet<string>()) : null;
        settings.MkvToolNix = mkv ? ScriptedMkv.Create(_ => ScriptedRunner.Ok()) : null;
        var queue = new EncodeQueue(runner, () => "ffmpeg");
        var encode = new EncodeViewModel(settings, queue, new PresetStore(Path.Combine(Path.GetTempPath(), "vq-no-presets")), Info, a => a());
        return new TrimMergeViewModel(settings, encode, path => path.Contains("bad", StringComparison.Ordinal) ? throw new InvalidOperationException("unreadable") : Info(path), _ => [TimeSpan.FromSeconds(8)]);
    }

    [AvaloniaFact]
    public async Task Cuts_copy_or_queue_a_reencode()
    {
        var requests = new List<ProcessRequest>();
        int exit = 0;
        var runner = new ScriptedRunner(request =>
        {
            requests.Add(request);
            ScriptedRunner.Emit(request, "out_time_us=5000000", "progress=continue");
            return new ProcessResult(exit, "stdout text", exit == 0 ? string.Empty : string.Empty);
        });
        TrimMergeViewModel vm = await PageAsync(runner);
        var toasts = new List<ToastRequest>();
        vm.ToastRequested += (_, t) => toasts.Add(t);

        await vm.CutCommand.ExecuteAsync(null); // nothing open
        Assert.False(vm.HasSource);

        string source = File_("ep.mkv");
        await vm.OpenCutSourceAsync(source);
        Assert.True(vm.HasSource);
        Assert.Equal("00:01:00.000", vm.End);
        Assert.Equal(Path.Combine(_work, "ep.cut.mkv"), vm.CutOutput);
        Assert.NotNull(vm.CutPreset);

        vm.Start = "10";
        vm.End = "5";
        await vm.CutCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("edit.error.range"), vm.Status);
        vm.Start = "x";
        await vm.CutCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("edit.error.range"), vm.Status);
        vm.Start = "10";
        vm.End = "";
        await vm.CutCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("edit.error.range"), vm.Status);

        vm.End = "40";
        await vm.CutCommand.ExecuteAsync(null);
        Assert.Contains(toasts, t => t.Kind == ToastKind.Warning); // starts between keyframes
        Assert.Equal(ToastKind.Success, toasts[^1].Kind);
        Assert.Equal(1, vm.Progress);
        Assert.Equal("/bin/ffmpeg", requests[^1].FileName);

        exit = 1;
        await vm.CutCommand.ExecuteAsync(null);
        Assert.Contains("stdout text", vm.Status, StringComparison.Ordinal); // no stderr: stdout explains

        vm.ReEncode = true;
        await vm.CutCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("trim.status.queued"), vm.Status);
        vm.CutPreset = null; // the editor's preset is used
        await vm.CutCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Encode.Queue.Jobs.Count);

        vm.CutPreset = new EncodePreset { Video = new VideoSettings { Encoder = "no-such-encoder" } };
        await vm.CutCommand.ExecuteAsync(null);
        Assert.NotEqual(Loc.T("trim.status.queued"), vm.Status); // the builder refused it

        await vm.OpenCutSourceAsync(File_("bad.mkv"));
        Assert.Equal("unreadable", vm.Status);
    }

    [AvaloniaFact]
    public async Task Cutting_needs_the_encoder_and_failed_starts_are_reported()
    {
        TrimMergeViewModel none = await PageAsync(new ScriptedRunner(_ => ScriptedRunner.Ok()), encoder: false);
        await none.OpenCutSourceAsync(File_("a.mkv"));
        none.End = "30";
        await none.CutCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("encode.error.noEncoder"), none.Status);

        TrimMergeViewModel broken = await PageAsync(new ScriptedRunner(request =>
            request.FileName == "/bin/ffmpeg" ? throw new ProcessStartException("/bin/ffmpeg", new System.ComponentModel.Win32Exception()) : ScriptedRunner.Ok()));
        await broken.OpenCutSourceAsync(File_("b.mkv"));
        broken.End = "30";
        await broken.CutCommand.ExecuteAsync(null);
        Assert.Contains("/bin/ffmpeg", broken.Status, StringComparison.Ordinal);
        Assert.False(broken.IsBusy);
    }

    [AvaloniaFact]
    public async Task Joins_check_order_and_use_the_right_tool()
    {
        var requests = new List<ProcessRequest>();
        int exit = 1;
        var runner = new ScriptedRunner(request =>
        {
            requests.Add(request);
            ScriptedRunner.Emit(request, "#GUI#progress 40%", "out_time_us=1000000", "progress=end");
            return new ProcessResult(exit, string.Empty, exit == 2 ? "mkvmerge error" : string.Empty);
        });
        TrimMergeViewModel vm = await PageAsync(runner);
        await vm.MergeCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("edit.merge.needTwo"), vm.Status);

        var toasts = new List<ToastRequest>();
        vm.ToastRequested += (_, t) => toasts.Add(t);
        await vm.AddMergeInputsAsync([File_("01.mkv"), File_("02.mkv"), File_("bad.mkv"), File_("notes.txt"), null]);
        Assert.Equal(["01.mkv", "02.mkv"], vm.MergeInputs.Select(i => i.FileName));
        Assert.Equal(ToastKind.Warning, Assert.Single(toasts).Kind);
        Assert.Equal(Path.Combine(_work, "01.joined.mkv"), vm.MergeOutput);
        Assert.Contains("00:01:00", vm.MergeInputs[0].Details, StringComparison.Ordinal);
        Assert.Empty(vm.MergeProblems);

        vm.MoveDownCommand.Execute(vm.MergeInputs[0]);
        Assert.Equal("02.mkv", vm.MergeInputs[0].FileName);
        vm.MoveDownCommand.Execute(vm.MergeInputs[1]); // already last
        vm.MoveUpCommand.Execute(vm.MergeInputs[1]);
        vm.MoveUpCommand.Execute(vm.MergeInputs[0]); // already first
        Assert.Equal("01.mkv", vm.MergeInputs[0].FileName);
        vm.MoveUpCommand.Execute(new MergeInput("/elsewhere.mkv", Info("/elsewhere.mkv"))); // not in the list
        vm.MoveDownCommand.Execute(new MergeInput("/elsewhere.mkv", Info("/elsewhere.mkv")));

        await vm.MergeCommand.ExecuteAsync(null); // mkvmerge exit 1: warnings, still written
        Assert.Equal(ToastKind.Success, toasts[^1].Kind);
        Assert.EndsWith("mkvmerge", requests[^1].FileName, StringComparison.Ordinal);

        exit = 2;
        await vm.MergeCommand.ExecuteAsync(null);
        Assert.Contains("mkvmerge error", vm.Status, StringComparison.Ordinal);

        exit = 0;
        vm.MergeOutput = Path.Combine(_work, "joined.mp4"); // ffmpeg's concat demuxer
        await vm.MergeCommand.ExecuteAsync(null);
        Assert.Equal("/bin/ffmpeg", requests[^1].FileName);
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "vq-concat-*.txt").Where(f => File.ReadAllText(f).Contains(_work, StringComparison.Ordinal)));

        vm.Settings.Encoder = null;
        await vm.MergeCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("encode.error.noEncoder"), vm.Status);
        vm.Settings.MkvToolNix = null;
        vm.MergeOutput = Path.Combine(_work, "joined.mkv");
        await vm.MergeCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("common.mkvtoolnixRequired"), vm.Status);

        vm.RemoveMergeInputCommand.Execute(vm.MergeInputs[0]);
        Assert.True(vm.HasMergeInputs);
        await vm.AddMergeInputsAsync([File_("03.mkv")]); // keeps the chosen output
        Assert.Equal(Path.Combine(_work, "joined.mkv"), vm.MergeOutput);
    }

    [AvaloniaFact]
    public async Task Keyframes_come_from_the_packet_index()
    {
        IReadOnlyList<TimeSpan> keyframes = TrimMergeViewModel.KeyframeTimes(await TestMedia.LongAsync());
        Assert.Equal(TimeSpan.Zero, keyframes[0]);
        Assert.True(keyframes.Count > 3);
        Assert.Empty(TrimMergeViewModel.KeyframeTimes(await TestMedia.RawH264Async())); // no duration, no rate
        Assert.Empty(TrimMergeViewModel.KeyframeTimes(await TestMedia.AudioOnlyAsync()));
    }

    [AvaloniaFact]
    public async Task Real_cut_and_join_from_the_page()
    {
        SettingsViewModel settings = await Pages.SettingsAsync();
        var encode = new EncodeViewModel(settings, new EncodeQueue(settings.Services.Processes, () => settings.Encoder!.Path), new PresetStore(Path.Combine(_work, "presets")));
        var vm = new TrimMergeViewModel(settings, encode);
        var view = new TrimMergeView();
        Window window = Ui.Show(view, vm);
        string clip = Path.Combine(_work, "clip.mkv");
        File.Copy(await TestMedia.ClipAsync(), clip);
        string joined = Path.Combine(_work, "both.mkv");
        using var scope = new DialogScope(new ScriptedDialogs().Answer(new[] { clip }, null, new[] { clip, clip }, null, joined));

        Ui.Invoke(view, "OnPickCutSource");
        await Ui.UntilAsync(() => vm.HasSource);
        Ui.Invoke(view, "OnPickCutSource"); // cancelled
        vm.Start = "0";
        vm.End = "1";
        await vm.CutCommand.ExecuteAsync(null);
        Assert.True(File.Exists(vm.CutOutput), vm.Status);

        Ui.Invoke(view, "OnAddMergeFiles");
        await Ui.UntilAsync(() => vm.MergeInputs.Count == 2);
        Ui.Invoke(view, "OnPickMergeOutput"); // cancelled: suggests the current name
        Ui.Invoke(view, "OnPickMergeOutput");
        Assert.Equal(joined, vm.MergeOutput);
        Assert.Contains(scope.Dialogs.Titles, t => t.EndsWith("|clip.joined.mkv", StringComparison.Ordinal));
        await vm.MergeCommand.ExecuteAsync(null);
        Assert.True(File.Exists(joined), vm.Status);

        vm.MergeOutput = string.Empty;
        Ui.Invoke(view, "OnPickMergeOutput"); // nothing chosen yet: a default name
        Assert.Contains(scope.Dialogs.Titles, t => t.EndsWith("|joined.mkv", StringComparison.Ordinal));

        vm.Source = null;
        Ui.Drop(window, view.FindControl<Border>("CutCard")!, clip);
        await Ui.UntilAsync(() => vm.HasSource);
        Ui.Drop(window, view.FindControl<Border>("MergeCard")!, clip);
        await Ui.UntilAsync(() => vm.MergeInputs.Count == 3);
        window.Close();
    }
}
