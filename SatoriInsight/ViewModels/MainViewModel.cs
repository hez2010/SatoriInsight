using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using SatoriInsight.Models;
using SatoriInsight.Services.Processes;
using SatoriInsight.Services.Recording;
using SatoriInsight.Services.Telemetry;

namespace SatoriInsight.ViewModels;

public sealed partial class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private DiagnosticFrame _frame = EmptyFrame();
    private GcEvent[] _visibleEvents = [];
    private GcEvent[] _pauseEvents = [];
    private GcEvent[] _filteredEvents = [];
    private readonly BulkObservableCollection<GcEvent> _eventRows = [];
    private GcEvent? _selectedEvent;
    private HistogramBin? _selectedPauseBin;
    private int _navigationIndex;
    private int _generationFilterIndex;

    public string[] GenerationFilterOptions { get; } = ["All generations", nameof(HeapGeneration.Gen0), nameof(HeapGeneration.Gen1), nameof(HeapGeneration.Gen2)];

    public int GenerationFilterIndex
    {
        get => _generationFilterIndex;
        set
        {
            if (Set(ref _generationFilterIndex, Math.Clamp(value, 0, 3)))
            {
                RefreshEventLog();
            }
        }
    }

    private bool _isPaused;
    private string _status = "Launch or attach to a .NET process.";

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand TogglePauseCommand { get; }

    public ICommand ClearPauseFilterCommand { get; }

    public MainViewModel(
        ILocalProcessDiscovery? processDiscovery = null, ILiveTelemetrySessionFactory? sessionFactory = null, ILocalProcessLauncher? processLauncher = null)
    {
        _processDiscovery = processDiscovery ?? new LocalProcessDiscovery();
        _sessionFactory = sessionFactory ?? new EventPipeSessionFactory();
        _processLauncher = processLauncher ?? new LocalProcessLauncher(_sessionFactory);
        TogglePauseCommand = new ActionCommand(_ => IsPaused = !IsPaused);
        ClearPauseFilterCommand = new ActionCommand(_ => SelectedPauseBin = null);
        CapturedRegionMap.ObjectRequested += InspectRegionObject;
        CapturedRegionMap.RegionObjectsRequested += InspectRegionObjects;
    }

    public int NavigationIndex
    {
        get => _navigationIndex;
        set
        {
            if (Set(ref _navigationIndex, value))
            {
                Notify(
                    nameof(IsMemoryActivityView), nameof(IsHeapInspectorView), nameof(PageTitle), nameof(PageSubtitle));
            }
        }
    }

    public bool IsMemoryActivityView => NavigationIndex == 0;

    public bool IsHeapInspectorView => NavigationIndex == 1;

    public string AppVersionLabel { get; } = $"Satori Insight · v{typeof(MainViewModel).Assembly.GetName().Version}";

    public string PageTitle => NavigationIndex switch
    {
        1 => "Heap inspector",
        _ => "Memory activity"
    };

    public string PageSubtitle => NavigationIndex switch
    {
        1 => "Captured regions, types, objects, and root paths.",
        _ => "Heap usage, regions, and collections."
    };

    public double WindowStartSeconds => _selectedPeriodStart ?? 0;

    public double ChartMaximumSeconds => Math.Max(1, (_timelineAnchor ?? _frame).AtSeconds);

    public double ChartStepSeconds => ChartMaximumSeconds switch
    {
        <= 6 => 1,
        <= 15 => 5,
        <= 45 => 10,
        <= 90 => 20,
        <= 180 => 60,
        _ => Math.Max(100, Math.Ceiling(ChartMaximumSeconds / 600) * 100)
    };

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (!Set(ref _isPaused, value))
            {
                return;
            }

            if (!value && !IsReplay)
            {
                ClearTimelineState();
            }

            if (!value && !IsReplay && (_liveSession?.Read() ?? _latestLiveFrame) is { } frame)
            {
                ApplyLiveFrame(frame);
            }

            Notify(nameof(PauseLabel), nameof(StreamLabel));
        }
    }

    public string PauseLabel => HasTimelineSelection && !IsReplay ? "▷  Resume live" : IsPaused ? "▷  Resume view" : "Ⅱ  Pause view";

    public string StreamLabel
    {
        get
        {
            if (IsReplay)
            {
                return "REPLAY";
            }

            if (IsConnected)
            {
                return IsPaused ? "VIEW PAUSED · RECORDING" : "RECORDING";
            }

            return ConnectionState.ToString().ToUpperInvariant();
        }
    }

    public string SessionTime => TimeSpan.FromSeconds(SessionSeconds).ToString(@"hh\:mm\:ss");

    public double SessionSeconds => _frame.AtSeconds;

    public string Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
            {
                Notify(nameof(QualityMessage), nameof(StateNoticeMessage));
            }
        }
    }

    public bool HasSessionData => _frame.History.Count > 0 || _frame.Events.Count > 0 || _frame.ProcessHistory?.Count > 0 || _frame.GcModes?.Count > 0 || Snapshots.Count > 0;

    public bool HasCollectedData => HasSessionData || _latestLiveFrame is { } latest &&
        (latest.History.Count > 0 || latest.Events.Count > 0 || latest.ProcessHistory?.Count > 0 || latest.GcModes?.Count > 0);

    public bool CanPause => !IsReplay && ActiveProcess is not null && (IsConnected || HasCollectedData);

    public IReadOnlyList<GcEvent> Events => _visibleEvents;

    public IReadOnlyList<GcEvent> PauseEvents => _pauseEvents;

    public IReadOnlyList<GcEvent> EventLog => _eventRows;

    private void RefreshEventLog()
    {
        _filteredEvents = GenerationFilterIndex == 0 && SelectedPauseBin is null ? _visibleEvents : _visibleEvents.Where(e => (GenerationFilterIndex == 0
            || e.Generation == GenerationFilterIndex - 1) && (SelectedPauseBin is null
            || !e.IsThreadLocal
            && e.PauseMilliseconds >= SelectedPauseBin.Lower && e.PauseMilliseconds < SelectedPauseBin.Upper)).ToArray();

        if (SelectedEvent is { } selected && !_filteredEvents.Contains(selected))
        {
            SelectedEvent = null;
        }

        if (_eventRows.ReplaceAll(_filteredEvents))
        {
            Notify(nameof(SelectedEvent));
        }

        Notify(nameof(EventFilterLabel), nameof(HasEvents));
    }

    public HistogramBin? SelectedPauseBin
    {
        get => _selectedPauseBin;
        set
        {
            if (Set(ref _selectedPauseBin, value))
            {
                RefreshEventLog();
                Notify(nameof(HasPauseFilter));
            }
        }
    }

    public bool HasPauseFilter => SelectedPauseBin is not null;

    public string EventFilterLabel => SelectedPauseBin is null
        ? $"{_filteredEvents.Length:N0} collections"
        : $"{_filteredEvents.Length:N0} collections · {SelectedPauseBin.Label} ms pause";

    public IReadOnlyList<MetricPoint> History => (_timelineAnchor ?? _frame).History;

    public IReadOnlyList<GcPauseCounterPoint> PauseCounters => (_frame.PauseCounters ?? []).Where(p => p.EndSeconds >= WindowStartSeconds
        && p.EndSeconds <= SessionSeconds && (!IsPeriodSelected
        || p.EndSeconds - p.IntervalSeconds >= WindowStartSeconds)).ToArray();

    public IReadOnlyList<HistogramBin> Histogram => GcStatistics.Histogram(Events);

    public bool HasPauses => PauseEvents.Count > 0;

    public double HistogramMaximumCount => Math.Max(1, Histogram.Max(bin => bin.Count) + 1);

    public double HistogramCountStep => Math.Max(1, Math.Ceiling(HistogramMaximumCount / 5));

    public string UsedLabel => BytesOrUnknown(_frame.Metrics?.HeapBytes);

    public string CommittedLabel => _frame.Metrics?.CommittedBytes is { } bytes ? $"of {Format.Bytes((long)bytes)} committed" : "Committed size unavailable";

    public string AllocationLabel => _frame.AllocationMiB is { } value ? $"{value:0.0}" : "—";

    public string AllocationTitle => IsPeriodSelected ? "AVG ALLOCATION RATE" : "ALLOCATION RATE";

    public string P99Label => !HasPauses ? "—" : $"{GcStatistics.Percentile(Events, 99):0.00}";

    public string P99WithUnitsLabel => HasPauses ? $"{P99Label} ms" : "—";

    public string P999Label => !HasPauses ? "—" : $"{GcStatistics.Percentile(Events, 99.9):0.00} ms";

    public string MinPauseLabel => !HasPauses ? "—" : $"{PauseEvents.Min(gc => gc.PauseMilliseconds):0.00} ms";

    public string MaxPauseLabel => !HasPauses ? "—" : $"{PauseEvents.Max(gc => gc.PauseMilliseconds):0.00} ms";

    public string P50Label => !HasPauses ? "—" : $"{GcStatistics.Percentile(Events, 50):0.00} ms";

    public string P95Label => !HasPauses ? "—" : $"{GcStatistics.Percentile(Events, 95):0.00} ms";

    public string PausePercentLabel => GcStatistics.CounterPausePercent(PauseCounters) is { } percent ? $"{percent:0.00}%" : "—";

    public string GcCountLabel => $"{PauseEvents.Count:N0} recorded pauses";

    public string PauseCountLabel => $"{PauseEvents.Count:N0} pauses · milliseconds";

    private GcCollectionCounts? DisplayCounts => _frame.GcCounts ?? (_frame.Events.Count == 0 ? null : new(
        _frame.Events.LongCount(e => e.Generation == 0), _frame.Events.LongCount(e => e.Generation == 1), _frame.Events.LongCount(e => e.Generation == 2)));

    public string Gen0CountLabel => DisplayCounts?.Gen0.ToString("N0") ?? "—";

    public string Gen1CountLabel => DisplayCounts?.Gen1.ToString("N0") ?? "—";

    public string Gen2CountLabel => DisplayCounts?.Gen2.ToString("N0") ?? "—";

    public string CollectionCountsCaption => IsPeriodSelected ? "Selected period" : HasTimelineSelection ? "Through selected time" : "Since connection";

    public bool HasHistory => History.Count > 0 || ProcessHistory.Any(sample => sample.ResidentBytes.HasValue);

    public bool HasEvents => EventLog.Count > 0;

    public string EmptyHistoryLabel => IsConnected ? "Waiting for heap measurements…" : "No heap measurements";

    public string EmptyEventsLabel => IsConnected || HasCollectedData ? "No collections match this view." : "Select a process to start recording.";

    public GcEvent? SelectedEvent
    {
        get => _selectedEvent;
        set
        {
            if (!_eventRows.IsUpdating && Set(ref _selectedEvent, value))
            {
                Notify(
                    nameof(EventTitle), nameof(EventReason), nameof(EventReasonTitle), nameof(EventPause), nameof(EventDuration), nameof(EventMode),
                    nameof(EventSuspend), nameof(EventBefore), nameof(EventAfter), nameof(EventPromoted), nameof(EventMemoryScope), nameof(EventBeforeTooltip),
                    nameof(EventGenerations), nameof(HasEventGenerations), nameof(EventLatencyMode), nameof(EventLatencyModeTooltip));
            }
        }
    }

    public string EventTitle => SelectedEvent is { } gc
        ? $"GC {gc.NumberLabel} · {gc.GenerationLabel}" + (gc.ThreadLocal is { } local ? $" · Thread {local.ThreadId}" : "")
        : "Select a collection";

    public string EventReason => SelectedEvent?.Reason ?? "—";

    public string EventReasonTitle => SelectedEvent?.IsThreadLocal == true ? "OUTCOME" : "TRIGGER";

    public string EventPause => SelectedEvent?.PauseLabel ?? "—";

    public string EventDuration => SelectedEvent?.DurationLabel ?? "—";

    public string EventSuspend => SelectedEvent?.SuspendLabel ?? "—";

    public string EventMode => SelectedEvent?.Kind ?? "—";

    public string EventLatencyMode => SelectedEvent?.LatencyModeLabel ?? "—";

    public string EventLatencyModeTooltip => SelectedEvent?.LatencyModeTooltip ?? "Latency mode for the selected collection.";

    public string EventBefore => SelectedEvent?.BeforeLabel ?? "—";

    public string EventAfter => SelectedEvent?.AfterLabel ?? "—";

    public string EventPromoted => SelectedEvent?.PromotedLabel ?? "—";

    public string EventMemoryScope => SelectedEvent?.MemoryScope ?? "Memory";

    public string EventBeforeTooltip => SelectedEvent?.BeforeTooltip ?? "Size before collection.";

    public IReadOnlyList<GcGenerationStats> EventGenerations => SelectedEvent?.Memory?.Generations ?? [];

    public bool HasEventGenerations => EventGenerations.Count > 0;

    public void FilterToPauseBin(HistogramBin bin)
    {
        GenerationFilterIndex = 0;
        SelectedPauseBin = bin;
        SelectedEvent = EventLog.FirstOrDefault();
        NavigationIndex = 0;
    }

    private SavedSession SessionForExport()
    {
        // Presentation can be paused while collection continues. Save the latest retained data.
        var frame = IsReplay ? _frame : _liveSession?.Read() ?? _latestLiveFrame ?? _frame;
        return new(
            SessionStorage.CurrentSchemaVersion, "local-eventpipe", ActiveProcess, _replay?.SessionSeconds ?? frame.AtSeconds,
            _replay?.Metrics ?? frame.History.ToArray(), _replay?.Collections ?? frame.Events.ToArray(), _replay?.HeapMetrics ?? frame.Metrics,
            _replay?.Health ?? frame.Health, _replay?.PauseCounters ?? frame.PauseCounters?.ToArray() ?? [], _replay?.Regions ?? frame.Regions.ToArray(),
            Snapshots.ToArray(), _replay?.Topology ?? frame.Topology, _replay?.GcCounts ?? frame.GcCounts,
            _replay?.ProcessMetrics ?? frame.ProcessHistory?.ToArray() ?? [], _replay?.GcModes ?? frame.GcModes?.ToArray() ?? []);
    }

    public string ExportJson() => SessionStorage.Write(SessionForExport());

    public Task ExportSessionAsync(Stream stream)
    {
        var session = SessionForExport();
        return Task.Run(() => SessionStorage.WriteAsync(stream, session, _lifetime.Token), _lifetime.Token);
    }

    private void RefreshFrame()
    {
        // Snapshot the sorted window once per refresh, not once per binding or percentile.
        IEnumerable<GcEvent> window;

        if (_timelineAnchor is not null)
        {
            window = _timelineEvents.Where(collection => collection.EndSeconds >= WindowStartSeconds && collection.EndSeconds <= SessionSeconds);
        }
        else if (_frame.Events is GcEventSnapshot snapshot)
        {
            window = snapshot.InWindow(WindowStartSeconds, SessionSeconds);
        }
        else
        {
            window = _frame.Events.Where(collection => collection.EndSeconds >= WindowStartSeconds && collection.EndSeconds <= SessionSeconds);
        }

        _visibleEvents = _timelineAnchor is not null ? window.ToArray() : window.OrderByDescending(e => e.EndSeconds).ToArray();
        _pauseEvents = _visibleEvents.Where(e => !e.IsThreadLocal).ToArray();

        if (HasTimelineSelection)
        {
            long gen0 = 0;
            long gen1 = 0;
            long gen2 = 0;

            foreach (var gc in _visibleEvents)
            {
                if (gc.Generation == 0)
                {
                    gen0++;
                }
                else if (gc.Generation == 1)
                {
                    gen1++;
                }
                else
                {
                    gen2++;
                }
            }

            _frame = _frame with
            {
                GcCounts = new(gen0, gen1, gen2)
            };
        }

        RefreshEventLog();
        NotifyProcessMetrics();
        Notify(nameof(GcModeLabel), nameof(GcLatencyModeLabel));
        Notify(
            nameof(SessionTime), nameof(SessionSeconds), nameof(WindowStartSeconds), nameof(ChartMaximumSeconds), nameof(ChartStepSeconds), nameof(UsedLabel),
            nameof(CommittedLabel), nameof(AllocationLabel), nameof(P99Label), nameof(P999Label), nameof(MinPauseLabel), nameof(MaxPauseLabel), nameof(P50Label), nameof(P95Label),
            nameof(PausePercentLabel), nameof(GcCountLabel), nameof(PauseCountLabel), nameof(Events), nameof(History), nameof(Histogram),
            nameof(HistogramMaximumCount), nameof(HistogramCountStep), nameof(HasPauses), nameof(P99WithUnitsLabel), nameof(PauseCounters),
            nameof(HasSessionData), nameof(HasCollectedData), nameof(CanPause), nameof(HasHistory), nameof(EmptyHistoryLabel), nameof(EmptyEventsLabel),
            nameof(GenerationSizes), nameof(LiveFragmentationLabel), nameof(HeapCountLabel), nameof(PauseEvents),
            nameof(Gen0CountLabel), nameof(Gen1CountLabel), nameof(Gen2CountLabel), nameof(CollectionCountsCaption), nameof(AllocationTitle),
            nameof(TimelineEvents), nameof(HasTimelineSelection), nameof(IsPeriodSelected), nameof(TimelineSelectionStart), nameof(TimelineSelectionEnd),
            nameof(TimelineSelectionLabel), nameof(PauseLabel), nameof(StreamLabel), nameof(ShowQualityNotice), nameof(QualityMessage));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new(name));
        return true;
    }

    private void Notify(params string[] names)
    {
        foreach (var name in names)
        {
            PropertyChanged?.Invoke(this, new(name));
        }
    }

    private static DiagnosticFrame EmptyFrame() => new(0, [], [], [], null, new(), new(), []);

    private static string BytesOrUnknown(double? value) => value is { } bytes ? Format.Bytes((long)bytes) : "—";
}

public sealed class ActionCommand(Action<object?> action) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add
        {
        }

        remove
        {
        }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => action(parameter);
}
