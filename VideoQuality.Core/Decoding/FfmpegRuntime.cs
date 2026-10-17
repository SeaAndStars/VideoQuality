using System.Runtime.InteropServices;
using VideoQuality.Core.Diagnostics;
using FFmpeg.AutoGen;

namespace VideoQuality.Core.Decoding;

/// <summary>
/// Locates the native FFmpeg shared libraries for <see cref="FFmpeg.AutoGen"/> and configures
/// FFmpeg logging. Call <see cref="Initialize"/> before touching any FFmpeg API.
/// </summary>
public static class FfmpegRuntime
{
    /// <summary>
    /// libavcodec major the installed FFmpeg.AutoGen bindings were generated for. Bump together with
    /// the package: 62 for AutoGen 8.x (FFmpeg 8), 63 for AutoGen 9.x (FFmpeg 9).
    /// </summary>
    public const int ExpectedAvcodecMajor = 63;

    private static readonly object Gate = new();
    private static bool _initialized;
    private static av_log_set_callback_callback? _avLogCallback;

    /// <summary>The directory the bindings were pointed at, if one was found.</summary>
    public static string? LibraryDirectory { get; private set; }

    /// <summary>libavcodec major actually loaded; equals <see cref="ExpectedAvcodecMajor"/> or Initialize throws.</summary>
    public static int LoadedAvcodecMajor { get; private set; }

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

            // Fail loudly on an ABI mismatch: bindings for one major loading another can corrupt
            // memory silently.
            int loadedMajor = (int)(ffmpeg.avcodec_version() >> 16);
            if (loadedMajor != ExpectedAvcodecMajor)
            {
                string message =
                    $"FFmpeg binding mismatch: FFmpeg.AutoGen expects libavcodec {ExpectedAvcodecMajor}, " +
                    $"but {loadedMajor} was loaded from {LibraryDirectory ?? "(system search path)"}. " +
                    "Point FFMPEG_LIB_PATH at a build with the matching soname, or update the bindings.";

                VqLog.Error("libav", message);
                throw new InvalidOperationException(message);
            }

            LoadedAvcodecMajor = loadedMajor;
            VqLog.Info("libav", $"loaded libavcodec {loadedMajor} from {LibraryDirectory ?? "(system search path)"}");

            // Route libav's own warnings/errors into the application log; the viewer decodes
            // in-process, where stderr would otherwise be invisible.
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_WARNING);
            InstallAvLogCallback();
            _initialized = true;
        }
    }

    /// <summary>Installs the libav log bridge. Safe to call more than once.</summary>
    public static unsafe void InstallAvLogCallback()
    {
        if (_avLogCallback is not null)
        {
            return;
        }

        _avLogCallback = OnAvLog;
        ffmpeg.av_log_set_callback(_avLogCallback);
        VqLog.Info("libav", "log bridge installed");
    }

    private static unsafe void OnAvLog(void* avcl, int level, string format, byte* vl)
    {
        try
        {
            // Levels are multiples of 8 plus optional print flags; mask the flags off.
            int severity = level & 0xF8;
            if (severity > ffmpeg.AV_LOG_WARNING)
            {
                return;
            }

            const int BufferSize = 1024;
            byte* buffer = stackalloc byte[BufferSize];
            int printPrefix = 1;
            ffmpeg.av_log_format_line(avcl, level, format, vl, buffer, BufferSize, &printPrefix);

            string message = Marshal.PtrToStringAnsi((IntPtr)buffer)?.Trim() ?? string.Empty;
            if (message.Length == 0)
            {
                return;
            }

            if (severity <= ffmpeg.AV_LOG_ERROR)
            {
                VqLog.Error("libav", message);
            }
            else
            {
                VqLog.Warn("libav", message);
            }
        }
        catch
        {
            // A logger must never break decoding.
        }
    }

    /// <summary>Library path and the libavcodec version actually loaded, for session logs.</summary>
    public static string Describe()
    {
        Initialize();
        return $"{LibraryDirectory ?? "(system search path)"} (avcodec {ffmpeg.avcodec_version()})";
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
        yield return "/opt/ffmpeg/lib";
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
        var directories = Candidates()
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Prefer a directory that actually has the soname these bindings expect; loading a
        // different major from an earlier PATH entry is the failure this guards against.
        string exactSoname = $"libavcodec.so.{ExpectedAvcodecMajor}";
        string exactDll = $"avcodec-{ExpectedAvcodecMajor}.dll";

        foreach (string directory in directories)
        {
            if (File.Exists(Path.Combine(directory, exactSoname)) ||
                File.Exists(Path.Combine(directory, exactDll)))
            {
                return directory;
            }
        }

        // Fall back to any libav build so the guard can report a useful mismatch message.
        foreach (string directory in directories)
        {
            try
            {
                if (Directory.EnumerateFiles(directory, "libavcodec.so*").Any() ||
                    Directory.EnumerateFiles(directory, "avcodec-*.dll").Any())
                {
                    return directory;
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
