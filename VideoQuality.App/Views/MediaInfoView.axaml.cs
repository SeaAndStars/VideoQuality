using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using VideoQuality.App.Services;
using VideoQuality.App.ViewModels;
using VideoQuality.Core.Localization;

namespace VideoQuality.App.Views;

public partial class MediaInfoView : UserControl
{
    public MediaInfoView()
    {
        InitializeComponent();
        PageIo.AcceptDrops(this, async paths =>
        {
            if (paths.FirstOrDefault(p => !string.IsNullOrEmpty(p)) is { } path && ViewModel is { } vm)
            {
                await vm.OpenAsync(path);
            }
        });
        DataContextChanged += (_, _) =>
        {
            if (ViewModel is { } vm)
            {
                vm.CopyRequested -= OnCopyRequested;
                vm.CopyRequested += OnCopyRequested;
            }
        };
    }

    private MediaInfoViewModel? ViewModel => DataContext as MediaInfoViewModel;

    private async void OnCopyRequested(object? sender, string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
            ToastHost.Show(new ToastRequest(ToastKind.Success, Loc.T("mediainfo.copied")));
        }
    }

    private async void OnOpen(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> files = await PageIo.PickFilesAsync(this, Loc.T("extract.picker.open"), false);
        if (files.Count > 0 && ViewModel is { } vm)
        {
            await vm.OpenAsync(files[0]);
        }
    }
}
