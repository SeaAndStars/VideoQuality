using VideoQuality.Core.Scoring;

namespace VideoQuality.Tests;

public class FfmpegProgressParserTests
{
    [Fact]
    public void Emits_progress_only_on_block_boundaries()
    {
        var parser = new FfmpegProgressParser();

        Assert.Null(parser.Consume("frame=42"));
        Assert.Null(parser.Consume("fps=120.5"));
        Assert.Null(parser.Consume("bitrate= 512.0kbits/s"));
        Assert.Null(parser.Consume("out_time_ms=1400000"));

        VmafProgress? progress = parser.Consume("progress=continue");

        Assert.NotNull(progress);
        Assert.Equal(42, progress!.FrameNumber);
        Assert.Equal(120.5, progress.Fps, 3);
        Assert.False(progress.Done);
    }

    [Fact]
    public void End_block_is_marked_done()
    {
        var parser = new FfmpegProgressParser();
        parser.Consume("frame=300");
        parser.Consume("fps=300.1");

        VmafProgress? progress = parser.Consume("progress=end");

        Assert.NotNull(progress);
        Assert.True(progress!.Done);
        Assert.Equal(300, progress.FrameNumber);
    }

    [Fact]
    public void Ignores_noise_and_malformed_lines()
    {
        var parser = new FfmpegProgressParser();

        Assert.Null(parser.Consume(null));
        Assert.Null(parser.Consume(string.Empty));
        Assert.Null(parser.Consume("no separator here"));
        Assert.Null(parser.Consume("=leading-equals"));
        Assert.Null(parser.Consume("frame=not-a-number"));
        Assert.Null(parser.Frame);
    }

    [Fact]
    public void Frame_is_null_until_reported()
    {
        var parser = new FfmpegProgressParser();

        Assert.Null(parser.Frame);
        parser.Consume("fps=24");
        Assert.Null(parser.Frame);
        parser.Consume("frame=0");
        Assert.Equal(0, parser.Frame);
    }

    [Fact]
    public void Unparsable_fps_keeps_the_last_value()
    {
        var parser = new FfmpegProgressParser();
        parser.Consume("fps=24");
        parser.Consume("fps=N/A");
        Assert.Equal(24, parser.Fps);
    }
}
