using System.Runtime;

namespace SatoriInsight.Services.Heap;

// PR #95 (.NET 10: #96): reusable-region filtering and padded region queues.
internal class LayoutVariant03 : LayoutVariant02
{
    public override int PullRequest => 95;

    public override bool TryMatch(SatoriHeapProbe heap, out GCLatencyMode latencyMode)
    {
        latencyMode = default;
        const int isLowLatencyModeOffset = 253; // SatoriRecycler::m_isLowLatencyMode
        const int ticksPerMillisecondOffset = 600; // SatoriRecycler::m_osTicksPerMilli
        const int recyclerOffset = 640; // sizeof(SatoriAllocator)

        return heap.TryReadRecycler(recyclerOffset, out var recycler)
            && recycler.HasOrdinaryRegionQueues()
            && recycler.HasIndependentTimeStampClock(ticksPerMillisecondOffset)
            && recycler.TryReadLatencyMode(isLowLatencyModeOffset, out latencyMode);
    }
}
