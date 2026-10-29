using System.Runtime;

namespace SatoriInsight.Services.Heap;

// PR #116 (.NET 10: #117): collection requests move the recycler's latency mode and clocks.
internal class LayoutVariant05 : LayoutVariant04
{
    public override int PullRequest => 116;

    public override bool TryMatch(SatoriHeapProbe heap, out GCLatencyMode latencyMode)
    {
        latencyMode = default;
        const int isLowLatencyModeOffset = 388; // SatoriRecycler::m_isLowLatencyMode
        const int ticksPerMillisecondOffset = 744; // SatoriRecycler::m_osTicksPerMilli
        const int recyclerOffset = 640; // sizeof(SatoriAllocator)

        return heap.TryReadRecycler(recyclerOffset, out var recycler)
            && recycler.HasOrdinaryRegionQueues()
            && recycler.HasIndependentTimeStampClock(ticksPerMillisecondOffset)
            && recycler.TryReadLatencyMode(isLowLatencyModeOffset, out latencyMode);
    }
}
