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
    internal static EngineerTaskPlan BuildStructuredDefaultPlan(
        string repositoryId,
        string goal,
        IReadOnlyList<string> allowedExtensions,
        int maxChangedFiles,
        RepositoryCodeGraphDigest? digest = null,
        IReadOnlyList<WorktreeSymbolInfo>? liveSymbols = null,
        EngineerPlannerMemorySnapshot? memory = null)
    {
        var effectiveDigest = digest ?? new RepositoryCodeGraphDigest(repositoryId, string.Empty, 0);
        var symbols = liveSymbols ?? [];
        var briefText = memory?.BriefContent;

        using (var archSpan = EngineerTelemetry.Source.StartActivity("engineer.plan.architect"))
        {
            archSpan?.SetTag("engineer.repository", repositoryId);
            archSpan?.SetTag("engineer.mode", "deterministic");
            archSpan?.SetTag("engineer.codegraph_bytes", effectiveDigest.ByteCount);
            archSpan?.SetStatus(ActivityStatusCode.Ok);
        }

        using (var criticSpan = EngineerTelemetry.Source.StartActivity("engineer.plan.critic"))
        {
            criticSpan?.SetTag("engineer.repository", repositoryId);
            criticSpan?.SetTag("engineer.mode", "deterministic");
            criticSpan?.SetTag("engineer.codegraph_bytes", effectiveDigest.ByteCount);
            criticSpan?.SetStatus(ActivityStatusCode.Ok);
        }

        var wantsDart = goal.Contains("dart", StringComparison.OrdinalIgnoreCase)
            || goal.Contains("flutter", StringComparison.OrdinalIgnoreCase)
            || goal.Contains("widget", StringComparison.OrdinalIgnoreCase);
        var wantsDocs = goal.Contains("readme", StringComparison.OrdinalIgnoreCase)
            || goal.Contains("markdown", StringComparison.OrdinalIgnoreCase);

        var primaryLanguage = wantsDart && goal.Contains("c#", StringComparison.OrdinalIgnoreCase)
            ? "mixed"
            : wantsDart
                ? "dart"
                : wantsDocs && !goal.Contains(".cs", StringComparison.OrdinalIgnoreCase)
                    ? "markdown"
                    : "csharp";

        var extLabel = allowedExtensions.Count > 0 ? string.Join(", ", allowedExtensions) : ".cs, .dart, .md";
        var briefTag = !string.IsNullOrWhiteSpace(memory?.BriefKey) ? $" [brief: {memory!.BriefKey}]" : string.Empty;
        var symbolTag = symbols.Count > 0
            ? string.Create(CultureInfo.InvariantCulture, $" Grounded on {symbols.Count} live Roslyn symbol(s){briefTag}.")
            : briefTag.Length > 0 ? $" Grounded on WorkingMemory{briefTag}." : string.Empty;

        var archMessage = $"Decompose '{goal}' on '{repositoryId}' into isolated worktree tasks respecting module boundaries, Roslyn AST/whitespace gates, and the {maxChangedFiles}-file policy ({extLabel}).{symbolTag}";
        var criticMessage = "Enforce Triple Git Lock, zero unpushed base commits, NoOverlappingFileSlices disjoint waves, Roslyn zero-error diagnostics, Release build/test gates, zero 4xx/5xx & zero OTel/trace errors, and SHA-bound human approval before push.";

        EngineerBrainstormTurn[] transcript =
        [
            new("User", "user", "Goal & Scope", goal),
            new("Architect", "architect", "DDD & Worktree Plan", archMessage),
            new("Critic", "critic", "Rules, Safety & Verification", criticMessage),
        ];

        // Grounded CodeGraph synthesis when live C# symbols are discovered in the target repository!
        if (symbols.Count > 0 && primaryLanguage is "csharp" or "mixed")
        {
            var maxCleanupBudget = Math.Max(0, maxChangedFiles - 1);
            var cleanupTargets = SelectGroundedCleanupTargets(
                effectiveDigest.PotentialCleanupTargets ?? [],
                goal,
                briefText,
                maxCleanupBudget,
                excludeWholeFiles: null);

            var deletedWholeFiles = cleanupTargets
                .Where(static c => !c.Contains('#', StringComparison.Ordinal) && !c.Contains("::", StringComparison.Ordinal))
                .Select(EngineerTaskSlices.NormalizePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var remainingFileBudget = Math.Max(1, maxChangedFiles - cleanupTargets.Length);
            var selectedFiles = SelectGroundedSliceFiles(symbols, goal, briefText, deletedWholeFiles, remainingFileBudget);

            if (selectedFiles.Length >= 2)
            {
                var groundedTasks = new EngineerTaskItem[selectedFiles.Length];
                var groundedSlices = new EngineerTaskSlice[selectedFiles.Length];
                for (var i = 0; i < selectedFiles.Length; i++)
                {
                    var file = selectedFiles[i];
                    var fileSymbols = symbols
                        .Where(s => string.Equals(EngineerTaskSlices.NormalizePath(s.FilePath), file, StringComparison.OrdinalIgnoreCase))
                        .Select(static s => s.Name)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    var typeLabel = fileSymbols.Length > 0 ? string.Join(", ", fileSymbols.Take(2)) : Path.GetFileNameWithoutExtension(file);
                    var sliceId = string.Create(CultureInfo.InvariantCulture, $"slice-{i + 1}-{SanitizeSliceToken(typeLabel)}");
                    var agentKey = string.Create(CultureInfo.InvariantCulture, $"ino.slice-{i + 1}");
                    var title = $"Update {typeLabel} in {Path.GetFileName(file)}";
                    var instructions = $"Apply surgical Roslyn AST edits to '{file}' ({typeLabel}) for: {goal}";

                    groundedTasks[i] = new EngineerTaskItem(
                        Id: sliceId,
                        Title: title,
                        Language: "csharp",
                        TargetFiles: [file],
                        DependsOn: [],
                        Instructions: instructions);
                    groundedSlices[i] = new EngineerTaskSlice(
                        SliceId: sliceId,
                        Title: title,
                        OwnedPaths: [file],
                        DependsOnSliceIds: [],
                        Status: "pending",
                        Percent: 0,
                        AssignedAgentKey: agentKey,
                        Language: "csharp",
                        Instructions: instructions);
                }

                var validatedSlices = EnsureDisjointSlices(groundedSlices);
                return new EngineerTaskPlan(
                    Goal: goal,
                    ArchitectureSummary: archMessage,
                    CriticReview: criticMessage,
                    Transcript: transcript,
                    Tasks: groundedTasks,
                    CleanupTargets: cleanupTargets.Length > 0 ? cleanupTargets : null,
                    Slices: validatedSlices,
                    CodeGraphDigest: string.IsNullOrWhiteSpace(effectiveDigest.CompactDigest) ? null : effectiveDigest.CompactDigest);
            }

            if (selectedFiles.Length == 1)
            {
                var singleFile = selectedFiles[0];
                var singleType = symbols
                    .FirstOrDefault(s => string.Equals(EngineerTaskSlices.NormalizePath(s.FilePath), singleFile, StringComparison.OrdinalIgnoreCase))?.Name
                    ?? Path.GetFileNameWithoutExtension(singleFile);
                var sliceId = $"slice-1-{SanitizeSliceToken(singleType)}";
                var title = $"Update {singleType} in {Path.GetFileName(singleFile)}";
                var instructions = $"Inspect '{singleFile}' ({singleType}) and implement: {goal}";

                EngineerTaskItem[] singleTasks =
                [
                    new(sliceId, title, "csharp", [singleFile], [], instructions),
                ];
                EngineerTaskSlice[] singleSlices =
                [
                    new(sliceId, title, [singleFile], [], "pending", 0, "ino.slice-1", "csharp", instructions),
                ];

                return new EngineerTaskPlan(
                    Goal: goal,
                    ArchitectureSummary: archMessage,
                    CriticReview: criticMessage,
                    Transcript: transcript,
                    Tasks: singleTasks,
                    CleanupTargets: cleanupTargets.Length > 0 ? cleanupTargets : null,
                    Slices: singleSlices,
                    CodeGraphDigest: string.IsNullOrWhiteSpace(effectiveDigest.CompactDigest) ? null : effectiveDigest.CompactDigest);
            }
        }

        var hasCompoundGoal = maxChangedFiles >= 2
            && primaryLanguage == "csharp"
            && goal.Contains(" and ", StringComparison.OrdinalIgnoreCase);

        EngineerTaskItem[] tasks = wantsDart && primaryLanguage == "mixed" && maxChangedFiles >= 2
            ? [
                new(
                    Id: "task-1-csharp",
                    Title: "Implement C# domain contracts & neuron logic via Roslyn",
                    Language: "csharp",
                    TargetFiles: [],
                    DependsOn: [],
                    Instructions: $"Inspect C# symbols and apply surgical Roslyn-verified edits for: {goal}"),
                new(
                    Id: "task-2-dart",
                    Title: "Align Flutter/Dart UI mirror & semantics",
                    Language: "dart",
                    TargetFiles: [],
                    DependsOn: ["task-1-csharp"],
                    Instructions: $"Update aligned Dart files within policy limits for: {goal}"),
            ]
            : hasCompoundGoal
                ? [
                    new(
                        Id: "task-1-foundation",
                        Title: $"Implement primary C# members for '{Truncate(goal, 42)}'",
                        Language: "csharp",
                        TargetFiles: [],
                        DependsOn: [],
                        Instructions: $"Inspect worktree Roslyn symbols and implement the primary part of: {goal}"),
                    new(
                        Id: "task-2-refinement",
                        Title: "Complete secondary C# members & verify AST diagnostics",
                        Language: "csharp",
                        TargetFiles: [],
                        DependsOn: ["task-1-foundation"],
                        Instructions: $"Complete remaining requested members and verify zero Roslyn errors for: {goal}"),
                ]
                : [
                    new(
                        Id: "task-1-implement",
                        Title: $"Implement {primaryLanguage} changes for '{Truncate(goal, 48)}'",
                        Language: primaryLanguage,
                        TargetFiles: [],
                        DependsOn: [],
                        Instructions: $"Inspect the confined worktree and implement the requested change cleanly: {goal}"),
                ];

        var defaultDraft = new EngineerTaskPlan(
            Goal: goal,
            ArchitectureSummary: archMessage,
            CriticReview: criticMessage,
            Transcript: transcript,
            Tasks: tasks,
            CodeGraphDigest: string.IsNullOrWhiteSpace(effectiveDigest.CompactDigest) ? null : effectiveDigest.CompactDigest);

        return defaultDraft with { Slices = EnsureDisjointSlices(EngineerTaskSlices.FromTaskPlan(defaultDraft)) };
    }

}
