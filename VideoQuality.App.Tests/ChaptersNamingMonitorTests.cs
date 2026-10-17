using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VideoQuality.App.Controls;
using VideoQuality.App.ViewModels;
using VideoQuality.App.Views;
using VideoQuality.Core.Chapters;
using VideoQuality.Core.Encode;
using VideoQuality.Core.Localization;
using VideoQuality.Core.Media;
using VideoQuality.Core.Monitor;
using VideoQuality.Core.Release;
using VideoQuality.Core.Tools;
using VideoQuality.Tests;

namespace VideoQuality.App.Tests;

public sealed class ChaptersTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("vq-app-chapters-").FullName;

    public void Dispose() => Directory.Delete(_work, true);

    [AvaloniaFact]
    public async Task Chapters_are_imported_edited_named_checked_and_sent_to_mux()
    {
        var vm = new ChaptersViewModel(_ => new MediaInfo
        {
            Path = "/v.mkv",
            Duration = TimeSpan.FromMinutes(24),
            Chapters = [new MediaChapter(TimeSpan.Zero, TimeSpan.FromMinutes(1.5), "A"), new MediaChapter(TimeSpan.FromMinutes(1.5), TimeSpan.FromMinutes(22), "B")],
        });
        Assert.False(vm.HasRows);
        await vm.ImportAsync("/v.mkv");
        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal(TimeSpan.FromMinutes(24), vm.Duration);

        vm.AddCommand.Execute(null);
        Assert.Equal("00:02:30.000", vm.Rows[^1].Time);
        vm.Rows[1].IsOpening = true;
        vm.Rows[^1].IsEnding = true;
        vm.AutoNameCommand.Execute(null);
        Assert.Equal(["Chapter1", "OP", "ED"], vm.Rows.Select(r => r.Name));

        vm.Rows[0].Time = "bad";
        Assert.Contains(Loc.T("chapters.check.badTime"), vm.Problems);
        Assert.Null(vm.Rows[0].Start);
        Assert.Equal(TimeSpan.Zero, vm.Marks()[0].Start);
        vm.SortCommand.Execute(null);
        Assert.Equal("bad", vm.Rows[^1].Time); // unparsable times sort last
        vm.RemoveCommand.Execute(vm.Rows[^1]);
        Assert.DoesNotContain(Loc.T("chapters.check.badTime"), vm.Problems);

        string? sent = null;
        vm.SendToMuxRequested += (_, path) => sent = path;
        vm.SendToMuxCommand.Execute(null);
        Assert.Equal(2, ChapterList.Load(sent!).Count);
        File.Delete(sent!);

        vm.ClearCommand.Execute(null);
        Assert.Empty(vm.Problems);
        vm.AddCommand.Execute(null);
        Assert.Equal("00:00:00.000", vm.Rows[0].Time);
        vm.AutoNameCommand.Execute(null); // no OP or ED marked
        Assert.Equal("Chapter1", vm.Rows[0].Name);
    }

    [AvaloniaFact]
    public async Task Chapter_files_load_and_bad_files_explain_themselves()
    {
        string xml = Path.Combine(_work, "c.xml");
        ChapterList.Save(xml, [new ChapterMark(TimeSpan.Zero, "OP"), new ChapterMark(TimeSpan.FromMinutes(20), "ED")]);
        var vm = new ChaptersViewModel(_ => throw new InvalidOperationException("not a video"));
        await vm.ImportAsync(xml);
        Assert.True(vm.Rows[0].IsOpening);
        Assert.True(vm.Rows[1].IsEnding);

        string broken = Path.Combine(_work, "broken.xml");
        File.WriteAllText(broken, "<Chapters>");
        await vm.ImportAsync(broken);
        Assert.Equal(2, vm.Rows.Count); // kept
        await vm.ImportAsync("/v.mkv");
        Assert.Equal("not a video", vm.Status);
        await vm.ImportAsync(Path.Combine(_work, "missing.txt"));
        Assert.Contains("missing.txt", vm.Status, StringComparison.Ordinal);

        string saved = Path.Combine(_work, "out.xml");
        vm.Save(saved);
        Assert.Contains(saved, vm.Status, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Chapters_page_imports_drops_and_saves()
    {
        string xml = Path.Combine(_work, "c.txt");
        File.WriteAllText(xml, "CHAPTER01=00:00:00.000\nCHAPTER01NAME=OP\n");
        var vm = new ChaptersViewModel();
        var view = new ChaptersView();
        Window window = Ui.Show(view, vm);
        string saved = Path.Combine(_work, "saved.xml");
        using var scope = new DialogScope(new ScriptedDialogs().Answer(new[] { xml }, null, saved, null));

        Ui.Invoke(view, "OnImport");
        await Ui.UntilAsync(() => vm.HasRows);
        Ui.Invoke(view, "OnImport");
        Ui.Invoke(view, "OnSave");
        Ui.Invoke(view, "OnSave");
        await Ui.SettleAsync();
        Assert.True(File.Exists(saved));

        vm.ClearCommand.Execute(null);
        Ui.Drop(window, view, xml);
        await Ui.UntilAsync(() => vm.HasRows);
        window.Close();
    }
}

public class NamingTests
{
    [AvaloniaFact]
    public void Names_follow_the_form()
    {
        var vm = new NamingViewModel(_ => throw new InvalidOperationException());
        vm.Title = "Takopii No Genzai";
        vm.ChineseTitle = "章鱼哔的原罪";
        Assert.Equal("[Studio GreenTea] Takopii No Genzai [01][WebRip][HEVC-10bit 1080p AAC ASSx2].mkv", vm.FileName);
        Assert.Equal(3, vm.Files.Count);
        Assert.Contains("章鱼哔的原罪", vm.ReleaseTitle, StringComparison.Ordinal);

        vm.Group = " ";
        vm.Collaborators = "A & B";
        vm.Source = "BDRip";
        Assert.StartsWith("[A&B&Studio GreenTea]", vm.FileName, StringComparison.Ordinal); // groups in name order
        Assert.Contains("FLAC", vm.Files[0].Name, StringComparison.Ordinal);
        Assert.Contains("AAC", vm.Files[1].Name, StringComparison.Ordinal);

        var copied = new List<string>();
        vm.CopyRequested += (_, text) => copied.Add(text);
        vm.CopyCommand.Execute(null);
        vm.CopyCommand.Execute(string.Empty);
        vm.CopyCommand.Execute("x");
        vm.CopyAllCommand.Execute(null);
        Assert.Equal("x", copied[0]);
        Assert.Equal(4, copied[1].Split(Environment.NewLine).Length);
    }

    [AvaloniaTheory]
    [InlineData("hevc", 10, "HEVC-10bit")]
    [InlineData("h264", 8, "AVC")]
    [InlineData("h264", 10, "AVC-10bit")]
    [InlineData("av1", 10, "AV1-10bit")]
    [InlineData("vp9", 8, "VP9")]
    public async Task Videos_fill_in_their_codec_resolution_and_audio(string codec, int depth, string expected)
    {
        var vm = new NamingViewModel(_ => new MediaInfo
        {
            Path = "/v.mkv",
            Streams =
            [
                new MediaStream { Kind = StreamKind.Video, Codec = codec, BitDepth = depth, Height = 720 },
                new MediaStream { Kind = StreamKind.Audio, Codec = "flac" },
            ],
        });
        await vm.FillFromAsync("/v.mkv");
        Assert.Equal((expected, "720p", "FLAC"), (vm.VideoCodec, vm.Resolution, vm.AudioCodec));
        Assert.Equal(Loc.T("naming.status.fromVideo"), vm.Status);
    }

    [AvaloniaFact]
    public async Task Release_names_and_unreadable_files_fill_the_form_or_explain()
    {
        var vm = new NamingViewModel(path => path.EndsWith("bad.mkv", StringComparison.Ordinal) ? throw new InvalidOperationException("unreadable") : new MediaInfo { Path = path });
        await vm.FillFromAsync("/r/[Studio GreenTea&Other] Some Show [OVA02v2][BDRip][HEVC-10bit 1080p FLAC][JPSC].mkv");
        Assert.Equal(("Some Show", EpisodeKind.Ova, 2, 2), (vm.Title, vm.Kind, (int)vm.Episode, (int)vm.Version));
        Assert.Equal(["Other", "Studio GreenTea"], new[] { vm.Group, vm.Collaborators }.Order());
        Assert.Equal(Loc.T("naming.status.parsed"), vm.Status);

        await vm.FillFromAsync("/x/empty.mkv"); // no video, no audio: the form stays
        Assert.Equal("HEVC-10bit", vm.VideoCodec);
        await vm.FillFromAsync("/x/bad.mkv");
        Assert.Equal("unreadable", vm.Status);
    }

    [AvaloniaFact]
    public async Task Naming_page_takes_drops_and_copies()
    {
        var vm = new NamingViewModel();
        var view = new NamingView();
        Window window = Ui.Show(view, vm);
        string clip = await TestMedia.ClipAsync();
        Ui.Drop(window, view, clip);
        await Ui.UntilAsync(() => vm.Status == Loc.T("naming.status.fromVideo"));
        Assert.Equal("AVC", vm.VideoCodec);

        vm.CopyCommand.Execute("copied text");
        await Ui.SettleAsync();
        Assert.Equal("copied text", await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(window.Clipboard!));
        view.DataContext = null;
        window.Close();
    }
}

public class MonitorTests
{
    private const string Smi = "NVIDIA GeForce RTX 5060 Ti, 35, 60, 5, 2048, 16384, 52\n";

    private sealed class FakeControl : IProcessControl
    {
        public int Id => 1;

        public TimeSpan ProcessorTime { get; set; }

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

    [AvaloniaFact]
    public async Task Samples_fill_the_gauges_and_list_running_encodes()
    {
        var control = new FakeControl();
        var release = new TaskCompletionSource<ProcessResult>();
        var runner = new ScriptedRunner((request, _) =>
        {
            if (request.FileName == "nvidia-smi")
            {
                return Task.FromResult(ScriptedRunner.Ok(Smi));
            }

            request.OnStarted?.Invoke(control);
            return release.Task;
        });
        var queue = new EncodeQueue(runner, () => "ffmpeg");
        var job = new EncodeJob("/in/ep.mkv", new EncodeCommand([["-i", "x"]], "/out/ep.mkv", [], [], 0, TimeSpan.FromSeconds(10)));
        queue.Add(job);
        await Ui.UntilAsync(() => job.ProcessorTime == TimeSpan.Zero && job.State == EncodeJobState.Running);

        var vm = new MonitorViewModel(new SystemMonitor(runner), queue);
        await vm.SampleAsync();
        control.ProcessorTime = TimeSpan.FromSeconds(4);
        await Task.Delay(20);
        await vm.SampleAsync();

        Assert.True(vm.HasGpu);
        Assert.Equal("NVIDIA GeForce RTX 5060 Ti", vm.GpuName);
        Assert.Equal("2048 / 16384 MiB", vm.GpuMemory.Value);
        Assert.Equal(2, vm.Cpu.History.Count);
        RunningEncode running = Assert.Single(vm.Encodes);
        Assert.True(vm.HasEncodes);
        Assert.Equal("ep.mkv", running.Name);

        release.SetResult(ScriptedRunner.Ok());
        await queue.WhenIdleAsync();
        await vm.SampleAsync();
        Assert.False(vm.HasEncodes);
    }

    [Fact]
    public void Gauges_keep_two_minutes_and_shares_need_a_total()
    {
        var gauge = new Gauge("CPU");
        for (int i = 0; i < 130; i++)
        {
            gauge.Add(i, i + " %");
        }

        Assert.Equal(120, gauge.History.Count);
        Assert.Equal("129 %", gauge.Value);
        Assert.Equal(25, MonitorViewModel.Share(1, 4));
        Assert.Equal(0, MonitorViewModel.Share(1, 0));
    }

    [AvaloniaFact]
    public async Task Monitor_page_samples_only_while_visible()
    {
        var runner = new ScriptedRunner(_ => new ProcessResult(9, string.Empty, string.Empty)); // no GPU
        var vm = new MonitorViewModel(new SystemMonitor(runner), new EncodeQueue(runner, () => "ffmpeg"));
        var view = new MonitorView();
        Window window = Ui.Show(view, vm);
        await Ui.UntilAsync(() => vm.Cpu.History.Count > 0);
        Assert.False(vm.HasGpu);

        view.IsVisible = false;
        int count = vm.Cpu.History.Count;
        await view.TickAsync(); // a direct tick still samples
        view.IsVisible = true;
        await Ui.UntilAsync(() => vm.Cpu.History.Count > count);

        view.DataContext = null;
        await view.TickAsync(); // no model: nothing to sample
        view.DataContext = vm;

        // A tick while one is running is skipped.
        Task first = view.TickAsync();
        await view.TickAsync();
        await first;
        view.Opacity = 0.5; // other property changes leave the timer alone
        window.Close();
    }

    [AvaloniaFact]
    public void Sparklines_draw_the_recent_values()
    {
        var line = new Sparkline { Width = 200, Height = 60, Values = [0, 50, 120, -5], Capacity = 4 };
        var empty = new Sparkline { Width = 200, Height = 60 };
        var single = new Sparkline { Width = 200, Height = 60, Values = [10] };
        Window window = Ui.Show(new StackPanel { Children = { line, empty, single } }, null);
        Assert.NotNull(Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window));
        Assert.Equal(4, line.Capacity);
        Assert.NotNull(line.Stroke);
        window.Close();
    }
}
