using Avalonia;
using Avalonia.Headless;
using VideoQuality.Core.Diagnostics;

namespace VideoQuality.App;

internal sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args) =>
        Run(args, a => BuildAvaloniaApp(a.Contains("--headless")).StartWithClassicDesktopLifetime(a));

    /// <summary>Logging and crash reporting around <paramref name="start"/>, which runs the app.</summary>
    internal static int Run(string[] args, Func<string[], int> start)
    {
        VqLog.Initialize();
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            return start(args);
        }
        catch (Exception ex)
        {
            VqLog.Error("crash", "fatal startup error", ex);
            throw;
        }
    }

    internal static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e) =>
        VqLog.Error("crash", "unhandled exception", e.ExceptionObject as Exception);

    internal static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        VqLog.Error("crash", "unobserved task exception", e.Exception);
        e.SetObserved();
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(headless: false);

    /// <summary>The desktop platform, or with <c>--headless</c> no window system at all (smoke tests, CI).</summary>
    public static AppBuilder BuildAvaloniaApp(bool headless) => headless
        ? AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
        : AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
