using System.Runtime;

namespace SatoriInsight.Services.Heap;

// PR #150 (.NET 10: #151): per-page free granules and wide allocation counters.
internal sealed class LayoutVariant12 : LayoutVariant11
{
    public override int PullRequest => 150;

    public override SatoriPageInfo? ReadPage(SatoriPageProbe page) => page.ReadFreeGranules();

    public override bool TryMatch(SatoriHeapProbe heap, out GCLatencyMode latencyMode)
    {
        latencyMode = default;
        const int recyclerOffset = 9216; // sizeof(SatoriAllocator)
        const int isLowLatencyModeOffset = 388; // SatoriRecycler::m_isLowLatencyMode
        const int ticksPerMillisecondOffset = 768; // SatoriRecycler::m_osTicksPerMilli

        return heap.TryReadRecycler(recyclerOffset, out var recycler)
            && recycler.HasShardedRegionQueues()
            && recycler.HasIndependentTimeStampClock(ticksPerMillisecondOffset)
            && recycler.TryReadLatencyMode(isLowLatencyModeOffset, out latencyMode);
    }
}
