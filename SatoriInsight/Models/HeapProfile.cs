namespace SatoriInsight.Models;

public sealed record ObjectReference(ulong Address, string Field);

public sealed record HeapObjectInfo(ulong Address, string Type, long Size, int RegionId, HeapGeneration Generation, ObjectReference[] References)
{
    public string AddressLabel => $"0x{Address:X}";

    public string SizeLabel => Format.Bytes(Size);

    public string GenerationLabel => Generation.ToString();
}

public sealed record HeapRootInfo(ulong Address, ulong ObjectAddress, string Kind, bool Pinned);

public sealed record FreeHole(ulong Address, long Size, int RegionId)
{
    public string AddressLabel => $"0x{Address:X}";

    public string SizeLabel => Format.Bytes(Size);
}

public sealed record HeapProfile(
    Guid Id, DateTime CapturedUtc, LocalDotNetProcess? Process, string Runtime, double CaptureMilliseconds, double AnalysisMilliseconds, HeapRegion[] Regions,
    TypeStat[] Types, HeapObjectInfo[] Objects, HeapRootInfo[] Roots, FreeHole[] Holes, string[] Warnings, bool MetadataOnly = false)
{
    public string Label
    {
        get
        {
            var size = MetadataOnly ? $"{Regions.Length:N0} regions" : Format.Bytes(Regions.Sum(region => region.ObjectBytes));
            return $"{CapturedUtc.ToLocalTime():HH:mm:ss} · {size}";
        }
    }

    public string Coverage => string.Join(" ", Warnings);
}

public sealed record RootPathStep(int Depth, string Link, ulong Address, string Type)
{
    public string AddressLabel => $"0x{Address:X}";
}
