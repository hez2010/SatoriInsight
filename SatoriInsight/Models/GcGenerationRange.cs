namespace SatoriInsight.Models;

public readonly record struct GcGenerationRange(int Generation, ulong Start, ulong UsedLength, ulong ReservedLength)
{
    public ulong End => Start + ReservedLength;

    public bool IsValid => Generation is >= 0 and <= 4 && Start != 0 && UsedLength <= ReservedLength
        && ReservedLength <= long.MaxValue && Start <= ulong.MaxValue - ReservedLength;

    public HeapGeneration HeapGeneration => Generation switch
    {
        0 => HeapGeneration.Gen0,
        1 => HeapGeneration.Gen1,
        2 => HeapGeneration.Gen2,
        3 => HeapGeneration.Loh,
        4 => HeapGeneration.Poh,
        _ => HeapGeneration.Unknown
    };
}
