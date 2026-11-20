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

[GenerateSerializer, Alias("engineer.task-slice")]
public sealed record EngineerTaskSlice(
    [property: Id(0)] string SliceId,
    [property: Id(1)] string Title,
    [property: Id(2)] string[] OwnedPaths,
    [property: Id(3)] string[] DependsOnSliceIds,
    [property: Id(4)] string Status = "pending",
    [property: Id(5)] int Percent = 0,
    [property: Id(6)] string AssignedAgentKey = "ino.slice-1",
    [property: Id(7)] string Language = "csharp",
    [property: Id(8)] string Instructions = "")
{
    public EngineerTaskSlice()
        : this("", "", [], [], "pending", 0, "ino.slice-1", "csharp", "")
    {
    }
}

[GenerateSerializer, Alias("engineer.collaborator")]
public sealed record MissionCollaborator(
    [property: Id(0)] string Id,
    [property: Id(1)] string DisplayName,
    [property: Id(2)] string Kind,
    [property: Id(3)] string? FocusNodeId = null,
    [property: Id(4)] bool Active = true)
{
    public MissionCollaborator()
        : this("", "", "person", null, true)
    {
    }
}

public sealed record NoOverlappingFileSlices(IReadOnlyList<EngineerTaskSlice> Slices) : Rule("overlapping_file_slices")
{
    public override bool Broken => EvaluateViolation(Slices) is not null;

    public override string Message => EvaluateViolation(Slices) ?? "Parallel DAG wave slices have disjoint file ownership.";

    internal static string? EvaluateViolation(IReadOnlyList<EngineerTaskSlice>? slices)
    {
        if (slices is null || slices.Count == 0)
        {
            return null;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slice in slices)
        {
            if (slice is null || string.IsNullOrWhiteSpace(slice.SliceId))
            {
                return "Every EngineerTaskSlice must have a non-empty SliceId.";
            }

            if (!ids.Add(slice.SliceId.Trim()))
            {
                return $"Duplicate SliceId '{slice.SliceId}' in slice plan.";
            }
        }

        if (!EngineerTaskSlices.TryComputeWaves(slices, out var waves, out var dagError))
        {
            return dagError ?? "Slice dependency graph contains a cycle or unknown dependency.";
        }

        for (var waveIndex = 0; waveIndex < waves.Length; waveIndex++)
        {
            var wave = waves[waveIndex];
            if (wave.Length < 2)
            {
                continue;
            }

            var claimed = new List<(string SliceId, string NormalizedPath)>();
            foreach (var slice in wave)
            {
                var slicePaths = (slice.OwnedPaths ?? [])
                    .Where(static p => !string.IsNullOrWhiteSpace(p))
                    .Select(EngineerTaskSlices.NormalizePath)
                    .Where(static p => p.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                foreach (var candidatePath in slicePaths)
                {
                    foreach (var (priorSliceId, priorPath) in claimed)
                    {
                        if (string.Equals(priorSliceId, slice.SliceId, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (PathsOverlap(priorPath, candidatePath))
                        {
                            return string.Create(
                                CultureInfo.InvariantCulture,
                                $"Parallel slices '{priorSliceId}' and '{slice.SliceId}' in DAG wave {waveIndex + 1} claim overlapping OwnedPaths ('{priorPath}' vs '{candidatePath}').");
                        }
                    }

                    claimed.Add((slice.SliceId, candidatePath));
                }
            }
        }

        return null;
    }

    private static bool PathsOverlap(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
            || left.StartsWith(right + "/", StringComparison.OrdinalIgnoreCase)
            || right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase);
}

public static class EngineerTaskSlices
{
    public static NoOverlappingFileSlices CheckDisjoint(IReadOnlyList<EngineerTaskSlice> slices)
        => new(slices);

    public static string NormalizePath(string rawPath)
        => (rawPath ?? string.Empty).Trim().Replace('\\', '/').Trim('/');

    public static EngineerTaskSlice[][] ComputeWaves(IReadOnlyList<EngineerTaskSlice> slices)
        => TryComputeWaves(slices, out var waves, out var error)
            ? waves
            : throw new InvalidOperationException(error ?? "Cannot compute topological waves for invalid EngineerTaskSlice DAG.");

    public static bool TryComputeWaves(
        IReadOnlyList<EngineerTaskSlice>? slices,
        out EngineerTaskSlice[][] waves,
        out string? error)
    {
        waves = [];
        error = null;
        if (slices is null || slices.Count == 0)
        {
            return true;
        }

        var byId = new Dictionary<string, EngineerTaskSlice>(slices.Count, StringComparer.Ordinal);
        foreach (var slice in slices)
        {
            if (slice is null || string.IsNullOrWhiteSpace(slice.SliceId))
            {
                error = "Every EngineerTaskSlice must declare a non-empty SliceId.";
                return false;
            }

            var id = slice.SliceId.Trim();
            if (!byId.TryAdd(id, slice))
            {
                error = $"Duplicate SliceId '{id}' in slice plan.";
                return false;
            }
        }

        var remainingDeps = new Dictionary<string, HashSet<string>>(slices.Count, StringComparer.Ordinal);
        foreach (var slice in slices)
        {
            var id = slice.SliceId.Trim();
            var deps = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rawDep in slice.DependsOnSliceIds ?? [])
            {
                if (string.IsNullOrWhiteSpace(rawDep))
                {
                    continue;
                }

                var dep = rawDep.Trim();
                if (string.Equals(dep, id, StringComparison.Ordinal))
                {
                    error = $"Slice '{id}' cannot depend on itself.";
                    return false;
                }

                if (!byId.ContainsKey(dep))
                {
                    error = $"Slice '{id}' depends on unknown slice '{dep}'.";
                    return false;
                }

                deps.Add(dep);
            }

            remainingDeps[id] = deps;
        }

        var waveList = new List<EngineerTaskSlice[]>();
        var unscheduled = slices.Select(static s => s.SliceId.Trim()).ToList();

        while (unscheduled.Count > 0)
        {
            var readyIds = unscheduled
                .Where(id => remainingDeps[id].Count == 0)
                .ToArray();

            if (readyIds.Length == 0)
            {
                error = "Slice plan contains a dependency cycle.";
                return false;
            }

            waveList.Add([.. readyIds.Select(id => byId[id])]);
            foreach (var completedId in readyIds)
            {
                unscheduled.Remove(completedId);
            }

            foreach (var pendingId in unscheduled)
            {
                foreach (var completedId in readyIds)
                {
                    remainingDeps[pendingId].Remove(completedId);
                }
            }
        }

        waves = [.. waveList];
        return true;
    }

    public static EngineerTaskSlice[] FromTaskPlan(EngineerTaskPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Slices is { Length: > 0 })
        {
            return plan.Slices;
        }

        if (plan.Tasks is null || plan.Tasks.Length == 0)
        {
            return [];
        }

        var converted = new EngineerTaskSlice[plan.Tasks.Length];
        for (var i = 0; i < plan.Tasks.Length; i++)
        {
            var task = plan.Tasks[i];
            converted[i] = new EngineerTaskSlice(
                SliceId: task.Id,
                Title: task.Title,
                OwnedPaths: task.TargetFiles ?? [],
                DependsOnSliceIds: task.DependsOn ?? [],
                Status: "pending",
                Percent: 0,
                AssignedAgentKey: string.Create(CultureInfo.InvariantCulture, $"ino.slice-{i + 1}"),
                Language: string.IsNullOrWhiteSpace(task.Language) ? "csharp" : task.Language,
                Instructions: task.Instructions ?? string.Empty);
        }

        return converted;
    }
}

[GenerateSerializer, Alias("engineer.ratified-plan")]
public sealed record RatifiedPlan(
    [property: Id(0)] string Goal,
    [property: Id(1)] string ArchitectureSummary,
    [property: Id(2)] string[] CleanupTargets,
    [property: Id(3)] EngineerTaskSlice[] Slices,
    [property: Id(4)] bool Ratified = true,
    [property: Id(5)] string? RatifiedBy = null,
    [property: Id(6)] DateTimeOffset? RatifiedAt = null,
    [property: Id(7)] EngineeringHarnessKind Harness = EngineeringHarnessKind.InoNative)
{
    public RatifiedPlan()
        : this("", "", [], [], true, null, null, EngineeringHarnessKind.InoNative)
    {
    }

    public NoOverlappingFileSlices DisjointSlicesRule() => new(Slices ?? []);

    public EngineerTaskSlice[][] TopologicalWaves() => EngineerTaskSlices.ComputeWaves(Slices ?? []);
}

[GenerateSerializer, Alias("engineer.mission")]
public sealed record Mission(
    [property: Id(0)] string MissionId,
    [property: Id(1)] string RepositoryId,
    [property: Id(2)] string Goal,
    [property: Id(3)] EngineeringHarnessKind Harness = EngineeringHarnessKind.InoNative,
    [property: Id(4)] bool RequirePlanRatification = false,
    [property: Id(5)] RatifiedPlan? RatifiedPlan = null,
    [property: Id(6)] MissionCollaborator[]? Collaborators = null,
    [property: Id(7)] string? LinkedCircleBrainId = null)
{
    public Mission()
        : this("", "os", "", EngineeringHarnessKind.InoNative, false, null, null, null)
    {
    }
}

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
    [property: Id(5)] string? RunId = null,
    [property: Id(6)] bool RequirePlanRatification = false,
    [property: Id(7)] EngineeringHarnessKind Harness = EngineeringHarnessKind.InoNative,
    [property: Id(8)] string[]? CleanupTargets = null,
    [property: Id(9)] EngineerTaskSlice[]? Slices = null,
    [property: Id(10)] string? CircleBrainId = null)
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

    [JsonIgnore]
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
        var summary = FormattableString.Invariant(
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
    [property: Id(14)] string? FailureDetail = null,
    [property: Id(15)] EngineeringHarnessKind Harness = EngineeringHarnessKind.InoNative,
    [property: Id(16)] EngineerTaskSlice[]? Slices = null,
    [property: Id(17)] string[]? DeletedFiles = null)
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
    [property: Id(4)] EngineerTaskItem[] Tasks,
    [property: Id(5)] string[]? CleanupTargets = null,
    [property: Id(6)] EngineerTaskSlice[]? Slices = null)
{
    public EngineerTaskPlan() : this("", "", "", [], []) { }

    public string? Validate(int maxChangedFiles = 5)
    {
        if (string.IsNullOrWhiteSpace(Goal))
        {
            return "EngineerTaskPlan requires a non-empty goal.";
        }

        if ((Tasks is null || Tasks.Length == 0) && (Slices is null || Slices.Length == 0))
        {
            return "EngineerTaskPlan must contain at least one task or slice.";
        }

        var distinctTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawCleanup in CleanupTargets ?? [])
        {
            if (string.IsNullOrWhiteSpace(rawCleanup))
            {
                return "EngineerTaskPlan contains a blank cleanup target path.";
            }

            var clean = rawCleanup.Trim().Replace('\\', '/');
            if (clean.StartsWith('/') || Path.IsPathRooted(clean) || clean.Split('/').Any(static seg => seg.Length == 0 || seg.StartsWith('.')))
            {
                return $"EngineerTaskPlan contains invalid or hidden cleanup target path '{rawCleanup}'.";
            }

            distinctTargets.Add(clean);
        }

        if (Tasks is { Length: > 0 })
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
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

            if (!TryComputeTopologicalOrder(out _))
            {
                return "EngineerTaskPlan contains a dependency cycle.";
            }
        }

        if (Slices is { Length: > 0 })
        {
            foreach (var slice in Slices)
            {
                foreach (var rawOwned in slice?.OwnedPaths ?? [])
                {
                    if (string.IsNullOrWhiteSpace(rawOwned))
                    {
                        return $"Slice '{slice?.SliceId}' contains a blank owned path.";
                    }

                    var clean = rawOwned.Trim().Replace('\\', '/');
                    if (clean.StartsWith('/') || Path.IsPathRooted(clean) || clean.Split('/').Any(static seg => seg.Length == 0 || seg.StartsWith('.')))
                    {
                        return $"Slice '{slice?.SliceId}' contains invalid or hidden owned path '{rawOwned}'.";
                    }

                    distinctTargets.Add(clean);
                }
            }

            var disjointRule = new NoOverlappingFileSlices(Slices);
            if (disjointRule.Broken)
            {
                return $"{disjointRule.Name}: {disjointRule.Message}";
            }
        }

        if (maxChangedFiles > 0 && distinctTargets.Count > maxChangedFiles)
        {
            return $"EngineerTaskPlan targets {distinctTargets.Count} distinct files, exceeding the policy maximum of {maxChangedFiles}.";
        }

        return null;
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
    string? FailureDetail = null);

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
