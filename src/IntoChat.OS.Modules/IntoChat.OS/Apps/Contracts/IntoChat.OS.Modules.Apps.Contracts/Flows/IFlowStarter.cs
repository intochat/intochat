using DigitalBrain.Flows;

namespace IntoChat.OS.Modules.Apps.Flows;

// What every OS flow type implements so the flows index can start it by alias and read it. Resume, Cancel and Relate are the
// Flow<T> base's own members. Head interleaves, so the index can read a flow while one of its steps runs.
public interface IFlowStarter
{
    Task StartFromJson(string title, string owner, string? parentFlowId, string innerJson);

    [DigitalBrain.ReadOnlyCall]
    Task<FlowHead> Head();

    Task Resume(string step, WaitOutcome outcome, string? by);

    Task Cancel(string reason);

    Task Relate(FlowRelation relation);
}

// What a flow reports of itself: its declared steps and the parts of its state the index cannot learn from signals alone, so an
// index that missed signals catches up. InnerJson is its own state as JSON; OperationId is the top-level "operationId" string of
// that state (the leaf operation of the current step), if any.
public sealed record FlowHead(
    string[] Steps,
    string Owner,
    string TraceId,
    string? ParentFlowId,
    DateTimeOffset Started,
    string InnerJson,
    string Title,
    FlowStatus Status,
    string[] Done,
    DateTimeOffset? Ended,
    string? OperationId);
