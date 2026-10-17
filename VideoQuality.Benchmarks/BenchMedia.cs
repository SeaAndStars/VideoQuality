namespace VideoQuality.Benchmarks;

/// <summary>Locates the media the decoder benchmarks run against.</summary>
internal static class BenchMedia
{
    public static string Reference => Resolve("VQ_BENCH_REF", "/tmp/vqtest/ref1080.mkv");

    public static string Distorted => Resolve("VQ_BENCH_DIST", "/tmp/vqtest/dist1080.mp4");

    private static string Resolve(string variable, string fallback)
    {
        string path = Environment.GetEnvironmentVariable(variable) is { Length: > 0 } configured ? configured : fallback;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Benchmark media not found: {path}. Set {variable} to a video file.", path);
        }

        return path;
    }
}
