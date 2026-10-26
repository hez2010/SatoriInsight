namespace SatoriInsight.ViewModels;

public sealed partial class MainViewModel
{
    public string GcModeLabel => _frame.GcModes?.LastOrDefault(sample => sample.Seconds <= SessionSeconds && sample.Collector.HasValue)
        ?.Collector?.ToString() ?? "Unknown";

    public string GcLatencyModeLabel => "Latency mode: " + (_frame.GcModes?
        .LastOrDefault(sample => sample.Seconds <= SessionSeconds && sample.LatencyMode.HasValue)?.LatencyMode?.ToString() ?? "Unknown");
}
