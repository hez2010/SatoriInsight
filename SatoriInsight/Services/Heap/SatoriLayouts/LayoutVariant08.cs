using System.Runtime;
using System.Runtime.InteropServices;

namespace SatoriInsight.Services.Heap;

// PR #136 (.NET 10: #138): additional allocation-budget history moves the recycler's clocks.
internal class LayoutVariant08 : LayoutVariant07
{
    public override int PullRequest => 136;

    public override bool TryMatch(SatoriHeapProbe heap, out GCLatencyMode latencyMode)
    {
        latencyMode = default;
        const int isLowLatencyModeOffset = 388; // SatoriRecycler::m_isLowLatencyMode
        const int ticksPerMillisecondOffset = 744; // SatoriRecycler::m_osTicksPerMilli
        var recyclerOffset = heap.Architecture == Architecture.Arm64 ? 8960 : 4736; // sizeof(SatoriAllocator)

        return heap.TryReadRecycler(recyclerOffset, out var recycler)
            && recycler.HasOrdinaryRegionQueues()
            && recycler.HasIndependentTimeStampClock(ticksPerMillisecondOffset)
            && recycler.TryReadLatencyMode(isLowLatencyModeOffset, out latencyMode);
    }
}
