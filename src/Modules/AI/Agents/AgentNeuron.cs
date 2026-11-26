using System.Runtime.CompilerServices;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.State;
using IntoChat.OS.Host.Logging;
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

// Legacy compatibility adapter over InterleavingRunHost<AgentStorage> (retained for the S3 two-release LegacyImport window).
[DigitalBrain.Neurons.Neuron("agent", State = "agent")]
internal sealed class AgentNeuron(
    ISecretRedactor redactor,
    TimeProvider clock,
    IServiceProvider services,
    ILogger<AgentNeuron>? logger = null,
    AgentWrites? writes = null)
    : InterleavingRunHost<AgentStorage>(redactor, clock, services, (ILogger?)logger ?? OsConsoleLog.For<AgentNeuron>()), IAgent, IAgentDriver
{
    private string Key => FullKey;

    protected override string HostKind => "Agent";
    protected override string BusyMessage => "This agent already has an active operation.";

    protected override AgentRun? ActiveRunOf(AgentStorage state) => state.ActiveRun;
    protected override bool BlockedOf(AgentStorage state) => state.Blocked;
    protected override string[] ContextsOf(AgentStorage state) => state.Definition.Contexts;

    protected override AgentStorage WithInterruptedRun(AgentStorage state, AgentRun closedRun)
        => state with { ActiveRun = null, Runs = Keep(state.Runs, closedRun) };

    protected override AgentStorage PrepareWrite(AgentStorage previous, AgentStorage next, bool blocked, bool bumpRevision)
        => next with { Version = previous.Version + 1, Blocked = blocked };

    protected override Task BeforeStore(AgentStorage next)
        => writes is not null ? writes.Before(next) : Task.CompletedTask;

    protected override void OnStored(AgentStorage committed) => writes?.Committed(committed);

    // Announced by a one-way call to the agent itself, so the request that woke the agent (a subscription, a
    // late Drive) is served first and a Start caller still gets the run's end.
    protected override Task OnRunInterrupted(AgentRun open, string error)
        => Context.Get<IAgentDriver>(Id).Interrupted(open.RunId, error);

    protected override Task OnBlockedChanged(bool blocked) => Notify(new AgentBlocked(Key, blocked));

    public async Task Interrupted(string runId, string error) => await Notify(new TurnFailed(Key, runId, await Runner.Mask(error), Cancelled: false));

    public async Task Configure(AgentDefinition definition, long expectedRevision)
    {
        await Fresh();
        EnsureIdle();
        AgentDefinitionRules.Validate(definition);
        if (State.Revision != expectedRevision) { throw new InvalidOperationException("Agent revision conflict."); }
        await EnsureContextWires(definition.Contexts);
        await Mutate(async () =>
        {
            await Persist(State with
            {
                Revision = State.Revision + 1,
                Definition = AgentDefinitionRules.Normalize(definition),
            });
            await Notify(new AgentConfigured(Key, State.Revision));
        });
        await Reconsider();
    }

    public async Task<AgentState> Read()
    {
        var state = await ReadCommittedAsync();
        return new AgentState(state.Revision, state.Definition, state.Summary, state.ActiveRun, [.. state.Runs]) { Version = state.Version, Blocked = IsBlocked };
    }

    public async Task<AiMessage[]> Transcript() => [.. (await ReadCommittedAsync()).Transcript];

    public Task Cancel()
    {
        CancelActiveRun();
        return Task.CompletedTask;
    }

    public Task CancelRun(string runId)
        // A delayed/replayed UI command must never cancel a later turn.
        => State.ActiveRun?.RunId == runId ? Cancel() : Task.CompletedTask;

    public async Task Clear()
    {
        await Fresh();
        EnsureIdle();
        await Mutate(async () =>
        {
            await Persist(State with { Revision = State.Revision + 1, Transcript = [], Summary = null, Runs = [] });
            await Notify(new TranscriptCleared(Key, State.Revision));
        });
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

        return await Mutate(async () =>
        {
            var compacted = await CompactHistoryAsync(State.Transcript, State.Definition, State.Summary, instructions, force: true, CancellationToken.None);
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
        });
    }

    public async Task Post(string text, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var runId = "post/" + id;
        await Fresh();
        // A pending post is not durable yet, so a duplicate must retry after its write commits.
        if (IsMutating) { throw new InvalidOperationException("The agent is answering; post again when the run ends."); }
        // A replayed post (same id) never adds a second entry.
        if (State.Runs.Any(run => run.RunId == runId)) { return; }
        EnsureIdle("The agent is answering; post again when the run ends.");
        var now = Clock.GetUtcNow();
        var run = new AgentRun(runId, AgentRunStatus.Completed, "", now, now, text) { Posted = true };
        await Mutate(async () =>
        {
            await Persist(State with { Transcript = [.. State.Transcript, new AiMessage("assistant", [new AiText(text)])], Runs = Keep(State.Runs, run) });
            await Notify(new TurnCompleted(Key, runId, text));
        });
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
        await EnforceTrialTurnPolicyAsync();
        await Persist(State with { ActiveRun = new AgentRun(runId, AgentRunStatus.Running, UserText(turn.Message), Clock.GetUtcNow()) { FlowId = flowId, TraceId = TraceContext.Current?.TraceId } });
        try
        {
            var driven = turn with { RunId = runId };
            await Context.Get<IAgentDriver>(Id).Drive(driven, runId);
        }
        catch
        {
            await UndoHandOffAsync(State with { ActiveRun = null }, runId);
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

    // started is the run id Start already admitted and recorded as active; null admits the turn here.
    private async IAsyncEnumerable<AgentTurnEvent> Execute(AgentTurn turn, string? started, [EnumeratorCancellation] CancellationToken ct)
    {
        string runId;
        AgentRun? replay;
        if (started is null) { await Fresh(); EnsureUnblocked(); (runId, replay) = Admit(turn); }
        else if (State.ActiveRun?.RunId == started && !IsActive) { (runId, replay) = (started, null); }
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
        if (started is null)
        {
            await EnforceTrialTurnPolicyAsync();
        }

        var definition = State.Definition;
        var input = Stored(turn.Message);
        var initialRun = State.ActiveRun is { } active && active.RunId == runId ? active : new AgentRun(runId, AgentRunStatus.Running, UserText(input), Clock.GetUtcNow()) { TraceId = TraceContext.Current?.TraceId };
        using var activeRun = BeginRun(runId, definition.Timeout, ct);
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
                var compacted = await CompactHistoryAsync(State.Transcript, definition, State.Summary, compactFocus, force: true, activeRun.Token);
                var maskedSummary = string.IsNullOrWhiteSpace(compacted.Summary) ? (State.Summary ?? "") : await Runner.Mask(compacted.Summary);
                var replyText = FormatCompactReply(previousCount, compacted.EvictedCount, compacted.Messages.Length, maskedSummary);
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
            var bounded = BoundPreTurnHistory(State.Transcript, definition, input, State.Summary);
            var plan = new TurnPlan(Scope, Key, runId, bounded.Messages, bounded.Summary, input,
                turn.Instructions ?? definition.Instructions, turn.Contracts ?? definition.Contracts, effectiveContexts,
                turn.Model ?? definition.Model, definition.Options, definition.MaxModelCalls, true)
            {
                IncludeComponentOperations = definition.IncludeComponentOperations,
                AlwaysOnOperationApps = definition.OperationApps,
                ToolSources = definition.ToolSources,
            };

            await foreach (var rawEvent in Runner.Stream(plan, turnState, sink, activeRun.Token))
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
                        var compacted = await CompactHistoryAsync(combinedTranscript, definition, State.Summary, focusInstructions: null, force: false, activeRun.Token);
                        var nextSummary = string.IsNullOrWhiteSpace(compacted.Summary)
                            ? State.Summary
                            : (compacted.Summary == State.Summary ? State.Summary : await Runner.Mask(compacted.Summary));
                        var summarized = nextSummary != State.Summary;
                        await Persist(State with { Transcript = compacted.Messages, Summary = nextSummary, ActiveRun = null, Runs = Keep(State.Runs, turnState.Run) });
                        if (summarized && nextSummary is { Length: > 0 }) { await Runner.WriteSummary(effectiveContexts, nextSummary); }
                        await Runner.RecordMilestone(effectiveContexts, turnState.Run.UserText, turnState.Visible);
                        committed = true;
                        await Announce();
                        if (done.Usage is not null) { await Notify(new TurnProgressed(Key, runId, "receipt", null, null, await Runner.Mask(ToolJson.Serialize(done.Usage)), ++sequence)); }
                        break;
                    case AgentTurnEvent.TurnFailed failed:
                        var adjusted = activeRun.NormalizeFailure(failed);
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
            if (committed)
            {
                await Notify(new TurnCompleted(Key, runId, turnState.Visible));
            }
            else
            {
                var closed = await FinalizeFailedRunAsync(activeRun, turnState, sink, runId, abandonedStream: yielding);
                // The end is announced whether or not it could be stored: a Start caller must never wait forever.
                var persisted = await TryPersistClosedRunAsync(State with { ActiveRun = null, Runs = Keep(State.Runs, closed.Run) }, runId);
                await Announce(persisted);
                await Notify(new TurnFailed(Key, runId, closed.Error, closed.Cancelled) { Failure = closed.FailureKind }, persisted);
            }
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

    private static string UserText(AiMessage message) => string.Concat(message.Content.OfType<AiText>().Select(text => text.Text));

    // Snapshot input content before committing it to durable history.
    private static AiMessage Stored(AiMessage message) => new(message.Role, message.Content.ToArray());

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

    private sealed class AgentTurnSink(Func<AgentRun, Task> store) : ITurnSink
    {
        public Task Store(AgentRun run) => store(run);
    }
}

// A test seam over the agent's own writes (06-06d): the runtime's journal is not a grain storage a fact can wrap, so the
// persistence and storage-fault facts register one to hold or fail a write. Never registered in production.
internal abstract class AgentWrites
{
    public abstract Task Before(AgentStorage next);

    public virtual void Committed(AgentStorage committed) { }
}
