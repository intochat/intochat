using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.Devices.Runtimes.Ollama;

internal sealed record PullRequest(string Model, bool Stream);

internal sealed record PullLine(
    string? Status,
    string? Digest,
    long? Total,
    long? Completed,
    string? Error);

internal sealed record TagsResponse(TagModel[]? Models);

internal sealed record TagModel(
    string Name,
    long Size,
    string? Digest,
    TagDetails? Details);

internal sealed record TagDetails(
    string? Family,
    string? ParameterSize,
    string? QuantizationLevel);

internal sealed record ShowRequest(string Model);

internal sealed record ShowResponse(
    TagDetails? Details,
    string[]? Capabilities);

internal sealed record DeleteRequest(string Model);

internal sealed record PsResponse(PsModel[]? Models);

internal sealed record PsModel(
    string Name,
    long Size,
    long SizeVram);

internal sealed record VersionResponse(string? Version);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(PullRequest))]
[JsonSerializable(typeof(PullLine))]
[JsonSerializable(typeof(TagsResponse))]
[JsonSerializable(typeof(TagModel))]
[JsonSerializable(typeof(TagDetails))]
[JsonSerializable(typeof(ShowRequest))]
[JsonSerializable(typeof(ShowResponse))]
[JsonSerializable(typeof(DeleteRequest))]
[JsonSerializable(typeof(PsResponse))]
[JsonSerializable(typeof(PsModel))]
[JsonSerializable(typeof(VersionResponse))]
internal sealed partial class OllamaJson : JsonSerializerContext;
