using System.Globalization;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Flows;

[JsonConverter(typeof(JsonStringEnumConverter<EngineeringHarnessKind>))]
[GenerateSerializer, Alias("engineer.harness-kind")]
public enum EngineeringHarnessKind
{
    InoNative = 0,
    ClaudeCli = 1,
    CodexCli = 2,
}



public sealed record EngineeringHarnessRequest(
    string FlowId,
    string RunId,
    string RepositoryId,
    string Goal,
    string WorktreeId,
    string WorktreePath,
    int InstanceIndex,
    int TotalInstances,
    string Strategy,
    string StrategyLabel,
    string StrategyPromptLens,
    EngineeringHarnessKind Harness,
    RatifiedPlan RatifiedPlan,
    BrainId? Brain = null,
    string? BaseBranch = null,
    string? SourceRepositoryPath = null);

public sealed record EngineeringHarnessResult(
    bool Succeeded,
    EngineeringHarnessKind Harness,
    string Summary,
    string ModelId,
    string[] TouchedFiles,
    string[] DeletedFiles,
    EngineerTaskSlice[] CompletedSlices,
    OperationProgressed[] ProgressEvents,
    bool UiStateGraphsHealthy = true,
    int SemanticErrorCount = 0,
    string? HydratedContextSummary = null,
    string? FailureDetail = null,
    int SelfRepairAttempts = 0);

public interface IEngineeringHarness
{
    EngineeringHarnessKind Kind { get; }

    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    Task<EngineeringHarnessResult> ExecuteAsync(
        EngineeringHarnessRequest request,
        Func<OperationProgressed, Task>? onProgress = null,
        CancellationToken ct = default);
}

public interface IEngineerPlanner
{
    Task<EngineerTaskPlan> PlanAsync(
        string repositoryId,
        string goal,
        IReadOnlyList<string> allowedExtensions,
        int maxChangedFiles,
        CancellationToken ct = default);
}

public interface IEngineerBuildVerifier
{
    Task<EngineerBuildReport> VerifyAsync(
        string worktreePath,
        IReadOnlyList<string> changedFiles,
        CancellationToken ct = default);
}

public interface ILiveVerifier
{
    Task<EngineerLiveReport> VerifyLiveAsync(
        string worktreePath,
        string goal,
        IReadOnlyList<string> changedFiles,
        CancellationToken ct = default);
}
