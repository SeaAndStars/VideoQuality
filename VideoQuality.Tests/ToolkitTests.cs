using VideoQuality.Core.Chapters;
using VideoQuality.Core.Edit;
using VideoQuality.Core.Encode;
using VideoQuality.Core.Media;
using VideoQuality.Core.Mkv;
using VideoQuality.Core.Monitor;
using VideoQuality.Core.Tools;

namespace VideoQuality.Tests;

public class ChapterListTests
{
    private static readonly ChapterMark[] Marks =
    [
        new(TimeSpan.Zero, "OP"),
        new(TimeSpan.FromSeconds(90.5), "Chapter1"),
        new(new TimeSpan(0, 0, 21, 0, 250), "ED"),
    ];

    [Fact]
    public void Ogm_round_trips()
    {
        string ogm = ChapterList.ToOgm(Marks);
        Assert.StartsWith("CHAPTER01=00:00:00.000", ogm, StringComparison.Ordinal);
        Assert.Contains("CHAPTER02NAME=Chapter1", ogm, StringComparison.Ordinal);
        Assert.Equal(Marks, ChapterList.Parse(ogm));
        Assert.Equal([new ChapterMark(TimeSpan.FromSeconds(5), string.Empty)], ChapterList.ParseOgm("chapter01=00:00:05.000\nCHAPTER02=garbage\n"));
    }

    [Fact]
    public void Matroska_xml_round_trips_with_nanoseconds()
    {
        string xml = ChapterList.ToXml(Marks, "jpn");
        Assert.Contains("<ChapterTimeStart>00:01:30.500000000</ChapterTimeStart>", xml, StringComparison.Ordinal);
        Assert.Contains("<ChapterLanguage>jpn</ChapterLanguage>", xml, StringComparison.Ordinal);
        Assert.Equal(Marks, ChapterList.Parse("﻿  " + xml));
        Assert.Equal([new ChapterMark(TimeSpan.Zero, string.Empty)], ChapterList.ParseXml("<Chapters><EditionEntry><ChapterAtom/></EditionEntry></Chapters>"));
    }

    [Fact]
    public void Save_and_load_pick_the_format_by_extension()
    {
        string folder = Path.Combine(Path.GetTempPath(), "vq-ch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        foreach (string name in new[] { "c.xml", "c.txt" })
        {
            string path = Path.Combine(folder, name);
            ChapterList.Save(path, Marks);
            Assert.Equal(Marks, ChapterList.Load(path));
        }

        Assert.StartsWith("<?xml", File.ReadAllText(Path.Combine(folder, "c.xml")), StringComparison.Ordinal);
        Directory.Delete(folder, true);
    }

    [Fact]
    public void Auto_naming_marks_op_ed_and_numbers_the_rest()
    {
        ChapterMark[] five = Enumerable.Range(0, 5).Select(i => new ChapterMark(TimeSpan.FromMinutes(i), "x")).ToArray();
        Assert.Equal(["Chapter1", "OP", "Chapter2", "ED", "Chapter3"], ChapterList.AutoName(five, 1, 3).Select(c => c.Name));
        Assert.Equal(["Chapter1", "Chapter2", "Chapter3", "Chapter4", "Chapter5"], ChapterList.AutoName(five, null, null).Select(c => c.Name));
    }

    [Fact]
    public void Check_reports_order_gaps_and_names()
    {
        Assert.Empty(ChapterList.Check(Marks, TimeSpan.FromMinutes(24)));
        Assert.Single(ChapterList.Check([], TimeSpan.Zero));
        IReadOnlyList<string> problems = ChapterList.Check(
            [new(TimeSpan.FromSeconds(5), ""), new(TimeSpan.FromSeconds(5), "b"), new(TimeSpan.FromMinutes(30), " ")],
            TimeSpan.FromMinutes(24));
        Assert.Equal(4, problems.Count);
        // Unknown duration: only the start is questioned, not "past the end".
        Assert.Single(ChapterList.Check([new(TimeSpan.FromMinutes(30), "late")], TimeSpan.Zero));
    }

    [Fact]
    public void Times_parse_in_both_precisions()
    {
        Assert.Equal(new TimeSpan(0, 1, 2, 3, 456), ChapterList.ParseTime("01:02:03.456"));
        Assert.Equal(new TimeSpan(0, 1, 2, 3, 456), ChapterList.ParseTime(" 01:02:03.456000000 "));
        Assert.Equal(TimeSpan.FromSeconds(3723), ChapterList.ParseTime("1:02:03"));
        Assert.Throws<FormatException>(() => ChapterList.ParseTime("soon"));
    }

    [Fact]
    public async Task Imports_a_videos_chapters()
    {
        string clip = await TestMedia.ClipAsync();
        IReadOnlyList<ChapterMark> chapters = ChapterList.FromMedia(MediaInfoReader.Read(clip));
        Assert.Equal(["OP", "Chapter1"], chapters.Select(c => c.Name));
        Assert.Equal(TimeSpan.FromSeconds(1), chapters[1].Start);
    }
}

public class TrimMergeTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "vq-edit-" + Guid.NewGuid().ToString("N"));

    public TrimMergeTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        Directory.Delete(_work, true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Copy_cut_snaps_to_the_previous_keyframe_and_says_so()
    {
        EditCommand cut = TrimMerge.CopyCut("/in.mkv", "/out.mkv", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(25),
            [TimeSpan.Zero, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(12)]);

        Assert.False(cut.UsesMkvMerge);
        Assert.Equal(TimeSpan.FromSeconds(15), cut.Duration);
        Assert.Contains("00:00:08.000", Assert.Single(cut.Warnings), StringComparison.Ordinal);
        Assert.Equal(["-ss", "10", "-i", "/in.mkv", "-t", "15"], cut.Arguments.Skip(3).Take(6));

        Assert.Empty(TrimMerge.CopyCut("/in.mkv", "/o.mkv", TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(9), [TimeSpan.FromSeconds(8)]).Warnings);
        Assert.Empty(TrimMerge.CopyCut("/in.mkv", "/o.mkv", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(9), [TimeSpan.FromSeconds(5)]).Warnings.Where(w => w.Length == 0));
        Assert.Throws<ArgumentException>(() => TrimMerge.CopyCut("/in.mkv", "/o.mkv", TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(9)));
    }

    [Fact]
    public void Encode_cut_sets_the_range_on_the_preset()
    {
        var info = new MediaInfo { Duration = TimeSpan.FromMinutes(1), Streams = [new MediaStream { Kind = StreamKind.Video }] };
        EncodeCommand command = TrimMerge.EncodeCut(new EncodePreset(), "/in.mkv", info, "/o.mkv", "/tmp", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
        Assert.Equal(TimeSpan.FromSeconds(10), command.Duration);
        Assert.Contains("-ss", command.Passes[0]);
        Assert.Throws<ArgumentException>(() => TrimMerge.EncodeCut(new EncodePreset(), "/in.mkv", info, "/o.mkv", "/tmp", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Compatibility_lists_mismatches()
    {
        MediaInfo a = new()
        {
            Path = "/a.mkv",
            Streams = [new MediaStream { Kind = StreamKind.Video, Codec = "hevc", Width = 1920, Height = 1080 }, new MediaStream { Kind = StreamKind.Audio, Codec = "aac", SampleRate = 48000, Channels = 2 }],
        };
        MediaInfo b = a with { Path = "/b.mkv", Streams = [new MediaStream { Kind = StreamKind.Video, Codec = "h264", Width = 1280, Height = 720 }] };
        MediaInfo noVideo = a with { Path = "/c.mka", Streams = [] };

        Assert.Single(TrimMerge.Compatibility([a]));
        Assert.Empty(TrimMerge.Compatibility([a, a]));
        Assert.Equal(2, TrimMerge.Compatibility([a, b]).Count);
        Assert.Equal(2, TrimMerge.Compatibility([a, noVideo]).Count);
        Assert.Throws<ArgumentException>(() => TrimMerge.Merge([a], "/o.mkv", _work));
    }

    [Fact]
    public async Task Copy_cut_and_both_merge_paths_work_for_real()
    {
        string clip = await TestMedia.ClipAsync();
        string ffmpeg = TestMedia.RequireEncoder();

        // Cut 0.5–1.5 s by stream copy.
        string part = Path.Combine(_work, "part.mkv");
        EditCommand cut = TrimMerge.CopyCut(clip, part, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5));
        ProcessResult result = await SystemProcessRunner.Instance.RunAsync(new ProcessRequest { FileName = ffmpeg, Arguments = cut.Arguments }, TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode == 0, result.Error);
        Assert.InRange(MediaInfoReader.Read(part).Duration.TotalSeconds, 0.9, 1.6);

        // ffmpeg concat (MP4 output), with a quote in a file name.
        string quoted = Path.Combine(_work, "it's.mkv");
        File.Copy(clip, quoted);
        MediaInfo info = MediaInfoReader.Read(clip);
        EditCommand concat = TrimMerge.Merge([info, MediaInfoReader.Read(quoted)], Path.Combine(_work, "joined.mp4"), _work);
        Assert.Empty(concat.Warnings);
        Assert.InRange(concat.Duration.TotalSeconds, 3.9, 4.1);
        result = await SystemProcessRunner.Instance.RunAsync(new ProcessRequest { FileName = ffmpeg, Arguments = concat.Arguments }, TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode == 0, result.Error);
        Assert.InRange(MediaInfoReader.Read(concat.Output).Duration.TotalSeconds, 3.8, 4.2);

        // mkvmerge append (MKV output).
        MkvToolNix tools = (await MkvToolNix.LocateAsync())!;
        EditCommand append = TrimMerge.Merge([info, info], Path.Combine(_work, "joined.mkv"), _work);
        Assert.True(append.UsesMkvMerge);
        Assert.Equal("+" + clip, append.Arguments[^1]);
        result = await SystemProcessRunner.Instance.RunAsync(new ProcessRequest { FileName = tools.MkvMerge, Arguments = append.Arguments }, TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode is 0 or 1, result.Output);
        Assert.InRange(MediaInfoReader.Read(append.Output).Duration.TotalSeconds, 3.8, 4.2);
    }
}

public class SystemMonitorTests
{
    [Fact]
    public void Parses_proc_stat_meminfo_and_nvidia_smi()
    {
        Assert.Equal((150UL, 1000UL), SystemMonitor.ParseProcStat("cpu  400 100 200 140 10 50 50 50 0 0\ncpu0 1 2 3 4\n"));
        Assert.Equal((20UL, 60UL), SystemMonitor.ParseProcStat("cpu  10 10 20 20\n"));
        Assert.Equal((6L * 1024 * 1024, 16L * 1024 * 1024), SystemMonitor.ParseMemInfo("MemTotal:       16384 kB\nMemFree: 1 kB\nMemAvailable:   10240 kB\n"));
        IReadOnlyList<GpuSample> gpus = SystemMonitor.ParseNvidiaSmi("NVIDIA GeForce RTX 5060 Ti, 35, 12, [N/A], 2048, 16311, 51\n\nbad,line\n");
        Assert.Equal(new GpuSample("NVIDIA GeForce RTX 5060 Ti", 35, 12, 0, 2048, 16311, 51), Assert.Single(gpus));
    }

    [Fact]
    public async Task Samples_cpu_memory_and_a_scripted_gpu()
    {
        using FakeTool smi = FakeTool.Create("nvidia-smi", new FakeRule { Stdout = "Fake GPU, 50, 25, 5, 100, 1000, 40\n" });
        var monitor = new SystemMonitor(SystemProcessRunner.Instance, smi.Path);

        SystemSample first = await monitor.SampleAsync(TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        SystemSample second = await monitor.SampleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, first.CpuPercent);
        Assert.InRange(second.CpuPercent, 0, 100);
        Assert.True(second.MemoryTotal > second.MemoryUsed && second.MemoryUsed > 0);
        Assert.Equal("Fake GPU", Assert.Single(second.Gpus).Name);
        Assert.Contains(SystemMonitor.NvidiaQuery, smi.Calls[0]);
    }

    [Fact]
    public async Task No_driver_or_a_failing_smi_means_no_gpus()
    {
        var missing = new SystemMonitor(SystemProcessRunner.Instance, Path.Combine(Path.GetTempPath(), "no-smi-" + Guid.NewGuid()));
        Assert.Empty((await missing.SampleAsync(TestContext.Current.CancellationToken)).Gpus);

        using FakeTool smi = FakeTool.Create("nvidia-smi", new FakeRule { Exit = 9 });
        Assert.Empty((await new SystemMonitor(SystemProcessRunner.Instance, smi.Path).SampleAsync(TestContext.Current.CancellationToken)).Gpus);
    }
}

public class EncoderToolTests
{
    private static readonly string Encoders = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ffmpeg-encoders.txt"));
    private static readonly string Filters = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ffmpeg-filters.txt"));

    [Fact]
    public async Task Locates_a_folder_ffmpeg_and_reads_its_encoders_and_filters()
    {
        using FakeTool ffmpeg = FakeTool.Create("ffmpeg",
            new FakeRule { Match = ["-version"], Stdout = "ffmpeg version n8.1.3 Copyright\nbuilt with gcc\n" },
            new FakeRule { Match = ["-encoders"], Stdout = Encoders },
            new FakeRule { Match = ["-filters"], Stdout = Filters });

        EncoderTool tool = (await EncoderTool.LocateAsync(ffmpeg.Directory, SystemProcessRunner.Instance, TestContext.Current.CancellationToken))!;

        Assert.Equal(ffmpeg.Path, tool.Path);
        Assert.Equal("ffmpeg version n8.1.3 Copyright", tool.Version);
        Assert.True(tool.HasEncoder("libx265") && tool.HasEncoder("hevc_nvenc") && tool.HasEncoder("aac"));
        Assert.True(tool.HasFilter("subtitles") && tool.HasFilter("bwdif"));
        Assert.False(tool.HasEncoder("Encoders:"));
        Assert.Contains(EncoderCatalog.AvailableVideo(tool), e => e.Name == "libx265");
        Assert.Contains(EncoderCatalog.AvailableAudio(tool), e => e.Name == "libopus");
        Assert.Equal(["copy"], EncoderCatalog.AvailableVideo(null).Select(e => e.Name));
        Assert.Equal(["copy", "none"], EncoderCatalog.AvailableAudio(null).Select(e => e.Name));
    }

    [Fact]
    public async Task Skips_folders_without_ffmpeg_and_programs_that_are_not_ffmpeg()
    {
        using FakeTool impostor = FakeTool.Create("ffmpeg", new FakeRule { Stdout = "something else\n" });
        using FakeTool failing = FakeTool.Create("ffmpeg", new FakeRule { Stdout = "ffmpeg version x\n", Exit = 1 });
        string empty = Path.Combine(Path.GetTempPath(), "vq-empty-" + Guid.NewGuid().ToString("N"));

        foreach (string folder in new[] { impostor.Directory, failing.Directory, empty })
        {
            EncoderTool? tool = await EncoderTool.LocateAsync(folder, new PathlessRunner(), TestContext.Current.CancellationToken);
            Assert.Null(tool);
        }
    }

    [Fact]
    public void Candidates_are_settings_folder_bundled_copy_then_path()
    {
        Assert.Equal(["/x", EncoderTool.BundledDirectory, null], EncoderTool.Candidates("/x"));
        Assert.Equal([EncoderTool.BundledDirectory, null], EncoderTool.Candidates(" "));
        Assert.EndsWith(Path.Combine("tools", "ffmpeg-encode"), EncoderTool.BundledDirectory, StringComparison.Ordinal);
    }

    /// <summary>Real processes, but "ffmpeg" on PATH does not exist (so only the folder is tried).</summary>
    private sealed class PathlessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default) =>
            Path.IsPathRooted(request.FileName)
                ? SystemProcessRunner.Instance.RunAsync(request, cancellationToken)
                : throw new ProcessStartException(request.FileName, new FileNotFoundException());
    }
}

public class MediaInfoTests
{
    [Fact]
    public async Task Reads_streams_chapters_and_writes_a_summary()
    {
        string clip = await TestMedia.ClipAsync();
        MediaInfo info = MediaInfoReader.Read(clip);

        Assert.True(info.IsMatroska);
        Assert.False(info.IsMp4);
        Assert.Equal(2, info.Duration.TotalSeconds, 1);
        MediaStream video = info.Video!;
        Assert.Equal(("h264", 320, 180, 8, "24000/1001"), (video.Codec, video.Width, video.Height, video.BitDepth, video.FrameRateText));
        MediaStream audio = Assert.Single(info.AudioStreams);
        Assert.Equal(("aac", 48000, 2, "jpn", "stereo"), (audio.Codec, audio.SampleRate, audio.Channels, audio.Language, audio.ChannelLayout));
        Assert.Equal(2, info.Chapters.Count);

        string text = info.ToText();
        Assert.Contains("#0 h264", text, StringComparison.Ordinal);
        Assert.Contains("[jpn]", text, StringComparison.Ordinal);
        Assert.Contains("00:00:01.000 Chapter1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_formats_every_kind_of_stream()
    {
        var info = new MediaInfo
        {
            Path = "/x.mkv",
            Format = "matroska,webm",
            Title = "Ep 1",
            Size = 3L << 30,
            BitRate = 5_000_000,
            Streams =
            [
                new MediaStream { Index = 0, Kind = StreamKind.Video, Codec = "hevc", Profile = "Main 10", Width = 1920, Height = 1080, FrameRate = 23.976, PixelFormat = "yuv420p10le", BitRate = 4_000_000 },
                new MediaStream { Index = 1, Kind = StreamKind.Audio, Codec = "flac", SampleRate = 48000, Channels = 2, SampleBits = 24, Default = true },
                new MediaStream { Index = 2, Kind = StreamKind.Audio, Codec = "aac", Channels = 6, BitRate = 256_000 },
                new MediaStream { Index = 3, Kind = StreamKind.Subtitle, Codec = "ass", Title = "JPSC", Language = "zh", Forced = true },
                new MediaStream { Index = 4, Kind = StreamKind.Attachment, FileName = "A.ttf", MimeType = "font/ttf" },
                new MediaStream { Index = 5, Kind = StreamKind.Data, Codec = "bin_data" },
            ],
        };

        string text = info.ToText();
        Assert.Contains("matroska,webm · 00:00:00.000 · 3.00 GiB · 5000 kb/s", text, StringComparison.Ordinal);
        Assert.Contains("Ep 1", text, StringComparison.Ordinal);
        Assert.Contains("hevc Main 10 1920×1080 23.976 fps yuv420p10le 4000 kb/s", text, StringComparison.Ordinal);
        Assert.Contains("flac 48 kHz 2 ch 24-bit default", text, StringComparison.Ordinal);
        Assert.Contains("aac 6 ch 256 kb/s", text, StringComparison.Ordinal);
        Assert.Contains("ass [zh] \"JPSC\" forced", text, StringComparison.Ordinal);
        Assert.Contains("A.ttf font/ttf", text, StringComparison.Ordinal);
        Assert.Contains("#5 bin_data", text, StringComparison.Ordinal);
        Assert.Equal("1.5 MiB", MediaInfo.FormatSize(1536 * 1024));
        Assert.Equal("2 KiB", MediaInfo.FormatSize(2048));
        Assert.Single(info.Attachments);
        Assert.Equal(512_000, MediaInfoReader.StatisticsBitRate(new Dictionary<string, string> { ["BPS"] = "512000" }));
        Assert.Equal(0, MediaInfoReader.StatisticsBitRate(new Dictionary<string, string>()));
    }

    [Fact]
    public void Unreadable_files_throw()
    {
        string path = Path.Combine(Path.GetTempPath(), "vq-not-media-" + Guid.NewGuid().ToString("N") + ".mkv");
        File.WriteAllText(path, "not a video");
        Assert.Throws<InvalidOperationException>(() => MediaInfoReader.Read(path));
        Assert.Throws<InvalidOperationException>(() => MediaInfoReader.Read(path + ".missing"));
        File.Delete(path);
    }
}
