using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.Conversations;

[JsonConverter(typeof(JsonStringEnumConverter<ConversationTurnRole>))]
public enum ConversationTurnRole
{
    User,
    Assistant,
    System,
    Tool,
}

public sealed record ConversationTurn(
    string TurnId,
    string Speaker,
    ConversationTurnRole Role,
    string Content,
    DateTimeOffset Timestamp,
    string? RunId = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
