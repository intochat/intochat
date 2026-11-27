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

/// <summary>Planned MCP verification call for <see cref="CandidateLiveVerificationRequest"/>.</summary>
public sealed record CandidateMcpCall(
    string Target,
    string Method,
    string Expected = "ok");

/// <summary>Result of a live UI verification hook (e.g. headless Playwright) executed against a running candidate before teardown.</summary>
public sealed record CandidateUiVerificationResult(
    bool Passed,
    int Http4xxCount,
    int Http5xxCount,
    int ErrorLogCount,
    IReadOnlyList<EngineerLiveProbe> Probes,
    IReadOnlyList<string> TraceSummaries);

/// <summary>Request sent by <c>EngineerLiveVerifier</c> to verify a candidate worktree through <c>CandidateLiveVerifier</c>.</summary>
public sealed record CandidateLiveVerificationRequest(
    string WorktreePath,
    string Goal,
    IReadOnlyList<string> ChangedFiles,
    string? CommitSha = null,
    string HealthPath = "/health",
    bool TouchesUi = false,
    string? TraceId = null,
    string? BrainId = null,
    IReadOnlyList<CandidateMcpCall>? McpCalls = null,
    Func<string, CancellationToken, Task<CandidateUiVerificationResult>>? VerifyUiAsync = null);

/// <summary>
/// DI bridge implemented by the Aspire component (<c>CandidateLiveVerifier</c>) over <c>ICandidateHost</c>
/// so <c>EngineerLiveVerifier</c> executes real isolated candidates with MCP, Playwright, and OTel checks.
/// </summary>
public interface IEngineerCandidateHostVerifier
{
    /// <summary>True when a candidate host backend is configured and ready to isolate candidate runs.</summary>
    bool IsConfigured { get; }

    /// <summary>Name of the active candidate host backend (e.g. <c>ProcessCandidateHost</c> or <c>AgentDevSlotsCandidateHost</c>).</summary>
    string HostKind { get; }

    Task<EngineerLiveReport> VerifyCandidateAsync(
        CandidateLiveVerificationRequest request,
        CancellationToken ct = default);
}

// Whether this installation has a live environment that live-verify can check a change against. The registered ILiveVerifier
// answers by implementing it. A registered verifier that does not implement it is an environment someone wired, and counts as
// configured. Where none is configured, an engineering run records live-verify as not available and goes on to the approval;
// where one is configured but cannot be reached, the run waits at live-verify.
public interface ILiveEnvironment
{
    bool Configured { get; }
}
