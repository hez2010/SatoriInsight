using System.Runtime;
using System.Runtime.InteropServices;

namespace SatoriInsight.Services.Heap;

// PR #139 (.NET 10: #140): recycler queues are sharded; relocated-to-higher-generation remains ordinary.
internal sealed class LayoutVariant09 : LayoutVariant08
{
    public override int PullRequest => 139;

    public override bool TryMatch(SatoriHeapProbe heap, out GCLatencyMode latencyMode)
    {
        latencyMode = default;
        const int isLowLatencyModeOffset = 388; // SatoriRecycler::m_isLowLatencyMode
        const int ticksPerMillisecondOffset = 744; // SatoriRecycler::m_osTicksPerMilli
        var recyclerOffset = heap.Architecture == Architecture.Arm64 ? 8960 : 4736; // sizeof(SatoriAllocator)

        return heap.TryReadRecycler(recyclerOffset, out var recycler)
            && recycler.HasShardedRegionQueues()
            && recycler.HasIndependentTimeStampClock(ticksPerMillisecondOffset)
            && recycler.TryReadLatencyMode(isLowLatencyModeOffset, out latencyMode);
    }
}
