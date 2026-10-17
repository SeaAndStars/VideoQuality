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

            // VideoQuality.App <reference> <encode> [<encode> …]
            if (desktop.Args is { Length: >= 2 } args)
            {
                _ = viewModel.OpenAndAnalyzeAsync(args[0], args[1..]);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
