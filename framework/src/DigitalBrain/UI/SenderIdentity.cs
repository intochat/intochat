using System.Text.Json.Serialization;

namespace DigitalBrain.UI;

/// <summary>The kind of speaker who authored a message in a conversation or room.</summary>
public enum SenderKind
{
    Self,
    Human,
    Agent,
}

/// <summary>Authenticated speaker attribution for a message.</summary>
/// <param name="AccountId">Authenticated account or agent identifier.</param>
/// <param name="DisplayName">Human-readable speaker name.</param>
/// <param name="Kind">Whether the speaker is the viewing user (Self), another human participant (Human), or an AI agent (Agent).</param>
/// <param name="RoomScoped">True when an agent speaker is strictly room-scoped (e.g. @Ino · Room only), false for personal 1:1 agents.</param>
public sealed record SenderIdentity(
    string AccountId,
    string DisplayName,
    SenderKind Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool RoomScoped = false);
