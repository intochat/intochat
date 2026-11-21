using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.AI.Voice;

internal sealed record SpeechRequest(string Text, string? Voice = null);

internal sealed record TranscriptionResponse(string Text, string Model);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SpeechRequest))]
[JsonSerializable(typeof(TranscriptionResponse))]
internal sealed partial class TalkJson : JsonSerializerContext;
