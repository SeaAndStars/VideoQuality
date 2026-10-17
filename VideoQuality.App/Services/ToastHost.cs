using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Ursa.Controls;
using VideoQuality.App.ViewModels;
using VideoQuality.Core.Localization;

namespace VideoQuality.App.Services;

/// <summary>
/// One toast surface for the whole window, so every page can report without owning a window.
/// The shell initialises it once the window has loaded.
/// </summary>
public static class ToastHost
{
    private static WindowToastManager? _manager;
    private static Window? _owner;

    public static void Initialize(Window owner)
    {
        _owner = owner;
        _manager = new WindowToastManager(owner) { MaxItems = 4 };
    }

    public static void Show(ToastRequest request)
    {
        if (_manager is null)
        {
            return;
        }

        NotificationType type = request.Kind switch
        {
            ToastKind.Success => NotificationType.Success,
            ToastKind.Warning => NotificationType.Warning,
            ToastKind.Error => NotificationType.Error,
            _ => NotificationType.Information,
        };

        object content = request.Message;
        if (request.ActionLabel is not null && request.Action is not null)
        {
            var action = new Button
            {
                Content = request.ActionLabel,
                Theme = _owner?.FindResource("BorderlessButton") as ControlTheme,
                Padding = new Avalonia.Thickness(8, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };

            action.Click += (_, _) =>
            {
                action.IsEnabled = false;
                action.Content = Loc.T("common.done");
                request.Action();
            };

            content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = request.Message, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, VerticalAlignment = VerticalAlignment.Center },
                    action,
                },
            };
        }

        TimeSpan expiration = request.Action is not null ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(4);
        _manager.Show(content, type, expiration, showIcon: true, showClose: true, onClick: null, onClose: null, classes: []);
    }

    /// <summary>How a question is put to the user; tests answer it themselves.</summary>
    public static Func<Window, string, string, Task<bool>> Ask { get; set; } = AskWithMessageBoxAsync;

    /// <summary>Yes/No question; closing the box without choosing counts as yes (the default).</summary>
    public static Task<bool> ConfirmAsync(string title, string message) =>
        _owner is null ? Task.FromResult(true) : Ask(_owner, title, message);

    internal static async Task<bool> AskWithMessageBoxAsync(Window owner, string title, string message)
    {
        MessageBoxResult result = await MessageBox.ShowAsync(owner, message, title, MessageBoxIcon.Question, MessageBoxButton.YesNo);
        return result != MessageBoxResult.No;
    }
}
