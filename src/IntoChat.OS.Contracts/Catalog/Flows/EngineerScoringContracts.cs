using System.Globalization;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Flows;

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
    [property: Id(17)] string[]? DeletedFiles = null,
    [property: Id(18)] SelfReviewReport? SelfReview = null)
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

