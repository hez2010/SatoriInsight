namespace SatoriInsight.Models;

public sealed record HeapCaptureRequest(LocalDotNetProcess? Process, string? DumpPath = null, int MaxObjects = 250000, int MaxReferences = 1000000);

public sealed record HeapCaptureResponse(HeapProfile? Profile, string? Error);
