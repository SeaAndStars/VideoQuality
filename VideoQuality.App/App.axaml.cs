using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using VideoQuality.App.ViewModels;
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
            var viewModel = new MainViewModel();
            string[] args = desktop.Args ?? [];

            // VideoQuality.App [<reference>] [<encode> …] [--mode=sidebyside|wipe|flicker|difference] [--page=<key>]
            string? modeArgument = args.FirstOrDefault(a => a.StartsWith("--mode=", StringComparison.OrdinalIgnoreCase));
            if (modeArgument is not null &&
                Enum.TryParse(modeArgument["--mode=".Length..], ignoreCase: true, out CompareMode mode))
            {
                viewModel.ApplyStartupMode(mode);
            }

            var splashViewModel = new SplashViewModel();
            splashViewModel.Startup = StartupAsync(viewModel, splashViewModel);

            desktop.MainWindow = new SplashView(splashViewModel, () =>
            {
                var shell = new ShellViewModel(viewModel);
                var window = new MainWindow { DataContext = shell };
                desktop.MainWindow = window;

                // --page=compare|subset|extract|mux|pipeline|settings opens that page, and files given
                // with it go to that page as if dropped there. Scripts alone open Font Subset;
                // anything else goes through Compare's drop router (the first file becomes the
                // reference when none is set).
                string? page = args.FirstOrDefault(a => a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))?["--page=".Length..].ToLowerInvariant();
                string?[] media = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
                if (page is null && media.Length > 0 && media.All(m => Path.GetExtension(m) is { } ext &&
                        (ext.Equals(".ass", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ssa", StringComparison.OrdinalIgnoreCase))))
                {
                    page = "subset";
                }

                if (page is not null)
                {
                    shell.Navigate(page);
                }

                if (media.Length > 0)
                {
                    window.Opened += async (_, _) =>
                    {
                        if (page is null || !await shell.OpenFilesAsync(page, media))
                        {
                            await viewModel.HandleDropAsync(media, Core.Ingest.DropZone.Auto);
                        }
                    };
                }

                return window;
            });
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Picks the text catalog before any view is created (the language is fixed per process) and
    /// gives the Semi/Ursa themes the matching locale for their own built-in text (Yes/No, …).
    /// </summary>
    private void ApplyLanguage(string? language)
    {
        Loc.SetLanguage(language);
        var culture = new CultureInfo(Loc.Language == "zh-Hans" ? "zh-CN" : "en-US");
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

    /// <summary>The work the splash waits for: libav, then ffmpeg resolution and capability probe.</summary>
    private static async Task StartupAsync(MainViewModel viewModel, SplashViewModel splash)
    {
        splash.Status = Loc.T("splash.loadingLibav");
        try
        {
            await Task.Run(FfmpegRuntime.Initialize).ConfigureAwait(true);
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
