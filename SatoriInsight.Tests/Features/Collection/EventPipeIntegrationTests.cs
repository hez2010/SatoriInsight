using System.Diagnostics;
using SatoriInsight.Models;
using SatoriInsight.Services.Processes;
using SatoriInsight.Services.Telemetry;
using Xunit;

namespace SatoriInsight.Tests;

public class EventPipeIntegrationTests
{
    [Theory(Timeout = 45000)]
    [InlineData("net10.0")]
    public async Task LocalCollectorReceivesRealMetricsCollectionsAndSuspensions(string framework)
    {
        using var target = await TestWorkload.StartAsync(framework);
        var token = TestContext.Current.CancellationToken;
        var processes = await new LocalProcessDiscovery().ListAsync(token);
        var identity = Assert.Single(processes, process => process.ProcessId == target.Process.Id);
        Assert.Contains("SatoriInsight.Workload", identity.CommandLineTooltip);
        await using var collector = await EventPipeCollector.StartAsync(identity, token);
        await target.Process.StandardInput.WriteLineAsync("segment");
        await target.Process.StandardInput.WriteLineAsync("gc");
        var frame = await WaitForFrame(collector, value => value.Metrics?.HeapBytes > 0 && value.Metrics.CommittedBytes > 0
            && value.Metrics.AllocationBytesPerSecond > 0 && value.Events.Any(gc => gc.Generation == 2
            && gc.PauseMilliseconds > 0) && value.PauseCounters?.Count > 0 && value.Regions.Count > 0 && value.ProcessHistory?.Count >= 2);
        Assert.NotNull(frame.GcCounts);
        Assert.True(frame.GcCounts.Gen2 > 0);
        Assert.All(frame.Regions, region => Assert.False(region.DetailsKnown));
        Assert.NotEmpty(frame.History);
        var processSample = frame.ProcessHistory![^1];
        Assert.True(processSample.CpuSeconds >= 0);
        Assert.True(processSample.ResidentBytes > 0);
        Assert.True(processSample.WallClockSeconds > 0);
        Assert.Contains(frame.Events, gc => gc.Generation == 2 && gc.Reason.StartsWith("Induced"));
        Assert.All(frame.Events, gc =>
        {
            Assert.True(gc.PauseMilliseconds >= 0);
            Assert.True(gc.SuspendMilliseconds >= 0);
        });
        Assert.Equal(frame.Events.Count, GcStatistics.Histogram(frame.Events).Sum(bin => bin.Count));
        Assert.NotNull(GcStatistics.CounterPausePercent(frame.PauseCounters!));
        await collector.DisposeAsync();
        Assert.True(collector.Completion.IsCompletedSuccessfully);
        var stoppedAt = collector.Read().AtSeconds;
        var samplesAtStop = collector.Read().ProcessHistory!.Count;
        await Task.Delay(100, token);
        Assert.Equal(stoppedAt, collector.Read().AtSeconds);
        Assert.Equal(samplesAtStop, collector.Read().ProcessHistory!.Count);
    }

    [Fact(Timeout = 45000)]
    public async Task TargetExitCompletesCollectionAndAReplacementSessionStartsEmpty()
    {
        using var target = await TestWorkload.StartAsync("net10.0");
        var token = TestContext.Current.CancellationToken;
        var identity = (await new LocalProcessDiscovery().ListAsync(token)).Single(p => p.ProcessId == target.Process.Id);

        await using (var first = await EventPipeCollector.StartAsync(identity, token))
        {
            await WaitForFrame(first, frame => frame.Metrics?.HeapBytes > 0);
        }

        await using var second = await EventPipeCollector.StartAsync(identity, token);
        Assert.Empty(second.Read().History);
        await WaitForFrame(second, frame => frame.Metrics?.HeapBytes > 0);
        await target.Process.StandardInput.WriteLineAsync("exit");
        var end = await second.Completion.WaitAsync(TimeSpan.FromSeconds(8), token);
        Assert.True(end.TargetExited);
        Assert.Null(end.Error);
    }

    private static async Task<DiagnosticFrame> WaitForFrame(ILiveTelemetrySession session, Func<DiagnosticFrame, bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        DiagnosticFrame frame;
        do
        {
            await Task.Delay(150, TestContext.Current.CancellationToken);
            frame = session.Read();

            if (condition(frame))
            {
                return frame;
            }

            if (session.Completion.IsCompleted)
            {
                throw new InvalidOperationException($"Collection ended early: {await session.Completion}");
            }
        }
        while (timeout.Elapsed < TimeSpan.FromSeconds(15));
        throw new TimeoutException($"No matching telemetry. Events={frame.Events.Count}, pauseCounters={frame.PauseCounters?.Count}, metrics={frame.Metrics}, health={frame.Health}");
    }
}
