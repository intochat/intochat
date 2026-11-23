using System.Text.Json.Serialization;

namespace IntoChat.Aspire;

public sealed record AspireCommand(
    string Name,
    string DisplayName,
    string? Description,
    bool Enabled,
    string? ConfirmationMessage);

public sealed record AspireInstanceView(
    string Id,
    string State,
    string? Health,
    string[] Urls,
    DateTimeOffset? StartedAt,
    DateTimeOffset? StoppedAt,
    AspireCommand[] Commands);

public sealed record AspireResource(
    string Name,
    string Type,
    string? Parent,
    AspireInstanceView[] Instances);

public sealed record AspireResources(
    bool Connected,
    AspireResource[] Items);

public sealed record AspireCommandResult(
    string Resource,
    string Command,
    bool Accepted,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Confirmation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Known = null);
