using VideoQuality.Core.Encode;
using VideoQuality.Core.Media;
using VideoQuality.Core.Release;
using VideoQuality.Core.Tools;

namespace VideoQuality.Tests;

/// <summary>Real encodes with the encoder ffmpeg on a generated 2-second clip.</summary>
public class EncodeIntegrationTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "vq-encode-" + Guid.NewGuid().ToString("N"));

    public EncodeIntegrationTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        Directory.Delete(_work, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static async Task<EncodeJob> RunAsync(EncodeQueue queue, EncodeJob job)
    {
        queue.Add(job);
        await queue.WhenIdleAsync(TestContext.Current.CancellationToken);
        return job;
    }

    private EncodeQueue Queue() => new(SystemProcessRunner.Instance, TestMedia.RequireEncoder);

    [Fact]
    public async Task GreenTea_web_preset_meets_its_own_targets()
    {
        string clip = await TestMedia.ClipAsync();
        MediaInfo info = MediaInfoReader.Read(clip);
        EncodePreset preset = GreenTeaSpec.Preset(ReleaseProfile.WebEpisode, SubtitleDelivery.Embedded) with
        {
            Video = GreenTeaSpec.Preset(ReleaseProfile.WebEpisode, SubtitleDelivery.Embedded).Video with { Preset = "ultrafast" },
        };
        string output = Path.Combine(_work, "web.mkv");
        EncodeCommand command = FfmpegCommandBuilder.Build(preset, clip, info, output, _work);
        var changes = new List<EncodeJobState>();
        EncodeQueue queue = Queue();
        queue.JobChanged += j => { lock (changes) { changes.Add(j.State); } };

        EncodeJob job = await RunAsync(queue, new EncodeJob(clip, command));

        Assert.True(job.State == EncodeJobState.Done, job.Error);
        Assert.Equal(1, job.Progress);
        Assert.True(job.WrittenSize > 0);
        Assert.Equal(job.WrittenSize, job.EstimatedSize);
        Assert.Contains(EncodeJobState.Running, changes);
        MediaInfo encoded = MediaInfoReader.Read(output);
        MediaStream video = encoded.Video!;
        Assert.Equal("hevc", video.Codec);
        Assert.Equal(10, video.BitDepth);
        Assert.Equal(23.976, video.FrameRate, 3);
        MediaStream audio = Assert.Single(encoded.AudioStreams);
        Assert.Equal("aac", audio.Codec);
        Assert.Equal(44100, audio.SampleRate);
        Assert.Equal(2, audio.Channels);
        Assert.Equal(2, encoded.Chapters.Count);
        Assert.Equal("OP", encoded.Chapters[0].Title);
    }

    [Fact]
    public async Task Burned_in_mp4_shows_the_subtitles_in_the_picture()
    {
        string clip = await TestMedia.ClipAsync();
        string folder = Path.Combine(_work, "sub folder 'a' [b], c; d: 中文");
        string script = TestMedia.Script(folder);
        EncodePreset preset = GreenTeaSpec.Preset(ReleaseProfile.WebEpisode, SubtitleDelivery.Burned) with
        {
            Video = new VideoSettings { Encoder = "libx265", Preset = "ultrafast", BitDepth = 10 },
            Burn = new SubtitleBurn { File = script, FontsDirectory = folder },
        };
        string output = Path.Combine(_work, "burned.mp4");
        EncodeJob job = await RunAsync(Queue(), new EncodeJob(clip, FfmpegCommandBuilder.Build(preset, clip, MediaInfoReader.Read(clip), output, _work)));

        Assert.True(job.State == EncodeJobState.Done, job.Error + string.Join('\n', job.Log));
        Assert.True(await MeanLumaAsync(output) > 200, "the white drawing should cover the frame");
        Assert.True(await MeanLumaAsync(clip) < 200);
        Assert.Empty(MediaInfoReader.Read(output).SubtitleStreams);
    }

    [Theory]
    [InlineData("libx264")]
    [InlineData("libx265")]
    public async Task Two_pass_target_size_cleans_up_its_logs(string encoder)
    {
        string clip = await TestMedia.ClipAsync();
        var preset = new EncodePreset
        {
            Video = new VideoSettings { Encoder = encoder, Mode = RateControl.TwoPass, BitrateKbps = 300, Preset = "ultrafast" },
            Audio = new AudioSettings { Encoder = "none" },
        };
        string output = Path.Combine(_work, encoder + ".mkv");
        EncodeCommand command = FfmpegCommandBuilder.Build(preset, clip, MediaInfoReader.Read(clip), output, _work);

        EncodeJob job = await RunAsync(Queue(), new EncodeJob(clip, command, "two-pass"));

        Assert.True(job.State == EncodeJobState.Done, job.Error);
        Assert.Equal("two-pass", job.Name);
        Assert.Equal(2, job.Pass);
        Assert.True(File.Exists(output));
        Assert.Empty(Directory.EnumerateFiles(_work, "vq-2pass-*"));
    }

    [Fact]
    public async Task A_failing_encode_reports_ffmpegs_error_and_removes_the_output()
    {
        string clip = await TestMedia.ClipAsync();
        var preset = new EncodePreset { Video = new VideoSettings { Preset = "ultrafast" }, Custom = new CustomArguments { Video = "-no-such-option 1" } };
        string output = Path.Combine(_work, "broken.mkv");
        EncodeJob job = await RunAsync(Queue(), new EncodeJob(clip, FfmpegCommandBuilder.Build(preset, clip, MediaInfoReader.Read(clip), output, _work)));

        Assert.Equal(EncodeJobState.Failed, job.State);
        Assert.Contains("no-such-option", job.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    /// <summary>Average luma of the middle frame, decoded by the encoder ffmpeg to 8-bit gray.</summary>
    private async Task<double> MeanLumaAsync(string video)
    {
        string raw = Path.Combine(_work, Guid.NewGuid().ToString("N") + ".gray");
        ProcessResult result = await SystemProcessRunner.Instance.RunAsync(new ProcessRequest
        {
            FileName = TestMedia.RequireEncoder(),
            Arguments = ["-hide_banner", "-y", "-ss", "1", "-i", video, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gray", raw],
        }, TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode == 0, result.Error);
        byte[] pixels = await File.ReadAllBytesAsync(raw, TestContext.Current.CancellationToken);
        return pixels.Average(b => (double)b);
    }
}
