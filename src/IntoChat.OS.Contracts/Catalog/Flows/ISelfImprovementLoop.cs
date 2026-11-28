using System.ComponentModel;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps.Flows;

[GenerateSerializer, Alias("loop.failing-fact")]
public sealed record LoopFailingFact(
    [property: Id(0)] string FactName,
    [property: Id(1)] string Detail)
{
    public LoopFailingFact() : this("", "") { }
}

[GenerateSerializer, Alias("loop.cycle-request")]
public sealed record LoopCycleRequest(
    [property: Id(0)] string RepoSlug = "os",
    [property: Id(1)] LoopFinding[]? Findings = null,
    [property: Id(2)] LoopTraceSpan[]? TraceSpans = null,
    [property: Id(3)] string? OwedMarkdown = null,
    [property: Id(4)] LoopFailingFact[]? FailingFacts = null,
    [property: Id(5)] int MaxConcurrentRuns = 1,
    [property: Id(6)] string Backend = "LocalGit",
    [property: Id(7)] string? RequestedBaseBranch = null,
    [property: Id(8)] EngineeringHarnessKind Harness = EngineeringHarnessKind.InoNative)
{
    public LoopCycleRequest() : this("os") { }
}

[GenerateSerializer, Alias("loop.run-record")]
public sealed record LoopRunRecord(
    [property: Id(0)] string FindingKey,
    [property: Id(1)] string FindingTitle,
    [property: Id(2)] string Source,
    [property: Id(3)] string FlowId,
    [property: Id(4)] string RunId,
    [property: Id(5)] string Status,
    [property: Id(6)] DateTimeOffset StartedAt,
    [property: Id(7)] DateTimeOffset? CompletedAt = null,
    [property: Id(8)] string? OutcomeDetail = null,
    [property: Id(9)] string? LessonKey = null,
    [property: Id(10)] long TokensUsed = 0)
{
    public LoopRunRecord() : this("", "", "", "", "", "running", default) { }
}

[GenerateSerializer, Alias("loop.cycle-result")]
public sealed record LoopCycleResult(
    [property: Id(0)] bool Dispatched,
    [property: Id(1)] string Status,
    [property: Id(2)] string Reason,
    [property: Id(3)] string? FindingKey = null,
    [property: Id(4)] string? FlowId = null,
    [property: Id(5)] string? RunId = null,
    [property: Id(6)] string? InjectedLessonKey = null,
    [property: Id(7)] int CandidatesEvaluated = 0,
    [property: Id(8)] int ActiveRuns = 0,
    [property: Id(9)] int RunsToday = 0)
{
    public LoopCycleResult() : this(false, "idle", "") { }
}

[GenerateSerializer, Alias("loop.state-snapshot")]
public sealed record LoopStateSnapshot(
    [property: Id(0)] string Status,
    [property: Id(1)] bool Paused,
    [property: Id(2)] LoopBudget Budget,
    [property: Id(3)] int RunsToday,
    [property: Id(4)] int ActiveRunCount,
    [property: Id(5)] string[] ParkedFindingKeys,
    [property: Id(6)] string[] AdmittedFindingKeys,
    [property: Id(7)] LoopRunRecord[] RecentRuns)
{
    public LoopStateSnapshot() : this("ready", false, new LoopBudget(), 0, 0, [], [], []) { }
}

[NeuronAlias("loop.self-improvement"), DefaultNeuronType("loop.self-improvement")]
[Description("Autonomous self-improvement loop orchestrating LoopFindings, LoopBudget, LoopLessons, and IEngineerRuns.")]
public interface ISelfImprovementLoop
{
    const string DefaultKey = "default";

    [ReadOnlyCall, Interleaves]
    Task<string> Status(CancellationToken ct = default);

    [ReadOnlyCall, Interleaves]
    Task<LoopStateSnapshot> Inspect(CancellationToken ct = default);

    Task<LoopStateSnapshot> ConfigureBudget(LoopBudget budget, CancellationToken ct = default);

    Task<LoopStateSnapshot> SetPaused(bool paused, CancellationToken ct = default);

    Task<LoopCycleResult> RunCycle(LoopCycleRequest request, CancellationToken ct = default);

    Task<LoopRunRecord?> RecordOutcome(
        string findingKey,
        string flowId,
        bool succeeded,
        string? detail = null,
        long tokensUsed = 0,
        CancellationToken ct = default);

    Task<LoopRunRecord[]> ReconcileActiveRuns(CancellationToken ct = default);
}
