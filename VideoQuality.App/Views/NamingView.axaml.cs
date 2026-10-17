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
            if (paths.FirstOrDefault(p => !string.IsNullOrEmpty(p)) is { } path)
            {
                await ViewModel.FillFromAsync(path);
            }
        });
        DataContextChanged += (_, _) =>
        {
            if (DataContext is NamingViewModel vm)
            {
                vm.CopyRequested -= OnCopyRequested;
                vm.CopyRequested += OnCopyRequested;
            }
        };
    }

    private NamingViewModel ViewModel => (NamingViewModel)DataContext!;

    // Copy is only offered on the visible page, so the window and its clipboard exist.
    private async void OnCopyRequested(object? sender, string text)
    {
        await TopLevel.GetTopLevel(this)!.Clipboard!.SetTextAsync(text);
        ToastHost.Show(new ToastRequest(ToastKind.Success, Loc.T("naming.copied")));
    }
}
