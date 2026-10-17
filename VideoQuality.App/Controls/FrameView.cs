using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VideoQuality.App.Controls;

/// <summary>
/// Draws one image with an explicit zoom/center viewport so two panes can stay locked together.
/// <para>
/// <see cref="Zoom"/> is screen pixels per image pixel; <c>0</c> means "fit to the control".
/// <see cref="CenterX"/>/<see cref="CenterY"/> are normalised image coordinates (0..1) that stay
/// pinned to the middle of the control, which makes the state independent of pane size.
/// </para>
/// </summary>
public class FrameView : Control
{
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<FrameView, IImage?>(nameof(Source));

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<FrameView, double>(nameof(Zoom));

    public static readonly StyledProperty<double> CenterXProperty =
        AvaloniaProperty.Register<FrameView, double>(nameof(CenterX), 0.5);

    public static readonly StyledProperty<double> CenterYProperty =
        AvaloniaProperty.Register<FrameView, double>(nameof(CenterY), 0.5);

    static FrameView()
    {
        AffectsRender<FrameView>(SourceProperty, ZoomProperty, CenterXProperty, CenterYProperty);
    }

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public double CenterX
    {
        get => GetValue(CenterXProperty);
        set => SetValue(CenterXProperty, value);
    }

    public double CenterY
    {
        get => GetValue(CenterYProperty);
        set => SetValue(CenterYProperty, value);
    }

    /// <summary>Current scale in screen pixels per image pixel (resolves "fit" to a number).</summary>
    public double GetEffectiveScale()
    {
        if (Source is not { } source)
        {
            return 1;
        }

        Size image = source.Size;
        if (image.Width <= 0 || image.Height <= 0)
        {
            return 1;
        }

        if (Zoom > 0)
        {
            return Zoom;
        }

        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return 1;
        }

        return Math.Min(Bounds.Width / image.Width, Bounds.Height / image.Height);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (Source is not { } source || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        Size image = source.Size;
        if (image.Width <= 0 || image.Height <= 0)
        {
            return;
        }

        double scale = GetEffectiveScale();
        double renderScaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        Rect destination = ImagePlacement.Destination(Bounds.Size, image, scale, CenterX, CenterY, renderScaling);

        // At 1:1 the image is larger than the pane; without this clip it paints over the header.
        using (context.PushClip(new Rect(0, 0, Bounds.Width, Bounds.Height)))
        using (context.PushRenderOptions(ImagePlacement.Sampling(scale, renderScaling)))
        {
            context.DrawImage(source, new Rect(0, 0, image.Width, image.Height), destination);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Size result = base.ArrangeOverride(finalSize);
        InvalidateVisual();
        return result;
    }
}
