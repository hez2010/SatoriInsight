using System.Runtime;
using SatoriInsight.Models;

namespace SatoriInsight.Services.Heap;

internal interface ISatoriLayoutVariant
{
    // The pull request number that introduced this layout variant.
    int PullRequest { get; }

    // Independent heap features, beyond the region header itself.
    bool TryMatch(SatoriHeapProbe heap, out GCLatencyMode latencyMode);

    // Returns null when this interpretation is invalid or the metadata changed.
    RegionHeaderInfo? Read(SatoriRegionProbe region);
}

internal static class SatoriLayouts
{
    // Latest validated match wins. Keep new variants first; never infer a
    // Satori layout from the CLR version or persist a match across samples.
    internal static readonly ISatoriLayoutVariant[] All =
    [
        new LayoutVariant09(), new LayoutVariant08(), new LayoutVariant07(),
        new LayoutVariant06(), new LayoutVariant05(), new LayoutVariant04(),
        new LayoutVariant03(), new LayoutVariant02(), new LayoutVariant01()
    ];

    internal static int AllMask => (1 << All.Length) - 1;
}
