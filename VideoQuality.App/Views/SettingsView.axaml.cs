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

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    private async void OnPickMkvToolNix(object? sender, RoutedEventArgs e)
    {
        if (await PageIo.PickFolderAsync(this, Loc.T("settings.picker.mkvtoolnix")) is { } folder && ViewModel is { } vm)
        {
            vm.MkvToolNixFolder = folder;
            await vm.LocateToolsAsync();
        }
    }

    private async void OnPickEncoder(object? sender, RoutedEventArgs e)
    {
        if (await PageIo.PickFolderAsync(this, Loc.T("settings.picker.encoder")) is { } folder && ViewModel is { } vm)
        {
            vm.EncoderFolder = folder;
            await vm.LocateToolsAsync();
        }
    }

    private async void OnOpenAppFolder(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            await PageIo.RevealAsync(this, vm.AppFolder);
        }
    }
}
