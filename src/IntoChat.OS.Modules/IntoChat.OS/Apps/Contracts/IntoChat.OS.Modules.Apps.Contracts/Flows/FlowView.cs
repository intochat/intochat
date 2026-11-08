using DigitalBrain.Flows;

namespace IntoChat.OS.Modules.Apps.Flows;

// One flow as the index shows it. FlowId is the flow's neuron id ({brain}/{type}/{key}). Status is Blocked for a Pending or
// Running flow with a BlockedBy relation whose target has not completed; BlockedBy names those targets. Fraction is the done
// share of the steps, plus the current (running or waiting) step's leaf operation progress. A step that awaits an operation inline
// reports no leaf fraction; store the id and wait for OperationDone instead.
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
    string? Detail,
    // The app and conversation of the caller that started the flow (App "assistant" for an agent's tool call); null before they were recorded.
    string? App = null,
    string? Conversation = null);

// Null fields do not filter; AnyStatus keeps views whose status is one of them. Newest started first, at most Limit.
public sealed record FlowQuery(string? Owner = null, FlowStatus? Status = null, string? Type = null, string? ParentFlowId = null, int Limit = 200,
    FlowStatus[]? AnyStatus = null);
