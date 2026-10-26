using System.Text.Json;
using SatoriInsight.Models;
using SatoriInsight.Services.Processes;
using SatoriInsight.Services.Recording;
using SatoriInsight.Services.Telemetry;
using SatoriInsight.ViewModels;
using Xunit;

namespace SatoriInsight.Tests;

public class ConnectionTests
{
    [Fact]
    public async Task TimelineSelectionDoesNotStopCollectionAndResetReturnsToLatestFrame()
    {
        var session = new Session(TestData.Target, TestData.Frame(5) with
        {
            History = [new(1, 1, 2, 3), new(5, 5, 6, 7)]
        });
        await using var vm = new MainViewModel(sessionFactory: new Factory(session));
        await vm.AttachAsync(TestData.Target);
        vm.SelectTimePoint(1.5);
        Assert.True(vm.IsConnected);
        Assert.True(vm.IsPaused);
        Assert.Equal(1.5, vm.SessionSeconds);
        Assert.Equal(5, vm.ChartMaximumSeconds);
        session.Frame = TestData.Frame(8);
        vm.Tick();
        Assert.Equal(1.5, vm.SessionSeconds);
        vm.ResetTimelineSelection();
        Assert.False(vm.IsPaused);
        Assert.False(vm.HasTimelineSelection);
        Assert.Equal(8, vm.SessionSeconds);
        Assert.True(vm.IsConnected);
    }

    [Fact]
    public async Task StartsDisconnectedWithNoFabricatedMetrics()
    {
        await using var vm = new MainViewModel();
        Assert.Equal(LocalConnectionState.Disconnected, vm.ConnectionState);
        Assert.Empty(vm.History);
        Assert.Empty(vm.Events);
        Assert.Equal("—", vm.UsedLabel);
        Assert.Equal("—", vm.P99Label);
        Assert.False(vm.HasSessionData);
        Assert.False(vm.CanPause);
    }

    [Fact]
    public async Task ProcessSearchSupportsNameAndPidAndClearsHiddenSelection()
    {
        var discovery = new Discovery([TestData.Target, TestData.Target with { ProcessId = 5678, Name = "OtherService" }]);
        await using var vm = new MainViewModel(discovery);
        await vm.RefreshProcessesAsync();
        vm.SelectedLocalProcess = TestData.Target;
        Assert.True(vm.CanAttach);
        vm.ProcessSearch = "5678";
        Assert.Equal("OtherService", Assert.Single(vm.LocalProcesses).Name);
        Assert.Null(vm.SelectedLocalProcess);
        vm.ProcessSearch = "testtarget";
        Assert.Equal(TestData.Target, Assert.Single(vm.LocalProcesses));
    }

    [Fact]
    public async Task PauseFreezesPresentationWhileSessionContinuesAndResumeReadsLatest()
    {
        var session = new Session(TestData.Target, TestData.Frame(2));
        await using var vm = new MainViewModel(sessionFactory: new Factory(session));
        await vm.AttachAsync(TestData.Target);
        Assert.Equal("1.0 MiB", vm.UsedLabel);
        vm.IsPaused = true;
        session.Frame = TestData.Frame(5) with
        {
            Health = new(EventsLost: 99)
        };
        vm.Tick();
        Assert.Equal(2, vm.SessionSeconds);
        Assert.True(vm.IsConnected);
        Assert.DoesNotContain("99", vm.QualityMessage);
        Assert.Equal(5, SessionStorage.Read(vm.ExportJson()).SessionSeconds);
        Assert.Equal(2, vm.SessionSeconds);
        vm.IsPaused = false;
        Assert.Contains("99", vm.QualityMessage);
        Assert.Equal(5, vm.SessionSeconds);
        Assert.Equal(5, Assert.Single(vm.Events).PauseMilliseconds);
    }

    [Fact]
    public async Task AViewPausedBeforeTheFirstCounterCanResumeAfterTargetExit()
    {
        var session = new Session(TestData.Target, new(0, [], [], [], null, new(), new(), []));
        await using var vm = new MainViewModel(sessionFactory: new Factory(session));
        await vm.AttachAsync(TestData.Target);
        vm.IsPaused = true;
        session.Frame = TestData.Frame(5);
        session.End.SetResult(new(true));
        vm.Tick();
        Assert.False(vm.IsConnected);
        Assert.False(vm.HasSessionData);
        Assert.True(vm.HasCollectedData);
        Assert.True(vm.CanPause);
        vm.IsPaused = false;
        Assert.Equal(5, vm.SessionSeconds);
        Assert.True(vm.HasSessionData);
    }

    [Fact]
    public async Task ReconnectDisposesOldSessionAndClearsItsEventsAndSelection()
    {
        var secondTarget = TestData.Target with
        {
            ProcessId = 5678
        };
        var first = new Session(TestData.Target, TestData.Frame(2));
        var second = new Session(secondTarget, new(0, [], [], [], null, new(), new(), []));
        await using var vm = new MainViewModel(sessionFactory: new Factory(first, second));
        await vm.AttachAsync(TestData.Target);
        vm.SelectedEvent = vm.Events[0];
        vm.FilterToPauseBin(vm.Histogram[3]);
        await vm.AttachAsync(secondTarget);
        Assert.True(first.Disposed);
        Assert.Empty(vm.Events);
        Assert.Empty(vm.History);
        Assert.Null(vm.SelectedEvent);
        Assert.Null(vm.SelectedPauseBin);
        Assert.Equal(secondTarget, vm.ActiveProcess);
        Assert.Equal("—", vm.UsedLabel);
    }

    [Fact]
    public async Task AttachFailureLeavesAnEmptyRetryableState()
    {
        await using var vm = new MainViewModel(sessionFactory: new FailureFactory());
        await vm.AttachAsync(TestData.Target);
        Assert.Equal(LocalConnectionState.Failed, vm.ConnectionState);
        Assert.True(vm.CanSelectProcess);
        Assert.Empty(vm.Events);
        Assert.Equal("—", vm.UsedLabel);
        Assert.Contains("Could not attach", vm.Status);
    }

    [Fact]
    public async Task ExitAndDisconnectKeepHistoricalDataAndLiveExportProvenance()
    {
        var session = new Session(TestData.Target, TestData.Frame(2));
        await using var vm = new MainViewModel(sessionFactory: new Factory(session));
        await vm.AttachAsync(TestData.Target);
        session.End.TrySetResult(new(true));
        vm.Tick();
        Assert.False(vm.IsConnected);
        Assert.True(vm.HasSessionData);
        Assert.Contains("exited", vm.Status);
        using var json = JsonDocument.Parse(vm.ExportJson());
        Assert.Equal("local-eventpipe", json.RootElement.GetProperty("Source").GetString());
        Assert.Equal(1234, json.RootElement.GetProperty("Process").GetProperty("ProcessId").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("MapSnapshot", out _));
    }

    [Fact]
    public async Task CommandLineTooltipDoesNotBecomePartOfExportedProcessMetadata()
    {
        var target = TestData.Target with
        {
            CommandLine = "dotnet TestData.Target.dll --private-value example"
        };
        await using var vm = new MainViewModel(sessionFactory: new Factory(new Session(target, TestData.Frame(2))));
        await vm.AttachAsync(target);
        Assert.Equal(target.CommandLine, target.CommandLineTooltip);
        using var json = JsonDocument.Parse(vm.ExportJson());
        Assert.False(json.RootElement.GetProperty("Process").TryGetProperty("CommandLine", out _));
        Assert.False(json.RootElement.GetProperty("Process").TryGetProperty("CommandLineTooltip", out _));
    }

    [Fact]
    public async Task CancelAfterAFactoryReturnsStillDisposesSessionAndLeavesConnectingState()
    {
        var factory = new DeferredFactory();
        await using var vm = new MainViewModel(sessionFactory: factory);
        var attach = vm.AttachAsync(TestData.Target);
        Assert.True(vm.IsConnecting);
        var disconnect = vm.DisconnectAsync();
        var session = new Session(TestData.Target, TestData.Frame(2));
        factory.Ready.SetResult(session);
        await Task.WhenAll(attach, disconnect);
        Assert.True(session.Disposed);
        Assert.Equal(LocalConnectionState.Disconnected, vm.ConnectionState);
        Assert.True(vm.CanSelectProcess);
    }

    [Fact]
    public async Task HistogramDrilldownKeepsFullPausePopulation()
    {
        await using var vm = new MainViewModel(sessionFactory: new Factory(new Session(TestData.Target, TestData.Frame(2))));
        await vm.AttachAsync(TestData.Target);
        vm.NavigationIndex = 1;
        Assert.True(vm.IsHeapInspectorView);

        vm.FilterToPauseBin(vm.Histogram[3]);
        Assert.True(vm.IsMemoryActivityView);
        Assert.Equal("Memory activity", vm.PageTitle);
        Assert.Single(vm.EventLog);
        Assert.Single(vm.Events);
        vm.ClearPauseFilterCommand.Execute(null);
        Assert.False(vm.HasPauseFilter);
    }

    private sealed class Discovery(IReadOnlyList<LocalDotNetProcess> targets) : ILocalProcessDiscovery
    {
        public Task<IReadOnlyList<LocalDotNetProcess>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult(targets);
    }

    private sealed class Factory(params Session[] sessions) : ILiveTelemetrySessionFactory
    {
        private readonly Queue<Session> _queue = new(sessions);

        public Task<ILiveTelemetrySession> StartAsync(LocalDotNetProcess target, CancellationToken cancellationToken = default) => Task.FromResult<ILiveTelemetrySession>(_queue.Dequeue());
    }

    private sealed class FailureFactory : ILiveTelemetrySessionFactory
    {
        public Task<ILiveTelemetrySession> StartAsync(LocalDotNetProcess target, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Endpoint not available");
    }

    private sealed class DeferredFactory : ILiveTelemetrySessionFactory
    {
        public TaskCompletionSource<ILiveTelemetrySession> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ILiveTelemetrySession> StartAsync(LocalDotNetProcess target, CancellationToken cancellationToken = default) => Ready.Task;
    }

    private sealed class Session(LocalDotNetProcess target, DiagnosticFrame frame) : ILiveTelemetrySession
    {
        public LocalDotNetProcess Target => target;

        public DiagnosticFrame Frame { get; set; } = frame;

        public bool Disposed { get; private set; }

        public TaskCompletionSource<CollectionEnd> End { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CollectionEnd> Completion => End.Task;

        public DiagnosticFrame Read()
        {
            return Frame;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            End.TrySetResult(new(false));
            return ValueTask.CompletedTask;
        }
    }
}
