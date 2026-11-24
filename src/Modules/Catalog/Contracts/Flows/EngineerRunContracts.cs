using System.Globalization;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Flows;

[GenerateSerializer, Alias("engineer.start-request")]
public sealed record EngineerStartRequest(
    [property: Id(0)] string RunId,
    [property: Id(1)] string Repository,
    [property: Id(2)] string Goal,
    [property: Id(3)] int InstanceCount = 1,
    [property: Id(4)] string? RequestedBaseBranch = null,
    [property: Id(5)] string? Backend = null,
    [property: Id(6)] bool RequirePlanRatification = false,
    [property: Id(7)] EngineeringHarnessKind Harness = EngineeringHarnessKind.InoNative,
    [property: Id(8)] string[]? CleanupTargets = null,
    [property: Id(9)] EngineerTaskSlice[]? Slices = null,
    [property: Id(10)] string? CircleBrainId = null)
{
    public EngineerStartRequest() : this("", "os", "", 1, null, null) { }

    public string ToStartJson() => System.Text.Json.JsonSerializer.Serialize(this, System.Text.Json.JsonSerializerOptions.Web);

    public Task<string> StartOnAsync(
        IFlows flows,
        string? title = null,
        string? parentFlowId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flows);
        var effectiveTitle = string.IsNullOrWhiteSpace(title) ? Goal : title.Trim();
        var explicitKey = string.IsNullOrWhiteSpace(RunId) ? null : RunId.Trim();
        return flows.Start(IEngineerRuns.EngineerFlow, effectiveTitle, parentFlowId, ToStartJson(), ct, flowId: explicitKey);
    }
}

[GenerateSerializer, Alias("engineer.delegation-request")]
public sealed record EngineerDelegationRequest(
    [property: Id(0)] string RepoSlug,
    [property: Id(1)] string Goal,
    [property: Id(2)] int ParallelInstances = 1,
    [property: Id(3)] string Backend = "LocalGit",
    [property: Id(4)] string? RequestedBaseBranch = null,
    [property: Id(5)] string? RunId = null,
    [property: Id(6)] bool RequirePlanRatification = false,
    [property: Id(7)] EngineeringHarnessKind Harness = EngineeringHarnessKind.InoNative,
    [property: Id(8)] string[]? CleanupTargets = null,
    [property: Id(9)] EngineerTaskSlice[]? Slices = null,
    [property: Id(10)] string? CircleBrainId = null)
{
    public EngineerDelegationRequest() : this("os", "", 1, "LocalGit", null, null) { }
}


[GenerateSerializer, Alias("engineer.instance-summary")]
public sealed record EngineerInstanceSummary(
    [property: Id(0)] int InstanceIndex,
    [property: Id(1)] string LeaseId,
    [property: Id(2)] string Backend,
    [property: Id(3)] string BranchName,
    [property: Id(4)] string Status,
    [property: Id(5)] string[] CompletedTaskIds,
    [property: Id(6)] string[] ChangedFiles,
    [property: Id(7)] string? CommitSha = null,
    [property: Id(8)] bool BuildSucceeded = false,
    [property: Id(9)] bool LiveSucceeded = false,
    [property: Id(10)] string? FailureDetail = null,
    [property: Id(11)] string Strategy = "surgical-patch",
    [property: Id(12)] int TotalScore = 0,
    [property: Id(13)] int ChangedLines = 0,
    [property: Id(14)] string UnifiedDiff = "",
    [property: Id(15)] int Rank = 1,
    [property: Id(16)] bool IsSelected = false,
    [property: Id(17)] EngineeringHarnessKind Harness = EngineeringHarnessKind.InoNative,
    [property: Id(18)] EngineerTaskSlice[]? Slices = null,
    [property: Id(19)] string[]? DeletedFiles = null)
{
    public EngineerInstanceSummary()
        : this(0, "", "LocalGit", "", "pending", [], [], null, false, false, null, "surgical-patch", 0, 0, "", 1, false)
    {
    }
}

public sealed record EngineerRunSummary(
    bool Ok,
    string FlowId,
    string RunId,
    string RepoSlug,
    string Goal,
    string Backend,
    int ParallelInstances,
    DigitalBrain.Flows.FlowStatus Status,
    string CurrentStage,
    string[] CompletedStages,
    string? ConfirmationPlanId,
    string? TraceId,
    string? WinningCommitSha,
    string? PushedRef,
    string? PullRequestUrl,
    string EngineerAppUri,
    string TasksBoardUri,
    EngineerTaskPlan? Plan,
    EngineerInstanceSummary[] Instances,
    string[] DiagnosticsAndTraces,
    string SummaryText,
    IntoChat.OS.Modules.Apps.InoCard? Card = null,
    int SelectedCandidateIndex = 0,
    EngineerCandidateResult[]? Candidates = null,
    EngineeringHarnessKind Harness = EngineeringHarnessKind.InoNative,
    bool AwaitingPlanRatification = false,
    RatifiedPlan? RatifiedPlan = null,
    string[]? CleanupTargets = null,
    EngineerTaskSlice[]? Slices = null,
    MissionCollaborator[]? Collaborators = null);

