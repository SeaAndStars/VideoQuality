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
using VideoQuality.App.Services;
using VideoQuality.App.ViewModels;
using VideoQuality.Core.Ingest;
using VideoQuality.Core.Scoring;
using VideoQuality.Core.Localization;

namespace VideoQuality.App.Views;

/// <summary>
/// The Compare page: synced viewer, per-frame graph and encode table for one reference and its
/// encodes (formerly the whole main window).
/// </summary>
public partial class CompareView : UserControl
{
    private VerticalLine? _marker;
    private MainViewModel? _viewModel;
    private Point _pressPosition;
    private bool _pressed;
    private bool _panning;
    private bool _draggingWipe;
    private Point _panLast;
    // Share of the viewer + graph space chosen by dragging the splitter (0 = size from the video's
    // aspect ratio). A share, not pixels, so maximise/restore scales it instead of keeping a height
    // that no longer fits and pushes the graph and table off the window.
    private double _userViewerShare;

    // While the splitter is being dragged, auto-sizing must not run: every drag step resizes the
    // viewer, which used to trigger UpdateViewerHeight and put the old height straight back.
    private bool _splitterDragging;
    private Border? _hotZone;
    private TopLevel? _topLevel;

    public CompareView()
    {
        InitializeComponent();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

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

        ViewerSplitter.DragStarted += (_, _) => _splitterDragging = true;
        ViewerSplitter.DragCompleted += (_, _) =>
        {
            _splitterDragging = false;
            RememberSplitterPosition();
        };
        ViewerSplitter.DoubleTapped += (_, _) =>
        {
            _userViewerShare = 0;
            UpdateViewerHeight();
        };

        // Size from the arranged layout, after the window-state change has been laid out; the window
        // SizeChanged event arrives first, with stale row heights.
        Layout.SizeChanged += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateViewerHeight, Avalonia.Threading.DispatcherPriority.Background);

        // Chrome size changes with DPI, maximise/restore and theme; re-measure after each layout
        // pass (cheap, and a no-op unless the overlap actually changed).
        Loaded += (_, _) =>
        {
            UpdateZoomLabel();
            UpdateViewerHeight();
        };

        ViewerHost.SizeChanged += (_, _) => UpdateViewerHeight();
    }

    /// <summary>The page's model; input only reaches the page while it shows one.</summary>
    private MainViewModel ViewModel => _viewModel!;

    /// <summary>Display scale (1.5 at 150% Windows scaling) of the window hosting this page.</summary>
    private double RenderScaling => _topLevel?.RenderScaling ?? 1;

    // Shortcuts (arrows, Space, F, Ctrl+O/V/R) listen on the window, so they work wherever focus is,
    // but only act while this page is the one shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this)!; // attached: inside a window
        _topLevel.AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);
        _topLevel.AddHandler(KeyUpEvent, OnGlobalKeyUp, RoutingStrategies.Tunnel);
        Loc.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>The graph's titles and the zoom readout are drawn in code: draw them again in the new language.</summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RedrawPlot();
        UpdateZoomLabel();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Loc.LanguageChanged -= OnLanguageChanged;
        _topLevel!.RemoveHandler(KeyDownEvent, OnGlobalKeyDown);
        _topLevel.RemoveHandler(KeyUpEvent, OnGlobalKeyUp);
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
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
            _viewModel.ConfirmAsync = ToastHost.ConfirmAsync;
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
        if (_viewModel is not { } viewModel)
        {
            PlotView.Refresh();
            return;
        }

        List<EncodeEntry> all = [.. viewModel.Encodes.Where(entry => entry.Report is { Frames.Count: > 0 })];

        List<EncodeEntry> entries = all.Where(entry => entry.IsVisible).ToList();

        if (entries.Count == 0)
        {
            plot.Axes.SetLimits(0, 100, 0, 100);
            GraphSubtitle.Text = all.Count == 0 ? string.Empty : Loc.T("compare.graph.allHidden");
            StyleAxes(plot, Loc.T("compare.graph.frameAxis"), "VMAF");
            PlotView.Refresh();
            return;
        }

        bool delta = viewModel.ShowDelta;
        EncodeEntry baseline = entries[0];
        bool smooth = viewModel.ShowSmoothed;

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
            bool selected = ReferenceEquals(entry, viewModel.SelectedEncode);
            line.LineWidth = selected ? 2.2f : 1.1f;
            line.MarkerSize = 0;

            maxFrame = Math.Max(maxFrame, xs[^1]);
            low = Math.Min(low, ys.Min());
            high = Math.Max(high, ys.Max());
        }

        if (viewModel.SelectedEncode?.Report is { } report)
        {
            _marker = plot.Add.VerticalLine(viewModel.CurrentFrame, 1.2f, Colors.White);

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

        if (!delta && viewModel.FixedScale)
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

        string backend = viewModel.SelectedEncode?.Report?.Backend.ToDisplayName() ?? string.Empty;
        string label = viewModel.SelectedEncode?.Report is { } selectedReport
            ? VmafModels.DisplayName(selectedReport.Model)
            : "VMAF";
        string mixed = viewModel.HasMixedModels ? "  ·  " + Loc.T("compare.graph.mixedModels") : string.Empty;

        string title = delta
            ? Loc.T("compare.graph.deltaTitle", label, baseline.Name, entries.Count) + mixed
            : Loc.T("compare.graph.title", label, entries.Count) +
              (backend.Length > 0 ? $"  ·  {backend}" : string.Empty) + mixed;

        // The card header carries the title, in the app's own typography; the plot has none.
        GraphSubtitle.Text = title;
        StyleAxes(plot, Loc.T("compare.graph.frameAxis"), delta ? "Δ VMAF" : "VMAF");
        PlotView.Refresh();
    }

    /// <summary>Axis labels and ticks at the app's text sizes, regular weight, muted colour.</summary>
    private static void StyleAxes(Plot plot, string x, string y)
    {
        var muted = Color.FromHex("#A0A1A7");
        foreach (var (axis, text) in new (ScottPlot.IAxis Axis, string Text)[] { (plot.Axes.Bottom, x), (plot.Axes.Left, y) })
        {
            axis.Label.Text = text;
            // ScottPlot's default face has no CJK glyphs; pick an installed one that covers the label.
            axis.Label.FontName = Fonts.Detect(text);
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
        IReadOnlyList<Core.Models.VmafFrame> frames = baseline.Report!.Frames; // plotted entries have frames
        int index = Math.Clamp(frameNumber, 0, frames.Count - 1);
        return frames[index].Vmaf;
    }

    private static double[] MovingAverage(double[] values, int window)
    {
        if (values.Length < window)
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

    // Row buttons carry their encode as DataContext.
    private static EncodeEntry RowOf(object? sender) => (EncodeEntry)((Control)sender!).DataContext!;

    private void OnToggleEncodeVisibility(object? sender, RoutedEventArgs e)
    {
        EncodeEntry entry = RowOf(sender);
        entry.IsVisible = !entry.IsVisible;
        RedrawPlot();
        e.Handled = true;
    }

    private void UpdateMarker()
    {
        if (_marker is null)
        {
            return;
        }

        _marker.X = ViewModel.CurrentFrame;
        PlotView.Refresh();
    }

    private void OnPlotPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressed = true;
        _pressPosition = e.GetPosition(PlotView);
    }

    private void OnPlotPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pressed)
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

        ViewModel.GoToFrame((int)Math.Round(coordinates.X));
    }

    // ---------------------------------------------------------------- viewer input

    private void OnViewWheel(object? sender, PointerWheelEventArgs e)
    {
        var view = (Control)sender!;
        if (ViewModel.ImageWidth <= 0)
        {
            return;
        }

        double current = EffectiveScale(view);
        Point position = e.GetPosition(view);

        ViewModel.Viewport.ZoomAt(
            position.X,
            position.Y,
            view.Bounds.Width,
            view.Bounds.Height,
            ViewModel.ImageWidth,
            ViewModel.ImageHeight,
            current,
            e.Delta.Y > 0 ? 1.25 : 0.8);

        UpdateZoomLabel();
        e.Handled = true;
    }

    private void OnViewPressed(object? sender, PointerPressedEventArgs e)
    {
        var view = (Control)sender!;
        PointerPointProperties properties = e.GetCurrentPoint(view).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        Point position = e.GetPosition(view);

        if (view is WipeView wipe)
        {
            double splitX = ViewModel.WipeSplit * wipe.Bounds.Width;
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
        var view = (Control)sender!;
        if (_draggingWipe && view is WipeView wipe)
        {
            Point position = e.GetPosition(wipe);
            ViewModel.WipeSplit = Math.Clamp(position.X / Math.Max(1, wipe.Bounds.Width), 0.02, 0.98);
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

        ViewModel.Viewport.Pan(delta.X, delta.Y, ViewModel.ImageWidth, ViewModel.ImageHeight, EffectiveScale(view));
        UpdateZoomLabel();
        e.Handled = true;
    }

    private void OnViewReleased(object? sender, PointerReleasedEventArgs e)
    {
        _panning = false;
        _draggingWipe = false;
        e.Pointer.Capture(null);
    }

    // The viewer panes are frame views, except the wipe.
    private static double EffectiveScale(Control view) =>
        view is WipeView wipeView ? wipeView.GetEffectiveScale() : ((FrameView)view).GetEffectiveScale();

    private Control ActiveView() => ViewModel.IsTwoPane ? ReferenceView : ViewModel.IsWipe ? Wipe : FlickerView;

    /// <summary>
    /// Sizes the viewer row: from the video's aspect ratio by default, or as the share the user chose
    /// with the splitter. Either way the graph row keeps its minimum height and every other row its
    /// actual height, so no window size or state can push the graph and table out of view.
    /// </summary>
    private void UpdateViewerHeight()
    {
        if (_viewModel is null || _splitterDragging)
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
        RowDefinition graphRow = Layout.RowDefinitions[4];
        double flexible = FlexibleHeight();
        if (flexible > 0)
        {
            double height;
            if (_userViewerShare > 0)
            {
                height = flexible * _userViewerShare;
            }
            else
            {
                double paneWidth = _viewModel.IsTwoPane ? (width - 12) / 2 : width;
                height = Math.Min(paneWidth / aspect + 40, 640);
            }

            double maxViewer = Math.Max(viewerRow.MinHeight, flexible - graphRow.MinHeight);
            height = Math.Clamp(height, viewerRow.MinHeight, maxViewer);

            if (Math.Abs(viewerRow.Height.Value - height) > 1)
            {
                viewerRow.Height = new GridLength(height);
            }

            if (!graphRow.Height.IsStar)
            {
                graphRow.Height = new GridLength(1, GridUnitType.Star);
            }
        }

        // A single-pane view gets the image's own width so wipe/flicker do not letterbox sideways.
        double contentHeight = Math.Max(120, viewerRow.Height.Value - 40);
        double singleWidth = Math.Min(width, contentHeight * aspect);
        if (double.IsNaN(SinglePaneHost.Width) || Math.Abs(SinglePaneHost.Width - singleWidth) > 1)
        {
            SinglePaneHost.Width = singleWidth;
        }

        UpdateZoomLabel();
    }

    /// <summary>Height shared by the viewer and graph rows: the layout minus every other row.</summary>
    private double FlexibleHeight()
    {
        double others = 0;
        for (int row = 0; row < Layout.RowDefinitions.Count; row++)
        {
            if (row is not (2 or 4))
            {
                others += Layout.RowDefinitions[row].ActualHeight;
            }
        }

        return Layout.Bounds.Height - others;
    }

    /// <summary>
    /// GridSplitter leaves both rows with fixed pixel heights; keep the user's choice as a share and
    /// give the graph row back its star sizing so it absorbs later window resizes.
    /// </summary>
    private void RememberSplitterPosition()
    {
        double flexible = FlexibleHeight();
        if (flexible > 0)
        {
            _userViewerShare = Math.Clamp(Layout.RowDefinitions[2].ActualHeight / flexible, 0.1, 0.95);
        }

        UpdateViewerHeight();
    }

    private void UpdateZoomLabel()
    {
        if (_viewModel is null)
        {
            return;
        }

        // Physical pixels per video pixel: 100% is pixel-exact whatever the Windows display scaling.
        double scale = EffectiveScale(ActiveView()) * RenderScaling;
        string suffix = _viewModel.Viewport.IsFit ? "  (" + Loc.T("compare.fit") + ")" : string.Empty;
        ZoomLabel.Text = $"{scale * 100:0}%{suffix}";
    }

    private void OnFit(object? sender, RoutedEventArgs e)
    {
        ViewModel.Viewport.Fit();
        UpdateZoomLabel();
    }

    private void OnActualSize(object? sender, RoutedEventArgs e)
    {
        ViewModel.Viewport.ActualSize(RenderScaling);
        UpdateZoomLabel();
    }

    // ---------------------------------------------------------------- keyboard

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null || !IsEffectivelyVisible || _topLevel!.FocusManager!.GetFocusedElement() is TextBox)
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
            _viewModel?.SetFlicker(false); // the page may have no model while another page shows
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- files

    private async void OnPickReference(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> files = await PageIo.PickFilesAsync(this, Loc.T("compare.picker.reference"), false, VideoTypes);
        if (files.Count > 0)
        {
            await ViewModel.HandleDropAsync([.. files], DropZone.Reference);
        }
    }

    private async void OnPickEncodes(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> files = await PageIo.PickFilesAsync(this, Loc.T("compare.picker.encodes"), true, VideoTypes);
        if (files.Count > 0)
        {
            await ViewModel.HandleDropAsync([.. files], DropZone.Encodes);
        }
    }

    /// <summary>Ctrl+V: files on the clipboard, or one path per line of text (file:// URIs too).</summary>
    private async Task PasteAsync()
    {
        IClipboard clipboard = _topLevel!.Clipboard!;

        IStorageItem[]? files = await clipboard.TryGetFilesAsync();
        if (files is { Length: > 0 })
        {
            await ViewModel.HandleDropAsync([.. files.Select(f => f.TryGetLocalPath())], DropZone.Auto);
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

        await ViewModel.HandleDropAsync(paths, DropZone.Auto);
    }

    private void OnStepBack(object? sender, RoutedEventArgs e) => ViewModel.StepFrame(-1);

    private void OnStepForward(object? sender, RoutedEventArgs e) => ViewModel.StepFrame(1);

    private void OnRemoveEncode(object? sender, RoutedEventArgs e)
    {
        ViewModel.RemoveEncode(RowOf(sender));
        e.Handled = true;
    }

    private void OnRescoreEncode(object? sender, RoutedEventArgs e)
    {
        _ = ViewModel.RescoreAsync(RowOf(sender));
        e.Handled = true;
    }

    private async void OnRevealEncode(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await PageIo.RevealAsync(this, RowOf(sender).Path);
    }

    private static (string Name, string[] Patterns) VideoTypes =>
        (Loc.T("picker.type.video"), [.. DropRouter.VideoExtensions.Select(extension => "*" + extension)]);

    // ---------------------------------------------------------------- drag and drop

    private static bool HasFiles(DragEventArgs e) => e.DataTransfer.Contains(DataFormat.File);

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (HasFiles(e))
        {
            ViewModel.IsDragging = true;
        }
    }

    // DragLeave also fires when the pointer crosses into a child control; only hide the overlay
    // when the pointer has really left the window, otherwise it flickers.
    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        Point position = e.GetPosition(this);
        if (position.X <= 0 || position.Y <= 0 || position.X >= Bounds.Width || position.Y >= Bounds.Height)
        {
            ViewModel.IsDragging = false;
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
        ViewModel.IsDragging = false;
        if (!HasFiles(e))
        {
            return;
        }

        // Non-local items (smb://, mtp://) come through as null and are rejected with a reason.
        List<string?> paths = [.. e.DataTransfer.TryGetFiles()!.Select(item => item.TryGetLocalPath())]; // HasFiles

        // Every zone is tagged with the drop zone it stands for.
        DropZone zone = zoneBorder is null ? DropZone.Auto : (DropZone)zoneBorder.Tag!;
        await ViewModel.HandleDropAsync(paths, zone);
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

    // ---------------------------------------------------------------- feedback

    private void OnToastRequested(object? sender, ToastRequest request) => ToastHost.Show(request);
}
