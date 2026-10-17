using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VideoQuality.App.ViewModels;
using VideoQuality.App.Views;
using VideoQuality.Core.Decoding;
using VideoQuality.Core.Diagnostics;

namespace VideoQuality.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
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

    /// <summary>The work the splash waits for: libav, then ffmpeg resolution and capability probe.</summary>
    private static async Task StartupAsync(MainViewModel viewModel, SplashViewModel splash)
    {
        splash.Status = "Loading FFmpeg libraries…";
        try
        {
            await Task.Run(FfmpegRuntime.Initialize).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // The main window reports decode problems itself; the splash only must not hang.
            VqLog.Warn("startup", "libav did not load", ex);
        }

        splash.Status = "Finding a VMAF-capable ffmpeg…";
        await viewModel.InitializeAsync().ConfigureAwait(true);
        splash.Status = "Ready";
        splash.IsReady = true;
    }
}
