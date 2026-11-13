using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.Work;

[JsonConverter(typeof(JsonStringEnumConverter<WorkStage>))]
public enum WorkStage
{
    Pending,
    Plan,
    Author,
    Draft,
    Verify,
    Approval,
    Deploy,
    Completed,
    Failed,
    Cancelled,
}

[JsonConverter(typeof(JsonStringEnumConverter<WorkStageOutcome>))]
public enum WorkStageOutcome
{
    NotStarted,
    InProgress,
    WaitingApproval,
    WaitingExternal,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// Domain record tracking transition between distinct work pipeline stages.
/// </summary>
public sealed record WorkStageTransition(
    string WorkId,
    WorkStage FromStage,
    WorkStage ToStage,
    WorkStageOutcome Outcome,
    DateTimeOffset TransitionedAt,
    string? Detail = null);

[JsonConverter(typeof(JsonStringEnumConverter<ChangeStage>))]
public enum ChangeStage
{
    Clone,
    Branch,
    SynthesizePatch,
    Build,
    Test,
    DiffReview,
    ApprovalGate,
    Promote,
}

/// <summary>
/// Domain record tracking transition between distinct ChangeFlow pipeline stages.
/// </summary>
public sealed record ChangeStageTransition(
    string RunId,
    ChangeStage FromStage,
    ChangeStage ToStage,
    WorkStageOutcome Outcome,
    DateTimeOffset TransitionedAt,
    string? Detail = null);

