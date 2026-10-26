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
    public double? ProcessPauseMilliseconds => IsThreadLocal ? null : PauseMilliseconds;

    [JsonIgnore]
    public double? ProcessSuspendMilliseconds => IsThreadLocal ? null : SuspendMilliseconds;

    [JsonIgnore]
    public string PauseLabel => ProcessPauseMilliseconds is { } value ? $"{value:0.00} ms" : "—";

    [JsonIgnore]
    public string SuspendLabel => ProcessSuspendMilliseconds is { } value ? $"{value:0.00} ms" : "—";

    [JsonIgnore]
    public string DurationLabel => IsThreadLocal && DurationMilliseconds < 1 ? $"{DurationMilliseconds * 1000:0.00} µs" : $"{DurationMilliseconds:0.00} ms";

    [JsonIgnore]
    public decimal? BeforeBytes => ThreadLocal is { } local ? (decimal)local.OccupancyBefore : Memory?.HeapBeforeBytes;

    [JsonIgnore]
    public decimal? AfterBytes => ThreadLocal is { } local ? (decimal)local.OccupancyAfter : Memory?.HeapAfterBytes;

    [JsonIgnore]
    public long? PromotedBytes => Memory?.PromotedBytes;

    [JsonIgnore]
    public string BeforeLabel => BeforeBytes is { } bytes ? Format.Bytes((double)bytes) : "—";

    [JsonIgnore]
    public string AfterLabel => AfterBytes is { } bytes ? Format.Bytes((double)bytes) : "—";

    [JsonIgnore]
    public string PromotedLabel => PromotedBytes is { } bytes ? Format.Bytes(bytes) : "—";

    [JsonIgnore]
    public string MemoryScope => IsThreadLocal ? "Thread occupancy" : "Managed heap";

    [JsonIgnore]
    public string BeforeTooltip => Memory?.BeforeIsEstimated == true ? "Estimated heap size before collection." : $"{MemoryScope} before collection.";
}

public sealed record GcCollectionCounts(long Gen0, long Gen1, long Gen2);
