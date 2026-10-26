namespace SatoriInsight.Models;

public sealed record SavedSession(
    int SchemaVersion, string Source, LocalDotNetProcess? Process, double SessionSeconds, MetricPoint[] Metrics, GcEvent[] Collections,
    RuntimeHeapMetrics? HeapMetrics, CollectionHealth? Health, GcPauseCounterPoint[] PauseCounters, HeapRegion[] Regions, HeapProfile[] Snapshots,
    HeapTopologyHistory? Topology, GcCollectionCounts? GcCounts, ProcessMetricPoint[] ProcessMetrics, GcModeSample[] GcModes);

public sealed record HeapSegmentChange(double Seconds, int RuntimeId, ulong Address, HeapRegion? Created = null, bool Reset = false);

public sealed record HeapTopologyHistory(double StartSeconds, HeapRegion[] InitialRegions, HeapSegmentChange[] Changes, RegionMapSampleInfo[] Samples);
