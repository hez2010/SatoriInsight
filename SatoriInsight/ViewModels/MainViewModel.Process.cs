using SatoriInsight.Models;
using SatoriInsight.Services.Telemetry;

namespace SatoriInsight.ViewModels;

public sealed partial class MainViewModel
{
    public IReadOnlyList<ProcessMetricPoint> ProcessHistory => (_timelineAnchor ?? _frame).ProcessHistory ?? [];

    private ProcessMetricPoint? CurrentProcessMetrics => ProcessStatistics.AtOrBefore(ProcessHistory, SessionSeconds);

    public string ProcessTimeCaption => IsPeriodSelected ? "Selected period" : "Since process start";

    public string CpuTimeLabel
    {
        get
        {
            if (!IsPeriodSelected)
            {
                return FormatProcessTime(CurrentProcessMetrics?.CpuSeconds);
            }

            var seconds = ProcessStatistics.CpuTimeInRange(ProcessHistory, WindowStartSeconds, SessionSeconds);
            return seconds.HasValue ? "≈ " + FormatProcessTime(seconds) : "—";
        }
    }

    public string CpuTimeTooltip
    {
        get
        {
            if (!IsPeriodSelected)
            {
                return "Total CPU time across all process threads since startup. This can exceed elapsed time.";
            }

            if (!ProcessStatistics.CpuTimeInRange(ProcessHistory, WindowStartSeconds, SessionSeconds).HasValue)
            {
                return "Not enough CPU measurements for this selection.";
            }

            return "Estimated CPU time in the selected period, across all process threads.";
        }
    }

    public string WallClockTimeLabel => FormatProcessTime(IsPeriodSelected
        ? SessionSeconds - WindowStartSeconds
        : CurrentProcessMetrics?.WallClockSeconds);

    public string ResidentMemoryLabel => CurrentProcessMetrics?.ResidentBytes is { } bytes ? Format.Bytes(bytes) : "—";

    public string PeakResidentMemoryLabel
    {
        get
        {
            var peak = ProcessStatistics.PeakResidentBytes(ProcessHistory, WindowStartSeconds, SessionSeconds);

            if (peak is not { } bytes)
            {
                return "";
            }

            var caption = IsPeriodSelected ? "Peak in selection" : "Recorded peak";
            return $"{caption}: {Format.Bytes(bytes)}";
        }
    }

    private static string FormatProcessTime(double? seconds)
    {
        if (seconds is not { } value)
        {
            return "—";
        }

        var duration = TimeSpan.FromSeconds(value);
        return $"{(long)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}.{duration.Milliseconds:000}";
    }

    private void NotifyProcessMetrics()
    {
        Notify(nameof(ProcessHistory), nameof(ProcessTimeCaption), nameof(CpuTimeLabel), nameof(CpuTimeTooltip),
            nameof(WallClockTimeLabel), nameof(ResidentMemoryLabel), nameof(PeakResidentMemoryLabel));
    }
}
