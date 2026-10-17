using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Ursa.Controls;
using VideoQuality.App.Services;
using VideoQuality.App.ViewModels;

namespace VideoQuality.App.Views;

/// <summary>The shell: self-drawn title bar, navigation rail, and the toolkit's pages.</summary>
public partial class MainWindow : UrsaWindow
{
    public MainWindow()
    {
        InitializeComponent();

        // Chrome size changes with DPI, maximise/restore and theme; re-measure after each layout
        // pass (cheap, and a no-op unless the overlap actually changed).
        LayoutUpdated += (_, _) => FitBelowTitleBar();
        SizeChanged += (_, _) => FitBelowTitleBar();
        Loaded += (_, _) =>
        {
            ToastHost.Initialize(this);
            FitBelowTitleBar();
        };
        DataContextChanged += (_, _) =>
        {
            if (Shell is { } shell)
            {
                shell.RevealRequested -= OnRevealRequested;
                shell.RevealRequested += OnRevealRequested;
            }
        };
    }

    private async void OnRevealRequested(object? sender, string path) => await PageIo.RevealAsync(this, path);

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    private void OnToggleRail(object? sender, RoutedEventArgs e)
    {
        if (Shell is { } shell)
        {
            shell.IsNavCollapsed = !shell.IsNavCollapsed;
        }
    }

    private void OnSettingsNavSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && Shell is { } shell)
        {
            shell.Navigate("settings");
        }
    }

    /// <summary>
    /// Ursa draws its title bar and caption buttons over the window content. Push the content down
    /// to clear the lowest of them: the caption buttons are taller than the title-bar strip and
    /// their size changes with DPI and when the window is maximised, so measure rather than assume.
    /// </summary>
    private void FitBelowTitleBar()
    {
        if (Root.GetVisualParent() is not Visual host)
        {
            return;
        }

        double contentTop = host.TranslatePoint(new Point(0, 0), this)?.Y ?? 0;
        double chromeBottom = contentTop;

        foreach (Visual visual in this.GetVisualDescendants())
        {
            // Window chrome only: the title bar and the caption buttons. Our own content, and
            // toasts / message boxes / dialogs floating above it, do not count.
            if (visual is not (TitleBar or Button) ||
                !visual.IsEffectivelyVisible ||
                visual.Bounds.Height <= 0 ||
                Root.IsVisualAncestorOf(visual) ||
                IsInsideOverlay(visual))
            {
                continue;
            }

            double bottom = visual.TranslatePoint(new Point(0, visual.Bounds.Height), this)?.Y ?? 0;
            chromeBottom = Math.Max(chromeBottom, bottom);
        }

        const double Gap = 4;
        double overlap = chromeBottom > contentTop ? Math.Ceiling(chromeBottom - contentTop + Gap) : 0;
        if (Math.Abs(Root.Margin.Top - overlap) > 0.5)
        {
            Root.Margin = new Thickness(0, overlap, 0, 0);
        }
    }

    private static bool IsInsideOverlay(Visual visual) =>
        visual.GetSelfAndVisualAncestors().Any(v =>
            v is ToastCard or MessageCard or Ursa.Controls.NotificationCard or MessageBoxControl or DialogControlBase);

    protected override void OnClosed(EventArgs e)
    {
        Shell?.Dispose();
        base.OnClosed(e);
    }
}
