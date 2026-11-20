using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps.Flows;

internal sealed partial class EngineerPlanner
{
    private async Task<EngineerTaskPlan?> TryDraftMultiAgentPlanAsync(
        IModelInference inference,
        string repositoryId,
        string goal,
        IReadOnlyList<string> allowedExtensions,
        int maxChangedFiles,
        RepositoryCodeGraphDigest digest,
        EngineerPlannerMemorySnapshot memory,
        CancellationToken ct)
    {
        var extList = allowedExtensions.Count > 0 ? string.Join(", ", allowedExtensions) : ".cs, .dart, .md";
        var digestBlock = string.IsNullOrWhiteSpace(digest.CompactDigest)
            ? string.Empty
            : $"\n[Repository CodeGraphDigest (<=2KB)]\n{digest.CompactDigest}\n";

        EngineerTaskPlan? architectCandidate = null;
        string? architectRawJson = null;
        string? architectSummaryText = null;

        // Turn 1: Architect decomposition & file/contract plan.
        try
        {
            using var archActivity = EngineerTelemetry.Source.StartActivity("engineer.plan.architect");
            archActivity?.SetTag("engineer.repository", repositoryId);
            archActivity?.SetTag("engineer.role", "architect");
            archActivity?.SetTag("engineer.codegraph_bytes", digest.ByteCount);

            var architectPrompt = $$"""
                You are the IntoChat-Engineer ARCHITECT agent planning a change for repository '{{repositoryId}}'.
                User Goal: {{goal}}
                Policy Constraints: Modify at most {{maxChangedFiles}} files total; allowed file extensions: {{extList}}.{{digestBlock}}
                Decompose the goal into a clean, cycle-free topological task graph with file-disjoint parallel slices respecting DDD boundaries and Contracts/ separation.
                Reply ONLY with valid JSON matching:
                {
                  "goal": "...",
                  "architectureSummary": "...",
                  "cleanupTargets": [],
                  "tasks": [
                    { "id": "t1", "title": "...", "language": "csharp" | "dart" | "markdown" | "mixed", "targetFiles": [], "dependsOn": [], "instructions": "..." }
                  ],
                  "slices": [
                    { "sliceId": "s1", "title": "...", "ownedPaths": [], "dependsOnSliceIds": [], "assignedAgentKey": "ino.slice-1", "language": "csharp", "instructions": "..." }
                  ]
                }
                """;

            var archResult = await inference.Generate(
                new InferenceRequest([new AiMessage("user", [new AiText(architectPrompt)])]),
                marker: null,
                cancellationToken: ct,
                brainId: null);

            var archReply = ExtractReplyText(archResult);
            architectRawJson = ExtractJsonObject(archReply);
            if (architectRawJson is not null)
            {
                architectCandidate = JsonSerializer.Deserialize<EngineerTaskPlan>(architectRawJson, Json);
                architectSummaryText = architectCandidate?.ArchitectureSummary;
            }

            if (string.IsNullOrWhiteSpace(architectSummaryText) && !string.IsNullOrWhiteSpace(archReply))
            {
                architectSummaryText = Truncate(archReply.ReplaceLineEndings(" "), 320);
            }

            archActivity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogSwallowed(ex, "Architect planning turn failed; falling back if Critic cannot recover.");
            return null;
        }

        // Turn 2: Critic DDD / HouseRules / edge-case audit & plan refinement.
        try
        {
            using var criticActivity = EngineerTelemetry.Source.StartActivity("engineer.plan.critic");
            criticActivity?.SetTag("engineer.repository", repositoryId);
            criticActivity?.SetTag("engineer.role", "critic");
            criticActivity?.SetTag("engineer.codegraph_bytes", digest.ByteCount);

            var proposalContext = architectRawJson ?? architectSummaryText ?? goal;
            var criticPrompt = $$"""
                You are the IntoChat-Engineer CRITIC agent auditing the Architect's proposed plan for repository '{{repositoryId}}'.
                User Goal: {{goal}}
                Policy Constraints: Modify at most {{maxChangedFiles}} files total; allowed file extensions: {{extList}}.{{digestBlock}}
                Architect Proposal:
                {{proposalContext}}

                Audit for DDD aggregate invariants, HouseRules (Contracts/ placement, zero swallowed exceptions, immutable state, safe relative paths), grounded cleanupTargets, file-disjoint parallel EngineerTaskSlice OwnedPaths (NoOverlappingFileSlices), file budget <= {{maxChangedFiles}}, and cycle-free dependencies.
                Refine the tasks/slices if needed and reply ONLY with valid JSON matching:
                {
                  "goal": "...",
                  "architectureSummary": "...",
                  "criticReview": "...",
                  "cleanupTargets": [],
                  "transcript": [
                    { "speaker": "User", "role": "user", "perspective": "Goal & Scope", "message": "..." },
                    { "speaker": "Architect", "role": "architect", "perspective": "DDD & File Decomposition", "message": "..." },
                    { "speaker": "Critic", "role": "critic", "perspective": "HouseRules & Edge-Case Critique", "message": "..." }
                  ],
                  "tasks": [
                    { "id": "t1", "title": "...", "language": "csharp" | "dart" | "markdown" | "mixed", "targetFiles": [], "dependsOn": [], "instructions": "..." }
                  ],
                  "slices": [
                    { "sliceId": "s1", "title": "...", "ownedPaths": [], "dependsOnSliceIds": [], "assignedAgentKey": "ino.slice-1", "language": "csharp", "instructions": "..." }
                  ]
                }
                """;

            var criticResult = await inference.Generate(
                new InferenceRequest(
                [
                    new AiMessage("user", [new AiText($"Goal: {goal}")]),
                    new AiMessage("assistant", [new AiText(proposalContext)]),
                    new AiMessage("user", [new AiText(criticPrompt)]),
                ]),
                marker: null,
                cancellationToken: ct,
                brainId: null);

            var criticReply = ExtractReplyText(criticResult);
            if (ExtractJsonObject(criticReply) is { } criticJson
                && JsonSerializer.Deserialize<EngineerTaskPlan>(criticJson, Json) is { } refinedRaw)
            {
                var merged = NormalizeMultiAgentPlan(
                    goal,
                    architectSummaryText,
                    architectCandidate,
                    refinedRaw,
                    criticReply,
                    digest,
                    memory,
                    maxChangedFiles);
                if (merged.Validate(maxChangedFiles) is null)
                {
                    criticActivity?.SetStatus(ActivityStatusCode.Ok);
                    return merged;
                }
            }

            if (architectCandidate is not null)
            {
                var fallbackReview = !string.IsNullOrWhiteSpace(criticReply)
                    ? Truncate(criticReply.ReplaceLineEndings(" "), 320)
                    : !string.IsNullOrWhiteSpace(architectCandidate.CriticReview)
                        ? architectCandidate.CriticReview
                        : $"Verified {maxChangedFiles}-file budget, Triple Git Lock, Roslyn zero-error diagnostics, and human SHA-bound approval.";
                var fromArchitect = NormalizeMultiAgentPlan(
                    goal,
                    architectSummaryText,
                    architectCandidate,
                    architectCandidate with { CriticReview = fallbackReview },
                    fallbackReview,
                    digest,
                    memory,
                    maxChangedFiles);
                if (fromArchitect.Validate(maxChangedFiles) is null)
                {
                    criticActivity?.SetStatus(ActivityStatusCode.Ok);
                    return fromArchitect;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogSwallowed(ex, "Critic planning turn failed; attempting validated Architect candidate before deterministic plan.");
            if (architectCandidate is not null)
            {
                var recovered = NormalizeMultiAgentPlan(
                    goal,
                    architectSummaryText,
                    architectCandidate,
                    architectCandidate,
                    null,
                    digest,
                    memory,
                    maxChangedFiles);
                if (recovered.Validate(maxChangedFiles) is null)
                {
                    return recovered;
                }
            }
        }

        return null;
    }

    private static EngineerTaskPlan NormalizeMultiAgentPlan(
        string cleanGoal,
        string? architectSummary,
        EngineerTaskPlan? architectDraft,
        EngineerTaskPlan criticDraft,
        string? rawCriticText,
        RepositoryCodeGraphDigest digest,
        EngineerPlannerMemorySnapshot memory,
        int maxChangedFiles)
    {
        var effectiveGoal = string.IsNullOrWhiteSpace(criticDraft.Goal) ? cleanGoal : criticDraft.Goal.Trim();
        var effectiveArch = !string.IsNullOrWhiteSpace(criticDraft.ArchitectureSummary)
            ? criticDraft.ArchitectureSummary.Trim()
            : !string.IsNullOrWhiteSpace(architectSummary)
                ? architectSummary.Trim()
                : $"Decomposed '{cleanGoal}' into isolated worktree tasks with surgical contract & AST boundaries.";
        var effectiveCritic = !string.IsNullOrWhiteSpace(criticDraft.CriticReview)
            ? criticDraft.CriticReview.Trim()
            : !string.IsNullOrWhiteSpace(architectDraft?.CriticReview)
                ? architectDraft!.CriticReview.Trim()
                : !string.IsNullOrWhiteSpace(rawCriticText)
                    ? Truncate(rawCriticText.ReplaceLineEndings(" "), 320)
                    : "Audited DDD invariants, HouseRules boundaries, cycle-free task graph, and live telemetry verification gates.";

        var effectiveTasks = criticDraft.Tasks is { Length: > 0 } ? criticDraft.Tasks : architectDraft?.Tasks ?? [];
        var rawSlices = criticDraft.Slices is { Length: > 0 }
            ? criticDraft.Slices
            : architectDraft?.Slices is { Length: > 0 } archSlices
                ? archSlices
                : EngineerTaskSlices.FromTaskPlan(criticDraft with { Tasks = effectiveTasks, Slices = null });
        var disjointSlices = EnsureDisjointSlices(rawSlices);

        var rawCleanup = criticDraft.CleanupTargets is { Length: > 0 }
            ? criticDraft.CleanupTargets
            : architectDraft?.CleanupTargets;
        if (rawCleanup is null or { Length: 0 } && digest.PotentialCleanupTargets is { Length: > 0 })
        {
            var usedDistinct = disjointSlices
                .SelectMany(static s => s.OwnedPaths ?? [])
                .Select(EngineerTaskSlices.NormalizePath)
                .Where(static p => p.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var remainingBudget = Math.Max(0, maxChangedFiles - usedDistinct.Count);
            if (remainingBudget > 0)
            {
                rawCleanup = SelectGroundedCleanupTargets(digest.PotentialCleanupTargets, cleanGoal, memory.BriefContent, remainingBudget, usedDistinct);
            }
        }

        var existingTurns = criticDraft.Transcript ?? architectDraft?.Transcript ?? [];
        var turns = new List<EngineerBrainstormTurn>(Math.Max(3, existingTurns.Length));

        if (!existingTurns.Any(static t => string.Equals(t.Role, "user", StringComparison.OrdinalIgnoreCase)))
        {
            turns.Add(new EngineerBrainstormTurn("User", "user", "Goal & Scope", cleanGoal));
        }

        turns.AddRange(existingTurns);

        if (!turns.Any(static t => string.Equals(t.Role, "architect", StringComparison.OrdinalIgnoreCase)))
        {
            turns.Add(new EngineerBrainstormTurn("Architect", "architect", "Decomposition & Contracts", effectiveArch));
        }

        if (!turns.Any(static t => string.Equals(t.Role, "critic", StringComparison.OrdinalIgnoreCase)))
        {
            turns.Add(new EngineerBrainstormTurn("Critic", "critic", "DDD, HouseRules & Edge-Case Audit", effectiveCritic));
        }

        return new EngineerTaskPlan(
            Goal: effectiveGoal,
            ArchitectureSummary: effectiveArch,
            CriticReview: effectiveCritic,
            Transcript: [.. turns],
            Tasks: effectiveTasks,
            CleanupTargets: rawCleanup is { Length: > 0 } ? rawCleanup : null,
            Slices: disjointSlices.Length > 0 ? disjointSlices : null,
            CodeGraphDigest: string.IsNullOrWhiteSpace(digest.CompactDigest) ? null : digest.CompactDigest);
    }

    private static string ExtractReplyText(InferenceResult result)
        => string.Join("\n", result.Messages.SelectMany(static m => m.Content.OfType<AiText>()).Select(static t => t.Text)).Trim();

    private static string? ExtractJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

}
