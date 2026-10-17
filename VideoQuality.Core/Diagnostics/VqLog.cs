using System.Runtime.InteropServices;
using System.Text;

namespace VideoQuality.Core.Diagnostics;

/// <summary>
/// Minimal structured file log. One line per event, written under a lock so the UI, the scorer and
/// background decoders can all log safely.
/// <para>
/// Logs go to <c>logs/</c> next to the executable, which keeps them easy to find for a desktop app.
/// <c>VQ_LOG_DIR</c> overrides the location; if the application directory is read-only the logger
/// falls back to the user state directory.
/// </para>
/// </summary>
public static class VqLog
{
    private static readonly object Gate = new();
    private static string? _path;
    private static bool _initialized;

    public static string? CurrentPath => _path;

    /// <summary>Logs beside the executable; override with <c>VQ_LOG_DIR</c>.</summary>
    public static string DefaultDirectory
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable("VQ_LOG_DIR");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured;
            }

            return Path.Combine(AppContext.BaseDirectory, "logs");
        }
    }

    /// <summary>Per-user fallback used when the application directory is not writable.</summary>
    public static string StateDirectory
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VideoQuality",
                    "logs");
            }

            string? stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            if (!string.IsNullOrWhiteSpace(stateHome))
            {
                return Path.Combine(stateHome, "videoquality", "logs");
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "state",
                "videoquality",
                "logs");
        }
    }

    public static void Initialize(string? directory = null)
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

            try
            {
                directory ??= DefaultDirectory;
                Directory.CreateDirectory(directory);
                _path = Path.Combine(directory, $"vq-{DateTime.Now:yyyyMMdd}.log");
                _initialized = true;
            }
            catch
            {
                // Logging must never take the app down.
                _initialized = true;
                _path = null;
                return;
            }
        }

        Info("log", $"writing to {_path}");
    }

    private static string? TryPrepare(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);

            // Prove the directory is writable before committing to it.
            string probe = Path.Combine(directory, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);

            return directory;
        }
        catch
        {
            return null;
        }
    }

    public static void Info(string source, string message) => Write("INF", source, message, null);

    public static void Warn(string source, string message, Exception? exception = null) =>
        Write("WRN", source, message, exception);

    public static void Error(string source, string message, Exception? exception = null) =>
        Write("ERR", source, message, exception);

    /// <summary>Logs a startup/session header so a bug report is self-contained.</summary>
    public static void SessionHeader(string title, IEnumerable<KeyValuePair<string, string>> fields)
    {
        Info("session", title);

        foreach (KeyValuePair<string, string> field in fields)
        {
            Info("session", $"{field.Key}: {field.Value}");
        }
    }

    private static void Write(string level, string source, string message, Exception? exception)
    {
        if (!_initialized)
        {
            Initialize();
        }

        if (_path is null)
        {
            return;
        }

        var builder = new StringBuilder(message.Length + 64);
        builder.Append(DateTime.Now.ToString("HH:mm:ss.fff"))
            .Append(" [").Append(level).Append("] ")
            .Append(source).Append(": ")
            .Append(message);

        if (exception is not null)
        {
            builder.Append(Environment.NewLine).Append(exception);
        }

        builder.AppendLine();

        lock (Gate)
        {
            try
            {
                File.AppendAllText(_path, builder.ToString(), Encoding.UTF8);
            }
            catch
            {
                // Never propagate logging failures.
            }
        }
    }
}
