using SatoriInsight.Models;

namespace SatoriInsight.Services.Heap;

// PR #144 (.NET 10: #145): usable-capacity accounting moves the region's object count and flags.
internal sealed class LayoutVariant10 : LayoutVariant09
{
    public override int PullRequest => 144;

    public override RegionHeaderInfo? Read(SatoriRegionProbe region)
    {
        // m_usableAtAttach replaces the old escaped/reclaimed byte counters at
        // this offset. Its bound helps distinguish older headers when populated.
        if (!region.IsSizeWithinRegion(208))
        {
            return null;
        }

        return region.Read(
            usedEndOffset: 88,             // m_used
            committedEndOffset: 96,        // m_committed
            containingQueueOffset: 160,    // m_containingQueue
            occupancyOffset: 168,          // m_occupancy
            objectCountOffset: 232,        // m_objCount
            hasPinnedObjectsOffset: 240);  // m_hasPinnedObjects
    }
}
