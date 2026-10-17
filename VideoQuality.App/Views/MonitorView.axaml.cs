using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using VideoQuality.App.ViewModels;

namespace VideoQuality.App.Views;

/// <summary>Samples once a second, only while the page is on screen.</summary>
public partial class MonitorView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _sampling;

    public MonitorView()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await TickAsync();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                UpdateTimer();
            }
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    /// <summary>One sample now (also what the timer does); skipped while the previous one runs.</summary>
    public async Task TickAsync()
    {
        if (_sampling || DataContext is not MonitorViewModel vm)
        {
            return;
        }

        _sampling = true;
        try
        {
            await vm.SampleAsync();
        }
        finally
        {
            _sampling = false;
        }
    }

    private void UpdateTimer()
    {
        if (IsVisible && VisualRoot is not null)
        {
            _timer.Start();
            _ = TickAsync();
        }
        else
        {
            _timer.Stop();
        }
    }
}
