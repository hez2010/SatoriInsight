using System.Text.Json.Serialization;

namespace SatoriInsight.Models;

public sealed record ThreadLocalGcInfo(
    int ClrInstanceId, int ThreadId, bool Collected, ulong OccupancyBefore, ulong OccupancyAfter, double DurationNanoseconds);

public sealed record GcGenerationStats(string Generation, long? BeforeBytes, long? AfterBytes, long? PromotedBytes)
{
    [JsonIgnore]
    public string BeforeLabel => BeforeBytes is { } bytes ? Format.Bytes(bytes) : "—";

    [JsonIgnore]
    public string AfterLabel => AfterBytes is { } bytes ? Format.Bytes(bytes) : "—";

    [JsonIgnore]
    public string PromotedLabel => PromotedBytes is { } bytes ? Format.Bytes(bytes) : "—";
}

public sealed record GcMemoryStats(long? HeapBeforeBytes, long? HeapAfterBytes, long? PromotedBytes, bool BeforeIsEstimated, GcGenerationStats[] Generations);

public sealed record GcEvent(
    long Number, double EndSeconds, int Generation, string Kind, string Reason, double DurationMilliseconds, double PauseMilliseconds,
    double SuspendMilliseconds, ThreadLocalGcInfo? ThreadLocal = null, GcMemoryStats? Memory = null)
{
    [JsonIgnore]
    public bool IsThreadLocal => ThreadLocal is not null;

    [JsonIgnore]
    public string NumberLabel => $"#{Number}";

    [JsonIgnore]
    public string TimeLabel => TimeSpan.FromSeconds(EndSeconds).ToString(@"mm\:ss\.fff");

    [JsonIgnore]
    public string GenerationLabel => $"Gen{Generation}";

    [JsonIgnore]
    public string PauseLabel => IsThreadLocal ? "—" : $"{PauseMilliseconds:0.00} ms";

    [JsonIgnore]
    public string SuspendLabel => IsThreadLocal ? "—" : $"{SuspendMilliseconds:0.00} ms";

    [JsonIgnore]
    public string DurationLabel => IsThreadLocal && DurationMilliseconds < 1 ? $"{DurationMilliseconds * 1000:0.00} µs" : $"{DurationMilliseconds:0.00} ms";

    [JsonIgnore]
    public decimal? BeforeBytes => ThreadLocal is { } local ? (decimal)local.OccupancyBefore : Memory?.HeapBeforeBytes;

    [JsonIgnore]
    public string BeforeLabel => BeforeBytes is { } bytes ? Format.Bytes((double)bytes) : "—";

    [JsonIgnore]
    public string AfterLabel => ThreadLocal is { } local
        ? Format.Bytes((double)local.OccupancyAfter)
        : Memory?.HeapAfterBytes is { } bytes ? Format.Bytes(bytes) : "—";

    [JsonIgnore]
    public string PromotedLabel => Memory?.PromotedBytes is { } bytes ? Format.Bytes(bytes) : "—";

    [JsonIgnore]
    public string MemoryScope => IsThreadLocal ? "Thread occupancy" : "Managed heap";

    [JsonIgnore]
    public string BeforeTooltip => Memory?.BeforeIsEstimated == true ? "Estimated heap size before collection." : $"{MemoryScope} before collection.";
}

public sealed record GcCollectionCounts(long Gen0, long Gen1, long Gen2);
