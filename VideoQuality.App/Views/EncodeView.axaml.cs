using Avalonia.Controls;
using Avalonia.Interactivity;
using VideoQuality.App.Services;
using VideoQuality.App.ViewModels;
using VideoQuality.Core.Localization;

namespace VideoQuality.App.Views;

public partial class EncodeView : UserControl
{
    public EncodeView()
    {
        InitializeComponent();
        PageIo.AcceptDrops(this, async paths =>
        {
            if (ViewModel is { } vm)
            {
                await vm.AddPathsAsync(paths);
            }
        });
    }

    private EncodeViewModel? ViewModel => DataContext as EncodeViewModel;

    private async void OnAddFiles(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            await vm.AddPathsAsync(await PageIo.PickFilesAsync(this, Loc.T("encode.picker.add"), true));
        }
    }

    private async void OnPickOutput(object? sender, RoutedEventArgs e)
    {
        if (await PageIo.PickFolderAsync(this, Loc.T("picker.outputFolder")) is { } folder && ViewModel is { } vm)
        {
            vm.OutputDirectory = folder;
        }
    }
}
