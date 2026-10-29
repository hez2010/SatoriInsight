using System.Runtime;
using System.Runtime.InteropServices;

namespace SatoriInsight.Services.Heap;

// PR #132 (.NET 10: #135): per-processor allocated-byte counters enlarge SatoriAllocator.
internal class LayoutVariant07 : LayoutVariant06
{
    public override int PullRequest => 132;

    public override bool TryMatch(SatoriHeapProbe heap, out GCLatencyMode latencyMode)
    {
        latencyMode = default;
        const int isLowLatencyModeOffset = 388; // SatoriRecycler::m_isLowLatencyMode
        const int ticksPerMillisecondOffset = 720; // SatoriRecycler::m_osTicksPerMilli
        var recyclerOffset = heap.Architecture == Architecture.Arm64 ? 8960 : 4736; // sizeof(SatoriAllocator)

        return heap.TryReadRecycler(recyclerOffset, out var recycler)
            && recycler.HasOrdinaryRegionQueues()
            && recycler.HasIndependentTimeStampClock(ticksPerMillisecondOffset)
            && recycler.TryReadLatencyMode(isLowLatencyModeOffset, out latencyMode);
    }
}
