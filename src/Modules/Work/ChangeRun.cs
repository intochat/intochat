using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.Work;

public sealed record BuildSummary(
    bool Success,
    int Errors,
    int Warnings,
    string Details);

public sealed record TestSummary(
    bool Success,
    int Passed,
    int Failed,
    int Total,
    string Details);

public sealed record ChangeRun(
    string RunId,
    string Goal,
    string Subject,
    ChangeStage Stage,
    WorkStageOutcome Outcome,
    string TargetBranch,
    string TempDirectory,
    StructuredDiff? Diff = null,
    BuildSummary? BuildSummary = null,
    TestSummary? TestSummary = null,
    string? ConfirmationPlanId = null,
    bool IsApproved = false,
    bool Promoted = false,
    bool Aborted = false,
    string? AbortReason = null,
    DateTimeOffset? CreatedAt = null);
