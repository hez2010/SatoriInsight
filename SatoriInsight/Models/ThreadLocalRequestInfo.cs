using System.Text.Json.Serialization;

namespace SatoriInsight.Models;

public enum ThreadLocalOutcome : uint { EscapedLimit, InsufficientProgress, SurvivalAbort, Completed }
public enum ThreadLocalTrigger : uint { WindowLimit, ExhaustedSpan }
public enum ThreadLocalRetry : uint { NotAttempted, Succeeded, Failed }

public sealed record ThreadLocalRequestInfo(
    ThreadLocalOutcome Outcome, ThreadLocalTrigger Trigger, ThreadLocalRetry Retry, bool TrackingProductive,
    ulong RegionId, ulong RequestedBytes, ulong AllocationProgress, ulong UsableBefore, ulong UsableAfter)
{
    [JsonIgnore]
    public string OutcomeLabel => Outcome switch
    {
        ThreadLocalOutcome.EscapedLimit => "Escape limit",
        ThreadLocalOutcome.InsufficientProgress => "Insufficient progress",
        ThreadLocalOutcome.SurvivalAbort => "Survival abort",
        _ => "Completed"
    };

    [JsonIgnore]
    public string TriggerLabel => Trigger == ThreadLocalTrigger.WindowLimit ? "Window limit" : "Exhausted span";

    [JsonIgnore]
    public string RetryLabel => Retry == ThreadLocalRetry.NotAttempted ? "Not attempted" : Retry.ToString();

    [JsonIgnore]
    public string RegionLabel => $"0x{RegionId:X}";

    [JsonIgnore]
    public string RequestedLabel => Format.Bytes((double)RequestedBytes);

    [JsonIgnore]
    public string ProgressLabel => Format.Bytes((double)AllocationProgress);

    [JsonIgnore]
    public string UsableBeforeLabel => Format.Bytes((double)UsableBefore);

    [JsonIgnore]
    public string UsableAfterLabel => Format.Bytes((double)UsableAfter);
}
