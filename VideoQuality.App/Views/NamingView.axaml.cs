using Avalonia.Controls;
using Avalonia.Input.Platform;
using VideoQuality.App.Services;
using VideoQuality.App.ViewModels;
using VideoQuality.Core.Localization;

namespace VideoQuality.App.Views;

public partial class NamingView : UserControl
{
    public NamingView()
    {
        InitializeComponent();
        PageIo.AcceptDrops(this, async paths =>
        {
            if (paths.FirstOrDefault(p => !string.IsNullOrEmpty(p)) is { } path && ViewModel is { } vm)
            {
                await vm.FillFromAsync(path);
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

    private NamingViewModel? ViewModel => DataContext as NamingViewModel;

    private async void OnCopyRequested(object? sender, string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
            ToastHost.Show(new ToastRequest(ToastKind.Success, Loc.T("naming.copied")));
        }
    }
}
