using VideoQuality.Core.Mkv;
using VideoQuality.Core.Tools;

namespace VideoQuality.Tests;

public class MkvCoverageTests
{
    [Theory]
    [InlineData("V_MPEG4/ISO/AVC", MkvTrackType.Video, ".h264")]
    [InlineData("V_MPEGH/ISO/HEVC", MkvTrackType.Video, ".hevc")]
    [InlineData("V_AV1", MkvTrackType.Video, ".ivf")]
    [InlineData("V_VP8", MkvTrackType.Video, ".ivf")]
    [InlineData("V_VP9", MkvTrackType.Video, ".ivf")]
    [InlineData("V_MPEG2", MkvTrackType.Video, ".m2v")]
    [InlineData("V_MPEG1", MkvTrackType.Video, ".m2v")]
    [InlineData("A_AAC", MkvTrackType.Audio, ".aac")]
    [InlineData("A_AAC/MPEG4/LC", MkvTrackType.Audio, ".aac")]
    [InlineData("A_AAC/MPEG2/LC", MkvTrackType.Audio, ".aac")]
    [InlineData("A_FLAC", MkvTrackType.Audio, ".flac")]
    [InlineData("A_OPUS", MkvTrackType.Audio, ".opus")]
    [InlineData("A_VORBIS", MkvTrackType.Audio, ".ogg")]
    [InlineData("A_AC3", MkvTrackType.Audio, ".ac3")]
    [InlineData("A_EAC3", MkvTrackType.Audio, ".eac3")]
    [InlineData("A_DTS", MkvTrackType.Audio, ".dts")]
    [InlineData("A_TRUEHD", MkvTrackType.Audio, ".thd")]
    [InlineData("A_MPEG/L3", MkvTrackType.Audio, ".mp3")]
    [InlineData("A_MPEG/L2", MkvTrackType.Audio, ".mp2")]
    [InlineData("A_PCM/INT/LIT", MkvTrackType.Audio, ".wav")]
    [InlineData("A_PCM/INT/BIG", MkvTrackType.Audio, ".wav")]
    [InlineData("A_PCM/FLOAT/IEEE", MkvTrackType.Audio, ".wav")]
    [InlineData("S_TEXT/ASS", MkvTrackType.Subtitles, ".ass")]
    [InlineData("S_ASS", MkvTrackType.Subtitles, ".ass")]
    [InlineData("S_TEXT/SSA", MkvTrackType.Subtitles, ".ssa")]
    [InlineData("S_SSA", MkvTrackType.Subtitles, ".ssa")]
    [InlineData("S_TEXT/UTF8", MkvTrackType.Subtitles, ".srt")]
    [InlineData("S_TEXT/WEBVTT", MkvTrackType.Subtitles, ".vtt")]
    [InlineData("S_HDMV/PGS", MkvTrackType.Subtitles, ".sup")]
    [InlineData("S_VOBSUB", MkvTrackType.Subtitles, ".sub")]
    [InlineData("V_X", MkvTrackType.Video, ".mkv")]
    [InlineData("A_X", MkvTrackType.Audio, ".mka")]
    [InlineData("S_X", MkvTrackType.Subtitles, ".mks")]
    [InlineData("B_X", MkvTrackType.Other, ".bin")]
    public void Every_codec_gets_its_extract_extension(string codecId, MkvTrackType type, string extension) =>
        Assert.Equal(extension, MkvExtensions.ForCodec(codecId, type));

    [Theory]
    [InlineData("a.TTF", "font/ttf")]
    [InlineData("a.otf", "font/otf")]
    [InlineData("a.ttc", "font/collection")]
    [InlineData("a.otc", "font/collection")]
    [InlineData("a.woff", "font/woff")]
    [InlineData("a.woff2", "font/woff2")]
    [InlineData("a.bin", "application/octet-stream")]
    public void Font_mime_types(string file, string mime) => Assert.Equal(mime, MkvToolNix.FontMimeType(file));

    [Fact]
    public void Identification_handles_sparse_and_failing_output()
    {
        Assert.Contains("broken; worse", Assert.Throws<InvalidDataException>(() => MkvFileInfo.Parse("""{"errors":["broken","worse"],"container":{}}""")).Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => MkvFileInfo.Parse("""{"errors":[],"container":{"recognized":false}}"""));
        Assert.Throws<InvalidDataException>(() => MkvFileInfo.Parse("""{"container":{}}"""));

        MkvFileInfo sparse = MkvFileInfo.Parse("""
            {"container":{"recognized":true},"file_name":"x.mkv",
             "tracks":[{"id":0,"type":"buttons","codec":"VobBtn","properties":{"codec_id":7,"default_track":false}},
                       {"id":1,"type":"audio","codec":"AAC","properties":{}}],
             "attachments":[{"id":1,"file_name":"f.ttf","content_type":"font/ttf"}]}
            """);
        Assert.Equal((0d, string.Empty, string.Empty, 0), (sparse.DurationSeconds, sparse.Title, sparse.Container, sparse.ChapterEntries));
        Assert.Equal(MkvTrackType.Other, sparse.Tracks[0].Type);
        Assert.Equal(string.Empty, sparse.Tracks[0].CodecId);
        Assert.Equal(string.Empty, sparse.Tracks[1].Details);
        Assert.Equal(0, sparse.Attachments[0].Size);

        MkvFileInfo full = MkvFileInfo.Parse("""
            {"container":{"recognized":true,"type":"Matroska","properties":{"duration":"x","title":"T"}},
             "tracks":[{"id":0,"type":"audio","codec":"AAC","properties":{"audio_channels":2,"language":"jpn","forced_track":true}}],
             "chapters":[{"num_entries":2},{"num_entries":3},{}]}
            """);
        Assert.Equal((0d, "T", 5), (full.DurationSeconds, full.Title, full.ChapterEntries));
        Assert.Equal(("2 ch", "jpn", true), (full.Tracks[0].Details, full.Tracks[0].Language, full.Tracks[0].Forced));
    }

    private static readonly MkvFileInfo TwoOfEach = new("in.mkv", "Matroska", 10, string.Empty,
    [
        new MkvTrack(0, MkvTrackType.Video, "AVC", "V_MPEG4/ISO/AVC", "und", string.Empty, true, false, string.Empty),
        new MkvTrack(1, MkvTrackType.Audio, "AAC", "A_AAC", "jpn", string.Empty, true, false, string.Empty),
        new MkvTrack(2, MkvTrackType.Audio, "AAC", "A_AAC", "eng", string.Empty, false, false, string.Empty),
        new MkvTrack(3, MkvTrackType.Subtitles, "ASS", "S_TEXT/ASS", "zh", "JPSC", true, false, string.Empty),
    ], [], 0);

    [Fact]
    public void Mux_selects_some_all_or_none_of_each_type_and_sets_flags()
    {
        var plan = new MuxPlan("out.mkv",
        [
            new MuxInput("in.mkv", [new MuxTrack(0), new MuxTrack(1, "", null, Forced: true), new MuxTrack(3, "zh", "JPSC", true, false)], KeepChapters: false),
            new MuxInput("unidentified.mkv", [new MuxTrack(0)]),
            new MuxInput("all.mkv"),
        ], Chapters: "ch.xml", Title: " ");

        IReadOnlyList<string> args = MkvToolNix.BuildMuxArguments(plan, new Dictionary<string, MkvFileInfo> { ["in.mkv"] = TwoOfEach });

        Assert.Equal(
        [
            "--gui-mode", "--output", "out.mkv",
            "--audio-tracks", "1",
            "--forced-display-flag", "1:yes",
            "--language", "3:zh", "--track-name", "3:JPSC", "--default-track-flag", "3:yes", "--forced-display-flag", "3:no",
            "--no-chapters", "in.mkv",
            "unidentified.mkv",
            "all.mkv",
            "--chapters", "ch.xml",
        ], args);

        IReadOnlyList<string> none = MkvToolNix.BuildMuxArguments(
            new MuxPlan("o.mkv", [new MuxInput("in.mkv", [new MuxTrack(3)])]),
            new Dictionary<string, MkvFileInfo> { ["in.mkv"] = TwoOfEach });
        Assert.Equal(["--gui-mode", "--output", "o.mkv", "--no-video", "--no-audio", "in.mkv"], none);
    }

    [Fact]
    public void Extract_arguments_without_tracks_or_attachments()
    {
        Assert.Equal(["--gui-mode", "in.mkv"], MkvToolNix.BuildExtractArguments("in.mkv", [], [], null));
    }

    [Fact]
    public void Run_results_succeed_on_zero_and_warnings()
    {
        Assert.True(new MkvRunResult(0, [], []).Succeeded);
        Assert.True(new MkvRunResult(1, ["w"], []).Succeeded);
        Assert.False(new MkvRunResult(2, [], ["e"]).Succeeded);
    }

    private static ScriptedRunner MkvTools(Func<ProcessRequest, ProcessResult>? run = null) => new(request =>
        request.Arguments.Contains("--version")
            ? ScriptedRunner.Ok("mkvmerge v97.0 ('You Don't Have A Clue') 64-bit\n")
            : run?.Invoke(request) ?? ScriptedRunner.Ok("{}"));

    [Fact]
    public async Task Locate_skips_missing_folders_unanswered_programs_and_impostors()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string empty = Directory.CreateTempSubdirectory("vq-mkv-none-").FullName;
        var impostor = new ScriptedRunner(_ => ScriptedRunner.Ok("something else"));
        var failing = new ScriptedRunner(_ => new ProcessResult(2, "mkvmerge broken", string.Empty));
        var missing = new ScriptedRunner(request => throw new ProcessStartException(request.FileName, new FileNotFoundException()));

        Assert.Null(await MkvToolNix.LocateAsync(empty + "-gone", missing, token));
        Assert.Null(await MkvToolNix.LocateAsync(empty, impostor, token));
        Assert.Null(await MkvToolNix.LocateAsync(empty, failing, token));

        MkvToolNix found = (await MkvToolNix.LocateAsync(empty, MkvTools(), token))!;
        Assert.Equal(Path.Combine(empty, OperatingSystem.IsWindows() ? "mkvmerge.exe" : "mkvmerge"), found.MkvMerge);
        Assert.Equal("mkvmerge v97.0 ('You Don't Have A Clue') 64-bit", found.Version);
        Directory.Delete(empty);
    }

    [Fact]
    public async Task Identify_reports_mkvmerge_errors_and_parses_json()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string folder = Directory.CreateTempSubdirectory("vq-mkv-id-").FullName;
        MkvToolNix tools = (await MkvToolNix.LocateAsync(folder, MkvTools(request => request.Arguments[^1] switch
        {
            "bad.mkv" => new ProcessResult(2, "Error: the file could not be opened", string.Empty),
            "json-error.mkv" => new ProcessResult(2, """{"errors":["unsupported"],"container":{}}""", string.Empty),
            "stderr.mkv" => new ProcessResult(2, string.Empty, "Error: on stderr"),
            _ => ScriptedRunner.Ok(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mkvmerge-identify.json"))),
        }), token))!;

        Assert.Equal("Error: the file could not be opened", (await Assert.ThrowsAsync<InvalidDataException>(() => tools.IdentifyAsync("bad.mkv", token))).Message);
        Assert.Contains("unsupported", (await Assert.ThrowsAsync<InvalidDataException>(() => tools.IdentifyAsync("json-error.mkv", token))).Message, StringComparison.Ordinal);
        Assert.Equal("Error: on stderr", (await Assert.ThrowsAsync<InvalidDataException>(() => tools.IdentifyAsync("stderr.mkv", token))).Message);
        Assert.Equal("Sample Episode", (await tools.IdentifyAsync("ok.mkv", token)).Title);
        Directory.Delete(folder);
    }

    [Fact]
    public async Task Runs_collect_progress_warnings_and_errors()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string folder = Directory.CreateTempSubdirectory("vq-mkv-run-").FullName;
        string? optionFile = null;
        MkvToolNix tools = (await MkvToolNix.LocateAsync(folder, MkvTools(request =>
        {
            ScriptedRunner.Emit(request, "#GUI#progress 50%", "#GUI#warning odd", "#GUI#error bad", "plain");
            optionFile = request.Arguments[0].TrimStart('@');
            Assert.True(request.Arguments[0].StartsWith('@') ? File.Exists(optionFile) : true);
            return new ProcessResult(2, string.Empty, " stderr text \n");
        }), token))!;

        var progress = new List<double>();
        MkvRunResult mux = await tools.MuxAsync(["--output", "x.mkv"], new SyncProgress<double>(progress.Add), token);
        Assert.Equal([0.5], progress);
        Assert.Equal(["odd"], mux.Warnings);
        Assert.Equal(["bad", "stderr text"], mux.Errors);
        Assert.False(File.Exists(optionFile));

        MkvRunResult extract = await tools.ExtractAsync(["in.mkv", "tracks", "0:x.h264"], null, token);
        Assert.Equal(2, extract.ExitCode);
        Directory.Delete(folder);
    }
}
