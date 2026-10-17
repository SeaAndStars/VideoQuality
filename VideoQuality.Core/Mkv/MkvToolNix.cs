using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VideoQuality.Core.Diagnostics;

namespace VideoQuality.Core.Mkv;

/// <summary>One track to take from a mux input, with the metadata it should carry.</summary>
public sealed record MuxTrack(int Id, string? Language = null, string? Name = null, bool? Default = null, bool? Forced = null);

/// <summary>A source file for mkvmerge; <see cref="Tracks"/> null means every track as-is.</summary>
public sealed record MuxInput(string Path, IReadOnlyList<MuxTrack>? Tracks = null, bool KeepAttachments = true, bool KeepChapters = true);

/// <summary>Everything mkvmerge needs to build one release file.</summary>
public sealed record MuxPlan(
    string Output,
    IReadOnlyList<MuxInput> Inputs,
    IReadOnlyList<string>? Attachments = null,
    string? Chapters = null,
    string? Title = null);

/// <summary>Progress, warnings and the final status of an mkvtoolnix run.</summary>
public sealed record MkvRunResult(int ExitCode, IReadOnlyList<string> Warnings, IReadOnlyList<string> Errors)
{
    /// <summary>mkvtoolnix exits 0 on success, 1 with warnings (output still written), 2 on error.</summary>
    public bool Succeeded => ExitCode is 0 or 1;
}

/// <summary>
/// mkvtoolnix (mkvmerge / mkvextract) for demuxing and muxing. Arguments go through a JSON option
/// file, so paths with spaces, quotes or CJK characters need no shell escaping on any platform.
/// </summary>
public sealed partial class MkvToolNix
{
    private MkvToolNix(string mkvmerge, string mkvextract, string version)
    {
        MkvMerge = mkvmerge;
        MkvExtract = mkvextract;
        Version = version;
    }

    public string MkvMerge { get; }

    public string MkvExtract { get; }

    public string Version { get; }

    /// <summary>
    /// Finds mkvtoolnix: an explicit folder, a portable copy in the app's tools/mkvtoolnix, PATH,
    /// then the standard Windows install. Null when none works.
    /// </summary>
    public static async Task<MkvToolNix?> LocateAsync(string? folder = null, CancellationToken cancellationToken = default)
    {
        string exe = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        var candidates = new List<string?>
        {
            folder,
            Path.Combine(AppPaths.Root, "tools", "mkvtoolnix"),
            null, // PATH
        };

        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MKVToolNix"));
        }

        foreach (string? directory in candidates)
        {
            if (directory is not null && !Directory.Exists(directory))
            {
                continue;
            }

            string merge = directory is null ? "mkvmerge" + exe : Path.Combine(directory, "mkvmerge" + exe);
            string extract = directory is null ? "mkvextract" + exe : Path.Combine(directory, "mkvextract" + exe);
            try
            {
                (int code, string output) = await CaptureAsync(merge, ["--version"], cancellationToken).ConfigureAwait(false);
                if (code == 0 && output.StartsWith("mkvmerge", StringComparison.Ordinal))
                {
                    return new MkvToolNix(merge, extract, output.Split('\n')[0].Trim());
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Not here; try the next location.
            }
        }

        return null;
    }

    public async Task<MkvFileInfo> IdentifyAsync(string path, CancellationToken cancellationToken = default)
    {
        (int code, string output) = await CaptureAsync(MkvMerge, ["-J", path], cancellationToken).ConfigureAwait(false);
        if (code == 2 && !output.TrimStart().StartsWith('{'))
        {
            throw new InvalidDataException(output.Trim());
        }

        return MkvFileInfo.Parse(output);
    }

    // ---------------------------------------------------------------- extract

    /// <summary>mkvextract arguments for the chosen tracks, attachments and chapters.</summary>
    public static IReadOnlyList<string> BuildExtractArguments(
        string source,
        IEnumerable<(int Id, string Output)> tracks,
        IEnumerable<(int Id, string Output)> attachments,
        string? chaptersOutput)
    {
        var args = new List<string> { "--gui-mode", source };
        var trackList = tracks.ToList();
        if (trackList.Count > 0)
        {
            args.Add("tracks");
            args.AddRange(trackList.Select(t => $"{t.Id}:{t.Output}"));
        }

        var attachmentList = attachments.ToList();
        if (attachmentList.Count > 0)
        {
            args.Add("attachments");
            args.AddRange(attachmentList.Select(a => $"{a.Id}:{a.Output}"));
        }

        if (chaptersOutput is not null)
        {
            args.Add("chapters");
            args.Add(chaptersOutput);
        }

        return args;
    }

    public Task<MkvRunResult> ExtractAsync(IReadOnlyList<string> arguments, IProgress<double>? progress, CancellationToken cancellationToken) =>
        RunAsync(MkvExtract, arguments, progress, cancellationToken);

    // ---------------------------------------------------------------- mux

    /// <summary>
    /// mkvmerge arguments for a plan. Per-input track selection uses --video/--audio/--subtitle-tracks
    /// style ID lists via the generic --*-tracks options, and per-track metadata uses ID-qualified
    /// options, exactly as mkvmerge's documentation describes.
    /// </summary>
    public static IReadOnlyList<string> BuildMuxArguments(MuxPlan plan, IReadOnlyDictionary<string, MkvFileInfo> identified)
    {
        var args = new List<string> { "--gui-mode", "--output", plan.Output };
        if (!string.IsNullOrWhiteSpace(plan.Title))
        {
            args.AddRange(["--title", plan.Title]);
        }

        foreach (MuxInput input in plan.Inputs)
        {
            if (input.Tracks is { } selected && identified.TryGetValue(input.Path, out MkvFileInfo? info))
            {
                AddSelection(args, "--video-tracks", "--no-video", MkvTrackType.Video, info, selected);
                AddSelection(args, "--audio-tracks", "--no-audio", MkvTrackType.Audio, info, selected);
                AddSelection(args, "--subtitle-tracks", "--no-subtitles", MkvTrackType.Subtitles, info, selected);

                foreach (MuxTrack track in selected)
                {
                    if (track.Language is { Length: > 0 } language)
                    {
                        args.AddRange(["--language", $"{track.Id}:{language}"]);
                    }

                    if (track.Name is not null)
                    {
                        args.AddRange(["--track-name", $"{track.Id}:{track.Name}"]);
                    }

                    if (track.Default is bool isDefault)
                    {
                        args.AddRange(["--default-track-flag", $"{track.Id}:{(isDefault ? "yes" : "no")}"]);
                    }

                    if (track.Forced is bool isForced)
                    {
                        args.AddRange(["--forced-display-flag", $"{track.Id}:{(isForced ? "yes" : "no")}"]);
                    }
                }
            }

            if (!input.KeepAttachments)
            {
                args.Add("--no-attachments");
            }

            if (!input.KeepChapters)
            {
                args.Add("--no-chapters");
            }

            args.Add(input.Path);
        }

        foreach (string attachment in plan.Attachments ?? [])
        {
            args.AddRange(["--attachment-mime-type", FontMimeType(attachment), "--attach-file", attachment]);
        }

        if (!string.IsNullOrWhiteSpace(plan.Chapters))
        {
            args.AddRange(["--chapters", plan.Chapters]);
        }

        return args;
    }

    private static void AddSelection(
        List<string> args, string option, string none, MkvTrackType type, MkvFileInfo info, IReadOnlyList<MuxTrack> selected)
    {
        var ofType = info.Tracks.Where(t => t.Type == type).Select(t => t.Id).ToHashSet();
        var keep = selected.Where(s => ofType.Contains(s.Id)).Select(s => s.Id).ToList();
        if (keep.Count == 0 && ofType.Count > 0)
        {
            args.Add(none);
        }
        else if (keep.Count < ofType.Count)
        {
            args.AddRange([option, string.Join(',', keep)]);
        }
    }

    /// <summary>MIME types players and mkvmerge itself use for embedded fonts.</summary>
    public static string FontMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        ".ttc" or ".otc" => "font/collection",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream",
    };

    public async Task<MkvRunResult> MuxAsync(IReadOnlyList<string> arguments, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // A JSON option file sidesteps every quoting rule (spaces, quotes, CJK paths) on every OS.
        string optionFile = Path.Combine(Path.GetTempPath(), $"vq_mkvmerge_{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(optionFile, JsonSerializer.Serialize(arguments), new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return await RunAsync(MkvMerge, ["@" + optionFile], progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(optionFile);
            }
            catch (IOException)
            {
                // Temp file; harmless if it lingers.
            }
        }
    }

    // ---------------------------------------------------------------- process plumbing

    /// <summary>Parses one line of --gui-mode output.</summary>
    internal static (double? Progress, string? Warning, string? Error) ParseGuiLine(string line)
    {
        if (ProgressPattern().Match(line) is { Success: true } progress)
        {
            return (int.Parse(progress.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) / 100.0, null, null);
        }

        if (line.StartsWith("#GUI#warning ", StringComparison.Ordinal))
        {
            return (null, line["#GUI#warning ".Length..].Trim(), null);
        }

        if (line.StartsWith("#GUI#error ", StringComparison.Ordinal))
        {
            return (null, null, line["#GUI#error ".Length..].Trim());
        }

        return (null, null, null);
    }

    [GeneratedRegex(@"^#GUI#progress (\d+)%")]
    private static partial Regex ProgressPattern();

    private static async Task<MkvRunResult> RunAsync(
        string executable, IReadOnlyList<string> arguments, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        VqLog.Info("mkv", $"{Path.GetFileName(executable)} {string.Join(' ', arguments)}");
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {executable}.");
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        });

        var warnings = new List<string>();
        var errors = new List<string>();
        Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        while (await process.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
        {
            (double? value, string? warning, string? error) = ParseGuiLine(line);
            if (value is double fraction)
            {
                progress?.Report(fraction);
            }

            if (warning is not null)
            {
                warnings.Add(warning);
            }

            if (error is not null)
            {
                errors.Add(error);
            }
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        string standardError = await stderr.ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(standardError))
        {
            errors.Add(standardError.Trim());
        }

        cancellationToken.ThrowIfCancellationRequested();
        VqLog.Info("mkv", $"{Path.GetFileName(executable)} exit {process.ExitCode}, {warnings.Count} warning(s), {errors.Count} error(s)");
        return new MkvRunResult(process.ExitCode, warnings, errors);
    }

    private static async Task<(int Code, string Output)> CaptureAsync(string executable, string[] arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {executable}.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string text = await output.ConfigureAwait(false);
        return (process.ExitCode, text.Length > 0 ? text : await error.ConfigureAwait(false));
    }
}
