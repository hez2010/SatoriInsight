using System.Text.Json.Serialization;
using SatoriInsight.Models;

namespace SatoriInsight.Services.Recording;

[JsonSourceGenerationOptions(RespectRequiredConstructorParameters = true, MaxDepth = 64)]
[JsonSerializable(typeof(SavedSession))]
[JsonSerializable(typeof(HeapCaptureRequest))]
[JsonSerializable(typeof(HeapCaptureResponse))]
internal partial class ProfilerJsonContext : JsonSerializerContext
{
}
