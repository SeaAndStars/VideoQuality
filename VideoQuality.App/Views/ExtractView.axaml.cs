using Avalonia.Controls;
using Avalonia.Interactivity;
using VideoQuality.App.Services;
using VideoQuality.App.ViewModels;

namespace VideoQuality.App.Views;

public partial class ExtractView : UserControl
{
    public ExtractView()
    {
        InitializeComponent();
        PageIo.AcceptDrops(this, async paths =>
        {
            if (paths.OfType<string>().FirstOrDefault(File.Exists) is { } file && ViewModel is { } vm)
            {
                await vm.OpenAsync(file);
            }
        });
    }

    private ExtractViewModel? ViewModel => DataContext as ExtractViewModel;

    private async void OnOpen(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> files = await PageIo.PickFilesAsync(this, "Open a video", false);
        if (files.Count > 0 && ViewModel is { } vm)
        {
            await vm.OpenAsync(files[0]);
        }
    }

    private async void OnPickOutput(object? sender, RoutedEventArgs e)
    {
        if (await PageIo.PickFolderAsync(this, "Extract into") is { } folder && ViewModel is { } vm)
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
