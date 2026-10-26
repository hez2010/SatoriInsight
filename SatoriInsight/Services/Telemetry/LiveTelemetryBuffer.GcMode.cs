using System.Runtime;
using SatoriInsight.Models;

namespace SatoriInsight.Services.Telemetry;

public sealed partial class LiveTelemetryBuffer
{
    private readonly List<GcModeSample> _gcModes = [];

    private GcEvent WithSampledLatencyMode(GcEvent gc)
    {
        if (gc.LatencyMode.HasValue)
        {
            return gc;
        }

        for (var index = _gcModes.Count - 1; index >= 0; index--)
        {
            var sample = _gcModes[index];

            if (sample.Seconds <= gc.EndSeconds && sample.Collector == GcCollectorKind.Satori && sample.LatencyMode.HasValue)
            {
                return gc with { LatencyMode = sample.LatencyMode, LatencyModeIsSampled = true };
            }
        }

        return gc;
    }

    public void GcMode(double at, GcCollectorKind? collector = null, GCLatencyMode? latencyMode = null)
    {
        if (!double.IsFinite(at) || at < 0 || collector is null && latencyMode is null)
        {
            return;
        }

        lock (_gate)
        {
            Observe(at);

            // A late DAC result must not override positive collector identification.
            if (collector is not null && collector != GcCollectorKind.Satori && _gcModes.Any(sample => sample.Collector == GcCollectorKind.Satori))
            {
                collector = null;
            }

            var index = _gcModes.Count;

            while (index > 0 && _gcModes[index - 1].Seconds > at)
            {
                index--;
            }

            var previousCollector = _gcModes.Take(index).LastOrDefault(sample => sample.Collector.HasValue)?.Collector;
            var previousLatency = _gcModes.Take(index).LastOrDefault(sample => sample.LatencyMode.HasValue)?.LatencyMode;

            if ((collector is null || collector == previousCollector) && (latencyMode is null || latencyMode == previousLatency))
            {
                return;
            }

            // EventPipe delivery can lag behind the one-second memory sampler.
            _gcModes.Insert(index, new(at, collector, latencyMode));
        }
    }
}
