using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VideoQuality.Core.Imaging;

/// <summary>Which components the difference image should show.</summary>
public enum DifferenceChannels
{
    Luma,
    Chroma,
    Both,
}

/// <summary>Builds a false-color difference image between two BGRA frames.</summary>
/// <remarks>
/// Per-pixel differences are computed in fixed point (1/16 of a code value) and mapped through a
/// colour table built once per call, with rows split across cores; a 1080p frame takes a few
/// milliseconds instead of tens, so the difference view keeps up with playback.
/// </remarks>
public static class PixelDiff
{
    // Differences are carried as value × 16. Chroma tops out at 255 × 1.4 = 357, so 5712 steps.
    private const int Scale = 16;
    private const int MaxScaled = 358 * Scale;

    // Luma weights 0.114 / 0.587 / 0.299 and the 1.4 chroma factor, × Scale, in 8.8 fixed point.
    private const int LumaB = 467;
    private const int LumaG = 2404;
    private const int LumaR = 1225;
    private const int ChromaFactor = 5734;

    // Rows per parallel work item; small enough to balance, large enough to amortise scheduling.
    private const int RowsPerChunk = 32;

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
        EnsureSameSize(a, b);

        // One BGRA word per scaled difference value.
        var palette = new uint[MaxScaled + 1];
        for (int value = 0; value < palette.Length; value++)
        {
            double t = Math.Clamp(value / (double)Scale * gain / 255.0, 0.0, 1.0);
            Colormap(t, out byte red, out byte green, out byte blue);
            palette[value] = blue | (uint)green << 8 | (uint)red << 16 | 0xFF000000u;
        }

        byte[] source = a.Bgra;
        byte[] compare = b.Bgra;
        byte[] destination = GC.AllocateUninitializedArray<byte>(source.Length);
        int stride = a.Stride;

        ForEachChunk(a.Height, (firstRow, endRow) =>
        {
            ReadOnlySpan<byte> s = source.AsSpan(firstRow * stride, (endRow - firstRow) * stride);
            ReadOnlySpan<byte> c = compare.AsSpan(firstRow * stride, s.Length);
            Span<uint> d = MemoryMarshal.Cast<byte, uint>(destination.AsSpan(firstRow * stride, s.Length));

            for (int i = 0, p = 0; i < s.Length; i += 4, p++)
            {
                d[p] = palette[Scaled(s[i], s[i + 1], s[i + 2], c[i], c[i + 1], c[i + 2], channels)];
            }
        });

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
        EnsureSameSize(a, b);

        const int Bins = 1024;
        const double MaxDifference = 400.0;
        var histogram = new long[Bins];

        // Scaled value → histogram bin, so the inner loop is two lookups.
        var binOf = new int[MaxScaled + 1];
        for (int value = 0; value < binOf.Length; value++)
        {
            binOf[value] = Math.Clamp((int)(value / (double)Scale / MaxDifference * Bins), 0, Bins - 1);
        }

        byte[] source = a.Bgra;
        byte[] compare = b.Bgra;
        int stride = a.Stride;

        ForEachChunk(a.Height, (firstRow, endRow) =>
        {
            ReadOnlySpan<byte> s = source.AsSpan(firstRow * stride, (endRow - firstRow) * stride);
            ReadOnlySpan<byte> c = compare.AsSpan(firstRow * stride, s.Length);
            Span<int> local = stackalloc int[Bins];

            for (int i = 0; i < s.Length; i += 4)
            {
                local[binOf[Scaled(s[i], s[i + 1], s[i + 2], c[i], c[i + 1], c[i + 2], channels)]]++;
            }

            for (int bin = 0; bin < Bins; bin++)
            {
                if (local[bin] != 0)
                {
                    Interlocked.Add(ref histogram[bin], local[bin]);
                }
            }
        });

        long total = (long)a.Width * a.Height;

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

    /// <summary>Luma, chroma, or the larger of the two, as difference × <see cref="Scale"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Scaled(int b0, int g0, int r0, int b1, int g1, int r1, DifferenceChannels channels)
    {
        int db = Math.Abs(b0 - b1);
        int dg = Math.Abs(g0 - g1);
        int dr = Math.Abs(r0 - r1);

        int luma = (LumaB * db + LumaG * dg + LumaR * dr + 128) >> 8;
        int chroma = (Math.Max(Math.Abs(db - dg), Math.Abs(dr - dg)) * ChromaFactor + 128) >> 8;

        return channels switch
        {
            DifferenceChannels.Luma => luma,
            DifferenceChannels.Chroma => chroma,
            _ => Math.Max(luma, chroma),
        };
    }

    private static void ForEachChunk(int height, Action<int, int> body)
    {
        int chunks = (height + RowsPerChunk - 1) / RowsPerChunk;
        Parallel.For(0, chunks, chunk =>
        {
            int firstRow = chunk * RowsPerChunk;
            body(firstRow, Math.Min(height, firstRow + RowsPerChunk));
        });
    }

    private static void EnsureSameSize(Decoding.DecodedFrame a, Decoding.DecodedFrame b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            throw new ArgumentException($"Frame sizes differ: {a.Width}x{a.Height} vs {b.Width}x{b.Height}.");
        }
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
