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

    [Theory]
    [InlineData(DifferenceChannels.Luma, 8.0)]
    [InlineData(DifferenceChannels.Chroma, 4.0)]
    [InlineData(DifferenceChannels.Both, 1.0)]
    [InlineData(DifferenceChannels.Both, 32.0)]
    public void Fixed_point_matches_the_floating_point_reference(DifferenceChannels channels, double gain)
    {
        (DecodedFrame a, DecodedFrame b) = NoisyPair(67, 45, seed: 3);

        DecodedFrame diff = PixelDiff.Difference(a, b, gain, channels);

        // The fixed-point path quantises to 1/16 of a code value; at high gain that can move a
        // pixel by a few levels on the steepest colormap segment, never more.
        int tolerance = (int)Math.Ceiling(gain / 8) + 1;
        for (int i = 0; i < a.Bgra.Length; i += 4)
        {
            ReferenceColour(a.Bgra, b.Bgra, i, gain, channels, out byte red, out byte green, out byte blue);
            Assert.InRange(diff.Bgra[i], blue - tolerance, blue + tolerance);
            Assert.InRange(diff.Bgra[i + 1], green - tolerance, green + tolerance);
            Assert.InRange(diff.Bgra[i + 2], red - tolerance, red + tolerance);
            Assert.Equal(255, diff.Bgra[i + 3]);
        }
    }

    [Theory]
    [InlineData(DifferenceChannels.Luma)]
    [InlineData(DifferenceChannels.Both)]
    public void Percentile_matches_the_floating_point_reference(DifferenceChannels channels)
    {
        (DecodedFrame a, DecodedFrame b) = NoisyPair(200, 150, seed: 9);

        foreach (double percentile in (double[])[0.5, 0.99, 0.999])
        {
            double expected = ReferencePercentile(a.Bgra, b.Bgra, percentile, channels);
            double actual = PixelDiff.DifferencePercentile(a, b, percentile, channels);

            // One histogram bin is 400 / 1024 ≈ 0.39.
            Assert.InRange(actual, expected - 0.4, expected + 0.4);
        }
    }

    [Fact]
    public void Size_mismatch_throws()
    {
        Assert.Throws<ArgumentException>(() =>
            PixelDiff.Difference(Solid(2, 2, 0, 0, 0), Solid(3, 2, 0, 0, 0)));
    }

    private static (DecodedFrame A, DecodedFrame B) NoisyPair(int width, int height, int seed)
    {
        var random = new Random(seed);
        DecodedFrame a = Solid(width, height, 0, 0, 0);
        DecodedFrame b = Solid(width, height, 0, 0, 0);

        for (int i = 0; i < a.Bgra.Length; i += 4)
        {
            for (int c = 0; c < 3; c++)
            {
                a.Bgra[i + c] = (byte)random.Next(256);
                int noise = random.Next(10) < 8 ? random.Next(-4, 5) : random.Next(-120, 121);
                b.Bgra[i + c] = (byte)Math.Clamp(a.Bgra[i + c] + noise, 0, 255);
            }
        }

        return (a, b);
    }

    // The original floating-point implementation, kept as the oracle for the fast path.
    private static double ReferenceDifference(byte[] s, byte[] c, int i, DifferenceChannels channels)
    {
        int db = Math.Abs(s[i] - c[i]);
        int dg = Math.Abs(s[i + 1] - c[i + 1]);
        int dr = Math.Abs(s[i + 2] - c[i + 2]);

        double luma = 0.114 * db + 0.587 * dg + 0.299 * dr;
        double chroma = Math.Max(Math.Abs(db - dg), Math.Abs(dr - dg)) * 1.4;

        return channels switch
        {
            DifferenceChannels.Luma => luma,
            DifferenceChannels.Chroma => chroma,
            _ => Math.Max(luma, chroma),
        };
    }

    private static void ReferenceColour(
        byte[] s, byte[] c, int i, double gain, DifferenceChannels channels,
        out byte red, out byte green, out byte blue)
    {
        double t = Math.Clamp(ReferenceDifference(s, c, i, channels) * gain / 255.0, 0.0, 1.0);

        (double R, double G, double B)[] stops =
            [(0, 0, 4), (66, 10, 104), (147, 38, 103), (221, 81, 58), (252, 255, 164)];
        double position = t * 4;
        int index = Math.Min(3, (int)position);
        double fraction = position - index;
        var (from, to) = (stops[index], stops[index + 1]);

        red = (byte)Math.Round(from.R + (to.R - from.R) * fraction);
        green = (byte)Math.Round(from.G + (to.G - from.G) * fraction);
        blue = (byte)Math.Round(from.B + (to.B - from.B) * fraction);
    }

    private static double ReferencePercentile(byte[] s, byte[] c, double percentile, DifferenceChannels channels)
    {
        var histogram = new int[1024];
        for (int i = 0; i < s.Length; i += 4)
        {
            histogram[Math.Clamp((int)(ReferenceDifference(s, c, i, channels) / 400.0 * 1024), 0, 1023)]++;
        }

        long target = Math.Max(1, (long)Math.Ceiling(s.Length / 4 * percentile));
        long accumulated = 0;
        for (int bin = 0; bin < 1024; bin++)
        {
            accumulated += histogram[bin];
            if (accumulated >= target)
            {
                return bin / 1024.0 * 400.0;
            }
        }

        return 400.0;
    }
}
