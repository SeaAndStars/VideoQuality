using System.Text;

namespace VideoQuality.Core.Diagnostics;

/// <summary>
/// Minimal structured file log. One line per event, written under a lock so the UI, the scorer and
/// background decoders can all log safely.
/// <para>
/// Logs go to <c>logs/</c> next to the executable, which keeps them easy to find for a desktop app.
/// <c>VQ_LOG_DIR</c> overrides the location; if that folder is not writable (an install under
/// Program Files) the per-user state folder is used instead; if neither is, logging is off.
/// </para>
/// </summary>
public static class VqLog
{
    private static readonly object Gate = new();
    private static LogFile? _file;

    public static string? CurrentPath => File.Path;

    /// <summary>Logs beside the executable; override with <c>VQ_LOG_DIR</c>.</summary>
    public static string DefaultDirectory => DefaultDirectoryFor(Environment.GetEnvironmentVariable("VQ_LOG_DIR"));

    internal static string DefaultDirectoryFor(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? Path.Combine(AppPaths.Root, "logs") : configured;

    /// <summary>Per-user fallback used when the application directory is not writable.</summary>
    public static string StateDirectory => StateDirectoryFor(OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable);

    internal static string StateDirectoryFor(bool windows, Func<string, string?> environment) =>
        windows
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoQuality", "logs")
            : !string.IsNullOrWhiteSpace(environment("XDG_STATE_HOME"))
                ? Path.Combine(environment("XDG_STATE_HOME")!, "videoquality", "logs")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state", "videoquality", "logs");

    private static LogFile File
    {
        get
        {
            lock (Gate)
            {
                return _file ??= LogFile.Open(DefaultDirectory, StateDirectory);
            }
        }
    }

    /// <summary>Opens the log (in <paramref name="directory"/> when given). Later calls do nothing.</summary>
    public static void Initialize(string? directory = null)
    {
        lock (Gate)
        {
            if (_file is not null)
            {
                return;
            }

            _file = directory is null ? LogFile.Open(DefaultDirectory, StateDirectory) : LogFile.Open(directory, StateDirectory);
        }

        Info("log", $"writing to {_file.Path}");
    }

    /// <summary>Closes the log so the next write (or <see cref="Initialize"/>) opens it again.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            _file = null;
        }
    }

    public static void Info(string source, string message) => File.Write("INF", source, message, null);

    public static void Warn(string source, string message, Exception? exception = null) =>
        File.Write("WRN", source, message, exception);

    public static void Error(string source, string message, Exception? exception = null) =>
        File.Write("ERR", source, message, exception);

    /// <summary>Logs a startup/session header so a bug report is self-contained.</summary>
    public static void SessionHeader(string title, IEnumerable<KeyValuePair<string, string>> fields)
    {
        Info("session", title);
        foreach (KeyValuePair<string, string> field in fields)
        {
            Info("session", $"{field.Key}: {field.Value}");
        }
    }
}

/// <summary>One day's log file; a null <see cref="Path"/> means logging is off.</summary>
internal sealed class LogFile(string? path)
{
    private readonly object _gate = new();

    public string? Path { get; } = path;

    /// <summary>The first of <paramref name="directories"/> that can be written to.</summary>
    public static LogFile Open(params string[] directories) =>
        new(directories.Select(Prepare).OfType<string>().Select(d => System.IO.Path.Combine(d, $"vq-{DateTime.Now:yyyyMMdd}.log")).FirstOrDefault());

    /// <summary>Creates the folder and proves it is writable; null when it is not.</summary>
    internal static string? Prepare(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string probe = System.IO.Path.Combine(directory, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return directory;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Appends one line. Logging never throws: a failed write is dropped.</summary>
    public void Write(string level, string source, string message, Exception? exception)
    {
        if (Path is null)
        {
            return;
        }

        var builder = new StringBuilder(message.Length + 64);
        builder.Append(DateTime.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture))
            .Append(" [").Append(level).Append("] ")
            .Append(source).Append(": ")
            .Append(message);
        if (exception is not null)
        {
            builder.Append(Environment.NewLine).Append(exception);
        }

        builder.AppendLine();
        lock (_gate)
        {
            try
            {
                File.AppendAllText(Path, builder.ToString(), Encoding.UTF8);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
