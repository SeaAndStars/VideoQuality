using System.Diagnostics;
using System.Runtime.InteropServices;
using VideoQuality.Core.Decoding;
using VideoQuality.Core.Diagnostics;
using VideoQuality.Core.Models;
using VideoQuality.Core.Reporting;
using VideoQuality.Core.Scoring;

VqLog.Initialize();
VqLog.Info("cli", "args: " + string.Join(' ', args));

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

switch (command)
{
    case "probe":
        return await ProbeAsync(GetOption(args, "--ffmpeg"));

    case "score":
        return await ScoreAsync(args);

    case "frame":
        return Frame(args);

    case "diag":
        return await DiagnosticsAsync(GetOption(args, "--ffmpeg"));

    default:
        PrintHelp();
        return command is "help" or "-h" or "--help" ? 0 : 1;
}

static async Task<int> ProbeAsync(string? requestedFfmpeg)
{
    string ffmpeg = await VmafRunner.ResolveFfmpegAsync(requestedFfmpeg);
    VmafCapabilities capabilities = await VmafRunner.ProbeAsync(ffmpeg);

    Console.WriteLine($"ffmpeg: {ffmpeg}");
    Console.WriteLine($"  version     : {capabilities.FfmpegVersion}");
    Console.WriteLine($"  libvmaf     : {(capabilities.HasLibvmaf ? "yes" : "no")}");
    Console.WriteLine($"  libvmaf_cuda: {(capabilities.HasLibvmafCuda ? "yes" : "no")}");
    Console.WriteLine($"  nvidia gpu  : {(capabilities.HasNvidiaGpu ? "yes" : "no")}");
    Console.WriteLine($"  cuda usable : {(capabilities.CudaUsable ? "yes" : "no")}");
    Console.WriteLine($"  libav       : {DescribeLibav()}");
    Console.WriteLine($"  log         : {VqLog.CurrentPath ?? "(disabled)"}");

    if (!capabilities.HasLibvmaf && !capabilities.HasLibvmafCuda)
    {
        Console.WriteLine();
        Console.WriteLine("No VMAF filter in this build. Pass --ffmpeg <path> to a VMAF-enabled build,");
        Console.WriteLine("or set VQ_FFMPEG=/opt/ffmpeg/bin/ffmpeg (see docs/ffmpeg-cuda.md).");
    }

    return 0;
}

static async Task<int> ScoreAsync(string[] args)
{
    string? reference = null;
    string? distorted = null;
    string ffmpeg = await VmafRunner.ResolveFfmpegAsync(GetOption(args, "--ffmpeg"));
    string model = GetOption(args, "--model") ?? VmafModels.Default;
    VmafBackend backend = args.Contains("--gpu") ? VmafBackend.Cuda
        : args.Contains("--cpu") ? VmafBackend.Cpu
        : VmafBackend.Auto;

    foreach (string argument in args.Skip(1))
    {
        if (argument.StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        if (reference is null)
        {
            reference = argument;
        }
        else if (distorted is null)
        {
            distorted = argument;
        }
    }

    if (reference is null || distorted is null)
    {
        Console.Error.WriteLine(
            "usage: vq score <reference> <distorted> [--gpu|--cpu] [--model NAME] [--ffmpeg PATH] " +
            "[--csv out.csv] [--json out.json]");
        return 1;
    }

    var options = new VmafOptions { FfmpegPath = ffmpeg, Model = model, Backend = backend };
    var progress = new Progress<VmafProgress>(p =>
    {
        if (p.FrameNumber is int frame && frame % 24 == 0)
        {
            Console.Write($"\r  scoring frame {frame}…");
        }
    });

    try
    {
        VmafReport report = await VmafRunner.RunAsync(reference, distorted, options, progress);
        Console.WriteLine();
        Console.WriteLine($"backend : {report.PrecisionLabel}");
        Console.WriteLine($"model   : {VmafModels.DisplayName(report.Model)} ({report.Model})");
        Console.WriteLine($"frames  : {report.FrameCount}");
        Console.WriteLine($"mean    : {report.Stats.Mean:F3}");
        Console.WriteLine($"hmean   : {report.Stats.HarmonicMean:F3}");
        Console.WriteLine($"p1      : {report.Stats.P1:F3}");
        Console.WriteLine($"min     : {report.Stats.Min:F3}");
        Console.WriteLine($"max     : {report.Stats.Max:F3}");
        Console.WriteLine($"stddev  : {report.Stats.StdDev:F3}");
        Console.WriteLine($"elapsed : {report.Duration.TotalSeconds:F2}s");

        if (report.FellBackToCpu)
        {
            Console.WriteLine($"note    : CUDA failed, scored on CPU: {FirstLine(report.FallbackReason)}");
        }
        else if (report.ConvertedTo8Bit)
        {
            Console.WriteLine("note    : 10-bit input converted to 8-bit on the GPU; use --cpu for full precision.");
        }

        Console.WriteLine($"command : {report.CommandLine}");

        string? csv = GetOption(args, "--csv");
        if (csv is not null)
        {
            VmafReportExporter.WriteCsv(csv, report);
            Console.WriteLine($"csv     : {csv}");
        }

        string? json = GetOption(args, "--json");
        if (json is not null)
        {
            VmafReportExporter.WriteJson(json, report, reference, distorted);
            Console.WriteLine($"json    : {json}");
        }

        VqLog.Info("cli", $"score ok backend={report.Backend} frames={report.FrameCount} mean={report.Stats.Mean:F3}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("error: " + ex.Message);
        VqLog.Error("cli", "score failed", ex);
        return 2;
    }
}

static int Frame(string[] args)
{
    string? video = null;
    int index = 0;
    string? ppm = GetOption(args, "--ppm");

    foreach (string argument in args.Skip(1))
    {
        if (argument.StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        if (video is null)
        {
            video = argument;
        }
        else if (int.TryParse(argument, out int parsed))
        {
            index = parsed;
        }
    }

    if (video is null)
    {
        Console.Error.WriteLine("usage: vq frame <video> [index] [--ppm out.ppm]");
        return 1;
    }

    try
    {
        using FrameDecoder decoder = FrameDecoder.Open(video);
        Console.WriteLine($"libav      : {FfmpegRuntime.Describe()}");
        Console.WriteLine($"video      : {decoder.Info}");

        var stopwatch = Stopwatch.StartNew();
        DecodedFrame frame = decoder.DecodeFrame(index);
        stopwatch.Stop();

        Console.WriteLine(
            $"frame {index} : {frame.Width}x{frame.Height}, stride {frame.Stride}, {frame.Bgra.Length:N0} bytes, " +
            $"pts {frame.Pts}, decoded in {stopwatch.ElapsedMilliseconds} ms");

        if (ppm is not null)
        {
            WritePpm(ppm, frame);
            Console.WriteLine($"wrote      : {ppm}");
        }

        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("error: " + ex.Message);
        VqLog.Error("cli", "frame failed", ex);
        return 2;
    }
}

static async Task<int> DiagnosticsAsync(string? requestedFfmpeg)
{
    string ffmpeg = await VmafRunner.ResolveFfmpegAsync(requestedFfmpeg);
    VmafCapabilities capabilities = await VmafRunner.ProbeAsync(ffmpeg);
    string libav = DescribeLibav();

    Console.WriteLine("VideoQuality diagnostics");
    Console.WriteLine($"  core version : {typeof(VmafRunner).Assembly.GetName().Version}");
    Console.WriteLine($"  .NET         : {Environment.Version}");
    Console.WriteLine($"  OS           : {RuntimeInformation.OSDescription}");
    Console.WriteLine($"  arch         : {RuntimeInformation.ProcessArchitecture}");
    Console.WriteLine($"  session type : {Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "unknown"}");
    Console.WriteLine($"  ffmpeg       : {ffmpeg}");
    Console.WriteLine($"  ffmpeg ver   : {capabilities.FfmpegVersion}");
    Console.WriteLine($"  libvmaf      : {capabilities.HasLibvmaf}");
    Console.WriteLine($"  libvmaf_cuda : {capabilities.HasLibvmafCuda}");
    Console.WriteLine($"  nvidia gpu   : {capabilities.HasNvidiaGpu}");
    Console.WriteLine($"  libav        : {libav}");
    Console.WriteLine($"  log          : {VqLog.CurrentPath ?? "(disabled)"}");

    VqLog.SessionHeader("diagnostics", new Dictionary<string, string>
    {
        ["core"] = typeof(VmafRunner).Assembly.GetName().Version?.ToString() ?? "?",
        ["dotnet"] = Environment.Version.ToString(),
        ["os"] = RuntimeInformation.OSDescription,
        ["ffmpeg"] = ffmpeg,
        ["ffmpegVersion"] = capabilities.FfmpegVersion,
        ["libvmaf"] = capabilities.HasLibvmaf.ToString(),
        ["libvmafCuda"] = capabilities.HasLibvmafCuda.ToString(),
        ["gpu"] = capabilities.HasNvidiaGpu.ToString(),
        ["libav"] = libav,
    });

    return 0;
}

static string DescribeLibav()
{
    try
    {
        return FfmpegRuntime.Describe();
    }
    catch (Exception ex)
    {
        return $"unavailable: {ex.Message}";
    }
}

static string FirstLine(string? text)
{
    if (string.IsNullOrWhiteSpace(text))
    {
        return "unknown error";
    }

    int newline = text.IndexOfAny(['\r', '\n']);
    return newline < 0 ? text : text[..newline];
}

static void WritePpm(string path, DecodedFrame frame)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    writer.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{frame.Width} {frame.Height}\n255\n"));

    var row = new byte[frame.Width * 3];
    for (int y = 0; y < frame.Height; y++)
    {
        int offset = y * frame.Stride;
        for (int x = 0; x < frame.Width; x++)
        {
            int source = offset + x * 4;
            row[x * 3 + 0] = frame.Bgra[source + 2];
            row[x * 3 + 1] = frame.Bgra[source + 1];
            row[x * 3 + 2] = frame.Bgra[source + 0];
        }

        writer.Write(row);
    }
}

static string? GetOption(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.Ordinal))
        {
            return args[i + 1];
        }
    }

    return null;
}

static void PrintHelp()
{
    Console.WriteLine("""
        VideoQuality CLI

          vq probe  [--ffmpeg PATH]
              Show which VMAF filters and GPU the resolved ffmpeg build has, plus the libav
              libraries actually loaded.

          vq score <reference> <distorted> [--gpu|--cpu] [--model NAME] [--ffmpeg PATH]
                                      [--csv out.csv] [--json out.json]
              Run VMAF. Default backend is Auto: libvmaf_cuda when available, otherwise libvmaf.
              --csv writes frame,vmaf rows; --json writes the full report with backend, precision,
              model, stats and command line.

          vq frame <video> [index] [--ppm out.ppm]
              Decode one frame through FFmpeg.AutoGen (no ffmpeg binary) and optionally write PPM.

          vq diag  [--ffmpeg PATH]
              Print a session header: versions, OS, ffmpeg capabilities, libav path, log location.
        """);
}
