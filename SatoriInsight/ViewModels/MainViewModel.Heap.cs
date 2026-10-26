using System.Collections.ObjectModel;
using SatoriInsight.Models;
using SatoriInsight.Services.Heap;
using SatoriInsight.Services.Recording;

namespace SatoriInsight.ViewModels;

public sealed partial class MainViewModel
{
    private CancellationTokenSource? _heapCancellation;
    private bool _heapBusy;
    private HeapProfile? _selectedSnapshot;
    private HeapProfile? _baselineSnapshot;
    private TypeStat? _selectedType;
    private HeapObjectInfo? _selectedObject;
    private ObjectReference? _selectedReference;
    private RootPathStep[] _rootPath = [];
    private string _heapStatus = "";
    private string _typeSearch = "";
    private int _inspectionTab;
    private SavedSession? _replay;
    private double _replayPosition;
    private Task? _heapTask;
    private bool _replayPlaying;
    private int? _objectRegion;
    private HeapObjectInfo[] _objects = [];
    private bool _sessionLoading;
    public const int MaxSnapshots = 4;

    public string ReplayPlayLabel => _replayPlaying ? "Pause replay" : "Play replay";

    public void ToggleReplay()
    {
        if (!IsReplay)
        {
            return;
        }

        _replayPlaying = !_replayPlaying;

        if (_replayPlaying && ReplayPosition >= ReplayMaximum)
        {
            ReplayPosition = ReplayMinimum;
        }

        Notify(nameof(ReplayPlayLabel));
    }

    private bool TickReplay()
    {
        if (!IsReplay)
        {
            return false;
        }

        if (_replayPlaying)
        {
            ReplayPosition += 1;

            if (ReplayPosition >= ReplayMaximum)
            {
                _replayPlaying = false;
                Notify(nameof(ReplayPlayLabel));
            }
        }

        return true;
    }

    public ObservableCollection<HeapProfile> Snapshots { get; } = [];

    public RegionMapViewModel LiveRegionMap { get; } = new();

    public RegionMapViewModel CapturedRegionMap { get; } = new();

    public bool IsHeapBusy => _heapBusy;

    public bool CanCaptureHeap => IsConnected && ActiveProcess is not null && !IsHeapBusy && !_isStopping && !IsReplay && !_sessionLoading && Snapshots.Count < MaxSnapshots;

    public bool CanOpenDump => !IsHeapBusy && !_sessionLoading && Snapshots.Count < MaxSnapshots;

    public bool CanOpenSession => !IsConnecting && !IsHeapBusy && !_isStopping && !_sessionLoading && !_disposed;

    public string HeapStatus
    {
        get => _heapStatus;
        private set
        {
            if (Set(ref _heapStatus, value))
            {
                Notify(nameof(HasHeapStatus));
            }
        }
    }

    public bool HasHeapStatus => !string.IsNullOrEmpty(HeapStatus);

    public bool HasSnapshot => SelectedSnapshot is not null;

    public bool HasObjectCapture => SelectedSnapshot is { MetadataOnly: false };

    public string CaptureSourceLabel => SelectedSnapshot is { } capture
        ? $"Captured {capture.CapturedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {capture.Process?.DisplayLabel ?? "Dump"}"
        : "Capture a heap or open a dump to inspect objects.";

    public int InspectionTab
    {
        get => _inspectionTab;
        set => Set(ref _inspectionTab, value);
    }

    public HeapProfile? SelectedSnapshot
    {
        get => _selectedSnapshot;
        set
        {
            if (Set(ref _selectedSnapshot, value))
            {
                CapturedRegionMap.SetSnapshot(value);
                HeapStatus = value?.Coverage ?? "";
                _objectRegion = null;

                SelectedType = null;
                SelectedObject = null;
                Notify(nameof(Types), nameof(TypeDifferences));
                NotifyObjects();
                NotifyHeap();

                if (HasSnapshot && (InspectionTab < 0 || !HasObjectCapture))
                {
                    InspectionTab = 0;
                }
            }
        }
    }

    public void ShowCapturedRegions()
    {
        if (SelectedSnapshot is null)
        {
            return;
        }

        InspectionTab = 0;
        NavigationIndex = 1;
    }

    public HeapProfile? BaselineSnapshot
    {
        get => _baselineSnapshot;
        set
        {
            if (Set(ref _baselineSnapshot, value))
            {
                Notify(nameof(TypeDifferences));
            }
        }
    }

    public string TypeSearch
    {
        get => _typeSearch;
        set
        {
            if (Set(ref _typeSearch, value))
            {
                Notify(nameof(Types));
            }
        }
    }

    public IReadOnlyList<TypeStat> Types => SelectedSnapshot?.Types.Where(t => t.Name.Contains(TypeSearch, StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];

    public TypeStat? SelectedType
    {
        get => _selectedType;
        set
        {
            if (Set(ref _selectedType, value))
            {
                SelectedObject = null;
                NotifyObjects();
            }
        }
    }

    private IEnumerable<HeapObjectInfo> MatchingObjects => (SelectedSnapshot?.Objects ?? []).Where(o => (SelectedType is null || o.Type == SelectedType.Name)
        && (_objectRegion is null
        || o.RegionId == _objectRegion));

    public IReadOnlyList<HeapObjectInfo> Objects => _objects;

    public string ObjectCountLabel => $"{_objects.Length:N0} captured objects · {SelectedType?.Name ?? "all types"}{(_objectRegion is { } id ? $" · region {id}" : "")}";

    public HeapObjectInfo? SelectedObject
    {
        get => _selectedObject;
        set
        {
            if (Set(ref _selectedObject, value))
            {
                _rootPath = [];
                SelectedReference = null;
                Notify(nameof(ObjectSummary), nameof(ObjectReferences), nameof(RootPath));
            }
        }
    }

    public ObjectReference? SelectedReference
    {
        get => _selectedReference;
        set => Set(ref _selectedReference, value);
    }

    public string ObjectSummary
    {
        get
        {
            if (SelectedObject is not { } obj)
            {
                return "Select an object.";
            }

            var roots = (SelectedSnapshot?.Roots ?? [])
                .Where(root => root.ObjectAddress == obj.Address)
                .Select(root => $" · {root.Kind}{(root.Pinned ? " (pinned)" : "")}");

            return $"{obj.AddressLabel} · {obj.Type}\n" +
                $"{obj.SizeLabel} · {obj.GenerationLabel} · region {obj.RegionId}\n" +
                $"{obj.References.Length:N0} captured references" + string.Concat(roots);
        }
    }

    public IReadOnlyList<ObjectReference> ObjectReferences => SelectedObject?.References ?? [];

    public IReadOnlyList<RootPathStep> RootPath => _rootPath;

    public IReadOnlyList<TypeGrowth> TypeDifferences => BaselineSnapshot is { MetadataOnly: false } b && SelectedSnapshot is { MetadataOnly: false } a
        ? HeapAnalysis.Compare(b, a)
        : [];

    public void FollowReference()
    {
        if (SelectedSnapshot?.Objects.FirstOrDefault(o => o.Address == SelectedReference?.Address) is { } obj)
        {
            InspectObject(obj);
        }
        else
        {
            HeapStatus = "Referenced object was not included in this capture.";
        }
    }

    public void InspectObject(HeapObjectInfo obj)
    {
        _objectRegion = null;
        SelectedType = null;
        NotifyObjects();
        SelectedObject = obj;
        InspectionTab = 2;
    }

    public void InspectRegionObject(HeapObjectInfo obj)
    {
        if (!CapturedRegionMap.HasObjectCapture)
        {
            return;
        }

        InspectObject(obj);
        NavigationIndex = 1;
    }

    public void InspectRegionObjects()
    {
        if (!CapturedRegionMap.HasObjectCapture || CapturedRegionMap.SelectedRegion is not { } region)
        {
            return;
        }

        SelectedType = null;
        _objectRegion = region.Id;
        InspectionTab = 2;
        NavigationIndex = 1;
        NotifyObjects();
    }

    public void InspectTypeObjects()
    {
        _objectRegion = null;
        InspectionTab = 2;
        NotifyObjects();
    }

    public void ShowAllObjects()
    {
        _objectRegion = null;
        SelectedType = null;
        NotifyObjects();
    }

    public Task CaptureHeapAsync(string? dumpPath = null)
    {
        if (IsHeapBusy || _disposed || (dumpPath is null ? !CanCaptureHeap : !CanOpenDump))
        {
            return Task.CompletedTask;
        }

        return _heapTask = CaptureHeapCoreAsync(dumpPath);
    }

    private async Task CaptureHeapCoreAsync(string? dumpPath)
    {
        if (IsHeapBusy || (dumpPath is null && !CanCaptureHeap))
        {
            return;
        }

        var target = ActiveProcess;
        _heapCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _heapBusy = true;
        NotifyHeap();
        HeapStatus = "Capturing heap…";

        try
        {
            var p = await new HeapCaptureService().CaptureAsync(new(dumpPath is null ? target : null, dumpPath), _heapCancellation.Token);

            if (dumpPath is null && ActiveProcess != target)
            {
                HeapStatus = "Target changed; capture discarded.";
                return;
            }

            Snapshots.Add(p);
            BaselineSnapshot ??= p;
            SelectedSnapshot = p;
            HeapStatus = p.Coverage + (Snapshots.Count == MaxSnapshots ? " Remove a capture before taking another." : "");
        }
        catch (OperationCanceledException)
        {
            HeapStatus = "Heap operation canceled.";
        }
        catch (Exception ex)
        {
            HeapStatus = $"Heap capture failed: {ex.Message}";
        }
        finally
        {
            _heapBusy = false;
            _heapCancellation.Dispose();
            _heapCancellation = null;
            NotifyHeap();
        }
    }

    public void CancelHeapOperation() => _heapCancellation?.Cancel();

    public Task FindRootAsync()
    {
        if (IsHeapBusy || _disposed || _sessionLoading)
        {
            return Task.CompletedTask;
        }

        return _heapTask = FindRootCoreAsync();
    }

    private async Task FindRootCoreAsync()
    {
        if (IsHeapBusy || SelectedSnapshot is not { } profile || SelectedObject is not { } obj)
        {
            return;
        }

        _heapBusy = true;
        _heapCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        NotifyHeap();
        HeapStatus = "Finding a root path…";
        InspectionTab = 3;

        try
        {
            var path = await Task.Run(() => HeapAnalysis.FindRootPath(profile, obj.Address, _heapCancellation.Token));

            if (SelectedSnapshot != profile || SelectedObject != obj)
            {
                return;
            }

            _rootPath = path;
            HeapStatus = path.Length > 0 ? $"Root path found: {path.Length} objects." : "No root path found in this capture.";
            Notify(nameof(RootPath));
        }
        catch (OperationCanceledException)
        {
            HeapStatus = "Root search canceled.";
        }
        catch (Exception ex)
        {
            HeapStatus = $"Root search failed: {ex.Message}";
        }
        finally
        {
            _heapBusy = false;
            _heapCancellation.Dispose();
            _heapCancellation = null;
            NotifyHeap();
        }
    }

    private void NotifyObjects()
    {
        var selected = SelectedObject;
        var objects = MatchingObjects.ToArray();

        if (!_objects.SequenceEqual(objects))
        {
            _objects = objects;
            Notify(nameof(Objects));
        }

        Notify(nameof(ObjectCountLabel));

        if (selected is not null && _objects.Contains(selected))
        {
            SelectedObject = selected;
        }
        else
        {
            SelectedObject = null;
        }
    }

    public void RemoveSnapshot()
    {
        if (SelectedSnapshot is not { } snapshot)
        {
            return;
        }

        Snapshots.Remove(snapshot);

        if (BaselineSnapshot == snapshot)
        {
            BaselineSnapshot = Snapshots.FirstOrDefault();
        }

        SelectedSnapshot = Snapshots.LastOrDefault();
        NotifyHeap();
    }

    private void NotifyHeap() => Notify(
        nameof(IsHeapBusy), nameof(CanCaptureHeap), nameof(CanOpenDump), nameof(CanOpenSession), nameof(CanSelectProcess), nameof(CanAttach),
        nameof(HasSnapshot), nameof(HasObjectCapture), nameof(HasSessionData), nameof(CaptureSourceLabel));

    public bool IsReplay => _replay is not null;

    public double ReplayMaximum => _replay?.SessionSeconds ?? 0;

    public double ReplayMinimum => _replay is { } session ? new double?[]
    {
        session.Metrics.FirstOrDefault()?.Seconds,
        session.Collections.MinBy(gc => gc.EndSeconds)?.EndSeconds,
        session.Topology?.StartSeconds
    }.Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0).Min() : 0;

    public double ReplayPosition
    {
        get => _replayPosition;
        set
        {
            if (_replay is null || !double.IsFinite(value) || !Set(ref _replayPosition, Math.Clamp(value, ReplayMinimum, ReplayMaximum)))
            {
                return;
            }

            SelectTimeline(_replayPosition, null, false);
        }
    }

    public Task ImportSessionAsync(string json) => ImportSessionCoreAsync(token => Task.Run(() => SessionStorage.Read(json), token));

    public Task ImportSessionAsync(Stream stream) => ImportSessionCoreAsync(token => Task.Run(() => SessionStorage.ReadAsync(stream, token), token));

    private async Task ImportSessionCoreAsync(Func<CancellationToken, Task<SavedSession>> read)
    {
        if (!CanOpenSession)
        {
            return;
        }

        _sessionLoading = true;
        NotifyConnection();
        NotifyHeap();

        try
        {
            var session = await read(_lifetime.Token);
            await DisconnectAsync();

            if (_disposed)
            {
                return;
            }

            _activeProcess = session.Process;
            _latestLiveFrame = null;
            _replay = session;
            _replayPlaying = false;
            SelectedEvent = null;
            SelectedPauseBin = null;
            GenerationFilterIndex = 0;
            Snapshots.Clear();

            foreach (var snapshot in session.Snapshots)
            {
                Snapshots.Add(snapshot);
            }

            BaselineSnapshot = Snapshots.FirstOrDefault();
            SelectedSnapshot = Snapshots.LastOrDefault();
            ClearTimelineState();
            _frame = RecordedFrame();
            _replayPosition = session.SessionSeconds;
            _isPaused = true;
            RefreshFrame();
            RefreshLiveRegions();
            NotifyHeap();
            NotifyConnection();
            Notify(nameof(IsReplay), nameof(ReplayMaximum), nameof(ReplayMinimum), nameof(PauseLabel), nameof(ReplayPlayLabel));
            Status = "Recording opened.";
        }
        finally
        {
            _sessionLoading = false;
            NotifyConnection();
            NotifyHeap();
        }
    }

    private void ResetHeapSession()
    {
        CancelHeapOperation();
        ClearTimelineState();
        _replay = null;
        _replayPlaying = false;
        Snapshots.Clear();
        BaselineSnapshot = null;
        SelectedSnapshot = null;
        LiveRegionMap.UpdateLive([]);
        HeapStatus = "";
        Notify(nameof(IsReplay), nameof(ReplayMaximum), nameof(ReplayMinimum), nameof(ReplayPlayLabel));
        NotifyHeap();
    }

    private void RefreshLiveRegions()
    {
        var sample = _frame.Topology?.Samples?.LastOrDefault(sample => sample.Seconds <= SessionSeconds);
        LiveRegionMap.UpdateLive(_frame.Regions, sample);
    }
}
