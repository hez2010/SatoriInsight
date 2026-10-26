using System.Text.Json.Serialization;

namespace SatoriInsight.Services.Updates;

internal sealed record GitHubRelease(string TagName, string? Body, bool Draft, bool Prerelease);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(GitHubRelease))]
internal partial class ReleaseJsonContext : JsonSerializerContext
{
}
