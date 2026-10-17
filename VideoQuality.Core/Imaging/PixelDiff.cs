namespace VideoQuality.Core.Imaging;

/// <summary>Which components the difference image should show.</summary>
public enum DifferenceChannels
{
    Luma,
    Chroma,
    Both,
}

/// <summary>Builds a false-color difference image between two BGRA frames.</summary>
public static class PixelDiff
{
    /// <summary>
    /// Difference amplified by <paramref name="gain"/> and mapped through a perceptual
    /// black → purple → red → orange → yellow ramp (an "inferno"-style colormap).
    /// </summary>
    /// <param name="gain">Multiplier applied before the colormap; 1 shows raw differences, 8 makes
    /// small codec artifacts obvious.</param>
    /// <param name="channels">Luma only, chroma only, or the larger of the two. Chroma matters for
    /// 4:2:0 content, where colored edges bleed even when luma looks fine.</param>
    public static Decoding.DecodedFrame Difference(
        Decoding.DecodedFrame a,
        Decoding.DecodedFrame b,
        double gain = 4.0,
        DifferenceChannels channels = DifferenceChannels.Both)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            throw new ArgumentException($"Frame sizes differ: {a.Width}x{a.Height} vs {b.Width}x{b.Height}.");
        }

        byte[] source = a.Bgra;
        byte[] compare = b.Bgra;
        var destination = new byte[a.Bgra.Length];

        for (int i = 0; i < destination.Length; i += 4)
        {
            int db = Math.Abs(source[i] - compare[i]);
            int dg = Math.Abs(source[i + 1] - compare[i + 1]);
            int dr = Math.Abs(source[i + 2] - compare[i + 2]);

            double luma = 0.114 * db + 0.587 * dg + 0.299 * dr;
            double chroma = Math.Max(Math.Abs(db - dg), Math.Abs(dr - dg)) * 1.4;

            double difference = channels switch
            {
                DifferenceChannels.Luma => luma,
                DifferenceChannels.Chroma => chroma,
                _ => Math.Max(luma, chroma),
            };

            double t = Math.Clamp(difference * gain / 255.0, 0.0, 1.0);
            Colormap(t, out byte red, out byte green, out byte blue);

            destination[i] = blue;
            destination[i + 1] = green;
            destination[i + 2] = red;
            destination[i + 3] = 255;
        }

        return new Decoding.DecodedFrame(a.Width, a.Height, a.Stride, destination, a.Pts);
    }

    /// <summary>
    /// Percentile of the raw difference magnitudes, used to pick a sensible display gain
    /// (for example normalising the 99.9th percentile to near the top of the colormap).
    /// </summary>
    public static double DifferencePercentile(
        Decoding.DecodedFrame a,
        Decoding.DecodedFrame b,
        double percentile,
        DifferenceChannels channels = DifferenceChannels.Both)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            throw new ArgumentException($"Frame sizes differ: {a.Width}x{a.Height} vs {b.Width}x{b.Height}.");
        }

        const int Bins = 1024;
        const double MaxDifference = 400.0;
        var histogram = new int[Bins];
        long total = 0;

        byte[] source = a.Bgra;
        byte[] compare = b.Bgra;

        for (int i = 0; i < source.Length; i += 4)
        {
            int db = Math.Abs(source[i] - compare[i]);
            int dg = Math.Abs(source[i + 1] - compare[i + 1]);
            int dr = Math.Abs(source[i + 2] - compare[i + 2]);

            double luma = 0.114 * db + 0.587 * dg + 0.299 * dr;
            double chroma = Math.Max(Math.Abs(db - dg), Math.Abs(dr - dg)) * 1.4;

            double difference = channels switch
            {
                DifferenceChannels.Luma => luma,
                DifferenceChannels.Chroma => chroma,
                _ => Math.Max(luma, chroma),
            };

            int bin = Math.Clamp((int)(difference / MaxDifference * Bins), 0, Bins - 1);
            histogram[bin]++;
            total++;
        }

        // Ceiling, not truncation: with 100 pixels a 99.9th percentile must include the single
        // largest value, otherwise the one non-zero pixel is silently ignored.
        long target = Math.Max(1, (long)Math.Ceiling(total * Math.Clamp(percentile, 0, 1)));
        long accumulated = 0;

        for (int bin = 0; bin < Bins; bin++)
        {
            accumulated += histogram[bin];
            if (accumulated >= target)
            {
                return bin / (double)Bins * MaxDifference;
            }
        }

        return MaxDifference;
    }

    /// <summary>Five-stop inferno-like ramp: black, indigo, magenta-red, orange, pale yellow.</summary>
    private static void Colormap(double t, out byte red, out byte green, out byte blue)
    {
        (double R, double G, double B) low = (0, 0, 4);
        (double R, double G, double B) quarter = (66, 10, 104);
        (double R, double G, double B) middle = (147, 38, 103);
        (double R, double G, double B) high = (221, 81, 58);
        (double R, double G, double B) top = (252, 255, 164);

        double position = Math.Clamp(t, 0, 1) * 4;
        int index = Math.Min(3, (int)position);
        double fraction = position - index;

        (double R, double G, double B) from = index switch
        {
            0 => low,
            1 => quarter,
            2 => middle,
            _ => high,
        };

        (double R, double G, double B) to = index switch
        {
            0 => quarter,
            1 => middle,
            2 => high,
            _ => top,
        };

        red = (byte)Math.Round(from.R + (to.R - from.R) * fraction);
        green = (byte)Math.Round(from.G + (to.G - from.G) * fraction);
        blue = (byte)Math.Round(from.B + (to.B - from.B) * fraction);
    }
}
