using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VideoQuality.Core.Decoding;
using VideoQuality.Core.Diagnostics;
using VideoQuality.Core.Models;

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
            throw new FileNotFoundException($"Reference video not found: {reference}", reference);
        }

        if (!File.Exists(distorted))
        {
            throw new FileNotFoundException($"Distorted video not found: {distorted}", distorted);
        }

        options ??= new VmafOptions();
        cancellationToken.ThrowIfCancellationRequested();

        var capabilities = await ProbeAsync(options.FfmpegPath, cancellationToken).ConfigureAwait(false);
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
        string commandLine = FormatCommandLine(options.FfmpegPath, arguments);

        VqLog.Info("vmaf", $"command {commandLine}");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await ExecuteAsync(options.FfmpegPath, arguments, progress, cancellationToken).ConfigureAwait(false);

            if (!File.Exists(logPath))
            {
                throw new InvalidOperationException(
                    $"ffmpeg exited successfully but did not write a VMAF log to '{logPath}'." + Environment.NewLine + commandLine);
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
                TryDelete(logPath);
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
    /// Picks the ffmpeg executable to use. An explicit path wins. When the plain <c>ffmpeg</c> on
    /// PATH has no VMAF filter, well-known custom build locations are tried, so a machine with the
    /// CUDA build from the README does not need any configuration.
    /// </summary>
    public static async Task<string> ResolveFfmpegAsync(string? requested = null, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(requested) &&
            !string.Equals(requested.Trim(), "ffmpeg", StringComparison.Ordinal))
        {
            return requested.Trim();
        }

        VmafCapabilities onPath = await ProbeAsync("ffmpeg", cancellationToken).ConfigureAwait(false);
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

            VmafCapabilities capabilities = await ProbeAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (capabilities.HasLibvmaf || capabilities.HasLibvmafCuda)
            {
                return candidate;
            }
        }

        return "ffmpeg";
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

        yield return OperatingSystem.IsWindows() ? @"C:\ffmpeg\bin\ffmpeg.exe" : "/opt/ffmpeg/bin/ffmpeg";
        yield return "/usr/local/opt/ffmpeg/bin/ffmpeg";
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "opt", "ffmpeg", "bin", "ffmpeg");
    }

    /// <summary>Checks the ffmpeg build for libvmaf / libvmaf_cuda and looks for an NVIDIA GPU.</summary>
    public static async Task<VmafCapabilities> ProbeAsync(string ffmpegPath = "ffmpeg", CancellationToken cancellationToken = default)
    {
        if (CapabilityCache.TryGetValue(ffmpegPath, out var cached))
        {
            return cached;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        VmafCapabilities capabilities;
        try
        {
            string filters = await CaptureAsync(ffmpegPath, ["-hide_banner", "-filters"], timeout.Token).ConfigureAwait(false);
            string version = await CaptureAsync(ffmpegPath, ["-hide_banner", "-version"], timeout.Token).ConfigureAwait(false);
            bool hasGpu = await HasNvidiaGpuAsync(timeout.Token).ConfigureAwait(false);

            string versionLine = version
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? "ffmpeg (unknown version)";

            capabilities = new VmafCapabilities(
                HasLibvmaf(filters),
                HasLibvmafCuda(filters),
                hasGpu,
                versionLine);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            capabilities = new VmafCapabilities(false, false, false, $"unavailable: {ex.Message}");
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
    /// Escapes a value that is embedded in an ffmpeg filter option list. The temporary log path we
    /// generate is already simple, but user-supplied model paths are not, so be conservative.
    /// </summary>
    private static string EscapeFilterValue(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (char c in value.Replace('\\', '/'))
        {
            if (c is ':' or '\'' or ',' or '[' or ']' or ';')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static VmafExecutionBackend ResolveBackend(VmafBackend requested, VmafCapabilities capabilities)
    {
        switch (requested)
        {
            case VmafBackend.Cpu:
                if (!capabilities.HasLibvmaf)
                {
                    throw new InvalidOperationException(
                        "This ffmpeg build has no libvmaf filter. Use an ffmpeg built with --enable-libvmaf.");
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

                throw new InvalidOperationException(
                    "This ffmpeg build has no libvmaf filter. Use an ffmpeg built with --enable-libvmaf " +
                    $"(or --enable-libvmaf-cuda for GPU scoring). Detected: {capabilities.Describe()}.");
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
            reasons.Add("ffmpeg has no libvmaf_cuda filter (rebuild with --enable-libvmaf-cuda --enable-cuda-nvcc --enable-nonfree)");
        }

        if (!capabilities.HasNvidiaGpu)
        {
            reasons.Add("no NVIDIA GPU was detected (nvidia-smi failed)");
        }

        throw new InvalidOperationException("GPU VMAF is not available: " + string.Join("; ", reasons) + ".");
    }

    private static async Task ExecuteAsync(
        string ffmpegPath,
        IReadOnlyList<string> arguments,
        IProgress<VmafProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var stderr = new StringBuilder();

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Could not start '{ffmpegPath}'.");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Could not start '{ffmpegPath}': {ex.Message}", ex);
        }

        using var registration = cancellationToken.Register(() => TryKill(process));

        Task pump = Task.Run(async () =>
        {
            while (true)
            {
                string? line;
                try
                {
                    line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (line is null)
                {
                    break;
                }

                lock (stderr)
                {
                    if (stderr.Length < 64_000)
                    {
                        stderr.AppendLine(line);
                    }
                }


            }
        }, CancellationToken.None);

        Task progressPump = Task.Run(async () =>
        {
            var parser = new FfmpegProgressParser();

            while (true)
            {
                string? line;
                try
                {
                    line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (line is null)
                {
                    break;
                }

                if (parser.Consume(line) is { } value)
                {
                    progress?.Report(value);
                }
            }
        }, CancellationToken.None);

        // The process is killed by the cancellation registration, so this wait is always finite.
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await pump.ConfigureAwait(false);
        await progressPump.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (process.ExitCode != 0)
        {
            string tail = Tail(stderr.ToString(), 4000);
            throw new InvalidOperationException(
                $"ffmpeg exited with code {process.ExitCode}.{Environment.NewLine}{tail}{Environment.NewLine}{FormatCommandLine(ffmpegPath, arguments)}");
        }
    }

    private static async Task<string> CaptureAsync(string fileName, string[] arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
    }

    private static async Task<bool> HasNvidiaGpuAsync(CancellationToken cancellationToken)
    {
        try
        {
            string executable = OperatingSystem.IsWindows() ? "nvidia-smi.exe" : "nvidia-smi";
            string output = await CaptureAsync(executable, ["-L"], cancellationToken).ConfigureAwait(false);
            return output.Contains("GPU", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

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
            throw new InvalidDataException("The VMAF log has no 'frames' array: " + Tail(json, 400));
        }

        var frames = new List<VmafFrame>(framesElement.GetArrayLength());
        int fallbackIndex = 0;

        foreach (JsonElement frame in framesElement.EnumerateArray())
        {
            int frameNumber = frame.TryGetProperty("frameNum", out JsonElement number) && number.TryGetInt32(out int parsed)
                ? parsed
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
            throw new InvalidDataException("The VMAF log contained no 'vmaf' metrics: " + Tail(json, 400));
        }

        return frames;
    }

    private static double ReadVmaf(JsonElement metrics)
    {
        if (metrics.TryGetProperty("vmaf", out JsonElement direct) && direct.TryGetDouble(out double value))
        {
            return value;
        }

        foreach (JsonProperty property in metrics.EnumerateObject())
        {
            if (property.Name.StartsWith("vmaf", StringComparison.OrdinalIgnoreCase) &&
                property.Value.TryGetDouble(out value))
            {
                return value;
            }
        }

        foreach (JsonProperty property in metrics.EnumerateObject())
        {
            if (property.Value.TryGetDouble(out value))
            {
                return value;
            }
        }

        return double.NaN;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may have exited between the check and the kill; nothing to do.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private static string FormatCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var sb = new StringBuilder(executable);
        foreach (string argument in arguments)
        {
            sb.Append(' ');
            if (argument.Length == 0 || argument.Any(char.IsWhiteSpace) || argument.IndexOfAny([';', '&', '|', '<', '>', '"', '\'']) >= 0)
            {
                sb.Append('"').Append(argument.Replace("\"", "\\\"")).Append('"');
            }
            else
            {
                sb.Append(argument);
            }
        }

        return sb.ToString();
    }

    private static string Tail(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[^maxLength..];

}
