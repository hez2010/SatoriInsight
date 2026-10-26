using SatoriInsight.Models;

namespace SatoriInsight.Tests;

internal static class TestData
{
    internal static readonly LocalDotNetProcess Target = new(1234, "TestTarget", new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc));

    public static DiagnosticFrame Frame(double at)
    {
        var gc = new GcEvent(1, at - 0.1, 0, "Blocking", "Test", 4, 5, 0.5);
        return new(at, [], [gc], [new(at, 1, 2, 3)], 3, new(
            HeapBytes: 1_048_576, CommittedBytes: 2_097_152, AllocationBytesPerSecond: 3_145_728), new(LastCounterSeconds: at), [new(at, 1, 5)]);
    }
}
