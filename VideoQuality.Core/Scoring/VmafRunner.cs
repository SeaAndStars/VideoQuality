using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VideoQuality.Core.Decoding;
using VideoQuality.Core.Diagnostics;
using VideoQuality.Core.Models;
using VideoQuality.Core.Localization;
using VideoQuality.Core.Tools;

namespace VideoQuality.Core.Scoring;

/// <summary>Options for one VMAF run.</summary>
public sealed class VmafOptions
{
    /// <summary>ffmpeg executable. Must be built with <c>--enable-libvmaf</c> for CPU scoring and
    /// <c>--enable-libvmaf-cuda --enable-cuda-nvcc</c> for GPU scoring.</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    public VmafBackend Backend { get; set; } = VmafBackend.Auto;

    /// <summary>Built-in model name (e.g. <c>vmaf_v0.6.1</c>, <c>vmaf_v0.6.1neg</c>,
    /// <c>vmaf_4k_v0.6.1</c>) or a path to a <c>.json</c> model.</summary>
    public string Model { get; set; } = VmafModels.Default;

    /// <summary>Threads for the CPU libvmaf filter. Ignored by libvmaf_cuda.</summary>
    public int Threads { get; set; } = Math.Clamp(Environment.ProcessorCount, 1, 16);

    /// <summary>Score every Nth frame only (libvmaf <c>n_subsample</c>).</summary>
    public int? Subsample { get; set; }

    /// <summary>Keep the temporary JSON log after the run.</summary>
    public bool KeepLog { get; set; }

    /// <summary>Runs ffmpeg and nvidia-smi; tests substitute scripted tools.</summary>
    public IProcessRunner ProcessRunner { get; set; } = SystemProcessRunner.Instance;
}

/// <summary>Progress reported while ffmpeg is running, parsed from <c>-progress pipe:1</c>.</summary>
/// <param name="FrameNumber">Frames encoded so far, if known.</param>
/// <param name="Fps">Encoding speed in frames per second.</param>
/// <param name="Done">True for the final <c>progress=end</c> block.</param>
public sealed record VmafProgress(int? FrameNumber, double Fps, bool Done);

/// <summary>
/// Runs <c>ffmpeg</c> with the CPU <c>libvmaf</c> filter or the GPU <c>libvmaf_cuda</c> filter and
/// parses the JSON log into per-frame scores.
/// </summary>
/// <remarks>
/// Input order is fixed: the first input is the distorted video, the second is the reference,
/// matching libvmaf's convention.
/// </remarks>
public static partial class VmafRunner
{
    private static readonly ConcurrentDictionary<string, VmafCapabilities> CapabilityCache = new(StringComparer.Ordinal);

    /// <summary>How long a probe waits for ffmpeg (a fresh copy can sit behind an antivirus scan).</summary>
    internal static TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Where drivers install nvidia-smi when it is not on PATH (WSL keeps it in /usr/lib/wsl/lib).</summary>
    internal static IReadOnlyList<string> NvidiaSmiPaths { get; set; } = OperatingSystem.IsWindows()
        ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe")]
        : ["/usr/bin/nvidia-smi", "/usr/lib/wsl/lib/nvidia-smi", "/opt/bin/nvidia-smi"];

    /// <summary>Where custom VMAF-enabled ffmpeg builds usually live.</summary>
    internal static IReadOnlyList<string> WellKnownFfmpegPaths { get; set; } =
    [
        OperatingSystem.IsWindows() ? @"C:\ffmpeg\bin\ffmpeg.exe" : "/opt/ffmpeg/bin/ffmpeg",
        "/usr/local/opt/ffmpeg/bin/ffmpeg",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "opt", "ffmpeg", "bin", "ffmpeg"),
    ];

    /// <summary>Forgets probed capabilities (tests; or after the user replaces ffmpeg).</summary>
    internal static void ClearCapabilityCache() => CapabilityCache.Clear();

    public static async Task<VmafReport> RunAsync(
        string reference,
        string distorted,
        VmafOptions? options = null,
        IProgress<VmafProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(distorted);

        if (!File.Exists(reference))
        {
            throw new FileNotFoundException(Loc.T("vmaf.error.referenceMissing", reference), reference);
        }

        if (!File.Exists(distorted))
        {
            throw new FileNotFoundException(Loc.T("vmaf.error.encodeMissing", distorted), distorted);
        }

        options ??= new VmafOptions();
        cancellationToken.ThrowIfCancellationRequested();

        var capabilities = await ProbeAsync(options.FfmpegPath, options.ProcessRunner, cancellationToken).ConfigureAwait(false);
        var backend = ResolveBackend(options.Backend, capabilities);
        string model = string.IsNullOrWhiteSpace(options.Model) ? VmafModels.Default : options.Model.Trim();

        // Needed for the report on both backends (exports must say what was scored) and for deciding
        // whether CUDA is scoring a converted 8-bit picture. Probing reads headers, not frames.
        int sourceBitDepth = ProbeSourceBitDepth(reference, distorted);

        VqLog.Info("vmaf", $"start backend={backend} model={model} depth={sourceBitDepth} " +
                           $"ref={reference} dis={distorted}");

        try
        {
            VmafReport report = await RunBackendAsync(
                backend, options, model, sourceBitDepth, reference, distorted, progress, cancellationToken)
                .ConfigureAwait(false);

            VqLog.Info("vmaf", $"done backend={backend} frames={report.FrameCount} mean={report.Stats.Mean:F3} " +
                               $"elapsed={report.Duration.TotalSeconds:F2}s");
            return report;
        }
        catch (Exception ex) when (options.Backend == VmafBackend.Auto &&
                                   backend == VmafExecutionBackend.Cuda &&
                                   ex is not OperationCanceledException)
        {
            // A CUDA run can fail for reasons outside our control (an NVDEC profile the GPU does
            // not support, a driver reset, a filter-format mismatch). Auto is allowed to fall back;
            // an explicitly requested CUDA run is not.
            VqLog.Warn("vmaf", "CUDA scoring failed; retrying on CPU", ex);
            VmafReport report = await RunBackendAsync(
                VmafExecutionBackend.Cpu, options, model, sourceBitDepth, reference, distorted, progress, cancellationToken)
                .ConfigureAwait(false);

            return report with
            {
                FellBackToCpu = true,
                FallbackReason = ex.Message,
            };
        }
    }

    private static async Task<VmafReport> RunBackendAsync(
        VmafExecutionBackend backend,
        VmafOptions options,
        string model,
        int sourceBitDepth,
        string reference,
        string distorted,
        IProgress<VmafProgress>? progress,
        CancellationToken cancellationToken)
    {
        string logPath = Path.Combine(Path.GetTempPath(), $"vq_vmaf_{Guid.NewGuid():N}.json");
        var arguments = BuildArguments(options, backend, logPath, reference, distorted);
        string commandLine = Encode.FfmpegCommandBuilder.FormatCommandLine(options.FfmpegPath, arguments);

        VqLog.Info("vmaf", $"command {commandLine}");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await ExecuteAsync(options.ProcessRunner, options.FfmpegPath, arguments, progress, cancellationToken).ConfigureAwait(false);

            if (!File.Exists(logPath))
            {
                throw new InvalidOperationException(Loc.T("vmaf.error.noLog", logPath) + Environment.NewLine + commandLine);
            }

            string json = await File.ReadAllTextAsync(logPath, cancellationToken).ConfigureAwait(false);
            var frames = ParseFrames(json);
            stopwatch.Stop();

            return new VmafReport(
                frames,
                VmafStats.FromFrames(frames),
                backend,
                commandLine,
                stopwatch.Elapsed)
            {
                SourceBitDepth = sourceBitDepth,
                ConvertedTo8Bit = backend == VmafExecutionBackend.Cuda && sourceBitDepth > 8,
                Model = model,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            VqLog.Error("vmaf", $"ffmpeg failed backend={backend}", ex);
            throw;
        }
        finally
        {
            if (!options.KeepLog)
            {
                FileSystem.TryDelete(logPath);
            }
        }
    }

    private static int ProbeSourceBitDepth(string reference, string distorted)
    {
        try
        {
            int referenceDepth = MediaProbe.Probe(reference).BitDepth;
            int distortedDepth = MediaProbe.Probe(distorted).BitDepth;
            return Math.Max(referenceDepth, distortedDepth);
        }
        catch (Exception ex)
        {
            VqLog.Warn("vmaf", "could not probe source bit depth", ex);
            return 8;
        }
    }

    /// <summary>
    /// Picks the ffmpeg executable to use. An explicit path wins, then <c>VQ_FFMPEG</c>, then the
    /// build bundled with a release (<c>ffmpeg/</c> beside the app), then <c>ffmpeg</c> on PATH if it
    /// has VMAF, then well-known custom build locations. A release therefore uses the ffmpeg it was
    /// tested with even when another one is on PATH.
    /// </summary>
    public static Task<string> ResolveFfmpegAsync(string? requested = null, CancellationToken cancellationToken = default) =>
        ResolveFfmpegAsync(requested, SystemProcessRunner.Instance, cancellationToken);

    /// <inheritdoc cref="ResolveFfmpegAsync(string?, CancellationToken)"/>
    public static async Task<string> ResolveFfmpegAsync(string? requested, IProcessRunner runner, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(requested) &&
            !string.Equals(requested.Trim(), "ffmpeg", StringComparison.Ordinal))
        {
            return requested.Trim();
        }

        string? configured = Environment.GetEnvironmentVariable("VQ_FFMPEG");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        if (BundledFfmpeg is { } bundled)
        {
            VmafCapabilities bundledCapabilities = await ProbeAsync(bundled, runner, cancellationToken).ConfigureAwait(false);
            if (bundledCapabilities.HasLibvmaf || bundledCapabilities.HasLibvmafCuda)
            {
                return bundled;
            }
        }

        VmafCapabilities onPath = await ProbeAsync("ffmpeg", runner, cancellationToken).ConfigureAwait(false);
        if (onPath.HasLibvmaf || onPath.HasLibvmafCuda)
        {
            return "ffmpeg";
        }

        foreach (string candidate in CustomBuildCandidates())
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            VmafCapabilities capabilities = await ProbeAsync(candidate, runner, cancellationToken).ConfigureAwait(false);
            if (capabilities.HasLibvmaf || capabilities.HasLibvmafCuda)
            {
                return candidate;
            }
        }

        return "ffmpeg";
    }

    /// <summary>The ffmpeg shipped inside a release (<c>&lt;app&gt;/ffmpeg/ffmpeg[.exe]</c>), if present.</summary>
    public static string? BundledFfmpeg
    {
        get
        {
            string path = Path.Combine(
                AppPaths.BundledFfmpegDirectory,
                OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            return File.Exists(path) ? path : null;
        }
    }

    private static IEnumerable<string> CustomBuildCandidates()
    {
        string? configured = Environment.GetEnvironmentVariable("VQ_FFMPEG");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured;
        }

        string? root = Environment.GetEnvironmentVariable("FFMPEG_ROOT");
        if (!string.IsNullOrWhiteSpace(root))
        {
            yield return Path.Combine(root, "bin", OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        }

        foreach (string path in WellKnownFfmpegPaths)
        {
            yield return path;
        }
    }

    /// <summary>Checks the ffmpeg build for libvmaf / libvmaf_cuda and looks for an NVIDIA GPU.</summary>
    public static Task<VmafCapabilities> ProbeAsync(string ffmpegPath = "ffmpeg", CancellationToken cancellationToken = default) =>
        ProbeAsync(ffmpegPath, SystemProcessRunner.Instance, cancellationToken);

    /// <inheritdoc cref="ProbeAsync(string, CancellationToken)"/>
    public static async Task<VmafCapabilities> ProbeAsync(string ffmpegPath, IProcessRunner runner, CancellationToken cancellationToken = default)
    {
        if (CapabilityCache.TryGetValue(ffmpegPath, out var cached))
        {
            return cached;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Generous: the first run of a freshly unzipped ffmpeg can sit behind an antivirus scan.
        timeout.CancelAfter(ProbeTimeout);

        VmafCapabilities capabilities;
        try
        {
            string filters = await CaptureAsync(runner, ffmpegPath, ["-hide_banner", "-filters"], timeout.Token).ConfigureAwait(false);
            string version = await CaptureAsync(runner, ffmpegPath, ["-hide_banner", "-version"], timeout.Token).ConfigureAwait(false);
            bool hasGpu = await HasNvidiaGpuAsync(runner, timeout.Token).ConfigureAwait(false);

            string versionLine = version
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? "ffmpeg (unknown version)";

            capabilities = new VmafCapabilities(
                HasLibvmaf(filters),
                HasLibvmafCuda(filters),
                hasGpu,
                versionLine);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own timeout, not the caller's cancellation. Not cached, so a later probe retries.
            return new VmafCapabilities(false, false, false, Loc.T("capabilities.timeout", ProbeTimeout.TotalSeconds));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            capabilities = new VmafCapabilities(false, false, false, Loc.T("capabilities.unavailable", ex.Message));
        }

        CapabilityCache[ffmpegPath] = capabilities;
        return capabilities;
    }

    /// <summary>Exposed for diagnostics and tests: the exact ffmpeg argument list for a run.</summary>
    public static IReadOnlyList<string> BuildArguments(
        VmafOptions options,
        VmafExecutionBackend backend,
        string logPath,
        string reference,
        string distorted)
    {
        var args = new List<string>
        {
            "-y",
            "-hide_banner",
            "-nostdin",
            "-nostats",
            "-loglevel", "info",
            "-progress", "pipe:1",
        };

        string filterOptions = BuildFilterOptions(options, backend, logPath);

        if (backend == VmafExecutionBackend.Cuda)
        {
            args.AddRange([
                "-hwaccel", "cuda",
                "-hwaccel_output_format", "cuda",
                "-i", distorted,
                "-hwaccel", "cuda",
                "-hwaccel_output_format", "cuda",
                "-i", reference,
                "-filter_complex",
                $"[0:v]scale_cuda=format=yuv420p[dis];[1:v]scale_cuda=format=yuv420p[ref];[dis][ref]libvmaf_cuda={filterOptions}",
            ]);
        }
        else
        {
            args.AddRange([
                "-i", distorted,
                "-i", reference,
                "-filter_complex",
                $"[0:v][1:v]libvmaf={filterOptions}",
            ]);
        }

        args.AddRange(["-f", "null", "-"]);
        return args;
    }

    private static string BuildFilterOptions(VmafOptions options, VmafExecutionBackend backend, string logPath)
    {
        var sb = new StringBuilder();
        sb.Append("log_path=").Append(EscapeFilterValue(logPath));
        sb.Append(":log_fmt=json");

        // Frame-index pairing. The default "lower or equal timestamp" sync mispairs frames when the
        // two files use different time bases and timestamps round differently; nearest matches the
        // frame-by-frame comparison the UI does. shortest stops framesync padding a duplicate frame
        // at EOF, which otherwise adds a bogus last score.
        sb.Append(":ts_sync_mode=nearest:shortest=1");

        if (backend == VmafExecutionBackend.Cpu)
        {
            sb.Append(":n_threads=").Append(Math.Clamp(options.Threads, 1, 64));
        }

        if (options.Subsample is > 0)
        {
            sb.Append(":n_subsample=").Append(options.Subsample.Value);
        }

        string model = string.IsNullOrWhiteSpace(options.Model) ? VmafModels.Default : options.Model.Trim();
        if (model.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            model.Contains(Path.DirectorySeparatorChar) ||
            model.Contains('/'))
        {
            sb.Append(":model=path=").Append(EscapeFilterValue(model));
        }
        else
        {
            sb.Append(":model=version=").Append(EscapeFilterValue(model));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Escapes a value embedded in an ffmpeg filter option list inside <c>-filter_complex</c>.
    /// Two levels apply: the option parser treats <c>\ : '</c> as special, then the filtergraph
    /// parser treats <c>\ ' [ ] , ;</c> as special and strips one level of backslashes first. A
    /// Windows path such as <c>C:/Temp/x.json</c> therefore becomes <c>C\\:/Temp/x.json</c>; with a
    /// single backslash the graph parser eats it and the option parser splits on the drive colon.
    /// </summary>
    internal static string EscapeFilterValue(string value)
    {
        // libvmaf and ffmpeg accept forward slashes on Windows; this avoids escaping every separator.
        string option = Escape(value.Replace('\\', '/'), "\\:'");
        return Escape(option, "\\'[],;");

        static string Escape(string text, string special)
        {
            var sb = new StringBuilder(text.Length + 8);
            foreach (char c in text)
            {
                if (special.Contains(c))
                {
                    sb.Append('\\');
                }

                sb.Append(c);
            }

            return sb.ToString();
        }
    }

    private static VmafExecutionBackend ResolveBackend(VmafBackend requested, VmafCapabilities capabilities)
    {
        switch (requested)
        {
            case VmafBackend.Cpu:
                if (!capabilities.HasLibvmaf)
                {
                    throw new InvalidOperationException(Loc.T("vmaf.error.noLibvmaf"));
                }

                return VmafExecutionBackend.Cpu;

            case VmafBackend.Cuda:
                EnsureCudaUsable(capabilities);
                return VmafExecutionBackend.Cuda;

            default:
                if (capabilities.CudaUsable)
                {
                    return VmafExecutionBackend.Cuda;
                }

                if (capabilities.HasLibvmaf)
                {
                    return VmafExecutionBackend.Cpu;
                }

                throw new InvalidOperationException(Loc.T("vmaf.error.noVmaf", capabilities.Describe()));
        }
    }

    private static void EnsureCudaUsable(VmafCapabilities capabilities)
    {
        if (capabilities.CudaUsable)
        {
            return;
        }

        var reasons = new List<string>();
        if (!capabilities.HasLibvmafCuda)
        {
            reasons.Add(Loc.T("vmaf.error.noCudaFilter"));
        }

        if (!capabilities.HasNvidiaGpu)
        {
            reasons.Add(Loc.T("vmaf.error.noGpu"));
        }

        throw new InvalidOperationException(Loc.T("vmaf.error.gpuUnavailable", string.Join("; ", reasons)));
    }

    private static async Task ExecuteAsync(
        IProcessRunner runner,
        string ffmpegPath,
        IReadOnlyList<string> arguments,
        IProgress<VmafProgress>? progress,
        CancellationToken cancellationToken)
    {
        var parser = new FfmpegProgressParser();
        ProcessResult result = await runner.RunAsync(
            new ProcessRequest
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                CaptureLimit = 64_000,
                OnOutputLine = line =>
                {
                    if (parser.Consume(line) is { } value)
                    {
                        progress?.Report(value);
                    }
                },
            },
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            string tail = Tail(result.Error, 4000);
            throw new InvalidOperationException(Loc.T("encode.error.exit", result.ExitCode, tail) + Environment.NewLine +
                Encode.FfmpegCommandBuilder.FormatCommandLine(ffmpegPath, arguments));
        }
    }

    private static async Task<string> CaptureAsync(
        IProcessRunner runner, string fileName, string[] arguments, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner.RunAsync(
            new ProcessRequest { FileName = fileName, Arguments = arguments }, cancellationToken).ConfigureAwait(false);
        return result.Output + result.Error;
    }

    private static async Task<bool> HasNvidiaGpuAsync(IProcessRunner runner, CancellationToken cancellationToken)
    {
        // PATH first, then where drivers install it: a desktop launcher or a minimal environment may
        // not have the driver's directory on PATH (WSL keeps it in /usr/lib/wsl/lib).
        foreach (string executable in NvidiaSmiCandidates())
        {
            try
            {
                string output = await CaptureAsync(runner, executable, ["-L"], cancellationToken).ConfigureAwait(false);
                if (output.Contains("GPU", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not there or not runnable; try the next location.
            }
        }

        return false;
    }

    private static IEnumerable<string> NvidiaSmiCandidates() =>
        NvidiaSmiPaths.Where(File.Exists).Prepend(OperatingSystem.IsWindows() ? "nvidia-smi.exe" : "nvidia-smi");

    private static bool HasLibvmaf(string filters) =>
        Regex.IsMatch(filters, @"(^|\s)libvmaf(\s|$)", RegexOptions.Multiline, Regex.InfiniteMatchTimeout);

    private static bool HasLibvmafCuda(string filters) =>
        Regex.IsMatch(filters, @"(^|\s)libvmaf_cuda(\s|$)", RegexOptions.Multiline, Regex.InfiniteMatchTimeout);

    private static IReadOnlyList<VmafFrame> ParseFrames(string json)
    {
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("frames", out JsonElement framesElement) ||
            framesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(Loc.T("vmaf.error.noFrames", Tail(json, 400)));
        }

        var frames = new List<VmafFrame>(framesElement.GetArrayLength());
        int fallbackIndex = 0;

        foreach (JsonElement frame in framesElement.EnumerateArray())
        {
            int frameNumber = frame.TryGetProperty("frameNum", out JsonElement number) && Number(number) is double parsed
                ? (int)parsed
                : fallbackIndex;

            if (frame.TryGetProperty("metrics", out JsonElement metrics) && metrics.ValueKind == JsonValueKind.Object)
            {
                double vmaf = ReadVmaf(metrics);
                if (!double.IsNaN(vmaf))
                {
                    frames.Add(new VmafFrame(frameNumber, vmaf));
                }
            }

            fallbackIndex++;
        }

        if (frames.Count == 0)
        {
            throw new InvalidDataException(Loc.T("vmaf.error.noMetrics", Tail(json, 400)));
        }

        return frames;
    }

    private static double ReadVmaf(JsonElement metrics)
    {
        // "vmaf", else the first "vmaf…" score (e.g. vmaf_neg), else the first number at all.
        List<JsonProperty> numbers = [.. metrics.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Number)];
        JsonProperty[] ordered =
        [
            .. numbers.Where(p => p.Name == "vmaf"),
            .. numbers.Where(p => p.Name.StartsWith("vmaf", StringComparison.OrdinalIgnoreCase)),
            .. numbers,
        ];
        return ordered.Length > 0 ? ordered[0].Value.GetDouble() : double.NaN;
    }

    /// <summary>The value of a JSON number; null for strings and everything else.</summary>
    private static double? Number(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number ? element.GetDouble() : null;

    private static string Tail(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[^maxLength..];

}
