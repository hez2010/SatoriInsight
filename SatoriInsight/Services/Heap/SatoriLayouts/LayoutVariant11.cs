using SatoriInsight.Models;

namespace SatoriInsight.Services.Heap;

// PR #147: the thread-local bitmap and movable survivor count move the object count and flags.
internal sealed class LayoutVariant11 : LayoutVariant10
{
    public override int PullRequest => 147;

    public override RegionHeaderInfo? Read(SatoriRegionProbe region)
    {
        if (!region.IsSizeWithinRegion(208)) // m_usableAtAttach
        {
            return null;
        }

        return region.Read(
            usedEndOffset: 88,             // m_used
            committedEndOffset: 96,        // m_committed
            containingQueueOffset: 160,    // m_containingQueue
            occupancyOffset: 168,          // m_occupancy
            objectCountOffset: 240,        // m_objCount
            hasPinnedObjectsOffset: 252);  // m_hasPinnedObjects
    }
}
