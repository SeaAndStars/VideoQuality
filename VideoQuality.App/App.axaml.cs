using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using VideoQuality.App.ViewModels;
using Avalonia.Controls;
using VideoQuality.App.Views;
using VideoQuality.Core.Decoding;
using VideoQuality.Core.Diagnostics;
using VideoQuality.Core.Localization;
using VideoQuality.Core.Settings;

namespace VideoQuality.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ApplyLanguage(SettingsFile.Load<UserSettings>().Language);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Start(desktop, StartupOptions.Parse(desktop.Args ?? []));
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The splash, then the main window; <c>--self-test</c> closes the app once it is up.</summary>
    private static void Start(IClassicDesktopStyleApplicationLifetime desktop, StartupOptions options)
    {
        var viewModel = new MainViewModel();
        var splashViewModel = new SplashViewModel();
        splashViewModel.Startup = StartupAsync(viewModel, splashViewModel, FfmpegRuntime.Initialize);
        desktop.MainWindow = new SplashView(splashViewModel, () =>
        {
            Window window = CreateMainWindow(viewModel, options);
            desktop.MainWindow = window;
            if (options.SelfTest)
            {
                window.Opened += (_, _) => desktop.Shutdown(0);
            }

            return window;
        });
    }

    /// <summary>
    /// The main window for the startup arguments: <c>--mode=</c> picks the compare mode,
    /// <c>--page=</c> the page, and files go to that page as if dropped there (scripts alone open
    /// Font Subset; anything else goes through Compare's drop router).
    /// </summary>
    internal static Window CreateMainWindow(MainViewModel viewModel, StartupOptions options)
    {
        if (options.Mode is { } mode)
        {
            viewModel.ApplyStartupMode(mode);
        }

        var shell = new ShellViewModel(viewModel);
        var window = new MainWindow { DataContext = shell };
        if (options.Page is { } page)
        {
            shell.Navigate(page);
        }

        if (options.Media.Count > 0)
        {
            window.Opened += async (_, _) =>
            {
                if (options.Page is null || !await shell.OpenFilesAsync(options.Page, options.Media))
                {
                    await viewModel.HandleDropAsync(options.Media, Core.Ingest.DropZone.Auto);
                }
            };
        }

        return window;
    }

    /// <summary>
    /// Picks the text catalog before any view is created (the language is fixed per process) and
    /// gives the Semi/Ursa themes the matching locale for their own built-in text (Yes/No, …).
    /// </summary>
    private void ApplyLanguage(string? language)
    {
        Loc.SetLanguage(language);
        CultureInfo culture = CultureFor(Loc.Language);
        foreach (IStyle style in Styles)
        {
            switch (style)
            {
                case Semi.Avalonia.SemiTheme semi:
                    semi.Locale = culture;
                    break;
                case Ursa.Themes.Semi.UrsaSemiTheme ursa:
                    ursa.Locale = culture;
                    break;
            }
        }
    }

    /// <summary>The theme locale for a text catalog.</summary>
    internal static CultureInfo CultureFor(string language) => new(language == "zh-Hans" ? "zh-CN" : "en-US");

    /// <summary>The work the splash waits for: libav, then ffmpeg resolution and capability probe.</summary>
    internal static async Task StartupAsync(MainViewModel viewModel, SplashViewModel splash, Action loadLibav)
    {
        splash.Status = Loc.T("splash.loadingLibav");
        try
        {
            await Task.Run(loadLibav).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // The main window reports decode problems itself; the splash only must not hang.
            VqLog.Warn("startup", "libav did not load", ex);
        }

        splash.Status = Loc.T("splash.findingFfmpeg");
        await viewModel.InitializeAsync().ConfigureAwait(true);
        splash.Status = Loc.T("splash.ready");
        splash.IsReady = true;
    }
}

/// <summary>
/// <c>VideoQuality.App [&lt;reference&gt;] [&lt;encode&gt; …] [--mode=sidebyside|wipe|flicker|difference] [--page=&lt;key&gt;] [--self-test] [--headless]</c>
/// </summary>
internal sealed record StartupOptions(CompareMode? Mode, string? Page, IReadOnlyList<string?> Media, bool SelfTest)
{
    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        string? Value(string name) => args.FirstOrDefault(a => a.StartsWith(name, StringComparison.OrdinalIgnoreCase))?[name.Length..];

        CompareMode? mode = Enum.TryParse(Value("--mode="), ignoreCase: true, out CompareMode parsed) ? parsed : null;
        string? page = Value("--page=")?.ToLowerInvariant();
        string?[] media = [.. args.Where(a => !a.StartsWith("--", StringComparison.Ordinal))];
        if (page is null && media.Length > 0 && media.All(m => Path.GetExtension(m!).ToLowerInvariant() is ".ass" or ".ssa"))
        {
            page = "subset";
        }

        return new StartupOptions(mode, page, media, args.Contains("--self-test"));
    }
}
