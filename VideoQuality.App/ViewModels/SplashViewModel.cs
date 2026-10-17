using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VideoQuality.App.ViewModels;

/// <summary>Status line and version for the splash window.</summary>
public partial class SplashViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Status { get; set; } = "Starting…";

    public string Version { get; } = "v" + (typeof(SplashViewModel).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        .Split('+')[0] ?? "dev");

    /// <summary>The startup work the splash waits for; set by the app before the splash opens.</summary>
    public Task Startup { get; set; } = Task.CompletedTask;
}
