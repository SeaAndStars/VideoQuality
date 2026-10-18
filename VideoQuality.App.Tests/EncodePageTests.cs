using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using VideoQuality.App.ViewModels;
using VideoQuality.App.Views;
using VideoQuality.Core.Encode;
using VideoQuality.Core.Localization;
using VideoQuality.Core.Media;
using VideoQuality.Core.Models;
using VideoQuality.Core.Scoring;
using VideoQuality.Core.Tools;
using VideoQuality.Tests;

namespace VideoQuality.App.Tests;

public sealed class EncodePageTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("vq-app-encode-").FullName;

    public void Dispose() => Directory.Delete(_work, true);

    private string Video(string name)
    {
        string path = Path.Combine(_work, name);
        File.WriteAllText(path, "video");
        return path;
    }

    private static MediaInfo Info(string path) => new()
    {
        Path = path,
        Duration = TimeSpan.FromMinutes(24),
        Size = 300L * 1024 * 1024,
        Streams =
        [
            new MediaStream { Index = 0, Kind = StreamKind.Video, Codec = "h264", Width = 1920, Height = 1080 },
            new MediaStream { Index = 1, Kind = StreamKind.Audio, Codec = "aac", BitRate = 128_000, SampleRate = 48000, Channels = 2 },
        ],
    };

    private EncodeViewModel Page(SettingsViewModel settings, EncodeQueue queue, Func<string, MediaInfo>? read = null) =>
        new(settings, queue, new PresetStore(Path.Combine(_work, "presets")), read ?? Info, action => action());

    [AvaloniaFact]
    public async Task Files_are_added_previewed_and_queued()
    {
        SettingsViewModel settings = await Pages.SettingsAsync();
        var release = new TaskCompletionSource<ProcessResult>();
        var queue = new EncodeQueue(new ScriptedRunner((_, token) => release.Task.WaitAsync(token)), () => "ffmpeg");
        EncodeViewModel vm = Page(settings, queue, path => path.EndsWith("bad.mkv", StringComparison.Ordinal) ? throw new InvalidOperationException("unreadable") : Info(path));
        var toasts = new List<ToastRequest>();
        vm.ToastRequested += (_, t) => toasts.Add(t);
        Assert.Equal(Loc.T("encode.preview.empty"), vm.CommandPreview);

        string folder = Directory.CreateDirectory(Path.Combine(_work, "season")).FullName;
        File.WriteAllText(Path.Combine(folder, "02.mkv"), "v");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "x");
        await vm.AddPathsAsync([Video("01.mkv"), folder, Video("bad.mkv"), Video("readme.txt"), null, Video("01.mkv")]);
        Assert.Equal(["01.mkv", "02.mkv"], vm.Inputs.Select(i => i.FileName));
        Assert.Equal("unreadable", Assert.Single(toasts).Message);
        Assert.True(vm.HasInputs);
        Assert.Contains("h264 1920×1080", vm.Inputs[0].Details, StringComparison.Ordinal);
        Assert.Contains("libx265", vm.CommandPreview, StringComparison.Ordinal);
        Assert.Contains(settings.Encoder!.Path, vm.CommandPreview, StringComparison.Ordinal);

        vm.Editor.Container = "mp4";
        vm.Editor.KeepSubtitles = true;
        Assert.NotEmpty(vm.Warnings);
        vm.OutputDirectory = " /out ";
        Assert.Contains("/out/01", vm.CommandPreview, StringComparison.Ordinal);

        vm.RemoveInputCommand.Execute(vm.Inputs[1]);
        vm.StartCommand.Execute(null);
        Assert.False(vm.HasInputs);
        await Ui.UntilAsync(() => vm.HasJobs && vm.Jobs[0].IsRunning);
        EncodeJobRow row = vm.Jobs[0];
        Assert.Contains("--:--:--", row.Details, StringComparison.Ordinal);
        Assert.True(row.IsActive);
        Assert.False(row.IsQueued);

        vm.StopCommand.Execute(row);
        await Ui.UntilAsync(() => row.IsFailed);
        Assert.Equal(Loc.T("encode.row.cancelled"), row.Details);
        vm.RetryCommand.Execute(row);
        await Ui.UntilAsync(() => row.IsRunning);
        release.SetResult(ScriptedRunner.Ok());
        await Ui.UntilAsync(() => row.IsDone);
        Assert.Equal(ToastKind.Success, toasts[^1].Kind);
        Assert.Contains("0 KiB", row.Details, StringComparison.Ordinal);

        string? revealed = null;
        (string, string)? compared = null;
        vm.RevealRequested += (_, path) => revealed = path;
        vm.CompareRequested += (_, pair) => compared = pair;
        vm.RevealCommand.Execute(row);
        vm.ScoreInCompareCommand.Execute(row);
        Assert.Equal(row.Output, revealed);
        Assert.Equal((row.Job.Source, row.Output), compared);

        vm.ClearFinishedCommand.Execute(null);
        Assert.False(vm.HasJobs);
    }

    [AvaloniaFact]
    public async Task Failures_pauses_and_missing_encoders()
    {
        SettingsViewModel settings = await Pages.SettingsAsync();
        var gate = new TaskCompletionSource<ProcessResult>();
        var control = new PausableControl();
        var queue = new EncodeQueue(new ScriptedRunner((request, _) =>
        {
            request.OnStarted?.Invoke(control);
            ScriptedRunner.Emit(request, "out_time_us=60000000", "total_size=1000000", "speed=2.0x", "fps=48", "progress=continue");
            return gate.Task;
        }), () => "ffmpeg");
        EncodeViewModel vm = Page(settings, queue);
        await vm.AddPathsAsync([Video("a.mkv"), Video("b.mkv")]);
        vm.StartCommand.Execute(null);
        await Ui.UntilAsync(() => vm.Jobs.Count == 2 && vm.Jobs[0].IsRunning && vm.Jobs[0].Details.Contains("2.00", StringComparison.Ordinal));
        EncodeJobRow running = vm.Jobs[0], queued = vm.Jobs[1];
        Assert.True(queued.IsQueued);
        Assert.Equal(Loc.T("encode.row.queued"), queued.Details);

        vm.PauseCommand.Execute(running);
        await Ui.UntilAsync(() => running.IsPaused);
        vm.ResumeCommand.Execute(running);
        await Ui.UntilAsync(() => running.IsRunning);
        vm.RemoveJobCommand.Execute(running); // running jobs stay
        Assert.Equal(2, vm.Jobs.Count);
        vm.RemoveJobCommand.Execute(queued);
        Assert.Single(vm.Jobs);

        var toasts = new List<ToastRequest>();
        vm.ToastRequested += (_, t) => toasts.Add(t);
        gate.SetResult(new ProcessResult(1, string.Empty, string.Empty));
        await Ui.UntilAsync(() => running.IsFailed);
        Assert.Equal(ToastKind.Error, toasts[^1].Kind);
        Assert.Contains("1", running.Details, StringComparison.Ordinal);

        // No encoder: nothing is queued.
        settings.Encoder = null;
        await vm.AddPathsAsync([Video("c.mkv")]);
        vm.StartCommand.Execute(null);
        Assert.Equal(ToastKind.Error, toasts[^1].Kind);
        Assert.True(vm.HasInputs);
        Assert.Contains("ffmpeg", vm.CommandPreview, StringComparison.Ordinal);

        // A preset the builder rejects: preview and start both explain.
        settings.Encoder = new EncoderTool("ffmpeg", "ffmpeg version x", new HashSet<string> { "libx265" }, new HashSet<string>());
        vm.Editor.Mode = RateControl.TargetSize;
        vm.Editor.TargetMbPer24Minutes = 1;
        Assert.DoesNotContain("ffmpeg", vm.CommandPreview, StringComparison.Ordinal);
        vm.StartCommand.Execute(null);
        Assert.Equal(ToastKind.Error, toasts[^1].Kind);

        settings.EncodeConcurrency = 3;
        Assert.Equal(3, queue.Concurrency);
    }

    [AvaloniaFact]
    public async Task Presets_are_saved_selected_and_deleted()
    {
        SettingsViewModel settings = await Pages.SettingsAsync(tools: false);
        EncodeViewModel vm = Page(settings, new EncodeQueue(Pages.NothingRuns, () => "ffmpeg"));
        Assert.True(vm.SelectedPreset!.BuiltIn);
        Assert.EndsWith(" (copy)", vm.SaveName, StringComparison.Ordinal);

        vm.SaveName = "  Mine ";
        vm.Editor.Quality = 17;
        vm.SavePresetCommand.Execute(null);
        Assert.Equal("Mine", vm.SelectedPreset!.Name);
        Assert.Equal("Mine", vm.SaveName);
        Assert.Equal(17, vm.Editor.Quality);

        vm.DeletePresetCommand.Execute(null);
        Assert.True(vm.SelectedPreset!.BuiltIn);
        vm.DeletePresetCommand.Execute(null); // built-ins stay
        Assert.True(vm.SelectedPreset!.BuiltIn);

        vm.SaveName = "Gone";
        vm.SavePresetCommand.Execute(null);
        File.Delete(Directory.GetFiles(Path.Combine(_work, "presets")).Single());
        vm.DeletePresetCommand.Execute(null); // already deleted on disk
        Assert.Equal("Gone", vm.SelectedPreset!.Name);

        vm.ReloadPresets("no such preset");
        Assert.Same(vm.Presets[0], vm.SelectedPreset);
        vm.SelectedPreset = null; // a combo box clearing itself leaves the editor alone
        Assert.Equal(vm.Presets[0].Name, vm.Editor.Name);
    }

    [AvaloniaFact]
    public async Task Encode_page_adds_files_picks_the_output_and_takes_drops()
    {
        SettingsViewModel settings = await Pages.SettingsAsync(tools: false);
        EncodeViewModel vm = Page(settings, new EncodeQueue(Pages.NothingRuns, () => "ffmpeg"));
        var view = new EncodeView();
        Window window = Ui.Show(view, vm);
        using var scope = new DialogScope(new ScriptedDialogs().Answer(new[] { Video("a.mkv") }, "/out", null));

        Ui.Invoke(view, "OnAddFiles");
        Ui.Invoke(view, "OnPickOutput");
        Ui.Invoke(view, "OnPickOutput");
        await Ui.UntilAsync(() => vm.HasInputs);
        Assert.Equal("/out", vm.OutputDirectory);
        Ui.Drop(window, view, Video("b.mkv"));
        await Ui.UntilAsync(() => vm.Inputs.Count == 2);
        window.Close();
    }

    [AvaloniaFact]
    public void The_default_dispatcher_posts_job_updates()
    {
        var settings = new SettingsViewModel(new MainViewModel(), new Services.AppServices { Processes = Pages.NothingRuns });
        var vm = new EncodeViewModel(settings, new EncodeQueue(Pages.NothingRuns, () => "ffmpeg"), new PresetStore(Path.Combine(_work, "p2")));
        vm.Queue.Add(new EncodeJob("/in.mkv", new EncodeCommand([["-i", "x"]], "/out.mkv", [], [], 0, TimeSpan.FromSeconds(1))));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(vm.HasJobs);
    }

    private sealed class PausableControl : IProcessControl
    {
        public int Id => 7;

        public TimeSpan ProcessorTime => TimeSpan.Zero;

        public void Suspend()
        {
        }

        public void Resume()
        {
        }

        public void Kill()
        {
        }
    }
}

public class PresetEditorTests
{
    [Fact]
    public void Every_field_round_trips_through_a_preset()
    {
        var preset = new EncodePreset
        {
            Name = "Full",
            Description = "all fields",
            Container = "mp4",
            Video = new VideoSettings
            {
                Encoder = "libx264", Mode = RateControl.Bitrate, BitrateKbps = 4000, Preset = "slow", Tune = "film", Profile = "high",
                BitDepth = 8, PixelFormat = "yuv420p", KeyframeInterval = 240, ConstantFrameRate = false, FrameRate = "24000/1001", EncoderParams = "aq-mode=3",
            },
            Frame = new FrameSettings { Width = 1280, Height = 720, CropLeft = 2, CropTop = 4, CropRight = 6, CropBottom = 8, Deinterlace = Deinterlace.Bwdif, Denoise = Denoise.Hqdn3d, DenoiseStrength = 3, Deband = true, Sharpen = 0.5, Flip = Flip.Horizontal, Rotate = 90 },
            Color = new ColorSettings { Primaries = "bt709", Transfer = "bt709", Matrix = "bt709", Range = "tv" },
            Burn = new SubtitleBurn { File = "/s.ass", StreamIndex = 1, FontsDirectory = "/fonts", ForceStyle = "Fontsize=40", CharacterEncoding = "UTF-8" },
            Audio = new AudioSettings { Encoder = "flac", SampleRate = 48000, Channels = 2, SampleBits = 24, CopyWhenCompatible = false, FirstTrackOnly = true },
            Streams = new StreamSettings { KeepSubtitles = true, KeepChapters = false, KeepMetadata = true, KeepAttachments = true, Start = 90.5, End = 120, Title = "T" },
            Custom = new CustomArguments { Input = "-a", Video = "-b", Audio = "-c", Filter = "eq", Output = "-d" },
        };

        var editor = new PresetEditor();
        int changes = 0;
        editor.Changed += (_, _) => changes++;
        editor.Load(preset);
        Assert.Equal(1, changes);
        EncodePreset copy = editor.ToPreset();
        Assert.Equal(PresetStore.Write(preset with { BuiltIn = false }), PresetStore.Write(copy));
        Assert.Equal("00:01:30.500", editor.Start);
        Assert.True(editor.UsesBitrate);
        Assert.False(editor.UsesQuality);
        Assert.False(editor.UsesTargetSize);
        Assert.False(editor.AudioHasBitrate);
        Assert.True(editor.HasParams);
        Assert.Contains("film", editor.Tunes);
        Assert.Contains("high", editor.Profiles);
        Assert.Contains(10, editor.BitDepths);
        Assert.Contains("slow", editor.EncoderPresets);
        Assert.Contains(RateControl.TwoPass, editor.Modes);

        editor.BurnStream = null;
        editor.OutputPattern = " ";
        Assert.Null(editor.ToPreset().Burn!.StreamIndex);
        Assert.Equal("{name}.encoded", editor.ToPreset().OutputPattern);
    }

    [Fact]
    public void Unknown_encoders_and_modes_fall_back()
    {
        var editor = new PresetEditor();
        editor.Load(new EncodePreset { Video = new VideoSettings { Encoder = "nope", Mode = RateControl.Copy }, Audio = new AudioSettings { Encoder = "nope" } });
        Assert.Equal(EncoderCatalog.Video[0], editor.Encoder);
        Assert.Equal(EncoderCatalog.Video[0].Modes[0], editor.Mode);
        Assert.Equal(EncoderCatalog.Audio[0], editor.AudioEncoder);
        Assert.Null(editor.ToPreset().Burn);
    }

    [Fact]
    public void Changing_the_encoder_resets_what_it_does_not_support()
    {
        var editor = new PresetEditor();
        editor.Mode = RateControl.TwoPass;
        editor.BitDepth = 12;
        editor.Tune = "grain";
        editor.Encoder = EncoderCatalog.FindVideo("libsvtav1")!; // no two-pass, no 12-bit
        Assert.Equal(RateControl.Quality, editor.Mode);
        Assert.Equal(30, editor.Quality);
        Assert.Equal(0, editor.BitDepth);
        Assert.Equal(string.Empty, editor.Tune);
        Assert.Equal("6", editor.EncoderPreset);

        editor.Mode = RateControl.Bitrate;
        editor.BitDepth = 10;
        editor.Encoder = EncoderCatalog.FindVideo("libx265")!; // keeps both
        Assert.Equal(RateControl.Bitrate, editor.Mode);
        Assert.Equal(10, editor.BitDepth);

        editor.Encoder = EncoderCatalog.FindVideo("copy")!;
        Assert.True(editor.IsCopy);
        Assert.Equal(RateControl.Copy, editor.ToPreset().Video.Mode);
        Assert.False(editor.HasParams);
    }

    [Fact]
    public void Encoder_lists_follow_the_located_ffmpeg_and_keep_the_selection()
    {
        var editor = new PresetEditor();
        editor.Encoder = EncoderCatalog.FindVideo("hevc_nvenc")!;
        var tool = new EncoderTool("ffmpeg", "ffmpeg version x", new HashSet<string> { "libx264", "aac" }, new HashSet<string>());
        editor.SetAvailable(tool);
        Assert.Equal(["libx264", "hevc_nvenc", "copy"], editor.VideoEncoders.Select(e => e.Name));
        Assert.Contains(editor.AudioEncoders, e => e.Name == "aac");

        editor.SetAvailable(null);
        Assert.Equal(EncoderCatalog.Video.Count, editor.VideoEncoders.Count);
        Assert.Equal(EncoderCatalog.Video, editor.VideoEncoders);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(" ", null)]
    [InlineData("90.5", 90.5)]
    [InlineData("1:30", 90.0)]
    [InlineData("00:01:30.500", 90.5)]
    [InlineData("1:2:3:4", null)]
    public void Times_parse_as_seconds_or_clock_times(string? text, double? expected) =>
        Assert.Equal(expected, PresetEditor.ParseTime(text));
}

public class EncodeEntryTests
{
    [AvaloniaFact]
    public void Rows_describe_their_state_and_report()
    {
        string file = Path.GetTempFileName();
        File.WriteAllBytes(file, new byte[2 * 1024 * 1024]);
        var entry = new EncodeEntry(file, "#3366FF");
        Assert.Equal("2.0 MB", entry.SizeText);
        Assert.Equal(("—", "—", "—", "—", "—", "—"), (entry.MeanText, entry.P1Text, entry.MaxText, entry.BackendText, entry.ModelText, entry.FramesText));
        Assert.Equal("—", entry.BitrateText);
        Assert.Equal(string.Empty, entry.BadgeText);
        Assert.False(entry.HasReport);

        foreach (EncodeState state in Enum.GetValues<EncodeState>())
        {
            entry.State = state;
            Assert.False(string.IsNullOrEmpty(entry.StatusText));
        }

        entry.State = EncodeState.Failed;
        entry.Error = "boom";
        Assert.Contains("boom", entry.StatusText, StringComparison.Ordinal);
        Assert.True(entry.IsFailed);
        entry.State = EncodeState.Scoring;
        Assert.True(entry.IsWorking && entry.IsBusy);
        entry.State = EncodeState.WaitingForReference;
        Assert.True(entry.IsPending);
        entry.State = EncodeState.Stale;
        Assert.True(entry.IsStale);
        entry.State = EncodeState.Done;
        Assert.True(entry.IsDone);

        entry.BitDepth = 10;
        entry.BitrateKbps = 5000;
        entry.Warning = "mismatch";
        entry.IsVisible = false;
        Assert.Equal(("10-bit", "5000 kb/s", true, 0.25), (entry.BadgeText, entry.BitrateText, entry.HasWarning, entry.SwatchOpacity));

        VmafFrame[] frames = [new(0, 90), new(1, 92)];
        entry.Report = new VmafReport(frames, VmafStats.FromFrames(frames), VmafExecutionBackend.Cpu, "cmd", TimeSpan.FromSeconds(1)) { Model = VmafModels.Neg };
        Assert.Equal(("91.00", "90.02", "92.00", "NEG", "2"), (entry.MeanText, entry.P1Text, entry.MaxText, entry.ModelText, entry.FramesText));
        Assert.NotEqual("—", entry.BackendText);
        Assert.NotNull(entry.Swatch);
        File.Delete(file);

        Assert.Equal(0, new EncodeEntry("/missing.mkv", "#000000").FileSize);
    }
}
