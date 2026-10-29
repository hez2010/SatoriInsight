using System.Runtime;
using SatoriInsight.Models;

namespace SatoriInsight.Services.Heap;

// PR #105 (.NET 10: #107): non-empty free-list mask moves the region queue and occupancy fields.
internal class LayoutVariant04 : LayoutVariant03
{
    public override int PullRequest => 105;

    public override bool TryMatch(SatoriHeapProbe heap, out GCLatencyMode latencyMode)
    {
        latencyMode = default;
        const int isLowLatencyModeOffset = 253; // SatoriRecycler::m_isLowLatencyMode
        const int ticksPerMillisecondOffset = 608; // SatoriRecycler::m_osTicksPerMilli
        const int recyclerOffset = 640; // sizeof(SatoriAllocator)

        return heap.TryReadRecycler(recyclerOffset, out var recycler)
            && recycler.HasOrdinaryRegionQueues()
            && recycler.HasIndependentTimeStampClock(ticksPerMillisecondOffset)
            && recycler.TryReadLatencyMode(isLowLatencyModeOffset, out latencyMode);
    }

    public override RegionHeaderInfo? Read(SatoriRegionProbe region) => region.Read(
        usedEndOffset: 80,              // m_used
        committedEndOffset: 88,         // m_committed
        containingQueueOffset: 152,      // m_containingQueue
        occupancyOffset: 160,            // m_occupancy
        objectCountOffset: 176,          // m_objCount
        hasPinnedObjectsOffset: 184);    // m_hasPinnedObjects
}
