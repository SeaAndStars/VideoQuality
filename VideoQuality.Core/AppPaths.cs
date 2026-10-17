namespace VideoQuality.Core;

/// <summary>
/// The release layout, so every executable finds the same shared folders:
/// <code>
///   VideoQuality.exe   settings.json   ffmpeg\   logs\   licenses\
///   tools\vq.exe
/// </code>
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// The application root: the executable's folder, or its parent when the executable lives in
    /// <c>tools/</c> (the CLI in a release).
    /// </summary>
    public static string Root { get; } = ResolveRoot(AppContext.BaseDirectory);

    /// <summary>Where a release bundles ffmpeg and its libraries.</summary>
    public static string BundledFfmpegDirectory => Path.Combine(Root, "ffmpeg");

    internal static string ResolveRoot(string baseDirectory)
    {
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        if (string.Equals(Path.GetFileName(directory), "tools", StringComparison.OrdinalIgnoreCase) &&
            Path.GetDirectoryName(directory) is { } parent)
        {
            return parent;
        }

        return directory;
    }
}
