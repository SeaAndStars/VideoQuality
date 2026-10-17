using BenchmarkDotNet.Attributes;
using VideoQuality.Core.Decoding;
using VideoQuality.Core.Imaging;

namespace VideoQuality.Benchmarks;

/// <summary>Difference image and auto-gain percentile on a 1080p frame pair.</summary>
[MemoryDiagnoser]
public class PixelDiffBenchmarks
{
    private DecodedFrame _a = null!;
    private DecodedFrame _b = null!;

    [Params(DifferenceChannels.Luma, DifferenceChannels.Both)]
    public DifferenceChannels Channels { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        const int Width = 1920;
        const int Height = 1080;
        var random = new Random(42);
        var a = new byte[Width * Height * 4];
        var b = new byte[a.Length];
        random.NextBytes(a);

        // Encode-like: mostly small errors, occasional large ones.
        for (int i = 0; i < a.Length; i++)
        {
            int noise = random.Next(100) < 97 ? random.Next(-3, 4) : random.Next(-60, 61);
            b[i] = (byte)Math.Clamp(a[i] + noise, 0, 255);
        }

        _a = new DecodedFrame(Width, Height, Width * 4, a, 0);
        _b = new DecodedFrame(Width, Height, Width * 4, b, 0);
    }

    [Benchmark]
    public DecodedFrame Difference() => PixelDiff.Difference(_a, _b, 8, Channels);

    [Benchmark]
    public double Percentile() => PixelDiff.DifferencePercentile(_a, _b, 0.999, Channels);
}
