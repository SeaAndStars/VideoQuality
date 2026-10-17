namespace VideoQuality.Core.Models;

/// <summary>
/// Pooled statistics over a set of per-frame VMAF scores.
/// </summary>
public sealed record VmafStats(
    int Count,
    double Mean,
    double HarmonicMean,
    double Min,
    double Max,
    double StdDev,
    double P1)
{
    public static VmafStats Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);

    /// <summary>
    /// Matches libvmaf's pooled metrics. Harmonic mean uses libvmaf's +1 offset so that
    /// a score of 0 does not produce a division by zero.
    /// </summary>
    public static VmafStats FromFrames(IReadOnlyList<VmafFrame> frames)
    {
        if (frames.Count == 0)
        {
            return Empty;
        }

        var values = new double[frames.Count];
        double sum = 0;
        double inverseSum = 0;
        double min = double.MaxValue;
        double max = double.MinValue;

        for (int i = 0; i < frames.Count; i++)
        {
            double v = frames[i].Vmaf;
            values[i] = v;
            sum += v;
            inverseSum += 1.0 / (v + 1.0);
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }

        double mean = sum / values.Length;
        double harmonicMean = values.Length / inverseSum - 1.0;

        double variance = 0;
        foreach (double v in values)
        {
            double d = v - mean;
            variance += d * d;
        }

        variance /= values.Length;

        Array.Sort(values);

        return new VmafStats(
            frames.Count,
            mean,
            harmonicMean,
            min,
            max,
            Math.Sqrt(variance),
            Percentile(values, 0.01));
    }

    /// <summary>Linear-interpolated percentile, <paramref name="p"/> in 0..1 over a sorted array.</summary>
    private static double Percentile(double[] sortedValues, double p)
    {
        if (sortedValues.Length == 1)
        {
            return sortedValues[0];
        }

        double position = p * (sortedValues.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = Math.Min(lower + 1, sortedValues.Length - 1);
        double fraction = position - lower;

        return sortedValues[lower] + (sortedValues[upper] - sortedValues[lower]) * fraction;
    }
}
