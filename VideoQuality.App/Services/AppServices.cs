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

    /// <summary>Environment variables (VQ_FFMPEG, …); tests pass their own.</summary>
    public Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;

    /// <summary>The loaded libav libraries, for the session log (throws when they cannot load).</summary>
    public Func<string> DescribeLibav { get; init; } = VideoQuality.Core.Decoding.FfmpegRuntime.Describe;

    /// <summary>Where the user's settings live; tests keep theirs in memory.</summary>
    public ISettingsStore Settings { get; init; } = FileSettingsStore.Instance;

    /// <summary>Starts the app again with the given arguments and closes this instance.</summary>
    public Action<IReadOnlyList<string>> Restart { get; init; } = RestartThisApp;

    /// <summary>Starts a process without waiting for it; tests replace it.</summary>
    internal static Action<ProcessStartInfo> StartProcess { get; set; } = StartDetached;

    /// <summary>Ends this instance; tests replace it.</summary>
    internal static Action ShutdownApp { get; set; } = ShutdownDesktop;

    /// <summary>
    /// The program and arguments that start this app again: the app itself, or under
    /// <c>dotnet VideoQuality.App.dll</c> the host with the app's dll.
    /// </summary>
    public static (string FileName, IReadOnlyList<string> Arguments) RestartCommand(string processPath, string entryAssembly, IReadOnlyList<string> arguments) =>
        Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? (processPath, [entryAssembly, .. arguments])
            : (processPath, arguments);

    private static void RestartThisApp(IReadOnlyList<string> arguments)
    {
        (string fileName, IReadOnlyList<string> args) = RestartCommand(System.Environment.ProcessPath!, System.Environment.GetCommandLineArgs()[0], arguments);
        var start = new ProcessStartInfo(fileName) { UseShellExecute = false };
        foreach (string argument in args)
        {
            start.ArgumentList.Add(argument);
        }

        StartProcess(start);
        ShutdownApp();
    }

    // Without shell execution Process.Start always returns the started process.
    internal static void StartDetached(ProcessStartInfo start) => Process.Start(start)!.Dispose();

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
