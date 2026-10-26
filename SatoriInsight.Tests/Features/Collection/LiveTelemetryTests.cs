using SatoriInsight.Models;
using SatoriInsight.Services.Telemetry;
using Xunit;

namespace SatoriInsight.Tests;

public class LiveTelemetryTests
{
    [Fact]
    public void EndRecordIsAuthoritativeWithoutAnyStartEvent()
    {
        var buffer = new LiveTelemetryBuffer();
        var gc = new GcEvent(10, 1.3, 0, "Blocking", "Small allocation", 100, 7, 2);
        buffer.GcEnd(gc);
        Assert.Equal(gc, Assert.Single(buffer.Read(2).Events));
        Assert.Equal(7, Assert.Single(buffer.Read(2).Events).PauseMilliseconds);
        Assert.Equal(2, Assert.Single(buffer.Read(2).Events).SuspendMilliseconds);
    }

    [Fact]
    public void ConcurrentCollectionsCanCompleteOutOfNumberOrderWithoutPairing()
    {
        var buffer = new LiveTelemetryBuffer();
        buffer.GcEnd(new(21, 1.2, 0, "Foreground", "Test", 5, 1.5, 0.2));
        buffer.GcEnd(new(20, 1.3, 2, "Background", "Test", 300, 3, 0.4));
        var frame = buffer.Read(2);
        Assert.Equal(2, frame.Events.Count);
        Assert.Equal(3, frame.Events.Single(gc => gc.Number == 20).PauseMilliseconds);
        Assert.Equal(1.5, frame.Events.Single(gc => gc.Number == 21).PauseMilliseconds);
    }

    [Fact]
    public void LostEventsAndInvalidReportedValuesAreVisible()
    {
        var buffer = new LiveTelemetryBuffer();
        buffer.EventsLost(10);
        buffer.GcEnd(new(1, 1, 0, "Blocking", "Test", 2, double.NaN, 0.1));
        var frame = buffer.Read(2);
        Assert.Empty(frame.Events);
        Assert.Equal(10, frame.Health!.EventsLost);
        Assert.Equal(1, frame.Health.DiscardedCollections);
        Assert.True(frame.Health.IsIncomplete);
    }

    [Fact]
    public void MissingCountersRemainNullAndHistoryIsCoalescedWithoutEviction()
    {
        var buffer = new LiveTelemetryBuffer();
        Assert.Null(buffer.Read(0).Metrics!.HeapBytes);

        for (var i = 0; i < 600; i++)
        {
            buffer.Counter(new("gc-heap-size", i * 1_048_576), i);
            buffer.Counter(new("alloc-rate", 1_048_576), i + 0.01);
            buffer.Counter(new("total-pause-time-by-gc", 5, 1), i + 0.02);
        }

        var frame = buffer.Read(600);
        Assert.Equal(600, frame.History.Count);
        Assert.Null(frame.Metrics!.CommittedBytes);
        Assert.All(frame.History, point =>
        {
            Assert.Null(point.CommittedMiB);
            Assert.Equal(1, point.AllocationMiB);
        });
        Assert.Equal(600, frame.PauseCounters!.Count);
        Assert.Equal(0.5, GcStatistics.CounterPausePercent(frame.PauseCounters));
    }

    [Fact]
    public void CompletedCollectionsHaveNoCountOrTimeRetentionLimit()
    {
        var buffer = new LiveTelemetryBuffer();

        for (var i = 0; i < 10010; i++)
        {
            buffer.GcEnd(new(i, i / 1000d, 0, "Blocking", "Test", 2, 1, 0.1));
        }

        var frame = buffer.Read(1000);
        Assert.Equal(10010, frame.Events.Count);
        Assert.False(frame.Health!.IsIncomplete);
    }

    [Theory]
    [InlineData("gc-heap-size", 12.5, "MB", 12500000)]
    [InlineData("gc-committed", 20, "MB", 20000000)]
    [InlineData("loh-size", 1024, "B", 1024)]
    public void RuntimeCounterUnitsAreNormalized(string name, double value, string units, double expected)
    {
        var payload = new Dictionary<string, object>
        {
            ["Payload"] = new Dictionary<string, object>
            {
                ["Name"] = name,
                ["Mean"] = value,
                ["DisplayUnits"] = units
            }
        };
        Assert.Equal(expected, RuntimeCounterParser.Parse(payload)!.Value);
    }

    [Fact]
    public void AllocationUsesIntervalButGcPauseCounterPreservesItsReportedIncrement()
    {
        var payload = new Dictionary<string, object>
        {
            ["Name"] = "alloc-rate",
            ["Increment"] = 2_097_152d,
            ["IntervalSec"] = 2d
        };
        Assert.Equal(1_048_576, RuntimeCounterParser.Parse(payload)!.Value);
        payload["Name"] = "total-pause-time-by-gc";
        payload["Increment"] = 10d;
        payload["DisplayUnits"] = "ms";
        var pause = RuntimeCounterParser.Parse(payload)!;
        Assert.Equal(10, pause.Value);
        Assert.Equal(2, pause.IntervalSeconds);
        payload["IntervalSec"] = 0d;
        Assert.Null(RuntimeCounterParser.Parse(payload));
    }
}
