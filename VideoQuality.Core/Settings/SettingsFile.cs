using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using VideoQuality.Core.Diagnostics;

namespace VideoQuality.Core.Settings;

/// <summary>
/// Reads and writes <c>settings.json</c>. Portable first: the file lives beside the application
/// (<see cref="AppPaths.Root"/>) so a release folder carries its settings when copied. When that
/// folder is not writable (Program Files, a read-only share) it falls back to the per-user config
/// directory.
/// </summary>
public static class SettingsFile
{
    public const string FileName = "settings.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private const string ReflectionNote =
        "Settings types are (de)serialized by reflection; the app that uses them is not trimmed.";

    public static string PortablePath => Path.Combine(AppPaths.Root, FileName);

    public static string UserPath => UserPathFor(OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"));

    internal static string UserPathFor(bool windows, string? xdgConfigHome)
    {
        string directory = windows
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoQuality")
            : Path.Combine(
                string.IsNullOrWhiteSpace(xdgConfigHome)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                    : xdgConfigHome,
                "videoquality");
        return Path.Combine(directory, FileName);
    }

    /// <summary>Where the last <see cref="Load{T}()"/> or <see cref="Save{T}(T)"/> found the file.</summary>
    public static string? CurrentPath { get; internal set; }

    /// <summary>Where the settings are (or will be written when none exist yet).</summary>
    public static string Location => CurrentPath ?? PortablePath;

    [RequiresUnreferencedCode(ReflectionNote)]
    public static T Load<T>()
        where T : new() => Load<T>([PortablePath, UserPath]);

    [RequiresUnreferencedCode(ReflectionNote)]
    public static string? Save<T>(T settings) => Save(settings, [PortablePath, UserPath]);

    /// <summary>The first readable file wins; a corrupt file is logged and replaced by defaults.</summary>
    [RequiresUnreferencedCode(ReflectionNote)]
    internal static T Load<T>(IReadOnlyList<string> candidates)
        where T : new()
    {
        foreach (string path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                CurrentPath = path;
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                VqLog.Warn("settings", $"could not read {path}; using defaults", ex);
                return new T();
            }
        }

        return new T();
    }

    /// <summary>Writes atomically to the first location that accepts it; returns that path.</summary>
    [RequiresUnreferencedCode(ReflectionNote)]
    internal static string? Save<T>(T settings, IReadOnlyList<string> candidates)
    {
        string json = JsonSerializer.Serialize(settings, Options);

        foreach (string path in candidates)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, json);
                File.Move(temporary, path, overwrite: true);
                CurrentPath = path;
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                VqLog.Info("settings", $"cannot write {path} ({ex.Message}); trying the next location");
            }
        }

        VqLog.Warn("settings", "settings were not saved: no writable location");
        return null;
    }
}
