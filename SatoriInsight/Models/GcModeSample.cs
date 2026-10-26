using System.Runtime;

namespace SatoriInsight.Models;

public enum GcCollectorKind
{
    Workstation,
    Server,
    Satori
}

// Each observation reports a collector, a latency mode, or both.
public sealed record GcModeSample(double Seconds, GcCollectorKind? Collector = null, GCLatencyMode? LatencyMode = null);
