using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VideoQuality.Core.Diagnostics;
using VideoQuality.Core.Tools;

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
    private MkvToolNix(string mkvmerge, string mkvextract, string version, IProcessRunner runner)
    {
        MkvMerge = mkvmerge;
        MkvExtract = mkvextract;
        Version = version;
        Runner = runner;
    }

    /// <summary>Runs mkvmerge and mkvextract; tests substitute scripted tools.</summary>
    public IProcessRunner Runner { get; }

    public string MkvMerge { get; }

    public string MkvExtract { get; }

    public string Version { get; }

    /// <summary>
    /// Finds mkvtoolnix: an explicit folder, a portable copy in the app's tools/mkvtoolnix, PATH,
    /// then the standard Windows install. Null when none works.
    /// </summary>
    public static Task<MkvToolNix?> LocateAsync(string? folder = null, CancellationToken cancellationToken = default) =>
        LocateAsync(folder, SystemProcessRunner.Instance, cancellationToken);

    /// <inheritdoc cref="LocateAsync(string?, CancellationToken)"/>
    public static async Task<MkvToolNix?> LocateAsync(string? folder, IProcessRunner runner, CancellationToken cancellationToken = default)
    {
        string exe = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        foreach (string? directory in Candidates(folder))
        {
            if (directory is not null && !Directory.Exists(directory))
            {
                continue;
            }

            string merge = directory is null ? "mkvmerge" + exe : Path.Combine(directory, "mkvmerge" + exe);
            string extract = directory is null ? "mkvextract" + exe : Path.Combine(directory, "mkvextract" + exe);
            try
            {
                (int code, string output) = await CaptureAsync(runner, merge, ["--version"], cancellationToken).ConfigureAwait(false);
                if (code == 0 && output.StartsWith("mkvmerge", StringComparison.Ordinal))
                {
                    return new MkvToolNix(merge, extract, output.Split('\n')[0].Trim(), runner);
                }
            }
            catch (ProcessStartException)
            {
                // Not here; try the next location.
            }
        }

        return null;
    }

    /// <summary>
    /// Where to look, in order: the folder chosen in Settings, the copy bundled with a release, PATH
    /// (<c>null</c>), then the default Windows install.
    /// </summary>
    internal static IReadOnlyList<string?> Candidates(string? folder)
    {
        var candidates = new List<string?>();
        if (!string.IsNullOrWhiteSpace(folder))
        {
            candidates.Add(folder);
        }

        candidates.Add(BundledDirectory);
        candidates.Add(null);
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MKVToolNix"));
        }

        return candidates;
    }

    /// <summary>Where a release bundles mkvmerge and mkvextract.</summary>
    public static string BundledDirectory => Path.Combine(AppPaths.Root, "tools", "mkvtoolnix");

    public async Task<MkvFileInfo> IdentifyAsync(string path, CancellationToken cancellationToken = default)
    {
        (int code, string output) = await CaptureAsync(Runner, MkvMerge, ["-J", path], cancellationToken).ConfigureAwait(false);
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
        RunAsync(Runner, MkvExtract, arguments, progress, cancellationToken);

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
            return await RunAsync(Runner, MkvMerge, ["@" + optionFile], progress, cancellationToken).ConfigureAwait(false);
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
    public static (double? Progress, string? Warning, string? Error) ParseGuiLine(string line)
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
        IProcessRunner runner, string executable, IReadOnlyList<string> arguments, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        VqLog.Info("mkv", $"{Path.GetFileName(executable)} {string.Join(' ', arguments)}");
        var warnings = new List<string>();
        var errors = new List<string>();
        ProcessResult result = await runner.RunAsync(
            new ProcessRequest
            {
                FileName = executable,
                Arguments = arguments,
                OnOutputLine = line =>
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
                },
            },
            cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            errors.Add(result.Error.Trim());
        }

        VqLog.Info("mkv", $"{Path.GetFileName(executable)} exit {result.ExitCode}, {warnings.Count} warning(s), {errors.Count} error(s)");
        return new MkvRunResult(result.ExitCode, warnings, errors);
    }

    private static async Task<(int Code, string Output)> CaptureAsync(
        IProcessRunner runner, string executable, string[] arguments, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner.RunAsync(
            new ProcessRequest { FileName = executable, Arguments = arguments, CaptureLimit = 64 * 1024 * 1024 },
            cancellationToken).ConfigureAwait(false);
        return (result.ExitCode, result.Output.Length > 0 ? result.Output : result.Error);
    }
}
