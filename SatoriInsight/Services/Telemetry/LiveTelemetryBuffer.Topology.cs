using SatoriInsight.Models;
using SatoriInsight.Services.Heap;

namespace SatoriInsight.Services.Telemetry;

public sealed partial class LiveTelemetryBuffer
{
    private readonly Dictionary<(int Runtime, ulong Address), HeapRegion> _regions = [];
    private readonly Dictionary<(int Runtime, ulong Address), HeapRegion> _segments = [];
    private readonly Dictionary<int, GcGenerationRange[]> _generationRanges = [];
    private (ulong Start, ulong End)[] _metadataPages = [];
    private readonly Queue<HeapSegmentChange> _topologyChanges = [];
    private readonly List<RegionMapSampleInfo> _regionSamples = [];
    private double _lastTopologySeconds;
    private int _regionId;

    public void SegmentCreated(int runtime, ulong address, ulong size, int kind, double at = 0)
    {
        if (size == 0 || size > long.MaxValue || address > ulong.MaxValue - size)
        {
            return;
        }

        lock (_gate)
        {
            if (OverlapsMetadata(address, address + size))
            {
                return;
            }

            var generation = kind switch
            {
                1 => HeapGeneration.Loh,
                2 => HeapGeneration.Frozen,
                3 => HeapGeneration.Poh,
                _ => HeapGeneration.Unknown
            };
            var region = new HeapRegion(
                _regions.TryGetValue((runtime, address), out var prior) ? prior.Id : _regionId++,
                -1, address, (long)size, 0, 0, 0, generation, 0, 0, [], false, address + size, 0, "Observed segment", runtime);
            Observe(at);
            _segments[(runtime, address)] = region;
            RemoveGenerationRanges(runtime, address, address + size);
            RefreshEventRegions(runtime, TopologyTimestamp(at));
        }
    }

    public void SegmentFreed(int runtime, ulong address, double at = 0)
    {
        lock (_gate)
        {
            if (_metadataPages.Any(page => address >= page.Start && address < page.End))
            {
                return;
            }

            Observe(at);
            var time = TopologyTimestamp(at);
            var hadRegion = _regions.TryGetValue((runtime, address), out var prior) && prior.Header is null;

            if (_segments.Remove((runtime, address), out var segment))
            {
                RemoveGenerationRanges(runtime, address, segment.EndAddress);
            }
            else
            {
                // Without the segment extent, child ranges cannot be identified safely.
                // The next complete generation enumeration restores the map.
                _generationRanges.Remove(runtime);
            }

            RefreshEventRegions(runtime, time);

            if (!hadRegion)
            {
                _topologyChanges.Enqueue(new(time, runtime, address));
            }
        }
    }

    public void GenerationRanges(int runtime, IReadOnlyList<GcGenerationRange> ranges, double at)
    {
        if (!double.IsFinite(at) || at < 0 || ranges.Any(range => !range.IsValid))
        {
            return;
        }

        var ordered = ranges.Where(range => range.ReservedLength > 0).OrderBy(range => range.Start).ToArray();

        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1].End > ordered[index].Start)
            {
                return;
            }
        }

        lock (_gate)
        {
            Observe(at);
            var visible = _metadataPages.Length == 0 ? ordered : ordered.Where(range => !OverlapsMetadata(range.Start, range.End)).ToArray();

            if (_generationRanges.TryGetValue(runtime, out var previous) && previous.SequenceEqual(visible))
            {
                return;
            }

            _generationRanges[runtime] = visible;
            RefreshEventRegions(runtime, TopologyTimestamp(at));
        }
    }

    private void RefreshEventRegions(int runtime, double time)
    {
        var desired = new Dictionary<ulong, HeapRegion>();
        var ranges = _generationRanges.GetValueOrDefault(runtime) ?? [];

        foreach (var range in ranges)
        {
            _regions.TryGetValue((runtime, range.Start), out var prior);

            if (prior is { Header: null, UsedRangeBytes: not null } && prior.Generation == range.HeapGeneration
                && prior.ReservedBytes == (long)range.ReservedLength && prior.UsedRangeBytes == (long)range.UsedLength)
            {
                desired[range.Start] = prior;
                continue;
            }

            var id = prior?.Id ?? _regionId++;
            desired[range.Start] = new(id, -1, range.Start, (long)range.ReservedLength, 0, 0, 0, range.HeapGeneration,
                0, 0, [], DetailsKnown: false, ObjectRangeEnd: range.End, Kind: "Generation range", RuntimeId: runtime,
                UsedRangeBytes: (long)range.UsedLength);
        }

        foreach (var segment in _segments.Values.Where(segment => segment.RuntimeId == runtime))
        {
            if (!Overlaps(ranges, segment.StartAddress, segment.EndAddress))
            {
                desired[segment.StartAddress] = segment;
            }
        }

        foreach (var prior in _regions.Values.Where(region => region.RuntimeId == runtime && region.Header is null).ToArray())
        {
            if (!desired.ContainsKey(prior.StartAddress))
            {
                _regions.Remove((runtime, prior.StartAddress));
                _topologyChanges.Enqueue(new(time, runtime, prior.StartAddress));
            }
        }

        foreach (var region in desired.Values)
        {
            var key = (runtime, region.StartAddress);

            if (_regions.TryGetValue(key, out var prior) && prior == region)
            {
                continue;
            }

            _regions[key] = region;
            _topologyChanges.Enqueue(new(time, runtime, region.StartAddress, region));
        }
    }

    private static bool Overlaps(GcGenerationRange[] ranges, ulong start, ulong end)
    {
        var low = 0;
        var high = ranges.Length;

        while (low < high)
        {
            var middle = low + (high - low) / 2;

            if (ranges[middle].End <= start)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low < ranges.Length && ranges[low].Start < end;
    }

    private bool OverlapsMetadata(ulong start, ulong end) => _metadataPages.Any(page => page.Start < end && start < page.End);

    private void RemoveGenerationRanges(int runtime, ulong start, ulong end)
    {
        if (_generationRanges.TryGetValue(runtime, out var ranges))
        {
            _generationRanges[runtime] = ranges.Where(range => range.End <= start || range.Start >= end).ToArray();
        }
    }

    public void RegionSample(RegionReadResult sample, double at)
    {
        lock (_gate)
        {
            Observe(at);
            var time = TopologyTimestamp(at);
            _regionSamples.Add(new(
                time, sample.Regions.Length, sample.SkippedRegions, sample.Complete, sample.BytesRead, sample.ReadMilliseconds, sample.Error));

            if (!sample.Recognized || sample.Error is not null)
            {
                return;
            }

            var observed = sample.Regions.Select(r => r.StartAddress).ToHashSet();

            if (sample.Complete)
            {
                foreach (var prior in _regions.Values.Where(r => r.Header is not null && !observed.Contains(r.StartAddress)).ToArray())
                {
                    _regions.Remove((prior.RuntimeId, prior.StartAddress));
                    _topologyChanges.Enqueue(new(time, prior.RuntimeId, prior.StartAddress));
                }
            }

            foreach (var incoming in sample.Regions)
            {
                var key = (incoming.RuntimeId, incoming.StartAddress);
                var found = _regions.TryGetValue(key, out var previous);
                // Partial reads may straddle a split/coalesce. Keep old geometry
                // until a complete pass instead of publishing overlapping extents.
                if (!sample.Complete && (found && previous!.EndAddress != incoming.EndAddress || !found && _regions.Values.Any(r => r.Header is not null
                    && r.StartAddress < incoming.EndAddress && incoming.StartAddress < r.EndAddress)))
                {
                    continue;
                }

                var current = incoming with
                {
                    Id = found ? previous!.Id : _regionId++
                };
                // Header samples intentionally contain no object census. Only persist
                // changed metadata, preserving stable IDs and selection between reads.
                if (found && previous!.Header == current.Header && previous.EndAddress == current.EndAddress)
                {
                    continue;
                }

                _regions[key] = current;
                _topologyChanges.Enqueue(new(time, current.RuntimeId, current.StartAddress, current));
            }

            _metadataPages = _regions.Values.Where(region => region.Header is not null)
                .Select(region => (region.Header!.PageAddress, region.Header.PageEnd)).Distinct().ToArray();

            // Direct headers are authoritative for their pages. In particular, raw Gen3
            // is not the standard event protocol's generation 3 (LOH).
            foreach (var region in _regions.Values.Where(region => region.Header is null && OverlapsMetadata(region.StartAddress, region.EndAddress)).ToArray())
            {
                _regions.Remove((region.RuntimeId, region.StartAddress));
                _topologyChanges.Enqueue(new(time, region.RuntimeId, region.StartAddress));
            }

            foreach (var segment in _segments.Values.Where(segment => OverlapsMetadata(segment.StartAddress, segment.EndAddress)).ToArray())
            {
                _segments.Remove((segment.RuntimeId, segment.StartAddress));
            }

            foreach (var runtime in _generationRanges.Keys.ToArray())
            {
                _generationRanges[runtime] = _generationRanges[runtime].Where(range => !OverlapsMetadata(range.Start, range.End)).ToArray();
            }
        }
    }
}
