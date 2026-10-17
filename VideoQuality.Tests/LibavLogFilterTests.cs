using VideoQuality.Core.Decoding;

namespace VideoQuality.Tests;

public class LibavLogFilterTests
{
    [Fact]
    public void Font_attachment_probe_warnings_are_dropped()
    {
        const string message =
            "[matroska,webm @ 0000026344902a00] Could not find codec parameters for stream 4 (Attachment: none): unknown codec\n" +
            "Consider increasing the value for the 'analyzeduration' (0) and 'probesize' (5000000) options";

        Assert.True(FfmpegRuntime.IsNoise(message));
    }

    [Fact]
    public void Missing_parameters_for_real_streams_are_kept()
    {
        Assert.False(FfmpegRuntime.IsNoise(
            "[mov,mp4 @ 0x1] Could not find codec parameters for stream 0 (Video: h264, none): unspecified size"));
        Assert.False(FfmpegRuntime.IsNoise("[h264 @ 0x1] error while decoding MB 10 20"));
    }
}
