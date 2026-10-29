using SatoriInsight.Models;

namespace SatoriInsight.Services.Heap;

// PR #120 (.NET 10: #121): zeroed allocation spans and thread-local collection windows change the region header.
internal class LayoutVariant06 : LayoutVariant05
{
    public override int PullRequest => 120;

    public override RegionHeaderInfo? Read(SatoriRegionProbe region) => region.Read(
        usedEndOffset: 88,              // m_used
        committedEndOffset: 96,         // m_committed
        containingQueueOffset: 160,      // m_containingQueue
        occupancyOffset: 168,            // m_occupancy
        objectCountOffset: 220,          // m_objCount
        hasPinnedObjectsOffset: 228);    // m_hasPinnedObjects
}
