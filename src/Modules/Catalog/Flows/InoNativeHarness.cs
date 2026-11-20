using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.Apps.Flows;

internal sealed record InoHydratedBrainSnapshot(
    string CompactContext,
    string? MatchedProjectBriefKey,
    int ByteCount);

internal sealed class InoNativeHarness(
    IServiceProvider? services = null,
    ICoder? coder = null,
    IWorktreeRoslyn? roslyn = null,
    ChangePolicy? policy = null,
    IWorkingMemory? workingMemory = null,
    ISessionJournal? sessionJournal = null,
    IUiIntrospector? uiIntrospector = null,
    IKnowledgeStore? knowledgeStore = null,
    ILogger<InoNativeHarness>? logger = null) : IEngineeringHarness
{
    public const int MaxHydratedContextBytes = 2_900;
    public const int MaxSelfRepairTurns = 2;
    public const string KnowledgeStoreKey = "memory";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ILogger _logger = (ILogger?)logger ?? OsConsoleLog.For<InoNativeHarness>();
    private readonly InoCodeOrchestrator _orchestrator = new(
        roslyn ?? services?.GetService<IWorktreeRoslyn>(),
        policy ?? services?.GetService<ChangePolicy>() ?? ChangePolicy.Default,
        (ILogger?)logger ?? OsConsoleLog.For<InoCodeOrchestrator>());

    public EngineeringHarnessKind Kind => EngineeringHarnessKind.InoNative;

    internal InoCodeOrchestrator Orchestrator => _orchestrator;

    public static InoNativeHarness CreateDefault(
        IServiceProvider? services = null,
        ICoder? coder = null,
        IWorktreeRoslyn? roslyn = null,
        ChangePolicy? policy = null,
        IWorkingMemory? workingMemory = null,
        ISessionJournal? sessionJournal = null,
        IUiIntrospector? uiIntrospector = null,
        IKnowledgeStore? knowledgeStore = null)
        => new(services, coder, roslyn, policy, workingMemory, sessionJournal, uiIntrospector, knowledgeStore);

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task<EngineeringHarnessResult> ExecuteAsync(
        EngineeringHarnessRequest request,
        Func<OperationProgressed, Task>? onProgress = null,
        CancellationToken ct = default)
        => ExecuteWithContextAsync(request, preHydrated: null, recordJournalMilestone: true, onProgress, ct);

    internal async Task<EngineeringHarnessResult> ExecuteWithContextAsync(
        EngineeringHarnessRequest request,
        InoHydratedBrainSnapshot? preHydrated,
        bool recordJournalMilestone,
        Func<OperationProgressed, Task>? onProgress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorktreePath);

        var plan = request.RatifiedPlan ?? new RatifiedPlan();
        var slices = plan.Slices is { Length: > 0 }
            ? plan.Slices
            :
            [
                new EngineerTaskSlice(
                    SliceId: "slice-1",
                    Title: request.Goal,
                    OwnedPaths: [],
                    DependsOnSliceIds: [],
                    Status: "pending",
                    Percent: 0,
                    AssignedAgentKey: "ino.slice-1",
                    Language: "csharp",
                    Instructions: request.Goal),
            ];

        var disjointRule = new NoOverlappingFileSlices(slices);
        if (DigitalBrain.Rules.Rules.Judge("ExecuteAsync", disjointRule) is { } rejected)
        {
            throw new RuleViolationException(rejected);
        }

        var progressEvents = new List<OperationProgressed>();
        var progressGate = new Lock();

        var hydrated = preHydrated ?? await HydrateBrainContextAsync(
            request.Brain,
            request.RepositoryId,
            request.Goal,
            ResolveWorkingMemory(request.Brain),
            ResolveUiIntrospector(request.Brain),
            ResolveKnowledgeStore(request.Brain),
            _logger,
            ct).ConfigureAwait(false);

        var cleanupResult = await _orchestrator.ExecuteCleanupTargetsAsync(
            request.WorktreePath,
            plan.CleanupTargets,
            ct).ConfigureAwait(false);

        if (cleanupResult.DeletedFiles.Length > 0 || cleanupResult.ModifiedFiles.Length > 0)
        {
            await InoCodeOrchestrator.EmitProgressAsync(
                request,
                sliceId: "cleanup",
                percent: 100,
                phase: "cleanup",
                detail: string.Join(" ", cleanupResult.Summaries),
                progressEvents,
                progressGate,
                onProgress).ConfigureAwait(false);
        }

        var waves = EngineerTaskSlices.ComputeWaves(slices);
        var completedSlices = new List<EngineerTaskSlice>(slices.Length);
        var allTouched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var deleted in cleanupResult.DeletedFiles)
        {
            allTouched.Add(deleted);
        }

        foreach (var modified in cleanupResult.ModifiedFiles)
        {
            allTouched.Add(modified);
        }

        var summaries = new List<string>(cleanupResult.Summaries);
        var modelIds = new List<string>();
        var totalSlices = slices.Length;

        foreach (var wave in waves)
        {
            ct.ThrowIfCancellationRequested();

            // Level-2 Intra-Worktree Parallelism: all disjoint slices in the same DAG wave execute concurrently via Task.WhenAll!
            var waveTasks = wave.Select(slice => ExecuteSliceAsync(
                request,
                plan,
                slice,
                totalSlices,
                hydrated.CompactContext,
                progressEvents,
                progressGate,
                onProgress,
                ct)).ToArray();

            var waveOutcomes = await Task.WhenAll(waveTasks).ConfigureAwait(false);
            foreach (var outcome in waveOutcomes)
            {
                completedSlices.Add(outcome.UpdatedSlice);
                if (!outcome.Succeeded)
                {
                    return new EngineeringHarnessResult(
                        Succeeded: false,
                        Harness: EngineeringHarnessKind.InoNative,
                        Summary: string.Join(" ", summaries),
                        ModelId: modelIds.LastOrDefault() ?? "ino-native",
                        TouchedFiles: [.. allTouched.Order(StringComparer.Ordinal)],
                        DeletedFiles: cleanupResult.DeletedFiles,
                        CompletedSlices: [.. completedSlices],
                        ProgressEvents: SnapshotProgress(progressEvents, progressGate),
                        UiStateGraphsHealthy: true,
                        SemanticErrorCount: outcome.SemanticErrorCount,
                        HydratedContextSummary: hydrated.CompactContext,
                        FailureDetail: outcome.FailureDetail,
                        SelfRepairAttempts: completedSlices.Sum(static s => s.SelfRepairAttempts));
                }

                foreach (var touched in outcome.TouchedFiles)
                {
                    allTouched.Add(touched);
                }

                if (!string.IsNullOrWhiteSpace(outcome.Summary))
                {
                    summaries.Add(outcome.Summary);
                }

                if (!string.IsNullOrWhiteSpace(outcome.ModelId))
                {
                    modelIds.Add(outcome.ModelId);
                }
            }
        }

        var totalSelfRepairAttempts = completedSlices.Sum(static s => s.SelfRepairAttempts);
        var touchedArray = allTouched.Order(StringComparer.Ordinal).ToArray();
        var liveCSharpFiles = touchedArray
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !cleanupResult.DeletedFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        var postDiagnostics = liveCSharpFiles.Length > 0
            ? await _orchestrator.GetSemanticDiagnosticsAsync(request.WorktreePath, liveCSharpFiles, ct).ConfigureAwait(false)
            : [];
        var firstError = postDiagnostics.FirstOrDefault(static d => string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase));
        if (firstError is not null)
        {
            return new EngineeringHarnessResult(
                Succeeded: false,
                Harness: EngineeringHarnessKind.InoNative,
                Summary: string.Join(" ", summaries),
                ModelId: modelIds.LastOrDefault() ?? "ino-native",
                TouchedFiles: touchedArray,
                DeletedFiles: cleanupResult.DeletedFiles,
                CompletedSlices: [.. completedSlices],
                ProgressEvents: SnapshotProgress(progressEvents, progressGate),
                UiStateGraphsHealthy: true,
                SemanticErrorCount: postDiagnostics.Count(static d => string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase)),
                HydratedContextSummary: hydrated.CompactContext,
                FailureDetail: $"Roslyn error {firstError.Id} in {firstError.FilePath}:{firstError.Line}: {firstError.Message}",
                SelfRepairAttempts: totalSelfRepairAttempts);
        }

        var uiCheck = await _orchestrator.VerifyModifiedScreensAsync(
            request.WorktreePath,
            liveCSharpFiles,
            ResolveUiIntrospector(request.Brain),
            ct).ConfigureAwait(false);

        if (!uiCheck.Succeeded)
        {
            return new EngineeringHarnessResult(
                Succeeded: false,
                Harness: EngineeringHarnessKind.InoNative,
                Summary: string.Join(" ", summaries),
                ModelId: modelIds.LastOrDefault() ?? "ino-native",
                TouchedFiles: touchedArray,
                DeletedFiles: cleanupResult.DeletedFiles,
                CompletedSlices: [.. completedSlices],
                ProgressEvents: SnapshotProgress(progressEvents, progressGate),
                UiStateGraphsHealthy: false,
                SemanticErrorCount: 0,
                HydratedContextSummary: hydrated.CompactContext,
                FailureDetail: string.Join("; ", uiCheck.Violations),
                SelfRepairAttempts: totalSelfRepairAttempts);
        }

        var combinedSummary = summaries.Count > 0
            ? string.Join(" ", summaries.Where(static s => !string.IsNullOrWhiteSpace(s)))
            : $"Completed {completedSlices.Count} InoNative slice(s).";
        var effectiveModel = modelIds.LastOrDefault(static m => !string.IsNullOrWhiteSpace(m)) ?? "ino-native";

        if (recordJournalMilestone && ResolveSessionJournal(request.Brain) is { } journal)
        {
            string[] decisions = totalSelfRepairAttempts > 0
                ?
                [
                    $"Executed {completedSlices.Count} slice(s) across {waves.Length} disjoint DAG wave(s) via InoNativeHarness ({request.Strategy}).",
                    cleanupResult.DeletedFiles.Length > 0
                        ? $"Deleted {cleanupResult.DeletedFiles.Length} dead cleanup target(s): {string.Join(", ", cleanupResult.DeletedFiles)}."
                        : "Verified 0 Roslyn semantic errors and 0 Accordant StateGraph violations.",
                    string.Create(CultureInfo.InvariantCulture, $"Resolved intra-slice Roslyn diagnostics in {totalSelfRepairAttempts} bounded self-repair turn(s)."),
                ]
                :
                [
                    $"Executed {completedSlices.Count} slice(s) across {waves.Length} disjoint DAG wave(s) via InoNativeHarness ({request.Strategy}).",
                    cleanupResult.DeletedFiles.Length > 0
                        ? $"Deleted {cleanupResult.DeletedFiles.Length} dead cleanup target(s): {string.Join(", ", cleanupResult.DeletedFiles)}."
                        : "Verified 0 Roslyn semantic errors and 0 Accordant StateGraph violations.",
                ];

            await RecordMilestoneBestEffortAsync(
                journal,
                request.FlowId,
                request.Goal,
                hydrated.MatchedProjectBriefKey,
                deliverables:
                [
                    .. completedSlices.Select(static s => $"Slice {s.SliceId} ({s.AssignedAgentKey}): {s.Title}"),
                    .. touchedArray.Select(static f => $"Modified/cleaned: {f}"),
                ],
                decisions: decisions,
                openThreads: [$"Awaiting human SHA-bound review for run '{request.RunId}' on '{request.WorktreeId}'."],
                resolvedThreads: null,
                _logger).ConfigureAwait(false);
        }

        return new EngineeringHarnessResult(
            Succeeded: true,
            Harness: EngineeringHarnessKind.InoNative,
            Summary: combinedSummary,
            ModelId: effectiveModel,
            TouchedFiles: touchedArray,
            DeletedFiles: cleanupResult.DeletedFiles,
            CompletedSlices: [.. completedSlices],
            ProgressEvents: SnapshotProgress(progressEvents, progressGate),
            UiStateGraphsHealthy: uiCheck.Succeeded,
            SemanticErrorCount: 0,
            HydratedContextSummary: hydrated.CompactContext,
            FailureDetail: null,
            SelfRepairAttempts: totalSelfRepairAttempts);
    }

    internal static async Task<InoHydratedBrainSnapshot> HydrateBrainContextAsync(
        BrainId? brain,
        string repositoryId,
        string goal,
        IWorkingMemory? workingMemory,
        IUiIntrospector? uiIntrospector,
        IKnowledgeStore? knowledgeStore,
        ILogger logger,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        string? matchedBriefKey = null;

        if (workingMemory is not null)
        {
            try
            {
                var pack = await workingMemory.ReadStartupPack().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(pack.Soul))
                {
                    sb.AppendLine($"SOUL: {CollapseSingleLine(pack.Soul, 320)}");
                }

                if (!string.IsNullOrWhiteSpace(pack.User))
                {
                    sb.AppendLine($"USER: {CollapseSingleLine(pack.User, 260)}");
                }

                if (!string.IsNullOrWhiteSpace(pack.Project))
                {
                    sb.AppendLine($"PROJECT: {CollapseSingleLine(pack.Project, 420)}");
                }

                if (!pack.Scratchpad.IsEmpty)
                {
                    var scratchBullets = pack.Scratchpad.ActiveThreads
                        .Concat(pack.Scratchpad.EnvironmentNotes)
                        .Concat(pack.Scratchpad.PendingDecisions)
                        .Where(static s => !string.IsNullOrWhiteSpace(s))
                        .Take(4)
                        .Select(static s => CollapseSingleLine(s, 140));
                    sb.AppendLine("WORKING_SCRATCHPAD: " + string.Join(" | ", scratchBullets));
                }

                var candidateBrief = SelectMatchingProjectBrief(pack.AvailableBriefs, repositoryId, goal);
                if (candidateBrief is not null)
                {
                    var brief = await workingMemory.GetBrief(candidateBrief.Key).ConfigureAwait(false);
                    if (brief is not null && !string.IsNullOrWhiteSpace(brief.Content))
                    {
                        matchedBriefKey = brief.Key;
                        sb.AppendLine($"BRIEF[{brief.Key}]: {CollapseSingleLine(brief.Content, 800)}");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogSwallowed(ex, $"hydrating IWorkingMemory for brain '{brain}'");
            }
        }

        if (uiIntrospector is not null)
        {
            try
            {
                var contracts = await uiIntrospector.Contracts().ConfigureAwait(false);
                var graphs = await uiIntrospector.ListStateGraphs().ConfigureAwait(false);
                if (contracts.Length > 0 || graphs.Length > 0)
                {
                    var kinds = string.Join(", ", contracts.Take(8).Select(static c => c.NeuronKind));
                    var healthyCount = graphs.Count(static g => g.IsHealthy);
                    sb.AppendLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"UI_INTROSPECTOR: {contracts.Length} UI contracts ({kinds}); {healthyCount}/{graphs.Length} healthy StateGraphs."));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogSwallowed(ex, $"hydrating IUiIntrospector for brain '{brain}'");
            }
        }

        if (knowledgeStore is not null)
        {
            try
            {
                var hits = await knowledgeStore.SemanticSearchAsync($"{repositoryId} {goal}", limit: 4, ct).ConfigureAwait(false);
                if (hits.Count > 0)
                {
                    var facts = string.Join(" | ", hits.Select(static h => $"{h.Node.Label} ({h.Node.Type})"));
                    sb.AppendLine($"KNOWLEDGE_GRAPH: {CollapseSingleLine(facts, 420)}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogSwallowed(ex, $"hydrating IKnowledgeStore for brain '{brain}'");
            }
        }

        var compact = TruncateUtf8(sb.ToString().Trim(), MaxHydratedContextBytes);
        var bytes = Encoding.UTF8.GetByteCount(compact);
        return new InoHydratedBrainSnapshot(compact, matchedBriefKey, bytes);
    }

    internal static async Task RecordMilestoneBestEffortAsync(
        ISessionJournal journal,
        string? conversationOrFlowId,
        string goal,
        string? projectBriefKey,
        string[] deliverables,
        string[] decisions,
        string[]? openThreads,
        string[]? resolvedThreads,
        ILogger logger)
    {
        try
        {
            await journal.RecordMilestone(new SessionMilestone
            {
                ConversationId = string.IsNullOrWhiteSpace(conversationOrFlowId) ? null : conversationOrFlowId,
                Goal = goal,
                ProjectBrief = projectBriefKey,
                Deliverables = deliverables,
                Decisions = decisions,
                OpenThreads = openThreads,
                ResolvedThreads = resolvedThreads,
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogSwallowed(ex, $"recording SessionMilestone for '{conversationOrFlowId}'");
        }
    }

    private async Task<SliceExecutionOutcome> ExecuteSliceAsync(
        EngineeringHarnessRequest request,
        RatifiedPlan plan,
        EngineerTaskSlice slice,
        int totalSlices,
        string hydratedContext,
        List<OperationProgressed> progressSink,
        Lock progressGate,
        Func<OperationProgressed, Task>? onProgress,
        CancellationToken ct)
    {
        var agentKey = string.IsNullOrWhiteSpace(slice.AssignedAgentKey) ? $"ino.{slice.SliceId}" : slice.AssignedAgentKey;
        var selfRepairAttempts = 0;

        try
        {
            await InoCodeOrchestrator.EmitProgressAsync(
                request,
                slice.SliceId,
                percent: 0,
                phase: "starting",
                detail: $"[{agentKey}] Starting slice '{slice.Title}'",
                progressSink,
                progressGate,
                onProgress).ConfigureAwait(false);

            var symbolHint = string.Empty;
            if (slice.Language is "csharp" or "mixed" or "")
            {
                var filter = slice.OwnedPaths is { Length: 1 } singleOwned ? singleOwned[0] : null;
                var symbols = await _orchestrator.FindSymbolsAsync(request.WorktreePath, filter, ct).ConfigureAwait(false);
                if (symbols.Count > 0)
                {
                    var topSymbols = string.Join(", ", symbols.Take(12).Select(static s => $"{s.Kind} {s.Name} ({s.FilePath})"));
                    symbolHint = $"\nRoslyn worktree symbols: {topSymbols}";
                }
            }

            await InoCodeOrchestrator.EmitProgressAsync(
                request,
                slice.SliceId,
                percent: 25,
                phase: "analyzing",
                detail: $"[{agentKey}] Hydrated memory & inspected AST symbols for '{slice.SliceId}'",
                progressSink,
                progressGate,
                onProgress).ConfigureAwait(false);

            var strategyDirective = request.TotalInstances >= 2
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"\n[Candidate w{request.InstanceIndex + 1} Strategy — {request.StrategyLabel} ({request.Strategy})]: {request.StrategyPromptLens}")
                : string.Empty;
            var memoryDirective = !string.IsNullOrWhiteSpace(hydratedContext)
                ? $"\n\n[Ino Hydrated Brain Context]\n{hydratedContext}"
                : string.Empty;
            var ownershipDirective = slice.OwnedPaths is { Length: > 0 }
                ? $"\nOwned files (strict disjoint slice boundary): {string.Join(", ", slice.OwnedPaths)}"
                : string.Empty;

            var slicePrompt = BuildSlicePrompt(
                request,
                plan,
                slice,
                totalSlices,
                symbolHint,
                ownershipDirective,
                strategyDirective,
                memoryDirective);

            var activeCoder = coder ?? services?.GetService<ICoder>();
            var editResult = await InvokeSliceWorkerAsync(
                request,
                slice,
                agentKey,
                slicePrompt,
                hydratedContext,
                activeCoder,
                ct).ConfigureAwait(false);

            var touchedSet = new HashSet<string>(editResult.TouchedFiles, StringComparer.Ordinal);
            var latestSummary = editResult.Summary;
            var latestModelId = editResult.ModelId;

            await InoCodeOrchestrator.EmitProgressAsync(
                request,
                slice.SliceId,
                percent: 60,
                phase: "editing",
                detail: $"[{agentKey}] Applied edits to {touchedSet.Count} file(s)",
                progressSink,
                progressGate,
                onProgress).ConfigureAwait(false);

            var sliceDiags = await _orchestrator.GetSliceDiagnosticsAsync(
                request.WorktreePath,
                [.. touchedSet],
                slice.OwnedPaths,
                ct).ConfigureAwait(false);
            var errorDiags = sliceDiags
                .Where(static d => string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            var canSelfRepair = activeCoder is not null || CanResolveModelInference();
            while (errorDiags.Length > 0 && canSelfRepair && selfRepairAttempts < MaxSelfRepairTurns)
            {
                ct.ThrowIfCancellationRequested();
                selfRepairAttempts++;
                var primaryErr = errorDiags[0];
                var repairDetail = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Repairing {primaryErr.Id} in {primaryErr.FilePath} (attempt {selfRepairAttempts}/{MaxSelfRepairTurns})");

                await InoCodeOrchestrator.EmitProgressAsync(
                    request,
                    slice.SliceId,
                    percent: 60 + (selfRepairAttempts * 10),
                    phase: "self-repair",
                    detail: repairDetail,
                    progressSink,
                    progressGate,
                    onProgress).ConfigureAwait(false);

                var diagnosticContext = await _orchestrator.BuildSelfRepairDiagnosticContextAsync(
                    request.WorktreePath,
                    errorDiags,
                    ct).ConfigureAwait(false);
                var repairPrompt = BuildSelfRepairPrompt(
                    slicePrompt,
                    slice,
                    selfRepairAttempts,
                    diagnosticContext);

                var repairResult = await InvokeSliceWorkerAsync(
                    request,
                    slice,
                    agentKey,
                    repairPrompt,
                    hydratedContext,
                    activeCoder,
                    ct).ConfigureAwait(false);

                foreach (var repairedPath in repairResult.TouchedFiles)
                {
                    touchedSet.Add(repairedPath);
                }

                if (!string.IsNullOrWhiteSpace(repairResult.Summary))
                {
                    latestSummary = repairResult.Summary;
                }

                if (!string.IsNullOrWhiteSpace(repairResult.ModelId))
                {
                    latestModelId = repairResult.ModelId;
                }

                sliceDiags = await _orchestrator.GetSliceDiagnosticsAsync(
                    request.WorktreePath,
                    [.. touchedSet],
                    slice.OwnedPaths,
                    ct).ConfigureAwait(false);
                errorDiags = sliceDiags
                    .Where(static d => string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }

            var sliceCsCount = touchedSet.Count(static f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
            var sliceErrorCount = errorDiags.Length;

            await InoCodeOrchestrator.EmitProgressAsync(
                request,
                slice.SliceId,
                percent: 90,
                phase: "diagnostics",
                detail: $"[{agentKey}] Semantic check on {sliceCsCount} C# file(s): {sliceErrorCount} error(s)",
                progressSink,
                progressGate,
                onProgress).ConfigureAwait(false);

            if (sliceErrorCount > 0)
            {
                var err = errorDiags[0];
                return new SliceExecutionOutcome(
                    Succeeded: false,
                    UpdatedSlice: slice with
                    {
                        Status = "failed",
                        Percent = 90,
                        AssignedAgentKey = agentKey,
                        SelfRepairAttempts = selfRepairAttempts,
                    },
                    TouchedFiles: [.. touchedSet.Order(StringComparer.Ordinal)],
                    Summary: latestSummary,
                    ModelId: latestModelId,
                    SemanticErrorCount: sliceErrorCount,
                    FailureDetail: $"Roslyn error {err.Id} in {err.FilePath}:{err.Line}: {err.Message}");
            }

            await InoCodeOrchestrator.EmitProgressAsync(
                request,
                slice.SliceId,
                percent: 100,
                phase: "completed",
                detail: $"[{agentKey}] Completed '{slice.Title}'",
                progressSink,
                progressGate,
                onProgress).ConfigureAwait(false);

            return new SliceExecutionOutcome(
                Succeeded: true,
                UpdatedSlice: slice with
                {
                    Status = "completed",
                    Percent = 100,
                    AssignedAgentKey = agentKey,
                    SelfRepairAttempts = selfRepairAttempts,
                },
                TouchedFiles: [.. touchedSet.Order(StringComparer.Ordinal)],
                Summary: latestSummary,
                ModelId: latestModelId,
                SemanticErrorCount: 0,
                FailureDetail: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogSwallowed(ex, $"InoNative slice '{slice.SliceId}' ({agentKey}) failed");
            return new SliceExecutionOutcome(
                Succeeded: false,
                UpdatedSlice: slice with
                {
                    Status = "failed",
                    AssignedAgentKey = agentKey,
                    SelfRepairAttempts = selfRepairAttempts,
                },
                TouchedFiles: [],
                Summary: string.Empty,
                ModelId: string.Empty,
                SemanticErrorCount: 0,
                FailureDetail: ex.Message);
        }
    }

    private async Task<CoderEditResult> InvokeSliceWorkerAsync(
        EngineeringHarnessRequest request,
        EngineerTaskSlice slice,
        string agentKey,
        string prompt,
        string hydratedContext,
        ICoder? activeCoder,
        CancellationToken ct)
    {
        if (activeCoder is not null && activeCoder is not LocalLlmCoder)
        {
            var syntheticLease = BuildSyntheticSliceLease(request);
            var result = await activeCoder.EditAsync(syntheticLease, prompt, ct).ConfigureAwait(false);
            foreach (var touched in result.TouchedFiles)
            {
                InoCodeOrchestrator.EnsureWithinSliceOwnership(touched, slice.OwnedPaths);
            }

            return result;
        }

        if (CanResolveModelInference())
        {
            return await RunNativeSliceModelWorkerAsync(
                request,
                slice,
                agentKey,
                prompt,
                hydratedContext,
                ct).ConfigureAwait(false);
        }

        if (activeCoder is not null)
        {
            var syntheticLease = BuildSyntheticSliceLease(request);
            var result = await activeCoder.EditAsync(syntheticLease, prompt, ct).ConfigureAwait(false);
            foreach (var touched in result.TouchedFiles)
            {
                InoCodeOrchestrator.EnsureWithinSliceOwnership(touched, slice.OwnedPaths);
            }

            return result;
        }

        throw new InvalidOperationException("No IModelInference or ICoder is configured for InoNativeHarness slice execution.");
    }

    private static WorkTreeLease BuildSyntheticSliceLease(EngineeringHarnessRequest request)
        => new(
            RunId: request.RunId,
            RepositoryId: request.RepositoryId,
            SourceRepoPath: request.SourceRepositoryPath ?? request.WorktreePath,
            TreePath: request.WorktreePath,
            RealGitDirPath: request.WorktreePath,
            GitPointerSha256Hex: new string('0', 64),
            BaseBranch: request.BaseBranch ?? "main",
            BaseCommitSha: new string('0', 40),
            BranchName: $"ino/{request.RunId}",
            LeaseId: request.WorktreeId,
            InstanceIndex: request.InstanceIndex);

    private static string BuildSelfRepairPrompt(
        string originalSlicePrompt,
        EngineerTaskSlice slice,
        int attempt,
        string diagnosticContext)
    {
        var ownedPathsNote = slice.OwnedPaths is { Length: > 0 }
            ? $"Strictly confine repair edits to owned slice path(s): {string.Join(", ", slice.OwnedPaths)}."
            : "Strictly confine repair edits within repository policy.";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{originalSlicePrompt}\n\n[Intra-Slice Roslyn Self-Repair — Attempt {attempt}/{MaxSelfRepairTurns}]\n{ownedPathsNote}\n{diagnosticContext}\nFix the reported C# compiler error(s) so semantic/syntax diagnostics reach 0 errors.");
    }

    private static string BuildSlicePrompt(
        EngineeringHarnessRequest request,
        RatifiedPlan plan,
        EngineerTaskSlice slice,
        int totalSlices,
        string symbolHint,
        string ownershipDirective,
        string strategyDirective,
        string memoryDirective)
    {
        if (totalSlices == 1
            && request.TotalInstances == 1
            && string.IsNullOrWhiteSpace(memoryDirective)
            && (slice.OwnedPaths is null || slice.OwnedPaths.Length == 0))
        {
            return request.Goal;
        }

        if (totalSlices == 1
            && string.IsNullOrWhiteSpace(memoryDirective)
            && (slice.OwnedPaths is null || slice.OwnedPaths.Length == 0))
        {
            return $"{request.Goal}{strategyDirective}";
        }

        return $"{request.Goal}\n\n[Task {slice.SliceId}: {slice.Title}]\n{slice.Instructions}\nArchitect note: {plan.ArchitectureSummary}{ownershipDirective}{symbolHint}{strategyDirective}{memoryDirective}";
    }

    private bool CanResolveModelInference()
        => services?.GetService<IModelInference>() is not null;

    private async Task<CoderEditResult> RunNativeSliceModelWorkerAsync(
        EngineeringHarnessRequest request,
        EngineerTaskSlice slice,
        string agentKey,
        string slicePrompt,
        string hydratedContext,
        CancellationToken ct)
    {
        var (inference, selection, modelId, supportsTemp) = await ResolveSliceInferenceAsync(request.Brain, ct).ConfigureAwait(false);
        var slicePolicy = slice.OwnedPaths is { Length: > 0 } && slice.OwnedPaths.Length > _orchestrator.Policy.MaxChangedFiles
            ? new ChangePolicy(
                maxChangedFiles: slice.OwnedPaths.Length,
                allowedExtensions: _orchestrator.Policy.AllowedExtensions,
                blockedFileNames: _orchestrator.Policy.BlockedFileNames)
            : _orchestrator.Policy;

        var sandbox = new LocalLlmCoder.ConfinedWorkTreeSandbox(request.WorktreePath, slicePolicy, _orchestrator.Roslyn);
        var tools = BuildSliceTools(slicePolicy);
        var ownedSummary = slice.OwnedPaths is { Length: > 0 }
            ? $"Strictly confine all writes to your owned slice paths: {string.Join(", ", slice.OwnedPaths)}."
            : "Confine all writes within the repository policy.";
        var memorySection = string.IsNullOrWhiteSpace(hydratedContext)
            ? string.Empty
            : $"\nShared Hydrated Brain Context (<3KB):\n{hydratedContext}\n";

        var messages = new List<AiMessage>
        {
            new("system",
            [
                new AiText($"""
                    You are {agentKey}, an autonomous parallel AgentNeuron slice worker inside InoNativeHarness.
                    {slicePolicy.Describe()}
                    {ownedSummary}
                    {memorySection}
                    Prefer roslyn_batch_edit or roslyn_edit_member + roslyn_diagnostics for AST-safe C# edits, and write_file / replace_in_file for other allowed files.
                    Once your slice's files are updated and verified, respond with a concise 1-sentence summary.
                    """),
            ]),
            new("user", [new AiText(slicePrompt)]),
        };

        var lastSummary = $"[{agentKey}] Implemented slice '{slice.Title}'.";
        for (var round = 0; round < 10; round++)
        {
            ct.ThrowIfCancellationRequested();
            var options = supportsTemp ? new InferenceOptions(Temperature: 0.1f) : null;
            var reply = await inference.Generate(
                new InferenceRequest(messages, Model: selection, Options: options, Tools: tools),
                marker: null,
                cancellationToken: ct,
                brainId: request.Brain?.Value).ConfigureAwait(false);

            var assistant = reply.Messages.LastOrDefault(static m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                ?? reply.Messages.LastOrDefault()
                ?? new AiMessage("assistant", []);
            messages.Add(assistant);

            var calls = assistant.Content.OfType<AiToolCall>().ToArray();
            var text = string.Join("\n", assistant.Content.OfType<AiText>().Select(static t => t.Text)).Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                lastSummary = text;
            }

            if (calls.Length == 0)
            {
                if (sandbox.TouchedFiles.Count > 0)
                {
                    foreach (var touched in sandbox.TouchedFiles)
                    {
                        InoCodeOrchestrator.EnsureWithinSliceOwnership(touched, slice.OwnedPaths);
                    }

                    return new CoderEditResult(lastSummary, sandbox.TouchedFiles, modelId);
                }

                messages.Add(new AiMessage("user", [new AiText("Apply the requested slice modification now using roslyn_batch_edit, roslyn_edit_member, replace_in_file, or write_file.")]));
                continue;
            }

            var toolResults = new List<AiContent>(calls.Length);
            foreach (var call in calls)
            {
                var output = await ExecuteSliceToolCallAsync(call, sandbox, request.WorktreePath, slice.OwnedPaths, ct).ConfigureAwait(false);
                toolResults.Add(new AiToolResult(call.CallId, LocalLlmCoder.EnsureJsonToolResult(output)));
            }

            messages.Add(new AiMessage("tool", [.. toolResults]));
        }

        if (sandbox.TouchedFiles.Count > 0)
        {
            foreach (var touched in sandbox.TouchedFiles)
            {
                InoCodeOrchestrator.EnsureWithinSliceOwnership(touched, slice.OwnedPaths);
            }

            return new CoderEditResult(lastSummary, sandbox.TouchedFiles, modelId);
        }

        throw new InvalidOperationException($"Slice worker '{agentKey}' completed tool rounds without modifying any file for slice '{slice.SliceId}'.");
    }

    private async Task<string> ExecuteSliceToolCallAsync(
        AiToolCall call,
        LocalLlmCoder.ConfinedWorkTreeSandbox sandbox,
        string worktreePath,
        string[]? sliceOwnedPaths,
        CancellationToken ct)
    {
        if (string.Equals(call.Name, "roslyn_batch_edit", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                if (!doc.RootElement.TryGetProperty("edits", out var editsEl) || editsEl.ValueKind != JsonValueKind.Array)
                {
                    return JsonSerializer.Serialize(new { ok = false, error = "roslyn_batch_edit requires an 'edits' array." }, Json);
                }

                var edits = new List<WorktreeCSharpEdit>();
                foreach (var item in editsEl.EnumerateArray())
                {
                    var rawKind = item.TryGetProperty("kind", out var k) ? (k.GetString() ?? "") : "";
                    var rawPath = item.TryGetProperty("path", out var p) ? (p.GetString() ?? "") : "";
                    var kind = rawKind.Trim().ToLowerInvariant() switch
                    {
                        "add_using" or "addusing" => WorktreeCSharpEditKind.AddUsing,
                        "add_or_replace_member" or "addorreplacemember" => WorktreeCSharpEditKind.AddOrReplaceMember,
                        "remove_member" or "removemember" => WorktreeCSharpEditKind.RemoveMember,
                        "create_type_file" or "createtypefile" => WorktreeCSharpEditKind.CreateTypeFile,
                        _ => throw new ArgumentException($"Unsupported batch edit kind '{rawKind}'."),
                    };

                    edits.Add(new WorktreeCSharpEdit(
                        Kind: kind,
                        RelativePath: rawPath,
                        TypeName: item.TryGetProperty("type_name", out var tn) ? tn.GetString() : null,
                        MemberName: item.TryGetProperty("member_name", out var mn) ? mn.GetString() : null,
                        NamespaceName: item.TryGetProperty("namespace_name", out var nn) ? nn.GetString() : null,
                        Source: item.TryGetProperty("source", out var src) ? src.GetString() : null,
                        TypeKind: item.TryGetProperty("type_kind", out var tk) ? tk.GetString() : null,
                        BaseTypes: item.TryGetProperty("base_types", out var bt) ? bt.GetString() : null));
                }

                var summary = await _orchestrator.ApplyAstEditsAsync(worktreePath, edits, sliceOwnedPaths, ct).ConfigureAwait(false);
                sandbox.RecordTouchedFiles(summary.TouchedFiles);

                return JsonSerializer.Serialize(new
                {
                    ok = summary.Succeeded,
                    touchedFiles = summary.TouchedFiles,
                    diagnostics = summary.Diagnostics,
                    detail = summary.Detail,
                }, Json);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogSwallowed(ex, "roslyn_batch_edit failed");
                return JsonSerializer.Serialize(new { ok = false, error = ex.Message }, Json);
            }
        }

        if (call.Name is "write_file" or "replace_in_file" or "roslyn_edit_member")
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            if (doc.RootElement.TryGetProperty("path", out var pathEl) && pathEl.ValueKind == JsonValueKind.String)
            {
                InoCodeOrchestrator.EnsureWithinSliceOwnership(pathEl.GetString() ?? string.Empty, sliceOwnedPaths);
            }
        }

        return await sandbox.ExecuteToolAsync(call.Name, call.ArgumentsJson, ct).ConfigureAwait(false);
    }

    private async Task<(IModelInference Inference, ModelSelection? Selection, string ModelId, bool SupportsTemp)> ResolveSliceInferenceAsync(
        BrainId? brain,
        CancellationToken ct)
    {
        var inference = services?.GetService<IModelInference>()
            ?? throw new InvalidOperationException("IModelInference is not registered.");
        var catalog = services?.GetService<ModelCatalog>();

        if (catalog is not null)
        {
            var resolved = await catalog.Decide(brain?.Value, ModelUse.Code, null, personModel: null, requiresTools: true, ct).ConfigureAwait(false);
            if (resolved.Provider == AiProvider.Host && services?.GetService<IChatClient>() is null)
            {
                throw new NoProviderConfiguredException("No tool-capable model is configured for InoNativeHarness.");
            }

            ModelSelection? sel = resolved.Provider == AiProvider.Host ? null
                : resolved.Profile is { Length: > 0 } ? new($"profile:{resolved.Profile}", resolved.Reasoning, resolved.MaxOutputTokens)
                : resolved.Provider == AiProvider.Ollama ? new($"ollama/{resolved.Model}", resolved.Reasoning, resolved.MaxOutputTokens)
                : new(resolved.Model, resolved.Reasoning, resolved.MaxOutputTokens);
            var desc = await inference.Describe(sel, marker: null, cancellationToken: ct, brainId: brain?.Value).ConfigureAwait(false);
            return (inference, sel, resolved.Model, desc.Temperature == CapabilitySupport.Supported);
        }

        var defaultDesc = await inference.Describe(null, marker: null, cancellationToken: ct, brainId: brain?.Value).ConfigureAwait(false);
        return (inference, null, defaultDesc.Model, defaultDesc.Temperature == CapabilitySupport.Supported);
    }

    private static IReadOnlyList<InferenceTool> BuildSliceTools(ChangePolicy policy)
    {
        var extensions = string.Join(", ", policy.AllowedExtensions.Order(StringComparer.OrdinalIgnoreCase));
        return
        [
            new(
                "list_files",
                "List allowed repository files under a relative directory.",
                """{"type":"object","properties":{"directory":{"type":"string"},"filter":{"type":"string"}}}"""),
            new(
                "read_file",
                "Read the UTF-8 contents of a file inside the worktree.",
                """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}"""),
            new(
                "replace_in_file",
                "Replace an exact target snippet in an owned file inside the worktree.",
                """{"type":"object","properties":{"path":{"type":"string"},"old_text":{"type":"string"},"new_text":{"type":"string"}},"required":["path","old_text","new_text"]}"""),
            new(
                "write_file",
                $"Create or overwrite an owned file ({extensions}) inside the worktree.",
                """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"]}"""),
            new(
                "roslyn_symbols",
                "Inspect C# types, namespaces, and members in the worktree via Roslyn.",
                """{"type":"object","properties":{"filter":{"type":"string"}}}"""),
            new(
                "roslyn_diagnostics",
                "Run Roslyn semantic diagnostics on worktree C# files.",
                """{"type":"object","properties":{"path":{"type":"string"}}}"""),
            new(
                "roslyn_edit_member",
                "Apply a single AST-validated C# edit (add_using, add_or_replace_member, remove_member, create_type_file).",
                """{"type":"object","properties":{"kind":{"type":"string"},"path":{"type":"string"},"type_name":{"type":"string"},"member_name":{"type":"string"},"namespace_name":{"type":"string"},"source":{"type":"string"},"type_kind":{"type":"string"},"base_types":{"type":"string"}},"required":["kind","path"]}"""),
            new(
                "roslyn_batch_edit",
                "Execute a deterministic multi-edit C# AST batch via InoCodeOrchestrator in a single turn.",
                """{"type":"object","properties":{"edits":{"type":"array","items":{"type":"object","properties":{"kind":{"type":"string"},"path":{"type":"string"},"type_name":{"type":"string"},"member_name":{"type":"string"},"namespace_name":{"type":"string"},"source":{"type":"string"},"type_kind":{"type":"string"},"base_types":{"type":"string"}},"required":["kind","path"]}}},"required":["edits"]}"""),
        ];
    }

    private IWorkingMemory? ResolveWorkingMemory(BrainId? brain)
    {
        if (workingMemory is not null)
        {
            return workingMemory;
        }

        if (services?.GetService<IWorkingMemory>() is { } direct)
        {
            return direct;
        }

        if (brain is { } b && services?.GetService<INeuronProxies>() is { } proxies)
        {
            return proxies.Get<IWorkingMemory>(IWorkingMemory.NeuronIdOf(b));
        }

        return null;
    }

    private ISessionJournal? ResolveSessionJournal(BrainId? brain)
    {
        if (sessionJournal is not null)
        {
            return sessionJournal;
        }

        if (services?.GetService<ISessionJournal>() is { } direct)
        {
            return direct;
        }

        if (brain is { } b && services?.GetService<INeuronProxies>() is { } proxies)
        {
            return proxies.Get<ISessionJournal>(ISessionJournal.NeuronIdOf(b));
        }

        return null;
    }

    private IUiIntrospector? ResolveUiIntrospector(BrainId? brain)
    {
        if (uiIntrospector is not null)
        {
            return uiIntrospector;
        }

        if (services?.GetService<IUiIntrospector>() is { } direct)
        {
            return direct;
        }

        if (brain is { } b && services?.GetService<INeuronProxies>() is { } proxies)
        {
            return proxies.Get<IUiIntrospector>(IUiIntrospector.NeuronIdOf(b));
        }

        return null;
    }

    private IKnowledgeStore? ResolveKnowledgeStore(BrainId? brain)
    {
        if (knowledgeStore is not null)
        {
            return knowledgeStore;
        }

        if (services?.GetService<IKnowledgeStore>() is { } direct)
        {
            return direct;
        }

        if (brain is { } b && services?.GetService<INeuronProxies>() is { } proxies)
        {
            return proxies.Get<IKnowledgeStore>(new NeuronId(b, "ai.knowledge-store", KnowledgeStoreKey));
        }

        return null;
    }

    internal static ContextBriefSummary? SelectMatchingProjectBrief(
        IReadOnlyList<ContextBriefSummary>? briefs,
        string repositoryId,
        string goal)
    {
        if (briefs is null || briefs.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(repositoryId))
        {
            var repoTag = $"repo:{repositoryId.Trim()}";
            foreach (var brief in briefs)
            {
                if (string.Equals(brief.Key, repoTag, StringComparison.OrdinalIgnoreCase))
                {
                    return brief;
                }
            }
        }

        foreach (var brief in briefs)
        {
            if (!string.IsNullOrWhiteSpace(repositoryId)
                && (brief.Key.Contains(repositoryId, StringComparison.OrdinalIgnoreCase)
                    || brief.Title.Contains(repositoryId, StringComparison.OrdinalIgnoreCase)))
            {
                return brief;
            }
        }

        foreach (var brief in briefs)
        {
            if (!string.IsNullOrWhiteSpace(goal)
                && brief.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(word => word.Length >= 4 && goal.Contains(word, StringComparison.OrdinalIgnoreCase)))
            {
                return brief;
            }
        }

        return briefs.FirstOrDefault(static b => string.Equals(b.Category, "project", StringComparison.OrdinalIgnoreCase))
            ?? briefs[0];
    }

    private static OperationProgressed[] SnapshotProgress(List<OperationProgressed> events, Lock gate)
    {
        lock (gate)
        {
            return [.. events];
        }
    }

    internal static string CollapseSingleLine(string text, int maxChars)
    {
        var single = text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Trim();
        return single.Length <= maxChars ? single : single[..maxChars].TrimEnd() + "...";
    }

    internal static string TruncateUtf8(string text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text) || Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return text;
        }

        var builder = new StringBuilder();
        var used = 0;
        const string Suffix = "...";
        var budget = Math.Max(0, maxBytes - Encoding.UTF8.GetByteCount(Suffix));

        foreach (var rune in text.EnumerateRunes())
        {
            var runeStr = rune.ToString();
            var runeBytes = Encoding.UTF8.GetByteCount(runeStr);
            if (used + runeBytes > budget)
            {
                break;
            }

            builder.Append(runeStr);
            used += runeBytes;
        }

        return builder.Append(Suffix).ToString();
    }

    private sealed record SliceExecutionOutcome(
        bool Succeeded,
        EngineerTaskSlice UpdatedSlice,
        string[] TouchedFiles,
        string Summary,
        string ModelId,
        int SemanticErrorCount,
        string? FailureDetail);
}
