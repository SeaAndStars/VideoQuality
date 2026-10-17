using VideoQuality.Core.Models;

namespace VideoQuality.Tests;

public class VmafStatsTests
{
    [Fact]
    public void Computes_mean_extremes_and_deviation()
    {
        var frames = new[]
        {
            new VmafFrame(0, 100),
            new VmafFrame(1, 90),
            new VmafFrame(2, 80),
        };

        VmafStats stats = VmafStats.FromFrames(frames);

        Assert.Equal(3, stats.Count);
        Assert.Equal(90, stats.Mean, 6);
        Assert.Equal(80, stats.Min, 6);
        Assert.Equal(100, stats.Max, 6);
        Assert.Equal(8.164966, stats.StdDev, 4);

        // libvmaf's harmonic mean uses a +1 offset.
        Assert.Equal(3 / (1.0 / 101 + 1.0 / 91 + 1.0 / 81) - 1, stats.HarmonicMean, 6);
    }

    [Fact]
    public void Harmonic_mean_punishes_outliers()
    {
        var even = new[] { new VmafFrame(0, 90), new VmafFrame(1, 90), new VmafFrame(2, 90) };
        var spiky = new[] { new VmafFrame(0, 100), new VmafFrame(1, 90), new VmafFrame(2, 80) };

        Assert.True(VmafStats.FromFrames(spiky).HarmonicMean < VmafStats.FromFrames(even).HarmonicMean);
    }

    [Fact]
    public void First_percentile_interpolates()
    {
        VmafFrame[] frames = Enumerable.Range(0, 100)
            .Select(i => new VmafFrame(i, i))
            .ToArray();

        VmafStats stats = VmafStats.FromFrames(frames);

        // p1 of 0..99 lands 0.99 between 0 and 1.
        Assert.Equal(0.99, stats.P1, 6);
    }

    [Fact]
    public void Empty_and_single_frame_sets_are_handled()
    {
        Assert.Equal(0, VmafStats.FromFrames(Array.Empty<VmafFrame>()).Count);

        VmafStats single = VmafStats.FromFrames(new[] { new VmafFrame(7, 88.5) });

        Assert.Equal(1, single.Count);
        Assert.Equal(88.5, single.Mean, 6);
        Assert.Equal(88.5, single.HarmonicMean, 6);
        Assert.Equal(88.5, single.P1, 6);
        Assert.Equal(0, single.StdDev, 6);
    }
}
