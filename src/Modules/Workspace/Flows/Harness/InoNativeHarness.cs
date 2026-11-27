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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.Apps.Flows;

internal sealed record InoHydratedBrainSnapshot(
    string CompactContext,
    string? MatchedProjectBriefKey,
    int ByteCount);

internal sealed partial class InoNativeHarness(
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
    // Local repair turns; when they all fail, one more turn may escalate to a keyed frontier model.
    public const int MaxSelfRepairTurns = 2;
    public const string FrontierEscalationPhase = "frontier-escalation";
    public const string KnowledgeStoreKey = "memory";

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
                        ModelId: string.IsNullOrWhiteSpace(outcome.ModelId) ? modelIds.LastOrDefault() ?? "ino-native" : outcome.ModelId,
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
            ? await _orchestrator.Roslyn.AnalyzeAsync(request.WorktreePath, liveCSharpFiles, ct).ConfigureAwait(false)
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
                var symbols = await _orchestrator.Roslyn.InspectSymbolsAsync(request.WorktreePath, filter, ct).ConfigureAwait(false);
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

            var frontierFailure = default(string);
            if (errorDiags.Length > 0
                && canSelfRepair
                && selfRepairAttempts >= MaxSelfRepairTurns
                && CanResolveModelInference()
                && EscalatesToFrontier()
                && await LocalLlmCoder.ResolveFrontierClientAsync(services, request.Brain, _logger, ct).ConfigureAwait(false) is { } frontier)
            {
                using (frontier)
                {
                    if (!string.Equals(frontier.Model.Model, latestModelId, StringComparison.OrdinalIgnoreCase))
                    {
                        selfRepairAttempts++;
                        var primaryErr = errorDiags[0];
                        var localModelId = string.IsNullOrWhiteSpace(latestModelId) ? "local" : latestModelId;
                        await InoCodeOrchestrator.EmitProgressAsync(
                            request,
                            slice.SliceId,
                            percent: 85,
                            phase: FrontierEscalationPhase,
                            detail: string.Create(
                                CultureInfo.InvariantCulture,
                                $"Escalating {primaryErr.Id} in {primaryErr.FilePath} from local model '{localModelId}' to frontier model '{frontier.Model.Model}' after {MaxSelfRepairTurns} failed local repair(s)"),
                            progressSink,
                            progressGate,
                            onProgress).ConfigureAwait(false);

                        var diagnosticContext = await _orchestrator.BuildSelfRepairDiagnosticContextAsync(
                            request.WorktreePath,
                            errorDiags,
                            ct).ConfigureAwait(false);
                        var escalationPrompt = BuildFrontierRepairPrompt(slicePrompt, slice, localModelId, diagnosticContext);

                        // The frontier model is the one now answering for this slice, whether or not its turn succeeds.
                        latestModelId = frontier.Model.Model;
                        try
                        {
                            var frontierResult = await RunNativeSliceModelWorkerAsync(
                                request,
                                slice,
                                agentKey,
                                escalationPrompt,
                                hydratedContext,
                                frontier,
                                ct).ConfigureAwait(false);

                            foreach (var repairedPath in frontierResult.TouchedFiles)
                            {
                                touchedSet.Add(repairedPath);
                            }

                            if (!string.IsNullOrWhiteSpace(frontierResult.Summary))
                            {
                                latestSummary = frontierResult.Summary;
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
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogSwallowed(ex, $"InoNative slice '{slice.SliceId}' frontier escalation to '{frontier.Model.Model}' failed");
                            frontierFailure = $" (frontier escalation to '{frontier.Model.Model}' failed)";
                        }
                    }
                }
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
                    FailureDetail: $"Roslyn error {err.Id} in {err.FilePath}:{err.Line}: {err.Message}{frontierFailure}");
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
                resolvedClient: null,
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

    private bool CanResolveModelInference()
        => services?.GetService<ModelCatalog>() is not null || services?.GetService<IModelInference>() is not null;

    private bool EscalatesToFrontier()
        => LocalLlmCoder.BindOptions(services?.GetService<IConfiguration>()).EscalateToFrontierOnRepeatedFailure;

    private Task<CoderEditResult> RunNativeSliceModelWorkerAsync(
        EngineeringHarnessRequest request,
        EngineerTaskSlice slice,
        string agentKey,
        string slicePrompt,
        string hydratedContext,
        ModelCatalog.ResolvedRoleClient? resolvedClient,
        CancellationToken ct)
    {
        var slicePolicy = slice.OwnedPaths is { Length: > 0 } && slice.OwnedPaths.Length > _orchestrator.Policy.MaxChangedFiles
            ? new ChangePolicy(slice.OwnedPaths.Length, _orchestrator.Policy.AllowedExtensions, _orchestrator.Policy.BlockedFileNames)
            : _orchestrator.Policy;
        var ownedSummary = slice.OwnedPaths is { Length: > 0 }
            ? $"Strictly confine all writes to your owned slice paths: {string.Join(", ", slice.OwnedPaths)}."
            : "Confine all writes within the repository policy.";
        var memorySection = string.IsNullOrWhiteSpace(hydratedContext) ? string.Empty : $"\nShared Hydrated Brain Context (<3KB):\n{hydratedContext}\n";

        var systemPrompt = $"""
            You are {agentKey}, an autonomous parallel AgentNeuron slice worker inside InoNativeHarness.
            {slicePolicy.Describe()}
            {ownedSummary}
            {memorySection}
            Prefer roslyn_batch_edit or roslyn_edit_member + roslyn_diagnostics for AST-safe C# edits, and write_file / replace_in_file for other allowed files.
            Once your slice's files are updated and verified, respond with a concise 1-sentence summary.
            """;

        return LocalLlmCoder.RunToolLoopAsync(
            services,
            new LocalLlmCoder.ToolLoopRequest(
                WorktreePath: request.WorktreePath,
                SystemPrompt: systemPrompt,
                UserPrompt: slicePrompt,
                Policy: slicePolicy,
                Roslyn: _orchestrator.Roslyn,
                MaxToolRounds: 10,
                DefaultSummary: $"[{agentKey}] Implemented slice '{slice.Title}'.",
                NudgePrompt: "Apply the requested slice modification now using roslyn_batch_edit, roslyn_edit_member, replace_in_file, or write_file.",
                ExhaustionMessagePrefix: $"Slice worker '{agentKey}' exhausted",
                Brain: request.Brain,
                SliceOwnedPaths: slice.OwnedPaths,
                BatchOrchestrator: _orchestrator,
                ExplicitClient: resolvedClient),
            ct: ct);
    }

    private static string BuildSelfRepairPrompt(string originalSlicePrompt, EngineerTaskSlice slice, int attempt, string diagnosticContext)
    {
        var ownedNote = slice.OwnedPaths is { Length: > 0 } ? $"Strictly confine repair edits to owned slice path(s): {string.Join(", ", slice.OwnedPaths)}." : "Strictly confine repair edits within repository policy.";
        return string.Create(CultureInfo.InvariantCulture, $"{originalSlicePrompt}\n\n[Intra-Slice Roslyn Self-Repair — Attempt {attempt}/{MaxSelfRepairTurns}]\n{ownedNote}\n{diagnosticContext}\nFix the reported C# compiler error(s) so semantic/syntax diagnostics reach 0 errors.");
    }

    private static string BuildFrontierRepairPrompt(string originalSlicePrompt, EngineerTaskSlice slice, string localModelId, string diagnosticContext)
    {
        var ownedNote = slice.OwnedPaths is { Length: > 0 } ? $"Strictly confine repair edits to owned slice path(s): {string.Join(", ", slice.OwnedPaths)}." : "Strictly confine repair edits within repository policy.";
        return string.Create(CultureInfo.InvariantCulture, $"{originalSlicePrompt}\n\n[Intra-Slice Frontier Escalation — local model '{localModelId}' failed {MaxSelfRepairTurns} repair attempt(s)]\n{ownedNote}\n{diagnosticContext}\nFix the reported C# compiler error(s) so semantic/syntax diagnostics reach 0 errors.");
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
        if (totalSlices == 1 && request.TotalInstances == 1 && string.IsNullOrWhiteSpace(memoryDirective) && (slice.OwnedPaths is null || slice.OwnedPaths.Length == 0))
        {
            return request.Goal;
        }

        if (totalSlices == 1 && string.IsNullOrWhiteSpace(memoryDirective) && (slice.OwnedPaths is null || slice.OwnedPaths.Length == 0))
        {
            return $"{request.Goal}{strategyDirective}";
        }

        return $"{request.Goal}\n\n[Task {slice.SliceId}: {slice.Title}]\n{slice.Instructions}\nArchitect note: {plan.ArchitectureSummary}{ownershipDirective}{symbolHint}{strategyDirective}{memoryDirective}";
    }
}
