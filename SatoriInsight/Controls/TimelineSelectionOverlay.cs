using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SatoriInsight.Controls;

public sealed class TimelineSelectionOverlay : Control
{
    public static readonly StyledProperty<double?> StartProperty = AvaloniaProperty.Register<TimelineSelectionOverlay, double?>(nameof(Start));
    public static readonly StyledProperty<double?> EndProperty = AvaloniaProperty.Register<TimelineSelectionOverlay, double?>(nameof(End));
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<TimelineSelectionOverlay, double>(nameof(Maximum), 1);

    public double? Start
    {
        get => GetValue(StartProperty);
        set => SetValue(StartProperty, value);
    }

    public double? End
    {
        get => GetValue(EndProperty);
        set => SetValue(EndProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    static TimelineSelectionOverlay() => AffectsRender<TimelineSelectionOverlay>(StartProperty, EndProperty, MaximumProperty);

    public override void Render(DrawingContext context)
    {
        if (Start is not { } start || End is not { } end || Maximum <= 0)
        {
            return;
        }

        var x1 = Math.Clamp(start / Maximum, 0, 1) * Bounds.Width;
        var x2 = Math.Clamp(end / Maximum, 0, 1) * Bounds.Width;
        using var clip = context.PushClip(new Rect(Bounds.Size));

        if (x2 > x1)
        {
            context.FillRectangle(new SolidColorBrush(Color.Parse("#B7EE81"), 0.15), new Rect(x1, 0, x2 - x1, Bounds.Height));
        }

        var pen = new Pen(Brush.Parse("#B7EE81"), 1.5);
        context.DrawLine(pen, new(x1, 0), new(x1, Bounds.Height));

        if (x2 > x1)
        {
            context.DrawLine(pen, new(x2, 0), new(x2, Bounds.Height));
        }
    }
}
