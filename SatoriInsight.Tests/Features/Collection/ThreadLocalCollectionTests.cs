using SatoriInsight.Models;
using SatoriInsight.Services.Recording;
using SatoriInsight.Services.Telemetry;
using SatoriInsight.ViewModels;
using Xunit;

namespace SatoriInsight.Tests;

public class ThreadLocalCollectionTests
{
    private static GcEvent Local(double at = 2, bool collected = true) => new(7, at, 0, "ThreadLocal", collected ? "Collected" : "Skipped", 0.0125, 0, 0,
        new ThreadLocalGcInfo(3, 123, collected, 2097152, 524288, 12500));

    private static GcEvent Global(int gen, double at = 1) => new(1, at, gen, "Blocking", "Test", 9, 5, 1);

    private static SavedSession Save(DiagnosticFrame f) => new(
        SessionStorage.CurrentSchemaVersion, "local-eventpipe", null, f.AtSeconds, f.History.ToArray(), f.Events.ToArray(), f.Metrics, f.Health,
        f.PauseCounters!.ToArray(), f.Regions.ToArray(), [], f.Topology, f.GcCounts, f.ProcessHistory!.ToArray(), f.GcModes!.ToArray());

    [Fact]
    public void ThreadLocalFloodKeepsAllDetailsGlobalPausesAndGenerationTotals()
    {
        var buffer = new LiveTelemetryBuffer();

        for (var gen = 0; gen < 3; gen++)
        {
            buffer.GcEnd(Global(gen));
        }

        const int localCount = 10020;
        var beforeFlood = buffer.Read(1);

        for (var i = 0; i < localCount; i++)
        {
            buffer.ThreadLocalCollection(Local(2 + i / 100000d));
        }

        var f = buffer.Read(3);
        Assert.Equal(new GcCollectionCounts(localCount + 1, 1, 1), f.GcCounts);
        Assert.Equal(localCount, f.Events.Count(e => e.IsThreadLocal));
        Assert.Equal(3, beforeFlood.Events.Count);
        Assert.Equal(3, f.Events.Count(e => !e.IsThreadLocal));
        Assert.Equal(3, GcStatistics.Histogram(f.Events).Sum(bin => bin.Count));
        Assert.Equal(5, GcStatistics.Percentile(f.Events, 50));
        Assert.Equal(5, GcStatistics.Percentile(f.Events, 99.9));
        Assert.False(f.Health!.IsIncomplete);
        var expired = buffer.Read(400);
        Assert.Equal(f.Events.Count, expired.Events.Count);
        Assert.Equal(f.GcCounts, expired.GcCounts);
        Assert.Equal(f.GcCounts, SessionStorage.Read(SessionStorage.Write(Save(f))).GcCounts);
    }

    [Fact]
    public async Task ReplayShowsGen0ThreadLocalWithoutChangingPausePopulationOrCounters()
    {
        var buffer = new LiveTelemetryBuffer();
        buffer.GcEnd(Global(1));
        buffer.Counter(new("gc-heap-size", 1000), 1.2);
        buffer.ThreadLocalCollection(Local());
        buffer.Counter(new("gc-heap-size", 2000), 2.2);
        await using var vm = new MainViewModel();
        await vm.ImportSessionAsync(SessionStorage.Write(Save(buffer.Read(3))));
        Assert.Equal("1", vm.Gen0CountLabel);
        Assert.Equal("1", vm.Gen1CountLabel);
        Assert.Equal("0", vm.Gen2CountLabel);
        Assert.Equal("5.00 ms", vm.P999Label);
        Assert.Equal("5.00 ms", vm.MaxPauseLabel);
        vm.GenerationFilterIndex = 1;
        vm.SelectedEvent = Assert.Single(vm.EventLog);
        Assert.Equal("ThreadLocal", vm.EventMode);
        Assert.Equal("—", vm.EventPause);
        Assert.Equal("—", vm.EventSuspend);
        Assert.Contains("Thread 123", vm.EventTitle);
        Assert.Equal("2.0 MiB", vm.EventBefore);
        vm.FilterToPauseBin(vm.Histogram.Single(bin => bin.Count == 1));
        Assert.False(Assert.Single(vm.EventLog).IsThreadLocal);
        vm.ReplayPosition = 1.5;
        Assert.Equal("0", vm.Gen0CountLabel);
        Assert.Equal("1", vm.Gen1CountLabel);
        var exported = SessionStorage.Read(vm.ExportJson());
        Assert.Equal(1, exported.GcCounts!.Gen0);
        Assert.Equal(Local().ThreadLocal, exported.Collections.Single(e => e.IsThreadLocal).ThreadLocal);
    }

    [Fact]
    public async Task LocalOnlySessionHasGen0CountsAndNoPauseSamples()
    {
        var buffer = new LiveTelemetryBuffer();
        buffer.ThreadLocalCollection(Local(collected: false));
        await using var vm = new MainViewModel();
        await vm.ImportSessionAsync(SessionStorage.Write(Save(buffer.Read(3))));
        Assert.Equal("1", vm.Gen0CountLabel);
        Assert.False(vm.HasPauses);
        Assert.Equal("—", vm.P999Label);
        Assert.Equal("—", vm.MaxPauseLabel);
        Assert.All(vm.Histogram, bin => Assert.Equal(0, bin.Count));
    }

    [Fact]
    public void ImportRejectsFabricatedThreadLocalPauseOrInconsistentDuration()
    {
        var buffer = new LiveTelemetryBuffer();
        buffer.ThreadLocalCollection(Local());
        var session = Save(buffer.Read(3));
        Assert.Throws<InvalidDataException>(() => SessionStorage.Read(SessionStorage.Write(session with { Collections = [Local() with { PauseMilliseconds = 5 }] })));
        Assert.Throws<InvalidDataException>(() => SessionStorage.Read(SessionStorage.Write(session with { Collections = [Local() with { Generation = 2 }] })));
        Assert.Throws<InvalidDataException>(() => SessionStorage.Read(SessionStorage.Write(session with { Collections = [Local() with { DurationMilliseconds = 1000 }] })));
        Assert.Throws<InvalidDataException>(() => SessionStorage.Read(SessionStorage.Write(session with { GcCounts = new(-1, 0, 0) })));
    }

    [Fact]
    public async Task FullHistoryStreamsRoundTripsAndExposesEveryMatchingRow()
    {
        var buffer = new LiveTelemetryBuffer();

        for (var i = 0; i < 12001; i++)
        {
            buffer.ThreadLocalCollection(Local(i / 20d));
        }

        var frame = buffer.Read(700);
        await using var stream = new MemoryStream();
        await SessionStorage.WriteAsync(stream, Save(frame), TestContext.Current.CancellationToken);
        stream.Position = 0;
        var restored = await SessionStorage.ReadAsync(stream, TestContext.Current.CancellationToken);
        Assert.Equal(12001, restored.Collections.Length);
        stream.Position = 0;
        await using var vm = new MainViewModel();
        await vm.ImportSessionAsync(stream);
        vm.SelectPeriod(640, 700);
        Assert.Empty(vm.Events); // The selected period is empty, but recording is complete.
        vm.ResetTimelineSelection();
        Assert.Equal(12001, vm.Events.Count);
        Assert.Equal(12001, vm.EventLog.Count);
        Assert.Equal(600, vm.EventLog[0].EndSeconds);
        Assert.Equal(0, vm.EventLog[^1].EndSeconds);
        Assert.Equal("12,001", vm.Gen0CountLabel);
    }
}
