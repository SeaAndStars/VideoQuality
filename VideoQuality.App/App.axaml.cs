using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VideoQuality.App.ViewModels;
using VideoQuality.App.Views;

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
            var window = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = window;

            _ = viewModel.InitializeAsync();

            // VideoQuality.App [<reference>] [<encode> …] [--mode=sidebyside|wipe|flicker|difference]
            if (desktop.Args is { Length: > 0 } args)
            {
                string? modeArgument = args.FirstOrDefault(a => a.StartsWith("--mode=", StringComparison.OrdinalIgnoreCase));
                if (modeArgument is not null &&
                    Enum.TryParse(modeArgument["--mode=".Length..], ignoreCase: true, out CompareMode mode))
                {
                    viewModel.CompareMode = mode;
                }

                // Startup arguments and "Open With" go through the same router as a drop: the first
                // file becomes the reference when none is set, the rest are encodes.
                string?[] media = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
                if (media.Length > 0)
                {
                    _ = viewModel.HandleDropAsync(media, Core.Ingest.DropZone.Auto);
                }
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
