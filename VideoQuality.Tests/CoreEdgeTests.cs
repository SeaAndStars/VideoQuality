using VideoQuality.Core.Decoding;
using VideoQuality.Core.Imaging;
using VideoQuality.Core.Ingest;

namespace VideoQuality.Tests;

/// <summary>Small edge cases across Core helpers.</summary>
public sealed class CoreEdgeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vq-edge-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(1920, 1080, 0, 0, null)] // unknown encode size
    [InlineData(0, 0, 1920, 1080, null)] // unknown reference size
    [InlineData(1920, 1080, 1920, 720, "1920")] // same width, other height
    public void Sizes_are_compared_only_when_both_are_known(int rw, int rh, int ew, int eh, string? expected)
    {
        string? warning = StreamMismatch.Describe(new StreamShape(rw, rh, 24, 10), new StreamShape(ew, eh, 24, 10));
        if (expected is null)
        {
            Assert.Null(warning);
        }
        else
        {
            Assert.Contains(expected, warning, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(0, 24)]
    [InlineData(24, 0)]
    [InlineData(24, 24.005)]
    public void Frame_rates_are_compared_only_when_both_are_known(double reference, double encode) =>
        Assert.Null(StreamMismatch.Describe(new StreamShape(8, 8, reference, 10), new StreamShape(8, 8, encode, 10)));

    [Fact]
    public void Empty_frames_have_the_largest_percentile()
    {
        var empty = new DecodedFrame(0, 0, 0, [], 0);
        Assert.Equal(400, PixelDiff.DifferencePercentile(empty, empty, 0.5));
    }

    [Fact]
    public void Drops_report_files_without_an_extension_and_plans_know_when_they_are_empty()
    {
        string bare = Path.Combine(_root, "README");
        File.WriteAllText(bare, string.Empty);
        string video = Path.Combine(_root, "a.mkv");
        File.WriteAllText(video, string.Empty);

        DropPlan rejected = DropRouter.Plan([bare], DropZone.Auto, null, []);
        Assert.True(rejected.IsEmpty);
        Assert.Single(rejected.Rejected);

        Assert.False(DropRouter.Plan([video], DropZone.Auto, null, []).IsEmpty); // a reference
        Assert.False(DropRouter.Plan([video], DropZone.Auto, "/ref.mkv", []).IsEmpty); // an encode
    }
}

public class JobAndDescriptionTests
{
    [Fact]
    public async Task Finished_jobs_ignore_cancel_and_unchanged_values_raise_nothing()
    {
        var service = new VideoQuality.Core.Jobs.JobService();
        var job = new VideoQuality.Core.Jobs.Job { Title = "t", Kind = VideoQuality.Core.Jobs.JobKind.Export, Work = (_, _, _) => Task.CompletedTask };
        Assert.False(job.IsFinished);
        service.Enqueue(job);
        await job.Completion;
        Assert.True(job.IsFinished);
        service.Cancel(job);
        Assert.False(job.Cancellation.IsCancellationRequested);

        var failed = new VideoQuality.Core.Jobs.Job { Title = "f", Kind = VideoQuality.Core.Jobs.JobKind.Index, Work = (_, _, _) => throw new InvalidOperationException("x") };
        var changes = new List<string?>();
        failed.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        service.Enqueue(failed);
        await failed.Completion;
        Assert.Equal(VideoQuality.Core.Jobs.JobState.Failed, failed.State);
        Assert.True(failed.IsFinished);
        Assert.Contains(nameof(VideoQuality.Core.Jobs.Job.State), changes);
        changes.Clear();
        TimeSpan elapsed = failed.Elapsed;
        failed.Elapsed = elapsed; // unchanged: no notification
        Assert.Empty(changes);
    }

    [Theory]
    [InlineData(0, 0, "", "h264 High 1920×1080 yuv420p")]
    [InlineData(23.976, 8_000_000, "", "h264 High 1920×1080 23.976 fps yuv420p 8000 kb/s")]
    public void Video_streams_are_described(double fps, long bitrate, string unused, string expected)
    {
        _ = unused;
        var stream = new VideoQuality.Core.Media.MediaStream
        {
            Kind = VideoQuality.Core.Media.StreamKind.Video, Codec = "h264", Profile = "High", Width = 1920, Height = 1080,
            FrameRate = fps, PixelFormat = "yuv420p", BitRate = bitrate,
        };
        Assert.Equal(expected, VideoQuality.Core.Media.MediaInfo.Describe(stream));
    }

    [Theory]
    [InlineData(48000, "stereo", 2, 24, 320_000, "aac LC 48 kHz stereo 24-bit 320 kb/s")]
    [InlineData(0, "", 6, 0, 0, "aac LC 6 ch")]
    [InlineData(44100, "", 0, 0, 0, "aac LC 44.1 kHz")]
    public void Audio_streams_are_described(int rate, string layout, int channels, int bits, long bitrate, string expected)
    {
        var stream = new VideoQuality.Core.Media.MediaStream
        {
            Kind = VideoQuality.Core.Media.StreamKind.Audio, Codec = "aac", Profile = "LC", SampleRate = rate,
            ChannelLayout = layout, Channels = channels, SampleBits = bits, BitRate = bitrate,
        };
        Assert.Equal(expected, VideoQuality.Core.Media.MediaInfo.Describe(stream));
    }

    [Fact]
    public void Rates_and_statistics_tags_tolerate_unknowns()
    {
        Assert.Equal(0, VideoQuality.Core.Media.MediaInfoReader.FrameRate(new FFmpeg.AutoGen.AVRational { num = 0, den = 0 }));
        Assert.Equal(string.Empty, VideoQuality.Core.Media.MediaInfoReader.FrameRateText(new FFmpeg.AutoGen.AVRational { num = 24, den = 0 }));
        Assert.Equal(0, VideoQuality.Core.Media.MediaInfoReader.StatisticsBitRate(new Dictionary<string, string> { ["BPS"] = "n/a" }));
        Assert.Equal(0, VideoQuality.Core.Media.MediaInfoReader.StatisticsBitRate(new Dictionary<string, string>()));
    }
}
