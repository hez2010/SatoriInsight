using System.Net.Http.Json;
using SatoriInsight.Models;

namespace SatoriInsight.Services.Updates;

public sealed class UpdateChecker(HttpClient httpClient)
{
    public async Task<AppUpdate?> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/hez2010/SatoriInsight/releases/latest");
            request.Headers.UserAgent.ParseAdd($"SatoriInsight/{currentVersion}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var release = await response.Content.ReadFromJsonAsync(ReleaseJsonContext.Default.GitHubRelease, cancellationToken).ConfigureAwait(false);

            if (release is null || release.Draft || release.Prerelease || string.IsNullOrWhiteSpace(release.TagName))
            {
                return null;
            }

            var tagVersion = release.TagName.StartsWith('v') ? release.TagName[1..] : release.TagName;

            if (!Version.TryParse(tagVersion, out var version) || NormalizeVersion(version) <= NormalizeVersion(currentVersion))
            {
                return null;
            }

            var changelog = string.IsNullOrWhiteSpace(release.Body) ? "Release notes are available on the release page." : release.Body;
            var releaseUri = new Uri($"https://github.com/hez2010/SatoriInsight/releases/tag/{Uri.EscapeDataString(release.TagName)}");
            return new AppUpdate(NormalizeVersion(version), changelog, releaseUri);
        }
        catch (Exception)
        {
            // Update checks must never interrupt startup, including when offline or rate limited.
            return null;
        }
    }

    private static Version NormalizeVersion(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
}
