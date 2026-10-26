using SatoriInsight.Models;

namespace SatoriInsight.Services.Telemetry;

public static class ProcessStatistics
{
    public static ProcessMetricPoint? AtOrBefore(IReadOnlyList<ProcessMetricPoint> history, double seconds)
    {
        var index = FindSampleIndex(history, seconds);
        return index >= 0 ? history[index] : null;
    }

    public static double? CpuTimeInRange(IReadOnlyList<ProcessMetricPoint> history, double start, double end)
    {
        if (end <= start)
        {
            return null;
        }

        var before = InterpolateCpu(history, start);
        var after = InterpolateCpu(history, end);

        if (before is not { } first || after is not { } last || last < first)
        {
            return null;
        }

        return last - first;
    }

    public static long? PeakResidentBytes(IReadOnlyList<ProcessMetricPoint> history, double start, double end)
    {
        long? peak = null;

        foreach (var sample in history)
        {
            if (sample.Seconds >= start && sample.Seconds <= end && sample.ResidentBytes is { } bytes)
            {
                peak = peak is { } previous ? Math.Max(previous, bytes) : bytes;
            }
        }

        return peak;
    }

    private static double? InterpolateCpu(IReadOnlyList<ProcessMetricPoint> history, double seconds)
    {
        var index = FindSampleIndex(history, seconds);

        if (index < 0 || history[index].CpuSeconds is not { } before)
        {
            return null;
        }

        var left = history[index];

        if (left.Seconds == seconds)
        {
            return before;
        }

        if (index + 1 >= history.Count || history[index + 1].CpuSeconds is not { } after || after < before)
        {
            return null;
        }

        var right = history[index + 1];
        var fraction = (seconds - left.Seconds) / (right.Seconds - left.Seconds);
        return before + (after - before) * fraction;
    }

    private static int FindSampleIndex(IReadOnlyList<ProcessMetricPoint> history, double seconds)
    {
        var low = 0;
        var high = history.Count - 1;

        while (low <= high)
        {
            var middle = low + (high - low) / 2;

            if (history[middle].Seconds <= seconds)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return high;
    }
}
