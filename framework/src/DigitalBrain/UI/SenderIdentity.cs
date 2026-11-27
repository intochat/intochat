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
public sealed record SenderIdentity(string AccountId, string DisplayName, SenderKind Kind);
