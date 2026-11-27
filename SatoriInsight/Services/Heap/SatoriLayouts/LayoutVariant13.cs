using System.Runtime;
using SatoriInsight.Models;

namespace SatoriInsight.Services.Heap;

// PR #156 (.NET 10: #157): twelve relocation-target queues and separate allocation and marking pin flags.
internal sealed class LayoutVariant13 : LayoutVariant12
{
    public override int PullRequest => 156;

    internal const int PinnedByMarkOffset = 264;

    public override bool TryMatch(SatoriHeapProbe heap, out GCLatencyMode latencyMode)
    {
        latencyMode = default;
        return heap.TryReadRecycler(9216, out var recycler)
            && recycler.HasShardedRegionQueues(relocatedToHigherGenOffset: 200)
            && recycler.HasIndependentTimeStampClock(osTicksPerMillisecondOffset: 784)
            && recycler.TryReadLatencyMode(offset: 404, out latencyMode);
    }

    public override RegionHeaderInfo? Read(SatoriRegionProbe region)
    {
        // m_wasPinnedByMark is historical. Only the allocation flag and current mark describe present pins.
        if (!region.TryReadFlag(PinnedByMarkOffset, out var pinnedByMark))
        {
            return null;
        }

        var metadata = base.Read(region);
        return pinnedByMark && metadata is { HasPinnedObjects: false } ? metadata with { HasPinnedObjects = true } : metadata;
    }

    public override long? ReadCandidateOccupancy(IEnumerable<SatoriRegionProbe> regions)
    {
        long bytes = 0;
        foreach (var region in regions)
        {
            if (region.ReadTaggedOccupancy(tagOffset: 20, occupancyOffset: 168) is not { } occupancy)
            {
                return null;
            }

            bytes += occupancy;
        }

        return bytes;
    }
}
