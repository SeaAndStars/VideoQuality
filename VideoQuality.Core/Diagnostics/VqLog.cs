using System.Runtime.InteropServices;
using System.Text;

namespace VideoQuality.Core.Diagnostics;

/// <summary>
/// Minimal structured file log. One line per event, written under a lock so the UI, the scorer and
/// background decoders can all log safely. Defaults to <c>$XDG_STATE_HOME/videoquality/logs</c> on
/// Linux and <c>%LOCALAPPDATA%\VideoQuality\logs</c> on Windows.
/// </summary>
public static class VqLog
{
    private static readonly object Gate = new();
    private static string? _path;
    private static bool _initialized;

    public static string? CurrentPath => _path;

    public static string DefaultDirectory
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
