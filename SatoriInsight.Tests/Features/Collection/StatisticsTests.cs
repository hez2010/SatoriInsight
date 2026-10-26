using SatoriInsight.Models;
using SatoriInsight.Services.Telemetry;
using Xunit;

namespace SatoriInsight.Tests;

public class StatisticsTests
{
    private static GcEvent[] Events(params double[] pauses) => pauses.Select((pause, index) => new GcEvent(index, index, 2, "Background", "Test", 200, pause, 50)).ToArray();

    [Fact]
    public void HistogramUsesReportedGcPauseAndNeverSuspendOrGcDuration()
    {
        var events = Events(0.5, 3.5);
        var bins = GcStatistics.Histogram(events);
        Assert.Equal(2, bins.Sum(bin => bin.Count));
        Assert.Equal(1, bins[0].Count);
        Assert.Equal(1, bins[2].Count);
        Assert.Equal(3.5, GcStatistics.Percentile(events, 99));
    }

    [Fact]
    public void HistogramUsesExclusiveUpperBoundsAndRejectsInvalidSamples()
    {
        var bins = GcStatistics.Histogram(Events(0, 1, 2, 5, 10, 20, 50, -1, double.NaN, double.PositiveInfinity));
        Assert.All(bins, bin => Assert.Equal(1, bin.Count));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(50, 2)]
    [InlineData(95, 4)]
    [InlineData(100, 4)]
    public void PercentilesUseNearestRank(double percentile, double expected) => Assert.Equal(expected, GcStatistics.Percentile(Events(4, 1, 3, 2), percentile));

    [Fact]
    public void PausePercentageUsesRuntimeCounterNotPotentiallyOverlappingGcTotals()
    {
        Assert.Equal(1, GcStatistics.CounterPausePercent([new(1, 1, 15), new(2, 1, 5)]));
        Assert.Null(GcStatistics.CounterPausePercent([]));
    }

    [Fact]
    public void EmptyWindowsAreDefined()
    {
        Assert.Equal(0, GcStatistics.Percentile([], 99));
        Assert.Equal(0, GcStatistics.Histogram([]).Sum(bin => bin.Count));
        Assert.Throws<ArgumentOutOfRangeException>(() => GcStatistics.Percentile([], 101));
    }
}
