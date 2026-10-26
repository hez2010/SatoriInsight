using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SatoriInsight.Models;

namespace SatoriInsight.Controls;

/// <summary>One drawing surface for region tiles, with hit testing and keyboard navigation.</summary>
public sealed class RegionMap : Control
{
    public static readonly StyledProperty<IReadOnlyList<HeapRegion>?> RegionsProperty = AvaloniaProperty.Register<RegionMap, IReadOnlyList<HeapRegion>?>(nameof(Regions));
    public static readonly StyledProperty<HeapRegion?> SelectedRegionProperty = AvaloniaProperty.Register<RegionMap, HeapRegion?>(nameof(SelectedRegion), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<RegionColorMode> ColorModeProperty = AvaloniaProperty.Register<RegionMap, RegionColorMode>(nameof(ColorMode));
    public static readonly StyledProperty<double> CellSizeProperty = AvaloniaProperty.Register<RegionMap, double>(nameof(CellSize), 20);

    public IReadOnlyList<HeapRegion>? Regions
    {
        get => GetValue(RegionsProperty);
        set => SetValue(RegionsProperty, value);
    }

    public HeapRegion? SelectedRegion
    {
        get => GetValue(SelectedRegionProperty);
        set => SetValue(SelectedRegionProperty, value);
    }

    public RegionColorMode ColorMode
    {
        get => GetValue(ColorModeProperty);
        set => SetValue(ColorModeProperty, value);
    }

    public double CellSize
    {
        get => GetValue(CellSizeProperty);
        set => SetValue(CellSizeProperty, value);
    }

    private readonly List<(Rect Rect, HeapRegion Region)> _tiles = [];
    private int _columns = 16;
    private int _hoverId = -1;
    private static readonly IBrush PinnedBackground = Brush.Parse("#0C111A");

    private double Pitch => Math.Clamp(double.IsFinite(CellSize) ? CellSize : 20, 4, 36) + 2;

    private int Columns(double width) => Math.Max(1, (int)((width - 4) / Pitch));

    private IEnumerable<IGrouping<(int RuntimeId, int HeapIndex, ulong PageAddress), HeapRegion>> Groups =>
        (Regions ?? [])
            .GroupBy(region => (region.RuntimeId, region.HeapIndex, PageAddress: region.Header?.PageAddress ?? 0))
            .OrderBy(group => group.Key.RuntimeId)
            .ThenBy(group => group.Key.HeapIndex)
            .ThenBy(group => group.Key.PageAddress);

    static RegionMap()
    {
        AffectsRender<RegionMap>(RegionsProperty, SelectedRegionProperty, ColorModeProperty, CellSizeProperty);
        AffectsMeasure<RegionMap>(RegionsProperty, CellSizeProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 700;
        return new(width, Math.Max(100, Groups.Sum(g => Math.Ceiling(g.Count() / (double)Columns(width)) * Pitch + 44)));
    }

    public RegionMap()
    {
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        AutomationProperties.SetName(this, "Heap region map. Use arrow keys to select a region; details are shown in the region inspector.");
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ControlAutomationPeer(this);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        _tiles.Clear();
        context.FillRectangle(Brushes.Transparent, Bounds.WithX(0).WithY(0));
        var groups = Groups.ToArray();

        if (groups.Length == 0)
        {
            Text(context, "No region data available.", 4, 20, "#8795A9");
            return;
        }

        _columns = Columns(Bounds.Width);
        var laneTop = 4d;

        foreach (var group in groups)
        {
            var regions = group.OrderBy(r => r.StartAddress).ToArray();
            var rowCount = (int)Math.Ceiling(regions.Length / (double)_columns);
            var heap = group.Key.PageAddress != 0 ? $"Page 0x{group.Key.PageAddress:X}" : group.Key.HeapIndex < 0
                ? $"CLR {group.Key.RuntimeId} · heap index unknown"
                : $"Heap {group.Key.HeapIndex}";
            var size = group.Key.HeapIndex < 0 && group.Key.PageAddress == 0
                ? $"{Format.Bytes(regions.Sum(r => r.ReservedBytes))} total"
                : $"{Format.Bytes(regions.Sum(r => r.CommittedBytes))} committed";
            Text(context, $"{heap} · 0x{regions[0].StartAddress:X} · {regions.Length:N0} regions · {size}", 4, laneTop, "#98A6B9", 11);
            laneTop += 25;

            for (var i = 0; i < regions.Length; i++)
            {
                var region = regions[i];
                var rect = new Rect(4 + (i % _columns) * Pitch, laneTop + (i / _columns) * Pitch, Pitch - 2, Pitch - 2);
                _tiles.Add((rect, region));
                context.FillRectangle(RegionPalette.GetBrush(region, ColorMode), rect);

                if (region.PinnedObjects > 0 || region.Header?.HasPinnedObjects == true)
                {
                    var radius = Math.Clamp(rect.Width * 0.225, 1.5, 4.5);
                    var inset = radius + Math.Min(1, (rect.Width - radius * 2) / 2);
                    var center = new Point(rect.Right - inset, rect.Top + inset);
                    var innerRadius = radius - Math.Min(1.5, radius * 0.4);
                    context.DrawEllipse(PinnedBackground, null, center, radius, radius);
                    context.DrawEllipse(RegionPalette.PinnedBrush, null, center, innerRadius, innerRadius);
                }

                if (SelectedRegion?.Id == region.Id)
                {
                    context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#58D8FF")), 2), rect.Inflate(1));
                }
                else if (_hoverId == region.Id)
                {
                    context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#AFBDCF")), 1), rect.Inflate(1));
                }
            }

            laneTop += rowCount * Pitch + 19;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var tile = _tiles.FirstOrDefault(t => t.Rect.Contains(e.GetPosition(this)));

        if (tile.Region is null)
        {
            return;
        }

        SetCurrentValue(SelectedRegionProperty, tile.Region);
        Focus();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var tile = _tiles.FirstOrDefault(t => t.Rect.Contains(e.GetPosition(this)));
        var id = tile.Region?.Id ?? -1;

        if (_hoverId == id)
        {
            return;
        }

        _hoverId = id;
        ToolTip.SetTip(this, DescribeRegion(tile.Region));
        InvalidateVisual();
    }

    private static string? DescribeRegion(HeapRegion? region)
    {
        if (region is null)
        {
            return null;
        }

        var description = $"{region.Label} · {region.GenerationLabel}\n{region.AddressLabel} – 0x{region.EndAddress:X}\n";

        if (region.Header is { } header)
        {
            return description +
                $"Queue: {header.Queue?.ToString() ?? "None"} · reusable for: {header.ReusableFor}\n" +
                $"{region.SizeLabel} committed · {header.OccupancyLabel} reported occupancy\n" +
                "Object liveness and fragmentation unavailable";
        }

        if (region.UsedRangeBytes is { } used)
        {
            return description + $"{Format.Bytes(used)} used range · {Format.Bytes(region.ReservedBytes)} reserved\n"
                + "Occupancy and fragmentation unavailable";
        }

        if (region.DetailsKnown)
        {
            return description +
                $"{region.Occupancy:P0} occupied · {region.Fragmentation:P1} free-hole fragmentation\n" +
                $"{region.SizeLabel} committed";
        }

        return description + "Occupancy and fragmentation unavailable";
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverId = -1;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var list = _tiles.Select(t => t.Region).ToArray();

        if (list.Length == 0)
        {
            return;
        }

        var index = list.ToList().FindIndex(r => r.Id == SelectedRegion?.Id);
        var delta = e.Key switch
        {
            Key.Left => -1,
            Key.Right => 1,
            Key.Up => -_columns,
            Key.Down => _columns,
            _ => 0
        };

        if (e.Key == Key.Home)
        {
            index = 0;
        }
        else if (e.Key == Key.End)
        {
            index = list.Length - 1;
        }
        else if (delta != 0)
        {
            index = Math.Clamp(index + delta, 0, list.Length - 1);
        }
        else
        {
            return;
        }

        SetCurrentValue(SelectedRegionProperty, list[index]);
        this.BringIntoView(_tiles[index].Rect);
        e.Handled = true;
    }

    private static void Text(
        DrawingContext context, string text, double x, double y, string color, double size = 11) => context.DrawText(new FormattedText(
        text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Inter"), size, new SolidColorBrush(Color.Parse(color))), new Point(x, y));
}
