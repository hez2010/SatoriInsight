using System.Diagnostics;
using System.Text.Json;
using SatoriInsight.Models;
using SatoriInsight.Services.Heap;
using SatoriInsight.Services.Recording;
using SatoriInsight.Services.Telemetry;
using SatoriInsight.ViewModels;
using Xunit;

namespace SatoriInsight.Tests;

public class RegionMetadataTests
{
    [Fact]
    public async Task UnassignedRegionsKeepRawCapacityWithoutDisplayingItAsManagedOccupancy()
    {
        var original = RegionTestData.Sample().Regions[0];
        var region = original with
        {
            Generation = HeapGeneration.Unassigned,
            Header = original.Header! with { Generation = -1, ReportedOccupancyBytes = original.CommittedBytes }
        };
        Assert.Equal("Unassigned", region.GenerationLabel);
        Assert.False(region.Header.HasReportedOccupancy);
        Assert.Equal("—", region.Header.OccupancyLabel);
        Assert.Equal("—", region.Header.ObjectCountLabel);

        await using var vm = new MainViewModel();
        vm.LiveRegionMap.SelectedRegion = region;
        Assert.Contains("Unassigned", vm.LiveRegionMap.RegionSummary);
        Assert.Contains("Reported occupancy: — · reported objects: —", vm.LiveRegionMap.RegionSummary);
        Assert.DoesNotContain("Generation -1", vm.LiveRegionMap.RegionSummary);
    }

    [Fact]
    public async Task RegionUpdatesRetainIdentityReplayMetadataAndDoNotTreatOccupancyAsFreeSpace()
    {
        var sample = RegionTestData.Sample();
        var buffer = new LiveTelemetryBuffer();
        buffer.RegionSample(sample, 2);
        var before = buffer.Read(2);
        buffer.RegionSample(sample, 4);
        var first = sample.Regions[0];
        var changed = first with
        {
            Header = first.Header! with
            {
                ReportedOccupancyBytes = 2048,
                Generation = 1
            },
            Generation = HeapGeneration.Gen1
        };
        buffer.RegionSample(sample with
        {
            Regions = [changed],
            Complete = false,
            SkippedRegions = 1
        }, 6);
        Assert.Equal(2, buffer.Read(6).Regions.Count); // partial samples never remove unseen regions
        buffer.EventsLost(1, 7);
        Assert.Equal(2, buffer.Read(7).Regions.Count); // metadata is independent of EventPipe loss
        buffer.RegionSample(sample with
        {
            Regions = [changed]
        }, 8);
        var frame = buffer.Read(8);
        Assert.Single(frame.Regions);
        Assert.Equal(before.Regions[0].Id, frame.Regions[0].Id);
        var session = new SavedSession(
            SessionStorage.CurrentSchemaVersion, "local-eventpipe", null, 8, [], [], null, frame.Health, [], frame.Regions.ToArray(), [], frame.Topology, frame.GcCounts, [], []);
        await using var vm = new MainViewModel();
        await vm.ImportSessionAsync(SessionStorage.Write(session));
        vm.ReplayPosition = 3;
        Assert.Equal(2, vm.LiveRegionMap.Regions.Count);
        Assert.Equal(4096, vm.LiveRegionMap.Regions[0].Header!.ReportedOccupancyBytes);
        vm.LiveRegionMap.SelectedRegion = vm.LiveRegionMap.Regions[0];
        Assert.Contains("Reported occupancy", vm.LiveRegionMap.RegionSummary);
        Assert.Contains("unavailable", vm.LiveRegionMap.RegionSummary);
        vm.ReplayPosition = 8;
        Assert.Single(vm.LiveRegionMap.Regions);
        Assert.Equal(2048, vm.LiveRegionMap.Regions[0].Header!.ReportedOccupancyBytes);
        Assert.Contains("regions", vm.LiveRegionMap.MapSourceLabel, StringComparison.OrdinalIgnoreCase);
    }

    public static bool SupportsRegionTarget => OperatingSystem.IsWindows();

    [Fact]
    public async Task LiveRegionHistoryIsIndependentOfTheSelectedCapture()
    {
        var sample = RegionTestData.Sample();
        var buffer = new LiveTelemetryBuffer();
        buffer.RegionSample(sample, 1);
        var original = sample.Regions[0];
        var updated = original with { Header = original.Header! with { ReportedOccupancyBytes = 2048 } };
        buffer.RegionSample(sample with { Regions = [updated, sample.Regions[1]] }, 2);
        var frame = buffer.Read(2);
        var captured = original with { Header = original.Header! with { ReportedOccupancyBytes = 8192 } };
        var profile = new HeapProfile(Guid.NewGuid(), DateTime.UtcNow, null, "test", 0, 0,
            [captured], [], [], [], [], [], MetadataOnly: true);
        var session = new SavedSession(SessionStorage.CurrentSchemaVersion, "local-eventpipe", null, 2, [], [], null,
            frame.Health, [], frame.Regions.ToArray(), [profile], frame.Topology, frame.GcCounts, [], []);

        await using var vm = new MainViewModel();
        await vm.ImportSessionAsync(SessionStorage.Write(session));

        Assert.NotNull(vm.SelectedSnapshot);
        Assert.False(vm.LiveRegionMap.HasObjectCapture);
        Assert.Equal(2048, vm.LiveRegionMap.Regions[0].Header!.ReportedOccupancyBytes);
        vm.SelectTimePoint(1.5);
        Assert.Equal(4096, vm.LiveRegionMap.Regions[0].Header!.ReportedOccupancyBytes);
        vm.ResetTimelineSelection();
        Assert.Equal(2048, vm.LiveRegionMap.Regions[0].Header!.ReportedOccupancyBytes);

        vm.LiveRegionMap.SelectedRegion = vm.LiveRegionMap.Regions[1];
        var liveSelection = vm.LiveRegionMap.SelectedRegion;
        vm.ShowCapturedRegions();
        Assert.Equal(8192, Assert.Single(vm.CapturedRegionMap.Regions).Header!.ReportedOccupancyBytes);
        Assert.True(vm.IsHeapInspectorView);
        Assert.True(vm.IsRegionInspection);
        Assert.Equal(liveSelection, vm.LiveRegionMap.SelectedRegion);
        var capturedSelection = vm.CapturedRegionMap.SelectedRegion;

        vm.SelectTimePoint(1.5);
        Assert.Equal(4096, vm.LiveRegionMap.Regions[0].Header!.ReportedOccupancyBytes);
        Assert.Equal(capturedSelection, vm.CapturedRegionMap.SelectedRegion);
        vm.ResetTimelineSelection();
        Assert.Equal(2048, vm.LiveRegionMap.Regions[0].Header!.ReportedOccupancyBytes);
        Assert.Equal(capturedSelection, vm.CapturedRegionMap.SelectedRegion);

        vm.RemoveSnapshot();
        Assert.Empty(vm.CapturedRegionMap.Regions);
        Assert.Null(vm.CapturedRegionMap.SelectedRegion);
        Assert.Equal(2, vm.LiveRegionMap.Regions.Count);
        Assert.Equal(liveSelection!.Id, vm.LiveRegionMap.SelectedRegion!.Id);
    }

    [Fact(Timeout = 30000, SkipUnless = nameof(SupportsRegionTarget), Skip = "Requires Windows process inspection.")]
    public async Task LiveAndCapturedRegionsWorkWithoutLoadingDac()
    {
        using var workload = await TestWorkload.StartSatoriAsync();
        var process = workload.Process;

        try
        {
            await process.StandardInput.WriteLineAsync("runtime");
            var identityLine = await process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken);
            using var identity = JsonDocument.Parse(identityLine![8..]);
            Assert.True(identity.RootElement.GetProperty("IsSatoriGC").GetBoolean());
            var target = new LocalDotNetProcess(process.Id, "Region test target", process.StartTime.ToUniversalTime());
            await using var collector = await EventPipeCollector.StartAsync(target, TestContext.Current.CancellationToken);
            DiagnosticFrame frame = collector.Read();
            var timeout = Stopwatch.StartNew();

            while ((!frame.Regions.Any(r => r.Header is not null) || frame.Topology?.Samples?.Length < 2) && timeout.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(200, TestContext.Current.CancellationToken);
                frame = collector.Read();
            }

            Assert.NotEmpty(frame.Regions);

            Assert.All(frame.Regions, r => Assert.NotNull(r.Header));
            Assert.All(frame.Regions, region => Assert.Null(region.UsedRangeBytes));
            Assert.All(frame.Regions, region => Assert.Equal(region.Header!.HeapGeneration, region.DisplayGeneration));
            var samples = frame.Topology!.Samples!;
            Assert.True(samples.Any(s => s.RegionCount > 0), JsonSerializer.Serialize(samples));
            Assert.InRange(samples[1].Seconds - samples[0].Seconds, 0.5, 1.8);
            var profile = await new HeapCaptureService().CaptureAsync(new(target), TestContext.Current.CancellationToken);
            Assert.True(profile.MetadataOnly);
            Assert.NotEmpty(profile.Regions);
            Assert.Empty(profile.Objects);
            await using var vm = new MainViewModel();
            vm.Snapshots.Add(profile);
            vm.SelectedSnapshot = profile;
            vm.ShowCapturedRegions();
            Assert.False(vm.HasObjectCapture);
            Assert.NotEmpty(vm.CapturedRegionMap.Regions);
        }
        finally
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("exit");

                if (!process.WaitForExit(2000))
                {
                    process.Kill();
                }
            }
        }
    }

}
