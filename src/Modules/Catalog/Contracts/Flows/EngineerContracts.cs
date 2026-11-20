using DigitalBrain;

namespace IntoChat.OS.Modules.Apps.Flows;

[GenerateSerializer, Alias("engineer.start-request")]
public sealed record EngineerStartRequest(
    [property: Id(0)] string RunId,
    [property: Id(1)] string Repository,
    [property: Id(2)] string Goal,
    [property: Id(3)] int InstanceCount = 1,
    [property: Id(4)] string? RequestedBaseBranch = null,
    [property: Id(5)] string? Backend = null)
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
        return flows.Start(IFlows.EngineerFlow, effectiveTitle, parentFlowId, ToStartJson(), ct, flowId: explicitKey);
    }
}

[GenerateSerializer, Alias("engineer.delegation-request")]
public sealed record EngineerDelegationRequest(
    [property: Id(0)] string RepoSlug,
    [property: Id(1)] string Goal,
    [property: Id(2)] int ParallelInstances = 1,
    [property: Id(3)] string Backend = "LocalGit",
    [property: Id(4)] string? RequestedBaseBranch = null,
    [property: Id(5)] string? RunId = null)
{
    public EngineerDelegationRequest() : this("os", "", 1, "LocalGit", null, null) { }
}

[GenerateSerializer, Alias("engineer.candidate-score")]
public sealed record EngineerCandidateScore(
    [property: Id(0)] bool BuildPassed,
    [property: Id(1)] bool TestsPassed,
    [property: Id(2)] bool LiveVerifyPassed,
    [property: Id(3)] bool ZeroTelemetryErrors,
    [property: Id(4)] int ChangedLines,
    [property: Id(5)] int TotalScore,
    [property: Id(6)] string? CommitSha,
    [property: Id(7)] string UnifiedDiff,
    [property: Id(8)] string Summary,
    [property: Id(9)] int WarningCount = 0)
{
    public EngineerCandidateScore() : this(false, false, false, false, 0, 0, null, "", "", 0) { }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool AllGatesPassed =>
        !string.IsNullOrWhiteSpace(CommitSha)
        && BuildPassed
        && TestsPassed
        && LiveVerifyPassed
        && ZeroTelemetryErrors;

    public static EngineerCandidateScore Compute(
        string? commitSha,
        string? unifiedDiff,
        int changedLines,
        EngineerBuildReport? build,
        EngineerLiveReport? live,
        int roslynErrorCount = 0)
    {
        var diff = unifiedDiff ?? string.Empty;
        var effectiveLines = changedLines > 0 ? changedLines : CountUnifiedDiffLines(diff);
        var hasCommit = !string.IsNullOrWhiteSpace(commitSha);
        var buildPassed = build is { Succeeded: true, DotnetBuildPassed: true, FlutterAnalyzePassed: true } && roslynErrorCount == 0;
        var testsPassed = build is { Succeeded: true, DotnetTestPassed: true, FailedTests: 0 };
        var livePassed = live is { Succeeded: true, HostHealthy: true, McpProbesPassed: true, PlaywrightPassed: true };
        var zeroTelemetryErrors = live is not null
            && live.Http4xxCount == 0
            && live.Http5xxCount == 0
            && live.ErrorSpanCount == 0
            && live.ErrorLogCount == 0;
        var warnings = build?.WarningCount ?? 0;

        var total = 0;
        if (hasCommit) { total += 100; }
        if (buildPassed) { total += 300; }
        if (testsPassed) { total += 250; }
        if (livePassed) { total += 250; }
        if (zeroTelemetryErrors) { total += 100; }

        total -= Math.Min(45, warnings * 5);
        total -= Math.Min(200, (build?.ErrorCount ?? 0) * 25 + roslynErrorCount * 50);
        if (effectiveLines > 0)
        {
            total -= Math.Min(40, Math.Max(0, effectiveLines - 1));
        }

        total = Math.Max(0, total);
        var shortSha = commitSha is { Length: >= 8 } sha ? sha[..8] : (hasCommit ? commitSha : "pending");
        var summary = System.FormattableString.Invariant(
            $"Score={total} · Build={(buildPassed ? "PASS" : "FAIL")}, Tests={(testsPassed ? "PASS" : "FAIL")}, Live={(livePassed ? "PASS" : "FAIL")}, OTel={(zeroTelemetryErrors ? "0 err" : "ERR")}, Δ={effectiveLines} lines ({shortSha})");

        return new EngineerCandidateScore(
            BuildPassed: buildPassed,
            TestsPassed: testsPassed,
            LiveVerifyPassed: livePassed,
            ZeroTelemetryErrors: zeroTelemetryErrors,
            ChangedLines: effectiveLines,
            TotalScore: total,
            CommitSha: commitSha,
            UnifiedDiff: diff,
            Summary: summary,
            WarningCount: warnings);
    }

    public static int CountUnifiedDiffLines(string? unifiedDiff)
    {
        if (string.IsNullOrWhiteSpace(unifiedDiff))
        {
            return 0;
        }

        var count = 0;
        foreach (var raw in unifiedDiff.Split('\n'))
        {
            if (raw.Length == 0)
            {
                continue;
            }

            if ((raw[0] == '+' && !raw.StartsWith("+++", StringComparison.Ordinal))
                || (raw[0] == '-' && !raw.StartsWith("---", StringComparison.Ordinal)))
            {
                count++;
            }
        }

        return count;
    }
}

[GenerateSerializer, Alias("engineer.candidate-result")]
public sealed record EngineerCandidateResult(
    [property: Id(0)] int InstanceIndex,
    [property: Id(1)] string Strategy,
    [property: Id(2)] string StrategyLabel,
    [property: Id(3)] string LeaseId,
    [property: Id(4)] string Backend,
    [property: Id(5)] string BranchName,
    [property: Id(6)] string Status,
    [property: Id(7)] string[] CompletedTaskIds,
    [property: Id(8)] string[] ChangedFiles,
    [property: Id(9)] int Rank,
    [property: Id(10)] bool IsSelected,
    [property: Id(11)] EngineerCandidateScore Score,
    [property: Id(12)] EngineerBuildReport? Build = null,
    [property: Id(13)] EngineerLiveReport? Live = null,
    [property: Id(14)] string? FailureDetail = null)
{
    public EngineerCandidateResult()
        : this(0, "surgical-patch", "Minimal Surgical Patch", "", "LocalGit", "", "pending", [], [], 1, false, new EngineerCandidateScore(), null, null, null)
    {
    }
}

public static class EngineerCandidateStrategies
{
    private static readonly (string Id, string Label, string PromptLens)[] Variants =
    [
        (
            "surgical-patch",
            "Minimal Surgical Patch",
            "Apply the smallest, cleanest surgical AST/file patch that satisfies every requirement with minimal diff footprint."),
        (
            "clean-refactor",
            "Alternative Strategy & Refactor",
            "Implement an alternative design lens using explicit helper structure, clear domain invariants, and self-documenting expressions."),
        (
            "defensive-hardening",
            "Defensive Edge-Case Hardening",
            "Prioritize boundary checks, null/range guards, and deterministic edge-case resilience within the file budget."),
        (
            "performance-lean",
            "Zero-Overhead Fast Path",
            "Favor allocation-free value paths, tight branchless expressions, and minimal runtime overhead."),
        (
            "strict-contracts",
            "Contract-First Decomposition",
            "Align types and method signatures cleanly around explicit contracts and pure static helpers."),
        (
            "idiomatic-clarity",
            "Idiomatic Readability Lens",
            "Optimize for maximum maintainer readability, pattern matching clarity, and concise XML/comment free structure."),
        (
            "composable-helpers",
            "Composable Helper Strategy",
            "Structure logic as small, composable, deterministically testable pure operations."),
        (
            "minimal-surface",
            "Minimal Public Surface",
            "Keep public API surface area as narrow as possible while satisfying all requested behaviors."),
    ];

    public static (string Id, string Label, string PromptLens) ForCandidate(int instanceIndex)
    {
        var idx = Math.Abs(instanceIndex) % Variants.Length;
        return Variants[idx];
    }

    public static string LabelFor(string? strategyId, int fallbackIndex = 0)
    {
        if (!string.IsNullOrWhiteSpace(strategyId))
        {
            foreach (var variant in Variants)
            {
                if (string.Equals(variant.Id, strategyId.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return variant.Label;
                }
            }
        }

        return ForCandidate(fallbackIndex).Label;
    }
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
    [property: Id(16)] bool IsSelected = false)
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
    EngineerCandidateResult[]? Candidates = null);

[GenerateSerializer, Alias("engineer.brainstorm-turn")]
public sealed record EngineerBrainstormTurn(
    [property: Id(0)] string Speaker,
    [property: Id(1)] string Role,
    [property: Id(2)] string Perspective,
    [property: Id(3)] string Message)
{
    public EngineerBrainstormTurn() : this("User", "user", "Goal", "") { }
}

[GenerateSerializer, Alias("engineer.task-item")]
public sealed record EngineerTaskItem(
    [property: Id(0)] string Id,
    [property: Id(1)] string Title,
    [property: Id(2)] string Language,
    [property: Id(3)] string[] TargetFiles,
    [property: Id(4)] string[] DependsOn,
    [property: Id(5)] string Instructions)
{
    public EngineerTaskItem() : this("", "", "csharp", [], [], "") { }
}

[GenerateSerializer, Alias("engineer.task-plan")]
public sealed record EngineerTaskPlan(
    [property: Id(0)] string Goal,
    [property: Id(1)] string ArchitectureSummary,
    [property: Id(2)] string CriticReview,
    [property: Id(3)] EngineerBrainstormTurn[] Transcript,
    [property: Id(4)] EngineerTaskItem[] Tasks)
{
    public EngineerTaskPlan() : this("", "", "", [], []) { }

    public string? Validate(int maxChangedFiles = 5)
    {
        if (string.IsNullOrWhiteSpace(Goal))
        {
            return "EngineerTaskPlan requires a non-empty goal.";
        }

        if (Tasks is null || Tasks.Length == 0)
        {
            return "EngineerTaskPlan must contain at least one task.";
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var distinctTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var task in Tasks)
        {
            if (task is null || string.IsNullOrWhiteSpace(task.Id))
            {
                return "Every task in EngineerTaskPlan must have a non-empty id.";
            }

            if (!ids.Add(task.Id.Trim()))
            {
                return $"Duplicate task id '{task.Id}' in EngineerTaskPlan.";
            }

            if (string.IsNullOrWhiteSpace(task.Title))
            {
                return $"Task '{task.Id}' must have a non-empty title.";
            }

            var lang = (task.Language ?? string.Empty).Trim().ToLowerInvariant();
            if (lang is not ("csharp" or "dart" or "markdown" or "mixed"))
            {
                return $"Task '{task.Id}' has unsupported language '{task.Language}' (expected csharp, dart, markdown, or mixed).";
            }

            foreach (var rawFile in task.TargetFiles ?? [])
            {
                if (string.IsNullOrWhiteSpace(rawFile))
                {
                    return $"Task '{task.Id}' contains a blank target file path.";
                }

                var clean = rawFile.Trim().Replace('\\', '/');
                if (clean.StartsWith('/') || Path.IsPathRooted(clean) || clean.Split('/').Any(static seg => seg.Length == 0 || seg.StartsWith('.')))
                {
                    return $"Task '{task.Id}' contains invalid or hidden target file path '{rawFile}'.";
                }

                distinctTargets.Add(clean);
            }
        }

        if (maxChangedFiles > 0 && distinctTargets.Count > maxChangedFiles)
        {
            return $"EngineerTaskPlan targets {distinctTargets.Count} distinct files, exceeding the policy maximum of {maxChangedFiles}.";
        }

        foreach (var task in Tasks)
        {
            foreach (var dep in task.DependsOn ?? [])
            {
                if (string.Equals(dep, task.Id, StringComparison.Ordinal))
                {
                    return $"Task '{task.Id}' cannot depend on itself.";
                }

                if (!ids.Contains(dep))
                {
                    return $"Task '{task.Id}' depends on unknown task '{dep}'.";
                }
            }
        }

        return TryComputeTopologicalOrder(out _)
            ? null
            : "EngineerTaskPlan contains a dependency cycle.";
    }

    public EngineerTaskItem[] TopologicalOrder()
        => TryComputeTopologicalOrder(out var ordered)
            ? ordered
            : throw new InvalidOperationException("Cannot order an EngineerTaskPlan containing a dependency cycle.");

    private bool TryComputeTopologicalOrder(out EngineerTaskItem[] ordered)
    {
        ordered = [];
        if (Tasks is null || Tasks.Length == 0)
        {
            return true;
        }

        var byId = Tasks.ToDictionary(static t => t.Id, StringComparer.Ordinal);
        var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var task in Tasks)
        {
            var deps = (task.DependsOn ?? []).Distinct(StringComparer.Ordinal).ToArray();
            inDegree[task.Id] = deps.Length;
            foreach (var dep in deps)
            {
                if (!byId.ContainsKey(dep))
                {
                    return false;
                }

                if (!dependents.TryGetValue(dep, out var list))
                {
                    list = [];
                    dependents[dep] = list;
                }

                list.Add(task.Id);
            }
        }

        var ready = new Queue<string>(Tasks.Where(t => inDegree[t.Id] == 0).Select(static t => t.Id));
        var result = new List<EngineerTaskItem>(Tasks.Length);

        while (ready.Count > 0)
        {
            var currentId = ready.Dequeue();
            result.Add(byId[currentId]);
            if (!dependents.TryGetValue(currentId, out var nextIds))
            {
                continue;
            }

            foreach (var nextId in nextIds)
            {
                inDegree[nextId]--;
                if (inDegree[nextId] == 0)
                {
                    ready.Enqueue(nextId);
                }
            }
        }

        if (result.Count != Tasks.Length)
        {
            return false;
        }

        ordered = [.. result];
        return true;
    }
}

[GenerateSerializer, Alias("engineer.build-report")]
public sealed record EngineerBuildReport(
    [property: Id(0)] bool Succeeded,
    [property: Id(1)] bool DotnetBuildPassed,
    [property: Id(2)] bool FlutterAnalyzePassed,
    [property: Id(3)] bool DotnetTestPassed,
    [property: Id(4)] int PassedTests,
    [property: Id(5)] int FailedTests,
    [property: Id(6)] int WarningCount,
    [property: Id(7)] int ErrorCount,
    [property: Id(8)] string[] Diagnostics,
    [property: Id(9)] long DurationMs,
    [property: Id(10)] string Summary)
{
    public EngineerBuildReport() : this(false, false, false, false, 0, 0, 0, 0, [], 0, "") { }
}

[GenerateSerializer, Alias("engineer.live-probe")]
public sealed record EngineerLiveProbe(
    [property: Id(0)] string Name,
    [property: Id(1)] string Kind,
    [property: Id(2)] bool Passed,
    [property: Id(3)] int? HttpStatus,
    [property: Id(4)] string Detail)
{
    public EngineerLiveProbe() : this("", "mcp", false, null, "") { }
}

[GenerateSerializer, Alias("engineer.live-report")]
public sealed record EngineerLiveReport(
    [property: Id(0)] bool Succeeded,
    [property: Id(1)] bool HostHealthy,
    [property: Id(2)] bool McpProbesPassed,
    [property: Id(3)] bool PlaywrightPassed,
    [property: Id(4)] int Http4xxCount,
    [property: Id(5)] int Http5xxCount,
    [property: Id(6)] int ErrorSpanCount,
    [property: Id(7)] int ErrorLogCount,
    [property: Id(8)] EngineerLiveProbe[] Probes,
    [property: Id(9)] string[] TraceSummaries,
    [property: Id(10)] string Summary)
{
    public EngineerLiveReport() : this(false, false, false, false, 0, 0, 0, 0, [], [], "") { }
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
