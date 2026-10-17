using Avalonia.Controls;
using Avalonia.Interactivity;
using VideoQuality.App.Services;
using VideoQuality.App.ViewModels;
using VideoQuality.Core.Localization;

namespace VideoQuality.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private SettingsViewModel ViewModel => (SettingsViewModel)DataContext!;

    private async void OnPickMkvToolNix(object? sender, RoutedEventArgs e)
    {
        if (await PageIo.PickFolderAsync(this, Loc.T("settings.picker.mkvtoolnix")) is { } folder)
        {
            ViewModel.MkvToolNixFolder = folder;
            await ViewModel.LocateToolsAsync();
        }
    }

    private async void OnPickEncoder(object? sender, RoutedEventArgs e)
    {
        if (await PageIo.PickFolderAsync(this, Loc.T("settings.picker.encoder")) is { } folder)
        {
            ViewModel.EncoderFolder = folder;
            await ViewModel.LocateToolsAsync();
        }
    }

    private async void OnOpenAppFolder(object? sender, RoutedEventArgs e) =>
        await PageIo.RevealAsync(this, ViewModel.AppFolder);
}
