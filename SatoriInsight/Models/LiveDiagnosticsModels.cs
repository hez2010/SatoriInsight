using System.Text.Json.Serialization;

namespace SatoriInsight.Models;

public sealed record LocalDotNetProcess(int ProcessId, string Name, DateTime StartTimeUtc, [property: JsonIgnore] string? CommandLine = null)
{
    [JsonIgnore]
    public string CommandLineTooltip => string.IsNullOrWhiteSpace(CommandLine) ? "Command line unavailable for this process." : CommandLine;

    public string StartedLabel => StartTimeUtc.ToLocalTime().ToString("HH:mm:ss");

    public string DisplayLabel => $"{Name} (PID {ProcessId})";
}

/// <summary>Null means unavailable, never a fabricated zero. Byte values are normalized from counter units.</summary>
public sealed record RuntimeHeapMetrics(
    double? HeapBytes = null, double? CommittedBytes = null, double? AllocationBytesPerSecond = null, double? Gen0Bytes = null, double? Gen1Bytes = null,
    double? Gen2Bytes = null, double? LohBytes = null, double? PohBytes = null, double? FragmentationPercent = null, int? HeapCount = null);

public sealed record GcPauseCounterPoint(double EndSeconds, double IntervalSeconds, double PauseMilliseconds);

public sealed record CollectionHealth(
    long EventsLost = 0, int DiscardedCollections = 0, double? LastCounterSeconds = null, string? RuntimeVersion = null, bool StackwalkSuppressed = true,
    bool? ThreadLocalEventsEnabled = null)
{
    public bool IsIncomplete => EventsLost > 0 || DiscardedCollections > 0;
}

public sealed record CollectionEnd(bool TargetExited, string? Error = null);

public enum LocalConnectionState
{
    Connecting,
    Connected,
    Disconnected,
    Failed
}
