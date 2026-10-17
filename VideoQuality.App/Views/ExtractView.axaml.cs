using Avalonia.Controls;
using Avalonia.Interactivity;
using VideoQuality.App.Services;
using VideoQuality.App.ViewModels;
using VideoQuality.Core.Localization;

namespace VideoQuality.App.Views;

public partial class ExtractView : UserControl
{
    public ExtractView()
    {
        InitializeComponent();
        PageIo.AcceptDrops(this, async paths =>
        {
            if (paths.OfType<string>().FirstOrDefault(File.Exists) is { } file)
            {
                await ViewModel.OpenAsync(file);
            }
        });
    }

    private ExtractViewModel ViewModel => (ExtractViewModel)DataContext!;

    private async void OnOpen(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> files = await PageIo.PickFilesAsync(this, Loc.T("extract.picker.open"), false);
        if (files.Count > 0)
        {
            await ViewModel.OpenAsync(files[0]);
        }
    }

    private async void OnPickOutput(object? sender, RoutedEventArgs e)
    {
        if (await PageIo.PickFolderAsync(this, Loc.T("extract.picker.output")) is { } folder)
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
