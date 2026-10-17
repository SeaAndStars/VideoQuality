using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Notifications;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using ScottPlot;
using Ursa.Controls;
using ScottPlot.Plottables;
using VideoQuality.App.Controls;
using VideoQuality.App.ViewModels;
using VideoQuality.Core.Ingest;
using VideoQuality.Core.Scoring;

namespace VideoQuality.App.Views;

public partial class MainWindow : UrsaWindow
{
    private VerticalLine? _marker;
    private MainViewModel? _viewModel;
    private Point _pressPosition;
    private bool _pressed;
    private bool _panning;
    private bool _draggingWipe;
    private Point _panLast;
    private bool _userSizedViewer;
    private Border? _hotZone;
    private WindowToastManager? _toasts;

    public MainWindow()
    {
        InitializeComponent();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
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

        ViewerSplitter.DragCompleted += (_, _) => _userSizedViewer = true;
        ViewerSplitter.DoubleTapped += (_, _) =>
        {
            _userSizedViewer = false;
            UpdateViewerHeight();
        };

        // Chrome size changes with DPI, maximise/restore and theme; re-measure after each layout
        // pass (cheap, and a no-op unless the overlap actually changed).
        LayoutUpdated += (_, _) => FitBelowTitleBar();

        Loaded += (_, _) =>
        {
            FitBelowTitleBar();
            _toasts = new WindowToastManager(this) { MaxItems = 4 };
            UpdateZoomLabel();
            UpdateViewerHeight();
        };

        SizeChanged += (_, _) =>
        {
            FitBelowTitleBar();
            UpdateViewerHeight();
        };
        ViewerHost.SizeChanged += (_, _) => UpdateViewerHeight();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Viewport.PropertyChanged -= OnViewportPropertyChanged;
            _viewModel.ToastRequested -= OnToastRequested;
            _viewModel.ConfirmAsync = null;
        }

        _viewModel = DataContext as MainViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.Viewport.PropertyChanged += OnViewportPropertyChanged;
            _viewModel.ToastRequested += OnToastRequested;
            _viewModel.ConfirmAsync = ConfirmAsync;
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
            case nameof(MainViewModel.ShowSmoothed):
            case nameof(MainViewModel.ShowDelta):
            case nameof(MainViewModel.HasMixedModels):
                RedrawPlot();
                break;

            case nameof(MainViewModel.CurrentFrame):
                UpdateMarker();
                break;

            case nameof(MainViewModel.CompareMode):
            case nameof(MainViewModel.IsEmpty):
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
            // Matches the surface layer (SemiColorBackground1) so the plot sits flush in its card.
            FigureBackgroundColor = Color.FromHex("#232429"),
            DataBackgroundColor = Color.FromHex("#232429"),
            AxisColor = Color.FromHex("#A0A1A7"),
            GridMajorLineColor = Color.FromHex("#2F3036"),
        });

        _marker = null;

        List<EncodeEntry> all = _viewModel?.Encodes
            .Where(entry => entry.Report is { Frames.Count: > 0 })
            .ToList() ?? [];

        List<EncodeEntry> entries = all.Where(entry => entry.IsVisible).ToList();

        if (entries.Count == 0)
        {
            plot.Axes.SetLimits(0, 100, 0, 100);
            GraphSubtitle.Text = all.Count == 0 ? string.Empty : "all curves hidden";
            StyleAxes(plot, "frame", "VMAF");
            PlotView.Refresh();
            return;
        }

        bool delta = _viewModel?.ShowDelta == true && entries[0].Report is not null;
        EncodeEntry baseline = entries[0];
        bool smooth = _viewModel?.ShowSmoothed == true;

        double maxFrame = 1;
        double low = double.MaxValue;
        double high = double.MinValue;

        foreach (EncodeEntry entry in entries)
        {
            IReadOnlyList<Core.Models.VmafFrame> frames = entry.Report!.Frames;
            double[] xs = frames.Select(f => (double)f.FrameNum).ToArray();
            double[] ys = frames
                .Select(f => delta ? f.Vmaf - BaselineValue(baseline, f.FrameNum) : f.Vmaf)
                .ToArray();

            if (smooth)
            {
                ys = MovingAverage(ys, 5);
            }

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

            if (delta)
            {
                plot.Add.HorizontalLine(0, 1, Colors.Gray, LinePattern.Dashed);
            }
            else
            {
                plot.Add.HorizontalLine(report.Stats.Mean, 1, Colors.Gray, LinePattern.Dashed);
                plot.Add.HorizontalLine(85, 1, Color.FromHex("#D55E00"), LinePattern.Dotted);
            }
        }

        if (!delta && _viewModel?.FixedScale != false)
        {
            plot.Axes.SetLimits(0, maxFrame, 0, 100);
        }
        else
        {
            double padding = Math.Max(1.0, (high - low) * 0.08);
            double bottom = Math.Floor(low - padding);
            double top = Math.Ceiling(high + padding);

            if (delta)
            {
                bottom = Math.Min(bottom, -1);
                top = Math.Max(top, 1);
            }
            else
            {
                bottom = Math.Max(0, bottom);
                top = Math.Min(100, top);
            }

            plot.Axes.SetLimits(0, maxFrame, bottom, top);
        }

        string backend = _viewModel?.SelectedEncode?.Report?.Backend.ToDisplayName() ?? string.Empty;
        string label = _viewModel?.SelectedEncode?.Report is { } selectedReport
            ? VmafModels.DisplayName(selectedReport.Model)
            : "VMAF";
        string mixed = _viewModel?.HasMixedModels == true ? "  ·  MIXED MODELS" : string.Empty;

        string title = delta
            ? $"Δ {label} vs {baseline.Name}  ·  {entries.Count} encode(s){mixed}"
            : $"{label}  ·  {entries.Count} encode(s)" +
              (backend.Length > 0 ? $"  ·  {backend}" : string.Empty) + mixed;

        // The card header carries the title, in the app's own typography; the plot has none.
        GraphSubtitle.Text = title;
        StyleAxes(plot, "frame", delta ? "Δ VMAF" : "VMAF");
        PlotView.Refresh();
    }

    /// <summary>Axis labels and ticks at the app's text sizes, regular weight, muted colour.</summary>
    private static void StyleAxes(Plot plot, string x, string y)
    {
        var muted = Color.FromHex("#A0A1A7");
        foreach (var (axis, text) in new (ScottPlot.IAxis Axis, string Text)[] { (plot.Axes.Bottom, x), (plot.Axes.Left, y) })
        {
            axis.Label.Text = text;
            axis.Label.FontSize = 12;
            axis.Label.Bold = false;
            axis.Label.ForeColor = muted;
            axis.TickLabelStyle.FontSize = 11;
            axis.TickLabelStyle.ForeColor = muted;
        }
    }

    /// <summary>Baseline value at a frame number; frames are dense and start at zero.</summary>
    private static double BaselineValue(EncodeEntry baseline, int frameNumber)
    {
        IReadOnlyList<Core.Models.VmafFrame> frames = baseline.Report!.Frames;
        if (frames.Count == 0)
        {
            return 0;
        }

        int index = Math.Clamp(frameNumber, 0, frames.Count - 1);
        return frames[index].Vmaf;
    }

    private static double[] MovingAverage(double[] values, int window)
    {
        if (values.Length < window || window <= 1)
        {
            return values;
        }

        var result = new double[values.Length];
        int half = window / 2;

        for (int i = 0; i < values.Length; i++)
        {
            int start = Math.Max(0, i - half);
            int end = Math.Min(values.Length - 1, i + half);
            double sum = 0;

            for (int j = start; j <= end; j++)
            {
                sum += values[j];
            }

            result[i] = sum / (end - start + 1);
        }

        return result;
    }

    private void OnToggleEncodeVisibility(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: EncodeEntry entry })
        {
            entry.IsVisible = !entry.IsVisible;
            RedrawPlot();
        }

        e.Handled = true;
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

    /// <summary>
    /// Sizes the viewer row to the image aspect so fit mode has no letterbox dead space, until the
    /// user drags the splitter (double-click it to return to automatic sizing). The graph row has a
    /// minimum height, so wipe mode can no longer crush the plot.
    /// </summary>
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

        RowDefinition viewerRow = Layout.RowDefinitions[2];
        if (!_userSizedViewer)
        {
            double paneWidth = _viewModel.IsTwoPane ? (width - 12) / 2 : width;
            double height = Math.Clamp(paneWidth / aspect + 40, 200, 640);
            double windowHeight = Bounds.Height > 0 ? Bounds.Height : 900;
            height = Math.Min(height, windowHeight * 0.55);

            if (Math.Abs(viewerRow.Height.Value - height) > 1)
            {
                viewerRow.Height = new GridLength(height);
            }
        }

        // A single-pane view gets the image's own width so wipe/flicker do not letterbox sideways.
        double contentHeight = Math.Max(120, ViewerHost.Bounds.Height - 40);
        double singleWidth = Math.Min(width, contentHeight * aspect);
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

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            switch (e.Key)
            {
                case Key.O when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                    OnPickEncodes(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case Key.O:
                    OnPickReference(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case Key.V:
                    _ = PasteAsync();
                    e.Handled = true;
                    return;
                case Key.R:
                    _viewModel.AnalyzeCommand.Execute(null);
                    e.Handled = true;
                    return;
            }
        }

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
        if (files.Count > 0 && _viewModel is not null)
        {
            await _viewModel.HandleDropAsync([.. files.Select(file => file.TryGetLocalPath())], DropZone.Reference);
        }
    }

    private async void OnPickEncodes(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(VideoPicker("Add encodes to compare", true));
        if (files.Count > 0 && _viewModel is not null)
        {
            await _viewModel.HandleDropAsync([.. files.Select(file => file.TryGetLocalPath())], DropZone.Encodes);
        }
    }

    /// <summary>Ctrl+V: files on the clipboard, or one path per line of text (file:// URIs too).</summary>
    private async Task PasteAsync()
    {
        if (_viewModel is null || Clipboard is not { } clipboard)
        {
            return;
        }

        IStorageItem[]? files = await clipboard.TryGetFilesAsync();
        if (files is { Length: > 0 })
        {
            await _viewModel.HandleDropAsync([.. files.Select(f => f.TryGetLocalPath())], DropZone.Auto);
            return;
        }

        string? text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        List<string?> paths = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Trim('"', '\''))
            .Select(line => Uri.TryCreate(line, UriKind.Absolute, out Uri? uri) && !uri.IsFile && uri.Scheme != "file"
                ? null // smb://, mtp://, http://: not local
                : uri is { IsFile: true } ? uri.LocalPath : line)
            .ToList();

        await _viewModel.HandleDropAsync(paths, DropZone.Auto);
    }

    private void OnStepBack(object? sender, RoutedEventArgs e) => _viewModel?.StepFrame(-1);

    private void OnStepForward(object? sender, RoutedEventArgs e) => _viewModel?.StepFrame(1);

    private void OnRemoveEncode(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: EncodeEntry entry })
        {
            _viewModel?.RemoveEncode(entry);
        }

        e.Handled = true;
    }

    private void OnRescoreEncode(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: EncodeEntry entry } && _viewModel is not null)
        {
            _ = _viewModel.RescoreAsync(entry);
        }

        e.Handled = true;
    }

    private async void OnRevealEncode(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: EncodeEntry entry } &&
            Path.GetDirectoryName(entry.Path) is { } folder)
        {
            await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
        }
    }

    private static FilePickerOpenOptions VideoPicker(string title, bool allowMultiple) => new()
    {
        Title = title,
        AllowMultiple = allowMultiple,
        FileTypeFilter =
        [
            new FilePickerFileType("Video")
            {
                Patterns = [.. DropRouter.VideoExtensions.Select(extension => "*" + extension)],
                MimeTypes = ["video/*"],
            },
        ],
    };

    // ---------------------------------------------------------------- drag and drop

    private static bool HasFiles(DragEventArgs e) => e.DataTransfer.Contains(DataFormat.File);

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (_viewModel is not null && HasFiles(e))
        {
            _viewModel.IsDragging = true;
        }
    }

    // DragLeave also fires when the pointer crosses into a child control; only hide the overlay
    // when the pointer has really left the window, otherwise it flickers.
    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        Point position = e.GetPosition(this);
        if (_viewModel is not null &&
            (position.X <= 0 || position.Y <= 0 || position.X >= Bounds.Width || position.Y >= Bounds.Height))
        {
            _viewModel.IsDragging = false;
            SetHotZone(null);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        SetHotZone(ZoneAt(e.Source));
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        Border? zoneBorder = ZoneAt(e.Source);
        SetHotZone(null);

        if (_viewModel is null)
        {
            return;
        }

        _viewModel.IsDragging = false;
        if (!HasFiles(e))
        {
            return;
        }

        // Non-local items (smb://, mtp://) come through as null and are rejected with a reason.
        List<string?> paths = (e.DataTransfer.TryGetFiles() ?? [])
            .Select(item => item.TryGetLocalPath())
            .ToList();

        DropZone zone = zoneBorder?.Tag is DropZone tagged ? tagged : DropZone.Auto;
        await _viewModel.HandleDropAsync(paths, zone);
    }

    private static Border? ZoneAt(object? source)
    {
        for (Visual? visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Border border && border.Classes.Contains("zone"))
            {
                return border;
            }
        }

        return null;
    }

    private void SetHotZone(Border? zone)
    {
        if (ReferenceEquals(zone, _hotZone))
        {
            return;
        }

        _hotZone?.Classes.Remove("hot");
        _hotZone = zone;
        _hotZone?.Classes.Add("hot");
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

    // ---------------------------------------------------------------- feedback

    private void OnToastRequested(object? sender, ToastRequest request)
    {
        if (_toasts is null)
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
                Theme = (Avalonia.Styling.ControlTheme?)this.FindResource("BorderlessButton"),
                Padding = new Thickness(8, 2),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };

            action.Click += (_, _) =>
            {
                action.IsEnabled = false;
                action.Content = "Done";
                request.Action();
            };

            content = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = request.Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 380, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                    action,
                },
            };
        }

        TimeSpan expiration = request.Action is not null ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(4);
        _toasts.Show(content, type, expiration, showIcon: true, showClose: true, onClick: null, onClose: null, classes: []);
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        MessageBoxResult result = await MessageBox.ShowAsync(
            this, message, title, MessageBoxIcon.Question, MessageBoxButton.YesNo);

        // Closing the box without choosing keeps the default (yes).
        return result != MessageBoxResult.No;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Viewport.PropertyChanged -= OnViewportPropertyChanged;
            _viewModel.ToastRequested -= OnToastRequested;
            _viewModel.FlushSettings();
            _viewModel.Dispose();
        }

        base.OnClosed(e);
    }
}
