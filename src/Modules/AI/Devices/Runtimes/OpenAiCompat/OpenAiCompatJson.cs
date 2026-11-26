using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.Devices.Runtimes.OpenAiCompat;

internal sealed record ModelsResponse(ModelEntry[]? Data);

internal sealed record ModelEntry(string Id, string? OwnedBy);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ModelsResponse))]
[JsonSerializable(typeof(ModelEntry))]
internal sealed partial class OpenAiCompatJson : JsonSerializerContext;
