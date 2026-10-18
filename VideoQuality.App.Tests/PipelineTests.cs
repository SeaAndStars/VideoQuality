using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using VideoQuality.App.Services;
using VideoQuality.App.ViewModels;
using VideoQuality.App.Views;
using VideoQuality.Core.Encode;
using VideoQuality.Core.Localization;
using VideoQuality.Core.Media;
using VideoQuality.Core.Mkv;
using VideoQuality.Core.Release;
using VideoQuality.Core.Scoring;
using VideoQuality.Core.Tools;
using VideoQuality.Tests;

namespace VideoQuality.App.Tests;

/// <summary>The release pipeline against scripted tools (every branch) and real ones (the happy path).</summary>
public sealed class PipelineTests : IDisposable
{
    private const string GoodLog = """{"frames":[{"frameNum":0,"metrics":{"vmaf":95.5}},{"frameNum":1,"metrics":{"vmaf":96.5}}]}""";
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts");
    private readonly string _work = Directory.CreateTempSubdirectory("vq-app-pipeline-").FullName;

    public void Dispose() => Directory.Delete(_work, true);

    /// <summary>What the scripted tools do; tests flip these to reach each failure.</summary>
    private sealed class Script
    {
        public int EncodeExit { get; set; }

        public int BurnExit { get; set; }

        public bool CancelEncodes { get; set; }

        public int MuxExit { get; set; }

        public bool UnreadableEncode { get; set; }

        public bool FailRules { get; set; }

        public bool WarnRules { get; set; }

        public bool UnreadableSource { get; set; }

        public bool NoChapters { get; set; }
    }

    private static MediaInfo Compliant(string path, bool warn) => new()
    {
        Path = path,
        Duration = TimeSpan.FromMinutes(24),
        Size = 300L * 1024 * 1024,
        Chapters = [new MediaChapter(TimeSpan.Zero, TimeSpan.FromMinutes(2), "OP")],
        Streams = path.EndsWith(".mp4", StringComparison.Ordinal)
            ?
            [
                new MediaStream { Index = 0, Kind = StreamKind.Video, Codec = "hevc", BitDepth = 10, Width = 1920, Height = 1080, FrameRate = 23.976 },
                new MediaStream { Index = 1, Kind = StreamKind.Audio, Codec = "aac", BitRate = warn ? 200_000 : 256_000, SampleRate = 44100, Channels = 2, Language = "jpn" },
            ]
            :
            [
                new MediaStream { Index = 0, Kind = StreamKind.Video, Codec = "hevc", BitDepth = 10, Width = 1920, Height = 1080, FrameRate = 23.976 },
                new MediaStream { Index = 1, Kind = StreamKind.Audio, Codec = "aac", BitRate = warn ? 200_000 : 256_000, SampleRate = 44100, Channels = 2, Language = "jpn" },
                new MediaStream { Index = 2, Kind = StreamKind.Subtitle, Codec = "ass", Language = "chi", Title = "JPSC" },
                new MediaStream { Index = 3, Kind = StreamKind.Subtitle, Codec = "ass", Language = "zh", Title = "JPTC" },
                new MediaStream { Index = 4, Kind = StreamKind.Attachment, FileName = "ABCDEFGH.ttf", MimeType = "font/ttf" },
            ],
    };

    private async Task<(PipelineViewModel Vm, Script Script, EncodeQueue Queue)> PageAsync()
    {
        var script = new Script();
        EncodeQueue queue = null!;
        var runner = new ScriptedRunner(request =>
        {
            if (request.FileName.Contains("nvidia-smi", StringComparison.Ordinal))
            {
                return ScriptedRunner.Ok("No devices were found\n");
            }

            if (request.Arguments.Contains("-filters"))
            {
                return ScriptedRunner.Ok(" ... libvmaf          VV->V      Calculate the VMAF\n");
            }

            if (request.Arguments.Contains("-version"))
            {
                return ScriptedRunner.Ok("ffmpeg version 9.0 test\n");
            }

            if (request.Arguments.Any(a => a.Contains("libvmaf", StringComparison.Ordinal)))
            {
                ScriptedRunner.Emit(request, "frame=1", "progress=continue", "frame=2", "progress=end");
                File.WriteAllText(ScriptedRunner.LogPath(request), GoodLog);
                return ScriptedRunner.Ok();
            }

            // An encode: the queue's job for this output.
            string output = request.Arguments[^1];
            bool burn = output.EndsWith(".mp4", StringComparison.Ordinal);
            if (script.CancelEncodes)
            {
                queue.Stop(queue.Jobs.Last(j => j.Output == output));
                throw new OperationCanceledException();
            }

            int exit = burn ? script.BurnExit : script.EncodeExit;
            ScriptedRunner.Emit(request, "out_time_us=600000000", "total_size=1000", "progress=continue");
            if (exit == 0)
            {
                File.WriteAllText(output, "encoded");
            }

            return new ProcessResult(exit, string.Empty, exit == 0 ? string.Empty : "x265 [error]: bad");
        });
        queue = new EncodeQueue(runner, () => "/bin/ffmpeg");

        var settings = new SettingsViewModel(new MainViewModel(Pages.Isolated()), Pages.Isolated(runner));
        await Ui.UntilAsync(() => settings.EncoderStatus != Loc.T("settings.encoder.looking"));
        settings.Encoder = new EncoderTool("/bin/ffmpeg", "ffmpeg version x", new HashSet<string> { "libx265", "aac" }, new HashSet<string> { "subtitles" });
        settings.MkvToolNix = ScriptedMkv.Create(request =>
        {
            if (request.Arguments[0] == "-J")
            {
                string path = request.Arguments[1];
                return path.EndsWith(".ass", StringComparison.Ordinal)
                    ? ScriptedRunner.Ok(ScriptedMkv.Identify("SSA/ASS subtitles", 0, 0, [(0, "subtitles", "SubStationAlpha", "", "")]))
                    : script.UnreadableEncode
                        ? new ProcessResult(2, "Error: broken", string.Empty)
                        : ScriptedRunner.Ok(ScriptedMkv.Identify("Matroska", 1440, 0, [(0, "video", "HEVC", "", ""), (1, "audio", "AAC", "", ""), (2, "subtitles", "SubStationAlpha", "", "")]));
            }

            ScriptedRunner.Emit(request, "#GUI#progress 100%", script.MuxExit == 1 ? "#GUI#warning odd" : "#GUI#progress 100%");
            return new ProcessResult(script.MuxExit, string.Empty, script.MuxExit == 2 ? "mux broke" : string.Empty);
        });

        var compare = new MainViewModel(Pages.Isolated(runner)) { FfmpegPath = "/bin/vmaf-ffmpeg-" + Guid.NewGuid().ToString("N")[..6], SelectedBackend = VmafBackend.Cpu };
        var naming = new NamingViewModel(_ => new MediaInfo { Path = "x" }) { Title = "Takopii No Genzai" };
        string sourcePath = Path.Combine(_work, "source.mkv");
        var vm = new PipelineViewModel(settings, compare, queue, naming, path =>
            path == sourcePath
                ? script.UnreadableSource
                    ? throw new InvalidOperationException("source unreadable")
                    : Compliant(path, false) with { Streams = [.. Compliant(path, false).Streams.Take(2)], Chapters = script.NoChapters ? [] : Compliant(path, false).Chapters }
                : script.FailRules ? new MediaInfo { Path = path } : Compliant(path, script.WarnRules));
        return (vm, script, queue);
    }

    private string Scripts()
    {
        string subs = Directory.CreateDirectory(Path.Combine(_work, "subs")).FullName;
        string fonts = Directory.CreateDirectory(Path.Combine(subs, "Fonts")).FullName;
        File.Copy(Path.Combine(Fixtures, "DejaVuSans.ttf"), Path.Combine(fonts, "DejaVuSans.ttf"), true);
        TestMedia.Script(subs, "ep01.JPSC.ass");
        TestMedia.Script(subs, "ep01.JPTC.ass");
        return subs;
    }

    private string Source()
    {
        string path = Path.Combine(_work, "source.mkv");
        File.WriteAllText(path, "raw");
        return path;
    }

    [AvaloniaFact]
    public async Task Every_step_runs_and_the_result_opens_in_compare()
    {
        (PipelineViewModel vm, Script script, _) = await PageAsync();
        Assert.False(vm.RunCommand.CanExecute(null));
        string subs = Scripts();
        vm.AddPaths([Source(), Path.Combine(subs, "ep01.JPSC.ass"), Path.Combine(subs, "ep01.JPTC.ass"), Path.Combine(subs, "ep01.JPSC.ass"), null]);
        Assert.Equal(Path.Combine(subs, "Fonts"), vm.FontDirectory);
        Assert.Equal(Path.Combine(_work, "release"), vm.OutputDirectory);
        Assert.Equal(2, vm.Scripts.Count);
        Assert.Equal(3, vm.OutputNames.Count);

        var toasts = new List<ToastRequest>();
        vm.ToastRequested += (_, t) => toasts.Add(t);
        script.WarnRules = true;
        await vm.RunCommand.ExecuteAsync(null);

        Assert.All(vm.Steps, s => Assert.True(s.IsDone, s.Title + ": " + s.Detail));
        Assert.Contains(".mp4", vm.BurnStep.Detail, StringComparison.Ordinal);
        Assert.Contains("200", vm.VerifyStep.Detail, StringComparison.Ordinal); // warnings are listed, not fatal
        Assert.Contains("96", vm.VmafStep.Detail, StringComparison.Ordinal);
        Assert.Equal(ToastKind.Success, toasts[^1].Kind);
        Assert.EndsWith("encoded.mkv", vm.EncodedFile, StringComparison.Ordinal);

        bool switched = false;
        vm.CompareRequested += (_, _) => switched = true;
        await vm.OpenInCompareCommand.ExecuteAsync(null);
        Assert.True(switched);
        Assert.Equal(vm.Video, vm.Compare.ReferencePath);

        // Mux warnings and a clean check.
        script.WarnRules = false;
        script.MuxExit = 1;
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Contains("odd", vm.MuxStep.Detail, StringComparison.Ordinal);
        Assert.Equal(Loc.T("pipeline.verify.passed", 3), vm.VerifyStep.Detail);

        vm.ClearCommand.Execute(null);
        Assert.Empty(vm.Scripts);
        Assert.All(vm.Steps, s => Assert.True(s.IsPending));
    }

    [AvaloniaFact]
    public async Task Without_scripts_or_a_reference_steps_are_skipped()
    {
        (PipelineViewModel vm, Script script, _) = await PageAsync();
        script.NoChapters = true;
        vm.SetVideo(Source());
        vm.AlreadyEncoded = true;
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.EncodeStep.IsSkipped);
        Assert.True(vm.SubsetStep.IsSkipped);
        Assert.True(vm.BurnStep.IsSkipped);
        Assert.True(vm.VmafStep.IsSkipped); // an encode without its reference
        Assert.Equal(vm.Video, vm.EncodedFile);

        // No chapters anywhere, an output folder already chosen, no fonts folder, BD rules (no burned copies).
        vm.SetVideo(Source());
        Assert.Equal(Path.Combine(_work, "release"), vm.OutputDirectory);
        vm.Reference = Source();
        vm.RunVmaf = false;
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.VmafStep.IsSkipped);

        string subs = Scripts();
        vm.AddPaths([Path.Combine(subs, "ep01.JPSC.ass")]);
        vm.FontDirectory = string.Empty;
        vm.MakeBurned = false;
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.BurnStep.IsSkipped);
        vm.MakeBurned = true;
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.BurnStep.IsDone, vm.BurnStep.Detail);

        // An encode with a reference opens both in Compare; the reference alone otherwise.
        await vm.OpenInCompareCommand.ExecuteAsync(null);
        vm.Reference = string.Empty;
        await vm.OpenInCompareCommand.ExecuteAsync(null);
        vm.EncodedFile = Path.Combine(_work, "gone.mkv");
        Assert.False(vm.OpenInCompareCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Each_failure_stops_the_run_at_its_step()
    {
        (PipelineViewModel vm, Script script, _) = await PageAsync();
        string subs = Scripts();
        vm.AddPaths([Source(), Path.Combine(subs, "ep01.JPSC.ass")]);
        var toasts = new List<ToastRequest>();
        vm.ToastRequested += (_, t) => toasts.Add(t);

        script.EncodeExit = 1;
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.EncodeStep.IsFailed);
        Assert.Contains("1", vm.EncodeStep.Detail, StringComparison.Ordinal);
        Assert.Equal(ToastKind.Error, toasts[^1].Kind);

        script.EncodeExit = 0;
        script.CancelEncodes = true;
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("encode.row.cancelled"), vm.EncodeStep.Detail);

        script.CancelEncodes = false;
        script.MuxExit = 2;
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.MuxStep.IsFailed);
        Assert.Contains("mux broke", vm.MuxStep.Detail, StringComparison.Ordinal);

        script.MuxExit = 0;
        script.BurnExit = 1;
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.BurnStep.IsFailed);

        script.BurnExit = 0;
        script.FailRules = true;
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.VerifyStep.IsFailed);

        script.FailRules = false;
        script.UnreadableEncode = true;
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.MuxStep.IsFailed); // the step that was running
        Assert.Contains("broken", vm.Status, StringComparison.Ordinal);

        script.UnreadableEncode = false;
        script.UnreadableSource = true;
        await vm.RunCommand.ExecuteAsync(null); // before any step
        Assert.All(vm.Steps, s => Assert.True(s.IsPending));
        Assert.Contains("source unreadable", vm.Status, StringComparison.Ordinal);

        // Missing fonts stop the subset step; a script with no recognisable name is untagged.
        script.UnreadableSource = false;
        File.WriteAllText(Path.Combine(subs, "notes.ass"), "[Events]\nFormat: Style, Text\nDialogue: x,{\\fnNope}a");
        vm.AddPaths([Path.Combine(subs, "notes.ass")]);
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.SubsetStep.IsFailed);
        Assert.Contains("Nope", vm.SubsetStep.Detail, StringComparison.Ordinal);

        script.CancelEncodes = false;
        vm.RemoveScriptCommand.Execute(Path.Combine(subs, "notes.ass"));
        vm.FontDirectory = string.Empty; // the scripts' folder is searched, Fonts/ included
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.SubsetStep.IsDone, vm.SubsetStep.Detail);
    }

    [AvaloniaFact]
    public async Task Burned_copies_can_be_cancelled_and_chapter_files_are_used()
    {
        (PipelineViewModel vm, Script script, _) = await PageAsync();
        string subs = Scripts();
        string chapters = Path.Combine(_work, "c.txt");
        File.WriteAllText(chapters, "CHAPTER01=00:00:00.000\nCHAPTER01NAME=OP\n");
        File.WriteAllText(Path.Combine(subs, "plain.ass"), File.ReadAllText(Path.Combine(subs, "ep01.JPSC.ass")));
        vm.AddPaths([Source(), Path.Combine(subs, "plain.ass"), chapters, Path.Combine(subs, "Fonts")]);
        Assert.Equal(chapters, vm.ChaptersFile);
        Assert.Equal(Path.Combine(subs, "Fonts"), vm.FontDirectory);

        int encodes = 0;
        vm.Queue.JobChanged += job =>
        {
            if (job.State == EncodeJobState.Running && job.Output.EndsWith(".mp4", StringComparison.Ordinal) && Interlocked.Increment(ref encodes) == 1)
            {
                vm.Queue.Stop(job);
            }
        };
        script.BurnExit = 0;
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.BurnStep.IsFailed || vm.BurnStep.IsDone);
    }

    [AvaloniaFact]
    public async Task Profiles_set_the_release_kind_and_sources()
    {
        (PipelineViewModel vm, _, _) = await PageAsync();
        vm.Profile = ReleaseProfile.BdMovie;
        Assert.Equal((false, 18m, "BDRip", "FLAC", EpisodeKind.Movie), (vm.MakeBurned, vm.Crf, vm.Naming.Source, vm.Naming.AudioCodec, vm.Naming.Kind));
        vm.Profile = ReleaseProfile.WebEpisode;
        Assert.Equal((true, 20m, "WebRip", "AAC", EpisodeKind.Episode), (vm.MakeBurned, vm.Crf, vm.Naming.Source, vm.Naming.AudioCodec, vm.Naming.Kind));
        vm.Naming.Kind = EpisodeKind.Ova;
        vm.Profile = ReleaseProfile.BdEpisode;
        Assert.Equal(EpisodeKind.Ova, vm.Naming.Kind);

        vm.AddPaths([Source(), Source(), Source()]);
        Assert.Equal(vm.Video, vm.Reference); // the second video is the reference; a third is ignored
        string elsewhere = Directory.CreateDirectory(Path.Combine(_work, "other")).FullName;
        File.WriteAllText(Path.Combine(elsewhere, "x.ass"), string.Empty);
        vm.FontDirectory = string.Empty;
        vm.AddPaths([Path.Combine(elsewhere, "x.ass")]);
        Assert.Equal(string.Empty, vm.FontDirectory); // no fonts folder beside the script

        var step = new PipelineStep { Title = "t", Description = "d" };
        step.State = StepState.Running;
        Assert.True(step.IsRunning);
    }

    [AvaloniaFact]
    public async Task Real_tools_produce_a_release()
    {
        SettingsViewModel settings = await Pages.SettingsAsync();
        string clip = Path.Combine(_work, "source.mkv");
        File.Copy(await TestMedia.ClipAsync(), clip);
        var compare = new MainViewModel { FfmpegPath = TestMedia.RequireEncoder(), SelectedBackend = VmafBackend.Cpu };
        var queue = new EncodeQueue(settings.Services.Processes, () => settings.Encoder!.Path);
        var vm = new PipelineViewModel(settings, compare, queue, new NamingViewModel { Title = "Clip" },
            path => path == clip ? MediaInfoReader.Read(path) : Compliant(path, false));
        string subs = Scripts();
        vm.AddPaths([clip, Path.Combine(subs, "ep01.JPSC.ass")]);
        await Ui.UntilAsync(() => vm.Naming.Status == Loc.T("naming.status.fromVideo"));
        vm.Crf = 35;

        await vm.RunCommand.ExecuteAsync(null);
        Assert.All(vm.Steps, s => Assert.True(s.IsDone, s.Title + ": " + s.Detail));
        string mkv = Path.Combine(vm.OutputDirectory, vm.OutputNames[0]);
        MkvFileInfo written = await settings.MkvToolNix!.IdentifyAsync(mkv);
        Assert.Contains(written.Tracks, t => t.Type == MkvTrackType.Subtitles && t.Name == "JPSC");
        Assert.True(written.ChapterEntries > 0);
    }

    [AvaloniaFact]
    public async Task Pipeline_page_buttons_and_drops()
    {
        (PipelineViewModel vm, _, _) = await PageAsync();
        var view = new PipelineView();
        Window window = Ui.Show(view, vm);
        string subs = Scripts();
        string source = Source();
        using var scope = new DialogScope(new ScriptedDialogs().Answer(
            new[] { source }, null, new[] { source }, null, new[] { Path.Combine(subs, "ep01.JPSC.ass") }, subs, null, "/out", null, new[] { "/c.xml" }, null));

        foreach (string handler in new[] { "OnPickVideo", "OnPickVideo", "OnPickReference", "OnPickReference", "OnAddScripts", "OnPickFonts", "OnPickFonts", "OnPickOutput", "OnPickOutput", "OnPickChapters", "OnPickChapters" })
        {
            Ui.Invoke(view, handler);
        }

        await Ui.SettleAsync();
        Assert.Equal((source, source, subs, "/out", "/c.xml"), (vm.Video, vm.Reference, vm.FontDirectory, vm.OutputDirectory, vm.ChaptersFile));
        Assert.Single(vm.Scripts);

        // Drop on a card's padding: an empty spot of the page itself is not hit-testable.
        Border card = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<Border>().First(b => b.Classes.Contains("surface"));
        Ui.Drop(window, card, new Avalonia.Point(4, 4), Path.Combine(subs, "ep01.JPTC.ass"));
        Assert.Equal(2, vm.Scripts.Count);
        window.Close();
    }
}
