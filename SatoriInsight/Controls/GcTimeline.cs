using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SatoriInsight.Models;

namespace SatoriInsight.Controls;

public sealed class GcTimeline : Control
{
    public static readonly StyledProperty<IReadOnlyList<GcEvent>?> EventsProperty = AvaloniaProperty.Register<GcTimeline, IReadOnlyList<GcEvent>?>(nameof(Events));
    public static readonly StyledProperty<GcEvent?> SelectedEventProperty = AvaloniaProperty.Register<GcTimeline, GcEvent?>(nameof(SelectedEvent), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<double> EndSecondsProperty = AvaloniaProperty.Register<GcTimeline, double>(nameof(EndSeconds), 120);
    public static readonly StyledProperty<double> LeftInsetProperty = AvaloniaProperty.Register<GcTimeline, double>(nameof(LeftInset), 49);
    public static readonly StyledProperty<double> RightInsetProperty = AvaloniaProperty.Register<GcTimeline, double>(nameof(RightInset), 0);

    public IReadOnlyList<GcEvent>? Events
    {
        get => GetValue(EventsProperty);
        set => SetValue(EventsProperty, value);
    }

    public GcEvent? SelectedEvent
    {
        get => GetValue(SelectedEventProperty);
        set => SetValue(SelectedEventProperty, value);
    }

    public double EndSeconds
    {
        get => GetValue(EndSecondsProperty);
        set => SetValue(EndSecondsProperty, value);
    }

    public double LeftInset
    {
        get => GetValue(LeftInsetProperty);
        set => SetValue(LeftInsetProperty, value);
    }

    public double RightInset
    {
        get => GetValue(RightInsetProperty);
        set => SetValue(RightInsetProperty, value);
    }

    public event Action<GcEvent>? CollectionInvoked;

    private readonly List<(Rect Rect, GcEvent Event)> _marks = [];

    static GcTimeline() => AffectsRender<GcTimeline>(EventsProperty, SelectedEventProperty, EndSecondsProperty, LeftInsetProperty, RightInsetProperty);

    public GcTimeline()
    {
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        _marks.Clear();
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var width = Math.Max(1, Bounds.Width - LeftInset - RightInset);
        var duration = Math.Max(1, EndSeconds);
        var pixelCount = Math.Max(1, (int)Math.Ceiling(width));
        var drawn = new bool[pixelCount * 3];
        IBrush[] brushes =
        [
            new SolidColorBrush(GenerationPalette.Gen0Color, 0.85), new SolidColorBrush(GenerationPalette.Gen1Color, 0.85),
            new SolidColorBrush(GenerationPalette.Gen2Color, 0.85)
        ];

        for (var generation = 0; generation < 3; generation++)
        {
            var y = 3 + generation * 21;
            context.DrawText(new FormattedText(
                $"Gen{generation}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Inter"), 10,
                new SolidColorBrush(Color.Parse("#8795A9"))), new Point(0, y));
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#283140")), 1), new Point(LeftInset, y + 9), new Point(Bounds.Width - RightInset, y + 9));
        }

        foreach (var gc in Events ?? [])
        {
            var x = LeftInset + gc.EndSeconds / duration * width;

            if (x < LeftInset || x > Bounds.Width - RightInset)
            {
                continue;
            }

            var pixel = gc.Generation * pixelCount + Math.Clamp((int)(x - LeftInset), 0, pixelCount - 1);

            if (drawn[pixel] && SelectedEvent != gc)
            {
                continue;
            }

            drawn[pixel] = true;
            var rect = new Rect(x, 3 + gc.Generation * 21, Math.Min(
                gc.IsThreadLocal ? 3 : Math.Clamp(gc.PauseMilliseconds / 2, 3, 12), Bounds.Width - RightInset - x), 15);
            context.DrawRectangle(brushes[gc.Generation], null, rect, 2, 2);

            if (SelectedEvent == gc)
            {
                context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#E4F7CE")), 1.5), rect.Inflate(2), 3, 3);
            }

            _marks.Add((rect.Inflate(4), gc));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var mark = _marks.FirstOrDefault(m => m.Rect.Contains(e.GetPosition(this)));

        if (mark.Event is null)
        {
            return;
        }

        SetCurrentValue(SelectedEventProperty, mark.Event);
        CollectionInvoked?.Invoke(mark.Event);
        e.Handled = true;
    }
}
