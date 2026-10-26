using Avalonia.Media;
using SatoriInsight.Models;

namespace SatoriInsight.Controls;

public sealed record RegionLayoutLegendItem(string Label, IBrush Brush);

public static class RegionLayoutPalette
{
    public static IBrush Unknown { get; } = Brush.Parse("#39465B");
    public static IBrush Uncommitted { get; } = Brush.Parse("#202734");
    public static IBrush Committed { get; } = Brush.Parse("#596B84");
    public static IBrush Used { get; } = Brush.Parse("#69B7DF");
    public static IBrush Objects { get; } = Brush.Parse("#73D5B0");
    public static IBrush Free { get; } = Brush.Parse("#E6B56E");

    private static readonly RegionLayoutLegendItem[] HeaderLegend =
    [
        new("Used range", Used),
        new("Remaining committed space", Committed),
        new("Uncommitted", Uncommitted)
    ];

    private static readonly RegionLayoutLegendItem[] UsedRangeLegend =
    [
        new("Used range", Used),
        new("Remaining reserved space", Unknown)
    ];

    private static readonly RegionLayoutLegendItem[] ObjectLegend =
    [
        new("Objects", Objects),
        new("Free space", Free),
        new("Unknown", Unknown)
    ];

    private static readonly RegionLayoutLegendItem[] UnknownLegend = [new("Unknown", Unknown)];

    public static IReadOnlyList<RegionLayoutLegendItem> GetLegend(HeapRegion? region, bool hasObjects) => region switch
    {
        null => [],
        { Header: not null } => HeaderLegend,
        { UsedRangeBytes: not null } => UsedRangeLegend,
        _ => hasObjects ? ObjectLegend : UnknownLegend
    };
}
