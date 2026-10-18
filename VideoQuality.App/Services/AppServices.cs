using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using VideoQuality.App.ViewModels;
using VideoQuality.Core.Settings;
using VideoQuality.Core.Tools;

namespace VideoQuality.App.Services;

/// <summary>What the view models need from the outside world; tests pass scripted versions.</summary>
public sealed class AppServices
{
    public static AppServices Default { get; } = new();

    public IProcessRunner Processes { get; init; } = SystemProcessRunner.Instance;

    /// <summary>HTTP for online presets; tests pass a client with a scripted handler.</summary>
    public HttpClient Http { get; init; } = SharedHttp;

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Environment variables (VQ_FFMPEG, …); tests pass their own.</summary>
    public Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;

    /// <summary>The loaded libav libraries, for the session log (throws when they cannot load).</summary>
    public Func<string> DescribeLibav { get; init; } = VideoQuality.Core.Decoding.FfmpegRuntime.Describe;

    /// <summary>Where the user's settings live; tests keep theirs in memory.</summary>
    public ISettingsStore Settings { get; init; } = FileSettingsStore.Instance;

    internal static void ShutdownDesktop() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
}

/// <summary>Loads and saves <see cref="UserSettings"/>.</summary>
public interface ISettingsStore
{
    UserSettings Load();

    void Save(UserSettings settings);
}

/// <summary>settings.json beside the app, or in the user's config folder (see <see cref="SettingsFile"/>).</summary>
public sealed class FileSettingsStore : ISettingsStore
{
    public static FileSettingsStore Instance { get; } = new();

    public UserSettings Load() => SettingsFile.Load<UserSettings>();

    public void Save(UserSettings settings) => SettingsFile.Save(settings);
}
