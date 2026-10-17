using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace VideoQuality.Core.Decoding;

/// <summary>
/// Locates the native FFmpeg shared libraries for <see cref="FFmpeg.AutoGen"/> and configures
/// FFmpeg logging. Call <see cref="Initialize"/> before touching any FFmpeg API.
/// </summary>
public static class FfmpegRuntime
{
    private static readonly object Gate = new();
    private static bool _initialized;

    /// <summary>The directory the bindings were pointed at, if one was found.</summary>
    public static string? LibraryDirectory { get; private set; }

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            string? directory = FindLibraryDirectory();
            if (directory is not null)
            {
                ffmpeg.RootPath = directory;
                LibraryDirectory = directory;
            }

            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            _initialized = true;
        }
    }

    /// <summary>Human-readable text for a negative FFmpeg return code.</summary>
    public static unsafe string ErrorText(int error)
    {
        const int BufferSize = 1024;
        byte* buffer = stackalloc byte[BufferSize];
        ffmpeg.av_strerror(error, buffer, BufferSize);
        return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? $"FFmpeg error {error}";
    }

    private static IEnumerable<string> Candidates()
    {
        yield return Environment.GetEnvironmentVariable("FFMPEG_LIB_PATH") ?? string.Empty;
        yield return Environment.GetEnvironmentVariable("FFMPEG_ROOT") ?? string.Empty;
        yield return AppContext.BaseDirectory;
        yield return "/usr/lib/x86_64-linux-gnu";
        yield return "/usr/lib/aarch64-linux-gnu";
        yield return "/usr/lib";
        yield return "/usr/local/lib";
        yield return "/opt/ffmpeg/lib";
        yield return "/opt/homebrew/lib";
        yield return "/usr/local/lib64";
        yield return "/usr/lib64";
    }

    private static string? FindLibraryDirectory()
    {
        foreach (string candidate in Candidates())
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate))
            {
                continue;
            }

            try
            {
                if (Directory.EnumerateFiles(candidate, "libavcodec.so*").Any() ||
                    Directory.EnumerateFiles(candidate, "avcodec-*.dll").Any())
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch (IOException)
            {
                // Unreadable directory; keep looking.
            }
            catch (UnauthorizedAccessException)
            {
                // Unreadable directory; keep looking.
            }
        }

        return null;
    }
}
