using System.Globalization;
using SatoriInsight.Models;
using SatoriInsight.Services.Processes;
using SatoriInsight.Services.Telemetry;

namespace SatoriInsight.ViewModels;

public sealed partial class MainViewModel
{
    public event Action? RefreshRequested;

    private readonly ILocalProcessDiscovery _processDiscovery;
    private readonly ILiveTelemetrySessionFactory _sessionFactory;
    private readonly ILocalProcessLauncher _processLauncher;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _connectCancellation;
    private ILiveTelemetrySession? _liveSession;
    private DiagnosticFrame? _latestLiveFrame;
    private Task? _attachTask;
    private Task? _stopProcessTask;
    private bool _disposed;
    private bool _isStopping;
    private bool _refreshingProcesses;
    private bool _userCanceledConnection;
    private bool _isLaunching;
    private LocalConnectionState _connectionState = LocalConnectionState.Disconnected;
    private LocalDotNetProcess? _activeProcess;
    private LocalDotNetProcess? _selectedLocalProcess;
    private IReadOnlyList<LocalDotNetProcess> _localProcesses = [];
    private readonly BulkObservableCollection<LocalDotNetProcess> _visibleProcesses = [];
    private string _processSearch = "";
    private string _processDiscoveryStatus = "";

    public LocalConnectionState ConnectionState => _connectionState;

    public bool IsConnected => ConnectionState == LocalConnectionState.Connected;

    public bool IsConnecting => ConnectionState == LocalConnectionState.Connecting;

    public bool CanSelectProcess => !IsConnecting && !_isStopping && !_disposed && !IsHeapBusy && !_sessionLoading;

    public bool CanDisconnect => (IsConnecting || _liveSession is not null) && !_isStopping;

    public bool IsRefreshingProcesses => _refreshingProcesses;

    public bool CanRefreshProcesses => !IsRefreshingProcesses;

    public bool CanAttach => SelectedLocalProcess is not null && CanSelectProcess;

    public string TargetName => ActiveProcess?.Name ?? "No process selected";

    public string TargetMetadata
    {
        get
        {
            if (ActiveProcess is not { } target)
            {
                return "Local .NET diagnostics";
            }

            var runtime = _latestLiveFrame?.Health?.RuntimeVersion is { } version ? $".NET {version}" : "Local .NET process";
            return $"PID {target.ProcessId} · {runtime}";
        }
    }

    public string SourceBadge
    {
        get
        {
            if (IsReplay)
            {
                return "REPLAY";
            }

            if (IsConnected)
            {
                return "LIVE";
            }

            if (IsConnecting)
            {
                return _isLaunching ? "LAUNCHING" : "CONNECTING";
            }

            return HasCollectedData ? "RECORDED" : "NOT CONNECTED";
        }
    }

    public string HeapCountLabel => _frame.Metrics?.HeapCount?.ToString(CultureInfo.InvariantCulture) ?? "—";

    public string ProcessButtonLabel => IsConnected ? "Change process" : "Attach process";

    public bool HasLaunchedProcess => _liveSession is ILaunchedTelemetrySession;

    public string LaunchProcessButtonLabel => HasLaunchedProcess ? "Stop process" : "Launch process";

    public string DisconnectButtonLabel
    {
        get
        {
            if (!IsConnecting)
            {
                return "Disconnect";
            }

            return _isLaunching ? "Cancel launch" : "Cancel connection";
        }
    }

    public string LiveFragmentationLabel => _frame.Metrics?.FragmentationPercent is { } percent ? $"{percent:0.0}%" : "—";

    public bool ShowStateNotice => !IsConnected;

    public string StateNoticeTitle => ConnectionState switch
    {
        LocalConnectionState.Connecting => _isLaunching ? "Launching" : "Connecting",
        LocalConnectionState.Failed => "Connection failed",
        _ => HasCollectedData ? "Recorded session" : "Choose a .NET process"
    };

    public string StateNoticeMessage => IsConnecting || ConnectionState == LocalConnectionState.Failed || HasCollectedData
        ? Status
        : "Launch an application or attach to a running process.";

    public bool ShowQualityNotice => _frame.Health?.IsIncomplete == true || !IsReplay && _frame.Health?.LastCounterSeconds is { } at && SessionSeconds - at > 5;

    public string QualityMessage
    {
        get
        {
            var health = _frame.Health;

            if (health is null)
            {
                return "";
            }

            var details = new List<string>();

            if (health.EventsLost > 0)
            {
                details.Add($"{health.EventsLost:N0} events were lost during recording. Results may be incomplete.");
            }

            if (health.DiscardedCollections > 0)
            {
                details.Add($"{health.DiscardedCollections:N0} collections could not be read.");
            }

            if (!IsReplay && health.LastCounterSeconds is { } at && SessionSeconds - at > 5)
            {
                details.Add("Measurements have stopped updating.");
            }

            return string.Join(" ", details);
        }
    }

    public IReadOnlyList<GenerationSizeViewModel> GenerationSizes
    {
        get
        {
            var metrics = _frame.Metrics ?? new();
            var sizes = new[]
            {
                metrics.Gen0Bytes,
                metrics.Gen1Bytes,
                metrics.Gen2Bytes,
                metrics.LohBytes,
                metrics.PohBytes
            };
            var maximum = Math.Max(1, sizes.Max() ?? 1);
            return sizes.Select((bytes, index) => new GenerationSizeViewModel((HeapGeneration)index, bytes, maximum)).ToArray();
        }
    }

    public LocalDotNetProcess? ActiveProcess => _activeProcess;

    public LocalDotNetProcess? SelectedLocalProcess
    {
        get => _selectedLocalProcess;
        set
        {
            if (Set(ref _selectedLocalProcess, value))
            {
                Notify(nameof(CanAttach));
            }
        }
    }

    public string ProcessSearch
    {
        get => _processSearch;
        set
        {
            if (!Set(ref _processSearch, value))
            {
                return;
            }

            RefreshProcessList();
        }
    }

    public IReadOnlyList<LocalDotNetProcess> LocalProcesses => _visibleProcesses;

    private void RefreshProcessList()
    {
        var search = ProcessSearch.Trim();
        var visible = _localProcesses.Where(process => process.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || process.ProcessId.ToString(CultureInfo.InvariantCulture).Contains(search, StringComparison.Ordinal)).ToArray();
        var selected = SelectedLocalProcess is { } prior ? visible.FirstOrDefault(process => process.ProcessId == prior.ProcessId
            && process.StartTimeUtc == prior.StartTimeUtc) : null;
        // Let the grid's reset update the bound selection before restoring it.
        // Ignoring those writes can leave its selected row out of sync with CanAttach.
        _visibleProcesses.ReplaceAll(visible);
        SelectedLocalProcess = selected;
        Notify(nameof(SelectedLocalProcess), nameof(ProcessCountLabel));
    }

    public string ProcessCountLabel => $"{LocalProcesses.Count} local .NET processes";

    public string ProcessDiscoveryStatus
    {
        get => _processDiscoveryStatus;
        private set => Set(ref _processDiscoveryStatus, value);
    }

    public async Task RefreshProcessesAsync()
    {
        if (_refreshingProcesses || _disposed)
        {
            return;
        }

        _refreshingProcesses = true;
        Notify(nameof(IsRefreshingProcesses), nameof(CanRefreshProcesses));

        try
        {
            _localProcesses = await _processDiscovery.ListAsync(_lifetime.Token);
            RefreshProcessList();
            ProcessDiscoveryStatus = _localProcesses.Count == 0 ? "No accessible .NET processes found. Start an application and refresh." : "";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ProcessDiscoveryStatus = $"Process discovery failed: {exception.Message}";
        }
        finally
        {
            _refreshingProcesses = false;
            Notify(nameof(IsRefreshingProcesses), nameof(CanRefreshProcesses));
        }
    }

    public Task AttachAsync(LocalDotNetProcess target)
    {
        if (!CanSelectProcess)
        {
            return Task.CompletedTask;
        }

        return _attachTask = ConnectCoreAsync(target, token => _sessionFactory.StartAsync(target, token));
    }

    public Task LaunchAsync(ProcessLaunchOptions options)
    {
        if (!CanSelectProcess || HasLaunchedProcess)
        {
            return Task.CompletedTask;
        }

        return _attachTask = ConnectCoreAsync(null, async token => await _processLauncher.LaunchAsync(options, token));
    }

    public Task StopProcessAsync()
    {
        if (!CanSelectProcess || _liveSession is not ILaunchedTelemetrySession session)
        {
            return Task.CompletedTask;
        }

        return _stopProcessTask = StopProcessCoreAsync(session);
    }

    private async Task StopProcessCoreAsync(ILaunchedTelemetrySession session)
    {
        var processStopped = false;
        _isStopping = true;
        NotifyConnection();
        Status = $"Stopping {session.Target.DisplayLabel}…";

        try
        {
            await session.StopProcessAsync();
            processStopped = true;
            await FinishLiveSessionAsync(session);
            Status = "Process stopped. The recording is available to inspect or save.";
        }
        catch (Exception exception)
        {
            Status = processStopped
                ? $"Process stopped, but could not finish recording: {exception.Message}"
                : $"Could not stop process: {exception.Message}";
        }
        finally
        {
            _isStopping = false;
            NotifyConnection();

            if (session.Completion.IsCompleted)
            {
                RequestRefresh();
            }
        }
    }

    private async Task ConnectCoreAsync(LocalDotNetProcess? target, Func<CancellationToken, Task<ILiveTelemetrySession>> connect)
    {
        await DisconnectAsync();

        if (_disposed || _liveSession is not null)
        {
            return;
        }

        _activeProcess = target;
        _frame = EmptyFrame();
        _latestLiveFrame = _frame;
        ResetHeapSession();
        SelectedEvent = null;
        SelectedPauseBin = null;
        GenerationFilterIndex = 0;
        _isPaused = false;
        _userCanceledConnection = false;
        _isLaunching = target is null;
        _connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _connectCancellation.CancelAfter(TimeSpan.FromSeconds(_isLaunching ? 30 : 8));
        SetConnectionState(LocalConnectionState.Connecting);
        Status = _isLaunching ? "Launching application…" : $"Connecting to {target!.DisplayLabel}…";

        try
        {
            var session = await connect(_connectCancellation.Token);

            if (_disposed || _connectCancellation.IsCancellationRequested)
            {
                await session.DisposeAsync();
                SetConnectionState(LocalConnectionState.Disconnected);
                Status = _isLaunching ? "Launch canceled." : "Connection canceled.";
                return;
            }

            if (session is ILaunchedTelemetrySession launched)
            {
                launched.Accept();
            }

            _activeProcess = session.Target;
            _liveSession = session;
            session.FrameReady += RequestRefresh;
            ApplyLiveFrame(session.Read());
            SetConnectionState(LocalConnectionState.Connected);
            Status = $"Connected to {session.Target.DisplayLabel}.";

            if (session.Completion.IsCompleted)
            {
                RequestRefresh();
            }
        }
        catch (OperationCanceledException)
        {
            SetConnectionState(LocalConnectionState.Disconnected);
            if (_userCanceledConnection)
            {
                Status = _isLaunching ? "Launch canceled." : "Connection canceled.";
            }
            else if (_isLaunching)
            {
                Status = "Launch timed out. Check that the application supports .NET diagnostics.";
            }
            else
            {
                Status = "Connection timed out. Refresh the process list and retry.";
            }
        }
        catch (Exception exception)
        {
            SetConnectionState(LocalConnectionState.Failed);
            Status = _isLaunching ? $"Could not launch: {exception.Message}" : $"Could not attach to {target!.DisplayLabel}: {exception.Message}";
        }
        finally
        {
            _connectCancellation.Dispose();
            _connectCancellation = null;
            _isLaunching = false;
            NotifyConnection();
        }
    }

    public async Task DisconnectAsync()
    {
        if (_stopProcessTask is { IsCompleted: false } stopping)
        {
            await stopping;
        }

        if (IsConnecting)
        {
            _userCanceledConnection = true;
            _connectCancellation?.Cancel();

            if (_attachTask is { } attach)
            {
                await attach;
            }
        }

        if (_liveSession is not { } session || _isStopping)
        {
            return;
        }

        _isStopping = true;
        NotifyConnection();

        try
        {
            await FinishLiveSessionAsync(session);
            Status = "Disconnected. The recording is available to inspect or save.";
        }
        catch (Exception exception)
        {
            SetConnectionState(LocalConnectionState.Failed);
            Status = $"Could not disconnect: {exception.Message}";
        }
        finally
        {
            _isStopping = false;
            NotifyConnection();
        }
    }

    private async Task FinishLiveSessionAsync(ILiveTelemetrySession session)
    {
        session.FrameReady -= RequestRefresh;

        try
        {
            await session.DisposeAsync();
        }
        finally
        {
            _liveSession = null;
            SetConnectionState(LocalConnectionState.Disconnected);
            _latestLiveFrame = session.Read();

            if (!IsPaused)
            {
                ApplyLiveFrame(_latestLiveFrame);
            }
        }
    }

    public void Tick()
    {
        if (TickReplay())
        {
            return;
        }

        if (_liveSession is not { } session)
        {
            return;
        }

        _latestLiveFrame = session.Read();

        if (!IsPaused)
        {
            ApplyLiveFrame(_latestLiveFrame);
        }

        if (session.Completion.IsCompleted && !_isStopping)
        {
            var end = session.Completion.IsCompletedSuccessfully
                ? session.Completion.Result
                : new(false, session.Completion.Exception?.GetBaseException().Message);
            session.FrameReady -= RequestRefresh;
            _liveSession = null;
            SetConnectionState(end.Error is null ? LocalConnectionState.Disconnected : LocalConnectionState.Failed);
            Status = end.TargetExited ? "Process exited. The recording is available." : end.Error is not null
                ? $"Recording stopped: {end.Error}"
                : "Recording ended. Collected data is available.";
            _ = DisposeEndedSessionAsync(session);
        }
        else if (!IsPaused)
        {
            Status = $"{ActiveProcess!.DisplayLabel} · {_frame.Events.Count:N0} collections";
        }

        NotifyConnection();
    }

    private async Task DisposeEndedSessionAsync(ILiveTelemetrySession session)
    {
        try
        {
            await session.DisposeAsync();
        }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                Status = $"Could not finish recording: {exception.Message}";
            }
        }
    }

    private void RequestRefresh() => RefreshRequested?.Invoke();

    private void ApplyLiveFrame(DiagnosticFrame frame)
    {
        _frame = frame;
        _latestLiveFrame = frame;
        RefreshFrame();

        RefreshLiveRegions();
        NotifyHeap();
    }

    private void SetConnectionState(LocalConnectionState state)
    {
        _connectionState = state;
        NotifyConnection();
        RefreshFrame();
    }

    private void NotifyConnection() => Notify(
        nameof(ConnectionState), nameof(IsConnected), nameof(IsConnecting), nameof(CanSelectProcess), nameof(CanDisconnect), nameof(CanAttach),
        nameof(CanPause), nameof(HasCollectedData), nameof(TargetName), nameof(TargetMetadata), nameof(SourceBadge), nameof(HeapCountLabel),
        nameof(ProcessButtonLabel), nameof(DisconnectButtonLabel), nameof(ShowQualityNotice), nameof(QualityMessage), nameof(ShowStateNotice),
        nameof(HasLaunchedProcess), nameof(LaunchProcessButtonLabel),
        nameof(StateNoticeTitle), nameof(StateNoticeMessage), nameof(StreamLabel), nameof(PauseLabel), nameof(CanCaptureHeap), nameof(CanOpenSession));

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        await _rootSearchTask;

        if (_heapTask is { } heapTask)
        {
            await heapTask;
        }

        await DisconnectAsync();
        _lifetime.Dispose();
    }
}

public sealed record GenerationSizeViewModel(HeapGeneration Generation, double? Bytes, double Maximum)
{
    public string Label => Generation.ToString();

    public string SizeLabel => Bytes is { } bytes ? Format.Bytes((long)bytes) : "—";

    public double RelativeSize => Bytes is { } bytes ? bytes / Maximum * 100 : 0;
}
