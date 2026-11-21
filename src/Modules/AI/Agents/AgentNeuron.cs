using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Neurons;
using DigitalBrain.State;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Agents.Context.Signals;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.AI.Agents;

// The agent's record as plain JSON state (06-06d; it keeps its state name "agent", clean break under SAFE-01). Every message
// held here is built from concrete arrays.
[ExportableState]
internal sealed record AgentStorage
{
    public long Revision { get; init; }
    public AgentDefinition Definition { get; init; } = new();
    public AiMessage[] Transcript { get; init; } = [];
    public string? Summary { get; init; }
    public AgentRun? ActiveRun { get; init; }
    public AgentRun[] Runs { get; init; } = [];
    public long Version { get; init; }
    // A cache of whether a listed context holds a trusted interrupt; activation recomputes it from the contexts.
    public bool Blocked { get; init; }
}

// A plain neuron since 06-06d. Every call interleaves (the contract says so), as the grain's did: a turn streams for minutes while
// Read, Cancel and Post reach the agent. The pending write (_beforeWrite, _write) keeps interleaving readers on committed versions;
// most writes publish nothing, so Persist saves through the runtime state directly and signals follow with Publish.
[DigitalBrain.Neurons.Neuron("agent", State = "agent")]
internal sealed class AgentNeuron(
    ISecretRedactor redactor,
    TimeProvider clock,
    IServiceProvider services,
    ILogger<AgentNeuron>? logger = null,
    AgentWrites? writes = null)
    : AiNeuron<AgentStorage>, IAgent, IAgentDriver
{
    private const int KeptRuns = 64;
    private CancellationTokenSource? _active;
    private string? _cancelBeforeDrive;
    private bool _mutating;
    // The agent's state as this activation holds it; the base's State is only what activation loaded.
    private AgentStorage _state = new();
    // Set when a write's outcome is unknown: the next call reloads the stored state and closes what it left open.
    private bool _stale;
    // Whether a listed context holds a trusted interrupt, and its text; every write stores the flag.
    private bool _blocked;
    private string? _interrupt;
    // Context signals interleave: only the latest recompute decides the outcome.
    private long _reconsidering;
    private TurnRunner? _runner;
    private new AgentStorage State => _state;
    // Commands still see the in-flight reservation (for admission and cancellation), but
    // interleaving readers must not expose a version before its write has committed.
    private AgentStorage? _beforeWrite;
    private TaskCompletionSource? _write;
    private AgentStorage Committed => _beforeWrite ?? State;
    private string Key => FullKey;
    // The brain the access check sees is the tools' and the meter's.
    private string Scope => BrainKey.ScopeOf(Key) ?? Key;
    private ILogger Logger => (ILogger?)logger ?? OsConsoleLog.For<AgentNeuron>();
    private TimeProvider Clock => clock;
    private TurnRunner Runner => _runner ??= new(Context, Id, redactor, clock, services, Logger);

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        _state = base.State;
        _blocked = _state.Blocked;
        await Resume();
    }

    // A run the previous activation left open is interrupted: external tool outcomes may be unknown.
    private async Task Resume()
    {
        if (State.ActiveRun is { } open)
        {
            const string Error = "Activation ended before the run committed; external tool outcomes may be unknown.";
            await Persist(State with { ActiveRun = null, Runs = Keep(State.Runs, Close(open, AgentRunStatus.Interrupted, Error)) });
            // Announced by a one-way call to the agent itself, so the request that woke the agent (a subscription, a
            // late Drive) is served first and a Start caller still gets the run's end.
            await Context.Get<IAgentDriver>(Id).Interrupted(open.RunId, Error);
        }
        // The stored flag is only a cache: the contexts may have changed while the agent was away.
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
    private async Task Reconsider()
    {
        var turn = ++_reconsidering;
        var interrupt = await Runner.FirstInterrupt(State.Definition.Contexts);
        if (turn != _reconsidering) { return; }
        var blocked = interrupt is not null;
        var changed = blocked != _blocked;
        _blocked = blocked;
        _interrupt = interrupt?.Text;
        if (changed && blocked) { await Cancel(); }
        // A write in flight or a run's own writes store the flag; only an idle agent writes it here.
        if (State.Blocked != blocked && _active is null && !_mutating && _write is null && State.ActiveRun is null)
        {
            _mutating = true;
            try { await Persist(State); }
            finally { _mutating = false; }
        }
        if (changed) { await Notify(new AgentBlocked(Key, blocked)); }
    }

    private void EnsureUnblocked()
    {
        if (_blocked) { throw new InvalidOperationException("Blocked: " + _interrupt); }
    }

    // After a write whose outcome is unknown the stored state is the truth: reload it and close the run it left open. The grain
    // deactivated for this; a plain activation reloads in place on its next call.
    private async Task Fresh()
    {
        if (!_stale || _active is not null || _mutating) { return; }
        _stale = false;
        _state = base.State;
        await Resume();
    }

    public async Task Interrupted(string runId, string error) => await Notify(new TurnFailed(Key, runId, await Runner.Mask(error), Cancelled: false));

    public async Task Configure(AgentDefinition definition, long expectedRevision)
    {
        await Fresh();
        EnsureIdle();
        ArgumentNullException.ThrowIfNull(definition);
        if (State.Revision != expectedRevision) { throw new InvalidOperationException("Agent revision conflict."); }
        if (definition.MaxModelCalls is < 1 or > 512 || definition.Timeout <= TimeSpan.Zero || definition.Timeout > TimeSpan.FromMinutes(60)
            || definition.MaxHistoryMessages is < 2 or > 4096
            || definition.CompactionThreshold is < 2 or > 4096
            || definition.CompactionRecentWindow is < 1 or > 4096) { throw new ArgumentException("Invalid agent execution limits.", nameof(definition)); }
        if (definition.Instructions.Length > 32000 || definition.Contracts.Length > 128 || definition.Contracts.Any(string.IsNullOrWhiteSpace)
            || definition.Contracts.Distinct(StringComparer.Ordinal).Count() != definition.Contracts.Length
            || definition.Contexts.Length > 32 || definition.Contexts.Any(key => string.IsNullOrWhiteSpace(key) || key.Length > 512))
        { throw new ArgumentException("Instructions, contracts or contexts are invalid.", nameof(definition)); }
        foreach (var id in Runner.ContextIds(definition.Contexts))
        {
            await Context.Wires.Ensure<ContextUpdated>(id);
            await Context.Wires.Ensure<ContextRemoved>(id);
        }
        _mutating = true;
        try
        {
            await Persist(State with
            {
                Revision = State.Revision + 1,
                Definition = definition with
                {
                    Contracts = [.. definition.Contracts],
                    Capabilities = [.. definition.Capabilities],
                    RoutingExamples = [.. definition.RoutingExamples],
                    Contexts = [.. definition.Contexts],
                },
            });
            await Notify(new AgentConfigured(Key, State.Revision));
        }
        finally { _mutating = false; }
        await Reconsider();
    }

    public async Task<AgentState> Read()
    {
        await Fresh();
        if (_write is { } pending) { await pending.Task; }
        var state = Committed;
        return new AgentState(state.Revision, state.Definition, state.Summary, state.ActiveRun, [.. state.Runs]) { Version = state.Version, Blocked = _blocked };
    }

    public async Task<AiMessage[]> Transcript()
    {
        await Fresh();
        if (_write is { } pending) { await pending.Task; }
        return [.. Committed.Transcript];
    }

    public Task Cancel()
    {
        if (_active is { } active) { active.Cancel(); }
        // A started run whose driver has not picked it up yet is cancelled when it does.
        else if (State.ActiveRun is { } pending) { _cancelBeforeDrive = pending.RunId; }
        return Task.CompletedTask;
    }

    public Task CancelRun(string runId)
    {
        // A delayed/replayed UI command must never cancel a later turn.
        return State.ActiveRun?.RunId == runId ? Cancel() : Task.CompletedTask;
    }

    public async Task Clear()
    {
        await Fresh();
        EnsureIdle();
        _mutating = true;
        try
        {
            await Persist(State with { Revision = State.Revision + 1, Transcript = [], Summary = null, Runs = [] });
            await Notify(new TranscriptCleared(Key, State.Revision));
        }
        finally { _mutating = false; }
    }

    public async Task<AgentCompactionResult> Compact(string? instructions = null)
    {
        await Fresh();
        EnsureIdle();
        var previousCount = State.Transcript.Length;
        if (previousCount == 0)
        {
            return new AgentCompactionResult(State.Revision, 0, 0, State.Summary ?? "");
        }

        _mutating = true;
        try
        {
            var compacted = await AgentHistoryCompactor.CompactAsync(
                State.Transcript,
                State.Definition,
                State.Summary,
                instructions,
                force: true,
                Scope,
                services,
                Logger,
                CancellationToken.None);
            var maskedSummary = string.IsNullOrWhiteSpace(compacted.Summary) ? (State.Summary ?? "") : await Runner.Mask(compacted.Summary);
            var nextRevision = State.Revision + 1;
            await Persist(State with
            {
                Revision = nextRevision,
                Transcript = compacted.Messages,
                Summary = maskedSummary.Length > 0 ? maskedSummary : null,
            });
            if (maskedSummary.Length > 0)
            {
                await Runner.WriteSummary(State.Definition.Contexts, maskedSummary);
            }
            await Runner.RecordMilestone(State.Definition.Contexts, userText: null, $"Decision: Compacted {compacted.EvictedCount} message(s) into conversation summary.");
            return new AgentCompactionResult(nextRevision, previousCount, compacted.Messages.Length, maskedSummary);
        }
        finally { _mutating = false; }
    }

    public async Task Post(string text, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var runId = "post/" + id;
        await Fresh();
        // A pending post is not durable yet, so a duplicate must retry after its write commits.
        if (_mutating) { throw new InvalidOperationException("The agent is answering; post again when the run ends."); }
        // A replayed post (same id) never adds a second entry.
        if (State.Runs.Any(run => run.RunId == runId)) { return; }
        if (State.ActiveRun is not null) { throw new InvalidOperationException("The agent is answering; post again when the run ends."); }
        var now = Clock.GetUtcNow();
        var run = new AgentRun(runId, AgentRunStatus.Completed, "", now, now, text) { Posted = true };
        _mutating = true;
        try
        {
            await Persist(State with { Transcript = [.. State.Transcript, new AiMessage("assistant", [new AiText(text)])], Runs = Keep(State.Runs, run) });
            await Notify(new TurnCompleted(Key, runId, text));
        }
        finally { _mutating = false; }
    }

    public async Task<string> Start(AgentTurn turn, string? flowId = null)
    {
        await Fresh();
        EnsureUnblocked();
        var (runId, replay) = Admit(turn);
        if (replay is not null)
        {
            await Notify(new TurnStarted(Key, runId, replay.UserText));
            await Notify(new TurnCompleted(Key, runId, await Runner.Mask(replay.Reply ?? "")));
            return runId;
        }
        await Persist(State with { ActiveRun = new AgentRun(runId, AgentRunStatus.Running, UserText(turn.Message), Clock.GetUtcNow()) { FlowId = flowId, TraceId = TraceContext.Current?.TraceId } });
        try
        {
            var driven = turn with { RunId = runId };
            await Context.Get<IAgentDriver>(Id).Drive(driven, runId);
        }
        catch
        {
            // The caller learns why the hand-off failed; a failure to undo it must not replace that.
            try { await Persist(State with { ActiveRun = null }); }
            catch (Exception error) { Logger.LogWarning(error, "Failed to clear active agent run {RunId} after hand-off failure.", runId); Abandon(error, runId); }
            throw;
        }
        return runId;
    }

    public async Task Drive(AgentTurn turn, string runId)
    {
        try { await foreach (var _ in Execute(turn, runId, CancellationToken.None)) { } }
        catch (Exception error) { Logger.LogWarning(error, "Agent turn {RunId} ended with an error.", runId); }
    }

    public IAsyncEnumerable<AgentTurnEvent> Run(AgentTurn turn, CancellationToken ct = default) => Execute(turn, null, ct);

    private sealed class AgentTurnSink(Func<AgentRun, Task> store) : ITurnSink
    {
        public Task Store(AgentRun run) => store(run);
    }

    // started is the run id Start already admitted and recorded as active; null admits the turn here.
    private async IAsyncEnumerable<AgentTurnEvent> Execute(AgentTurn turn, string? started, [EnumeratorCancellation] CancellationToken ct)
    {
        string runId;
        AgentRun? replay;
        if (started is null) { await Fresh(); EnsureUnblocked(); (runId, replay) = Admit(turn); }
        else if (State.ActiveRun?.RunId == started && _active is null) { (runId, replay) = (started, null); }
        else
        {
            // Whoever closed the run (a Resume, a Run that took it over) published its end; a run
            // recorded nowhere gets its end here, so a Start caller is never left waiting.
            Logger.LogWarning("Agent run {RunId} was no longer active when its driver arrived.", started);
            if (State.ActiveRun?.RunId != started && State.Runs.All(run => run.RunId != started))
            { await Notify(new TurnFailed(Key, started, "The run was lost before it started.", Cancelled: false)); }
            yield break;
        }
        if (replay is not null)
        {
            await Notify(new TurnStarted(Key, runId, replay.UserText));
            yield return new AgentTurnEvent.Started(runId);
            yield return new AgentTurnEvent.TextChunk(replay.Reply ?? "");
            yield return new AgentTurnEvent.Completed([new AiMessage("assistant", [new AiText(replay.Reply ?? "")])], replay.Usage);
            await Notify(new TurnCompleted(Key, runId, await Runner.Mask(replay.Reply ?? "")));
            yield return new AgentTurnEvent.Finished();
            yield break;
        }

        var definition = State.Definition;
        var input = Stored(turn.Message);
        var initialRun = State.ActiveRun is { } active && active.RunId == runId ? active : new AgentRun(runId, AgentRunStatus.Running, UserText(input), Clock.GetUtcNow()) { TraceId = TraceContext.Current?.TraceId };
        // The timeout has its own source, so a run that ran out of time is told apart from a cancelled one.
        using var timeout = new CancellationTokenSource(definition.Timeout);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var timedOut = $"The run exceeded its {definition.Timeout} timeout.";
        // Set before the first await, so an interleaved Run, Start, Configure or Clear sees this run.
        _active = lifetime;
        if (_cancelBeforeDrive == runId) { lifetime.Cancel(); }
        _cancelBeforeDrive = null;
        var turnState = new TurnState(initialRun);
        var committed = false;
        var yielding = false;
        var shown = 0;
        var sequence = 0;

        // Announces the text not yet announced; the caller stored it first, so the step carries that version.
        async Task Announce(bool persisted = true)
        {
            if (shown < turnState.Visible.Length)
            {
                var part = turnState.Visible[shown..];
                shown = turnState.Visible.Length;
                await Notify(new TurnProgressed(Key, runId, "text", part, null, null, ++sequence), persisted);
            }
        }

        var sink = new AgentTurnSink(async run =>
        {
            await Persist(State with { ActiveRun = run });
            await Announce();
        });

        try
        {
            // Start already stored this run; only a Run admits it here.
            if (State.ActiveRun != turnState.Run) { await Persist(State with { ActiveRun = turnState.Run }); }
            await Notify(new TurnStarted(Key, runId, turnState.Run.UserText));
            var effectiveContexts = turn.Contexts ?? definition.Contexts;

            if (AgentHistoryCompactor.TryParseCompactCommand(turnState.Run.UserText, out var compactFocus))
            {
                var previousCount = State.Transcript.Length;
                var compacted = await AgentHistoryCompactor.CompactAsync(
                    State.Transcript,
                    definition,
                    State.Summary,
                    compactFocus,
                    force: true,
                    Scope,
                    services,
                    Logger,
                    lifetime.Token);
                var maskedSummary = string.IsNullOrWhiteSpace(compacted.Summary) ? (State.Summary ?? "") : await Runner.Mask(compacted.Summary);
                var replyText = previousCount == 0
                    ? "No conversation messages to compact yet."
                    : $"Compacted {compacted.EvictedCount} of {previousCount} message(s) ({compacted.Messages.Length} kept in active window).\n{maskedSummary}";
                turnState.Raw.Append(replyText);
                await Runner.Refresh(turnState, final: true);
                turnState.Run = turnState.Run with { Status = AgentRunStatus.Completed, EndedAt = Clock.GetUtcNow(), Reply = turnState.Visible };
                await Persist(State with
                {
                    Transcript = compacted.Messages,
                    Summary = maskedSummary.Length > 0 ? maskedSummary : null,
                    ActiveRun = null,
                    Runs = Keep(State.Runs, turnState.Run),
                });
                if (maskedSummary.Length > 0)
                {
                    await Runner.WriteSummary(effectiveContexts, maskedSummary);
                }
                await Runner.RecordMilestone(effectiveContexts, null, $"Decision: Compacted {compacted.EvictedCount} message(s) into conversation summary.");
                committed = true;
                await Announce();
                var assistantReply = new AiMessage("assistant", [new AiText(turnState.Visible)]);
                yielding = true; yield return new AgentTurnEvent.Started(runId); yielding = false;
                yielding = true; yield return new AgentTurnEvent.TextChunk(turnState.Visible); yielding = false;
                yielding = true; yield return new AgentTurnEvent.Completed([assistantReply], null); yielding = false;
                yielding = true; yield return new AgentTurnEvent.Finished(); yielding = false;
                yield break;
            }

            await Runner.Hydrate(effectiveContexts, UserText(input));
            var preTurnMax = Math.Max(1, Math.Min(definition.MaxHistoryMessages, definition.CompactionThreshold) - 1);
            var bounded = AgentHistoryCompactor.BoundWindow(
                State.Transcript,
                definition with { MaxHistoryMessages = Math.Max(2, preTurnMax), CompactionThreshold = Math.Max(2, preTurnMax) },
                Math.Max(0, TurnRunner.MaxHistoryCharacters - Size(input)),
                State.Summary);
            var plan = new TurnPlan(Scope, Key, runId, bounded.Messages, bounded.Summary, input,
                turn.Instructions ?? definition.Instructions, turn.Contracts ?? definition.Contracts, effectiveContexts,
                turn.Model ?? definition.Model, definition.Options, definition.MaxModelCalls, true);

            await foreach (var rawEvent in Runner.Stream(plan, turnState, sink, lifetime.Token))
            {
                var current = rawEvent;
                switch (current)
                {
                    case AgentTurnEvent.ToolStarted tool:
                        await Notify(new TurnProgressed(Key, runId, "tool-call", null, tool.Name, tool.Arguments, ++sequence));
                        break;
                    case AgentTurnEvent.ToolCompleted tool:
                        var refusal = TurnRunner.ToolRefusal(tool.Result);
                        await Notify(new TurnProgressed(Key, runId, "tool-result", refusal?.Message, tool.Name, tool.Result, ++sequence));
                        break;
                    case AgentTurnEvent.ToolFailed tool:
                        await Notify(new TurnProgressed(Key, runId, "tool-result", tool.Message, tool.Name,
                            TurnRunner.SerializeToolError(tool.Code, tool.Message), ++sequence));
                        break;
                    case AgentTurnEvent.Completed done:
                        var combinedTranscript = (AiMessage[])[.. State.Transcript, input, .. done.Messages];
                        var compacted = await AgentHistoryCompactor.CompactAsync(
                            combinedTranscript,
                            definition,
                            State.Summary,
                            focusInstructions: null,
                            force: false,
                            Scope,
                            services,
                            Logger,
                            lifetime.Token);
                        var nextSummary = string.IsNullOrWhiteSpace(compacted.Summary)
                            ? State.Summary
                            : (compacted.Summary == State.Summary ? State.Summary : await Runner.Mask(compacted.Summary));
                        var summarized = nextSummary != State.Summary;
                        await Persist(State with { Transcript = compacted.Messages, Summary = nextSummary, ActiveRun = null, Runs = Keep(State.Runs, turnState.Run) });
                        if (summarized && nextSummary is { Length: > 0 }) { await Runner.WriteSummary(effectiveContexts, nextSummary); }
                        await Runner.RecordMilestone(effectiveContexts, turnState.Run.UserText, turnState.Visible);
                        committed = true;
                        await Announce();
                        if (done.Usage is not null) { await Notify(new TurnProgressed(Key, runId, "receipt", null, null, await Runner.Mask(JsonSerializer.Serialize(done.Usage)), ++sequence)); }
                        break;
                    case AgentTurnEvent.TurnFailed failed:
                        // The loop sees only a cancelled token; a run out of time failed, it was not cancelled.
                        var adjusted = failed.Cancelled && TimedOut(timeout, ct)
                            ? new(timedOut, Cancelled: false) { Failure = AgentFailure.TimedOut }
                            : failed;
                        turnState.Failure = adjusted;
                        current = adjusted;
                        break;
                }
                yielding = true;
                yield return current;
                yielding = false;
            }
        }
        finally
        {
            try
            {
                if (committed)
                {
                    await Notify(new TurnCompleted(Key, runId, turnState.Visible));
                }
                else
                {
                    // Finally while yielding means the consumer stopped pulling: an abandoned stream is a cancelled run.
                    var expired = !yielding && TimedOut(timeout, ct);
                    var failure = turnState.Failure;
                    // A recorded failure is authoritative; abandonment and the token only decide when none was recorded.
                    var cancelled = !expired && (failure is not null ? failure.Cancelled : yielding || lifetime.IsCancellationRequested);
                    var error = expired ? timedOut
                        : failure?.Message ?? (yielding ? "The response stream was abandoned." : "Execution failed before committing its result.");
                    // The error and the text so far are masked; a redactor that fails leaves the held-back text and a generic error.
                    try { await Runner.Refresh(turnState, final: true); error = await Runner.Mask(error); }
                    catch (Exception mask) { Logger.LogWarning(mask, "Agent run {RunId} could not mask its outcome.", runId); error = "Execution failed before committing its result."; }
                    var failureKind = expired
                        ? AgentFailure.TimedOut
                        : (cancelled ? AgentFailure.Unknown : (failure?.Failure ?? AgentFailure.Unknown));
                    var closedRun = Close(turnState.Streamed, cancelled ? AgentRunStatus.Cancelled : AgentRunStatus.Failed, error, cancelled ? "tool_cancelled" : "tool_interrupted")
                        with
                    { Failure = failureKind };
                    // The end is announced whether or not it could be stored: a Start caller must never wait forever.
                    var persisted = true;
                    try { await Persist(State with { ActiveRun = null, Runs = Keep(State.Runs, closedRun) }); }
                    catch (Exception persist) { persisted = false; Logger.LogWarning(persist, "Failed to persist final agent run {RunId}.", runId); Abandon(persist, runId); }
                    await Announce(persisted);
                    await Notify(new TurnFailed(Key, runId, error, cancelled) { Failure = failureKind }, persisted);
                }
            }
            finally { _active = null; }
        }
    }

    // Validates the turn and decides whether it is a fresh run or a replay of a retained one.
    private (string RunId, AgentRun? Replay) Admit(AgentTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var message = turn.Message ?? throw new ArgumentException("An agent turn needs a message.", nameof(turn));
        if (message.Role != "user" || message.Content is not { Count: > 0 }) { throw new ArgumentException("An agent accepts a non-empty user message.", nameof(turn)); }
        if (message.Content.Any(content => content is not (AiText or AiImage or AiAudio)) || message.Content.OfType<AiText>().Sum(content => (long)content.Text.Length) is 0 or > 32000)
        { throw new ArgumentException("User messages carry 1-32000 characters of text, with images or audio, never tool calls or reasoning.", nameof(turn)); }
        if (turn.RunId is { Length: > 200 }) { throw new ArgumentException("A run id has at most 200 characters.", nameof(turn)); }
        if (turn.Contexts is { } contexts && (contexts.Length > 32 || contexts.Any(key => string.IsNullOrWhiteSpace(key) || key.Length > 512)))
        { throw new ArgumentException("A turn names at most 32 contexts, each 1-512 characters.", nameof(turn)); }
        _ = InferenceMapping.ToChatMessage(message);
        var runId = string.IsNullOrWhiteSpace(turn.RunId) ? Guid.NewGuid().ToString("N") : turn.RunId;
        if (Committed.Runs.FirstOrDefault(run => run.RunId == runId) is { } retained)
        {
            // A retained run id replays only for the same text; another text must never get an unrelated answer.
            if (retained.UserText != UserText(message)) { throw new InvalidOperationException("Run ID already belongs to another message."); }
            // A replay touches nothing, so it needs no idle agent; a run that did not complete is retried under its id.
            if (retained.Status == AgentRunStatus.Completed) { return (runId, retained); }
        }
        EnsureIdle();
        return (runId, null);
    }

    private void EnsureIdle()
    {
        if (_active is not null || _mutating || State.ActiveRun is not null) { throw new InvalidOperationException("This agent already has an active operation."); }
    }

    private static string UserText(AiMessage message) => string.Concat(message.Content.OfType<AiText>().Select(text => text.Text));

    // Snapshot input content before committing it to durable history.
    private static AiMessage Stored(AiMessage message) => new(message.Role, message.Content.ToArray());

    private static long Size(AiMessage message) => AgentHistoryCompactor.MessageSize(message);

    // Only the run's own time limit is a timeout; the caller's token and Cancel are cancellations.
    private static bool TimedOut(CancellationTokenSource timeout, CancellationToken caller) => timeout.IsCancellationRequested && !caller.IsCancellationRequested;

    // The stored state no longer matches what this activation holds; a fresh activation reads the
    // stored run back and Resume closes it as interrupted.
    private void Abandon(Exception error, string runId)
    {
        Logger.LogError(error, "Agent run {RunId} could not store its outcome; the agent recovers from storage on its next call.", runId);
        _stale = true;
    }

    private AgentRun Close(AgentRun run, AgentRunStatus status, string error, string openCallCode = "tool_interrupted") => run with
    {
        Status = status,
        EndedAt = Clock.GetUtcNow(),
        Error = error,
        ToolCalls = [.. run.ToolCalls.Select(call => call.Result is null && call.Code is null
            ? call with { Code = openCallCode, Error = "Execution ended before the tool call completed." } : call)],
    };

    private static AgentRun[] Keep(AgentRun[] runs, AgentRun run) => [.. runs.Where(item => item.RunId != run.RunId).TakeLast(KeptRuns - 1), run];

    // Neuron<TState>.Save always publishes a signal; most agent writes have none, and a failed
    // signal must never undo a committed write, so the agent persists through the runtime state on its own.
    private async Task Persist(AgentStorage next)
    {
        var previous = _state;
        var writing = _write = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _beforeWrite = previous;
        _state = next with { Version = previous.Version + 1, Blocked = _blocked };
        try
        {
            if (writes is not null) { await writes.Before(_state); }
            await Store(_state);
            writes?.Committed(_state);
        }
        catch { _state = previous; throw; }
        finally
        {
            _beforeWrite = null;
            _write = null;
            writing.TrySetResult();
        }
    }

    private string RunKeyOf(string runId)
        => new DigitalBrain.NeuronId(Scope is ['@', .. var sys] ? DigitalBrain.BrainId.System(sys) : new DigitalBrain.BrainId(Scope), "agent.run", runId).ToString();

    // The trace stored with the active run when it was admitted, else the trace of the call announcing it.
    private string? TraceOf(string runId)
        => (State.ActiveRun is { } active && active.RunId == runId ? active.TraceId : null) ?? TraceContext.Current?.TraceId;

    private async Task Notify(DigitalBrain.Signal signal, bool persisted = true)
    {
        // Failed persistence needs a fresh Read to reactivate/recover the abandoned run,
        // even when a consumer already projected the last committed version.
        var version = persisted ? Committed.Version : 0;
        signal = signal switch
        {
            TurnStarted started => started with { Version = version, RunKey = started.RunKey ?? RunKeyOf(started.RunId), TraceId = started.TraceId ?? TraceOf(started.RunId) },
            TurnProgressed progressed => progressed with { Version = version, RunKey = progressed.RunKey ?? RunKeyOf(progressed.RunId) },
            TurnCompleted completed => completed with { Version = version, RunKey = completed.RunKey ?? RunKeyOf(completed.RunId) },
            TurnFailed failed => failed with { Version = version, RunKey = failed.RunKey ?? RunKeyOf(failed.RunId) },
            _ => signal,
        };
        try { await Publish(signal); }
        catch (Exception error) { Logger.LogWarning(error, "Agent signal delivery failed; consumers can reconcile through Read and Transcript."); }
    }
}

// A test seam over the agent's own writes (06-06d): the runtime's journal is not a grain storage a fact can wrap, so the
// persistence and storage-fault facts register one to hold or fail a write. Never registered in production.
internal abstract class AgentWrites
{
    public abstract Task Before(AgentStorage next);

    public virtual void Committed(AgentStorage committed) { }
}
