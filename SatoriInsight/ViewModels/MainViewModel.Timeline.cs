using SatoriInsight.Models;
using SatoriInsight.Services.Telemetry;

namespace SatoriInsight.ViewModels;

public sealed partial class MainViewModel
{
    private DiagnosticFrame? _timelineAnchor;
    private GcEvent[] _timelineEvents = [];
    private double? _selectedTime;
    private double? _selectedPeriodStart;

    public bool HasTimelineSelection => _selectedTime.HasValue;

    public bool IsPeriodSelected => _selectedPeriodStart.HasValue;

    public double? TimelineSelectionStart => _selectedPeriodStart ?? _selectedTime;

    public double? TimelineSelectionEnd => _selectedTime;

    public IReadOnlyList<GcEvent> TimelineEvents => _timelineAnchor is not null ? _timelineEvents : Events;

    public string TimelineSelectionLabel => _selectedTime is not { } end ? (IsReplay ? "Recorded session" : "Live session") : _selectedPeriodStart is { } start
        ? $"{start:0.000}–{end:0.000} s · {end - start:0.000} s selected"
        : $"At {end:0.000} s";

    public void SelectTimePoint(double seconds) => SelectTimeline(seconds, null, true);

    public void SelectPeriod(double start, double end) => SelectTimeline(Math.Max(start, end), Math.Min(start, end), true);

    private void SelectTimeline(double end, double? start, bool stopPlayback)
    {
        if (!double.IsFinite(end) || start is { } s && !double.IsFinite(s))
        {
            return;
        }

        if (_timelineAnchor is null)
        {
            _timelineAnchor = IsReplay ? RecordedFrame() : _frame;
            _timelineEvents = _timelineAnchor.Events.OrderByDescending(gc => gc.EndSeconds).ToArray();
        }

        var source = _timelineAnchor;
        _selectedTime = Math.Clamp(end, 0, source.AtSeconds);
        _selectedPeriodStart = start is { } a ? Math.Clamp(a, 0, _selectedTime.Value) : null;

        if (_selectedPeriodStart == _selectedTime)
        {
            _selectedPeriodStart = null;
        }

        _isPaused = true;

        if (IsReplay)
        {
            _replayPosition = _selectedTime.Value;

            if (stopPlayback)
            {
                _replayPlaying = false;
            }

            Notify(nameof(ReplayPosition), nameof(ReplayPlayLabel));
        }

        var last = source.History.LastOrDefault(p => p.Seconds <= _selectedTime);
        var metrics = _selectedTime == source.AtSeconds ? source.Metrics : last?.HeapMetrics;
        var allocation = IsPeriodSelected
            ? source.History.Where(p => p.Seconds >= _selectedPeriodStart && p.Seconds <= _selectedTime).Select(p => p.AllocationMiB).Average()
            : last?.AllocationMiB;

        if (metrics is not null && IsPeriodSelected)
        {
            metrics = metrics with
            {
                AllocationBytesPerSecond = allocation * 1048576
            };
        }

        _frame = new(_selectedTime.Value, source.Topology is { } topology
            ? HeapTopology.At(topology, _selectedTime.Value)
            : [], source.Events, source.History, allocation, metrics, source.Health is { } health
            ? health with
            {
                LastCounterSeconds = last?.Seconds
            }
            : null, source.PauseCounters, source.Topology, ProcessHistory: source.ProcessHistory);

        if (SelectedEvent is { } selected && (selected.EndSeconds > _selectedTime || selected.EndSeconds < WindowStartSeconds))
        {
            SelectedEvent = null;
        }

        RefreshFrame();
        RefreshLiveRegions();
        NotifyHeap();
    }

    public void ResetTimelineSelection()
    {
        var fallback = _timelineAnchor ?? _frame;
        ClearTimelineState();

        if (IsReplay)
        {
            _replayPlaying = false;
            _replayPosition = ReplayMaximum;
            _frame = RecordedFrame();
            Notify(nameof(ReplayPosition), nameof(ReplayPlayLabel));
            RefreshFrame();
            RefreshLiveRegions();
            NotifyHeap();
        }
        else
        {
            _isPaused = false;
            ApplyLiveFrame(_liveSession?.Read() ?? _latestLiveFrame ?? fallback);
        }
    }

    private void ClearTimelineState()
    {
        _timelineAnchor = null;
        _timelineEvents = [];
        _selectedTime = _selectedPeriodStart = null;
    }

    private DiagnosticFrame RecordedFrame()
    {
        var session = _replay!;
        return new(
            session.SessionSeconds, session.Regions, session.Collections, session.Metrics,
            session.HeapMetrics?.AllocationBytesPerSecond / 1048576 ?? session.Metrics.LastOrDefault()?.AllocationMiB, session.HeapMetrics, session.Health,
            session.PauseCounters, session.Topology, session.GcCounts, session.ProcessMetrics);
    }
}
