using VideoQuality.Core;
using VideoQuality.Core.Models;
using VideoQuality.Core.Scoring;
using VideoQuality.Core.Tools;

namespace VideoQuality.Tests;

/// <summary>
/// VmafRunner's decisions (probing, backend choice, CUDA fallback, resolving ffmpeg, log parsing)
/// with scripted processes. Shares the "ffmpeg" collection: these tests change environment variables
/// and the bundled-ffmpeg folder, which the integration tests read.
/// </summary>
[Collection("ffmpeg")]
public sealed class VmafRunnerTests : IDisposable
{
    private const string Filters = " ... libvmaf          VV->V      Calculate the VMAF\n ... libvmaf_cuda     VV->V      Calculate the VMAF (CUDA)\n";
    private const string GoodLog = """{"frames":[{"frameNum":0,"metrics":{"vmaf":90.5}},{"frameNum":1,"metrics":{"vmaf":91.5}}]}""";
    private readonly string _work = Path.Combine(Path.GetTempPath(), "vq-vmaf-" + Guid.NewGuid().ToString("N"));
    private readonly IReadOnlyList<string> _smiPaths = VmafRunner.NvidiaSmiPaths;
    private readonly IReadOnlyList<string> _wellKnown = VmafRunner.WellKnownFfmpegPaths;

    public VmafRunnerTests()
    {
        Directory.CreateDirectory(_work);
        VmafRunner.ClearCapabilityCache();
        VmafRunner.NvidiaSmiPaths = [];
        VmafRunner.WellKnownFfmpegPaths = [];
    }

    public void Dispose()
    {
        VmafRunner.ClearCapabilityCache();
        VmafRunner.NvidiaSmiPaths = _smiPaths;
        VmafRunner.WellKnownFfmpegPaths = _wellKnown;
        VmafRunner.ProbeTimeout = TimeSpan.FromSeconds(60);
        Directory.Delete(_work, true);
    }

    private string Ffmpeg(string name = "ffmpeg") => Path.Combine(_work, name + "-" + Guid.NewGuid().ToString("N")[..6]);

    private string Video(string name)
    {
        string path = Path.Combine(_work, name);
        File.WriteAllText(path, "not media: the bit-depth probe falls back to 8");
        return path;
    }

    /// <summary>A runner where ffmpeg has the given filters, nvidia-smi says <paramref name="gpu"/>, and scoring writes <paramref name="log"/>.</summary>
    private static ScriptedRunner Tools(string filters = Filters, bool gpu = true, string? log = GoodLog, Func<ProcessRequest, int>? exit = null) =>
        new(request =>
        {
            if (request.FileName.Contains("nvidia-smi", StringComparison.Ordinal))
            {
                return gpu ? ScriptedRunner.Ok("GPU 0: NVIDIA GeForce RTX 5060 Ti (UUID: x)\n") : ScriptedRunner.Ok("No devices were found\n");
            }

            if (request.Arguments.Contains("-filters"))
            {
                return ScriptedRunner.Ok(filters);
            }

            if (request.Arguments.Contains("-version"))
            {
                return ScriptedRunner.Ok("ffmpeg version 9.0 test\nbuilt with gcc\n");
            }

            ScriptedRunner.Emit(request, "frame=1", "fps=10", "progress=continue", "frame=2", "progress=end");
            int code = exit?.Invoke(request) ?? 0;
            if (code == 0 && log is not null)
            {
                File.WriteAllText(ScriptedRunner.LogPath(request), log);
            }

            return new ProcessResult(code, string.Empty, code == 0 ? string.Empty : new string('e', 5000) + "\nlast error line");
        });

    [Fact]
    public async Task Probe_reads_filters_version_and_gpu_and_caches()
    {
        ScriptedRunner runner = Tools();
        string ffmpeg = Ffmpeg();

        VmafCapabilities capabilities = await VmafRunner.ProbeAsync(ffmpeg, runner, TestContext.Current.CancellationToken);

        Assert.Equal(new VmafCapabilities(true, true, true, "ffmpeg version 9.0 test"), capabilities);
        Assert.True(capabilities.CudaUsable);
        Assert.Equal("libvmaf, libvmaf_cuda, NVIDIA GPU present", capabilities.Describe());
        int requests = runner.Requests.Count;
        Assert.Same(capabilities, await VmafRunner.ProbeAsync(ffmpeg, runner, TestContext.Current.CancellationToken));
        Assert.Equal(requests, runner.Requests.Count);
    }

    [Fact]
    public async Task Gpu_detection_tries_path_then_driver_locations()
    {
        string driverSmi = Path.Combine(_work, "nvidia-smi");
        File.WriteAllText(driverSmi, string.Empty);
        VmafRunner.NvidiaSmiPaths = [Path.Combine(_work, "missing-smi"), driverSmi];
        var runner = new ScriptedRunner(request => request.FileName switch
        {
            "nvidia-smi" or "nvidia-smi.exe" => throw new ProcessStartException(request.FileName, new FileNotFoundException()),
            _ when request.FileName == driverSmi => ScriptedRunner.Ok("GPU 0: test"),
            _ => ScriptedRunner.Ok(" ... libvmaf   VV->V  x\n"),
        });

        VmafCapabilities found = await VmafRunner.ProbeAsync(Ffmpeg(), runner, TestContext.Current.CancellationToken);
        Assert.True(found.HasNvidiaGpu);
        Assert.False(found.HasLibvmafCuda);
        Assert.Equal("libvmaf, no libvmaf_cuda, NVIDIA GPU present", found.Describe());

        var none = new ScriptedRunner(request => request.FileName.Contains("smi", StringComparison.Ordinal)
            ? throw new ProcessStartException(request.FileName, new FileNotFoundException())
            : ScriptedRunner.Ok(string.Empty));
        VmafCapabilities nothing = await VmafRunner.ProbeAsync(Ffmpeg(), none, TestContext.Current.CancellationToken);
        Assert.Equal("no libvmaf, no libvmaf_cuda, no NVIDIA GPU", nothing.Describe());
        Assert.Equal("ffmpeg (unknown version)", nothing.FfmpegVersion);
    }

    [Fact]
    public async Task Probe_failures_and_timeouts_report_unavailable()
    {
        var missing = new ScriptedRunner(request => throw new ProcessStartException(request.FileName, new FileNotFoundException("gone")));
        VmafCapabilities failed = await VmafRunner.ProbeAsync(Ffmpeg(), missing, TestContext.Current.CancellationToken);
        Assert.StartsWith("unavailable: ", failed.FfmpegVersion, StringComparison.Ordinal);

        VmafRunner.ProbeTimeout = TimeSpan.FromMilliseconds(100);
        var hanging = new ScriptedRunner(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return ScriptedRunner.Ok();
        });
        string ffmpeg = Ffmpeg();
        VmafCapabilities slow = await VmafRunner.ProbeAsync(ffmpeg, hanging, TestContext.Current.CancellationToken);
        Assert.Contains("0.1 s", slow.FfmpegVersion, StringComparison.Ordinal);
        await VmafRunner.ProbeAsync(ffmpeg, hanging, TestContext.Current.CancellationToken);
        Assert.Equal(2, hanging.Requests.Count); // a timeout is not cached: probed again (the first request hangs each time)

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VmafRunner.ProbeAsync(Ffmpeg(), hanging, cancelled.Token));
    }

    [Fact]
    public async Task Cpu_run_reports_progress_and_frames()
    {
        ScriptedRunner runner = Tools(gpu: false);
        var progress = new List<VmafProgress>();
        var options = new VmafOptions { FfmpegPath = Ffmpeg(), ProcessRunner = runner, Subsample = 2, Threads = 999, Model = " " };

        VmafReport report = await VmafRunner.RunAsync(Video("r.mkv"), Video("d.mkv"), options, new SyncProgress<VmafProgress>(progress.Add), TestContext.Current.CancellationToken);

        Assert.Equal(VmafExecutionBackend.Cpu, report.Backend);
        Assert.Equal(2, report.FrameCount);
        Assert.Equal(91, report.Stats.Mean);
        Assert.Equal(8, report.SourceBitDepth);
        Assert.False(report.ConvertedTo8Bit);
        Assert.Equal(VmafModels.Default, report.Model);
        Assert.Equal([1, 2], progress.Select(p => p.FrameNumber!.Value));
        string filter = runner.Requests.Last().Arguments.Single(a => a.Contains("libvmaf=", StringComparison.Ordinal));
        Assert.Contains(":n_threads=64:n_subsample=2:model=version=" + VmafModels.Default, filter, StringComparison.Ordinal);
        Assert.False(File.Exists(ScriptedRunner.LogPath(runner.Requests.Last())));
    }

    [Fact]
    public async Task Cuda_run_with_a_model_file_keeps_its_log_and_notes_the_8_bit_conversion()
    {
        string hevc10 = await TestMedia.Hevc10Async();
        ScriptedRunner runner = Tools();
        var options = new VmafOptions { FfmpegPath = Ffmpeg(), ProcessRunner = runner, Backend = VmafBackend.Cuda, Model = "/models/custom.json", KeepLog = true };

        VmafReport report = await VmafRunner.RunAsync(hevc10, hevc10, options, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(VmafExecutionBackend.Cuda, report.Backend);
        Assert.True(report.ConvertedTo8Bit);
        Assert.Equal(10, report.SourceBitDepth);
        string filter = runner.Requests.Last().Arguments.Single(a => a.Contains("libvmaf_cuda=", StringComparison.Ordinal));
        Assert.Contains("scale_cuda", filter, StringComparison.Ordinal);
        Assert.Contains(":model=path=/models/custom.json", filter, StringComparison.Ordinal);
        Assert.Contains("\"[0:v]scale_cuda", report.CommandLine, StringComparison.Ordinal);
        string log = ScriptedRunner.LogPath(runner.Requests.Last());
        Assert.True(File.Exists(log));
        File.Delete(log);
    }

    [Fact]
    public async Task Auto_falls_back_to_cpu_when_cuda_fails()
    {
        ScriptedRunner runner = Tools(exit: request => request.Arguments.Any(a => a.Contains("libvmaf_cuda", StringComparison.Ordinal)) ? 1 : 0);
        VmafReport report = await VmafRunner.RunAsync(Video("r.mkv"), Video("d.mkv"),
            new VmafOptions { FfmpegPath = Ffmpeg(), ProcessRunner = runner }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(report.FellBackToCpu);
        Assert.Equal(VmafExecutionBackend.Cpu, report.Backend);
        Assert.EndsWith("last error line", report.FallbackReason!.Split('\n').First(l => l.Contains("last error", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explicit_backends_fail_clearly()
    {
        string r = Video("r.mkv"), d = Video("d.mkv");
        CancellationToken token = TestContext.Current.CancellationToken;

        // CUDA requested, but this ffmpeg has no libvmaf_cuda and there is no GPU.
        var noCuda = new VmafOptions { FfmpegPath = Ffmpeg(), ProcessRunner = Tools(filters: " ... libvmaf  VV->V x\n", gpu: false), Backend = VmafBackend.Cuda };
        string both = (await Assert.ThrowsAsync<InvalidOperationException>(() => VmafRunner.RunAsync(r, d, noCuda, cancellationToken: token))).Message;
        Assert.Contains("libvmaf_cuda", both, StringComparison.Ordinal);
        Assert.Contains("nvidia-smi", both, StringComparison.Ordinal);

        var gpuOnly = new VmafOptions { FfmpegPath = Ffmpeg(), ProcessRunner = Tools(filters: " ... libvmaf  VV->V x\n"), Backend = VmafBackend.Cuda };
        string filterOnly = (await Assert.ThrowsAsync<InvalidOperationException>(() => VmafRunner.RunAsync(r, d, gpuOnly, cancellationToken: token))).Message;
        Assert.DoesNotContain("nvidia-smi", filterOnly, StringComparison.Ordinal);

        var noGpu = new VmafOptions { FfmpegPath = Ffmpeg(), ProcessRunner = Tools(gpu: false), Backend = VmafBackend.Cuda };
        Assert.DoesNotContain("libvmaf_cuda filter", (await Assert.ThrowsAsync<InvalidOperationException>(() => VmafRunner.RunAsync(r, d, noGpu, cancellationToken: token))).Message, StringComparison.Ordinal);

        var cpuWithout = new VmafOptions { FfmpegPath = Ffmpeg(), ProcessRunner = Tools(filters: string.Empty), Backend = VmafBackend.Cpu };
        await Assert.ThrowsAsync<InvalidOperationException>(() => VmafRunner.RunAsync(r, d, cpuWithout, cancellationToken: token));

        var autoWithout = new VmafOptions { FfmpegPath = Ffmpeg(), ProcessRunner = Tools(filters: string.Empty, gpu: false) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => VmafRunner.RunAsync(r, d, autoWithout, cancellationToken: token));

        // An explicit CUDA run that fails is not retried on the CPU.
        var cudaFails = new VmafOptions { FfmpegPath = Ffmpeg(), ProcessRunner = Tools(exit: _ => 3), Backend = VmafBackend.Cuda };
        Assert.Contains("code 3", (await Assert.ThrowsAsync<InvalidOperationException>(() => VmafRunner.RunAsync(r, d, cudaFails, cancellationToken: token))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inputs_and_logs_are_validated()
    {
        string r = Video("r.mkv"), d = Video("d.mkv");
        CancellationToken token = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<ArgumentException>(() => VmafRunner.RunAsync(" ", d, cancellationToken: token));
        await Assert.ThrowsAsync<ArgumentException>(() => VmafRunner.RunAsync(r, "", cancellationToken: token));
        await Assert.ThrowsAsync<FileNotFoundException>(() => VmafRunner.RunAsync(r + ".x", d, cancellationToken: token));
        await Assert.ThrowsAsync<FileNotFoundException>(() => VmafRunner.RunAsync(r, d + ".x", cancellationToken: token));
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VmafRunner.RunAsync(r, d, null, null, cancelled.Token));
        }

        VmafOptions Cpu(string? log) => new() { FfmpegPath = Ffmpeg(), ProcessRunner = Tools(gpu: false, log: log), Backend = VmafBackend.Cpu };
        await Assert.ThrowsAsync<InvalidOperationException>(() => VmafRunner.RunAsync(r, d, Cpu(null), cancellationToken: token));
        await Assert.ThrowsAsync<InvalidDataException>(() => VmafRunner.RunAsync(r, d, Cpu("""{"x":1}"""), cancellationToken: token));
        await Assert.ThrowsAsync<InvalidDataException>(() => VmafRunner.RunAsync(r, d, Cpu("""{"frames":3}"""), cancellationToken: token));
        await Assert.ThrowsAsync<InvalidDataException>(() => VmafRunner.RunAsync(r, d, Cpu("""{"frames":[{"metrics":{}},{"metrics":3},{}]}"""), cancellationToken: token));
    }

    [Fact]
    public async Task Log_parsing_accepts_other_metric_names_and_missing_frame_numbers()
    {
        const string log = """{"frames":[{"metrics":{"vmaf_neg":80}},{"frameNum":"x","metrics":{"psnr":70}},{"frameNum":7,"metrics":{"vmaf":"n/a","vmaf_x":"y","other":60}}]}""";
        VmafReport report = await VmafRunner.RunAsync(Video("r.mkv"), Video("d.mkv"),
            new VmafOptions { FfmpegPath = Ffmpeg(), ProcessRunner = Tools(gpu: false, log: log), Backend = VmafBackend.Cpu }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([(0, 80.0), (1, 70.0), (7, 60.0)], report.Frames.Select(f => (f.FrameNum, f.Vmaf)));
    }

    [Fact]
    public async Task Resolve_prefers_explicit_then_env_then_bundled_then_path_then_custom_builds()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        Assert.Equal("/x/ffmpeg", await VmafRunner.ResolveFfmpegAsync(" /x/ffmpeg ", Tools(), token));

        string configured = Path.Combine(_work, "configured-ffmpeg");
        File.WriteAllText(configured, string.Empty);
        string bundledDir = AppPaths.BundledFfmpegDirectory;
        string bundled = Path.Combine(bundledDir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        string root = Path.Combine(_work, "root");
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        string rooted = Path.Combine(root, "bin", OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        File.WriteAllText(rooted, string.Empty);
        string wellKnown = Path.Combine(_work, "wellknown-ffmpeg");
        File.WriteAllText(wellKnown, string.Empty);
        try
        {
            Environment.SetEnvironmentVariable("VQ_FFMPEG", configured);
            Assert.Equal(configured, await VmafRunner.ResolveFfmpegAsync(null, Tools(), token));

            // Bundled copy with VMAF wins over PATH.
            Environment.SetEnvironmentVariable("VQ_FFMPEG", configured + ".missing");
            Directory.CreateDirectory(bundledDir);
            File.WriteAllText(bundled, string.Empty);
            Assert.Equal(bundled, VmafRunner.BundledFfmpeg);
            Assert.Equal(bundled, await VmafRunner.ResolveFfmpegAsync("ffmpeg", Tools(), token));

            // No VMAF in the bundled copy, but on PATH.
            VmafRunner.ClearCapabilityCache();
            var pathOnly = new ScriptedRunner(request => ScriptedRunner.Ok(request.FileName == "ffmpeg" && request.Arguments.Contains("-filters") ? Filters : string.Empty));
            Assert.Equal("ffmpeg", await VmafRunner.ResolveFfmpegAsync(null, pathOnly, token));
            File.Delete(bundled);
            Assert.Null(VmafRunner.BundledFfmpeg);

            // Neither: FFMPEG_ROOT, then the well-known locations (missing ones skipped).
            VmafRunner.ClearCapabilityCache();
            Environment.SetEnvironmentVariable("FFMPEG_ROOT", root);
            VmafRunner.WellKnownFfmpegPaths = [Path.Combine(_work, "nowhere"), wellKnown];
            var rootOnly = new ScriptedRunner(request => ScriptedRunner.Ok(request.FileName == rooted && request.Arguments.Contains("-filters") ? Filters : string.Empty));
            Assert.Equal(rooted, await VmafRunner.ResolveFfmpegAsync(null, rootOnly, token));

            VmafRunner.ClearCapabilityCache();
            var wellKnownOnly = new ScriptedRunner(request => ScriptedRunner.Ok(request.FileName == wellKnown && request.Arguments.Contains("-filters") ? Filters : string.Empty));
            Assert.Equal(wellKnown, await VmafRunner.ResolveFfmpegAsync(null, wellKnownOnly, token));

            // Nothing has VMAF: plain "ffmpeg" (the run reports what is missing).
            VmafRunner.ClearCapabilityCache();
            Environment.SetEnvironmentVariable("VQ_FFMPEG", null);
            Environment.SetEnvironmentVariable("FFMPEG_ROOT", null);
            Assert.Equal("ffmpeg", await VmafRunner.ResolveFfmpegAsync(null, new ScriptedRunner(_ => ScriptedRunner.Ok()), token));
        }
        finally
        {
            Environment.SetEnvironmentVariable("VQ_FFMPEG", null);
            Environment.SetEnvironmentVariable("FFMPEG_ROOT", null);
            FileSystem.TryDelete(bundled);
        }
    }

    [Fact]
    public void Default_overloads_use_real_processes()
    {
        // The parameterless forms run real processes; a missing program is "unavailable", not a crash.
        VmafCapabilities capabilities = VmafRunner.ProbeAsync(Ffmpeg("missing")).GetAwaiter().GetResult();
        Assert.False(capabilities.HasLibvmaf);
        Assert.Equal("/y", VmafRunner.ResolveFfmpegAsync("/y").GetAwaiter().GetResult());
    }

    [Theory]
    [InlineData(@"C:\Temp\x.json", @"C\\:/Temp/x.json")]
    [InlineData("/a/it's [1],b;c.json", @"/a/it\\\'s \[1\]\,b\;c.json")]
    public void Filter_values_are_escaped_for_both_parser_levels(string value, string expected) =>
        Assert.Equal(expected, VmafRunner.EscapeFilterValue(value));
}

public class FileSystemTests
{
    [Fact]
    public void TryDelete_removes_files_and_tolerates_what_it_cannot_delete()
    {
        string folder = Path.Combine(Path.GetTempPath(), "vq-fs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "f.txt");
        File.WriteAllText(file, "x");

        Assert.True(FileSystem.TryDelete(null));
        Assert.True(FileSystem.TryDelete(file));
        Assert.False(File.Exists(file));
        Assert.True(FileSystem.TryDelete(file)); // already gone
        File.WriteAllText(file, "x");
        Assert.False(FileSystem.TryDelete(Path.Combine(file, "child"))); // a file in the way: IOException
        Assert.False(FileSystem.TryDelete(folder));                      // a directory: UnauthorizedAccessException
        Directory.Delete(folder, true);
    }
}
