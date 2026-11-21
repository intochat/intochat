using DigitalBrain.State;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.Flutter.Conversations;

namespace IntoChat.OS.Modules.AI.Conversations;

[ExportableState]
internal sealed record ConversationState
{
    public long Revision { get; init; }
    public Participant[] Participants { get; init; } = [];
    public SeatedAgent[] Agents { get; init; } = [];
    public LoggedEntry[] Log { get; init; } = [];
    public AgentRun? ActiveRun { get; init; }
    public EntryId? Replacing { get; init; }
    public AgentRun[] Runs { get; init; } = [];
    public bool Imported { get; init; }
    public bool Blocked { get; init; }
}

internal sealed record SeatedAgent(string AgentId, AgentDefinition Definition);
