using SatoriInsight.Models;
using SatoriInsight.Services.Heap;

namespace SatoriInsight.Tests;

internal static class RegionTestData
{
    public static RegionReadResult Sample()
    {
        const ulong page = 0x40000000;
        const ulong size = 2 * 1024 * 1024;
        var regions = Enumerable.Range(1, 2).Select(index =>
        {
            var start = page + (ulong)index * size;
            var header = new RegionHeaderInfo(page, page + 0x40000000, 2, RegionQueueKind.RecyclerTenured, RegionReuseLevel.None,
                start + 0x100000, start + size, 0, 0, 4096, 16, false);
            return new HeapRegion(index, -1, start, 0, (long)size, 0, 0, HeapGeneration.Gen2, 0, 0, [],
                DetailsKnown: false, ObjectRangeEnd: start + size, RuntimeId: -1, Header: header);
        }).ToArray();
        return new(true, regions, 0, true, 0, 0);
    }
}
