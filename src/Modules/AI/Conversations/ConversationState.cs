using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Components;
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

    public ThreadTrustPosture TrustPosture { get; init; } = ThreadTrustPosture.OwnerTrusted;
    public ThreadKind Kind { get; init; } = ThreadKind.DirectAssistant;
    public bool PostureLocked { get; init; }
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LeadAgentId { get; init; }
    public PinnedScreen[] PinnedScreens { get; init; } = [];
    public SeatedMember[] Members { get; init; } = [];
}

internal sealed record SeatedAgent(string AgentId, AgentDefinition Definition);

internal sealed record SeatedMember(string Account, BrainId Brain, Role Role);

internal sealed class DefaultConversationClock : TimeProvider
{
    public static TimeProvider Instance { get; } = new DefaultConversationClock();
}
