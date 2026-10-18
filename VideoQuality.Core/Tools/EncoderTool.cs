using System.Text.RegularExpressions;

namespace VideoQuality.Core.Tools;

/// <summary>
/// The ffmpeg that encodes (Encode, Trim &amp; Merge, Pipeline): a release bundles a GPL build in
/// <c>tools/ffmpeg-encode</c>. Only ever run as a separate process.
/// </summary>
public sealed partial record EncoderTool(
    string Path,
    string Version,
    IReadOnlySet<string> Encoders,
    IReadOnlySet<string> Filters)
{
    /// <summary>Where a release bundles the encoder ffmpeg.</summary>
    public static string BundledDirectory => System.IO.Path.Combine(AppPaths.Root, "tools", "ffmpeg-encode");

    public bool HasEncoder(string name) => Encoders.Contains(name);

    public bool HasFilter(string name) => Filters.Contains(name);

    /// <summary>The named values hevc_nvenc takes (presets, tunes, …); empty without NVENC.</summary>
    public IReadOnlySet<string> NvencValues { get; init; } = new HashSet<string>();

    /// <summary>hevc_nvenc has the ultra-high-quality tune (NVENC SDK 12.2+ headers; older builds stop at <c>hq</c>).</summary>
    public bool SupportsNvencUhq => NvencValues.Contains("uhq");

    /// <summary>
    /// Where to look, in order: the folder chosen in Settings, the bundled copy, then PATH
    /// (<c>null</c>).
    /// </summary>
    public static IReadOnlyList<string?> Candidates(string? folder)
    {
        var candidates = new List<string?>();
        if (!string.IsNullOrWhiteSpace(folder))
        {
            candidates.Add(folder);
        }

        candidates.Add(BundledDirectory);
        candidates.Add(null);
        return candidates;
    }

    /// <summary>The first candidate that runs as ffmpeg, with its encoders and filters; null when none.</summary>
    public static async Task<EncoderTool?> LocateAsync(
        string? folder, IProcessRunner runner, CancellationToken cancellationToken = default)
    {
        string exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        foreach (string? directory in Candidates(folder))
        {
            if (directory is not null && !File.Exists(System.IO.Path.Combine(directory, exe)))
            {
                continue;
            }

            string path = directory is null ? exe : System.IO.Path.Combine(directory, exe);
            try
            {
                ProcessResult version = await runner.RunAsync(
                    new ProcessRequest { FileName = path, Arguments = ["-hide_banner", "-version"] }, cancellationToken).ConfigureAwait(false);
                if (version.ExitCode != 0 || !version.Output.StartsWith("ffmpeg version", StringComparison.Ordinal))
                {
                    continue;
                }

                ProcessResult encoders = await runner.RunAsync(
                    new ProcessRequest { FileName = path, Arguments = ["-hide_banner", "-encoders"] }, cancellationToken).ConfigureAwait(false);
                ProcessResult filters = await runner.RunAsync(
                    new ProcessRequest { FileName = path, Arguments = ["-hide_banner", "-filters"] }, cancellationToken).ConfigureAwait(false);
                IReadOnlySet<string> encoderNames = ParseList(encoders.Output, EncoderLine());
                IReadOnlySet<string> nvenc = new HashSet<string>();
                if (encoderNames.Contains("hevc_nvenc"))
                {
                    ProcessResult help = await runner.RunAsync(
                        new ProcessRequest { FileName = path, Arguments = ["-hide_banner", "-h", "encoder=hevc_nvenc"] }, cancellationToken).ConfigureAwait(false);
                    nvenc = ParseList(help.Output, OptionValueLine());
                }

                return new EncoderTool(
                    path,
                    version.Output.Split('\n')[0].Trim(),
                    encoderNames,
                    ParseList(filters.Output, FilterLine()))
                {
                    NvencValues = nvenc,
                };
            }
            catch (ProcessStartException)
            {
                // Not on PATH (or not runnable); try the next location.
            }
        }

        return null;
    }

    internal static IReadOnlySet<string> ParseList(string text, Regex line) =>
        line.Matches(text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    // " V....D libx265              libx265 H.265 / HEVC" (capabilities, then the name)
    [GeneratedRegex(@"^ [VAS][F.][S.][X.][B.][D.] (\S+)", RegexOptions.Multiline)]
    internal static partial Regex EncoderLine();

    // "     uhq             5            E..V....... Ultra high quality" (a named option value)
    [GeneratedRegex(@"^\s{4,}(\w+)\s+-?\d+\s+E\.", RegexOptions.Multiline)]
    internal static partial Regex OptionValueLine();

    // " TS bwdif             V->V       Deinterlace" (timeline/slice flags, then the name)
    [GeneratedRegex(@"^ [T.][S.][C.]? (\S+)\s+\S*->\S*", RegexOptions.Multiline)]
    internal static partial Regex FilterLine();
}
