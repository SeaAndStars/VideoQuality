using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using VideoQuality.Core.Tools;

namespace VideoQuality.App.Services;

/// <summary>What the view models need from the outside world; tests pass scripted versions.</summary>
public sealed class AppServices
{
    public static AppServices Default { get; } = new();

    public IProcessRunner Processes { get; init; } = SystemProcessRunner.Instance;

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
        (string fileName, IReadOnlyList<string> args) = RestartCommand(Environment.ProcessPath!, Environment.GetCommandLineArgs()[0], arguments);
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
