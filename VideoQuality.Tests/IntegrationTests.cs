using System.Diagnostics;
using FFmpeg.AutoGen;
using VideoQuality.Core.Decoding;
using VideoQuality.Core.Models;
using VideoQuality.Core.Scoring;

namespace VideoQuality.Tests;

/// <summary>Decoding and scoring on generated clips with the real libav libraries and ffmpeg.</summary>
[Collection("ffmpeg")]
public class IntegrationTests
{
    private static async Task<(string Ffmpeg, VmafCapabilities Capabilities)> ResolveAsync()
    {
        string ffmpeg = await VmafRunner.ResolveFfmpegAsync(Environment.GetEnvironmentVariable("VQ_FFMPEG"));
        VmafCapabilities capabilities = await VmafRunner.ProbeAsync(ffmpeg);
        return (ffmpeg, capabilities);
    }

    [Fact]
    public void Loaded_libavcodec_matches_the_bindings()
    {
        FfmpegRuntime.Initialize();
        Assert.Equal(FfmpegRuntime.ExpectedAvcodecMajor, FfmpegRuntime.LoadedAvcodecMajor);
    }

    [Fact]
    public async Task Same_file_scores_near_100()
    {
        string video = await TestMedia.ClipAsync();
        (string ffmpeg, VmafCapabilities capabilities) = await ResolveAsync();
        Assert.True(capabilities.HasLibvmaf, "the resolved ffmpeg has no libvmaf");

        VmafReport report = await VmafRunner.RunAsync(video, video, new VmafOptions { FfmpegPath = ffmpeg, Backend = VmafBackend.Cpu });

        Assert.Equal(48, report.FrameCount);
        Assert.True(report.Stats.Mean > 99.0, $"mean was {report.Stats.Mean:F3}");
    }

    [Fact]
    public async Task Cpu_and_cuda_agree_on_8_bit()
    {
        string video = await TestMedia.ClipAsync();
        (string ffmpeg, VmafCapabilities capabilities) = await ResolveAsync();
        // A real GPU is optional (CI has none); the CUDA paths are covered with scripted tools.
        Assert.SkipWhen(!capabilities.CudaUsable, "libvmaf_cuda or an NVIDIA GPU is not available");

        VmafReport cpu = await VmafRunner.RunAsync(video, video, new VmafOptions { FfmpegPath = ffmpeg, Backend = VmafBackend.Cpu });
        VmafReport gpu = await VmafRunner.RunAsync(video, video, new VmafOptions { FfmpegPath = ffmpeg, Backend = VmafBackend.Cuda });

        Assert.Equal("CPU", cpu.Backend.ToDisplayName());
        Assert.Equal("CUDA", gpu.Backend.ToDisplayName());
        Assert.True(Math.Abs(cpu.Stats.Mean - gpu.Stats.Mean) < 0.5, $"CPU {cpu.Stats.Mean:F3} vs CUDA {gpu.Stats.Mean:F3}");
    }

    [Fact]
    public async Task Decoded_frame_matches_ffmpeg_extraction()
    {
        string video = await TestMedia.ClipAsync();
        using FrameDecoder decoder = FrameDecoder.Open(video);
        DecodedFrame decoded = decoder.DecodeFrame(5);
        byte[] extracted = await ExtractBgraAsync(TestMedia.RequireEncoder(), video, 5);

        Assert.Equal(decoded.Bgra.Length, extracted.Length);
        int maxDifference = 0;
        for (int i = 0; i < extracted.Length; i++)
        {
            maxDifference = Math.Max(maxDifference, Math.Abs(extracted[i] - decoded.Bgra[i]));
        }

        Assert.True(maxDifference <= 2, $"max channel difference was {maxDifference}");
    }

    [Fact]
    public async Task Stepping_backwards_returns_the_same_pixels_as_a_fresh_decode()
    {
        string video = await TestMedia.ClipAsync();
        using FrameDecoder stepper = FrameDecoder.Open(video);
        int start = Math.Min(stepper.Info.FrameCount - 1, 40);
        stepper.DecodeFrame(start);

        // The first step back re-seeks and caches the frames before it; the rest are cache hits.
        for (int index = start - 1; index >= start - 6; index--)
        {
            DecodedFrame stepped = stepper.DecodeFrame(index);
            using FrameDecoder fresh = FrameDecoder.Open(video);
            Assert.Equal(fresh.DecodeFrame(index).Bgra, stepped.Bgra);
        }
    }

    private static async Task<byte[]> ExtractBgraAsync(string ffmpeg, string video, int index)
    {
        var startInfo = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string argument in new[] { "-v", "error", "-i", video, "-vf", $"select=eq(n\\,{index})", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", "-" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        using var buffer = new MemoryStream();
        Task copy = process.StandardOutput.BaseStream.CopyToAsync(buffer);
        await process.WaitForExitAsync();
        await copy;
        Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
        return buffer.ToArray();
    }
}

/// <summary>FrameDecoder on every kind of input it meets.</summary>
[Collection("ffmpeg")]
public class FrameDecoderTests
{
    [Fact]
    public async Task Reads_the_properties_of_each_kind_of_clip()
    {
        using (FrameDecoder hevc = FrameDecoder.Open(await TestMedia.Hevc10Async()))
        {
            Assert.Equal(("hevc", 10, "bt709", "tv", true), (hevc.Info.Codec, hevc.Info.BitDepth, hevc.Info.ColorSpace, hevc.Info.ColorRange, hevc.Info.IsLossy));
            Assert.Equal("10-bit · lossy", hevc.Info.Badge);
        }

        using (FrameDecoder ffv1 = FrameDecoder.Open(await TestMedia.LosslessAsync()))
        {
            Assert.Equal(("ffv1", "bt601", "pc", false), (ffv1.Info.Codec, ffv1.Info.ColorSpace, ffv1.Info.ColorRange, ffv1.Info.IsLossy));
            Assert.Equal(string.Empty, ffv1.Info.Badge);
        }

        using (FrameDecoder mp4 = FrameDecoder.Open(await TestMedia.Mp4Async()))
        {
            Assert.Equal((48, "smpte170m"), (mp4.Info.FrameCount, mp4.Info.ColorSpace));
            Assert.True(mp4.Info.HasKnownFrameCount);
        }

        using (FrameDecoder av1 = FrameDecoder.Open(await TestMedia.Av1Async()))
        {
            Assert.Equal(("av1", "bt2020"), (av1.Info.Codec, av1.Info.ColorSpace));
            Assert.Equal(320, av1.DecodeFrame(3).Width);
        }

        using FrameDecoder raw = FrameDecoder.Open(await TestMedia.RawH264Async());
        Assert.Equal(string.Empty, raw.Info.ColorRange);
        Assert.Equal(180, raw.DecodeFrame(10).Height);
        Assert.Contains("320x180", raw.Info.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clamps_requests_and_serves_repeats_from_the_cache()
    {
        using FrameDecoder decoder = FrameDecoder.Open(await TestMedia.ClipAsync());
        Assert.Equal(64, decoder.CacheCapacity);
        DecodedFrame first = decoder.DecodeFrame(-5);
        Assert.Same(first, decoder.DecodeFrame(0));
        DecodedFrame last = decoder.DecodeFrame(10_000);
        Assert.Same(last, decoder.DecodeFrame(decoder.Info.FrameCount - 1));
    }

    [Fact]
    public async Task Long_jumps_seek_and_the_cache_evicts_old_frames()
    {
        using FrameDecoder decoder = FrameDecoder.Open(await TestMedia.LongAsync());
        decoder.DecodeFrame(0);
        decoder.DecodeFrame(300);            // more than MaxForwardDecode ahead: seeks
        decoder.DecodeFrame(301);            // the next frame: decodes forward
        decoder.DecodeFrame(100);            // a long jump back: seeks without keeping neighbours
        for (int i = 101; i < 101 + 80; i++) // more than the cache holds
        {
            decoder.DecodeFrame(i);
        }

        DecodedFrame again = decoder.DecodeFrame(100);
        Assert.Equal(320 * 4, again.Stride);
    }

    [Fact]
    public async Task Past_the_end_of_a_stream_without_a_frame_count_throws()
    {
        using FrameDecoder decoder = FrameDecoder.Open(await TestMedia.RawH264Async());
        Assert.False(decoder.Info.HasKnownFrameCount && decoder.Info.FrameCount > 1000);
        Assert.Throws<EndOfStreamException>(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                decoder.DecodeFrame(i * 5);
            }
        });
    }

    [Fact]
    public async Task Bad_inputs_are_reported()
    {
        Assert.Throws<FileNotFoundException>(() => FrameDecoder.Open("/no/such/video.mkv"));
        Assert.Throws<ArgumentException>(() => FrameDecoder.Open(" "));
        string audioOnly = await TestMedia.AudioOnlyAsync();
        Assert.Throws<InvalidOperationException>(() => FrameDecoder.Open(audioOnly));
        string junk = Path.Combine(Path.GetTempPath(), "vq-junk-" + Guid.NewGuid().ToString("N") + ".mkv");
        File.WriteAllText(junk, "not a video");
        Assert.Throws<InvalidOperationException>(() => FrameDecoder.Open(junk));
        File.Delete(junk);

        FrameDecoder disposed = FrameDecoder.Open(await TestMedia.ClipAsync());
        disposed.Dispose();
        disposed.Dispose();
        Assert.Throws<ObjectDisposedException>(() => disposed.DecodeFrame(0));
    }

    [Theory]
    [InlineData(12, "yuv420p", 12)]
    [InlineData(0, "p016le", 16)]
    [InlineData(0, "p012le", 12)]
    [InlineData(0, "p010le", 10)]
    [InlineData(0, "yuv420p10le", 10)]
    [InlineData(0, "yuv444p12be", 12)]
    [InlineData(0, "gray16le", 16)]
    [InlineData(0, "yuv420p9le", 9)]
    [InlineData(0, "yuv420p", 8)]
    public void Bit_depth_comes_from_the_raw_sample_size_or_the_pixel_format(int bits, string format, int expected) =>
        Assert.Equal(expected, FrameDecoder.ResolveBitDepth(bits, format));

    [Fact]
    public void Colour_and_codec_classification()
    {
        Assert.Equal("bt2020", FrameDecoder.ResolveColorSpace(AVColorSpace.AVCOL_SPC_BT2020_CL));
        Assert.Equal(string.Empty, FrameDecoder.ResolveColorSpace(AVColorSpace.AVCOL_SPC_UNSPECIFIED));
        foreach (AVCodecID lossless in new[] { AVCodecID.AV_CODEC_ID_RAWVIDEO, AVCodecID.AV_CODEC_ID_HUFFYUV, AVCodecID.AV_CODEC_ID_UTVIDEO, AVCodecID.AV_CODEC_ID_PNG, AVCodecID.AV_CODEC_ID_QTRLE })
        {
            Assert.False(FrameDecoder.IsLossyCodec(lossless));
        }

        Assert.Equal(4, FrameDecoder.CacheCapacityFor(3840 * 4, 2160 * 4));
    }

    [Fact]
    public void Frame_rate_falls_back_from_average_to_real_to_25()
    {
        Assert.Equal(24, FrameDecoder.ResolveFrameRate(new AVRational { num = 24, den = 1 }, new AVRational { num = 30, den = 1 }));
        Assert.Equal(30, FrameDecoder.ResolveFrameRate(new AVRational { num = 0, den = 1 }, new AVRational { num = 30, den = 1 }));
        Assert.Equal(30, FrameDecoder.ResolveFrameRate(new AVRational { num = 1, den = 0 }, new AVRational { num = 30, den = 1 }));
        Assert.Equal(25, FrameDecoder.ResolveFrameRate(new AVRational { num = -1, den = 1 }, new AVRational { num = 0, den = 1 }));
        Assert.Equal(25, FrameDecoder.ResolveFrameRate(default, new AVRational { num = 1, den = 0 }));
    }

    [Fact]
    public void Error_helpers_throw_with_libavs_text()
    {
        FrameDecoder.Ensure(true, "decoder.error.native", "x");
        Assert.Contains("x failed", Assert.Throws<InvalidOperationException>(() => FrameDecoder.Ensure(false, "decoder.error.native", "x")).Message, StringComparison.Ordinal);
        FrameDecoder.Check(0, "decoder.error.call", "y");
        Assert.StartsWith("y failed: ", Assert.Throws<InvalidOperationException>(() => FrameDecoder.Check(-22, "decoder.error.call", "y")).Message, StringComparison.Ordinal);
        FrameDecoder.CheckReceive(ffmpeg.AVERROR(ffmpeg.EAGAIN));
        Assert.Throws<InvalidOperationException>(() => FrameDecoder.CheckReceive(-22));
    }

    [Fact]
    public void Seek_falls_back_to_a_plain_seek_when_backward_fails()
    {
        var flags = new List<int>();
        FrameDecoder.SeekWithFallback(f => { flags.Add(f); return 0; });
        Assert.Equal([1], flags);
        flags.Clear();
        FrameDecoder.SeekWithFallback(f => { flags.Add(f); return -1; });
        Assert.Equal([1, 0], flags);
    }
}
