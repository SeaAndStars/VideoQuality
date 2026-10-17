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

            // VideoQuality.App [<reference>] [<encode> …] [--mode=sidebyside|wipe|flicker|difference]
            string? modeArgument = args.FirstOrDefault(a => a.StartsWith("--mode=", StringComparison.OrdinalIgnoreCase));
            if (modeArgument is not null &&
                Enum.TryParse(modeArgument["--mode=".Length..], ignoreCase: true, out CompareMode mode))
            {
                viewModel.CompareMode = mode;
            }

            var splashViewModel = new SplashViewModel();
            splashViewModel.Startup = StartupAsync(viewModel, splashViewModel);

            desktop.MainWindow = new SplashView(splashViewModel, () =>
            {
                var window = new MainWindow { DataContext = viewModel };
                desktop.MainWindow = window;

                // Startup arguments and "Open With" go through the same router as a drop: the first
                // file becomes the reference when none is set, the rest are encodes.
                string?[] media = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
                if (media.Length > 0)
                {
                    window.Opened += (_, _) => _ = viewModel.HandleDropAsync(media, Core.Ingest.DropZone.Auto);
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
