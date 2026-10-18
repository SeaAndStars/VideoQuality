using VideoQuality.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using VideoQuality.Core.Localization;

namespace VideoQuality.App.ViewModels;

/// <summary>Status line and version for the splash window.</summary>
public partial class SplashViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Status { get; set; } = Loc.T("splash.starting");

    /// <summary>Startup finished: the spinner gives way to a check mark.</summary>
    [ObservableProperty]
    public partial bool IsReady { get; set; }

    public string Version => AppInfo.Version;

    /// <summary>The startup work the splash waits for; set by the app before the splash opens.</summary>
    public Task Startup { get; set; } = Task.CompletedTask;
}
