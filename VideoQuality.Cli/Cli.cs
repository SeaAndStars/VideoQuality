using System.Diagnostics;
using System.Runtime.InteropServices;
using VideoQuality.Core.Decoding;
using VideoQuality.Core.Diagnostics;
using VideoQuality.Core.Fonts;
using VideoQuality.Core.Models;
using VideoQuality.Core.Reporting;
using VideoQuality.Core.Scoring;
using VideoQuality.Core.Tools;

namespace VideoQuality.Cli;

/// <summary>
/// The <c>vq</c> commands. Everything the commands touch outside the process (console, external
/// tools, environment, libav, log) comes in through the constructor, so tests run them in-process.
/// </summary>
internal sealed class Cli(
    TextWriter output,
    TextWriter error,
    IProcessRunner runner,
    Func<string, string?> environment,
    Func<string> describeLibav,
    Func<string?> logPath)
{
    public static Cli Default() => new(
        Console.Out,
        Console.Error,
        SystemProcessRunner.Instance,
        Environment.GetEnvironmentVariable,
        FfmpegRuntime.Describe,
        () => VqLog.CurrentPath);

    public async Task<int> RunAsync(string[] args)
    {
        VqLog.Initialize();
        VqLog.Info("cli", "args: " + string.Join(' ', args));

        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
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

            case "subset":
                return Subset(args);

            default:
                PrintHelp();
                return command is "help" or "-h" or "--help" ? 0 : 1;
        }
    }

    private async Task<int> ProbeAsync(string? requestedFfmpeg)
    {
        string ffmpeg = await VmafRunner.ResolveFfmpegAsync(requestedFfmpeg, runner);
        VmafCapabilities capabilities = await VmafRunner.ProbeAsync(ffmpeg, runner);

        output.WriteLine($"ffmpeg: {ffmpeg}");
        output.WriteLine($"  version     : {capabilities.FfmpegVersion}");
        output.WriteLine($"  libvmaf     : {YesNo(capabilities.HasLibvmaf)}");
        output.WriteLine($"  libvmaf_cuda: {YesNo(capabilities.HasLibvmafCuda)}");
        output.WriteLine($"  nvidia gpu  : {YesNo(capabilities.HasNvidiaGpu)}");
        output.WriteLine($"  cuda usable : {YesNo(capabilities.CudaUsable)}");
        output.WriteLine($"  libav       : {DescribeLibav()}");
        output.WriteLine($"  log         : {LogPath()}");

        if (!capabilities.HasLibvmaf && !capabilities.HasLibvmafCuda)
        {
            output.WriteLine();
            output.WriteLine("No VMAF filter in this build. Pass --ffmpeg <path> to a VMAF-enabled build,");
            output.WriteLine("or set VQ_FFMPEG=/opt/ffmpeg/bin/ffmpeg (see docs/ffmpeg-cuda.md).");
        }

        return 0;
    }

    private async Task<int> ScoreAsync(string[] args)
    {
        List<string> files = Positional(args);
        if (files.Count < 2)
        {
            error.WriteLine(
                "usage: vq score <reference> <distorted> [--gpu|--cpu] [--model NAME] [--ffmpeg PATH] " +
                "[--csv out.csv] [--json out.json]");
            return 1;
        }

        string reference = files[0];
        string distorted = files[1];
        string ffmpeg = await VmafRunner.ResolveFfmpegAsync(GetOption(args, "--ffmpeg"), runner);
        VmafBackend backend = args.Contains("--gpu") ? VmafBackend.Cuda
            : args.Contains("--cpu") ? VmafBackend.Cpu
            : VmafBackend.Auto;
        var options = new VmafOptions
        {
            FfmpegPath = ffmpeg,
            Model = GetOption(args, "--model") ?? VmafModels.Default,
            Backend = backend,
            ProcessRunner = runner,
        };
        var progress = new Reporter<VmafProgress>(p =>
        {
            if (p.FrameNumber is int frame && frame % 24 == 0)
            {
                output.Write($"\r  scoring frame {frame}…");
            }
        });

        try
        {
            VmafReport report = await VmafRunner.RunAsync(reference, distorted, options, progress);
            output.WriteLine();
            output.WriteLine($"backend : {report.PrecisionLabel}");
            output.WriteLine($"model   : {VmafModels.DisplayName(report.Model)} ({report.Model})");
            output.WriteLine($"frames  : {report.FrameCount}");
            output.WriteLine($"mean    : {report.Stats.Mean:F3}");
            output.WriteLine($"hmean   : {report.Stats.HarmonicMean:F3}");
            output.WriteLine($"p1      : {report.Stats.P1:F3}");
            output.WriteLine($"min     : {report.Stats.Min:F3}");
            output.WriteLine($"max     : {report.Stats.Max:F3}");
            output.WriteLine($"stddev  : {report.Stats.StdDev:F3}");
            output.WriteLine($"elapsed : {report.Duration.TotalSeconds:F2}s");

            if (report.FellBackToCpu)
            {
                output.WriteLine($"note    : CUDA failed, scored on CPU: {FirstLine(report.FallbackReason)}");
            }
            else if (report.ConvertedTo8Bit)
            {
                output.WriteLine("note    : 10-bit input converted to 8-bit on the GPU; use --cpu for full precision.");
            }

            output.WriteLine($"command : {report.CommandLine}");

            if (GetOption(args, "--csv") is { } csv)
            {
                VmafReportExporter.WriteCsv(csv, report);
                output.WriteLine($"csv     : {csv}");
            }

            if (GetOption(args, "--json") is { } json)
            {
                VmafReportExporter.WriteJson(json, report, reference, distorted);
                output.WriteLine($"json    : {json}");
            }

            VqLog.Info("cli", $"score ok backend={report.Backend} frames={report.FrameCount} mean={report.Stats.Mean:F3}");
            return 0;
        }
        catch (Exception ex)
        {
            error.WriteLine();
            error.WriteLine("error: " + ex.Message);
            VqLog.Error("cli", "score failed", ex);
            return 2;
        }
    }

    private int Frame(string[] args)
    {
        List<string> positional = Positional(args);
        if (positional.Count == 0)
        {
            error.WriteLine("usage: vq frame <video> [index] [--ppm out.ppm]");
            return 1;
        }

        string video = positional[0];
        int index = positional.Skip(1).Select(a => int.TryParse(a, out int parsed) ? parsed : (int?)null).OfType<int>().FirstOrDefault();
        try
        {
            using FrameDecoder decoder = FrameDecoder.Open(video);
            output.WriteLine($"libav      : {FfmpegRuntime.Describe()}");
            output.WriteLine($"video      : {decoder.Info}");

            var stopwatch = Stopwatch.StartNew();
            DecodedFrame frame = decoder.DecodeFrame(index);
            stopwatch.Stop();

            output.WriteLine(
                $"frame {index} : {frame.Width}x{frame.Height}, stride {frame.Stride}, {frame.Bgra.Length:N0} bytes, " +
                $"pts {frame.Pts}, decoded in {stopwatch.ElapsedMilliseconds} ms");

            if (GetOption(args, "--ppm") is { } ppm)
            {
                WritePpm(ppm, frame);
                output.WriteLine($"wrote      : {ppm}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            error.WriteLine("error: " + ex.Message);
            VqLog.Error("cli", "frame failed", ex);
            return 2;
        }
    }

    private async Task<int> DiagnosticsAsync(string? requestedFfmpeg)
    {
        string ffmpeg = await VmafRunner.ResolveFfmpegAsync(requestedFfmpeg, runner);
        VmafCapabilities capabilities = await VmafRunner.ProbeAsync(ffmpeg, runner);
        string libav = DescribeLibav();
        string version = typeof(VmafRunner).Assembly.GetName().Version!.ToString();

        output.WriteLine("VideoQuality diagnostics");
        output.WriteLine($"  core version : {version}");
        output.WriteLine($"  .NET         : {Environment.Version}");
        output.WriteLine($"  OS           : {RuntimeInformation.OSDescription}");
        output.WriteLine($"  arch         : {RuntimeInformation.ProcessArchitecture}");
        output.WriteLine($"  session type : {environment("XDG_SESSION_TYPE") ?? "unknown"}");
        output.WriteLine($"  ffmpeg       : {ffmpeg}");
        output.WriteLine($"  ffmpeg ver   : {capabilities.FfmpegVersion}");
        output.WriteLine($"  libvmaf      : {capabilities.HasLibvmaf}");
        output.WriteLine($"  libvmaf_cuda : {capabilities.HasLibvmafCuda}");
        output.WriteLine($"  nvidia gpu   : {capabilities.HasNvidiaGpu}");
        output.WriteLine($"  libav        : {libav}");
        output.WriteLine($"  log          : {LogPath()}");

        VqLog.SessionHeader("diagnostics", new Dictionary<string, string>
        {
            ["core"] = version,
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

    private int Subset(string[] args)
    {
        List<string> scripts = Positional(args);
        if (scripts.Count == 0)
        {
            error.WriteLine("usage: vq subset <script.ass>... [--fonts DIR] [--output DIR]");
            return 1;
        }

        try
        {
            string scriptDir = Path.GetDirectoryName(Path.GetFullPath(scripts[0]))!;
            var options = new FontSubsetOptions
            {
                Scripts = scripts,
                FontDirectories = [GetOption(args, "--fonts") ?? Path.Combine(scriptDir, "fonts")],
                OutputDirectory = GetOption(args, "--output") ?? Path.Combine(scriptDir, "output"),
            };
            FontSubsetReport report = FontSubsetter.Run(options, new Reporter<string>(message => output.WriteLine("  " + message)));
            PrintSubsetReport(report);
            return report.Succeeded ? 0 : 2;
        }
        catch (Exception ex)
        {
            error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    private void PrintSubsetReport(FontSubsetReport report)
    {
        output.WriteLine();
        foreach (SubsetFont font in report.Fonts)
        {
            output.WriteLine($"  {font.NewFamily,-9} ← {string.Join(", ", font.ScriptNames)}");
            output.WriteLine($"            {Path.GetFileName(font.SourcePath)}#{font.SourceFace}: {font.Characters} chars, " +
                             $"{font.SourceBytes / 1024.0:N0} KB → {font.SubsetBytes / 1024.0:N1} KB");
        }

        foreach (FontRequest request in report.MissingFonts)
        {
            output.WriteLine($"  MISSING   {request} (no file in the font folders provides it)");
        }

        foreach ((FontRequest request, FontMatch match) in report.Synthesized)
        {
            string faux = string.Join(' ', new[] { match.FauxBold ? "faux bold" : null, match.FauxItalic ? "faux italic" : null }.OfType<string>());
            output.WriteLine($"  synthetic {request} → {match.Face} ({faux})");
        }

        foreach ((string font, IReadOnlyList<int> codepoints) in report.MissingGlyphs)
        {
            string more = codepoints.Count > 12 ? " …" : string.Empty;
            output.WriteLine($"  no glyph  {font}: {string.Join(" ", codepoints.Take(12).Select(c => $"U+{c:X4}"))}{more}");
        }

        foreach (string unused in report.UnusedStyleFonts)
        {
            output.WriteLine($"  unused    {unused} (named by a style, never drawn; not shipped)");
        }

        foreach (string script in report.ScriptsWritten)
        {
            output.WriteLine($"  wrote     {script}");
        }
    }

    private string DescribeLibav()
    {
        try
        {
            return describeLibav();
        }
        catch (Exception ex)
        {
            return $"unavailable: {ex.Message}";
        }
    }

    private string LogPath() => logPath() ?? "(disabled)";

    private static string YesNo(bool value) => value ? "yes" : "no";

    internal static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "unknown error";
        }

        int newline = text.IndexOfAny(['\r', '\n']);
        return newline < 0 ? text : text[..newline];
    }

    internal static void WritePpm(string path, DecodedFrame frame)
    {
        using FileStream stream = File.Create(path);
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

    /// <summary>Arguments after the command that are neither options nor option values.</summary>
    internal static List<string> Positional(string[] args)
    {
        var values = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                i += Flags.Contains(args[i]) ? 0 : 1; // an option's value is not positional
                continue;
            }

            values.Add(args[i]);
        }

        return values;
    }

    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "--gpu", "--cpu" };

    internal static string? GetOption(string[] args, string name)
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

    private void PrintHelp() => output.WriteLine("""
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

          vq subset <script.ass>... [--fonts DIR] [--output DIR]
              Subset and rename the fonts the scripts use (HarfBuzz). Defaults: fonts/ and output/
              next to the first script. Exit code 2 when a font is missing.
        """);

    /// <summary>Reports on the caller's thread (Progress&lt;T&gt; posts to the thread pool).</summary>
    private sealed class Reporter<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
