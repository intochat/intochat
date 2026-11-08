using DigitalBrain.Flows;

namespace IntoChat.OS.Modules.Apps.Flows;

// The index's persisted entries, keyed by flow id. Fields are only ever added.
public sealed record FlowsState(Dictionary<string, FlowEntry> Flows)
{
    // Finished flows kept; the oldest finished beyond this are dropped.
    public const int KeepFinished = 2000;

    public FlowsState() : this(new Dictionary<string, FlowEntry>(StringComparer.Ordinal)) { }
}

// One flow as folded from its signals; Steps, Owner, TraceId, ParentFlowId and Started are read from the flow at flow.started.
public sealed record FlowEntry(
    string FlowId,
    string Type,
    string Title,
    string Owner,
    string TraceId,
    FlowStatus Status,
    string[] Steps,
    string[] Done,
    string? Current,
    Waiting? Waiting,
    FlowRelation[] Relations,
    string? ParentFlowId,
    DateTimeOffset Started,
    DateTimeOffset? Ended,
    string? Detail)
{
    public bool Finished => Status is FlowStatus.Completed or FlowStatus.Failed or FlowStatus.Cancelled;
}
