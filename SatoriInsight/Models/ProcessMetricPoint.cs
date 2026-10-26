using System.Text.Json.Serialization;

namespace SatoriInsight.Models;

public sealed record ProcessMetricPoint(double Seconds, double? CpuSeconds, double WallClockSeconds, long? ResidentBytes)
{
    [JsonIgnore]
    public double? ResidentMiB => ResidentBytes / 1_048_576d;
}
