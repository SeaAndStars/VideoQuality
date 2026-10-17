using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using VideoQuality.Core.Diagnostics;
using VideoQuality.Core.Localization;
using VideoQuality.Core.Tools;

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
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            string? directory = FindLibraryDirectory(Candidates(Environment.GetEnvironmentVariable));
            LibraryDirectory = directory;
            UseDirectory(directory, path => ffmpeg.RootPath = path, OperatingSystem.IsWindows() ? SetDllDirectory : null);
            VqLog.Info("libav", $"loading from {Describe(directory)}");

            // Fail loudly on an ABI mismatch: bindings for one major loading another can corrupt
            // memory silently. A missing dependency DLL surfaces as NotSupportedException.
            LoadedAvcodecMajor = ValidateLoaded(() => (int)(ffmpeg.avcodec_version() >> 16), directory);
            VqLog.Info("libav", $"loaded libavcodec {LoadedAvcodecMajor} from {Describe(directory)}");

            // Route libav's own warnings/errors into the application log; the viewer decodes
            // in-process, where stderr would otherwise be invisible.
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_WARNING);
            InstallAvLogCallback();
            _initialized = true;
        }
    }

    /// <summary>
    /// Points the bindings at <paramref name="directory"/>. On Windows the bindings load
    /// avcodec/avutil/… by full path, but Windows resolves *their* imports (libdav1d.dll, libvmaf.dll,
    /// libwinpthread-1.dll) only from the app directory, system directories and PATH, so the FFmpeg
    /// directory is added to that search with <paramref name="setDllDirectory"/>.
    /// </summary>
    internal static void UseDirectory(string? directory, Action<string> setRoot, Func<string, bool>? setDllDirectory)
    {
        if (directory is null)
        {
            return;
        }

        setRoot(directory);
        if (setDllDirectory is not null && !setDllDirectory(directory))
        {
            VqLog.Warn("libav", $"SetDllDirectory({directory}) failed: {Marshal.GetLastPInvokeError()}");
        }
    }

    /// <summary>The libavcodec major <paramref name="loadMajor"/> reports, or a clear error.</summary>
    internal static int ValidateLoaded(Func<int> loadMajor, string? directory)
    {
        int major;
        try
        {
            major = loadMajor();
        }
        catch (NotSupportedException ex)
        {
            string message = Loc.T("libav.error.load", ExpectedAvcodecMajor, Describe(directory));
            VqLog.Error("libav", message, ex);
            throw new InvalidOperationException(message, ex);
        }

        if (major != ExpectedAvcodecMajor)
        {
            string message = Loc.T("libav.error.mismatch", ExpectedAvcodecMajor, major, Describe(directory));
            VqLog.Error("libav", message);
            throw new InvalidOperationException(message);
        }

        return major;
    }

    internal static string Describe(string? directory) => directory ?? Loc.T("libav.systemPath");

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
        // Levels are multiples of 8 plus optional print flags; mask the flags off. Every message
        // reaches the callback, so the level filter is ours.
        int severity = level & 0xF8;
        if (severity > ffmpeg.AV_LOG_WARNING)
        {
            return;
        }

        const int BufferSize = 1024;
        byte* buffer = stackalloc byte[BufferSize];
        int printPrefix = 1;
        ffmpeg.av_log_format_line(avcl, level, format, vl, buffer, BufferSize, &printPrefix);

        // libav formats UTF-8; the ANSI decoder garbled non-ASCII file names on Windows.
        WriteLogLine(severity, Marshal.PtrToStringUTF8((IntPtr)buffer));
    }

    /// <summary>Writes one libav log line (warnings and errors only; empty lines and known noise are dropped).</summary>
    internal static void WriteLogLine(int severity, string? text)
    {
        string message = (text ?? string.Empty).Trim();
        if (message.Length == 0 || IsNoise(message))
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

    /// <summary>
    /// libav warnings that say nothing useful for this app. Matroska font attachments never have
    /// codec parameters, so every open of such a file logged one two-line warning per font.
    /// </summary>
    internal static bool IsNoise(string message) =>
        message.Contains("Could not find codec parameters for stream", StringComparison.Ordinal) &&
        message.Contains("(Attachment", StringComparison.Ordinal);

    /// <summary>Library path and the libavcodec version actually loaded, for session logs.</summary>
    public static string Describe()
    {
        Initialize();
        return $"{Describe(LibraryDirectory)} (avcodec {ffmpeg.avcodec_version()})";
    }

    /// <summary>Human-readable text for a negative FFmpeg return code.</summary>
    public static unsafe string ErrorText(int error)
    {
        const int BufferSize = 1024;
        byte* buffer = stackalloc byte[BufferSize];
        ffmpeg.av_strerror(error, buffer, BufferSize);
        return Marshal.PtrToStringUTF8((IntPtr)buffer)!;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string path);

    /// <summary>Where to look for the libraries, most specific first.</summary>
    internal static IEnumerable<string> Candidates(Func<string, string?> environment)
    {
        yield return environment("FFMPEG_LIB_PATH") ?? string.Empty;

        // Windows builds keep the DLLs next to ffmpeg.exe in bin\; Linux uses lib/.
        string root = environment("FFMPEG_ROOT") ?? string.Empty;
        foreach (string folder in root.Length > 0 ? (string[])[root, Path.Combine(root, "bin"), Path.Combine(root, "lib")] : [])
        {
            yield return folder;
        }

        // The ffmpeg executable the user pointed at usually has its DLLs beside it on Windows.
        yield return Path.GetDirectoryName(environment("VQ_FFMPEG") ?? string.Empty) ?? string.Empty;

        // A release bundles ffmpeg and its libraries in ffmpeg/ beside the app.
        yield return AppPaths.BundledFfmpegDirectory;
        yield return AppContext.BaseDirectory;

        foreach (string folder in SystemFolders)
        {
            yield return folder;
        }
    }

    /// <summary>Usual install locations (scripts/build-ffmpeg-windows.sh installs to C:\ffmpeg\bin).</summary>
    internal static IReadOnlyList<string> SystemFolders { get; } = OperatingSystem.IsWindows()
        ? [@"C:\ffmpeg\bin"]
        : ["/opt/ffmpeg/lib", "/usr/lib/x86_64-linux-gnu", "/usr/lib/aarch64-linux-gnu", "/usr/lib", "/usr/local/lib", "/opt/homebrew/lib", "/usr/local/lib64", "/usr/lib64"];

    /// <summary>
    /// The first folder with the libavcodec these bindings expect; else the first with any libavcodec
    /// (so the version check can report a useful mismatch); else null (the system search path).
    /// </summary>
    internal static string? FindLibraryDirectory(IEnumerable<string> candidates)
    {
        List<string> directories = [.. candidates
            .Where(candidate => candidate.Length > 0 && Directory.Exists(candidate))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)];

        string exactSoname = $"libavcodec.so.{ExpectedAvcodecMajor}";
        string exactDll = $"avcodec-{ExpectedAvcodecMajor}.dll";
        return directories.FirstOrDefault(d => File.Exists(Path.Combine(d, exactSoname)) || File.Exists(Path.Combine(d, exactDll)))
            ?? directories.FirstOrDefault(d => FileSystem.SafeEnumerateFiles(d, "libavcodec.so*").Concat(FileSystem.SafeEnumerateFiles(d, "avcodec-*.dll")).Any());
    }
}
