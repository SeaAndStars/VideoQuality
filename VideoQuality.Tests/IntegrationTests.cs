using System.Diagnostics;
using VideoQuality.Core.Decoding;
using VideoQuality.Core.Models;
using VideoQuality.Core.Scoring;

namespace VideoQuality.Tests;

/// <summary>
/// Tests that need a real ffmpeg build and a real clip. They are skipped unless
/// <c>VQ_TEST_VIDEO</c> points at a local file and the resolved ffmpeg has libvmaf.
/// </summary>
[Collection("ffmpeg")]
public class IntegrationTests
{
    private static string? Video => Environment.GetEnvironmentVariable("VQ_TEST_VIDEO");

    private static async Task<(string Ffmpeg, VmafCapabilities Capabilities)> ResolveAsync()
    {
        string ffmpeg = await VmafRunner.ResolveFfmpegAsync(Environment.GetEnvironmentVariable("VQ_FFMPEG"));
        VmafCapabilities capabilities = await VmafRunner.ProbeAsync(ffmpeg);
        return (ffmpeg, capabilities);
    }

    [SkippableFact]
    public void Loaded_libavcodec_matches_the_bindings()
    {
        try
        {
            FfmpegRuntime.Initialize();
        }
        catch (Exception ex)
        {
            Skip.If(true, "libav could not be loaded: " + ex.Message);
        }

        Assert.Equal(FfmpegRuntime.ExpectedAvcodecMajor, FfmpegRuntime.LoadedAvcodecMajor);
    }

    [SkippableFact]
    public async Task Same_file_scores_near_100()
    {
        string? video = Video;
        Skip.If(string.IsNullOrWhiteSpace(video) || !File.Exists(video), "set VQ_TEST_VIDEO to a local clip");

        (string ffmpeg, VmafCapabilities capabilities) = await ResolveAsync();
        Skip.If(!capabilities.HasLibvmaf, "resolved ffmpeg has no libvmaf");

        VmafReport report = await VmafRunner.RunAsync(
            video!,
            video!,
            new VmafOptions { FfmpegPath = ffmpeg });

        Assert.True(report.FrameCount > 0);
        Assert.True(report.Stats.Mean > 99.0, $"mean was {report.Stats.Mean:F3}");
    }

    [SkippableFact]
    public async Task Cpu_and_cuda_agree_on_8_bit()
    {
        string? video = Video;
        Skip.If(string.IsNullOrWhiteSpace(video) || !File.Exists(video), "set VQ_TEST_VIDEO to a local clip");

        (string ffmpeg, VmafCapabilities capabilities) = await ResolveAsync();
        Skip.If(!capabilities.CudaUsable, "libvmaf_cuda or an NVIDIA GPU is not available");

        VmafReport cpu = await VmafRunner.RunAsync(
            video!,
            video!,
            new VmafOptions { FfmpegPath = ffmpeg, Backend = VmafBackend.Cpu });

        VmafReport gpu = await VmafRunner.RunAsync(
            video!,
            video!,
            new VmafOptions { FfmpegPath = ffmpeg, Backend = VmafBackend.Cuda });

        Assert.Equal("CPU", cpu.Backend.ToDisplayName());
        Assert.Equal("CUDA", gpu.Backend.ToDisplayName());
        Assert.True(Math.Abs(cpu.Stats.Mean - gpu.Stats.Mean) < 0.5,
            $"CPU {cpu.Stats.Mean:F3} vs CUDA {gpu.Stats.Mean:F3}");
    }

    [SkippableFact]
    public async Task Decoded_frame_matches_ffmpeg_extraction()
    {
        string? video = Video;
        Skip.If(string.IsNullOrWhiteSpace(video) || !File.Exists(video), "set VQ_TEST_VIDEO to a local clip");

        (string ffmpeg, _) = await ResolveAsync();
        Skip.If(ffmpeg is null, "no ffmpeg executable");

        using FrameDecoder decoder = FrameDecoder.Open(video!);
        DecodedFrame decoded = decoder.DecodeFrame(5);
        byte[] extracted = await ExtractBgraAsync(ffmpeg, video!, 5);

        Assert.Equal(decoded.Bgra.Length, extracted.Length);

        int maxDifference = 0;
        for (int i = 0; i < extracted.Length; i++)
        {
            maxDifference = Math.Max(maxDifference, Math.Abs(extracted[i] - decoded.Bgra[i]));
        }

        Assert.True(maxDifference <= 2, $"max channel difference was {maxDifference}");
    }

    [SkippableFact]
    public void Stepping_backwards_returns_the_same_pixels_as_a_fresh_decode()
    {
        string? video = Video;
        Skip.If(string.IsNullOrWhiteSpace(video) || !File.Exists(video), "set VQ_TEST_VIDEO to a local clip");

        using FrameDecoder stepper = FrameDecoder.Open(video!);
        int start = Math.Min(stepper.Info.FrameCount - 1, 40);
        stepper.DecodeFrame(start);

        // The first step back re-seeks and caches the frames before it; the rest are cache hits.
        for (int index = start - 1; index >= start - 6; index--)
        {
            DecodedFrame stepped = stepper.DecodeFrame(index);

            using FrameDecoder fresh = FrameDecoder.Open(video!);
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

        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(video);
        startInfo.ArgumentList.Add("-vf");
        startInfo.ArgumentList.Add($"select=eq(n\\,{index})");
        startInfo.ArgumentList.Add("-frames:v");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("rawvideo");
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("bgra");
        startInfo.ArgumentList.Add("-");

        using var process = Process.Start(startInfo)!;
        using var buffer = new MemoryStream();
        Task copy = process.StandardOutput.BaseStream.CopyToAsync(buffer);
        Task<string> error = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();
        await copy;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(await error);
        }

        return buffer.ToArray();
    }
}
