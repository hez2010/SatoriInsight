namespace SatoriInsight.Models;

public sealed record AppUpdate(Version Version, string Changelog, Uri ReleaseUri)
{
    public string Title => $"Satori Insight {Version.ToString(Version.Revision > 0 ? 4 : 3)} is available";
}
