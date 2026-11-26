using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.AI.Agents.Context.Signals;
using IntoChat.OS.Modules.AI.Inference;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.AI.Agents;

// Shared interleaving run-execution state machine for ConversationNeuron and AgentNeuron:
// optimistic write-barrier and stale recovery (Fresh/Persist/Abandon/Committed), active-run lifecycle
// (admission, cancellation, timeout, secret masking, Close/Keep run retention), race-guarded context-interrupt
// Reconsider, history compaction helpers, and trial-turn capability policy enforcement.
internal abstract class InterleavingRunHost<TState>(
    ISecretRedactor redactor,
    TimeProvider clock,
    IServiceProvider services,
    ILogger logger)
    : AiNeuron<TState> where TState : class, new()
{
    protected const int KeptRuns = 64;

    private CancellationTokenSource? _active;
    private string? _cancelBeforeDrive;
    private bool _mutating;
    // The host's state as this activation holds it; the base's State is only what activation loaded.
    private TState _state = new();
    // Set when a write's outcome is unknown: the next call reloads the stored state and closes what it left open.
    private bool _stale;
    // Whether a listed context holds a trusted interrupt, and its text; every write stores the flag.
    private bool _blocked;
    private string? _interrupt;
    // Context signals interleave: only the latest recompute decides the outcome.
    private long _reconsidering;
    private TurnRunner? _runner;
    // Commands still see the in-flight reservation (for admission and cancellation), but
    // interleaving readers must not expose a version before its write has committed.
    private TState? _beforeWrite;
    private TaskCompletionSource? _write;

    protected new TState State => _state;
    protected TState Committed => _beforeWrite ?? _state;
    protected bool IsBlocked => _blocked;
    protected string? InterruptReason => _interrupt;
    protected bool IsActive => _active is not null;
    protected bool IsMutating => _mutating;
    protected string Scope => BrainKey.ScopeOf(FullKey) ?? Id.Brain.Value;
    protected IServiceProvider Services => services;
    protected TimeProvider Clock => clock;
    protected ILogger Logger => logger;
    protected TurnRunner Runner => _runner ??= new(Context, Id, redactor, clock, services, logger);

    protected abstract string HostKind { get; }
    protected abstract string BusyMessage { get; }

    protected abstract AgentRun? ActiveRunOf(TState state);
    protected abstract bool BlockedOf(TState state);
    protected abstract string[] ContextsOf(TState state);
    protected abstract TState WithInterruptedRun(TState state, AgentRun closedRun);
    protected abstract TState PrepareWrite(TState previous, TState next, bool blocked, bool bumpRevision);

    protected virtual Task OnStateLoaded(CancellationToken cancellationToken) => Task.CompletedTask;
    protected virtual Task OnRunInterrupted(AgentRun open, string error) => Task.CompletedTask;
    protected virtual Task OnBlockedChanged(bool blocked) => Task.CompletedTask;
    protected virtual Task BeforeStore(TState next) => Task.CompletedTask;
    protected virtual void OnStored(TState committed) { }
    protected virtual Task AfterCommit(TState committed, bool announce) => Task.CompletedTask;

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        _state = base.State;
        _blocked = BlockedOf(_state);
        await OnStateLoaded(cancellationToken);
        await Resume();
    }

    // A run the previous activation left open is interrupted: external tool outcomes may be unknown.
    protected async Task Resume()
    {
        if (ActiveRunOf(_state) is { } open)
        {
            const string Error = "Activation ended before the run committed; external tool outcomes may be unknown.";
            await Persist(WithInterruptedRun(_state, Close(open, AgentRunStatus.Interrupted, Error)));
            await OnRunInterrupted(open, Error);
        }
        // The stored flag is only a cache: the contexts may have changed while the host was away.
        await Reconsider();
    }

    [On]
    public Task OnContextUpdated(ContextUpdated signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return Reconsidered();
    }

    [On]
    public Task OnContextRemoved(ContextRemoved signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return Reconsidered();
    }

    private async Task Reconsidered()
    {
        await Fresh();
        await Reconsider();
    }

    // Re-reads every listed context; a newly found interrupt cancels the active run.
    protected async Task Reconsider()
    {
        var turn = ++_reconsidering;
        var interrupt = await Runner.FirstInterrupt(ContextsOf(_state));
        if (turn != _reconsidering) { return; }
        var blocked = interrupt is not null;
        var changed = blocked != _blocked;
        _blocked = blocked;
        _interrupt = interrupt?.Text;
        if (changed && blocked) { CancelActiveRun(); }
        // A write in flight or a run's own writes store the flag; only an idle host writes it here.
        if (BlockedOf(_state) != blocked && _active is null && !_mutating && _write is null && ActiveRunOf(_state) is null)
        {
            await Mutate(() => Persist(_state));
        }
        if (changed) { await OnBlockedChanged(blocked); }
    }

    // After a write whose outcome is unknown the stored state is the truth: reload it and close the run it left open.
    protected async Task Fresh()
    {
        if (!_stale || _active is not null || _mutating) { return; }
        _stale = false;
        _state = base.State;
        await Resume();
    }

    protected Task AwaitPendingWrite() => _write is { } pending ? pending.Task : Task.CompletedTask;

    protected async Task<TState> ReadCommittedAsync()
    {
        await Fresh();
        await AwaitPendingWrite();
        return Committed;
    }

    protected async Task EnsureContextWires(string[] contexts)
    {
        foreach (var ctxId in Runner.ContextIds(contexts))
        {
            await Context.Wires.Ensure<ContextUpdated>(ctxId);
            await Context.Wires.Ensure<ContextRemoved>(ctxId);
        }
    }

    protected void EnsureIdle(string? message = null)
    {
        if (_active is not null || _mutating || ActiveRunOf(_state) is not null)
        {
            throw new InvalidOperationException(message ?? BusyMessage);
        }
    }

    protected void EnsureUnblocked()
    {
        if (_blocked) { throw new InvalidOperationException("Blocked: " + _interrupt); }
    }

    protected void CancelActiveRun()
    {
        if (_active is { } active) { active.Cancel(); }
        // A started run whose driver has not picked it up yet is cancelled when it does.
        else if (ActiveRunOf(_state) is { } pending) { _cancelBeforeDrive = pending.RunId; }
    }

    protected async Task Mutate(Func<Task> action)
    {
        _mutating = true;
        try { await action(); }
        finally { _mutating = false; }
    }

    protected async Task<TResult> Mutate<TResult>(Func<Task<TResult>> action)
    {
        _mutating = true;
        try { return await action(); }
        finally { _mutating = false; }
    }

    protected void UpdateLocalState(Func<TState, TState> update) => _state = update(_state);

    protected void SetBlockedCache(bool blocked) => _blocked = blocked;

    protected ActiveRunScope BeginRun(string runId, TimeSpan timeoutDuration, CancellationToken callerToken = default)
    {
        // The timeout has its own source, so a run that ran out of time is told apart from a cancelled one.
        var timeout = new CancellationTokenSource(timeoutDuration);
        var lifetime = callerToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(callerToken, timeout.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        // Set before the first await, so an interleaved command sees this run.
        _active = lifetime;
        if (_cancelBeforeDrive == runId) { lifetime.Cancel(); }
        _cancelBeforeDrive = null;
        return new ActiveRunScope(this, timeout, lifetime, callerToken, $"The run exceeded its {timeoutDuration} timeout.");
    }

    protected async Task<ClosedRunFailure> FinalizeFailedRunAsync(
        ActiveRunScope active,
        TurnState turnState,
        ITurnSink sink,
        string runId,
        bool abandonedStream = false)
    {
        // Finally while yielding means the consumer stopped pulling: an abandoned stream is a cancelled run.
        var expired = !abandonedStream && active.IsTimedOut;
        var failure = turnState.Failure;
        // A recorded failure is authoritative; abandonment and the token only decide when none was recorded.
        var cancelled = !expired && (failure is not null ? failure.Cancelled : abandonedStream || active.IsCancellationRequested);
        var error = expired
            ? active.TimedOutMessage
            : failure?.Message ?? (abandonedStream ? "The response stream was abandoned." : "Execution failed before committing its result.");
        // The error and the text so far are masked; a redactor that fails leaves the held-back text and a generic error.
        try
        {
            await Runner.Refresh(turnState, final: true);
            await Runner.FlushShown(turnState, sink);
            error = await Runner.Mask(error);
        }
        catch (Exception maskError)
        {
            Logger.LogWarning(maskError, "{HostKind} run {RunId} could not mask its outcome.", HostKind, runId);
            error = "Execution failed before committing its result.";
        }

        var failureKind = expired
            ? AgentFailure.TimedOut
            : (cancelled ? AgentFailure.Unknown : (failure?.Failure ?? AgentFailure.Unknown));
        var closedRun = Close(
            turnState.Streamed,
            cancelled ? AgentRunStatus.Cancelled : AgentRunStatus.Failed,
            error,
            cancelled ? "tool_cancelled" : "tool_interrupted") with
        {
            Failure = failureKind,
        };
        return new ClosedRunFailure(closedRun, error, cancelled, failureKind);
    }

    protected async Task<bool> TryPersistClosedRunAsync(TState next, string runId)
    {
        try
        {
            await Persist(next);
            return true;
        }
        catch (Exception persistError)
        {
            Logger.LogWarning(persistError, "Failed to persist final {HostKind} run {RunId}.", HostKind, runId);
            Abandon(persistError, runId);
            return false;
        }
    }

    protected async Task UndoHandOffAsync(TState clearedState, string runId)
    {
        // The caller learns why the hand-off failed; a failure to undo it must not replace that.
        try { await Persist(clearedState); }
        catch (Exception undoError)
        {
            Logger.LogWarning(undoError, "Failed to clear active {HostKind} run {RunId} after hand-off failure.", HostKind, runId);
            Abandon(undoError, runId);
        }
    }

    // The stored state no longer matches what this activation holds; the next call reloads from storage and Resume closes it.
    protected void Abandon(Exception error, string runId)
    {
        Logger.LogError(error, "{HostKind} run {RunId} could not store its outcome; recovering from storage on next call.", HostKind, runId);
        _stale = true;
    }

    protected AgentRun Close(AgentRun run, AgentRunStatus status, string error, string openCallCode = "tool_interrupted") => run with
    {
        Status = status,
        EndedAt = Clock.GetUtcNow(),
        Error = error,
        ToolCalls = [.. run.ToolCalls.Select(call => call.Result is null && call.Code is null
            ? call with { Code = openCallCode, Error = "Execution ended before the tool call completed." } : call)],
    };

    protected static AgentRun[] Keep(AgentRun[] runs, AgentRun run)
        => [.. runs.Where(item => item.RunId != run.RunId).TakeLast(KeptRuns - 1), run];

    protected static (AiMessage[] Messages, string? Summary) BoundPreTurnHistory(
        AiMessage[] messages,
        AgentDefinition definition,
        AiMessage input,
        string? summary)
    {
        var preTurnMax = Math.Max(1, Math.Min(definition.MaxHistoryMessages, definition.CompactionThreshold) - 1);
        return AgentHistoryCompactor.BoundWindow(
            messages,
            definition with { MaxHistoryMessages = Math.Max(2, preTurnMax), CompactionThreshold = Math.Max(2, preTurnMax) },
            Math.Max(0, TurnRunner.MaxHistoryCharacters - AgentHistoryCompactor.MessageSize(input)),
            summary);
    }

    protected Task<(AiMessage[] Messages, string Summary, int EvictedCount)> CompactHistoryAsync(
        AiMessage[] messages,
        AgentDefinition definition,
        string? existingSummary,
        string? focusInstructions,
        bool force,
        CancellationToken ct)
        => AgentHistoryCompactor.CompactAsync(
            messages,
            definition,
            existingSummary,
            focusInstructions,
            force,
            Scope,
            Services,
            Logger,
            ct);

    protected static string FormatCompactReply(int previousCount, int evictedCount, int keptCount, string maskedSummary)
        => previousCount == 0
            ? "No conversation messages to compact yet."
            : $"Compacted {evictedCount} of {previousCount} message(s) ({keptCount} kept in active window).\n{maskedSummary}";

    // Neuron<TState>.Save always publishes a signal; interleaving hosts persist through Store directly
    // behind a write-barrier and publish signals after commit so a failed signal never undoes a write.
    protected async Task Persist(TState next, bool announce = true)
    {
        var previous = _state;
        var writing = _write = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _beforeWrite = previous;
        _state = PrepareWrite(previous, next, _blocked, announce);
        try
        {
            await BeforeStore(_state);
            await Store(_state);
            OnStored(_state);
        }
        catch
        {
            _state = previous;
            throw;
        }
        finally
        {
            _beforeWrite = null;
            _write = null;
            writing.TrySetResult();
        }

        await AfterCommit(_state, announce);
    }

    protected async Task EnforceTrialTurnPolicyAsync()
    {
        var policy = Services.GetService<IBrainCapabilityPolicy>();
        if (policy is not null)
        {
            var decision = await policy.EvaluateAsync(Scope, BrainCapability.AssistantTurn, FullKey);
            if (!decision.Allowed)
            {
                throw new ClaimRequiredException(decision);
            }
        }

        var registry = Services.GetService<IBrainResidencyRegistry>();
        if (registry is not null && registry.TryGetEphemeral(Scope, out var ephemeral) && !registry.TryConsumeTurn(Scope, out _))
        {
            throw new ClaimRequiredException(CapabilityDecision.RequireClaim(
                BrainCapability.AssistantTurn,
                $"You've reached the {ephemeral.MaxTrialTurns}-turn trial limit. Sign in to claim this workspace and continue your conversation without losing anything.",
                FullKey,
                CapabilityDecision.TrialTurnsExhaustedCode,
                remainingTurns: 0));
        }
    }

    protected readonly record struct ClosedRunFailure(AgentRun Run, string Error, bool Cancelled, AgentFailure FailureKind);

    protected sealed class ActiveRunScope(
        InterleavingRunHost<TState> host,
        CancellationTokenSource timeout,
        CancellationTokenSource lifetime,
        CancellationToken callerToken,
        string timedOutMessage) : IDisposable
    {
        public CancellationToken Token => lifetime.Token;
        public string TimedOutMessage => timedOutMessage;
        // Only the run's own time limit is a timeout; the caller's token and Cancel are cancellations.
        public bool IsTimedOut => timeout.IsCancellationRequested && !callerToken.IsCancellationRequested;
        public bool IsCancellationRequested => lifetime.IsCancellationRequested;

        public AgentTurnEvent.TurnFailed NormalizeFailure(AgentTurnEvent.TurnFailed failed)
            => failed.Cancelled && IsTimedOut
                ? new(timedOutMessage, Cancelled: false) { Failure = AgentFailure.TimedOut }
                : failed;

        public void Dispose()
        {
            host._active = null;
            lifetime.Dispose();
            timeout.Dispose();
        }
    }
}
