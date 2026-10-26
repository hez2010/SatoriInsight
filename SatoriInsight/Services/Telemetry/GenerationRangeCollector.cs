using SatoriInsight.Models;

namespace SatoriInsight.Services.Telemetry;

// CoreCLR emits each generation-range enumeration synchronously on one thread.
// Only the uninterrupted enumeration immediately before GCEnd is a post-GC map.
public sealed class GenerationRangeCollector(LiveTelemetryBuffer buffer)
{
    private readonly Dictionary<int, RangeBatch> _pending = [];
    private readonly HashSet<int> _boundariesAfterLoss = [];
    private bool _afterLoss;

    public void Add(int thread, int runtime, GcGenerationRange range)
    {
        if (!_pending.TryGetValue(thread, out var batch))
        {
            batch = new RangeBatch(runtime)
            {
                Invalid = _afterLoss && !_boundariesAfterLoss.Contains(thread)
            };
            _pending.Add(thread, batch);
        }

        if (batch.Runtime != runtime || !range.IsValid)
        {
            batch.Invalid = true;
            return;
        }

        if (range.ReservedLength != 0)
        {
            batch.Ranges.Add(range);
        }
    }

    public void ObserveEvent(int thread, int eventId)
    {
        // GCStart is not used. GCEnd and generation-range records have dedicated handlers.
        if (eventId is not (1 or 2 or 23))
        {
            _pending.Remove(thread);

            if (_afterLoss)
            {
                _boundariesAfterLoss.Add(thread);
            }
        }
    }

    public void Complete(int thread, int runtime, double seconds)
    {
        if (_afterLoss)
        {
            _boundariesAfterLoss.Add(thread);
        }

        if (!_pending.Remove(thread, out var batch) || batch.Runtime != runtime || batch.Invalid)
        {
            return;
        }

        buffer.GenerationRanges(runtime, batch.Ranges, seconds);
    }

    public void Reset()
    {
        _pending.Clear();
        _boundariesAfterLoss.Clear();
        _afterLoss = true;
    }

    private sealed class RangeBatch(int runtime)
    {
        public int Runtime { get; } = runtime;

        public List<GcGenerationRange> Ranges { get; } = [];

        public bool Invalid { get; set; }
    }
}
