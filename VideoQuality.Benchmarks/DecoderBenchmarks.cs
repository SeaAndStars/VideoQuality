using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using VideoQuality.Core.Decoding;

namespace VideoQuality.Benchmarks;

/// <summary>
/// The viewer's decode patterns: playback (sequential), stepping backwards (cache miss → keyframe
/// seek), random scrubbing, and decoding the reference/encode pair the way the UI does.
/// </summary>
/// <remarks>
/// Each iteration reopens the decoders in <see cref="IterationSetup"/>, so every measurement starts
/// from a cold cache and the open cost is not part of the number.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, warmupCount: 1, iterationCount: 10, invocationCount: 1)]
public class DecoderBenchmarks
{
    private const int PlaybackFrames = 60;

    private FrameDecoder? _reference;
    private FrameDecoder? _distorted;
    private int _start;
    private int[] _scrubTargets = [];

    [GlobalSetup]
    public void Setup()
    {
        using FrameDecoder reference = FrameDecoder.Open(BenchMedia.Reference);
        using FrameDecoder distorted = FrameDecoder.Open(BenchMedia.Distorted);
        int frameCount = Math.Min(reference.Info.FrameCount, distorted.Info.FrameCount);

        _start = frameCount / 2;
        var random = new Random(7);
        _scrubTargets = Enumerable.Range(0, 10).Select(_ => random.Next(frameCount)).ToArray();
    }

    [IterationSetup]
    public void Reopen()
    {
        DisposeDecoders();
        _reference = FrameDecoder.Open(BenchMedia.Reference);
        _distorted = FrameDecoder.Open(BenchMedia.Distorted);

        // Park both decoders just before the measured range: playback continues from here.
        _reference.DecodeFrame(_start - 1);
        _distorted.DecodeFrame(_start - 1);
    }

    [IterationCleanup]
    public void DisposeDecoders()
    {
        _reference?.Dispose();
        _distorted?.Dispose();
        _reference = null;
        _distorted = null;
    }

    /// <summary>One decoder, 60 frames in order: sequential decode + BGRA conversion.</summary>
    [Benchmark]
    public long Sequential()
    {
        long checksum = 0;
        for (int i = _start; i < _start + PlaybackFrames; i++)
        {
            checksum += _distorted!.DecodeFrame(i).Bgra[0];
        }

        return checksum;
    }

    /// <summary>Reference and encode, 60 frames, one after the other (the viewer before).</summary>
    [Benchmark(Baseline = true)]
    public long PairSerial()
    {
        long checksum = 0;
        for (int i = _start; i < _start + PlaybackFrames; i++)
        {
            checksum += _reference!.DecodeFrame(i).Bgra[0];
            checksum += _distorted!.DecodeFrame(i).Bgra[0];
        }

        return checksum;
    }

    /// <summary>Reference and encode, 60 frames, the two decoders running concurrently.</summary>
    [Benchmark]
    public long PairParallel()
    {
        long checksum = 0;
        for (int i = _start; i < _start + PlaybackFrames; i++)
        {
            int index = i;
            Task<DecodedFrame> reference = Task.Run(() => _reference!.DecodeFrame(index));
            DecodedFrame distorted = _distorted!.DecodeFrame(index);
            checksum += reference.Result.Bgra[0] + distorted.Bgra[0];
        }

        return checksum;
    }

    /// <summary>Left arrow ×10: every step back is a cache miss that re-seeks.</summary>
    [Benchmark]
    public long StepBackward()
    {
        long checksum = 0;
        for (int i = _start - 2; i > _start - 12; i--)
        {
            checksum += _distorted!.DecodeFrame(i).Bgra[0];
        }

        return checksum;
    }

    /// <summary>Ten random slider jumps.</summary>
    [Benchmark]
    public long Scrub()
    {
        long checksum = 0;
        foreach (int target in _scrubTargets)
        {
            checksum += _distorted!.DecodeFrame(target).Bgra[0];
        }

        return checksum;
    }
}
