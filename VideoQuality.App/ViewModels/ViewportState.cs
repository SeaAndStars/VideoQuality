using CommunityToolkit.Mvvm.ComponentModel;

namespace VideoQuality.App.ViewModels;

/// <summary>Shared viewer state, so both panes (and the wipe view) always show the same region.</summary>
public partial class ViewportState : ObservableObject
{
    /// <summary>
    /// Device-independent units per image pixel; 0 means fit-to-pane. On a scaled display one DIP is
    /// <c>RenderScaling</c> physical pixels, so pixel-exact 1:1 is <c>1 / RenderScaling</c>.
    /// </summary>
    [ObservableProperty]
    public partial double Zoom { get; set; }

    /// <summary>Normalised image coordinate pinned to the centre of the viewport.</summary>
    [ObservableProperty]
    public partial double CenterX { get; set; } = 0.5;

    [ObservableProperty]
    public partial double CenterY { get; set; } = 0.5;

    public bool IsFit => Zoom <= 0;

    public void Fit()
    {
        Zoom = 0;
        CenterX = 0.5;
        CenterY = 0.5;
        OnPropertyChanged(nameof(IsFit));
    }

    /// <summary>
    /// One video pixel per physical screen pixel. <paramref name="renderScaling"/> is the display
    /// scale (1.5 at 150% in Windows); ignoring it showed "1:1" frames magnified by that factor.
    /// </summary>
    public void ActualSize(double renderScaling = 1)
    {
        Zoom = 1 / Math.Max(renderScaling, 0.01);
        CenterX = 0.5;
        CenterY = 0.5;
        OnPropertyChanged(nameof(IsFit));
    }

    /// <summary>
    /// Zoom keeping the image point under the cursor fixed. <paramref name="currentScale"/> is the
    /// scale currently in effect (fit resolved by the control).
    /// </summary>
    public void ZoomAt(
        double cursorX,
        double cursorY,
        double viewportWidth,
        double viewportHeight,
        double imageWidth,
        double imageHeight,
        double currentScale,
        double factor)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || viewportWidth <= 0 || viewportHeight <= 0)
        {
            return;
        }

        double newScale = Math.Clamp(currentScale * factor, 0.02, 16.0);

        double imageX = ((cursorX - viewportWidth / 2) / currentScale + CenterX * imageWidth) / imageWidth;
        double imageY = ((cursorY - viewportHeight / 2) / currentScale + CenterY * imageHeight) / imageHeight;

        CenterX = Math.Clamp(imageX - (cursorX - viewportWidth / 2) / (newScale * imageWidth), 0, 1);
        CenterY = Math.Clamp(imageY - (cursorY - viewportHeight / 2) / (newScale * imageHeight), 0, 1);
        Zoom = newScale;
        OnPropertyChanged(nameof(IsFit));
    }

    /// <summary>Pan by a screen-space delta.</summary>
    public void Pan(double deltaX, double deltaY, double imageWidth, double imageHeight, double scale)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || scale <= 0)
        {
            return;
        }

        CenterX = Math.Clamp(CenterX - deltaX / (scale * imageWidth), 0, 1);
        CenterY = Math.Clamp(CenterY - deltaY / (scale * imageHeight), 0, 1);
    }

    partial void OnZoomChanged(double value) => OnPropertyChanged(nameof(IsFit));
}
