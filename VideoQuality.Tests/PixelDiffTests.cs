using VideoQuality.Core.Decoding;
using VideoQuality.Core.Imaging;

namespace VideoQuality.Tests;

public class PixelDiffTests
{
    private static DecodedFrame Solid(int width, int height, byte b, byte g, byte r)
    {
        int stride = width * 4;
        var pixels = new byte[stride * height];

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }

        return new DecodedFrame(width, height, stride, pixels, 0);
    }

    [Fact]
    public void Identical_frames_produce_no_difference()
    {
        DecodedFrame a = Solid(4, 4, 10, 20, 30);
        DecodedFrame b = Solid(4, 4, 10, 20, 30);

        DecodedFrame diff = PixelDiff.Difference(a, b, gain: 8);

        for (int i = 0; i < diff.Bgra.Length; i += 4)
        {
            // The colormap's zero point is (0, 0, 4), not pure black.
            Assert.InRange(diff.Bgra[i], 0, 4);
            Assert.Equal(0, diff.Bgra[i + 1]);
            Assert.Equal(0, diff.Bgra[i + 2]);
            Assert.Equal(255, diff.Bgra[i + 3]);
        }

        Assert.Equal(0, PixelDiff.DifferencePercentile(a, b, 0.999), 6);
    }

    [Fact]
    public void Luma_difference_is_amplified_and_coloured()
    {
        DecodedFrame a = Solid(2, 2, 0, 0, 0);
        DecodedFrame b = Solid(2, 2, 0, 0, 200); // red channel only

        DecodedFrame diff = PixelDiff.Difference(a, b, gain: 4);

        // The red channel dominates the luma difference, so the first pixel must be lit.
        Assert.True(diff.Bgra[2] > 0 || diff.Bgra[1] > 0 || diff.Bgra[0] > 0);
        Assert.Equal(255, diff.Bgra[3]);
    }

    [Fact]
    public void Percentile_tracks_the_largest_differences()
    {
        DecodedFrame a = Solid(10, 10, 0, 0, 0);
        DecodedFrame b = Solid(10, 10, 0, 0, 0);
        b.Bgra[0] = 255; // one blue pixel

        double p = PixelDiff.DifferencePercentile(a, b, 0.999, DifferenceChannels.Both);

        Assert.True(p > 0);
        Assert.True(p <= 400);
    }

    [Fact]
    public void Size_mismatch_throws()
    {
        Assert.Throws<ArgumentException>(() =>
            PixelDiff.Difference(Solid(2, 2, 0, 0, 0), Solid(3, 2, 0, 0, 0)));
    }
}
