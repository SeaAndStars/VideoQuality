namespace VideoQuality.Tests;

/// <summary>
/// The real entry point with <c>--headless --self-test</c>: starts the app on the headless platform,
/// shows the splash and the main window, and exits 0. It runs here, not in the App tests, because a
/// process can start only one Avalonia application.
/// </summary>
public class AppStartupTests
{
    [Fact]
    public void The_app_starts_headless_and_exits_cleanly()
    {
        VideoQuality.App.Services.AppServices.ShutdownDesktop(); // no application yet: nothing to shut down
        int code = 0;
        var thread = new Thread(() => code = VideoQuality.App.Program.Main(["--headless", "--self-test", "--page=settings"]));
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "the app did not exit");
        Assert.Equal(0, code);
    }
}
