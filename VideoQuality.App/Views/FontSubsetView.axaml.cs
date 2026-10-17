using Avalonia.Controls;
using Avalonia.Interactivity;
using VideoQuality.App.Services;
using VideoQuality.App.ViewModels;

namespace VideoQuality.App.Views;

public partial class FontSubsetView : UserControl
{
    public FontSubsetView()
    {
        InitializeComponent();
        PageIo.AcceptDrops(this, paths =>
        {
            ViewModel?.AddPaths(paths);
            return Task.CompletedTask;
        });
    }

    private FontSubsetViewModel? ViewModel => DataContext as FontSubsetViewModel;

    private async void OnAddScripts(object? sender, RoutedEventArgs e) =>
        ViewModel?.AddPaths(await PageIo.PickFilesAsync(this, "Add ASS scripts", true, ("ASS/SSA scripts", ["*.ass", "*.ssa"])));

    private async void OnPickFontFolder(object? sender, RoutedEventArgs e)
    {
        if (await PageIo.PickFolderAsync(this, "Folder with the fonts") is { } folder && ViewModel is { } vm)
        {
            vm.FontDirectory = folder;
        }
    }

    private async void OnPickOutputFolder(object? sender, RoutedEventArgs e)
    {
        if (await PageIo.PickFolderAsync(this, "Output folder") is { } folder && ViewModel is { } vm)
        {
            vm.OutputDirectory = folder;
        }
    }

    private async void OnOpenOutput(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { OutputDirectory.Length: > 0 } vm)
        {
            await PageIo.RevealAsync(this, vm.OutputDirectory);
        }
    }
}
