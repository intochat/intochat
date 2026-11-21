using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Agents;

namespace IntoChat.OS.Modules.Flutter.Conversations;

public sealed record ConversationView(
    ConversationId Id,
    long Revision,
    Participant[] Participants,
    Entry[] Entries,
    AgentRun? Active,
    AgentRun[] Runs,
    bool Blocked,
    string[] Contexts)
{
    public EntryId? Replacing { get; init; }
}
