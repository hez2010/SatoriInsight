using System.Runtime;
using SatoriInsight.Models;

namespace SatoriInsight.Services.Heap;

// PR #74: demoted occupancy and free-list capacities. The recent pre-#91 layout on both branches.
internal class LayoutVariant01 : ISatoriLayoutVariant
{
    public virtual int PullRequest => 74;

    public virtual bool TryMatch(SatoriHeapProbe heap, out GCLatencyMode latencyMode)
    {
        latencyMode = default;
        const int isLowLatencyModeOffset = 249; // SatoriRecycler::m_isLowLatencyMode
        const int ticksPerMillisecondOffset = 584; // SatoriRecycler::m_perfCounterTicksPerMilli
        const int recyclerOffset = 640; // sizeof(SatoriAllocator)

        return heap.TryReadRecycler(recyclerOffset, out var recycler)
            && recycler.HasOrdinaryRegionQueues()
            && recycler.HasPerformanceCounterClock(ticksPerMillisecondOffset)
            && recycler.TryReadLatencyMode(isLowLatencyModeOffset, out latencyMode);
    }

    public virtual RegionHeaderInfo? Read(SatoriRegionProbe region) => region.Read(
        usedEndOffset: 80,              // m_used
        committedEndOffset: 88,         // m_committed
        containingQueueOffset: 144,      // m_containingQueue
        occupancyOffset: 152,            // m_occupancy
        objectCountOffset: 168,          // m_objCount
        hasPinnedObjectsOffset: 176);    // m_hasPinnedObjects
}
