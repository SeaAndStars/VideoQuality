using VideoQuality.Core.Ingest;

namespace VideoQuality.Tests;

public class StreamMismatchTests
{
    private static readonly StreamShape Reference = new(1920, 1080, 24000 / 1001.0, 34525);

    [Fact]
    public void Matching_streams_have_no_warning() =>
        Assert.Null(StreamMismatch.Describe(Reference, Reference with { }));

    [Fact]
    public void Unknown_reference_count_is_not_a_mismatch()
    {
        // An MKV reference before its packet walk finishes: count unknown, not "34526".
        Assert.Null(StreamMismatch.Describe(Reference with { Frames = 0 }, Reference));
    }

    [Fact]
    public void Exact_counts_that_differ_are_reported()
    {
        string? warning = StreamMismatch.Describe(Reference, Reference with { Frames = 34500 });
        Assert.Equal("Mismatch: 34500 frames vs reference 34525", warning);
    }

    [Fact]
    public void Resolution_and_rate_are_reported_together()
    {
        string? warning = StreamMismatch.Describe(Reference, new StreamShape(1280, 720, 25, 34525));
        Assert.Equal("Mismatch: resolution 1280×720 vs reference 1920×1080; 25 fps vs reference 23.976 fps", warning);
    }
}
