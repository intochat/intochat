using System.Globalization;
using System.Text;
using DigitalBrain;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Components;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps.Flows;

[GenerateSerializer, Alias("loop.finding-failure-count")]
internal sealed record LoopFindingFailureCount(
    [property: Id(0)] string FindingKey,
    [property: Id(1)] int FailureCount)
{
    public LoopFindingFailureCount() : this("", 0) { }
}

[GenerateSerializer, Alias("loop.self-improvement-state")]
internal sealed record SelfImprovementLoopState(
    [property: Id(0)] bool Paused,
    [property: Id(1)] LoopBudget Budget,
    [property: Id(2)] string CurrentDayUtc,
    [property: Id(3)] int RunsToday,
    [property: Id(4)] LoopFindingFailureCount[] FailuresByFinding,
    [property: Id(5)] string[] ParkedFindingKeys,
    [property: Id(6)] string[] AdmittedFindingKeys,
    [property: Id(7)] LoopRunRecord[] Runs)
{
    public const int MaxRetainedRuns = 200;

    public SelfImprovementLoopState()
        : this(false, new LoopBudget(), "", 0, [], [], [], [])
    {
    }
}

[Neuron("loop.self-improvement")]
internal sealed partial class SelfImprovementLoop(
    IServiceProvider? services = null,
    IEngineerRuns? engineerRunsOverride = null,
    IWorkingMemory? workingMemoryOverride = null,
    ILogger<SelfImprovementLoop>? logger = null) : Neuron<SelfImprovementLoopState>, ISelfImprovementLoop
{
    private const string StatusReady = "ready";
    private const string StatusPaused = "paused";
    private const string StatusKilled = "killed";
    private const string ReservedFlowIdPrefix = "loop-res-";

    [Slot]
    private partial IFlows Flows { get; }

    [Slot(Key = "confirmations")]
    private partial IConfirmationInbox Approval { get; }

    // Where the loop keeps its findings: a storage connection the installer binds; it has no default, so an unbound loop is
    // refused at activation. Status does not open it.
    [Slot]
    private partial IStorage FindingsLog { get; }

    [Slot(Key = IEngineerRuns.DefaultKey)]
    private partial IEngineerRuns EngineerRunsSlot { get; }

    public Task<string> Status(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _ = Flows;
        _ = Approval;
        return Task.FromResult(ComputeStatus(State));
    }

    public Task<LoopStateSnapshot> Inspect(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var normalized = RolloverDayIfNeeded(State, CurrentUtcDay());
        return Task.FromResult(ToSnapshot(normalized));
    }

    public async Task<LoopStateSnapshot> ConfigureBudget(LoopBudget budget, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ct.ThrowIfCancellationRequested();

        if (budget.RunsPerDay < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(budget), "RunsPerDay must be >= 0.");
        }

        if (budget.TokensPerRun < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(budget), "TokensPerRun must be >= 0.");
        }

        if (budget.AttemptsPerFinding <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(budget), "AttemptsPerFinding must be >= 1.");
        }

        var next = RolloverDayIfNeeded(State, CurrentUtcDay()) with { Budget = budget };
        await PersistAsync(next).ConfigureAwait(true);
        return ToSnapshot(next);
    }

    public async Task<LoopStateSnapshot> SetPaused(bool paused, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var next = RolloverDayIfNeeded(State, CurrentUtcDay()) with { Paused = paused };
        await PersistAsync(next).ConfigureAwait(true);
        return ToSnapshot(next);
    }

    public async Task<LoopCycleResult> RunCycle(LoopCycleRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var now = Context.Time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var state = RolloverDayIfNeeded(State, today);

        // Fail-closed: kill switch and pause gates refuse before inspecting findings or dispatching runs.
        if (state.Budget.KillSwitch)
        {
            return new LoopCycleResult(
                Dispatched: false,
                Status: LoopBudgetExceededException.KillSwitchCode,
                Reason: "Autonomous loop execution is disabled by the kill switch.",
                ActiveRuns: CountActiveRuns(state.Runs),
                RunsToday: state.RunsToday);
        }

        if (state.Paused)
        {
            return new LoopCycleResult(
                Dispatched: false,
                Status: StatusPaused,
                Reason: "Self-improvement loop is paused.",
                ActiveRuns: CountActiveRuns(state.Runs),
                RunsToday: state.RunsToday);
        }

        // Reconcile any active runs first so concurrency and failure budgets reflect real flow states.
        var (reconciledState, changedDuringReconcile) = await ReconcileActiveRunsCoreAsync(state, now, ct).ConfigureAwait(true);
        state = reconciledState;

        var activeRuns = CountActiveRuns(state.Runs);
        var maxConcurrent = Math.Max(1, request.MaxConcurrentRuns);
        if (activeRuns >= maxConcurrent)
        {
            if (changedDuringReconcile)
            {
                await PersistAsync(state).ConfigureAwait(true);
            }

            return new LoopCycleResult(
                Dispatched: false,
                Status: "loop.concurrency-cap",
                Reason: string.Create(
                    CultureInfo.InvariantCulture,
                    $"Active self-improvement runs ({activeRuns}) reached concurrency cap ({maxConcurrent})."),
                ActiveRuns: activeRuns,
                RunsToday: state.RunsToday);
        }

        var candidates = CollectAndPrioritizeFindings(request);
        if (candidates.Count == 0)
        {
            if (changedDuringReconcile)
            {
                await PersistAsync(state).ConfigureAwait(true);
            }

            return new LoopCycleResult(
                Dispatched: false,
                Status: "no-findings",
                Reason: "No actionable findings were supplied for this loop cycle.",
                CandidatesEvaluated: 0,
                ActiveRuns: activeRuns,
                RunsToday: state.RunsToday);
        }

        var tracker = BuildBudgetTracker(state, today);
        var inFlightKeys = new HashSet<string>(
            state.Runs.Where(IsActiveRun).Select(static r => r.FindingKey),
            StringComparer.OrdinalIgnoreCase);
        var completedKeys = new HashSet<string>(
            state.AdmittedFindingKeys ?? [],
            StringComparer.OrdinalIgnoreCase);

        LoopFinding? selected = null;
        LoopBudgetExceededException? lastBudgetRefusal = null;

        foreach (var candidate in candidates)
        {
            if (inFlightKeys.Contains(candidate.Key) || completedKeys.Contains(candidate.Key))
            {
                continue;
            }

            try
            {
                tracker.EnsureCanStartRun(candidate.Key);
                selected = candidate;
                break;
            }
            catch (LoopBudgetExceededException ex)
            {
                lastBudgetRefusal = ex;
                if (string.Equals(ex.Code, LoopBudgetExceededException.DailyBudgetCode, StringComparison.Ordinal)
                    || string.Equals(ex.Code, LoopBudgetExceededException.KillSwitchCode, StringComparison.Ordinal))
                {
                    break;
                }
            }
        }

        if (selected is null)
        {
            if (changedDuringReconcile)
            {
                await PersistAsync(state).ConfigureAwait(true);
            }

            if (lastBudgetRefusal is not null)
            {
                return new LoopCycleResult(
                    Dispatched: false,
                    Status: lastBudgetRefusal.Code,
                    Reason: lastBudgetRefusal.Message,
                    CandidatesEvaluated: candidates.Count,
                    ActiveRuns: activeRuns,
                    RunsToday: state.RunsToday);
            }

            return new LoopCycleResult(
                Dispatched: false,
                Status: "no-eligible-findings",
                Reason: "All candidate findings are already in flight, completed, or suppressed.",
                CandidatesEvaluated: candidates.Count,
                ActiveRuns: activeRuns,
                RunsToday: state.RunsToday);
        }

        // Retrieve prior lessons for this finding and inject them into the engineer run prompt.
        var memory = ResolveWorkingMemoryOnGrainTurn();
        LoopLessonContext lessonContext = new(null, string.Empty);
        if (memory is not null)
        {
            try
            {
                lessonContext = await LoopLessons.FindRelevantLessonsAsync(memory, selected, ct).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger().LogSwallowed(ex, $"retrieving LoopLessons for finding '{selected.Key}'");
            }
        }

        // Charge daily run budget and reserve the active run fail-closed BEFORE dispatching so crashes/timeouts cannot bypass caps.
        tracker.RecordRunStarted(selected.Key);
        var nextRunsToday = state.RunsToday + 1;
        var runId = $"loop-{Guid.NewGuid().ToString("N")[..10]}";
        var reservedFlowId = $"{ReservedFlowIdPrefix}{runId}";
        var repoSlug = string.IsNullOrWhiteSpace(request.RepoSlug) ? "os" : request.RepoSlug.Trim();
        var goal = BuildEngineerGoal(selected, lessonContext);

        var reservedRecord = new LoopRunRecord(
            FindingKey: selected.Key,
            FindingTitle: selected.Title,
            Source: selected.Source,
            FlowId: reservedFlowId,
            RunId: runId,
            Status: "running",
            StartedAt: now,
            CompletedAt: null,
            OutcomeDetail: null,
            LessonKey: lessonContext.PrimaryLessonKey,
            TokensUsed: 0);

        state = state with
        {
            RunsToday = nextRunsToday,
            Runs = AppendBoundedRun(state.Runs, reservedRecord),
        };
        await PersistAsync(state).ConfigureAwait(true);

        var delegationRequest = new EngineerDelegationRequest(
            RepoSlug: repoSlug,
            Goal: goal,
            ParallelInstances: 1,
            Backend: string.IsNullOrWhiteSpace(request.Backend) ? "LocalGit" : request.Backend.Trim(),
            RequestedBaseBranch: request.RequestedBaseBranch,
            RunId: runId,
            RequirePlanRatification: false,
            Harness: request.Harness);

        var engineerRuns = ResolveEngineerRuns();
        EngineerRunSummary summary;
        try
        {
            summary = await engineerRuns.StartEngineerRun(delegationRequest, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            var cancelledRecord = reservedRecord with
            {
                Status = "cancelled",
                CompletedAt = Context.Time.GetUtcNow(),
                OutcomeDetail = "Dispatch cancelled before engineer run settled.",
            };
            state = ReplaceReservedRun(state, runId, cancelledRecord);
            await PersistAsync(state).ConfigureAwait(true);
            throw;
        }
        catch (Exception ex)
        {
            var failedAt = Context.Time.GetUtcNow();
            var failedRecord = reservedRecord with
            {
                Status = "failed",
                CompletedAt = failedAt,
                OutcomeDetail = ex.Message,
            };
            state = ReplaceReservedRun(state, runId, failedRecord);
            state = ApplyFindingFailure(state, selected.Key);
            await TryRecordFailureLessonAsync(memory, selected.Key, selected.Title, ex.Message, ct).ConfigureAwait(true);
            await PersistAsync(state).ConfigureAwait(true);
            throw;
        }

        var effectiveFlowId = string.IsNullOrWhiteSpace(summary.FlowId) ? runId : summary.FlowId;
        var effectiveRunId = string.IsNullOrWhiteSpace(summary.RunId) ? runId : summary.RunId;

        var runStatus = MapFlowStatus(summary.Ok, summary.Status);
        var completedAt = runStatus is "completed" or "failed" ? Context.Time.GetUtcNow() : (DateTimeOffset?)null;
        var outcomeDetail = runStatus is "completed" or "failed" ? summary.SummaryText : null;

        var runRecord = reservedRecord with
        {
            FlowId = effectiveFlowId,
            RunId = effectiveRunId,
            Status = runStatus,
            CompletedAt = completedAt,
            OutcomeDetail = outcomeDetail,
        };

        state = ReplaceReservedRun(state, runId, runRecord);

        if (string.Equals(runStatus, "failed", StringComparison.Ordinal))
        {
            state = ApplyFindingFailure(state, selected.Key);
            await TryRecordFailureLessonAsync(
                memory,
                selected.Key,
                selected.Title,
                summary.SummaryText,
                ct).ConfigureAwait(true);
        }
        else if (string.Equals(runStatus, "completed", StringComparison.Ordinal))
        {
            state = ApplyFindingCompleted(state, selected.Key);
            await TryRecordSuccessLessonAsync(
                memory,
                selected.Key,
                summary.SummaryText,
                ct).ConfigureAwait(true);
        }

        await PersistAsync(state).ConfigureAwait(true);

        return new LoopCycleResult(
            Dispatched: true,
            Status: runStatus is "running" or "waiting" ? "dispatched" : runStatus,
            Reason: string.Create(
                CultureInfo.InvariantCulture,
                $"Dispatched engineer run '{effectiveRunId}' ({effectiveFlowId}) for finding '{selected.Key}' (status={runStatus})."),
            FindingKey: selected.Key,
            FlowId: effectiveFlowId,
            RunId: effectiveRunId,
            InjectedLessonKey: lessonContext.PrimaryLessonKey,
            CandidatesEvaluated: candidates.Count,
            ActiveRuns: CountActiveRuns(state.Runs),
            RunsToday: state.RunsToday);
    }

    public async Task<LoopRunRecord?> RecordOutcome(
        string findingKey,
        string flowId,
        bool succeeded,
        string? detail = null,
        long tokensUsed = 0,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(findingKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowId);
        ct.ThrowIfCancellationRequested();

        var now = Context.Time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var state = RolloverDayIfNeeded(State, today);
        var cleanKey = findingKey.Trim();
        var cleanFlowId = flowId.Trim();

        var existingIndex = Array.FindLastIndex(
            state.Runs,
            r => string.Equals(r.FlowId, cleanFlowId, StringComparison.OrdinalIgnoreCase));

        // Idempotency: an already-terminal run must not double-count failures, prematurely park findings, or double-add tokens.
        if (existingIndex >= 0 && !IsActiveRun(state.Runs[existingIndex]))
        {
            return state.Runs[existingIndex];
        }

        var effectiveDetail = string.IsNullOrWhiteSpace(detail)
            ? (succeeded ? "Engineer run completed." : "Engineer run failed.")
            : detail.Trim();

        var effectiveSucceeded = succeeded;
        if (tokensUsed > 0)
        {
            var tracker = BuildBudgetTracker(state, today);
            try
            {
                tracker.RecordTokens(cleanFlowId, new InferenceUsage(null, null, tokensUsed));
            }
            catch (LoopBudgetExceededException ex)
            {
                effectiveSucceeded = false;
                effectiveDetail = $"{effectiveDetail} [{ex.Code}: {ex.Message}]";
            }
        }

        LoopRunRecord updatedRecord;
        if (existingIndex >= 0)
        {
            var existing = state.Runs[existingIndex];
            updatedRecord = existing with
            {
                Status = effectiveSucceeded ? "completed" : "failed",
                CompletedAt = now,
                OutcomeDetail = effectiveDetail,
                TokensUsed = Math.Max(0, existing.TokensUsed + Math.Max(0, tokensUsed)),
            };
            var updatedRuns = state.Runs.ToArray();
            updatedRuns[existingIndex] = updatedRecord;
            state = state with { Runs = updatedRuns };
        }
        else
        {
            updatedRecord = new LoopRunRecord(
                FindingKey: cleanKey,
                FindingTitle: cleanKey,
                Source: "manual",
                FlowId: cleanFlowId,
                RunId: cleanFlowId,
                Status: effectiveSucceeded ? "completed" : "failed",
                StartedAt: now,
                CompletedAt: now,
                OutcomeDetail: effectiveDetail,
                LessonKey: null,
                TokensUsed: Math.Max(0, tokensUsed));
            state = state with { Runs = AppendBoundedRun(state.Runs, updatedRecord) };
        }

        var memory = ResolveWorkingMemoryOnGrainTurn();
        if (effectiveSucceeded)
        {
            state = ApplyFindingCompleted(state, cleanKey);
            await TryRecordSuccessLessonAsync(memory, cleanKey, effectiveDetail, ct).ConfigureAwait(true);
        }
        else
        {
            state = ApplyFindingFailure(state, cleanKey);
            await TryRecordFailureLessonAsync(memory, cleanKey, updatedRecord.FindingTitle, effectiveDetail, ct).ConfigureAwait(true);
        }

        await PersistAsync(state).ConfigureAwait(true);
        return updatedRecord;
    }

    public async Task<LoopRunRecord[]> ReconcileActiveRuns(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var now = Context.Time.GetUtcNow();
        var state = RolloverDayIfNeeded(State, DateOnly.FromDateTime(now.UtcDateTime));
        var (reconciled, changed) = await ReconcileActiveRunsCoreAsync(state, now, ct).ConfigureAwait(true);
        if (changed)
        {
            await PersistAsync(reconciled).ConfigureAwait(true);
        }

        return reconciled.Runs;
    }

    private async Task<(SelfImprovementLoopState State, bool Changed)> ReconcileActiveRunsCoreAsync(
        SelfImprovementLoopState state,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (state.Runs.Length == 0 || !state.Runs.Any(IsActiveRun))
        {
            return (state, false);
        }

        var engineerRuns = ResolveEngineerRuns();
        var memory = ResolveWorkingMemoryOnGrainTurn();
        var updatedRuns = state.Runs.ToArray();
        var nextState = state;
        var changed = false;

        for (var i = 0; i < updatedRuns.Length; i++)
        {
            var run = updatedRuns[i];
            if (!IsActiveRun(run) || string.IsNullOrWhiteSpace(run.FlowId))
            {
                continue;
            }

            try
            {
                // Orphaned synthetic pre-dispatch reservation that never received a real EngineerFlow id: fail it closed so ActiveRunCount never leaks.
                if (run.FlowId.StartsWith(ReservedFlowIdPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    changed = true;
                    updatedRuns[i] = run with
                    {
                        Status = "failed",
                        CompletedAt = now,
                        OutcomeDetail = "Orphaned pre-dispatch reservation never settled to an engineer flow.",
                    };
                    continue;
                }

                var summary = await engineerRuns.InspectEngineerRun(run.FlowId, ct).ConfigureAwait(true);

                // Guard transient lookup/RPC errors so an active run is not prematurely marked failed and charged an attempt.
                if (!summary.Ok
                    && (summary.Status is not (FlowStatus.Failed or FlowStatus.Cancelled)
                        || summary.SummaryText?.StartsWith("Could not inspect", StringComparison.OrdinalIgnoreCase) == true))
                {
                    continue;
                }

                var mappedStatus = MapFlowStatus(summary.Ok, summary.Status);
                if (string.Equals(mappedStatus, run.Status, StringComparison.Ordinal))
                {
                    continue;
                }

                changed = true;
                var completedAt = mappedStatus is "completed" or "failed" ? now : run.CompletedAt;
                updatedRuns[i] = run with
                {
                    Status = mappedStatus,
                    CompletedAt = completedAt,
                    OutcomeDetail = summary.SummaryText,
                };

                if (string.Equals(mappedStatus, "failed", StringComparison.Ordinal))
                {
                    nextState = ApplyFindingFailure(nextState, run.FindingKey);
                    await TryRecordFailureLessonAsync(memory, run.FindingKey, run.FindingTitle, summary.SummaryText, ct).ConfigureAwait(true);
                }
                else if (string.Equals(mappedStatus, "completed", StringComparison.Ordinal))
                {
                    nextState = ApplyFindingCompleted(nextState, run.FindingKey);
                    await TryRecordSuccessLessonAsync(memory, run.FindingKey, summary.SummaryText, ct).ConfigureAwait(true);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger().LogSwallowed(ex, $"reconciling SelfImprovementLoop run '{run.FlowId}'");
            }
        }

        return (nextState with { Runs = updatedRuns }, changed);
    }

    private static IReadOnlyList<LoopFinding> CollectAndPrioritizeFindings(LoopCycleRequest request)
    {
        var all = new List<LoopFinding>();
        if (request.FailingFacts is { Length: > 0 })
        {
            var factTuples = request.FailingFacts
                .Where(static f => f is not null && !string.IsNullOrWhiteSpace(f.FactName))
                .Select(static f => (f.FactName, f.Detail))
                .ToArray();
            all.AddRange(LoopFindingSources.FromFailingFacts(factTuples));
        }

        if (request.TraceSpans is { Length: > 0 })
        {
            all.AddRange(LoopFindingSources.FromErrorTraces(request.TraceSpans));
        }

        if (!string.IsNullOrWhiteSpace(request.OwedMarkdown))
        {
            all.AddRange(LoopFindingSources.FromOwedMarkdown(request.OwedMarkdown));
        }

        if (request.Findings is { Length: > 0 })
        {
            foreach (var finding in request.Findings)
            {
                if (finding is not null && !string.IsNullOrWhiteSpace(finding.Key))
                {
                    all.Add(finding);
                }
            }
        }

        var deduplicator = new FindingDeduplicator();
        var admitted = deduplicator.Admit(all);
        return admitted
            .Select(static (finding, index) => (finding, Priority: PriorityOf(finding.Source), index))
            .OrderBy(static x => x.Priority)
            .ThenBy(static x => x.index)
            .Select(static x => x.finding)
            .ToArray();
    }

    private static int PriorityOf(string? source) => source?.Trim().ToLowerInvariant() switch
    {
        "fact" => 0,
        "trace" => 1,
        "owed" => 2,
        _ => 3,
    };

    private static string BuildEngineerGoal(LoopFinding finding, LoopLessonContext lessonContext)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"[SelfImprovementLoop · {finding.Source} · {finding.Key}] {finding.Title}");
        if (!string.IsNullOrWhiteSpace(finding.Detail))
        {
            sb.AppendLine();
            sb.Append("Detail: ").Append(finding.Detail.Trim());
        }

        if (!string.IsNullOrWhiteSpace(lessonContext.PromptSection))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append(lessonContext.PromptSection);
        }

        return sb.ToString();
    }

    private LoopBudgetTracker BuildBudgetTracker(SelfImprovementLoopState state, DateOnly today)
    {
        var day = ParseUtcDayOrDefault(state.CurrentDayUtc, today);
        var failures = (state.FailuresByFinding ?? [])
            .Select(static f => new KeyValuePair<string, int>(f.FindingKey, f.FailureCount));
        var tokens = (state.Runs ?? [])
            .Where(static r => !string.IsNullOrWhiteSpace(r.FlowId) && r.TokensUsed > 0)
            .GroupBy(static r => r.FlowId, StringComparer.Ordinal)
            .Select(static g => new KeyValuePair<string, long>(g.Key, g.Sum(static r => r.TokensUsed)));

        return LoopBudgetTracker.Restore(
            state.Budget ?? new LoopBudget(),
            day,
            state.RunsToday,
            failures,
            state.ParkedFindingKeys,
            tokens,
            Context.Time);
    }

    private static SelfImprovementLoopState ApplyFindingFailure(SelfImprovementLoopState state, string findingKey)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in state.FailuresByFinding ?? [])
        {
            counts[entry.FindingKey] = entry.FailureCount;
        }

        var nextFailures = counts.GetValueOrDefault(findingKey) + 1;
        counts[findingKey] = nextFailures;

        var parked = new HashSet<string>(state.ParkedFindingKeys ?? [], StringComparer.OrdinalIgnoreCase);
        var maxAttempts = Math.Max(1, (state.Budget ?? new LoopBudget()).AttemptsPerFinding);
        if (nextFailures >= maxAttempts)
        {
            parked.Add(findingKey);
        }

        return state with
        {
            FailuresByFinding =
            [
                .. counts
                    .OrderBy(static kv => kv.Key, StringComparer.Ordinal)
                    .Select(static kv => new LoopFindingFailureCount(kv.Key, kv.Value)),
            ],
            ParkedFindingKeys = [.. parked.Order(StringComparer.Ordinal)],
        };
    }

    private static SelfImprovementLoopState ApplyFindingCompleted(SelfImprovementLoopState state, string findingKey)
    {
        var admitted = new HashSet<string>(state.AdmittedFindingKeys ?? [], StringComparer.OrdinalIgnoreCase)
        {
            findingKey,
        };
        return state with
        {
            AdmittedFindingKeys = [.. admitted.Order(StringComparer.Ordinal)],
        };
    }

    private static SelfImprovementLoopState ReplaceReservedRun(
        SelfImprovementLoopState state,
        string reservedRunId,
        LoopRunRecord replacement)
    {
        var idx = Array.FindLastIndex(
            state.Runs,
            r => string.Equals(r.RunId, reservedRunId, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        {
            return state with { Runs = AppendBoundedRun(state.Runs, replacement) };
        }

        var updated = state.Runs.ToArray();
        updated[idx] = replacement;
        return state with { Runs = updated };
    }

    private static LoopRunRecord[] AppendBoundedRun(LoopRunRecord[] existing, LoopRunRecord next)
    {
        var list = new List<LoopRunRecord>((existing?.Length ?? 0) + 1);
        if (existing is { Length: > 0 })
        {
            list.AddRange(existing);
        }

        list.Add(next);
        if (list.Count > SelfImprovementLoopState.MaxRetainedRuns)
        {
            list.RemoveRange(0, list.Count - SelfImprovementLoopState.MaxRetainedRuns);
        }

        return [.. list];
    }

    private static SelfImprovementLoopState RolloverDayIfNeeded(SelfImprovementLoopState state, DateOnly today)
    {
        var todayText = today.ToString("O", CultureInfo.InvariantCulture);
        if (string.Equals(state.CurrentDayUtc, todayText, StringComparison.Ordinal))
        {
            return state;
        }

        return state with
        {
            CurrentDayUtc = todayText,
            RunsToday = 0,
        };
    }

    private DateOnly CurrentUtcDay() => DateOnly.FromDateTime(Context.Time.GetUtcNow().UtcDateTime);

    private static DateOnly ParseUtcDayOrDefault(string? raw, DateOnly fallback)
        => !string.IsNullOrWhiteSpace(raw) && DateOnly.TryParseExact(raw, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : fallback;

    private static string ComputeStatus(SelfImprovementLoopState state)
    {
        if (state.Budget?.KillSwitch == true)
        {
            return StatusKilled;
        }

        return state.Paused ? StatusPaused : StatusReady;
    }

    private static LoopStateSnapshot ToSnapshot(SelfImprovementLoopState state) => new(
        Status: ComputeStatus(state),
        Paused: state.Paused,
        Budget: state.Budget ?? new LoopBudget(),
        RunsToday: state.RunsToday,
        ActiveRunCount: CountActiveRuns(state.Runs),
        ParkedFindingKeys: state.ParkedFindingKeys ?? [],
        AdmittedFindingKeys: state.AdmittedFindingKeys ?? [],
        RecentRuns: state.Runs ?? []);

    private static bool IsActiveRun(LoopRunRecord run)
        => string.Equals(run.Status, "running", StringComparison.OrdinalIgnoreCase)
            || string.Equals(run.Status, "waiting", StringComparison.OrdinalIgnoreCase);

    private static int CountActiveRuns(LoopRunRecord[]? runs)
        => runs?.Count(IsActiveRun) ?? 0;

    private static string MapFlowStatus(bool ok, FlowStatus flowStatus)
    {
        if (!ok || flowStatus is FlowStatus.Failed or FlowStatus.Cancelled)
        {
            return "failed";
        }

        return flowStatus switch
        {
            FlowStatus.Completed => "completed",
            FlowStatus.Waiting => "waiting",
            _ => "running",
        };
    }

    private IEngineerRuns ResolveEngineerRuns()
        => engineerRunsOverride
            ?? services?.GetService<IEngineerRuns>()
            ?? EngineerRunsSlot;

    private IWorkingMemory? ResolveWorkingMemoryOnGrainTurn()
    {
        if (workingMemoryOverride is not null)
        {
            return workingMemoryOverride;
        }

        if (services?.GetService<IWorkingMemory>() is { } injected)
        {
            return injected;
        }

        try
        {
            var wmId = IWorkingMemory.NeuronIdOf(Id.Brain);
            if (Implements<IWorkingMemory>(wmId))
            {
                return Context.Get<IWorkingMemory>(wmId);
            }
        }
        catch (InvalidOperationException ex)
        {
            Logger().LogDebug(ex, "IWorkingMemory unavailable in detached SelfImprovementLoop invocation.");
        }

        return null;
    }

    private async Task TryRecordFailureLessonAsync(
        IWorkingMemory? memory,
        string findingKey,
        string whatWasTried,
        string? whyItFailed,
        CancellationToken ct)
    {
        if (memory is null)
        {
            return;
        }

        try
        {
            var reason = string.IsNullOrWhiteSpace(whyItFailed) ? "Engineer run failed." : whyItFailed;
            await LoopLessons.RecordFailureAsync(memory, findingKey, whatWasTried, reason, ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger().LogSwallowed(ex, $"recording failure LoopLesson for '{findingKey}'");
        }
    }

    private async Task TryRecordSuccessLessonAsync(
        IWorkingMemory? memory,
        string findingKey,
        string? whatWorked,
        CancellationToken ct)
    {
        if (memory is null)
        {
            return;
        }

        try
        {
            var detail = string.IsNullOrWhiteSpace(whatWorked) ? "Engineer run completed." : whatWorked;
            await LoopLessons.RecordSuccessAsync(memory, findingKey, detail, ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger().LogSwallowed(ex, $"recording success LoopLesson for '{findingKey}'");
        }
    }

    private Task PersistAsync(SelfImprovementLoopState next)
        => Save(next, new SelfImprovementLoopChanged(ComputeStatus(next), next.RunsToday));

    private ILogger Logger() => (ILogger?)logger ?? OsConsoleLog.For<SelfImprovementLoop>();
}
