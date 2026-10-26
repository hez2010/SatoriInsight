using System.Globalization;

namespace SatoriInsight.Models;

public sealed record TypeGrowth(string Name, long BaselineBytes, long CurrentBytes, int CurrentCount)
{
    public long DeltaBytes => CurrentBytes - BaselineBytes;

    public string BaselineLabel => Format.Bytes(BaselineBytes);

    public string CurrentLabel => Format.Bytes(CurrentBytes);

    public string DeltaLabel => $"{(DeltaBytes >= 0 ? "+" : "−")}{Format.Bytes(Math.Abs(DeltaBytes))}";

    public string CountLabel => CurrentCount.ToString("N0", CultureInfo.InvariantCulture);
}
