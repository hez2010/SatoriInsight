using SatoriInsight.Models;

namespace SatoriInsight.Services.Telemetry;

/// <summary>Per-collection pause values from the analyzed GCEnd callback; never suspension boundaries.</summary>
public static class GcStatistics
{
    private static readonly (string Label, double Upper)[] Bounds =
    [
        ("<1", 1), ("1–2", 2), ("2–5", 5), ("5–10", 10), ("10–20", 20), ("20–50", 50), ("50+", double.PositiveInfinity)
    ];

    public static IReadOnlyList<HistogramBin> Histogram(IEnumerable<GcEvent> events)
    {
        var counts = new int[Bounds.Length];

        foreach (var pause in Values(events))
        {
            for (var i = 0; i < Bounds.Length; i++)
            {
                if (pause < Bounds[i].Upper)
                {
                    counts[i]++;
                    break;
                }
            }
        }

        return Bounds.Select((bound, i) => new HistogramBin(bound.Label, i == 0 ? 0 : Bounds[i - 1].Upper, bound.Upper, counts[i])).ToArray();
    }

    public static double Percentile(IEnumerable<GcEvent> events, double percentile)
    {
        if (percentile is < 0 or > 100 || !double.IsFinite(percentile))
        {
            throw new ArgumentOutOfRangeException(nameof(percentile));
        }

        var values = Values(events).Order().ToArray();
        return values.Length == 0 ? 0 : values[Math.Max(0, (int)Math.Ceiling((decimal)percentile * values.Length / 100m) - 1)];
    }

    // The runtime counter avoids summing overlapping background/foreground GC totals.
    public static double? CounterPausePercent(IEnumerable<GcPauseCounterPoint> counters)
    {
        var samples = counters.Where(p => double.IsFinite(p.PauseMilliseconds) && p.PauseMilliseconds >= 0 && double.IsFinite(p.IntervalSeconds)
            && p.IntervalSeconds > 0).ToArray();
        var seconds = samples.Sum(p => p.IntervalSeconds);
        return seconds <= 0 ? null : Math.Clamp(samples.Sum(p => p.PauseMilliseconds) / (seconds * 1000) * 100, 0, 100);
    }

    private static IEnumerable<double> Values(IEnumerable<GcEvent> events) => events.Where(gc => !gc.IsThreadLocal).Select(gc => gc.PauseMilliseconds).Where(p => double.IsFinite(p)
        && p >= 0);
}
