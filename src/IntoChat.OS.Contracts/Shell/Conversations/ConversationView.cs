using IntoChat.OS.Modules.AI.Agents;

namespace IntoChat.OS.Modules.Flutter.Conversations;

public sealed record SeatedAgentSummary(
    string AgentId,
    string DisplayName,
    string? Handle = null,
    string? MemoryScopeKey = null,
    bool IsLead = false);

public sealed record ConversationView(
    ConversationId Id,
    long Revision,
    Participant[] Participants,
    ConversationEntry[] Entries,
    AgentRun? Active,
    AgentRun[] Runs,
    bool Blocked,
    string[] Contexts)
{
    public EntryId? Replacing { get; init; }
    public string? BlockedReason { get; init; }
    public ThreadTrustPosture TrustPosture { get; init; } = ThreadTrustPosture.OwnerTrusted;
    public ThreadKind Kind { get; init; } = ThreadKind.DirectAssistant;
    public string? Title { get; init; }
    public string? LeadAgentId { get; init; }
    public PinnedScreen[] PinnedScreens { get; init; } = [];
    public string[] PresentAccounts { get; init; } = [];
    public SeatedAgentSummary[] SeatedAgents { get; init; } = [];
    public string? HandoffLimitNotice { get; init; }
}
