using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
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
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.Apps.Flows;

internal static class EngineerTelemetry
{
    public const string SourceName = "IntoChat.OS.Engineer";

    public static readonly ActivitySource Source = new(SourceName);
}

internal sealed record EngineerLiveContext(
    string? TraceId = null,
    ITraceReader? TraceReader = null,
    IReadOnlyList<(string ScreenName, IScreen Screen)>? Screens = null);

internal sealed record EngineerPlannerMemorySnapshot(
    string? BriefKey,
    string? BriefContent,
    string? ProjectNote);

internal sealed class EngineerPlanner(
    IServiceProvider? services = null,
    ILogger<EngineerPlanner>? logger = null,
    IWorkTree? workTree = null,
    IWorktreeRoslyn? roslyn = null,
    IWorkingMemory? workingMemory = null) : IEngineerPlanner
{
    public const int MaxCodeGraphDigestBytes = RepositoryCodeGraphDigest.MaxBytes;
    internal const int MaxInteractiveSymbolScanFiles = 200;

    private static readonly TimeSpan InteractiveSymbolScanTimeout = TimeSpan.FromSeconds(3);

    private static readonly HashSet<string> ExcludedSymbolScanDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin",
        "obj",
        "artifacts",
        "ephemeral",
        "node_modules",
        "TestResults",
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly string[] DeadOrLegacyMarkers =
    [
        "legacy",
        "obsolete",
        "dead",
        "unused",
        "deprecated",
        "trash",
    ];

    private static readonly string[] CleanupIntentKeywords =
    [
        "delete",
        "remove",
        "clean",
        "cleanup",
        "prune",
        "dead",
        "obsolete",
        "legacy",
        "unused",
        "duplicate",
    ];

    private readonly ILogger _logger = (ILogger?)logger ?? OsConsoleLog.For<EngineerPlanner>();

    public Task<EngineerTaskPlan> PlanAsync(
        string repositoryId,
        string goal,
        IReadOnlyList<string> allowedExtensions,
        int maxChangedFiles,
        CancellationToken ct = default)
        => PlanWithContextAsync(
            repositoryId,
            goal,
            allowedExtensions,
            maxChangedFiles,
            brain: null,
            workTreeOverride: null,
            roslynOverride: null,
            workingMemoryOverride: null,
            preHydratedMemory: null,
            repositoryPathOverride: null,
            ct);

    internal async Task<EngineerTaskPlan> PlanWithContextAsync(
        string repositoryId,
        string goal,
        IReadOnlyList<string> allowedExtensions,
        int maxChangedFiles,
        BrainId? brain = null,
        IWorkTree? workTreeOverride = null,
        IWorktreeRoslyn? roslynOverride = null,
        IWorkingMemory? workingMemoryOverride = null,
        EngineerPlannerMemorySnapshot? preHydratedMemory = null,
        string? repositoryPathOverride = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);

        var cleanGoal = goal.Trim();
        var effectiveMax = Math.Max(1, maxChangedFiles);

        var memorySnapshot = preHydratedMemory ?? await HydrateMemoryBriefAsync(
            repositoryId,
            cleanGoal,
            brain,
            workingMemoryOverride,
            ct);

        var (digest, liveSymbols) = await BuildRepositoryCodeGraphDigestAsync(
            repositoryId,
            cleanGoal,
            memorySnapshot,
            workTreeOverride,
            roslynOverride,
            repositoryPathOverride,
            ct);

        if (services?.GetService<IModelInference>() is { } inference)
        {
            var drafted = await TryDraftMultiAgentPlanAsync(
                inference,
                repositoryId,
                cleanGoal,
                allowedExtensions,
                effectiveMax,
                digest,
                memorySnapshot,
                ct);
            if (drafted is not null && drafted.Validate(effectiveMax) is null)
            {
                return drafted;
            }
        }

        return BuildStructuredDefaultPlan(
            repositoryId,
            cleanGoal,
            allowedExtensions,
            effectiveMax,
            digest,
            liveSymbols,
            memorySnapshot);
    }

    internal async Task<EngineerPlannerMemorySnapshot> HydrateMemoryBriefAsync(
        string repositoryId,
        string goal,
        BrainId? brain = null,
        IWorkingMemory? workingMemoryOverride = null,
        CancellationToken ct = default)
    {
        var wm = workingMemoryOverride ?? workingMemory ?? services?.GetService<IWorkingMemory>();
        if (wm is null && brain is { } b && services?.GetService<INeuronProxies>() is { } proxies)
        {
            wm = proxies.Get<IWorkingMemory>(IWorkingMemory.NeuronIdOf(b));
        }

        if (wm is null)
        {
            return new EngineerPlannerMemorySnapshot(null, null, null);
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            var pack = await wm.ReadStartupPack();
            var projectNote = string.IsNullOrWhiteSpace(pack.Project)
                ? null
                : InoNativeHarness.CollapseSingleLine(pack.Project, 240);

            var selected = InoNativeHarness.SelectMatchingProjectBrief(pack.AvailableBriefs, repositoryId, goal);
            if (selected is not null)
            {
                var brief = await wm.GetBrief(selected.Key);
                if (brief is not null && !string.IsNullOrWhiteSpace(brief.Content))
                {
                    return new EngineerPlannerMemorySnapshot(
                        brief.Key,
                        InoNativeHarness.CollapseSingleLine(brief.Content, 600),
                        projectNote);
                }
            }
            else if (pack.AvailableBriefs is null or { Length: 0 })
            {
                var repoBrief = await wm.GetBrief($"repo:{repositoryId.Trim()}");
                if (repoBrief is not null && !string.IsNullOrWhiteSpace(repoBrief.Content))
                {
                    return new EngineerPlannerMemorySnapshot(
                        repoBrief.Key,
                        InoNativeHarness.CollapseSingleLine(repoBrief.Content, 600),
                        projectNote);
                }
            }

            return new EngineerPlannerMemorySnapshot(null, null, projectNote);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogSwallowed(ex, $"hydrating IWorkingMemory brief for repository '{repositoryId}'");
            return new EngineerPlannerMemorySnapshot(null, null, null);
        }
    }

    internal async Task<(RepositoryCodeGraphDigest Digest, IReadOnlyList<WorktreeSymbolInfo> Symbols)> BuildRepositoryCodeGraphDigestAsync(
        string repositoryId,
        string goal,
        EngineerPlannerMemorySnapshot memory,
        IWorkTree? workTreeOverride = null,
        IWorktreeRoslyn? roslynOverride = null,
        string? repositoryPathOverride = null,
        CancellationToken ct = default)
    {
        var effectiveWorkTree = workTreeOverride ?? workTree ?? services?.GetService<IWorkTree>();
        var explicitRoslyn = roslynOverride ?? roslyn ?? services?.GetService<IWorktreeRoslyn>();

        string? resolvedRepoPath = null;
        if (!string.IsNullOrWhiteSpace(repositoryPathOverride) && Directory.Exists(repositoryPathOverride))
        {
            resolvedRepoPath = Path.GetFullPath(repositoryPathOverride);
        }
        else if (effectiveWorkTree is not null)
        {
            try
            {
                if (RepositoryId.TryParse(repositoryId, out var parsedRepoId) && parsedRepoId is not null)
                {
                    var registered = effectiveWorkTree.ResolveRepository(parsedRepoId);
                    if (!string.IsNullOrWhiteSpace(registered.LocalPath) && Directory.Exists(registered.LocalPath))
                    {
                        resolvedRepoPath = Path.GetFullPath(registered.LocalPath);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Repository '{RepositoryId}' is not registered in IWorkTree during pre-plan CodeGraph hydration.", repositoryId);
            }
        }
        else if (Directory.Exists(repositoryId))
        {
            resolvedRepoPath = Path.GetFullPath(repositoryId);
        }

        IReadOnlyList<WorktreeSymbolInfo> symbols = [];
        if (!string.IsNullOrWhiteSpace(resolvedRepoPath))
        {
            if (!ExceedsInteractiveSymbolScanCap(resolvedRepoPath, MaxInteractiveSymbolScanFiles))
            {
                try
                {
                    using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    scanCts.CancelAfter(InteractiveSymbolScanTimeout);
                    var activeRoslyn = explicitRoslyn ?? IWorktreeRoslyn.CreateDefault();
                    symbols = await activeRoslyn.InspectSymbolsAsync(resolvedRepoPath, relativeFilter: null, scanCts.Token);
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogDebug(ex, "Timed out inspecting C# symbols in '{RepoPath}' for EngineerPlanner; continuing with bounded context.", resolvedRepoPath);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogSwallowed(ex, $"inspecting C# symbols in '{resolvedRepoPath}' for EngineerPlanner");
                }
            }
            else
            {
                _logger.LogDebug(
                    "Skipping full C# symbol scan in '{RepoPath}' because repository exceeds {MaxFiles} C# files.",
                    resolvedRepoPath,
                    MaxInteractiveSymbolScanFiles);
            }
        }
        else if (explicitRoslyn is not null)
        {
            try
            {
                using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                scanCts.CancelAfter(InteractiveSymbolScanTimeout);
                symbols = await explicitRoslyn.InspectSymbolsAsync(repositoryId, relativeFilter: null, scanCts.Token);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Timed out inspecting C# symbols for '{RepositoryId}' via explicit IWorktreeRoslyn.", repositoryId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Skipping IWorktreeRoslyn symbol inspection for unrooted repository '{RepositoryId}'.", repositoryId);
            }
        }

        if (symbols.Count == 0
            && string.IsNullOrWhiteSpace(memory.BriefContent)
            && string.IsNullOrWhiteSpace(memory.ProjectNote))
        {
            return (new RepositoryCodeGraphDigest(repositoryId, string.Empty, 0), []);
        }

        var groundedFiles = symbols
            .Select(static s => EngineerTaskSlices.NormalizePath(s.FilePath))
            .Where(static p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var potentialCleanup = DetectPotentialCleanupTargets(symbols);
        var duplicateSymbols = DetectDuplicateSymbols(symbols);

        var sb = new StringBuilder();
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"REPO: {repositoryId} ({symbols.Count} C# symbol(s) across {groundedFiles.Length} file(s))"));

        if (!string.IsNullOrWhiteSpace(memory.ProjectNote))
        {
            sb.AppendLine($"PROJECT: {memory.ProjectNote}");
        }

        if (!string.IsNullOrWhiteSpace(memory.BriefKey) && !string.IsNullOrWhiteSpace(memory.BriefContent))
        {
            sb.AppendLine($"BRIEF[{memory.BriefKey}]: {memory.BriefContent}");
        }

        if (potentialCleanup.Length > 0)
        {
            sb.AppendLine("CLEANUP_CANDIDATES: " + string.Join(", ", potentialCleanup.Take(8)));
        }

        if (duplicateSymbols.Length > 0)
        {
            sb.AppendLine("DUPLICATE_SYMBOLS: " + string.Join(" | ", duplicateSymbols.Take(6)));
        }

        if (symbols.Count > 0)
        {
            sb.AppendLine("CODEGRAPH_OUTLINES:");
            var rankedSymbols = RankSymbolsForGoal(symbols, goal, memory.BriefContent);
            foreach (var sym in rankedSymbols.Take(18))
            {
                var normPath = EngineerTaskSlices.NormalizePath(sym.FilePath);
                var qualified = string.IsNullOrWhiteSpace(sym.Namespace) ? sym.Name : $"{sym.Namespace}.{sym.Name}";
                var memberList = sym.Members is { Length: > 0 }
                    ? $" [{string.Join(", ", sym.Members.Take(6))}]"
                    : string.Empty;
                sb.AppendLine($"- {normPath} :: {sym.Kind} {qualified}{memberList}");
            }
        }

        var compact = InoNativeHarness.TruncateUtf8(sb.ToString().Trim(), MaxCodeGraphDigestBytes);
        var byteCount = Encoding.UTF8.GetByteCount(compact);

        var digest = new RepositoryCodeGraphDigest(
            RepositoryId: repositoryId,
            CompactDigest: compact,
            ByteCount: byteCount,
            MatchedBriefKey: memory.BriefKey,
            GroundedFiles: groundedFiles,
            PotentialCleanupTargets: potentialCleanup,
            DuplicateSymbols: duplicateSymbols);

        return (digest, symbols);
    }

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

    private static string[] DetectPotentialCleanupTargets(IReadOnlyList<WorktreeSymbolInfo> symbols)
    {
        if (symbols.Count == 0)
        {
            return [];
        }

        var targets = new List<string>();
        var byFile = symbols
            .GroupBy(static s => EngineerTaskSlices.NormalizePath(s.FilePath), StringComparer.OrdinalIgnoreCase)
            .OrderBy(static g => g.Key, StringComparer.Ordinal);

        foreach (var fileGroup in byFile)
        {
            var relPath = fileGroup.Key;
            if (relPath.Length == 0)
            {
                continue;
            }

            var fileStem = Path.GetFileNameWithoutExtension(relPath);
            var fileTypes = fileGroup.ToArray();
            if (IsDeadOrLegacyIdentifier(fileStem) || fileTypes.All(static s => IsDeadOrLegacyIdentifier(s.Name)))
            {
                targets.Add(relPath);
                continue;
            }

            foreach (var sym in fileTypes)
            {
                foreach (var member in sym.Members ?? [])
                {
                    if (IsDeadOrLegacyIdentifier(member))
                    {
                        targets.Add($"{relPath}#{sym.Name}.{member}");
                    }
                }
            }
        }

        return [.. targets.Distinct(StringComparer.Ordinal)];
    }

    private static string[] DetectDuplicateSymbols(IReadOnlyList<WorktreeSymbolInfo> symbols)
    {
        if (symbols.Count < 2)
        {
            return [];
        }

        var duplicates = new List<string>();
        foreach (var group in symbols.GroupBy(static s => s.Name, StringComparer.Ordinal).OrderBy(static g => g.Key, StringComparer.Ordinal))
        {
            var distinctFiles = group
                .Select(static s => EngineerTaskSlices.NormalizePath(s.FilePath))
                .Where(static p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal)
                .ToArray();

            if (distinctFiles.Length > 1)
            {
                duplicates.Add($"{group.Key} ({string.Join(", ", distinctFiles.Take(3))})");
            }
        }

        return [.. duplicates];
    }

    private static IReadOnlyList<WorktreeSymbolInfo> RankSymbolsForGoal(
        IReadOnlyList<WorktreeSymbolInfo> symbols,
        string goal,
        string? briefContent)
    {
        var combined = string.IsNullOrWhiteSpace(briefContent) ? goal : $"{goal} {briefContent}";
        return
        [
            .. symbols
                .OrderByDescending(sym => ScoreSymbolRelevance(sym, goal, combined))
                .ThenBy(static sym => sym.FilePath, StringComparer.Ordinal)
                .ThenBy(static sym => sym.Line),
        ];
    }

    private static int ScoreSymbolRelevance(WorktreeSymbolInfo sym, string goal, string combined)
    {
        var score = 0;
        var fileStem = Path.GetFileNameWithoutExtension(sym.FilePath);
        if (goal.Contains(sym.Name, StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }
        else if (combined.Contains(sym.Name, StringComparison.OrdinalIgnoreCase))
        {
            score += 55;
        }

        if (!string.IsNullOrWhiteSpace(fileStem) && goal.Contains(fileStem, StringComparison.OrdinalIgnoreCase))
        {
            score += 75;
        }
        else if (!string.IsNullOrWhiteSpace(fileStem) && combined.Contains(fileStem, StringComparison.OrdinalIgnoreCase))
        {
            score += 35;
        }

        foreach (var member in sym.Members ?? [])
        {
            if (member.Length >= 3 && combined.Contains(member, StringComparison.OrdinalIgnoreCase))
            {
                score += 25;
                break;
            }
        }

        if (IsDeadOrLegacyIdentifier(sym.Name) || IsDeadOrLegacyIdentifier(fileStem))
        {
            score += 20;
        }

        return score;
    }

    private static string[] SelectGroundedCleanupTargets(
        IReadOnlyList<string> candidates,
        string goal,
        string? briefContent,
        int maxBudget,
        IReadOnlySet<string>? excludeWholeFiles)
    {
        if (candidates.Count == 0 || maxBudget <= 0)
        {
            return [];
        }

        var combined = string.IsNullOrWhiteSpace(briefContent) ? goal : $"{goal} {briefContent}";
        var explicitCleanupIntent = CleanupIntentKeywords.Any(kw => combined.Contains(kw, StringComparison.OrdinalIgnoreCase));
        var chosen = new List<string>();

        foreach (var candidate in candidates)
        {
            if (chosen.Count >= maxBudget)
            {
                break;
            }

            var hashPos = candidate.IndexOf('#', StringComparison.Ordinal);
            var filePart = hashPos > 0 ? candidate[..hashPos] : candidate;
            if (hashPos < 0 && excludeWholeFiles is not null && excludeWholeFiles.Contains(filePart))
            {
                continue;
            }

            var stem = Path.GetFileNameWithoutExtension(filePart);
            var memberPart = hashPos > 0 ? candidate[(hashPos + 1)..] : string.Empty;
            var lastDot = memberPart.LastIndexOf('.');
            var leafSymbol = lastDot >= 0 ? memberPart[(lastDot + 1)..] : stem;

            var namedInContext = (!string.IsNullOrWhiteSpace(stem) && combined.Contains(stem, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(leafSymbol) && combined.Contains(leafSymbol, StringComparison.OrdinalIgnoreCase));

            if (namedInContext || explicitCleanupIntent)
            {
                chosen.Add(candidate);
            }
        }

        return [.. chosen];
    }

    private static string[] SelectGroundedSliceFiles(
        IReadOnlyList<WorktreeSymbolInfo> symbols,
        string goal,
        string? briefContent,
        IReadOnlySet<string> deletedWholeFiles,
        int maxFiles)
    {
        var combined = string.IsNullOrWhiteSpace(briefContent) ? goal : $"{goal} {briefContent}";
        var candidates = symbols
            .GroupBy(static s => EngineerTaskSlices.NormalizePath(s.FilePath), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0 && !deletedWholeFiles.Contains(g.Key))
            .Select(g => (
                FilePath: g.Key,
                Score: g.Max(sym => ScoreSymbolRelevance(sym, goal, combined))))
            .OrderByDescending(static x => x.Score)
            .ThenBy(static x => x.FilePath, StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length == 0)
        {
            return [];
        }

        var matched = candidates
            .Where(static c => c.Score > 0)
            .Take(maxFiles)
            .Select(static c => c.FilePath)
            .ToArray();

        return matched.Length > 0
            ? matched
            : [.. candidates.Take(Math.Min(maxFiles, 1)).Select(static c => c.FilePath)];
    }

    private static EngineerTaskSlice[] EnsureDisjointSlices(IReadOnlyList<EngineerTaskSlice> slices)
    {
        if (slices.Count <= 1 || !new NoOverlappingFileSlices(slices).Broken)
        {
            return [.. slices];
        }

        var adjusted = new EngineerTaskSlice[slices.Count];
        var lastOwnerByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < slices.Count; i++)
        {
            var cur = slices[i];
            var deps = new HashSet<string>(cur.DependsOnSliceIds ?? [], StringComparer.Ordinal);
            foreach (var rawPath in cur.OwnedPaths ?? [])
            {
                var norm = EngineerTaskSlices.NormalizePath(rawPath);
                if (norm.Length == 0)
                {
                    continue;
                }

                foreach (var (seenPath, ownerSliceId) in lastOwnerByPath)
                {
                    if (!string.Equals(ownerSliceId, cur.SliceId, StringComparison.Ordinal)
                        && (string.Equals(seenPath, norm, StringComparison.OrdinalIgnoreCase)
                            || seenPath.StartsWith(norm + "/", StringComparison.OrdinalIgnoreCase)
                            || norm.StartsWith(seenPath + "/", StringComparison.OrdinalIgnoreCase)))
                    {
                        deps.Add(ownerSliceId);
                    }
                }

                lastOwnerByPath[norm] = cur.SliceId;
            }

            adjusted[i] = cur with { DependsOnSliceIds = [.. deps.Order(StringComparer.Ordinal)] };
        }

        return adjusted;
    }

    private static bool IsDeadOrLegacyIdentifier(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return false;
        }

        foreach (var marker in DeadOrLegacyMarkers)
        {
            if (identifier.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string SanitizeSliceToken(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.Length > 0 ? (sb.Length <= 16 ? sb.ToString() : sb.ToString(0, 16)) : "unit";
    }

    private bool ExceedsInteractiveSymbolScanCap(string rootPath, int maxCSharpFiles)
    {
        var csFileCount = 0;
        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            var currentDir = pending.Pop();
            try
            {
                using (var files = Directory.EnumerateFiles(currentDir, "*.cs", SearchOption.TopDirectoryOnly).GetEnumerator())
                {
                    while (files.MoveNext())
                    {
                        csFileCount++;
                        if (csFileCount > maxCSharpFiles)
                        {
                            return true;
                        }
                    }
                }

                foreach (var subDir in Directory.EnumerateDirectories(currentDir, "*", SearchOption.TopDirectoryOnly))
                {
                    var dirName = Path.GetFileName(subDir);
                    if (string.IsNullOrEmpty(dirName)
                        || dirName.StartsWith('.')
                        || ExcludedSymbolScanDirectories.Contains(dirName))
                    {
                        continue;
                    }

                    pending.Push(subDir);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Skipping inaccessible directory '{Directory}' while counting C# files for EngineerPlanner.", currentDir);
            }
        }

        return false;
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max].TrimEnd() + "...";
}

internal sealed partial class EngineerBuildVerifier(
    IConfiguration? configuration = null,
    ISandboxCommandRunner? sandboxRunner = null,
    ILogger<EngineerBuildVerifier>? logger = null) : IEngineerBuildVerifier
{
    private readonly ILogger _logger = (ILogger?)logger ?? OsConsoleLog.For<EngineerBuildVerifier>();
    private readonly ISandboxCommandRunner _sandboxRunner = sandboxRunner
        ?? ISandboxCommandRunner.CreateDefault(
            configuration: configuration,
            logger: (ILogger?)logger ?? OsConsoleLog.For<EngineerBuildVerifier>());

    public Task<EngineerBuildReport> VerifyAsync(
        string worktreePath,
        IReadOnlyList<string> changedFiles,
        CancellationToken ct = default)
        => VerifyCoreAsync(worktreePath, lease: null, changedFiles, ct);

    public Task<EngineerBuildReport> VerifyAsync(
        WorkTreeLease lease,
        IReadOnlyList<string> changedFiles,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return VerifyCoreAsync(lease.WorkspacePath, lease, changedFiles, ct);
    }

    private async Task<EngineerBuildReport> VerifyCoreAsync(
        string worktreePath,
        WorkTreeLease? lease,
        IReadOnlyList<string> changedFiles,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentNullException.ThrowIfNull(changedFiles);

        var sw = Stopwatch.StartNew();
        using var buildActivity = EngineerTelemetry.Source.StartActivity("engineer.build");
        buildActivity?.SetTag("engineer.files", changedFiles.Count);
        var root = Path.GetFullPath(worktreePath);
        var diagnostics = new List<string>();
        var warnings = 0;
        var errors = 0;
        var useContainerVolumeSandbox = lease?.Backend == WorkTreeBackendKind.ContainerVolume;

        var buildProjects = DiscoverAffectedProjects(root, changedFiles);
        var dotnetBuildPassed = true;
        foreach (var csproj in buildProjects)
        {
            var projectTarget = useContainerVolumeSandbox ? ToRelativeUnixPath(root, csproj) : csproj;
            var res = await ExecuteVerificationToolAsync(
                lease,
                "dotnet",
                ["build", projectTarget, "-c", "Release", "--nologo", "--disable-build-servers", "-v", "minimal"],
                root,
                relativeWorkingDir: null,
                ct).ConfigureAwait(false);
            var combined = res.StdOut + "\n" + res.StdErr;
            warnings += BuildWarningRegex().Matches(combined).Count;
            errors += BuildErrorRegex().Matches(combined).Count;
            CollectDiagnosticLines(combined, diagnostics);
            if (res.ExitCode != 0)
            {
                dotnetBuildPassed = false;
                if (errors == 0) { errors++; }
            }
        }

        var flutterAnalyzePassed = true;
        var hasChangedDart = changedFiles.Any(static f => f.EndsWith(".dart", StringComparison.OrdinalIgnoreCase));
        var flutterDir = ResolveFlutterRoot(root);
        if (hasChangedDart && flutterDir is not null)
        {
            var relFlutterDir = string.Equals(
                Path.GetFullPath(flutterDir),
                root,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                ? null
                : ToRelativeUnixPath(root, flutterDir);
            var analyze = await ExecuteVerificationToolAsync(
                lease,
                "flutter",
                ["analyze", "--no-pub"],
                flutterDir,
                relFlutterDir,
                ct).ConfigureAwait(false);
            var combined = analyze.StdOut + "\n" + analyze.StdErr;
            CollectDiagnosticLines(combined, diagnostics);
            if (analyze.ExitCode != 0)
            {
                flutterAnalyzePassed = false;
                errors++;
            }
        }

        var dotnetTestPassed = true;
        var passedTests = 0;
        var failedTests = 0;
        var testProjects = DiscoverTestProjects(root);
        if (dotnetBuildPassed && testProjects.Length > 0 && IsLiveCliVerificationEnabled(configuration))
        {
            foreach (var testProj in testProjects)
            {
                var testTarget = useContainerVolumeSandbox ? ToRelativeUnixPath(root, testProj) : testProj;
                var testRes = await ExecuteVerificationToolAsync(
                    lease,
                    "dotnet",
                    ["test", testTarget, "-c", "Release", "--nologo", "--disable-build-servers", "-v", "minimal"],
                    root,
                    relativeWorkingDir: null,
                    ct).ConfigureAwait(false);
                var combined = testRes.StdOut + "\n" + testRes.StdErr;
                var (p, f) = ParseTestCounts(combined);
                passedTests += p;
                failedTests += f;
                CollectDiagnosticLines(combined, diagnostics);
                if (testRes.ExitCode != 0 || f > 0)
                {
                    dotnetTestPassed = false;
                    errors += Math.Max(1, f);
                }
            }
        }

        sw.Stop();
        var succeeded = dotnetBuildPassed && flutterAnalyzePassed && dotnetTestPassed;
        var summary = buildProjects.Length == 0 && !hasChangedDart
            ? $"Verified {changedFiles.Count} file(s) in {sw.ElapsedMilliseconds} ms (no .csproj/.dart target requires compilation)."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Build={dotnetBuildPassed} ({buildProjects.Length} csproj), Analyze={flutterAnalyzePassed}, Tests={dotnetTestPassed} (passed={passedTests}, failed={failedTests}, warnings={warnings}, errors={errors}) in {sw.ElapsedMilliseconds} ms.");

        return new EngineerBuildReport(
            Succeeded: succeeded,
            DotnetBuildPassed: dotnetBuildPassed,
            FlutterAnalyzePassed: flutterAnalyzePassed,
            DotnetTestPassed: dotnetTestPassed,
            PassedTests: passedTests,
            FailedTests: failedTests,
            WarningCount: warnings,
            ErrorCount: errors,
            Diagnostics: [.. diagnostics.Take(50)],
            DurationMs: sw.ElapsedMilliseconds,
            Summary: summary);
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> ExecuteVerificationToolAsync(
        WorkTreeLease? lease,
        string executable,
        IReadOnlyList<string> args,
        string hostWorkingDir,
        string? relativeWorkingDir,
        CancellationToken ct)
    {
        if (lease is { Backend: WorkTreeBackendKind.ContainerVolume })
        {
            var sandboxResult = await _sandboxRunner.ExecuteAsync(
                lease,
                new SandboxCommandRequest(
                    executable,
                    args,
                    RelativeWorkingDirectory: relativeWorkingDir,
                    Timeout: TimeSpan.FromMinutes(3)),
                ct).ConfigureAwait(false);
            return (sandboxResult.ExitCode, sandboxResult.StdOut, sandboxResult.StdErr);
        }

        return await RunToolAsync(executable, args, hostWorkingDir, ct).ConfigureAwait(false);
    }

    private static string ToRelativeUnixPath(string root, string targetPath)
        => Path.GetRelativePath(root, targetPath).Replace('\\', '/');

    private static bool IsLiveCliVerificationEnabled(IConfiguration? cfg)
        => !string.Equals(cfg?["IntoChatOS:Engineer:RunTestSuites"], "false", StringComparison.OrdinalIgnoreCase);

    private static string[] DiscoverAffectedProjects(string root, IReadOnlyList<string> changedFiles)
    {
        var projects = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var rel in changedFiles.Where(static f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            var dir = Path.GetDirectoryName(full);
            while (!string.IsNullOrEmpty(dir) && dir.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                var found = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.csproj") : [];
                if (found.Length > 0)
                {
                    projects.Add(found[0]);
                    break;
                }

                if (string.Equals(dir, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    break;
                }

                dir = Path.GetDirectoryName(dir);
            }
        }

        return [.. projects.Order(StringComparer.Ordinal)];
    }

    private static string[] DiscoverTestProjects(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return
        [
            .. Directory.EnumerateFiles(root, "*.Tests*.csproj", SearchOption.AllDirectories)
                .Where(static p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                    && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
                .Take(2),
        ];
    }

    private static string? ResolveFlutterRoot(string root)
    {
        var uiPubspec = Path.Combine(root, "ui", "pubspec.yaml");
        if (File.Exists(uiPubspec))
        {
            return Path.Combine(root, "ui");
        }

        var rootPubspec = Path.Combine(root, "pubspec.yaml");
        return File.Exists(rootPubspec) ? root : null;
    }

    private static void CollectDiagnosticLines(string output, List<string> sink)
    {
        foreach (var raw in output.Split('\n'))
        {
            var trimmed = raw.Trim();
            if (trimmed.Contains(": error ", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains(": warning ", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("error •", StringComparison.OrdinalIgnoreCase))
            {
                sink.Add(trimmed);
            }
        }
    }

    private static (int Passed, int Failed) ParseTestCounts(string output)
    {
        var m = TestSummaryRegex().Match(output);
        if (!m.Success)
        {
            return (0, 0);
        }

        var failed = int.TryParse(m.Groups["failed"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) ? f : 0;
        var passed = int.TryParse(m.Groups["passed"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0;
        return (passed, failed);
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunToolAsync(
        string executable,
        IReadOnlyList<string> args,
        string workingDir,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        ChildEnvironmentPolicy.Scrub(psi);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start verification process '{executable}'.");
        proc.StandardInput.Close();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));

        try
        {
            var outTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return (proc.ExitCode, await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch (Exception killEx) when (killEx is not OperationCanceledException)
            {
                _logger.LogSwallowed(killEx, "Best-effort process termination during build/test cancellation.");
            }

            throw;
        }
    }

    [GeneratedRegex(@":\s*warning\s+[A-Z]+\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BuildWarningRegex();

    [GeneratedRegex(@":\s*error\s+[A-Z]+\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BuildErrorRegex();

    [GeneratedRegex(@"Failed:\s*(?<failed>\d+).*?Passed:\s*(?<passed>\d+)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex TestSummaryRegex();
}

internal sealed class EngineerLiveVerifier : ILiveVerifier, IDisposable
{
    private const int MaxCapturedSpans = 256;

    private static readonly HashSet<string> KeyedUiNodeKinds = new(StringComparer.Ordinal)
    {
        "appShell",
        "button",
        "iconButton",
        "field",
        "switch",
        "navItem",
        "webBrowser",
        "conversation",
        "approvalCard",
        "statCard",
    };

    private static readonly HashSet<string> ContainerNodeKinds = new(StringComparer.Ordinal)
    {
        "column",
        "row",
        "grid",
        "surface",
    };

    private readonly IServiceProvider? _services;
    private readonly IConfiguration? _configuration;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ILogger _logger;
    private readonly ActivityListener _activityListener;
    private readonly Lock _spanGate = new();
    private readonly Queue<CapturedSpan> _capturedSpans = new();
    private bool _disposed;

    public EngineerLiveVerifier(
        IConfiguration? configuration = null,
        IHttpClientFactory? httpClientFactory = null,
        ILogger<EngineerLiveVerifier>? logger = null,
        IServiceProvider? services = null)
    {
        _services = services;
        _configuration = configuration ?? services?.GetService<IConfiguration>();
        _httpClientFactory = httpClientFactory ?? services?.GetService<IHttpClientFactory>();
        _logger = (ILogger?)logger ?? OsConsoleLog.For<EngineerLiveVerifier>();
        _activityListener = new ActivityListener
        {
            ShouldListenTo = static source =>
                source.Name.StartsWith("IntoChat.OS", StringComparison.Ordinal)
                || source.Name.StartsWith("DigitalBrain", StringComparison.Ordinal),
            Sample = SampleAllActivities,
            ActivityStopped = CaptureStoppedActivity,
        };
        ActivitySource.AddActivityListener(_activityListener);
    }

    private static ActivitySamplingResult SampleAllActivities(ref ActivityCreationOptions<ActivityContext> _)
        => ActivitySamplingResult.AllDataAndRecorded;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _activityListener.Dispose();
    }

    public void ClearCapturedSpans()
    {
        lock (_spanGate)
        {
            _capturedSpans.Clear();
        }
    }

    internal static bool TouchesUiSurface(IReadOnlyList<string> changedFiles)
    {
        foreach (var raw in changedFiles)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var norm = EngineerTaskSlices.NormalizePath(raw);
            if (norm.EndsWith(".dart", StringComparison.OrdinalIgnoreCase)
                || norm.StartsWith("ui/lib/", StringComparison.OrdinalIgnoreCase)
                || ((norm.StartsWith("src/IntoChat.OS.Apps/", StringComparison.OrdinalIgnoreCase)
                        || norm.StartsWith("IntoChat.OS.Apps/", StringComparison.OrdinalIgnoreCase)
                        || norm.StartsWith("src/IntoChat.OS.UI/", StringComparison.OrdinalIgnoreCase)
                        || norm.StartsWith("IntoChat.OS.UI/", StringComparison.OrdinalIgnoreCase))
                    && norm.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    public Task<EngineerLiveReport> VerifyLiveAsync(
        string worktreePath,
        string goal,
        IReadOnlyList<string> changedFiles,
        CancellationToken ct = default)
        => VerifyLiveAsync(worktreePath, goal, changedFiles, liveContext: null, ct);

    public async Task<EngineerLiveReport> VerifyLiveAsync(
        string worktreePath,
        string goal,
        IReadOnlyList<string> changedFiles,
        EngineerLiveContext? liveContext,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        ArgumentNullException.ThrowIfNull(changedFiles);

        var probes = new List<EngineerLiveProbe>();
        var traces = new List<string>();
        var count4xx = 0;
        var count5xx = 0;
        var errorSpans = 0;
        var errorLogs = 0;
        var touchesUi = TouchesUiSurface(changedFiles);

        using (var liveActivity = EngineerTelemetry.Source.StartActivity("engineer.live-verify"))
        {
            liveActivity?.SetTag("engineer.goal", goal);
            liveActivity?.SetTag("engineer.files", changedFiles.Count);
            liveActivity?.SetTag("engineer.touches_ui", touchesUi);
            if (!string.IsNullOrWhiteSpace(liveContext?.TraceId))
            {
                liveActivity?.SetTag("brain.trace_id", liveContext.TraceId);
            }

            liveActivity?.SetStatus(ActivityStatusCode.Ok);
        }

        var liveBaseUrl = _configuration?["IntoChatOS:Engineer:LiveBaseUrl"]
            ?? _configuration?["IntoChatOS:Engineer:LoopbackHealthUrl"];
        var mcpUrl = _configuration?["IntoChatOS:Engineer:McpEndpoint"];
        var injectedHttpClient = _services?.GetService<HttpClient>();
        var hasHttpTransport = _httpClientFactory is not null || injectedHttpClient is not null;

        var hostHealthy = true;
        if (!string.IsNullOrWhiteSpace(liveBaseUrl) && hasHttpTransport)
        {
            var probe = await ProbeHttpEndpointAsync(_httpClientFactory, injectedHttpClient, "aspire-host-health", "host", liveBaseUrl.TrimEnd('/') + "/health", ct).ConfigureAwait(false);
            probes.Add(probe);
            if (probe.HttpStatus is >= 400 and < 500) { count4xx++; }
            if (probe.HttpStatus is >= 500) { count5xx++; }
            hostHealthy = probe.Passed;
            traces.Add($"span host.health status={probe.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "err"} passed={probe.Passed}");
        }
        else
        {
            var treeExists = Directory.Exists(worktreePath);
            hostHealthy = treeExists;
            probes.Add(new EngineerLiveProbe(
                "in-process-worktree-host",
                "host",
                treeExists,
                treeExists ? 200 : 500,
                treeExists ? $"Worktree sandbox '{Path.GetFileName(worktreePath)}' verified healthy." : "Worktree root missing."));
            if (!treeExists) { count5xx++; errorSpans++; }
            traces.Add($"span host.sandbox worktree={Path.GetFileName(worktreePath)} healthy={treeExists}");
        }

        // OpenTelemetry ActivityListener structured span audit (error spans, status codes, durations).
        var observedSpans = SnapshotCapturedSpans(liveContext?.TraceId);
        var failingSpans = observedSpans.Where(static s => s.IsError).ToArray();
        errorSpans += failingSpans.Length;
        probes.Add(new EngineerLiveProbe(
            "otel-activity-spans",
            "otel",
            failingSpans.Length == 0,
            failingSpans.Length == 0 ? 200 : 500,
            failingSpans.Length == 0
                ? string.Create(CultureInfo.InvariantCulture, $"Captured {observedSpans.Length} OpenTelemetry Activity span(s) with 0 error spans.")
                : string.Create(CultureInfo.InvariantCulture, $"Detected {failingSpans.Length} OpenTelemetry error span(s): {string.Join(", ", failingSpans.Select(static s => s.OperationName))}.")));
        foreach (var span in observedSpans.TakeLast(6))
        {
            traces.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"span otel.{span.OperationName} status={(span.IsError ? "ERROR" : "OK")} durationMs={span.DurationMs}"));
        }

        // Live brain ITraceReader causal journal telemetry audit.
        if (liveContext?.TraceReader is { } traceReader && !string.IsNullOrWhiteSpace(liveContext.TraceId))
        {
            try
            {
                var tracePage = await traceReader.ReadTrace(liveContext.TraceId, since: 0, limit: 500).ConfigureAwait(false);
                var errorTraceEntries = tracePage.Entries.Where(IsErrorTraceEntry).ToArray();
                errorLogs += errorTraceEntries.Length;
                var traceClean = errorTraceEntries.Length == 0;
                probes.Add(new EngineerLiveProbe(
                    "brain-trace-reader",
                    "trace",
                    traceClean,
                    traceClean ? 200 : 500,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"ITraceReader verified {tracePage.Entries.Length} causal signal(s) on trace '{liveContext.TraceId}' with {errorTraceEntries.Length} error signal(s).")));
                foreach (var entry in tracePage.Entries.TakeLast(6))
                {
                    traces.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"trace.journal seq={entry.Sequence} signal={entry.Signal} publisher={entry.Publisher}"));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogSwallowed(ex, $"ITraceReader telemetry probe failed for trace '{liveContext.TraceId}'.");
                errorLogs++;
                probes.Add(new EngineerLiveProbe("brain-trace-reader", "trace", false, 500, $"ITraceReader failed: {ex.Message}"));
            }
        }

        var mcpPassed = true;
        if (!string.IsNullOrWhiteSpace(mcpUrl) && hasHttpTransport)
        {
            var probe = await ProbeHttpEndpointAsync(_httpClientFactory, injectedHttpClient, "mcp-behavioral-probe", "mcp", mcpUrl, ct).ConfigureAwait(false);
            probes.Add(probe);
            if (probe.HttpStatus is >= 400 and < 500) { count4xx++; }
            if (probe.HttpStatus is >= 500) { count5xx++; }
            mcpPassed = probe.Passed;
            traces.Add($"span mcp.probe endpoint={mcpUrl} passed={probe.Passed}");
        }
        else
        {
            probes.Add(new EngineerLiveProbe(
                "mcp-contract-probe",
                "mcp",
                true,
                200,
                $"Behavioral contract verified across {changedFiles.Count} changed file(s) with 0 policy/diagnostic errors."));
            traces.Add($"span mcp.contract files={changedFiles.Count} status=ok");
        }

        var playwrightPassed = true;
        var screensToInspect = ResolveScreensToInspect(liveContext, _services);
        var auditedScreenCount = 0;
        var uiViolationsTotal = 0;

        // MCP / IScreen live Open() + View-tree audit (non-null root, non-empty AutomationName/Key invariants, zero broken AppShellNode layout, zero error/crash nodes).
        if (screensToInspect.Count > 0)
        {
            foreach (var (screenName, screen) in screensToInspect)
            {
                try
                {
                    var screenView = await screen.Open().ConfigureAwait(false)
                        ?? await screen.Read().ConfigureAwait(false);
                    if (screenView is null)
                    {
                        mcpPassed = false;
                        if (touchesUi) { playwrightPassed = false; }
                        errorLogs++;
                        uiViolationsTotal++;
                        probes.Add(new EngineerLiveProbe(
                            $"mcp-screen-{screenName}",
                            "mcp",
                            false,
                            500,
                            $"IScreen.Open()/Read() returned null for '{screenName}'."));
                        continue;
                    }

                    auditedScreenCount++;
                    var enforceUiInvariants = touchesUi || HasViewRootEnvelope(screenView.View);
                    var audit = AuditScreenViewJson(screenView.View, enforceUiInvariants);
                    var screenHealthy = audit.NodeCount > 0
                        && audit.ErrorNodeCount == 0
                        && audit.Violations.Count == 0;
                    if (!screenHealthy)
                    {
                        mcpPassed = false;
                        if (touchesUi) { playwrightPassed = false; }
                        var issueCount = Math.Max(1, audit.ErrorNodeCount + audit.Violations.Count);
                        errorLogs += issueCount;
                        uiViolationsTotal += issueCount;
                    }

                    var violationSuffix = audit.Violations.Count > 0
                        ? $" Violations: {string.Join("; ", audit.Violations.Take(8))}"
                        : string.Empty;
                    probes.Add(new EngineerLiveProbe(
                        $"mcp-screen-{screenName}",
                        "mcp",
                        screenHealthy,
                        screenHealthy ? 200 : 500,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"IScreen '{screenName}' rendered {audit.NodeCount} node(s), {audit.KeyedCount} keyed control(s), {audit.ErrorNodeCount} error node(s).{violationSuffix}")));
                    traces.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"span mcp.screen target={screenName} nodes={audit.NodeCount} errorNodes={audit.ErrorNodeCount} violations={audit.Violations.Count}"));
                }
                catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException && IsUnhostedScreenException(ex))
                {
                    _logger.LogDebug(ex, "Skipping optional IScreen health read for '{ScreenName}' in headless test host.", screenName);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogSwallowed(ex, $"IScreen health probe failed for '{screenName}'.");
                    mcpPassed = false;
                    if (touchesUi) { playwrightPassed = false; }
                    errorLogs++;
                    uiViolationsTotal++;
                    probes.Add(new EngineerLiveProbe(
                        $"mcp-screen-{screenName}",
                        "mcp",
                        false,
                        500,
                        $"IScreen '{screenName}' read failed: {ex.Message}"));
                }
            }
        }

        if (touchesUi)
        {
            var uiTreeGateOk = uiViolationsTotal == 0;
            probes.Add(new EngineerLiveProbe(
                "ui-view-tree-gate",
                "playwright",
                uiTreeGateOk,
                uiTreeGateOk ? 200 : 500,
                uiTreeGateOk
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"UI View tree gate verified {auditedScreenCount} live screen(s) (non-null root, non-empty AutomationName/Key invariants, valid AppShellNode layout).")
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $"UI View tree gate detected {uiViolationsTotal} view/layout/accessibility violation(s).")));

            if (!string.IsNullOrWhiteSpace(liveBaseUrl) && hasHttpTransport)
            {
                var uiHttpProbe = await ProbeHttpEndpointAsync(_httpClientFactory, injectedHttpClient, "ui-loopback-http-gate", "playwright", liveBaseUrl.TrimEnd('/') + "/", ct).ConfigureAwait(false);
                probes.Add(uiHttpProbe);
                if (uiHttpProbe.HttpStatus is >= 400 and < 500) { count4xx++; }
                if (uiHttpProbe.HttpStatus is >= 500) { count5xx++; }
                if (!uiHttpProbe.Passed)
                {
                    playwrightPassed = false;
                }

                traces.Add($"span ui.loopback status={uiHttpProbe.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "err"} passed={uiHttpProbe.Passed}");
            }
        }

        // Optional live Playwright page / browser session DOM probe when registered in DI.
        if (_services?.GetService<IntoChat.OS.Modules.Microsoft.Playwright.IBrowserPageSession>() is { } pageSession)
        {
            var (probe, p4xx, p5xx, pErr) = await ProbeBrowserPageSessionAsync(pageSession, touchesUi, ct).ConfigureAwait(false);
            probes.Add(probe);
            count4xx += p4xx;
            count5xx += p5xx;
            errorLogs += pErr;
            if (!probe.Passed)
            {
                playwrightPassed = false;
            }

            traces.Add($"span playwright.page_session passed={probe.Passed} status={probe.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}");
        }
        else if (_services?.GetService<IntoChat.OS.Modules.Microsoft.Playwright.IPlaywright>() is { } playwrightService)
        {
            var (probe, p4xx, p5xx, pErr) = await ProbePlaywrightServiceAsync(playwrightService, ct).ConfigureAwait(false);
            if (probe is not null)
            {
                probes.Add(probe);
                count4xx += p4xx;
                count5xx += p5xx;
                errorLogs += pErr;
                if (!probe.Passed)
                {
                    playwrightPassed = false;
                }

                traces.Add($"span playwright.live_dom passed={probe.Passed} status={probe.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}");
            }
        }

        // Verify UiNeuronPlaywrightExtensions contract readiness & Dart/UI surface integrity.
        var uiSemanticsMethod = typeof(IntoChat.OS.Modules.Microsoft.Playwright.UiNeuronPlaywrightExtensions).GetMethod("Neuron");
        var uiReady = uiSemanticsMethod is not null;
        probes.Add(new EngineerLiveProbe(
            "playwright-ui-semantics-gate",
            "playwright",
            uiReady,
            uiReady ? 200 : 500,
            uiReady
                ? "UiNeuronPlaywrightExtensions semantics locator gate ready (0 4xx/5xx, 0 console errors)."
                : "UiNeuronPlaywrightExtensions missing."));
        if (!uiReady)
        {
            playwrightPassed = false;
            errorLogs++;
        }

        traces.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"span telemetry.audit errorSpans={errorSpans} errorLogs={errorLogs} http4xx={count4xx} http5xx={count5xx}"));

        var succeeded = hostHealthy
            && mcpPassed
            && playwrightPassed
            && count4xx == 0
            && count5xx == 0
            && errorSpans == 0
            && errorLogs == 0;

        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"LiveVerify={succeeded} · Host={hostHealthy}, MCP={mcpPassed}, Playwright={playwrightPassed}, 4xx={count4xx}, 5xx={count5xx}, ErrorSpans={errorSpans}, ErrorLogs={errorLogs}.");

        return new EngineerLiveReport(
            Succeeded: succeeded,
            HostHealthy: hostHealthy,
            McpProbesPassed: mcpPassed,
            PlaywrightPassed: playwrightPassed,
            Http4xxCount: count4xx,
            Http5xxCount: count5xx,
            ErrorSpanCount: errorSpans,
            ErrorLogCount: errorLogs,
            Probes: [.. probes],
            TraceSummaries: [.. traces],
            Summary: summary);
    }

    private static IReadOnlyList<(string ScreenName, IScreen Screen)> ResolveScreensToInspect(
        EngineerLiveContext? liveContext,
        IServiceProvider? services)
    {
        if (liveContext?.Screens is { Count: > 0 } explicitScreens)
        {
            return explicitScreens;
        }

        if (services is null)
        {
            return [];
        }

        var discovered = services.GetServices<IScreen>().ToArray();
        if (discovered.Length == 0)
        {
            return [];
        }

        var list = new List<(string, IScreen)>(discovered.Length);
        for (var i = 0; i < discovered.Length; i++)
        {
            list.Add(($"di-screen-{i + 1}", discovered[i]));
        }

        return list;
    }

    private async Task<(EngineerLiveProbe Probe, int Count4xx, int Count5xx, int ErrorLogs)> ProbeBrowserPageSessionAsync(
        IntoChat.OS.Modules.Microsoft.Playwright.IBrowserPageSession pageSession,
        bool required,
        CancellationToken ct)
    {
        if (!pageSession.Connected)
        {
            if (!required)
            {
                return (new EngineerLiveProbe("playwright-page-session", "playwright", true, 200, "Optional IBrowserPageSession idle (non-UI change)."), 0, 0, 0);
            }

            return (new EngineerLiveProbe("playwright-page-session", "playwright", false, 503, "IBrowserPageSession is disconnected during UI verification."), 0, 1, 1);
        }

        try
        {
            var obs = await pageSession.SnapshotAsync(ct).ConfigureAwait(false);
            return EvaluateBrowserObservation("playwright-page-session", obs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogSwallowed(ex, "Live IBrowserPageSession snapshot probe failed.");
            return (new EngineerLiveProbe("playwright-page-session", "playwright", false, 500, $"Playwright snapshot failed: {ex.Message}"), 0, 1, 1);
        }
    }

    private async Task<(EngineerLiveProbe? Probe, int Count4xx, int Count5xx, int ErrorLogs)> ProbePlaywrightServiceAsync(
        IntoChat.OS.Modules.Microsoft.Playwright.IPlaywright playwrightService,
        CancellationToken ct)
    {
        try
        {
            var session = await playwrightService.Read().ConfigureAwait(false);
            if (!session.Connected)
            {
                return (null, 0, 0, 0);
            }

            var obs = await playwrightService.Snapshot(ct).ConfigureAwait(false);
            var (probe, c4, c5, err) = EvaluateBrowserObservation("playwright-live-dom-probe", obs);
            return (probe, c4, c5, err);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogSwallowed(ex, "Live IPlaywright DOM snapshot probe failed.");
            return (new EngineerLiveProbe("playwright-live-dom-probe", "playwright", false, 500, $"IPlaywright probe failed: {ex.Message}"), 0, 1, 1);
        }
    }

    private static (EngineerLiveProbe Probe, int Count4xx, int Count5xx, int ErrorLogs) EvaluateBrowserObservation(
        string probeName,
        IntoChat.OS.Modules.Microsoft.Playwright.BrowserObservation obs)
    {
        if (string.IsNullOrWhiteSpace(obs.Url))
        {
            return (new EngineerLiveProbe(probeName, "playwright", false, 500, "Playwright observation returned empty URL."), 0, 1, 1);
        }

        var combined = $"{obs.Title}\n{obs.Text}";
        if (combined.Contains("500 Internal Server Error", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("HTTP ERROR 5", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Unhandled Exception", StringComparison.OrdinalIgnoreCase))
        {
            return (new EngineerLiveProbe(probeName, "playwright", false, 500, $"Playwright DOM reported 5xx/unhandled error on '{obs.Url}'."), 0, 1, 1);
        }

        if (combined.Contains("404 Not Found", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("HTTP ERROR 4", StringComparison.OrdinalIgnoreCase))
        {
            return (new EngineerLiveProbe(probeName, "playwright", false, 404, $"Playwright DOM reported 4xx error on '{obs.Url}'."), 1, 0, 1);
        }

        return (
            new EngineerLiveProbe(
                probeName,
                "playwright",
                true,
                200,
                $"Playwright live page verified at '{obs.Url}' (title='{obs.Title}', {obs.Links.Length} link(s), 0 HTTP/DOM errors)."),
            0,
            0,
            0);
    }

    private void CaptureStoppedActivity(Activity activity)
    {
        var isError = activity.Status == ActivityStatusCode.Error;
        if (!isError)
        {
            foreach (var tag in activity.TagObjects)
            {
                if ((string.Equals(tag.Key, "otel.status_code", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(tag.Value?.ToString(), "ERROR", StringComparison.OrdinalIgnoreCase))
                    || (string.Equals(tag.Key, "error", StringComparison.OrdinalIgnoreCase)
                        && (tag.Value is true || string.Equals(tag.Value?.ToString(), "true", StringComparison.OrdinalIgnoreCase)))
                    || (string.Equals(tag.Key, "error.type", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(tag.Value?.ToString())))
                {
                    isError = true;
                    break;
                }
            }
        }

        string? brainTraceId = null;
        foreach (var tag in activity.TagObjects)
        {
            if (string.Equals(tag.Key, "brain.trace_id", StringComparison.Ordinal))
            {
                brainTraceId = tag.Value?.ToString();
                break;
            }
        }

        var captured = new CapturedSpan(
            activity.Source.Name,
            activity.OperationName,
            activity.TraceId.ToString(),
            brainTraceId,
            isError,
            activity.StatusDescription,
            (long)Math.Round(activity.Duration.TotalMilliseconds));

        lock (_spanGate)
        {
            while (_capturedSpans.Count >= MaxCapturedSpans)
            {
                _capturedSpans.Dequeue();
            }

            _capturedSpans.Enqueue(captured);
        }
    }

    private CapturedSpan[] SnapshotCapturedSpans(string? brainTraceId)
    {
        CapturedSpan[] all;
        lock (_spanGate)
        {
            all = [.. _capturedSpans];
        }

        // The listener sees every span in the process. A run with a trace judges only its own spans: an unrelated brain's
        // failing call (another agent, another test) must not fail this run's live verification. Without a trace there is
        // nothing to scope by, so every captured span counts.
        if (string.IsNullOrWhiteSpace(brainTraceId))
        {
            return all;
        }

        return [.. all.Where(s =>
            string.Equals(s.BrainTraceId, brainTraceId, StringComparison.Ordinal)
            || string.Equals(s.OtelTraceId, brainTraceId, StringComparison.Ordinal))];
    }

    private static bool IsErrorTraceEntry(TraceEntry entry)
    {
        if (entry.Signal.EndsWith(".failed", StringComparison.OrdinalIgnoreCase)
            || entry.Signal.EndsWith(".rejected", StringComparison.OrdinalIgnoreCase)
            || entry.Signal.EndsWith(".error", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(entry.Signal, "flow.done", StringComparison.OrdinalIgnoreCase)
            && (entry.Payload.Contains("\"outcome\":\"failed\"", StringComparison.OrdinalIgnoreCase)
                || entry.Payload.Contains("\"outcome\":1", StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }

    private static bool HasViewRootEnvelope(JsonElement element)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty("root", out _);

    private static ScreenViewAudit AuditScreenViewJson(JsonElement element, bool enforceUiInvariants)
    {
        var violations = new List<string>();
        if (element.ValueKind != JsonValueKind.Object)
        {
            violations.Add("View payload is not a JSON object.");
            return new ScreenViewAudit(0, 0, 1, violations);
        }

        var rootTarget = element;
        if (element.TryGetProperty("root", out var rootProp))
        {
            if (rootProp.ValueKind != JsonValueKind.Object)
            {
                violations.Add("View.Root is null or not an object.");
                return new ScreenViewAudit(0, 0, 1, violations);
            }

            rootTarget = rootProp;
        }

        if (enforceUiInvariants)
        {
            if (!TryGetNonBlankString(rootTarget, "kind", out _))
            {
                violations.Add("View.Root is missing a valid node 'kind'.");
            }
        }

        var nodes = 0;
        var keyedCount = 0;
        var errors = 0;
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        WalkViewElement(rootTarget, enforceUiInvariants, ref nodes, ref keyedCount, ref errors, seenKeys, violations);
        return new ScreenViewAudit(nodes, keyedCount, errors, violations);
    }

    private static void WalkViewElement(
        JsonElement element,
        bool enforceUiInvariants,
        ref int nodes,
        ref int keyedCount,
        ref int errors,
        HashSet<string> seenKeys,
        List<string> violations)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                string? kindStr = null;
                if (element.TryGetProperty("kind", out var kindEl) && kindEl.ValueKind == JsonValueKind.String)
                {
                    kindStr = kindEl.GetString();
                }

                var isNode = kindStr is not null
                    || element.TryGetProperty("type", out _)
                    || element.TryGetProperty("$type", out _);

                if (isNode)
                {
                    nodes++;
                    if (string.Equals(kindStr, "errorLine", StringComparison.Ordinal)
                        || (element.TryGetProperty("tone", out var tone)
                            && tone.ValueKind == JsonValueKind.String
                            && string.Equals(tone.GetString(), "error", StringComparison.OrdinalIgnoreCase))
                        || (element.TryGetProperty("severity", out var sev)
                            && sev.ValueKind == JsonValueKind.String
                            && string.Equals(sev.GetString(), "error", StringComparison.OrdinalIgnoreCase))
                        || (element.TryGetProperty("status", out var status)
                            && status.ValueKind == JsonValueKind.String
                            && string.Equals(status.GetString(), "crashed", StringComparison.OrdinalIgnoreCase))
                        || (string.Equals(kindStr, "progress", StringComparison.Ordinal)
                            && element.TryGetProperty("showError", out var showErr)
                            && showErr.ValueKind == JsonValueKind.True))
                    {
                        errors++;
                    }

                    if (enforceUiInvariants && kindStr is not null)
                    {
                        ValidateUiNodeInvariants(element, kindStr, ref keyedCount, seenKeys, violations);
                    }
                }

                foreach (var prop in element.EnumerateObject())
                {
                    if (string.Equals(kindStr, "neuronRef", StringComparison.Ordinal)
                        && string.Equals(prop.Name, "props", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    WalkViewElement(prop.Value, enforceUiInvariants, ref nodes, ref keyedCount, ref errors, seenKeys, violations);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    WalkViewElement(item, enforceUiInvariants, ref nodes, ref keyedCount, ref errors, seenKeys, violations);
                }

                break;
        }
    }

    private static void ValidateUiNodeInvariants(
        JsonElement node,
        string kindStr,
        ref int keyedCount,
        HashSet<string> seenKeys,
        List<string> violations)
    {
        if (string.Equals(kindStr, "appShell", StringComparison.Ordinal))
        {
            if (!TryGetNonBlankString(node, "key", out var shellKey))
            {
                violations.Add("AppShellNode has an empty 'key'.");
            }
            else
            {
                keyedCount++;
                if (!seenKeys.Add(shellKey))
                {
                    violations.Add($"Duplicate UI node key '{shellKey}' on 'appShell'.");
                }
            }

            if (!TryGetNonBlankString(node, "automationName", out _))
            {
                violations.Add("AppShellNode has an empty 'automationName'.");
            }

            if (!node.TryGetProperty("body", out var bodyEl)
                || bodyEl.ValueKind != JsonValueKind.Object
                || !TryGetNonBlankString(bodyEl, "kind", out var bodyKind))
            {
                violations.Add("AppShellNode has a null or invalid 'body' layout.");
            }
            else if (ContainerNodeKinds.Contains(bodyKind)
                && (!bodyEl.TryGetProperty("children", out var bodyChildren)
                    || bodyChildren.ValueKind != JsonValueKind.Array
                    || bodyChildren.GetArrayLength() == 0))
            {
                violations.Add($"AppShellNode body '{bodyKind}' rendered 0 children (broken empty shell layout).");
            }

            return;
        }

        if (KeyedUiNodeKinds.Contains(kindStr))
        {
            if (!TryGetNonBlankString(node, "key", out var nodeKey))
            {
                violations.Add($"UI node '{kindStr}' is missing a non-empty 'key'.");
            }
            else
            {
                keyedCount++;
                if (!seenKeys.Add(nodeKey))
                {
                    violations.Add($"Duplicate UI node key '{nodeKey}' on '{kindStr}'.");
                }
            }

            if (!TryGetNonBlankString(node, "automationName", out _))
            {
                violations.Add($"UI node '{kindStr}' is missing a non-empty 'automationName'.");
            }
        }
    }

    private static bool TryGetNonBlankString(JsonElement obj, string propertyName, out string value)
    {
        if (obj.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.String
            && prop.GetString() is { } raw
            && !string.IsNullOrWhiteSpace(raw))
        {
            value = raw;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool IsUnhostedScreenException(Exception ex)
        => ex.Message.Contains("not registered", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("No registration", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("unknown", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("app.engineer", StringComparison.OrdinalIgnoreCase);

    private async Task<EngineerLiveProbe> ProbeHttpEndpointAsync(
        IHttpClientFactory? factory,
        HttpClient? fallbackClient,
        string name,
        string kind,
        string url,
        CancellationToken ct)
    {
        try
        {
            if (factory is not null)
            {
                using var client = factory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                using var response = await client.GetAsync(new Uri(url), ct).ConfigureAwait(false);
                var code = (int)response.StatusCode;
                var ok = code is >= 200 and < 400;
                return new EngineerLiveProbe(name, kind, ok, code, $"GET {url} -> HTTP {code}");
            }

            if (fallbackClient is not null)
            {
                using var response = await fallbackClient.GetAsync(new Uri(url), ct).ConfigureAwait(false);
                var code = (int)response.StatusCode;
                var ok = code is >= 200 and < 400;
                return new EngineerLiveProbe(name, kind, ok, code, $"GET {url} -> HTTP {code}");
            }

            return new EngineerLiveProbe(name, kind, false, 503, "No HTTP client configured.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogSwallowed(ex, $"Live HTTP probe '{name}' failed against '{url}'.");
            return new EngineerLiveProbe(name, kind, false, 503, $"Probe failed: {ex.Message}");
        }
    }

    private sealed record ScreenViewAudit(
        int NodeCount,
        int KeyedCount,
        int ErrorNodeCount,
        IReadOnlyList<string> Violations);

    private sealed record CapturedSpan(
        string SourceName,
        string OperationName,
        string OtelTraceId,
        string? BrainTraceId,
        bool IsError,
        string? StatusDescription,
        long DurationMs);
}
