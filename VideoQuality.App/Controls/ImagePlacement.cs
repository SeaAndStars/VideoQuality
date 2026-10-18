using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace VideoQuality.App.Controls;

/// <summary>
/// Shared placement and sampling for the frame views, so "100%" means one video pixel on one
/// physical screen pixel at any display scaling, and inspection zoom stays crisp.
/// </summary>
internal static class ImagePlacement
{
    /// <summary>
    /// Screen pixels per image pixel: the explicit <paramref name="zoom"/>, or (0) the scale that fits
    /// the image into <paramref name="bounds"/>; 1 when there is nothing to fit.
    /// </summary>
    public static double EffectiveScale(IImage? source, double zoom, Size bounds)
    {
        if (source is null || source.Size.Width <= 0 || source.Size.Height <= 0)
        {
            return 1;
        }

        if (zoom > 0)
        {
            return zoom;
        }

        return bounds.Width <= 0 || bounds.Height <= 0
            ? 1
            : Math.Min(bounds.Width / source.Size.Width, bounds.Height / source.Size.Height);
    }

    /// <summary>
    /// The destination rectangle for an image drawn at <paramref name="scale"/> (DIPs per image
    /// pixel) with the given normalised centre. The origin is snapped to the physical pixel grid, so
    /// at 100% every video pixel lands exactly on one screen pixel instead of straddling two.
    /// </summary>
    public static Rect Destination(Size bounds, Size image, double scale, double centerX, double centerY, double renderScaling)
    {
        double width = image.Width * scale;
        double height = image.Height * scale;
        double x = bounds.Width / 2 - centerX * width;
        double y = bounds.Height / 2 - centerY * height;

        if (renderScaling > 0)
        {
            x = Math.Round(x * renderScaling) / renderScaling;
            y = Math.Round(y * renderScaling) / renderScaling;
        }

        return new Rect(x, y, width, height);
    }

    /// <summary>
    /// Nearest-neighbour at 100% and above (each video pixel stays a sharp block, which is what
    /// inspection needs), high-quality filtering when the frame is shrunk to fit.
    /// </summary>
    public static RenderOptions Sampling(double scale, double renderScaling) => new()
    {
        BitmapInterpolationMode = scale * Math.Max(renderScaling, 1e-6) >= 0.999
            ? BitmapInterpolationMode.None
            : BitmapInterpolationMode.HighQuality,
    };
}
