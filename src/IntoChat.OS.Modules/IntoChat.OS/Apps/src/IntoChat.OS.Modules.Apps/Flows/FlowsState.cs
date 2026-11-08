using DigitalBrain.Flows;

namespace IntoChat.OS.Modules.Apps.Flows;

// The index's persisted entries, keyed by flow id. Fields are only ever added.
public sealed record FlowsState(Dictionary<string, FlowEntry> Flows)
{
    // Finished flows kept; the oldest finished beyond this are dropped.
    public const int KeepFinished = 2000;

    public FlowsState() : this(new Dictionary<string, FlowEntry>(StringComparer.Ordinal)) { }

    // The state with entry put in, keeping the newest KeepFinished finished flows.
    public FlowsState With(FlowEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var flows = new Dictionary<string, FlowEntry>(Flows, StringComparer.Ordinal) { [entry.FlowId] = entry };
        var finished = flows.Values.Where(candidate => candidate.Finished).ToArray();
        if (finished.Length > KeepFinished)
        {
            foreach (var old in finished.OrderBy(candidate => candidate.Ended).Take(finished.Length - KeepFinished)) { flows.Remove(old.FlowId); }
        }
        return this with { Flows = flows };
    }
}

// One flow as folded from its signals. Steps, Owner, TraceId, ParentFlowId and Started come from the flow's own FlowHead; until
// that read succeeded HeadKnown is false and the entry is a placeholder. OperationId is the current step's leaf operation, read
// from the flow when the step starts or waits.
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
    string? Detail,
    bool HeadKnown = false,
    string? OperationId = null)
{
    public bool Finished => Status is FlowStatus.Completed or FlowStatus.Failed or FlowStatus.Cancelled;

    // The entry with what the flow reports merged in; a status the index already folded wins unless the flow reports an end.
    public FlowEntry Merge(FlowHead head)
    {
        ArgumentNullException.ThrowIfNull(head);
        var ended = head.Status is FlowStatus.Completed or FlowStatus.Failed or FlowStatus.Cancelled;
        return this with
        {
            Title = Title.Length == 0 ? head.Title : Title,
            Owner = head.Owner.Length == 0 ? Owner : head.Owner,
            TraceId = head.TraceId,
            Steps = head.Steps,
            Done = [.. Done.Union(head.Done, StringComparer.Ordinal)],
            Status = ended && !Finished ? head.Status : Status == FlowStatus.Pending ? head.Status : Status,
            Ended = Ended ?? head.Ended,
            ParentFlowId = ParentFlowId ?? head.ParentFlowId,
            Started = head.Started == default ? Started : head.Started,
            OperationId = head.OperationId,
            HeadKnown = true,
        };
    }
}
