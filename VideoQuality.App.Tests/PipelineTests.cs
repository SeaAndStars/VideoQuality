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

        public int ExtractExit { get; set; }

        public string SourceContainer { get; set; } = "Matroska";

        /// <summary>The source MKV carries no subtitle tracks or attachments.</summary>
        public bool SourceBare { get; set; }
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
            if (request.Arguments[0] == "--gui-mode")
            {
                if (script.ExtractExit != 0)
                {
                    return new ProcessResult(script.ExtractExit, string.Empty, "extract broke");
                }

                foreach (string pair in request.Arguments.Skip(2).Where(a => a.Contains(':', StringComparison.Ordinal)))
                {
                    string output = pair[(pair.IndexOf(':', StringComparison.Ordinal) + 1)..];
                    if (output.EndsWith(".ass", StringComparison.Ordinal))
                    {
                        TestMedia.Script(Path.GetDirectoryName(output)!, Path.GetFileName(output));
                    }
                    else
                    {
                        File.Copy(Path.Combine(Fixtures, "DejaVuSans.ttf"), output, true);
                    }
                }

                return ScriptedRunner.Ok();
            }

            if (request.Arguments[0] == "-J" && request.Arguments[1].EndsWith("source.mkv", StringComparison.Ordinal))
            {
                return ScriptedRunner.Ok(script.SourceBare
                    ? ScriptedMkv.Identify(script.SourceContainer, 1440, 0, [(0, "video", "AVC", "", ""), (1, "audio", "AAC", "jpn", "")])
                    : ScriptedMkv.Identify(script.SourceContainer, 1440, 0,
                        [(0, "video", "AVC", "", ""), (1, "audio", "AAC", "jpn", ""), (2, "subtitles", "SubStationAlpha", "chi", "简/日"), (3, "subtitles", "SubStationAlpha", "", "")],
                        [(1, "a.ttf", "application/x-truetype-font"), (2, "b.OTF", "application/octet-stream"), (3, "cover.jpg", "image/jpeg")]));
            }

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

    /// <summary>Every step ran, except Extract (scripts and fonts were given) and Trim (off by default).</summary>
    private static void AssertRan(PipelineViewModel vm)
    {
        Assert.Equal((Loc.T("pipeline.extract.notNeeded"), Loc.T("pipeline.step.off")), (vm.ExtractStep.Detail, vm.TrimStep.Detail));
        Assert.All(vm.Steps.Where(s => s.Key is not ("extract" or "trim")), s => Assert.True(s.IsDone, s.Title + ": " + s.Detail));
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
        await vm.Inference;
        Assert.Equal(Path.Combine(subs, "Fonts"), vm.FontDirectory);
        Assert.Equal(Path.Combine(_work, "release"), vm.OutputDirectory);
        Assert.Equal(2, vm.Scripts.Count);
        Assert.Equal(3, vm.OutputNames.Count);

        var toasts = new List<ToastRequest>();
        vm.ToastRequested += (_, t) => toasts.Add(t);
        script.WarnRules = true;
        await vm.RunCommand.ExecuteAsync(null);

        AssertRan(vm);
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
        vm.ExtractStep.Enabled = false;
        vm.SetVideo(Source());
        await vm.Inference;
        vm.AlreadyEncoded = true;
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.EncodeStep.IsSkipped);
        Assert.True(vm.SubsetStep.IsSkipped);
        Assert.True(vm.BurnStep.IsSkipped);
        Assert.True(vm.VmafStep.IsSkipped); // an encode without its reference
        Assert.Equal(vm.Video, vm.EncodedFile);

        // No chapters anywhere, an output folder already chosen, no fonts folder, BD rules (no burned copies).
        vm.SetVideo(Source());
        await vm.Inference;
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
        await vm.Inference;
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
        await vm.Inference;
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
        await vm.Inference;
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
    public async Task Nvenc_encodes_every_file_and_written_files_can_be_revealed()
    {
        (PipelineViewModel vm, _, EncodeQueue queue) = await PageAsync();
        vm.Encoder = PipelineEncoder.NvencUhq;
        Assert.Equal(((decimal)NvencUhq.Quality, true), (vm.Crf, vm.IsNvenc));
        vm.Profile = ReleaseProfile.BdEpisode;
        vm.Profile = ReleaseProfile.WebEpisode;
        Assert.Equal((decimal)NvencUhq.Quality, vm.Crf); // the release kind keeps NVENC's quality

        string subs = Scripts();
        vm.AddPaths([Source(), Path.Combine(subs, "ep01.JPSC.ass"), Path.Combine(subs, "ep01.JPTC.ass")]);
        await vm.Inference;
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal(3, queue.Jobs.Count);
        Assert.All(queue.Jobs, j => Assert.Contains("hevc_nvenc", j.Command.Passes[^1]));
        Assert.Equal([".mkv", ".mp4", ".mp4"], vm.Outputs.Select(Path.GetExtension));

        vm.RevealCommand.Execute(vm.Outputs[0]); // nobody listening
        string? revealed = null;
        vm.RevealRequested += (_, path) => revealed = path;
        vm.RevealCommand.Execute(vm.Outputs[0]);
        Assert.Equal(vm.Outputs[0], revealed);

        vm.Encoder = PipelineEncoder.X265;
        Assert.Equal((20m, false), (vm.Crf, vm.IsNvenc));
        vm.ClearCommand.Execute(null);
        Assert.Empty(vm.Outputs);
    }

    [AvaloniaFact]
    public async Task A_source_fills_in_scripts_fonts_chapters_names_and_the_release_kind()
    {
        (PipelineViewModel vm, Script script, _) = await PageAsync();
        string folder = Directory.CreateDirectory(Path.Combine(_work, "ep")).FullName;
        string video = Path.Combine(folder, "Silent Witch 05.mkv");
        File.WriteAllText(video, "raw");
        File.WriteAllText(Path.Combine(folder, "Silent Witch 05 JPTC.ass"), string.Empty);
        File.WriteAllText(Path.Combine(folder, "Silent Witch 05 JPSC.ass"), string.Empty);
        File.WriteAllText(Path.Combine(folder, "Other 06 JPSC.ass"), string.Empty);
        File.WriteAllText(Path.Combine(folder, "Silent Witch 05.xml"), "<Chapters />");
        Directory.CreateDirectory(Path.Combine(folder, "Fonts"));

        script.UnreadableSource = true; // only the name to go on
        vm.SetVideo(video);
        await vm.Inference;
        Assert.Equal(2, vm.Scripts.Count);
        Assert.Equal(Path.Combine(folder, "Fonts"), vm.FontDirectory);
        Assert.Equal(Path.Combine(folder, "Silent Witch 05.xml"), vm.ChaptersFile);
        Assert.Equal(("Silent Witch", 5m, ReleaseProfile.WebEpisode), (vm.Naming.Title, vm.Naming.Episode, vm.Profile));
        Assert.Equal(["JPSC", "JPTC"], vm.Burns.Select(b => b.Language)); // tagged by the scripts' names, in name order
        Assert.Equal(3, vm.OutputNames.Count);
        Assert.EndsWith("[JPTC].mp4", vm.Burns[1].FileName, StringComparison.Ordinal);

        // Nothing is replaced once chosen; a lossless track means a disc, a long nameless one a movie.
        vm.Burns.Clear();
        await vm.InferAsync(video);
        Assert.Empty(vm.Burns);
        string disc = Path.Combine(_work, "source.mkv");
        File.WriteAllText(disc, "raw");
        script.UnreadableSource = false;
        await vm.InferAsync(disc);
        Assert.Equal(ReleaseProfile.WebEpisode, vm.Profile); // AAC, 24 minutes
        string flac = Path.Combine(folder, "Concert [Hi10p].mkv");
        File.WriteAllText(flac, "raw");
        var bd = new PipelineViewModel(vm.Settings, vm.Compare, vm.Queue, new NamingViewModel(_ => new MediaInfo { Path = "x" }), path => path.Contains("Mute", StringComparison.Ordinal) ? new MediaInfo { Path = path } : new MediaInfo
        {
            Path = path,
            Duration = TimeSpan.FromMinutes(110),
            Streams = [new MediaStream { Index = 0, Kind = StreamKind.Audio, Codec = "flac" }],
        });
        await bd.InferAsync(flac);
        Assert.Equal((ReleaseProfile.BdMovie, EpisodeKind.Movie, "Concert"), (bd.Profile, bd.Naming.Kind, bd.Naming.Title));
        await bd.InferAsync(Path.Combine(folder, "[WebRip] Concert 03.mkv"));
        Assert.Equal(ReleaseProfile.WebEpisode, bd.Profile); // the name says web: a FLAC track does not make it a disc
        await bd.InferAsync(Path.Combine(folder, "No Title Here.mkv"));
        Assert.Equal(ReleaseProfile.BdMovie, bd.Profile); // no number: the length decides
        await bd.InferAsync(Path.Combine(folder, "Show [BDRip] 04.mkv"));
        Assert.Equal(ReleaseProfile.BdEpisode, bd.Profile);
        await bd.InferAsync(Path.Combine(folder, "Mute The Movie.mkv"));
        Assert.Equal(ReleaseProfile.WebMovie, bd.Profile); // named a movie, whatever its length
        await bd.InferAsync(Path.Combine(folder, "Mute clip.mkv"));
        Assert.Equal(ReleaseProfile.WebEpisode, bd.Profile); // no audio and no length: web, an episode
        script.UnreadableSource = true;
        await vm.InferAsync(Path.Combine(_work, "source.mkv"));
        Assert.Equal(ReleaseProfile.WebEpisode, vm.Profile); // unreadable and nameless

        // A release file name fills the form as it is.
        string release = Path.Combine(folder, "[Studio GreenTea] Show [02][BDRip][HEVC-10bit 1080p FLAC ASSx2].mkv");
        await bd.InferAsync(release);
        Assert.Equal(("Show", 2m, ReleaseProfile.BdEpisode), (bd.Naming.Title, bd.Naming.Episode, bd.Profile));
        await bd.InferAsync(Path.Combine(folder, "[Studio GreenTea] Film [Movie][WebRip][HEVC-10bit 1080p AAC ASSx2].mkv"));
        Assert.Equal(ReleaseProfile.WebMovie, bd.Profile);
        Assert.Equal(Loc.T("naming.status.parsed"), bd.Status);
    }

    [AvaloniaFact]
    public async Task Outputs_and_steps_can_each_be_changed()
    {
        (PipelineViewModel vm, _, EncodeQueue queue) = await PageAsync();
        Assert.False(vm.AddBurnTargetCommand.CanExecute(null));
        string subs = Scripts();
        string jpsc = Path.Combine(subs, "ep01.JPSC.ass");
        string jptc = Path.Combine(subs, "ep01.JPTC.ass");
        vm.AddPaths([Source(), jpsc]);
        await vm.Inference;
        Assert.Equal(["JPSC"], vm.Burns.Select(b => b.Language));

        // One MP4 with JPSC, then a JPTC one added freely from the same script, and a plain SC/TC pair.
        vm.AddBurnTargetCommand.Execute(null);
        Assert.Equal((jpsc, "JPTC"), (vm.Burns[1].Script, vm.Burns[1].Language));
        vm.AddBurnTargetCommand.Execute(null);
        vm.AddBurnTargetCommand.Execute(null);
        vm.AddBurnTargetCommand.Execute(null); // every tag in use: back to the first
        Assert.Equal(["JPSC", "JPTC", "SC", "TC", "JPSC"], vm.Burns.Select(b => b.Language));
        Assert.EndsWith("[SC].mp4", vm.Burns[2].FileName, StringComparison.Ordinal);
        Assert.EndsWith("[TC].mp4", vm.Burns[3].FileName, StringComparison.Ordinal);
        vm.RemoveBurnCommand.Execute(vm.Burns[4]);
        vm.RemoveBurnCommand.Execute(vm.Burns[3]);
        vm.RemoveBurnCommand.Execute(vm.Burns[2]);
        vm.Burns[1].Language = "TC";
        Assert.EndsWith("[TC].mp4", vm.Burns[1].FileName, StringComparison.Ordinal);
        vm.Burns[1].Language = "JPTC";

        // A second script gets its own MP4 unless one already burns it; a new MP4 prefers a script without one.
        vm.AddPaths([jptc]);
        Assert.Equal(3, vm.Burns.Count);
        vm.RemoveBurnCommand.Execute(vm.Burns[2]);
        vm.AddBurnTargetCommand.Execute(null);
        Assert.Equal(jptc, vm.Burns[2].Script);
        vm.RemoveScriptCommand.Execute(jptc);
        Assert.Equal(2, vm.Burns.Count);

        // Scripts that name no Chinese tag get the first free one.
        string plain = Path.Combine(subs, "plain.ass");
        string japanese = Path.Combine(subs, "ep01.ja.ass");
        File.WriteAllText(plain, string.Empty);
        File.WriteAllText(japanese, string.Empty);
        vm.AddPaths([plain, japanese]);
        Assert.Equal(["SC", "TC"], vm.Burns.Skip(2).Select(b => b.Language));
        vm.RemoveBurnCommand.Execute(vm.Burns[3]);
        vm.RemoveScriptCommand.Execute(japanese); // no MP4 burns it any more
        vm.RemoveScriptCommand.Execute(plain);
        Assert.Equal(2, vm.Burns.Count);

        // MKV off: only the two MP4s are made, checked and listed.
        vm.MakeMkv = false;
        Assert.Equal(2, vm.OutputNames.Count);
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.MuxStep.IsSkipped);
        Assert.Equal(["[JPSC].mp4", "[JPTC].mp4"], vm.Outputs.Select(o => o[^10..]));
        Assert.True(vm.VerifyStep.IsDone, vm.VerifyStep.Detail);

        // Subset off: the MKV carries the whole font folder. Verify off, burn off.
        vm.MakeMkv = true;
        vm.SubsetStep.Enabled = false;
        vm.VerifyStep.Enabled = false;
        vm.MakeBurned = false;
        Assert.Single(vm.OutputNames);
        int jobs = queue.Jobs.Count;
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal((true, true, true, Loc.T("pipeline.step.off")), (vm.SubsetStep.IsSkipped, vm.VerifyStep.IsSkipped, vm.BurnStep.IsSkipped, vm.BurnStep.Detail));
        Assert.True(vm.MuxStep.IsDone, vm.MuxStep.Detail);
        Assert.Equal(jobs + 1, queue.Jobs.Count); // the encode only

        // Nothing to check: no MKV and no MP4s.
        vm.VerifyStep.Enabled = true;
        vm.MakeMkv = false;
        vm.FontDirectory = Path.Combine(_work, "missing");
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("pipeline.verify.nothing"), vm.VerifyStep.Detail);
        vm.MakeMkv = true;
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.MuxStep.IsDone, vm.MuxStep.Detail); // no font folder: no fonts
        vm.ClearCommand.Execute(null);
        Assert.Empty(vm.Burns);
    }

    [AvaloniaFact]
    public async Task Extract_pulls_the_sources_scripts_and_fonts()
    {
        (PipelineViewModel vm, Script script, _) = await PageAsync();
        vm.SetVideo(Source());
        await vm.Inference;
        Assert.Empty(vm.Scripts);
        script.ExtractExit = 2;
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal((true, "extract broke"), (vm.ExtractStep.IsFailed, vm.ExtractStep.Detail));

        // Both ASS tracks and the two fonts (by type and by extension; not the cover).
        script.ExtractExit = 0;
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("pipeline.extract.done", 2, 2), vm.ExtractStep.Detail);
        Assert.Equal(["source.2.简_日.ass", "source.3.sub.ass"], vm.Scripts.Select(Path.GetFileName));
        Assert.Equal(["JPSC", "JPTC"], vm.Burns.Select(b => b.Language));
        Assert.EndsWith(Path.Combine("extract", "Fonts"), vm.FontDirectory, StringComparison.Ordinal);
        AssertRanFromExtract(vm);

        // Given now: nothing to pull. Scripts only, from a bare MKV: nothing there. Not Matroska: skipped.
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("pipeline.extract.notNeeded"), vm.ExtractStep.Detail);
        vm.ClearCommand.Execute(null);
        vm.SetVideo(Source());
        await vm.Inference;
        vm.ExtractFonts = false;
        script.SourceBare = true;
        vm.SubsetStep.Enabled = vm.MuxStep.Enabled = vm.BurnStep.Enabled = false;
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("pipeline.extract.nothing"), vm.ExtractStep.Detail);
        script.SourceContainer = "QuickTime/MP4";
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("pipeline.extract.notMatroska"), vm.ExtractStep.Detail);
        vm.ExtractScripts = false;
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("pipeline.extract.notNeeded"), vm.ExtractStep.Detail);
        vm.ExtractStep.Enabled = false;
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("pipeline.step.off"), vm.ExtractStep.Detail);
    }

    private static void AssertRanFromExtract(PipelineViewModel vm) =>
        Assert.All(vm.Steps.Where(s => s.Key != "trim"), s => Assert.True(s.IsDone, s.Title + ": " + s.Detail));

    [AvaloniaFact]
    public async Task Trim_chapters_encoder_mux_verify_and_vmaf_settings_change_the_run()
    {
        (PipelineViewModel vm, Script script, EncodeQueue queue) = await PageAsync();
        string subs = Scripts();
        string jpsc = Path.Combine(subs, "ep01.JPSC.ass");
        string jptc = Path.Combine(subs, "ep01.JPTC.ass");
        vm.AddPaths([Source(), jpsc, jptc]);
        await vm.Inference;
        string Command(int fromEnd) => string.Join(' ', queue.Jobs[^fromEnd].Command.Passes[^1]);

        // Trim: a cut moves the chapters and every encode; VMAF can't line it up with the full source.
        vm.TrimStep.Enabled = true;
        vm.TrimStart = "00:01:00.000";
        vm.TrimEnd = "00:23:00.000";
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("pipeline.trim.done", "00:01:00.000", "00:23:00.000"), vm.TrimStep.Detail);
        Assert.All([1, 2, 3], n => Assert.Contains("-ss 60 ", Command(n), StringComparison.Ordinal));
        Assert.Equal(Loc.T("pipeline.vmaf.trimmed"), vm.VmafStep.Detail);
        Assert.Equal("OP", vm.ChaptersStep.Detail); // the opening that runs at the cut's start
        vm.TrimStart = " ";
        vm.TrimEnd = string.Empty;
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("pipeline.trim.done", "00:00:00.000", "00:24:00.000"), vm.TrimStep.Detail);
        vm.TrimEnd = "00:00:00.000";
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.TrimStep.IsFailed);
        vm.TrimEnd = "soon";
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.TrimStep.IsFailed);
        vm.TrimStep.Enabled = false;

        // Chapters: as they are, or none at all.
        vm.ChaptersStep.Enabled = false;
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("pipeline.chapters.asIs", 1), vm.ChaptersStep.Detail);
        vm.ChaptersStep.Enabled = true;
        vm.DetectSongs = false;
        script.NoChapters = true;
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("pipeline.chapters.none"), vm.ChaptersStep.Detail);
        script.NoChapters = false;

        // Encoder: an Encode page preset's picture settings, frame rate kept by the rules.
        vm.Encoder = PipelineEncoder.Preset;
        Assert.Equal(PresetStore.BuiltIn[0], vm.SelectedPreset);
        vm.SelectedPreset = PresetStore.BuiltIn.First(p => p.Name == "x264 compatible MP4");
        Assert.Contains("x264 compatible MP4", vm.EncodeStep.Description, StringComparison.Ordinal);
        Assert.True(vm.IsPreset);

        // Mux: a title of its own and the second script as the default track. Verify: report, go on.
        vm.MkvTitle = " My title ";
        vm.DefaultScript = jptc;
        vm.VerifyStrict = false;
        script.FailRules = true;
        vm.Reference = Source();
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Contains("-c:v libx264", Command(3), StringComparison.Ordinal);
        Assert.Contains("-r 24000/1001", Command(3), StringComparison.Ordinal);
        Assert.True(vm.VerifyStep.IsDone, vm.VerifyStep.Detail);
        Assert.True(vm.VmafStep.IsDone, vm.VmafStep.Detail);
        Assert.Equal(vm.Reference, vm.VmafReference);
        vm.Encoder = PipelineEncoder.X265;
        Assert.Equal(Loc.T("pipeline.step.encode.description"), vm.EncodeStep.Description);
        Assert.Equal(vm.Naming.Set()[0].Title, vm.ReleaseTitle);
    }

    [AvaloniaFact]
    public async Task Real_tools_produce_a_release()
    {
        SettingsViewModel settings = await Pages.SettingsAsync();
        string clip = Path.Combine(_work, "source.mkv");
        File.Copy(await TestMedia.ClipAsync(), clip);
        // Scored with an ffmpeg that has libvmaf (the encoder lookup does not promise one).
        var compare = new MainViewModel { FfmpegPath = await VmafRunner.ResolveFfmpegAsync(), SelectedBackend = VmafBackend.Cpu };
        var queue = new EncodeQueue(settings.Services.Processes, () => settings.Encoder!.Path);
        var vm = new PipelineViewModel(settings, compare, queue, new NamingViewModel { Title = "Clip" },
            path => path == clip ? MediaInfoReader.Read(path) : Compliant(path, false));
        string subs = Scripts();
        vm.AddPaths([clip, Path.Combine(subs, "ep01.JPSC.ass")]);
        await vm.Inference;
        await vm.Inference;
        vm.Crf = 35;

        await vm.RunCommand.ExecuteAsync(null);
        AssertRan(vm);
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
