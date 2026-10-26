using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using SatoriInsight.Models;

namespace SatoriInsight.Controls;

public sealed record RegionLegendItem(string Label, IBrush Brush, IBrush TextBrush, string Description);

public static class RegionPalette
{
    private static readonly Color OccupancyColor = Color.Parse("#9ADFA4");
    private static readonly Color FragmentationColor = Color.Parse("#E6B56E");
    private static readonly Color UnavailableColor = GenerationPalette.GetColor(HeapGeneration.Unknown);
    private static readonly Color NoQueueColor = GenerationPalette.GetColor(HeapGeneration.Unassigned);
    private static readonly IBrush UnavailableBrush = new ImmutableSolidColorBrush(UnavailableColor);
    private static readonly IBrush NoQueueBrush = new ImmutableSolidColorBrush(NoQueueColor);

    private static readonly IReadOnlyDictionary<RegionQueueKind, Color> QueueColors = new Dictionary<RegionQueueKind, Color>
    {
        [RegionQueueKind.Allocator] = Color.Parse("#FF9B42"),
        [RegionQueueKind.WorkChunk] = Color.Parse("#E8EEF5"),
        [RegionQueueKind.RecyclerEphemeral] = Color.Parse("#3EA6FF"),
        [RegionQueueKind.RecyclerEphemeralFinalizationTracking] = Color.Parse("#F568D4"),
        [RegionQueueKind.RecyclerTenured] = Color.Parse("#EEE05A"),
        [RegionQueueKind.RecyclerTenuredFinalizationTracking] = Color.Parse("#FF6678"),
        [RegionQueueKind.RecyclerNursery] = Color.Parse("#35D694"),
        [RegionQueueKind.RecyclerFinalizationPending] = Color.Parse("#AD82FF"),
        [RegionQueueKind.RecyclerFinalizationScanComplete] = Color.Parse("#30D6DC"),
        [RegionQueueKind.RecyclerStaying] = Color.Parse("#889828"),
        [RegionQueueKind.RecyclerRelocating] = Color.Parse("#78F828"),
        [RegionQueueKind.RecyclerRelocated] = Color.Parse("#F8A8C8"),
        [RegionQueueKind.RecyclerRelocatedToHigherGen] = Color.Parse("#D828F8"),
        [RegionQueueKind.RecyclerRelocationTarget] = Color.Parse("#C87838"),
        [RegionQueueKind.RecyclerDeferredSweep] = Color.Parse("#98F8C0"),
        [RegionQueueKind.RecyclerReusable] = Color.Parse("#B870A8"),
        [RegionQueueKind.RecyclerDemoted] = Color.Parse("#C0B828")
    };

    private static readonly RegionLegendItem UnavailableLegend = CreateLegend("Unavailable", UnavailableColor);

    private static readonly IReadOnlyList<RegionLegendItem> GenerationLegend = Enum.GetValues<HeapGeneration>()
        .Select(generation => CreateLegend(generation.ToString(), GenerationPalette.GetColor(generation),
            generation == HeapGeneration.Unassigned ? "Not assigned to a managed generation." : generation.ToString()))
        .ToArray();

    private static readonly IBrush OccupancyGradient = CreateGradient(OccupancyColor);
    private static readonly IBrush FragmentationGradient = CreateGradient(FragmentationColor);
    private static readonly IReadOnlyList<RegionLegendItem> ScaleLegend = [UnavailableLegend];

    private static readonly IReadOnlyList<RegionLegendItem> QueueLegend =
    [
        .. Enum.GetValues<RegionQueueKind>().Select(queue => CreateLegend(queue.ToString(), QueueColors[queue])),
        CreateLegend("None", NoQueueColor, description: "Not in a queue."),
        UnavailableLegend
    ];

    public static IBrush PinnedBrush { get; } = new ImmutableSolidColorBrush(Colors.White);

    public static IReadOnlyList<RegionLegendItem> GetLegend(RegionColorMode mode) => mode switch
    {
        RegionColorMode.Occupancy or RegionColorMode.Fragmentation => ScaleLegend,
        RegionColorMode.Queue => QueueLegend,
        _ => GenerationLegend
    };

    public static IBrush? GetGradient(RegionColorMode mode) => mode switch
    {
        RegionColorMode.Occupancy => OccupancyGradient,
        RegionColorMode.Fragmentation => FragmentationGradient,
        _ => null
    };

    public static IBrush GetBrush(HeapRegion region, RegionColorMode mode)
    {
        switch (mode)
        {
            case RegionColorMode.Occupancy:
                return (region.Header?.HasReportedOccupancy ?? region.DetailsKnown)
                    ? CreateBrush(OccupancyColor, region.Occupancy)
                    : UnavailableBrush;

            case RegionColorMode.Fragmentation:
                return region.DetailsKnown ? CreateBrush(FragmentationColor, region.Fragmentation) : UnavailableBrush;

            case RegionColorMode.Queue:
                if (region.Header is not { } header)
                {
                    return UnavailableBrush;
                }

                if (header.Queue is not { } queue)
                {
                    return NoQueueBrush;
                }

                return QueueColors.TryGetValue(queue, out var color) ? new ImmutableSolidColorBrush(color) : UnavailableBrush;

            default:
                return region.DisplayGeneration == HeapGeneration.Unknown
                    ? UnavailableBrush
                    : new ImmutableSolidColorBrush(GenerationPalette.GetColor(region.DisplayGeneration));
        }
    }

    private static IBrush CreateBrush(Color color, double intensity) =>
        new ImmutableSolidColorBrush(color, GetOpacity(intensity));

    private static double GetOpacity(double intensity) => 0.18 + Math.Clamp(intensity, 0, 1) * 0.76;

    private static RegionLegendItem CreateLegend(string label, Color color, string? description = null) =>
        new(label, new ImmutableSolidColorBrush(color), new ImmutableSolidColorBrush(color == NoQueueColor ? UnavailableColor : color), description ?? label);

    private static IBrush CreateGradient(Color color)
    {
        var low = Color.FromArgb((byte)Math.Round(GetOpacity(0) * 255), color.R, color.G, color.B);
        var high = Color.FromArgb((byte)Math.Round(GetOpacity(1) * 255), color.R, color.G, color.B);
        return new ImmutableLinearGradientBrush([new ImmutableGradientStop(0, low), new ImmutableGradientStop(1, high)],
            startPoint: RelativePoint.TopLeft, endPoint: new RelativePoint(1, 0, RelativeUnit.Relative));
    }
}
