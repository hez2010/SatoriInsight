using System.Diagnostics;
using SatoriInsight.Models;
using SatoriInsight.Services.Processes;
using SatoriInsight.Services.Recording;
using SatoriInsight.Services.Telemetry;
using SatoriInsight.ViewModels;
using Xunit;

namespace SatoriInsight.Tests;

public class ProcessMetricsTests
{
    private const long MiB = 1_048_576;

    private static ProcessMetricPoint[] Measurements() =>
    [
        new(0, 10, 100, 40 * MiB), new(2, 12, 102, 48 * MiB),
        new(4, 18, 104, 60 * MiB), new(6, 20, 106, 72 * MiB), new(8, 26, 108, 80 * MiB)
    ];

    private static SavedSession Session(ProcessMetricPoint[]? measurements = null) =>
        new(SessionStorage.CurrentSchemaVersion, "local-eventpipe", null, 8, [], [], null, null, [], [], [], null, null, measurements ?? Measurements(), []);

    [Fact]
    public void SamplesAreRetainedIndependentlyOfHeapCounters()
    {
        var buffer = new LiveTelemetryBuffer();

        for (var i = 0; i <= 700; i++)
        {
            buffer.ProcessSample(new(i, i * 2, 100 + i, (i + 1) * MiB));
        }

        buffer.ProcessSample(new(699, 2000, 1000, MiB)); // Delayed sample must not reorder history.
        buffer.ProcessSample(new(701, double.NaN, 801, MiB));
        var frame = buffer.Read(702);

        Assert.Empty(frame.History);
        Assert.Equal(701, frame.ProcessHistory!.Count);
        Assert.Equal(0, frame.ProcessHistory[0].Seconds);
        Assert.Equal(1400, frame.ProcessHistory[^1].CpuSeconds);
        Assert.Equal(701, frame.ProcessHistory[^1].ResidentMiB);
    }

    [Fact]
    public async Task TimePointsAndRangesRestoreProcessStatsAndExportTheFullHistory()
    {
        await using var vm = new MainViewModel();
        await vm.ImportSessionAsync(SessionStorage.Write(Session()));

        Assert.True(vm.HasSessionData);
        Assert.True(vm.HasHistory);
        Assert.Equal("00:00:26.000", vm.CpuTimeLabel);
        Assert.Equal("00:01:48.000", vm.WallClockTimeLabel);
        Assert.Equal("80.0 MiB", vm.ResidentMemoryLabel);

        vm.SelectTimePoint(3);
        Assert.Equal("00:00:12.000", vm.CpuTimeLabel);
        Assert.Equal("00:01:42.000", vm.WallClockTimeLabel);
        Assert.Equal("48.0 MiB", vm.ResidentMemoryLabel);
        Assert.Equal("Recorded peak: 48.0 MiB", vm.PeakResidentMemoryLabel);
        Assert.Equal(5, vm.ProcessHistory.Count);

        vm.SelectPeriod(5, 1);
        Assert.Equal("≈ 00:00:08.000", vm.CpuTimeLabel);
        Assert.Equal("00:00:04.000", vm.WallClockTimeLabel);
        Assert.Equal("60.0 MiB", vm.ResidentMemoryLabel);
        Assert.Equal("Peak in selection: 60.0 MiB", vm.PeakResidentMemoryLabel);
        Assert.Equal("Selected period", vm.ProcessTimeCaption);

        using var saved = new MemoryStream();
        await vm.ExportSessionAsync(saved);
        saved.Position = 0;
        var restored = await SessionStorage.ReadAsync(saved, TestContext.Current.CancellationToken);
        Assert.Equal(Measurements(), restored.ProcessMetrics);

        vm.ResetTimelineSelection();
        Assert.Equal("00:00:26.000", vm.CpuTimeLabel);
        Assert.Equal("80.0 MiB", vm.ResidentMemoryLabel);
        Assert.Equal("Since process start", vm.ProcessTimeCaption);
    }

    [Fact]
    public async Task MissingMeasurementsStayUnavailableAndCpuIsNotExtrapolated()
    {
        var history = Measurements();
        history[2] = history[2] with
        {
            CpuSeconds = null,
            ResidentBytes = null
        };
        Assert.Null(ProcessStatistics.CpuTimeInRange(history, 3, 5));
        Assert.Null(ProcessStatistics.CpuTimeInRange(history, -1, 2));
        Assert.Null(ProcessStatistics.CpuTimeInRange(history, 6, 9));

        await using var vm = new MainViewModel();
        await vm.ImportSessionAsync(SessionStorage.Write(Session(history)));
        vm.SelectTimePoint(4);
        Assert.Equal("—", vm.CpuTimeLabel);
        Assert.Equal("—", vm.ResidentMemoryLabel);

        vm.SelectPeriod(3, 5);
        Assert.Equal("—", vm.CpuTimeLabel);
        Assert.Equal("00:00:02.000", vm.WallClockTimeLabel);
        Assert.Contains("Not enough", vm.CpuTimeTooltip);

        await vm.ImportSessionAsync(SessionStorage.Write(Session([])));
        Assert.Equal("—", vm.CpuTimeLabel);
        Assert.Equal("—", vm.WallClockTimeLabel);
        Assert.Equal("—", vm.ResidentMemoryLabel);
    }

    [Fact]
    public async Task PausingTheViewKeepsProcessStatsStableWhileRecordingContinues()
    {
        var session = new MetricsSession
        {
            Frame = new(4, [], [], [], null, ProcessHistory: Measurements().Take(3).ToArray())
        };
        await using var vm = new MainViewModel(sessionFactory: session);
        await vm.AttachAsync(TestData.Target);
        vm.IsPaused = true;

        session.Frame = new(8, [], [], [], null, ProcessHistory: Measurements());
        vm.Tick();

        Assert.Equal("00:00:18.000", vm.CpuTimeLabel);
        Assert.Equal("60.0 MiB", vm.ResidentMemoryLabel);
        Assert.Equal(5, SessionStorage.Read(vm.ExportJson()).ProcessMetrics!.Length);

        vm.IsPaused = false;
        Assert.Equal("00:00:26.000", vm.CpuTimeLabel);
        Assert.Equal("80.0 MiB", vm.ResidentMemoryLabel);

        vm.SelectTimePoint(2);
        Assert.Equal("48.0 MiB", vm.ResidentMemoryLabel);
        vm.ResetTimelineSelection();
        Assert.Equal("80.0 MiB", vm.ResidentMemoryLabel);
    }

    [Fact]
    public void InvalidOrOutOfOrderMeasurementsAreRejectedOnImport()
    {
        ProcessMetricPoint[][] invalid =
        [
            [new(1, -1, 1, MiB)], [new(1, 1, -1, MiB)], [new(1, 1, 1, -1)],
            [new(9, 1, 1, MiB)], [new(2, 1, 2, MiB), new(1, 1, 1, MiB)],
            [new(1, 2, 2, MiB), new(2, 1, 3, MiB)],
            [new(1, 2, 2, MiB), new(2, 3, 1, MiB)],
            [new(1, 1, double.MaxValue, MiB)]
        ];

        foreach (var history in invalid)
        {
            Assert.Throws<InvalidDataException>(() => SessionStorage.Read(SessionStorage.Write(Session(history))));
        }

        // Multiple CPU threads can consume more CPU time than elapsed wall time.
        Assert.NotNull(SessionStorage.Read(SessionStorage.Write(Session([new(1, 12, 2, MiB)]))));
    }

    [Fact]
    public void ReaderUsesRealProcessTimesAndResidentMemory()
    {
        using var process = Process.GetCurrentProcess();
        var target = new LocalDotNetProcess(process.Id, process.ProcessName, process.StartTime.ToUniversalTime());
        using var reader = new ProcessMetricReader(target);
        var first = Assert.IsType<ProcessMetricPoint>(reader.Read(1));
        var second = Assert.IsType<ProcessMetricPoint>(reader.Read(2));

        Assert.True(first.ResidentBytes > 0);
        Assert.True(first.CpuSeconds >= 0);
        Assert.True(second.CpuSeconds >= first.CpuSeconds);
        Assert.True(second.WallClockSeconds >= first.WallClockSeconds);
        Assert.Throws<InvalidOperationException>(() => new ProcessMetricReader(target with { StartTimeUtc = target.StartTimeUtc.AddSeconds(1) }));
    }

    private sealed class MetricsSession : ILiveTelemetrySession, ILiveTelemetrySessionFactory
    {
        private readonly TaskCompletionSource<CollectionEnd> _end = new();

        public DiagnosticFrame Frame { get; set; } = null!;

        public LocalDotNetProcess Target => TestData.Target;

        public Task<CollectionEnd> Completion => _end.Task;

        public DiagnosticFrame Read() => Frame;

        public Task<ILiveTelemetrySession> StartAsync(LocalDotNetProcess target, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ILiveTelemetrySession>(this);
        }

        public ValueTask DisposeAsync()
        {
            _end.TrySetResult(new(false));
            return ValueTask.CompletedTask;
        }
    }
}
