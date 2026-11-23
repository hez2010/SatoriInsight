using System.Globalization;

namespace SatoriInsight.Models;

public sealed record TypeGrowth(TypeStat? Baseline, TypeStat? Current)
{
    public string Name => (Current ?? Baseline!).Name;

    public long BaselineBytes => Baseline?.Bytes ?? 0;

    public long CurrentBytes => Current?.Bytes ?? 0;

    public long DeltaBytes => CurrentBytes - BaselineBytes;

    public int DeltaCount => (Current?.Count ?? 0) - (Baseline?.Count ?? 0);

    public string BaselineLabel => Format.Bytes(BaselineBytes);

    public string CurrentLabel => Format.Bytes(CurrentBytes);

    public string DeltaLabel => $"{(DeltaBytes >= 0 ? "+" : "−")}{Format.Bytes(Math.Abs(DeltaBytes))}";

    public string DeltaCountLabel => DeltaCount.ToString("+#,0;−#,0;0", CultureInfo.InvariantCulture);

    public string InspectLabel => Current is null ? "Inspect baseline" : "Inspect instances";
}
