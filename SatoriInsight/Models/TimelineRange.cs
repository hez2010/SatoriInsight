namespace SatoriInsight.Models;

public readonly record struct TimelineRange(double Start, double End)
{
    public double Duration => End - Start;

    public bool Contains(double seconds) => seconds >= Start && seconds <= End;

    public double FractionAt(double seconds) => (seconds - Start) / Duration;

    public double TimeAt(double fraction) => double.Lerp(Start, End, Math.Clamp(fraction, 0, 1));
}
