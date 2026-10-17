using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VideoQuality.App.Controls;

/// <summary>A small line chart of the last values (0–100), newest on the right.</summary>
public sealed class Sparkline : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke), Brushes.DodgerBlue);

    /// <summary>How many values span the full width.</summary>
    public static readonly StyledProperty<int> CapacityProperty =
        AvaloniaProperty.Register<Sparkline, int>(nameof(Capacity), 120);

    static Sparkline() => AffectsRender<Sparkline>(ValuesProperty, StrokeProperty, CapacityProperty);

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public int Capacity
    {
        get => GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        Rect bounds = Bounds;
        context.DrawRectangle(new SolidColorBrush(Colors.Gray, 0.08), null, new Rect(bounds.Size), 4, 4);
        if (Values is not { Count: > 1 } values)
        {
            return;
        }

        double step = bounds.Width / Math.Max(1, Capacity - 1);
        double left = bounds.Width - ((values.Count - 1) * step);
        var geometry = new StreamGeometry();
        using (StreamGeometryContext g = geometry.Open())
        {
            for (int i = 0; i < values.Count; i++)
            {
                var point = new Point(left + (i * step), bounds.Height * (1 - (Math.Clamp(values[i], 0, 100) / 100)));
                if (i == 0)
                {
                    g.BeginFigure(point, false);
                }
                else
                {
                    g.LineTo(point);
                }
            }
        }

        context.DrawGeometry(null, new Pen(Stroke, 1.5), geometry);
    }
}
