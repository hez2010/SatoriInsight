using System.Diagnostics;
using System.Text.Json;
using SatoriInsight.Models;
using SatoriInsight.Services.Recording;
using SatoriInsight.Services.Telemetry;
using Xunit;

namespace SatoriInsight.Tests;

public class GenerationRangeTests
{
    [Fact]
    public void RangesReplaceSegmentsAndRetireMovedBoundariesWithoutInventingOccupancy()
    {
        var buffer = new LiveTelemetryBuffer();
        buffer.SegmentCreated(1, 0x1000, 0x4000, 0, 1);
        buffer.GenerationRanges(1, [new(2, 0x1000, 0x1000, 0x1000), new(1, 0x2000, 0x1000, 0x1000), new(0, 0x3000, 0x100, 0x2000)], 2);
        var first = buffer.Read(2);
        Assert.Equal(new[] { HeapGeneration.Gen2, HeapGeneration.Gen1, HeapGeneration.Gen0 }, first.Regions.Select(region => region.Generation));
        Assert.All(first.Regions, region => Assert.False(region.DetailsKnown));
        Assert.All(first.Regions, region => Assert.Equal(0, region.ObjectBytes));
        Assert.Equal(0x100, first.Regions[2].UsedRangeBytes);
        var id = first.Regions[0].Id;
        buffer.GenerationRanges(1, [new(2, 0x1000, 0x1000, 0x1000), new(1, 0x2000, 0x1000, 0x1000), new(0, 0x3000, 0x100, 0x2000)], 3);
        buffer.GenerationRanges(1, [new(2, 0x1000, 0x2000, 0x2000), new(1, 0x3000, 0, 0), new(0, 0x3000, 0, 0x2000)], 4);
        var current = buffer.Read(4);
        Assert.Equal(2, current.Regions.Count);
        Assert.Equal(id, current.Regions[0].Id);
        Assert.DoesNotContain(current.Regions, region => region.StartAddress == 0x2000);
        Assert.Equal(3, HeapTopology.At(current.Topology!, 2).Length);
        Assert.Equal(2, HeapTopology.At(current.Topology!, 4).Length);
        buffer.SegmentFreed(1, 0x1000, 5);
        Assert.Empty(buffer.Read(5).Regions);
        Assert.Empty(HeapTopology.At(buffer.Read(5).Topology!, 5));
    }

    [Fact]
    public void RawMetadataTakesPrecedenceAndRetainsGen3InsteadOfMappingItToLoh()
    {
        const ulong page = 0x40000000;
        const ulong start = page + 0x200000;
        var buffer = new LiveTelemetryBuffer();
        buffer.SegmentCreated(1, start, 0x200000, 0);
        buffer.GenerationRanges(1, [new(3, start, 0x100000, 0x200000)], 1);
        Assert.Equal(HeapGeneration.Loh, Assert.Single(buffer.Read(1).Regions).Generation);
        var header = new RegionHeaderInfo(page, page + 0x40000000, 3, RegionQueueKind.RecyclerTenured, RegionReuseLevel.None,
            start + 0x100000, start + 0x200000, 0, 0, 4096, 10, false);
        var region = new HeapRegion(0, -1, start, 0, 0x200000, 0, 0, HeapGeneration.Gen3, 0, 0, [],
            DetailsKnown: false, ObjectRangeEnd: start + 0x200000, RuntimeId: -1, Header: header);
        buffer.RegionSample(new(true, [region], 0, true, 100, 1), 2);
        buffer.GenerationRanges(1, [new(3, start, 0x100000, 0x200000)], 3);
        buffer.SegmentCreated(1, start, 0x200000, 0, 4);
        buffer.SegmentFreed(1, start, 5);
        buffer.EventsLost(2, 6);
        var current = Assert.Single(buffer.Read(6).Regions);
        Assert.Equal(HeapGeneration.Gen3, current.DisplayGeneration);
        Assert.Equal(header, current.Header);
        Assert.Null(current.UsedRangeBytes);
        Assert.Equal(current, Assert.Single(HeapTopology.At(buffer.Read(6).Topology!, 6)));
    }

    [Theory(Timeout = 30000)]
    [InlineData("net10.0", false)]
    [InlineData("net10.0", true)]
    public async Task ServerAndWorkstationTargetsProduceGenerationMapsAndReplay(string framework, bool serverGc)
    {
        using var workload = await TestWorkload.StartAsync(framework, serverGc);
        var token = TestContext.Current.CancellationToken;
        await workload.Process.StandardInput.WriteLineAsync("runtime");
        var runtimeLine = await workload.Process.StandardOutput.ReadLineAsync(token);
        using var runtime = JsonDocument.Parse(runtimeLine![8..]);
        Assert.Equal(serverGc, runtime.RootElement.GetProperty("IsServerGC").GetBoolean());
        var target = new LocalDotNetProcess(workload.Process.Id, "Generation target", workload.Process.StartTime.ToUniversalTime());
        await using var collector = await EventPipeCollector.StartAsync(target, token);
        await workload.Process.StandardInput.WriteLineAsync("generations");
        Assert.Equal("GENERATIONS_READY", await workload.Process.StandardOutput.ReadLineAsync(token));
        var timeout = Stopwatch.StartNew();
        DiagnosticFrame frame;

        do
        {
            await Task.Delay(100, token);
            frame = collector.Read();
        }
        while ((!frame.Regions.Any(region => region.Generation == HeapGeneration.Gen1)
            || !frame.Regions.Any(region => region.Generation == HeapGeneration.Gen0)) && timeout.Elapsed < TimeSpan.FromSeconds(10));

        Assert.Contains(frame.Regions, region => region.Generation == HeapGeneration.Gen0 && region.UsedRangeBytes.HasValue);
        Assert.Contains(frame.Regions, region => region.Generation == HeapGeneration.Gen1 && region.UsedRangeBytes.HasValue);
        Assert.Contains(frame.Regions, region => region.Generation == HeapGeneration.Gen2 && region.UsedRangeBytes.HasValue);
        Assert.Contains(frame.Regions, region => region.Generation == HeapGeneration.Loh && region.UsedRangeBytes.HasValue);
        Assert.Contains(frame.Regions, region => region.Generation == HeapGeneration.Poh && region.UsedRangeBytes.HasValue);
        Assert.All(frame.Regions, region => Assert.Null(region.Header));

        var ordered = frame.Regions.OrderBy(region => region.StartAddress).ToArray();

        for (var index = 1; index < ordered.Length; index++)
        {
            Assert.True(ordered[index - 1].EndAddress <= ordered[index].StartAddress);
        }

        var session = new SavedSession(SessionStorage.CurrentSchemaVersion, "local-eventpipe", target, frame.AtSeconds,
            frame.History.ToArray(), frame.Events.ToArray(), frame.Metrics, frame.Health, frame.PauseCounters!.ToArray(),
            frame.Regions.ToArray(), [], frame.Topology, frame.GcCounts, frame.ProcessHistory!.ToArray(), frame.GcModes!.ToArray());
        var restored = SessionStorage.Read(SessionStorage.Write(session));
        Assert.Equal(ordered.Select(region => (region.StartAddress, region.Generation, region.UsedRangeBytes)),
            HeapTopology.At(restored.Topology!, frame.AtSeconds).Select(region => (region.StartAddress, region.Generation, region.UsedRangeBytes)));
    }
}
