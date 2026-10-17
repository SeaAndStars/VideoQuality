using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ScottPlot;
using ScottPlot.Plottables;
using VideoQuality.App.Controls;
using VideoQuality.App.ViewModels;
using VideoQuality.Core.Scoring;

namespace VideoQuality.App.Views;

public partial class MainWindow : Window
{
    private VerticalLine? _marker;
    private MainViewModel? _viewModel;
    private Point _pressPosition;
    private bool _pressed;
    private bool _panning;
    private bool _draggingWipe;
    private Point _panLast;

    public MainWindow()
    {
        InitializeComponent();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnGlobalKeyUp, RoutingStrategies.Tunnel);

        PlotView.PointerPressed += OnPlotPointerPressed;
        PlotView.PointerReleased += OnPlotPointerReleased;

        foreach (Control view in new Control[] { ReferenceView, DistortedView, DifferenceView, FlickerView, Wipe })
        {
            view.PointerWheelChanged += OnViewWheel;
            view.PointerPressed += OnViewPressed;
            view.PointerMoved += OnViewMoved;
            view.PointerReleased += OnViewReleased;
            view.SizeChanged += (_, _) => UpdateZoomLabel();
        }

        Loaded += (_, _) =>
        {
            UpdateZoomLabel();
            UpdateViewerHeight();
        };

        SizeChanged += (_, _) => UpdateViewerHeight();
        ViewerHost.SizeChanged += (_, _) => UpdateViewerHeight();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Viewport.PropertyChanged -= OnViewportPropertyChanged;
        }

        _viewModel = DataContext as MainViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.Viewport.PropertyChanged += OnViewportPropertyChanged;
        }

        RedrawPlot();
        UpdateZoomLabel();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.Report):
            case nameof(MainViewModel.Encodes):
            case nameof(MainViewModel.SelectedEncode):
            case nameof(MainViewModel.FixedScale):
                RedrawPlot();
                break;

            case nameof(MainViewModel.CurrentFrame):
                UpdateMarker();
                break;

            case nameof(MainViewModel.CompareMode):
                UpdateZoomLabel();
                UpdateViewerHeight();
                break;

            case nameof(MainViewModel.ImageWidth):
            case nameof(MainViewModel.ImageHeight):
                UpdateViewerHeight();
                break;

            case nameof(MainViewModel.ReferenceBitmap):
            case nameof(MainViewModel.DistortedBitmap):
            case nameof(MainViewModel.DifferenceBitmap):
                UpdateZoomLabel();
                break;
        }
    }

    private void OnViewportPropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateZoomLabel();

    // ---------------------------------------------------------------- graph

    private void RedrawPlot()
    {
        Plot plot = PlotView.Plot;
        plot.Clear();
        plot.SetStyle(new PlotStyle
        {
            FigureBackgroundColor = Color.FromHex("#0b0d10"),
            DataBackgroundColor = Color.FromHex("#0b0d10"),
            AxisColor = Color.FromHex("#9aa0a6"),
            GridMajorLineColor = Color.FromHex("#23262b"),
        });

        _marker = null;

        List<EncodeEntry> entries = _viewModel?.Encodes
            .Where(entry => entry.Report is { Frames.Count: > 0 })
            .ToList() ?? [];

        if (entries.Count == 0)
        {
            plot.Axes.SetLimits(0, 100, 0, 100);
            plot.Title("VMAF per frame");
            plot.XLabel("frame");
            plot.YLabel("VMAF");
            PlotView.Refresh();
            return;
        }

        double maxFrame = 1;
        double low = 100;
        double high = 0;

        foreach (EncodeEntry entry in entries)
        {
            IReadOnlyList<Core.Models.VmafFrame> frames = entry.Report!.Frames;
            double[] xs = frames.Select(f => (double)f.FrameNum).ToArray();
            double[] ys = frames.Select(f => f.Vmaf).ToArray();

            Scatter line = plot.Add.ScatterLine(xs, ys, entry.PlotColor);
            bool selected = ReferenceEquals(entry, _viewModel?.SelectedEncode);
            line.LineWidth = selected ? 2.2f : 1.1f;
            line.MarkerSize = 0;

            maxFrame = Math.Max(maxFrame, xs[^1]);
            low = Math.Min(low, ys.Min());
            high = Math.Max(high, ys.Max());
        }

        if (_viewModel?.SelectedEncode?.Report is { } report)
        {
            _marker = plot.Add.VerticalLine(_viewModel.CurrentFrame, 1.2f, Colors.White);
            plot.Add.HorizontalLine(report.Stats.Mean, 1, Colors.Gray, LinePattern.Dashed);
            plot.Add.HorizontalLine(85, 1, Color.FromHex("#e06c75"), LinePattern.Solid);
        }

        if (_viewModel?.FixedScale != false)
        {
            plot.Axes.SetLimits(0, maxFrame, 0, 100);
        }
        else
        {
            double bottom = Math.Max(0, Math.Floor(low - 2));
            double top = Math.Min(100, Math.Ceiling(high + 2));
            if (top - bottom < 10)
            {
                bottom = Math.Max(0, bottom - 5);
                top = Math.Min(100, top + 5);
            }

            plot.Axes.SetLimits(0, maxFrame, bottom, top);
        }

        string backend = _viewModel?.SelectedEncode?.Report?.Backend.ToDisplayName() ?? string.Empty;
        plot.Title($"VMAF per frame — {entries.Count} encode(s)" + (backend.Length > 0 ? $" — {backend}" : string.Empty));
        plot.XLabel("frame");
        plot.YLabel("VMAF");
        PlotView.Refresh();
    }

    private void UpdateMarker()
    {
        if (_marker is null || _viewModel is null)
        {
            return;
        }

        _marker.X = _viewModel.CurrentFrame;
        PlotView.Refresh();
    }

    private void OnPlotPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressed = true;
        _pressPosition = e.GetPosition(PlotView);
    }

    private void OnPlotPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pressed || _viewModel is null)
        {
            return;
        }

        _pressed = false;
        Point position = e.GetPosition(PlotView);

        // A click picks a frame; a drag is ScottPlot's pan/zoom.
        if (Math.Abs(position.X - _pressPosition.X) > 4 || Math.Abs(position.Y - _pressPosition.Y) > 4)
        {
            return;
        }

        Coordinates coordinates = PlotView.Plot.GetCoordinates(
            (float)position.X,
            (float)position.Y,
            PlotView.Plot.Axes.Bottom,
            PlotView.Plot.Axes.Left);

        _viewModel.GoToFrame((int)Math.Round(coordinates.X));
    }

    // ---------------------------------------------------------------- viewer input

    private void OnViewWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_viewModel is null || sender is not Control view || _viewModel.ImageWidth <= 0)
        {
            return;
        }

        double current = EffectiveScale(view);
        Point position = e.GetPosition(view);

        _viewModel.Viewport.ZoomAt(
            position.X,
            position.Y,
            view.Bounds.Width,
            view.Bounds.Height,
            _viewModel.ImageWidth,
            _viewModel.ImageHeight,
            current,
            e.Delta.Y > 0 ? 1.25 : 0.8);

        UpdateZoomLabel();
        e.Handled = true;
    }

    private void OnViewPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is null || sender is not Control view)
        {
            return;
        }

        PointerPointProperties properties = e.GetCurrentPoint(view).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        Point position = e.GetPosition(view);

        if (view is WipeView wipe)
        {
            double splitX = _viewModel.WipeSplit * wipe.Bounds.Width;
            if (Math.Abs(position.X - splitX) <= 20)
            {
                _draggingWipe = true;
                e.Pointer.Capture(wipe);
                e.Handled = true;
                return;
            }
        }

        _panning = true;
        _panLast = position;
        e.Pointer.Capture(view);
        e.Handled = true;
    }

    private void OnViewMoved(object? sender, PointerEventArgs e)
    {
        if (_viewModel is null || sender is not Control view)
        {
            return;
        }

        if (_draggingWipe && view is WipeView wipe)
        {
            Point position = e.GetPosition(wipe);
            _viewModel.WipeSplit = Math.Clamp(position.X / Math.Max(1, wipe.Bounds.Width), 0.02, 0.98);
            e.Handled = true;
            return;
        }

        if (!_panning)
        {
            return;
        }

        Point current = e.GetPosition(view);
        Vector delta = current - _panLast;
        _panLast = current;

        _viewModel.Viewport.Pan(delta.X, delta.Y, _viewModel.ImageWidth, _viewModel.ImageHeight, EffectiveScale(view));
        UpdateZoomLabel();
        e.Handled = true;
    }

    private void OnViewReleased(object? sender, PointerReleasedEventArgs e)
    {
        _panning = false;
        _draggingWipe = false;
        e.Pointer.Capture(null);
    }

    private double EffectiveScale(Control view) => view switch
    {
        FrameView frameView => frameView.GetEffectiveScale(),
        WipeView wipeView => wipeView.GetEffectiveScale(),
        _ => 1,
    };

    private Control? ActiveView() => _viewModel switch
    {
        null => null,
        { IsTwoPane: true } => ReferenceView,
        { IsWipe: true } => Wipe,
        _ => FlickerView,
    };

    /// <summary>Pins the viewer height to the image aspect so fit mode has no letterbox dead space.</summary>
    private void UpdateViewerHeight()
    {
        if (_viewModel is null)
        {
            return;
        }

        double width = ViewerHost.Bounds.Width;
        if (width <= 2)
        {
            return;
        }

        double aspect = _viewModel.ImageWidth > 0 && _viewModel.ImageHeight > 0
            ? _viewModel.ImageWidth / _viewModel.ImageHeight
            : 16.0 / 9.0;

        double paneWidth = _viewModel.IsTwoPane ? (width - 8) / 2 : width;
        double height = Math.Clamp(paneWidth / aspect + 30, 200, 640);

        if (double.IsNaN(ViewerHost.Height) || Math.Abs(ViewerHost.Height - height) > 1)
        {
            ViewerHost.Height = height;
        }

        // A single-pane view gets the image's own width so wipe/flicker do not letterbox sideways.
        double contentHeight = Math.Max(120, ViewerHost.Height - 30);
        double singleWidth = contentHeight * aspect;
        if (double.IsNaN(SinglePaneHost.Width) || Math.Abs(SinglePaneHost.Width - singleWidth) > 1)
        {
            SinglePaneHost.Width = singleWidth;
        }

        UpdateZoomLabel();
    }

    private void UpdateZoomLabel()
    {
        if (_viewModel is null)
        {
            return;
        }

        Control? view = ActiveView();
        double scale = view is null ? 1 : EffectiveScale(view);
        string suffix = _viewModel.Viewport.IsFit ? "  (fit)" : string.Empty;
        ZoomLabel.Text = $"{scale * 100:0}%{suffix}";
    }

    private void OnFit(object? sender, RoutedEventArgs e)
    {
        _viewModel?.Viewport.Fit();
        UpdateZoomLabel();
    }

    private void OnActualSize(object? sender, RoutedEventArgs e)
    {
        _viewModel?.Viewport.ActualSize();
        UpdateZoomLabel();
    }

    // ---------------------------------------------------------------- keyboard

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null || FocusManager?.GetFocusedElement() is TextBox)
        {
            return;
        }

        int step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;

        switch (e.Key)
        {
            case Key.Left:
                _viewModel.StepFrame(-step);
                e.Handled = true;
                break;

            case Key.Right:
                _viewModel.StepFrame(step);
                e.Handled = true;
                break;

            case Key.Home:
                _viewModel.GoToFrame(0);
                e.Handled = true;
                break;

            case Key.End:
                _viewModel.GoToFrame(int.MaxValue);
                e.Handled = true;
                break;

            case Key.Space:
                _viewModel.TogglePlayPause();
                e.Handled = true;
                break;

            case Key.F:
                _viewModel.SetFlicker(true);
                e.Handled = true;
                break;
        }
    }

    private void OnGlobalKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F)
        {
            _viewModel?.SetFlicker(false);
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- files

    private async void OnPickReference(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(VideoPicker("Choose the reference video", false));
        string? path = files.Count > 0 ? files[0].TryGetLocalPath() : null;

        if (path is not null && _viewModel is not null)
        {
            await _viewModel.SetReferenceAsync(path);
        }
    }

    private async void OnPickEncodes(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(VideoPicker("Add encodes to compare", true));
        string[] paths = files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray();

        if (paths.Length > 0 && _viewModel is not null)
        {
            await _viewModel.AddEncodesAsync(paths);
        }
    }

    private void OnRemoveEncode(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: EncodeEntry entry })
        {
            _viewModel?.RemoveEncode(entry);
        }

        e.Handled = true;
    }

    private static FilePickerOpenOptions VideoPicker(string title, bool allowMultiple) => new()
    {
        Title = title,
        AllowMultiple = allowMultiple,
        FileTypeFilter =
        [
            new FilePickerFileType("Video")
            {
                Patterns = ["*.mp4", "*.mkv", "*.mov", "*.webm", "*.y4m", "*.avi", "*.ts"],
                MimeTypes = ["video/*"],
            },
        ],
    };

    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (_viewModel is null || !e.DataTransfer.Contains(DataFormat.File))
        {
            return;
        }

        string[] paths = (e.DataTransfer.TryGetFiles() ?? [])
            .Select(item => item.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray();

        if (paths.Length == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_viewModel.ReferencePath))
        {
            await _viewModel.SetReferenceAsync(paths[0]);
            if (paths.Length > 1)
            {
                await _viewModel.AddEncodesAsync(paths[1..]);
            }
        }
        else
        {
            await _viewModel.AddEncodesAsync(paths);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Viewport.PropertyChanged -= OnViewportPropertyChanged;
            _viewModel.Dispose();
        }

        base.OnClosed(e);
    }
}
