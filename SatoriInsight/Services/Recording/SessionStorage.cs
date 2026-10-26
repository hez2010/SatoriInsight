using System.Text.Json;
using SatoriInsight.Models;

namespace SatoriInsight.Services.Recording;

public static class SessionStorage
{
    public const int CurrentSchemaVersion = 11;

    public static string Write(SavedSession session) => JsonSerializer.Serialize(session, ProfilerJsonContext.Default.SavedSession);

    public static SavedSession Read(string json)
    {
        var session = JsonSerializer.Deserialize(json, ProfilerJsonContext.Default.SavedSession) ?? throw new InvalidDataException("Empty session.");
        return Validate(session);
    }

    public static async Task<SavedSession> ReadAsync(Stream stream, CancellationToken token)
    {
        var session = await JsonSerializer.DeserializeAsync(stream, ProfilerJsonContext.Default.SavedSession, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Empty session.");
        return Validate(session);
    }

    public static Task WriteAsync(Stream stream, SavedSession session, CancellationToken token) =>
        JsonSerializer.SerializeAsync(stream, session, ProfilerJsonContext.Default.SavedSession, token);

    private static SavedSession Validate(SavedSession session)
    {
        if (session.SchemaVersion != CurrentSchemaVersion || session.Source != "local-eventpipe" || !Nonnegative(session.SessionSeconds)
            || session.SessionSeconds > TimeSpan.MaxValue.TotalSeconds - 1)
        {
            throw new InvalidDataException("Unsupported or invalid session.");
        }

        if (session.Metrics is null || session.Collections is null || session.PauseCounters is null || session.Regions is null
            || session.Snapshots is null || session.ProcessMetrics is null || session.GcModes is null)
        {
            throw new InvalidDataException("Session is missing telemetry arrays.");
        }

        bool Time(double seconds) => Nonnegative(seconds) && seconds <= session.SessionSeconds;

        if (session.Collections.Any(gc => gc is null || gc.Number < 0 || gc.Generation is < 0 or > 2 || !Time(gc.EndSeconds)
            || !Nonnegative(gc.PauseMilliseconds) || !Nonnegative(gc.SuspendMilliseconds) || !Nonnegative(gc.DurationMilliseconds))
            || session.Metrics.Any(p => p is null
            || !Time(p.Seconds) || !Optional(p.UsedMiB) || !Optional(p.CommittedMiB) || !Optional(p.AllocationMiB)) || session.PauseCounters.Any(p => p is null
            || !Time(p.EndSeconds) || !Nonnegative(p.PauseMilliseconds) || !Nonnegative(p.IntervalSeconds) || p.IntervalSeconds == 0))
        {
            throw new InvalidDataException("Session contains invalid timing or counter values.");
        }

        ValidateProcessMetrics(session.ProcessMetrics, session.SessionSeconds);

        var previousModeTime = 0d;

        foreach (var sample in session.GcModes)
        {
            if (sample is null || !Time(sample.Seconds) || sample.Seconds < previousModeTime ||
                sample.Collector is null && sample.LatencyMode is null ||
                sample.Collector is { } collector && !Enum.IsDefined(collector) ||
                sample.LatencyMode is { } latency && !Enum.IsDefined(latency))
            {
                throw new InvalidDataException("Invalid GC mode history.");
            }

            previousModeTime = sample.Seconds;
        }

        foreach (var gc in session.Collections)
        {
            if (gc.Memory is { } memory && (memory.HeapBeforeBytes < 0 || memory.HeapAfterBytes < 0 || memory.PromotedBytes < 0 || memory.Generations is null
                || memory.Generations.Any(g => g is null
                || string.IsNullOrEmpty(g.Generation) || g.BeforeBytes < 0 || g.AfterBytes < 0 || g.PromotedBytes < 0)))
            {
                throw new InvalidDataException("Invalid collection memory statistics.");
            }

            if (gc.ThreadLocal is { } local && (gc.Kind != "ThreadLocal" || gc.Generation != 0 || gc.Number > uint.MaxValue
                || local.ClrInstanceId is < 0 or > ushort.MaxValue || local.ThreadId < 0 || !Nonnegative(local.DurationNanoseconds)
                || gc.DurationMilliseconds != local.DurationNanoseconds / 1_000_000 || gc.PauseMilliseconds != 0 || gc.SuspendMilliseconds != 0)
                || gc.Kind == "ThreadLocal"
                && !gc.IsThreadLocal)
            {
                throw new InvalidDataException("Invalid ThreadLocal collection.");
            }
        }

        ValidateMetrics(session.HeapMetrics);
        ValidateCounts(session.GcCounts);
        var previousMetricTime = 0d;

        foreach (var point in session.Metrics)
        {
            if (point.Seconds < previousMetricTime)
            {
                throw new InvalidDataException("Heap measurements are out of order.");
            }

            previousMetricTime = point.Seconds;
            ValidateMetrics(point.HeapMetrics);
            ValidateCounts(point.GcCounts);
        }

        ValidateRegions(session.Regions);

        if (session.Snapshots.Length > 4 || session.Snapshots.Any(p => p is null)
            || session.Snapshots.Select(p => p.Id).Distinct().Count() != session.Snapshots.Length)
        {
            throw new InvalidDataException("Session contains invalid or too many captures (maximum four).");
        }

        foreach (var profile in session.Snapshots)
        {
            ValidateProfile(profile);
        }

        if (session.Topology is { } topology)
        {
            if (!Time(topology.StartSeconds) || topology.InitialRegions is null || topology.Changes is null || topology.Samples is null)
            {
                throw new InvalidDataException("Invalid topology history.");
            }

            ValidateRegions(topology.InitialRegions);
            var lastSample = 0d;

            foreach (var sample in topology.Samples)
            {
                if (sample is null || !Time(sample.Seconds) || sample.Seconds < lastSample || sample.RegionCount < 0 || sample.SkippedRegions < 0
                    || sample.BytesRead < 0 || !Nonnegative(sample.ReadMilliseconds))
                {
                    throw new InvalidDataException("Invalid region sampling history.");
                }

                lastSample = sample.Seconds;
            }

            var previous = topology.StartSeconds;

            foreach (var change in topology.Changes)
            {
                if (change is null || !Time(change.Seconds) || change.Seconds < previous)
                {
                    throw new InvalidDataException("Topology changes are out of order.");
                }

                previous = change.Seconds;

                if (change.Created is { } created)
                {
                    ValidateRegions([created]);

                    if (created.StartAddress != change.Address || created.RuntimeId != change.RuntimeId || change.Reset)
                    {
                        throw new InvalidDataException("Invalid segment change.");
                    }
                }
            }
        }

        return session;
    }

    private static bool Nonnegative(double value) => double.IsFinite(value) && value >= 0;

    private static bool Optional(double? value) => value is null || Nonnegative(value.Value);

    private static void ValidateProcessMetrics(ProcessMetricPoint[] samples, double sessionSeconds)
    {
        var previousTime = -1d;
        var previousWallTime = -1d;
        double? previousCpu = null;

        foreach (var sample in samples)
        {
            if (sample is null || !Nonnegative(sample.Seconds) || sample.Seconds > sessionSeconds || sample.Seconds <= previousTime ||
                !Nonnegative(sample.WallClockSeconds) || sample.WallClockSeconds < previousWallTime ||
                sample.WallClockSeconds > TimeSpan.MaxValue.TotalSeconds - 1 || !Optional(sample.CpuSeconds) ||
                sample.CpuSeconds > TimeSpan.MaxValue.TotalSeconds - 1 || sample.CpuSeconds < previousCpu || sample.ResidentBytes < 0)
            {
                throw new InvalidDataException("Invalid process measurements.");
            }

            previousTime = sample.Seconds;
            previousWallTime = sample.WallClockSeconds;
            previousCpu = sample.CpuSeconds ?? previousCpu;
        }
    }

    private static void ValidateCounts(GcCollectionCounts? counts)
    {
        if (counts is { } c && (c.Gen0 < 0 || c.Gen1 < 0 || c.Gen2 < 0))
        {
            throw new InvalidDataException("Invalid GC generation counts.");
        }
    }

    private static void ValidateMetrics(RuntimeHeapMetrics? metrics)
    {
        if (metrics is null)
        {
            return;
        }

        double?[] values =
        [
            metrics.HeapBytes, metrics.CommittedBytes, metrics.AllocationBytesPerSecond, metrics.Gen0Bytes, metrics.Gen1Bytes, metrics.Gen2Bytes,
            metrics.LohBytes, metrics.PohBytes, metrics.FragmentationPercent
        ];

        if (values.Any(v => !Optional(v) || v > long.MaxValue) || metrics.HeapCount < 0)
        {
            throw new InvalidDataException("Invalid heap counters.");
        }
    }

    private static void ValidateTypes(IReadOnlyList<TypeStat> types)
    {
        if (types.Any(t => t is null || string.IsNullOrEmpty(t.Name) || t.Bytes < 0 || t.Count < 0)
            || types.Select(t => t.Name).Distinct().Count() != types.Count || types.Sum(t => (decimal)t.Bytes) > long.MaxValue
            || types.Sum(t => (decimal)t.Count) > int.MaxValue)
        {
            throw new InvalidDataException("Invalid type census.");
        }
    }

    private static void ValidateRegions(HeapRegion[] regions)
    {
        if (regions.Any(r => r is null || r.Types is null || r.ReservedBytes < 0 || r.CommittedBytes < 0 || r.ObjectBytes < 0 || r.FreeBytes < 0
            || r.ObjectCount < 0 || r.PinnedObjects < 0 || r.LargestHole < 0 || r.StartAddress > ulong.MaxValue - (ulong)r.ReservedBytes
            && r.ObjectRangeEnd == 0 || r.EndAddress < r.StartAddress || !Enum.IsDefined(r.Generation))
            || regions.Select(r => r.Id).Distinct().Count() != regions.Length
            || regions.Select(r => (r.RuntimeId, r.StartAddress)).Distinct().Count() != regions.Length
            || regions.Sum(r => (decimal)r.ObjectBytes + r.FreeBytes + r.CommittedBytes + r.ReservedBytes) > long.MaxValue)
        {
            throw new InvalidDataException("Invalid heap regions.");
        }

        foreach (var region in regions)
        {
            ValidateTypes(region.Types);

            if (region.UsedRangeBytes is { } used && (used < 0 || (ulong)used > region.EndAddress - region.StartAddress
                || region.ReservedBytes != (long)(region.EndAddress - region.StartAddress) || region.Header is not null || region.DetailsKnown
                || region.CommittedBytes != 0 || region.ObjectBytes != 0 || region.FreeBytes != 0 || region.ObjectCount != 0 || region.PinnedObjects != 0))
            {
                throw new InvalidDataException("Invalid generation range.");
            }

            if (region.Header is { } h && (h.PageAddress >= region.StartAddress || h.PageEnd < region.EndAddress || h.Generation is < -1 or > 3
                || h.UsedEnd < region.StartAddress || h.UsedEnd > h.CommittedEnd || h.CommittedEnd > region.EndAddress || h.ReportedOccupancyBytes < 0
                || (ulong)h.ReportedOccupancyBytes > region.EndAddress - region.StartAddress || h.ReportedObjectCount < 0 || (h.Queue.HasValue
                && !Enum.IsDefined(h.Queue.Value)) || !Enum.IsDefined(h.ReusableFor) || region.DetailsKnown
                || region.ObjectBytes != 0 || region.FreeBytes != 0 || region.ObjectCount != 0 || region.PinnedObjects != 0
                || region.CommittedBytes != (long)(h.CommittedEnd - region.StartAddress) || region.ReservedBytes != (long)(region.EndAddress - h.CommittedEnd)
                || !(h.AllocationStart == 0
                && h.AllocationEnd == 0 || h.AllocationStart >= region.StartAddress
                && h.AllocationStart <= h.AllocationEnd && h.AllocationEnd <= region.EndAddress)))
            {
                throw new InvalidDataException("Invalid region header metadata.");
            }
        }
    }

    private static void ValidateProfile(HeapProfile profile)
    {
        if (profile.Regions is null || profile.Types is null || profile.Objects is null || profile.Roots is null || profile.Holes is null
            || profile.Warnings is null || profile.Objects.Length > 2000000 || profile.Roots.Length > 100000 || profile.Holes.Length > 2000000
            || !Nonnegative(profile.CaptureMilliseconds) || !Nonnegative(profile.AnalysisMilliseconds))
        {
            throw new InvalidDataException("Invalid captured heap.");
        }

        ValidateRegions(profile.Regions);
        ValidateTypes(profile.Types);

        if (profile.MetadataOnly && (profile.Objects.Length != 0 || profile.Roots.Length != 0 || profile.Holes.Length != 0 || profile.Types.Length != 0
            || profile.Regions.Any(r => r.Header is null)))
        {
            throw new InvalidDataException("Region metadata capture contains an object census.");
        }

        var regions = profile.Regions.ToDictionary(r => r.Id);
        bool InRegion(int id, ulong address, long size) => size > 0 && regions.TryGetValue(id, out var region) && address >= region.StartAddress
            && address < region.EndAddress && (ulong)size <= region.EndAddress - address;

        foreach (var obj in profile.Objects)
        {
            if (obj is null || string.IsNullOrEmpty(obj.Type) || obj.References is null || !InRegion(obj.RegionId, obj.Address, obj.Size)
                || !Enum.IsDefined(obj.Generation))
            {
                throw new InvalidDataException($"Invalid object {obj?.AddressLabel} (size {obj?.Size}, region {obj?.RegionId}).");
            }
        }

        foreach (var hole in profile.Holes)
        {
            if (hole is null || !InRegion(hole.RegionId, hole.Address, hole.Size))
            {
                throw new InvalidDataException($"Invalid free range at 0x{hole?.Address:X} (size {hole?.Size}, region {hole?.RegionId}).");
            }
        }

        if (profile.Objects.Select(o => o.Address).Distinct().Count() != profile.Objects.Length || profile.Objects.Sum(o => (long)o.References.Length) > 8000000
            || profile.Objects.Any(o => o.References.Any(r => r is null
            || r.Field is null)) || profile.Roots.Any(r => r is null
            || r.Kind is null))
        {
            throw new InvalidDataException("Invalid objects, roots, or free ranges.");
        }
    }
}
