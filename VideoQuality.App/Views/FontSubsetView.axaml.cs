using Avalonia.Controls;
using Avalonia.Interactivity;
using VideoQuality.App.Services;
using VideoQuality.App.ViewModels;
using VideoQuality.Core.Localization;

namespace VideoQuality.App.Views;

public partial class FontSubsetView : UserControl
{
    public FontSubsetView()
    {
        InitializeComponent();
        PageIo.AcceptDrops(this, paths =>
        {
            ViewModel.AddPaths(paths);
            return Task.CompletedTask;
        });
    }

    private FontSubsetViewModel ViewModel => (FontSubsetViewModel)DataContext!;

    private async void OnAddScripts(object? sender, RoutedEventArgs e) =>
        ViewModel.AddPaths(await PageIo.PickFilesAsync(this, Loc.T("picker.addScripts"), true, (Loc.T("picker.type.scripts"), ["*.ass", "*.ssa"])));

    private async void OnPickFontFolder(object? sender, RoutedEventArgs e)
    {
        if (await PageIo.PickFolderAsync(this, Loc.T("picker.fontFolder")) is { } folder)
        {
            ViewModel.FontDirectory = folder;
        }
    }

    private async void OnPickOutputFolder(object? sender, RoutedEventArgs e)
    {
        if (await PageIo.PickFolderAsync(this, Loc.T("picker.outputFolder")) is { } folder)
        {
            ViewModel.OutputDirectory = folder;
        }
    }

    private async void OnOpenOutput(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.OutputDirectory.Length > 0)
        {
            await PageIo.RevealAsync(this, ViewModel.OutputDirectory);
        }
    }
}
