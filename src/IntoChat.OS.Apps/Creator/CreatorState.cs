using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Trace;

namespace IntoChat.OS.Apps.Creator;

// Selected is what the inspector shows; TraceId the trace last opened (a pulse); ReplayCursor the replay scrubber's step (an index into its signals in sequence order, null when live);
// ConversationId the Ino conversation agent the dialogue belongs to.
public sealed record CreatorState(Selection? Selected, string? TraceId, long? ReplayCursor, string ConversationId)
{
    public CreatorState() : this(null, null, null, "") { }

    public string Status { get; init; } = "Ready";

    // What the describe and change fields hold, folded from their text fields' changes.
    public string Draft { get; init; } = "";

    public string ChangeDraft { get; init; } = "";

    // The last scenario run's transcript.
    public string? Transcript { get; init; }
}

// A selection of the graph: a node (its id; a ghost is draft:<alias>), an edge (its full key) or a pulse (its trace id).
public sealed record Selection(string Kind, string Id)
{
    public const string Node = "node", Edge = "edge", Pulse = "pulse";

    // A graph key back into a selection; a bare id is a node. Null clears.
    public static Selection? Parse(string? key) => key switch
    {
        null or "" => null,
        _ when key.StartsWith("node:", StringComparison.Ordinal) => new(Node, key["node:".Length..]),
        _ when key.StartsWith("edge:", StringComparison.Ordinal) => new(Edge, key),
        _ when key.StartsWith("pulse:", StringComparison.Ordinal) => key.Length > "pulse:".Length ? new(Pulse, key["pulse:".Length..]) : null,
        _ => new(Node, key),
    };

    public string GraphKey => Kind switch
    {
        Node => BrainGraph.NodeKey(Id),
        Pulse => BrainGraph.PulseKey(Id),
        _ => Id,
    };
}

// What the inspector read for the selection in the last turn (Render cannot await).
public sealed record Inspection(string Kind, string Id)
{
    public static readonly Inspection None = new("", "");

    public string? Alias { get; init; }
    public string? State { get; init; }
    public string? View { get; init; }
    public IReadOnlyDictionary<string, string> Files { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<TraceEntry> Signals { get; init; } = [];
    public IReadOnlyList<TraceHop> Hops { get; init; } = [];
    public string? Notice { get; init; }
}

// One journaled signal of a trace, folded: Depth is its causation chain length to a root hop.
public sealed record TraceHop(long Sequence, string Publisher, string Alias, string Address, string? Cause, int Depth, DateTimeOffset At, string Payload, string? FlowId);
