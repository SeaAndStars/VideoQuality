using System.Diagnostics;
using VideoQuality.Core.Mkv;

namespace VideoQuality.Tests;

public class MkvToolNixTests
{
    private static MkvFileInfo Fixture() =>
        MkvFileInfo.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mkvmerge-identify.json")));

    [Fact]
    public void Identification_lists_tracks_languages_names_and_attachments()
    {
        MkvFileInfo info = Fixture();

        Assert.Equal("Sample Episode", info.Title);
        Assert.Equal(3, info.Tracks.Count);
        MkvTrack subs = info.Tracks.Single(t => t.Type == MkvTrackType.Subtitles);
        Assert.Equal(("S_TEXT/ASS", "zh-Hans", "简体中文", true, ".ass"), (subs.CodecId, subs.Language, subs.Name, subs.Default, subs.Extension));
        Assert.Equal(".h264", info.Tracks.Single(t => t.Type == MkvTrackType.Video).Extension);
        Assert.Equal(".aac", info.Tracks.Single(t => t.Type == MkvTrackType.Audio).Extension);
        Assert.Equal(("DejaVuSans.ttf", "font/ttf"), (info.Attachments.Single().FileName, info.Attachments.Single().MimeType));
        Assert.True(info.DurationSeconds > 9);
    }

    [Fact]
    public void Mux_arguments_select_tracks_and_set_metadata_per_track()
    {
        MkvFileInfo info = Fixture();
        var plan = new MuxPlan(
            "out.mkv",
            [new MuxInput("in.mkv", [new MuxTrack(0), new MuxTrack(2, "zh-Hant", "繁體中文", Default: false)], KeepAttachments: false)],
            Attachments: ["fonts/ABCDEFGH.otf"],
            Title: "Ep 01");

        IReadOnlyList<string> args = MkvToolNix.BuildMuxArguments(plan, new Dictionary<string, MkvFileInfo> { ["in.mkv"] = info });

        Assert.Equal(
            ["--gui-mode", "--output", "out.mkv", "--title", "Ep 01",
             "--no-audio",
             "--language", "2:zh-Hant", "--track-name", "2:繁體中文", "--default-track-flag", "2:no",
             "--no-attachments", "in.mkv",
             "--attachment-mime-type", "font/otf", "--attach-file", "fonts/ABCDEFGH.otf"],
            args);
    }

    [Fact]
    public void Extract_arguments_group_tracks_attachments_and_chapters()
    {
        IReadOnlyList<string> args = MkvToolNix.BuildExtractArguments(
            "in.mkv", [(2, "out/subs.ass")], [(1, "out/font.ttf")], "out/chapters.xml");

        Assert.Equal(["--gui-mode", "in.mkv", "tracks", "2:out/subs.ass", "attachments", "1:out/font.ttf", "chapters", "out/chapters.xml"], args);
    }

    [Theory]
    [InlineData("#GUI#progress 42%", 0.42, null, null)]
    [InlineData("#GUI#warning Track 2: something odd", null, "Track 2: something odd", null)]
    [InlineData("#GUI#error The file could not be opened", null, null, "The file could not be opened")]
    [InlineData("Progress: 42%", null, null, null)]
    public void Gui_mode_lines_are_parsed(string line, double? progress, string? warning, string? error)
    {
        Assert.Equal((progress, warning, error), MkvToolNix.ParseGuiLine(line));
    }

    [Fact]
    public async Task Mux_then_extract_round_trips_subtitles_and_fonts()
    {
        string? video = Environment.GetEnvironmentVariable("VQ_TEST_VIDEO");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(video) || !File.Exists(video), "set VQ_TEST_VIDEO to a local clip");
        MkvToolNix? tools = await MkvToolNix.LocateAsync();
        Assert.SkipWhen(tools is null, "mkvtoolnix is not installed");
        const string Font = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
        Assert.SkipUnless(File.Exists(Font), "DejaVu not installed");

        string root = Directory.CreateTempSubdirectory("vq-mkv-").FullName;
        try
        {
            string ass = Path.Combine(root, "字幕 1.ass");
            await File.WriteAllTextAsync(ass, "[Script Info]\nScriptType: v4.00+\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,你好\n");
            string output = Path.Combine(root, "out 输出.mkv");

            var plan = new MuxPlan(output, [new MuxInput(video!), new MuxInput(ass, [new MuxTrack(0, "zh-Hans", "简体", Default: true)])],
                Attachments: [Font], Title: "Round trip");
            MkvRunResult mux = await tools!.MuxAsync(MkvToolNix.BuildMuxArguments(plan, new Dictionary<string, MkvFileInfo>
            {
                [ass] = await tools.IdentifyAsync(ass),
            }), null, default);
            Assert.True(mux.Succeeded, string.Join("; ", mux.Errors));

            MkvFileInfo info = await tools.IdentifyAsync(output);
            MkvTrack subs = info.Tracks.Single(t => t.Type == MkvTrackType.Subtitles);
            Assert.Equal(("zh-Hans", "简体", "Round trip"), (subs.Language, subs.Name, info.Title));

            string extractedFont = Path.Combine(root, "x", "font.ttf");
            string extractedAss = Path.Combine(root, "x", "subs.ass");
            MkvRunResult extract = await tools.ExtractAsync(MkvToolNix.BuildExtractArguments(
                output, [(subs.Id, extractedAss)], [(info.Attachments.Single().Id, extractedFont)], null), null, default);
            Assert.True(extract.Succeeded, string.Join("; ", extract.Errors));

            Assert.Equal(await File.ReadAllBytesAsync(Font), await File.ReadAllBytesAsync(extractedFont));
            Assert.Contains("你好", await File.ReadAllTextAsync(extractedAss));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Bundled_copy_is_tried_before_PATH_when_no_folder_is_set()
    {
        IReadOnlyList<string?> candidates = MkvToolNix.Candidates(null);
        Assert.Equal(MkvToolNix.BundledDirectory, candidates[0]);
        Assert.Null(candidates[1]);

        IReadOnlyList<string?> custom = MkvToolNix.Candidates("/opt/mkv");
        Assert.Equal(["/opt/mkv", MkvToolNix.BundledDirectory, null], custom.Take(3));
    }
}
