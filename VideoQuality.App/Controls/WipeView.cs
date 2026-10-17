using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VideoQuality.App.Controls;

/// <summary>
/// Draws two images with a shared viewport, the left one clipped to <see cref="Split"/> and the
/// right one after it: the classic wipe comparison.
/// </summary>
public class WipeView : Control
{
    public static readonly StyledProperty<IImage?> SourceAProperty =
        AvaloniaProperty.Register<WipeView, IImage?>(nameof(SourceA));

    public static readonly StyledProperty<IImage?> SourceBProperty =
        AvaloniaProperty.Register<WipeView, IImage?>(nameof(SourceB));

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<WipeView, double>(nameof(Zoom));

    public static readonly StyledProperty<double> CenterXProperty =
        AvaloniaProperty.Register<WipeView, double>(nameof(CenterX), 0.5);

    public static readonly StyledProperty<double> CenterYProperty =
        AvaloniaProperty.Register<WipeView, double>(nameof(CenterY), 0.5);

    /// <summary>Divider position as a fraction of the control width (0..1).</summary>
    public static readonly StyledProperty<double> SplitProperty =
        AvaloniaProperty.Register<WipeView, double>(nameof(Split), 0.5);

    static WipeView()
    {
        AffectsRender<WipeView>(
            SourceAProperty,
            SourceBProperty,
            ZoomProperty,
            CenterXProperty,
            CenterYProperty,
            SplitProperty);
    }

    public IImage? SourceA
    {
        get => GetValue(SourceAProperty);
        set => SetValue(SourceAProperty, value);
    }

    public IImage? SourceB
    {
        get => GetValue(SourceBProperty);
        set => SetValue(SourceBProperty, value);
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

    public double Split
    {
        get => GetValue(SplitProperty);
        set => SetValue(SplitProperty, value);
    }

    public double GetEffectiveScale()
    {
        if (SourceA is not { } source)
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

        if (SourceA is not { } sourceA || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        Size image = sourceA.Size;
        double scale = GetEffectiveScale();
        double width = image.Width * scale;
        double height = image.Height * scale;
        double x = Bounds.Width / 2 - CenterX * width;
        double y = Bounds.Height / 2 - CenterY * height;
        var destination = new Rect(x, y, width, height);
        var sourceRect = new Rect(0, 0, image.Width, image.Height);

        double splitX = Math.Clamp(Split, 0, 1) * Bounds.Width;

        using (context.PushClip(new Rect(0, 0, Bounds.Width, Bounds.Height)))
        {
            using (context.PushClip(new Rect(0, 0, splitX, Bounds.Height)))
            {
                context.DrawImage(sourceA, sourceRect, destination);
            }

            if (SourceB is { } sourceB)
            {
                using (context.PushClip(new Rect(splitX, 0, Math.Max(0, Bounds.Width - splitX), Bounds.Height)))
                {
                    context.DrawImage(sourceB, new Rect(0, 0, sourceB.Size.Width, sourceB.Size.Height), destination);
                }
            }

            var divider = new Pen(new SolidColorBrush(Color.Parse("#f0f3f7")), 2);
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#80000000")), 5), new Point(splitX - 1, 0), new Point(splitX - 1, Bounds.Height));
            context.DrawLine(divider, new Point(splitX, 0), new Point(splitX, Bounds.Height));

            // grab handle: a pill with left/right chevrons, so the divider is obviously draggable
            double centreY = Bounds.Height / 2;
            var handle = new RoundedRect(new Rect(splitX - 10, centreY - 26, 20, 52), 10);
            context.DrawRectangle(new SolidColorBrush(Color.Parse("#f0f3f7")), null, handle);

            var arrow = new Pen(new SolidColorBrush(Color.Parse("#20242a")), 1.8);
            context.DrawLine(arrow, new Point(splitX - 3.5, centreY - 5), new Point(splitX - 7, centreY));
            context.DrawLine(arrow, new Point(splitX - 7, centreY), new Point(splitX - 3.5, centreY + 5));
            context.DrawLine(arrow, new Point(splitX + 3.5, centreY - 5), new Point(splitX + 7, centreY));
            context.DrawLine(arrow, new Point(splitX + 7, centreY), new Point(splitX + 3.5, centreY + 5));
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Size result = base.ArrangeOverride(finalSize);
        InvalidateVisual();
        return result;
    }
}
