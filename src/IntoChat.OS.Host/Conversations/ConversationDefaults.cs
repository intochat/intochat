namespace IntoChat.OS.Host.Conversations;

/// <summary>
/// App-supplied conversation defaults so the AI module remains app-agnostic while honoring shipped apps'
/// default agent name, legacy pre-S3 agent key prefix, and allowed UI surfaces for group invitations.
/// </summary>
public sealed record ConversationDefaults(
    string DefaultAgentId,
    string? LegacyAgentKeyPrefix = null,
    string[]? AllowedInviteSurfaces = null)
{
    public static ConversationDefaults Generic { get; } = new("assistant");
}
