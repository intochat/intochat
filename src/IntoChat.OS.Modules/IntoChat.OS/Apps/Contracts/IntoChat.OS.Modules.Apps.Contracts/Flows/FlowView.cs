using DigitalBrain.Flows;

namespace IntoChat.OS.Modules.Apps.Flows;

// One flow as the index shows it. FlowId is the flow's neuron id ({brain}/{type}/{key}). Status is Blocked for a Pending or
// Running flow with a BlockedBy relation whose target has not completed; BlockedBy names those targets. Fraction is the done
// share of the steps, plus the running step's leaf operation progress.
public sealed record FlowView(
    string FlowId,
    string Type,
    string Title,
    string Owner,
    string TraceId,
    FlowStatus Status,
    string[] Steps,
    string[] Done,
    string? Current,
    WaitReason? WaitingFor,
    string? WaitingStep,
    string? ConfirmationPlanId,
    double Fraction,
    FlowRelation[] Relations,
    string[] BlockedBy,
    string? ParentFlowId,
    DateTimeOffset Started,
    DateTimeOffset? Ended,
    string? Detail);

// Null fields do not filter. Newest started first, at most Limit.
public sealed record FlowQuery(string? Owner = null, FlowStatus? Status = null, string? Type = null, string? ParentFlowId = null, int Limit = 200);
