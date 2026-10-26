using System.Globalization;
using System.Text.Json.Serialization;

namespace SatoriInsight.Models;

public enum HeapGeneration
{
    Gen0,
    Gen1,
    Gen2,
    Loh,
    Poh,
    Frozen,
    Mixed,
    Unknown,
    Gen3,
    Unassigned
}

public enum RegionColorMode
{
    Generation,
    Occupancy,
    Fragmentation,
    Queue
}

public sealed record HeapRegion(
    int Id, int HeapIndex, ulong StartAddress, long ReservedBytes, long CommittedBytes, long ObjectBytes, long FreeBytes, HeapGeneration Generation,
    int PinnedObjects, int ObjectCount, IReadOnlyList<TypeStat> Types, bool DetailsKnown = true, ulong ObjectRangeEnd = 0, long LargestHole = 0,
    string Kind = "Region", int RuntimeId = 0, RegionHeaderInfo? Header = null, long? UsedRangeBytes = null)
{
    public ulong EndAddress => ObjectRangeEnd != 0 ? ObjectRangeEnd : StartAddress + (ulong)ReservedBytes;

    public double Occupancy => Header is { Generation: >= 0 } h
        ? (double)h.ReportedOccupancyBytes / Math.Max(1, (double)(EndAddress - StartAddress))
        : CommittedBytes == 0 ? 0 : (double)ObjectBytes / CommittedBytes;

    public double Fragmentation => ObjectBytes + FreeBytes == 0 ? 0 : (double)FreeBytes / (ObjectBytes + FreeBytes);

    public long UnparsedBytes => (long)Math.Clamp((decimal)(EndAddress - StartAddress) - ObjectBytes - FreeBytes, 0, long.MaxValue);

    public string Label => $"R{Id:000}";

    [JsonIgnore]
    public HeapGeneration DisplayGeneration => Header?.HeapGeneration ?? Generation;

    public string GenerationLabel => DisplayGeneration.ToString();

    public string AddressLabel => $"0x{StartAddress:X12}";

    public string SizeLabel => Format.Bytes(CommittedBytes);
}

public sealed record TypeStat(string Name, int Count, long Bytes)
{
    public string SizeLabel => Format.Bytes(Bytes);

    public string CountLabel => Count.ToString("N0", CultureInfo.InvariantCulture);
}
