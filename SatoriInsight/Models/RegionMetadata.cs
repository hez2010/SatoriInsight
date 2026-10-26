using System.Text.Json.Serialization;

namespace SatoriInsight.Models;

// Names and numeric values follow the runtime's SatoriQueue.h and SatoriRegion.h.
public enum RegionQueueKind
{
    Allocator,
    WorkChunk,
    RecyclerEphemeral,
    RecyclerEphemeralFinalizationTracking,
    RecyclerTenured,
    RecyclerTenuredFinalizationTracking,
    RecyclerNursery,
    RecyclerFinalizationPending,
    RecyclerFinalizationScanComplete,
    RecyclerStaying,
    RecyclerRelocating,
    RecyclerRelocated,
    RecyclerRelocatedToHigherGen,
    RecyclerRelocationTarget,
    RecyclerDeferredSweep,
    RecyclerReusable,
    RecyclerDemoted
}

public enum RegionReuseLevel
{
    None,
    Gen1,
    Gen0
}

public sealed record RegionHeaderInfo(
    ulong PageAddress, ulong PageEnd, int Generation, RegionQueueKind? Queue, RegionReuseLevel ReusableFor, ulong UsedEnd, ulong CommittedEnd,
    ulong AllocationStart, ulong AllocationEnd, long ReportedOccupancyBytes, int ReportedObjectCount, bool HasPinnedObjects)
{
    [JsonIgnore]
    public HeapGeneration HeapGeneration => Generation switch
    {
        -1 => HeapGeneration.Unassigned,
        0 => HeapGeneration.Gen0,
        1 => HeapGeneration.Gen1,
        2 => HeapGeneration.Gen2,
        3 => HeapGeneration.Gen3,
        _ => HeapGeneration.Unknown
    };

    // InitializeAt and MakeBlank reserve the whole allocation range in m_occupancy.
    // For generation -1 this is capacity, not managed-object occupancy.
    [JsonIgnore]
    public bool HasReportedOccupancy => Generation >= 0;

    [JsonIgnore]
    public string OccupancyLabel => HasReportedOccupancy ? Format.Bytes(ReportedOccupancyBytes) : "—";

    [JsonIgnore]
    public string ObjectCountLabel => HasReportedOccupancy ? ReportedObjectCount.ToString("N0") : "—";
}

public sealed record RegionMapSampleInfo(
    double Seconds, int RegionCount, int SkippedRegions, bool Complete, long BytesRead, double ReadMilliseconds, string? Error = null);
