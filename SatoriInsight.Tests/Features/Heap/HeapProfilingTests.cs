using Microsoft.Diagnostics.NETCore.Client;
using SatoriInsight.Models;
using SatoriInsight.Services.Heap;
using SatoriInsight.Services.Processes;
using SatoriInsight.Services.Recording;
using SatoriInsight.Services.Telemetry;
using SatoriInsight.ViewModels;
using Xunit;

namespace SatoriInsight.Tests;

public class HeapProfilingTests
{
    [Theory(Timeout = 90000)]
    [InlineData("net8.0")]
    [InlineData("net10.0")]
    public async Task WorkerCapturesRealRegionsObjectsHolesAndRootsAndReplayPreservesThem(string framework)
    {
        using var target = await TestWorkload.StartAsync(framework);
        await target.Process.StandardInput.WriteLineAsync("fragment");
        Assert.Equal(
            "FRAGMENTED",
            await target.Process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var identity = (await new LocalProcessDiscovery().ListAsync(TestContext.Current.CancellationToken)).Single(p => p.ProcessId == target.Process.Id);
        var profile = await new HeapCaptureService().CaptureAsync(new(identity), TestContext.Current.CancellationToken);
        Assert.NotEmpty(profile.Regions);
        Assert.NotEmpty(profile.Objects);
        Assert.NotEmpty(profile.Types);
        Assert.NotEmpty(profile.Roots);
        Assert.NotEmpty(profile.Holes);
        Assert.Contains(profile.Objects, obj => obj.Generation == HeapGeneration.Loh);
        Assert.Contains(profile.Regions, region => region.Generation == HeapGeneration.Poh);
        Assert.All(profile.Regions, r => Assert.True(r.EndAddress >= r.StartAddress));
        Assert.Equal(profile.Objects.Sum(o => o.Size), profile.Types.Sum(t => t.Bytes));
        var root = profile.Roots.First(r => profile.Objects.Any(o => o.Address == r.ObjectAddress));
        Assert.NotEmpty(HeapAnalysis.FindRootPath(profile, root.ObjectAddress, TestContext.Current.CancellationToken));
        await using var vm = new MainViewModel();
        vm.Snapshots.Add(profile);
        var exported = vm.ExportJson();
        await vm.ImportSessionAsync(exported);
        Assert.Equal(profile.Objects.Length, Assert.Single(vm.Snapshots).Objects.Length);
        Assert.True(vm.IsReplay);
        Assert.All(HeapAnalysis.Compare(profile, profile), difference => Assert.Equal(0, difference.DeltaBytes));
    }

    [Fact]
    public void LiveTopologyAppliesCreatesFreesAndLossWithoutInventingOccupancy()
    {
        var buffer = new LiveTelemetryBuffer();
        buffer.SegmentCreated(1, 0x1000, 4096, 1);
        var region = Assert.Single(buffer.Read(1).Regions);
        Assert.False(region.DetailsKnown);
        Assert.Equal(HeapGeneration.Loh, region.Generation);
        buffer.SegmentFreed(1, 0x1000);
        Assert.Empty(buffer.Read(2).Regions);
        buffer.SegmentCreated(1, 0x1000, 8192, 0);
        buffer.EventsLost(1);
        Assert.Empty(buffer.Read(3).Regions);
    }

    [Fact]
    public async Task ReplayScrubsAndTailPercentileAndMaximumUseSelectedWindow()
    {
        var events = Enumerable.Range(1, 1000).Select(i => new GcEvent(i, i / 100d, 0, "Blocking", "Test", i, i, 0)).ToArray();
        var session = new SavedSession(SessionStorage.CurrentSchemaVersion, "local-eventpipe", null, 10, [new(1, 1, 2, 3)], events, null, null, [], [], [], null, null, []);
        await using var vm = new MainViewModel();
        await vm.ImportSessionAsync(System.Text.Json.JsonSerializer.Serialize(session));
        Assert.Equal("999.00 ms", vm.P999Label);
        Assert.Equal("1000.00 ms", vm.MaxPauseLabel);
        vm.ReplayPosition = 5;
        Assert.Equal(500, vm.Events.Count);
        Assert.Equal("500.00 ms", vm.MaxPauseLabel);
    }

    [Fact]
    public async Task ReplayRestoresSegmentChangesAndGenerationCountersIncludingEventLoss()
    {
        var buffer = new LiveTelemetryBuffer();
        buffer.SegmentCreated(1, 0x1000, 4096, 1, 1);
        buffer.Counter(new("loh-size", 2048), 1);
        buffer.SegmentCreated(1, 0x4000, 4096, 3, 2);
        buffer.SegmentFreed(1, 0x1000, 3);
        buffer.Counter(new("loh-size", 0), 3);
        buffer.EventsLost(2);
        var frame = buffer.Read(4);
        var session = new SavedSession(
            SessionStorage.CurrentSchemaVersion, "local-eventpipe", null, 4, frame.History.ToArray(), [], frame.Metrics, frame.Health, [],
            frame.Regions.ToArray(), [], frame.Topology, frame.GcCounts, []);
        await using var vm = new MainViewModel();
        await vm.ImportSessionAsync(SessionStorage.Write(session));
        vm.ReplayPosition = 1.5;
        Assert.Equal(0x1000UL, Assert.Single(vm.LiveRegionMap.Regions).StartAddress);
        Assert.Equal(2048, vm.GenerationSizes.Single(g => g.Generation == HeapGeneration.Loh).Bytes);
        vm.ReplayPosition = 2.5;
        Assert.Equal(2, vm.LiveRegionMap.Regions.Count);
        vm.ReplayPosition = 4;
        Assert.Empty(vm.LiveRegionMap.Regions);
        Assert.True(vm.ShowQualityNotice);
        vm.ReplayPosition = 1.5;
        // Re-exporting a scrubbed session must retain its final counters and all changes.
        var restored = SessionStorage.Read(vm.ExportJson());
        Assert.Equal(0, restored.HeapMetrics!.LohBytes);
        Assert.Empty(HeapTopology.At(restored.Topology!, 4));
    }

    [Fact]
    public void TopologyHistoryPreservesEveryChangeAndTheOriginalTimeRange()
    {
        var buffer = new LiveTelemetryBuffer();
        buffer.SegmentCreated(1, 0x1000, 4096, 0, 1);
        buffer.SegmentCreated(2, 0x1000, 4096, 1, 2);
        buffer.SegmentFreed(1, 0x1000, 310);
        var history = buffer.Read(311).Topology!;
        Assert.Equal(0, history.StartSeconds);
        Assert.Equal(2, HeapTopology.At(history, 300).Length);
        Assert.Equal(2, Assert.Single(HeapTopology.At(history, 311)).RuntimeId);

        for (var i = 0; i < 20005; i++)
        {
            buffer.SegmentFreed(1, 0x1000, 312 + i / 100000d);
        }

        Assert.Equal(2, HeapTopology.At(buffer.Read(1000).Topology!, 300).Length);
        Assert.False(buffer.Read(1000).Health!.IsIncomplete);
    }

    private static HeapProfile Graph() => new(Guid.NewGuid(), DateTime.UtcNow, null, "10", 1, 1, [new(
        1, 0, 0x1000, 0, 4096, 96, 0, HeapGeneration.Gen2, 0, 3, [], ObjectRangeEnd: 0x2000)], [new("Node", 3, 96)], [new(0x1000, "Node", 32, 1, HeapGeneration.Gen2, [new(0x1020, "next")]), new(
        0x1020, "Node", 32, 1, HeapGeneration.Gen2, [new(0x1040, "next"), new(0x1000, "cycle")]), new(0x1040, "Node", 32, 1, HeapGeneration.Gen2, [])], [new(0x3000, 0x1000, "StrongHandle", false)], [], []);

    [Fact]
    public void RootSearchTraversesReferencesHandlesCyclesAndSupportsCancellation()
    {
        var profile = Graph();
        var path = HeapAnalysis.FindRootPath(profile, 0x1040, TestContext.Current.CancellationToken);
        Assert.Equal(new ulong[] { 0x1000, 0x1020, 0x1040 }, path.Select(p => p.Address));
        Assert.Equal("next", path[1].Link);
        Assert.Empty(HeapAnalysis.FindRootPath(profile, 0x9000, TestContext.Current.CancellationToken));
        Assert.Throws<OperationCanceledException>(() => HeapAnalysis.FindRootPath(profile, 0x1040, new CancellationToken(true)));
    }

    [Fact]
    public void SessionImportRejectsCorruptObjectGraphsBeforeReplacingTheSession()
    {
        var profile = Graph();
        var session = new SavedSession(SessionStorage.CurrentSchemaVersion, "local-eventpipe", null, 0, [], [], null, null, [], [], [profile], null, null, []);
        Assert.Single(SessionStorage.Read(SessionStorage.Write(session)).Snapshots);
        Assert.Throws<InvalidDataException>(() => SessionStorage.Read(SessionStorage.Write(session with { Snapshots = [profile with { Objects = [profile.Objects[0], profile.Objects[0]] }] })));
        Assert.Throws<InvalidDataException>(() => SessionStorage.Read(SessionStorage.Write(session with { Snapshots = [profile with { Objects = [profile.Objects[0] with { References = null! }] }] })));
        Assert.Throws<InvalidDataException>(() => SessionStorage.Read(SessionStorage.Write(session with { Snapshots = [profile with { Holes = [new(0x1FF0, 100, 1)] }] })));
    }

    [Fact(Timeout = 90000)]
    public async Task DumpCanBeInspectedOfflineAndCaptureLimitsRemainExplicit()
    {
        using var target = await TestWorkload.StartAsync("net10.0");
        var path = Path.Combine(Path.GetTempPath(), $"satori-test-{Guid.NewGuid():N}.dmp");

        try
        {
            new DiagnosticsClient(target.Process.Id).WriteDump(DumpType.WithHeap, path, logDumpGeneration: false);
            var profile = await new HeapCaptureService().CaptureAsync(
                new(null, path, MaxObjects: 25, MaxReferences: 25), TestContext.Current.CancellationToken);
            Assert.Null(profile.Process);
            Assert.InRange(profile.Objects.Length, 1, 25);
            Assert.NotEmpty(profile.Regions);
            Assert.NotEmpty(profile.Warnings);
            Assert.Contains(profile.Regions, region => !region.DetailsKnown);
            var session = new SavedSession(SessionStorage.CurrentSchemaVersion, "local-eventpipe", null, 0, [], [], null, null, [], [], [profile], null, null, []);
            Assert.Single(SessionStorage.Read(SessionStorage.Write(session)).Snapshots);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void ComparisonIncludesNewAndRemovedTypesAndPreservesNegativeGrowth()
    {
        var baseline = Graph() with
        {
            Types = [new("Node", 3, 96), new("Removed", 2, 50)]
        };
        var current = Graph() with
        {
            Types = [new("Node", 4, 128), new("New", 1, 20)]
        };
        var growth = HeapAnalysis.Compare(baseline, current);
        Assert.Equal(32, growth.Single(t => t.Name == "Node").DeltaBytes);
        Assert.Equal(-50, growth.Single(t => t.Name == "Removed").DeltaBytes);
        Assert.Equal(20, growth.Single(t => t.Name == "New").DeltaBytes);
    }

    [Fact]
    public async Task CapturedObjectListsExposeAllMatchingRowsAndKeepObjectNavigation()
    {
        var profile = Graph() with
        {
            Regions = [new(1, 0, 0x1000, 0, 16384, 16384, 0, HeapGeneration.Gen2, 0, 512, [], ObjectRangeEnd: 0x5000)],
            Objects = Enumerable.Range(0, 512).Select(index => new HeapObjectInfo(
                0x1000UL + (ulong)index * 32, index % 2 == 0 ? "Node" : "Other", 32, 1, HeapGeneration.Gen2, [])).ToArray(),
            Types = [new("Node", 256, 8192), new("Other", 256, 8192)]
        };
        await using var vm = new MainViewModel();
        vm.Snapshots.Add(profile);
        vm.SelectedSnapshot = profile;
        Assert.Equal(512, vm.Objects.Count);
        Assert.Equal(512, vm.CapturedRegionMap.RegionObjects.Count);

        vm.SelectedType = profile.Types[0];
        Assert.Equal(256, vm.Objects.Count);
        Assert.All(vm.Objects, obj => Assert.Equal("Node", obj.Type));
        vm.InspectObject(profile.Objects[^1]);
        Assert.Equal(512, vm.Objects.Count);
        Assert.Equal(profile.Objects[^1], vm.SelectedObject);
        Assert.Equal(2, vm.InspectionTab);
    }
}
