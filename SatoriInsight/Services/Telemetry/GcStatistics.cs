using SatoriInsight.Models;

namespace SatoriInsight.Services.Telemetry;

/// <summary>Per-collection pause values from the analyzed GCEnd callback; never suspension boundaries.</summary>
public static class GcStatistics
{
    public static IReadOnlyList<PauseInterval> PauseIntervals { get; } =
    [
        new("<1", 0, 1), new("1–2", 1, 2), new("2–5", 2, 5), new("5–10", 5, 10),
        new("10–20", 10, 20), new("20–50", 20, 50), new("50+", 50, double.PositiveInfinity)
    ];

    public static IReadOnlyList<HistogramBin> Histogram(IEnumerable<GcEvent> events)
    {
        var counts = new int[PauseIntervals.Count];

        foreach (var pause in Values(events))
        {
            for (var i = 0; i < PauseIntervals.Count; i++)
            {
                if (PauseIntervals[i].Contains(pause))
                {
                    counts[i]++;
                    break;
                }
            }
        }

        return PauseIntervals.Select((interval, i) => new HistogramBin(interval.Label, interval.Lower, interval.Upper, counts[i])).ToArray();
    }

    public static double Percentile(IEnumerable<GcEvent> events, double percentile)
    {
        if (percentile is < 0 or > 100 || !double.IsFinite(percentile))
        {
            throw new ArgumentOutOfRangeException(nameof(percentile));
        }

        var values = Values(events).Order().ToArray();

        if (values.Length == 0)
        {
            return 0;
        }

        var rank = Math.Max(1, (int)(percentile * values.Length / 100));
        // Compare cumulative percentages in the same units as the requested percentile.
        if (100d * rank / values.Length < percentile)
        {
            rank++;
        }

        return values[rank - 1];
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
