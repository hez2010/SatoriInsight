using System.Globalization;

namespace SatoriInsight.Services.Telemetry;

public sealed record RuntimeCounterSample(string Name, double Value, double IntervalSeconds = 0);

public static class RuntimeCounterParser
{
    private static readonly HashSet<string> Names =
    [
        "gc-heap-size", "gc-committed", "alloc-rate", "gen-0-size", "gen-1-size", "gen-2-size", "loh-size", "poh-size", "gc-fragmentation",
        "total-pause-time-by-gc"
    ];

    public static RuntimeCounterSample? Parse(object? payload)
    {
        if (payload is not IDictionary<string, object> fields)
        {
            return null;
        }

        if (fields.TryGetValue("Payload", out var nested) && nested is IDictionary<string, object> inner)
        {
            fields = inner;
        }

        if (!fields.TryGetValue("Name", out var nameValue) || nameValue is not string name || !Names.Contains(name))
        {
            return null;
        }

        double value;
        var intervalSeconds = 0d;

        if (name is "alloc-rate" or "total-pause-time-by-gc")
        {
            if (!Number(fields, "Increment", out var increment) || !Number(fields, "IntervalSec", out var interval) || interval <= 0)
            {
                return null;
            }

            intervalSeconds = interval;
            value = name == "alloc-rate" ? increment / interval : increment;
        }
        else if (!Number(fields, "Mean", out value))
        {
            return null;
        }

        var units = fields.TryGetValue("DisplayUnits", out var unitValue) ? unitValue?.ToString() : null;
        // RuntimeEventSource uses decimal MB for heap/committed, B for generation sizes and allocation.
        var scale = units switch
        {
            "MB" => 1_000_000d,
            "MiB" => 1_048_576d,
            "KB" => 1000d,
            "KiB" => 1024d,
            "B" or "bytes" or "%" => 1d,
            _ => name is "gc-heap-size" or "gc-committed" ? 1_000_000d : 1d
        };
        value *= scale;
        return double.IsFinite(value) && value >= 0 && (name == "gc-fragmentation" ? value <= 100 : value <= long.MaxValue)
            ? new(name, value, intervalSeconds)
            : null;
    }

    private static bool Number(IDictionary<string, object> fields, string key, out double number)
    {
        number = 0;

        if (!fields.TryGetValue(key, out var value) || value is null)
        {
            return false;
        }

        try
        {
            number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return double.IsFinite(number);
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }
    }
}
