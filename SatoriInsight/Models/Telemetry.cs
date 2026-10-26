namespace SatoriInsight.Models;

public sealed record MetricPoint(
    double Seconds, double? UsedMiB, double? CommittedMiB, double? AllocationMiB, RuntimeHeapMetrics? HeapMetrics = null, GcCollectionCounts? GcCounts = null);

public sealed record HistogramBin(string Label, double Lower, double Upper, int Count);

public sealed record DiagnosticFrame(
    double AtSeconds, IReadOnlyList<HeapRegion> Regions, IReadOnlyList<GcEvent> Events, IReadOnlyList<MetricPoint> History, double? AllocationMiB,
    RuntimeHeapMetrics? Metrics = null, CollectionHealth? Health = null, IReadOnlyList<GcPauseCounterPoint>? PauseCounters = null,
    HeapTopologyHistory? Topology = null, GcCollectionCounts? GcCounts = null,
    IReadOnlyList<ProcessMetricPoint>? ProcessHistory = null, IReadOnlyList<GcModeSample>? GcModes = null);
