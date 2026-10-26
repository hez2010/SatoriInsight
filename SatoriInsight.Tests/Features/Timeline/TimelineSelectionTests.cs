using SatoriInsight.Models;
using SatoriInsight.Services.Recording;
using SatoriInsight.ViewModels;
using Xunit;

namespace SatoriInsight.Tests;

public class TimelineSelectionTests
{
    private static SavedSession Session()
    {
        const int mib = 1048576;
        MetricPoint Point(double at, int size, int allocation) => new(at, size, size * 2, allocation, new(
            HeapBytes: size * mib, CommittedBytes: size * 2 * mib, AllocationBytesPerSecond: allocation * mib, Gen1Bytes: size * mib));
        return new(SessionStorage.CurrentSchemaVersion, "local-eventpipe", null, 8, [Point(2, 1, 10), Point(4, 2, 20), Point(6, 3, 30)], [new(1, 1, 1, "Blocking", "Test", 5, 5, 1), new(2, 2, 0, "Blocking", "Test", 2, 2, 0.1), new(
            3, 3, 0, "ThreadLocal", "Collected", 0.01, 0, 0, new(1, 123, true, 200, 100, 10000)), new(4, 5, 2, "Background", "Test", 100, 30, 0.2)], Point(6, 3, 30).HeapMetrics, new(), [new(2, 1, 5), new(4, 1, 10), new(6, 2, 20)], [], [], null, new(2, 1, 1), [], []);
    }

    [Fact]
    public async Task GenerationFilterIncludesBothKindsOfGen0AndSupportsAllGenerations()
    {
        await using var vm = new MainViewModel();
        await vm.ImportSessionAsync(SessionStorage.Write(Session()));
        Assert.Equal(new[] { "All generations", "Gen0", "Gen1", "Gen2" }, vm.GenerationFilterOptions);
        vm.GenerationFilterIndex = 1;
        Assert.Equal(2, vm.EventLog.Count);
        Assert.Contains(vm.EventLog, gc => gc.IsThreadLocal);
        Assert.Contains(vm.EventLog, gc => !gc.IsThreadLocal);
        vm.GenerationFilterIndex = 2;
        Assert.Equal(1, Assert.Single(vm.EventLog).Generation);
        vm.GenerationFilterIndex = 3;
        Assert.Equal(2, Assert.Single(vm.EventLog).Generation);
        vm.GenerationFilterIndex = 0;
        Assert.Equal(4, vm.EventLog.Count);
    }

    [Fact]
    public async Task PointSelectionRestoresSampledMetricsAndStatsWhileTimelineStaysAnchored()
    {
        await using var vm = new MainViewModel();
        await vm.ImportSessionAsync(SessionStorage.Write(Session()));
        vm.SelectTimePoint(3.5);
        Assert.True(vm.HasTimelineSelection);
        Assert.False(vm.IsPeriodSelected);
        Assert.Equal(3.5, vm.SessionSeconds);
        Assert.Equal(8, vm.ChartMaximumSeconds);
        Assert.Equal(3, vm.History.Count);
        Assert.Equal(4, vm.TimelineEvents.Count);
        Assert.Equal("1.0 MiB", vm.UsedLabel);
        Assert.Equal("10.0", vm.AllocationLabel);
        Assert.Equal("2", vm.Gen0CountLabel);
        Assert.Equal("1", vm.Gen1CountLabel);
        Assert.Equal("0", vm.Gen2CountLabel);
        Assert.Equal("5.00 ms", vm.MaxPauseLabel);
        Assert.Equal(3, vm.Events.Count);
        vm.ResetTimelineSelection();
        Assert.False(vm.HasTimelineSelection);
        Assert.Equal(8, vm.SessionSeconds);
        Assert.Equal("3.0 MiB", vm.UsedLabel);
        Assert.Equal(4, vm.Events.Count);
    }

    [Fact]
    public async Task ReverseDragSelectsPeriodAndScopesCountsHistogramAndSampledRates()
    {
        await using var vm = new MainViewModel();
        await vm.ImportSessionAsync(SessionStorage.Write(Session()));
        vm.SelectPeriod(6, 2.5);
        Assert.True(vm.IsPeriodSelected);
        Assert.Equal(2.5, vm.WindowStartSeconds);
        Assert.Equal(6, vm.SessionSeconds);
        Assert.Equal(8, vm.ChartMaximumSeconds);
        Assert.Equal("1", vm.Gen0CountLabel);
        Assert.Equal("0", vm.Gen1CountLabel);
        Assert.Equal("1", vm.Gen2CountLabel);
        Assert.Equal("30.00 ms", vm.P50Label);
        Assert.Equal(1, vm.Histogram.Sum(bin => bin.Count));
        Assert.Equal("3.0 MiB", vm.UsedLabel);
        Assert.Equal("25.0", vm.AllocationLabel);
        Assert.Equal("AVG ALLOCATION RATE", vm.AllocationTitle);
        Assert.Equal("1.00%", vm.PausePercentLabel);
        var saved = SessionStorage.Read(vm.ExportJson());
        Assert.Equal(8, saved.SessionSeconds);
        Assert.Equal(4, saved.Collections.Length);
        vm.SelectPeriod(4.1, 4.2);
        Assert.Equal("—", vm.AllocationLabel);
        Assert.Equal("—", vm.PausePercentLabel);
        Assert.False(vm.HasPauses);
    }
}
