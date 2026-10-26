using SatoriInsight.Models;

namespace SatoriInsight.Services.Telemetry;

public static class HeapTopology
{
    public static HeapRegion[] At(HeapTopologyHistory history, double seconds)
    {
        if (seconds < history.StartSeconds)
        {
            return [];
        }

        var regions = history.InitialRegions.ToDictionary(r => (r.RuntimeId, r.StartAddress));

        foreach (var change in history.Changes)
        {
            if (change.Seconds > seconds)
            {
                break;
            }

            Apply(regions, change);
        }

        return regions.Values.OrderBy(r => r.StartAddress).ToArray();
    }

    internal static void Apply(Dictionary<(int Runtime, ulong Address), HeapRegion> regions, HeapSegmentChange change)
    {
        if (change.Reset)
        {
            regions.Clear();
        }
        else if (change.Created is { } region)
        {
            regions[(change.RuntimeId, change.Address)] = region;
        }
        else
        {
            regions.Remove((change.RuntimeId, change.Address));
        }
    }
}
