using System.Runtime;

namespace SatoriInsight.Services.Heap;

// PR #91 (.NET 10: #92): independent timestamp frequency alongside the OS clock.
internal class LayoutVariant02 : LayoutVariant01
{
    public override int PullRequest => 91;

    public override bool TryMatch(SatoriHeapProbe heap, out GCLatencyMode latencyMode)
    {
        latencyMode = default;
        const int isLowLatencyModeOffset = 249; // SatoriRecycler::m_isLowLatencyMode
        const int ticksPerMillisecondOffset = 584; // SatoriRecycler::m_osTicksPerMilli
        const int recyclerOffset = 640; // sizeof(SatoriAllocator)

        return heap.TryReadRecycler(recyclerOffset, out var recycler)
            && recycler.HasOrdinaryRegionQueues()
            && recycler.HasIndependentTimeStampClock(ticksPerMillisecondOffset)
            && recycler.TryReadLatencyMode(isLowLatencyModeOffset, out latencyMode);
    }
}
