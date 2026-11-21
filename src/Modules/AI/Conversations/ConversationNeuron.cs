using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Agents.Context.Signals;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.AI.Conversations;

[Neuron(IConversation.Type)]
internal sealed class ConversationNeuron(
    ISecretRedactor redactor,
    TimeProvider clock,
    IServiceProvider services,
    ILogger<ConversationNeuron>? logger = null)
    : AiNeuron<ConversationState>, IConversation, IConversationDriver
{
    private const int KeptRuns = 64;
    private const int MaxSayCharacters = 32_000;
    private const string ConversationsKeyPrefix = "conversations/";
    private const string DefaultAgentId = "ino";

    private CancellationTokenSource? _active;
    private string? _cancelBeforeDrive;
    private bool _mutating;
    private ConversationState _state = new();
    private bool _stale;
    private bool _blocked;
    private string? _interrupt;
    private long _reconsidering;
    private Task? _importing;
    private TurnRunner? _runner;
    private ConversationState? _beforeWrite;
    private TaskCompletionSource? _write;

    private new ConversationState State => _state;
    private ConversationState Committed => _beforeWrite ?? State;
    private string Scope => Id.Brain.Value;
    private ILogger Logger => (ILogger?)logger ?? OsConsoleLog.For<ConversationNeuron>();
    private TimeProvider Clock => clock;
    private TurnRunner Runner => _runner ??= new(Context, Id, redactor, clock, services, Logger);

    private ConversationId ConvId
    {
        get
        {
            var raw = Id.Key.StartsWith(ConversationsKeyPrefix, StringComparison.Ordinal)
                ? Id.Key[ConversationsKeyPrefix.Length..]
                : Id.Key;
            return ConversationId.TryParse(raw, out var parsed) ? parsed : ConversationId.Parse("main");
        }
    }

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        _state = base.State;
        _blocked = _state.Blocked;
        await Resume();
    }

    private async Task Resume()
    {
        if (State.ActiveRun is { } open)
        {
            const string Error = "Activation ended before the run committed; external tool outcomes may be unknown.";
            await Persist(State with
            {
                ActiveRun = null,
                Replacing = null,
                Runs = Keep(State.Runs, Close(open, AgentRunStatus.Interrupted, Error)),
            });
        }
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

    private async Task Reconsider()
    {
        var turn = ++_reconsidering;
        var contexts = AllContexts(State);
        var interrupt = await Runner.FirstInterrupt(contexts);
        if (turn != _reconsidering) { return; }
        var blocked = interrupt is not null;
        var changed = blocked != _blocked;
        _blocked = blocked;
        _interrupt = interrupt?.Text;
        if (changed && blocked) { await Stop(); }
        if (State.Blocked != blocked && _active is null && !_mutating && _write is null && State.ActiveRun is null)
        {
            _mutating = true;
            try { await Persist(State); }
            finally { _mutating = false; }
        }
    }

    private async Task Fresh()
    {
        if (!_stale || _active is not null || _mutating) { return; }
        _stale = false;
        _state = base.State;
        await Resume();
    }

    private IBrainOwners Owners => services.GetService<IBrainOwners>() ?? throw FriendRelay.Refused();
    private IFriends Friends => Context.Get<IFriends>(FriendsDefaults.Id(Id.Brain.Value));

    private bool IsSameBrainCaller()
    {
        var caller = Context.Caller;
        return caller is null || caller.Brain == Id.Brain;
    }

    private void AuthorizeOwnerOnly()
    {
        if (!IsSameBrainCaller())
        {
            throw FriendRelay.Refused();
        }
    }

    private async Task<AccountId> RequireOwnerAsPersonAsync()
    {
        var caller = Context.Caller;
        if (caller is not { Actor: Actor.Person person }
            || string.IsNullOrEmpty(person.Account)
            || caller.Brain != Id.Brain)
        {
            throw FriendRelay.Refused();
        }
        if (caller.Via is { } via && (via.Brain != Id.Brain || via.Type is not ("app.ino" or "app.friends")))
        {
            throw FriendRelay.Refused();
        }
        var owner = await Owners.OwnerAccount(Id.Brain);
        if (!string.Equals(owner, person.Account, StringComparison.Ordinal))
        {
            throw FriendRelay.Refused();
        }
        return new AccountId(person.Account);
    }

    private AccountId CallerAccount()
    {
        var account = Context.Caller?.For?.Account;
        return !string.IsNullOrWhiteSpace(account) ? new AccountId(account) : new AccountId(Id.Brain.Value);
    }

    private void EnsureIdle()
    {
        if (_active is not null || _mutating || State.ActiveRun is not null)
        {
            throw new InvalidOperationException("This conversation already has an active run.");
        }
    }

    private void EnsureUnblocked()
    {
        if (_blocked) { throw new InvalidOperationException("Blocked: " + _interrupt); }
    }

    private Task EnsureImportedAsync()
    {
        if (State.Imported) { return Task.CompletedTask; }
        if (_importing is { } pending) { return pending; }
        var task = ImportOnceAsync();
        _importing = task;
        return task;
    }

    private async Task ImportOnceAsync()
    {
        try
        {
            if (State.Imported) { return; }
            if (!Id.Key.StartsWith(ConversationsKeyPrefix, StringComparison.Ordinal))
            {
                _state = _state with { Imported = true };
                return;
            }

            var rawId = Id.Key[ConversationsKeyPrefix.Length..];
            var oldAgent = Context.Get<IAgent>(new NeuronId(Id.Brain, AgentSignals.NeuronType, "ino/conversations/" + rawId));
            var oldState = await oldAgent.Read();
            if (oldState.Revision == 0 && oldState.Runs.Length == 0 && string.IsNullOrWhiteSpace(oldState.Summary))
            {
                _state = _state with { Imported = true };
                return;
            }

            var transcript = oldState.Runs.Length > 0 ? await oldAgent.Transcript() : [];
            var ownerAccount = CallerAccount();
            var (importedEntries, importedRuns) = LegacyImport.From(oldState, transcript, ownerAccount, DefaultAgentId);

            var nextAgents = State.Agents.Any(a => string.Equals(a.AgentId, DefaultAgentId, StringComparison.Ordinal))
                ? State.Agents
                : [.. State.Agents, new SeatedAgent(DefaultAgentId, NormalizeDefinition(oldState.Definition))];

            foreach (var ctxId in Runner.ContextIds(nextAgents.SelectMany(a => a.Definition.Contexts).Distinct(StringComparer.Ordinal).ToArray()))
            {
                await Context.Wires.Ensure<ContextUpdated>(ctxId);
                await Context.Wires.Ensure<ContextRemoved>(ctxId);
            }

            var nextParticipants = WithParticipants(State.Participants, new Participant.Human(ownerAccount), new Participant.Assistant(DefaultAgentId));
            var nextRevision = State.Revision + 1;
            var nextLog = importedEntries.Length > 0
                ? ConversationLog.Append(State.Log, nextRevision, importedEntries)
                : State.Log;
            var nextRuns = importedRuns.Length > KeptRuns
                ? importedRuns[(importedRuns.Length - KeptRuns)..]
                : importedRuns;

            _blocked = oldState.Blocked;
            await Persist(State with
            {
                Participants = nextParticipants,
                Agents = nextAgents,
                Log = nextLog,
                Runs = nextRuns,
                Imported = true,
            });
            await Reconsider();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Logger.LogWarning(error, "Legacy import for conversation {ConversationId} failed; will retry on next call.", Id.Key);
        }
        finally
        {
            _importing = null;
        }
    }

    public async Task Seat(string agentId, AgentDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(definition);
        ValidateDefinition(definition);
        await Fresh();
        EnsureIdle();

        var trimmedId = agentId.Trim();
        var normalized = NormalizeDefinition(definition);
        var existingIndex = Array.FindIndex(State.Agents, a => string.Equals(a.AgentId, trimmedId, StringComparison.Ordinal));
        if (existingIndex >= 0 && SameDefinition(State.Agents[existingIndex].Definition, normalized))
        {
            return;
        }

        foreach (var ctxId in Runner.ContextIds(normalized.Contexts))
        {
            await Context.Wires.Ensure<ContextUpdated>(ctxId);
            await Context.Wires.Ensure<ContextRemoved>(ctxId);
        }

        var agents = State.Agents.ToList();
        var seated = new SeatedAgent(trimmedId, normalized);
        if (existingIndex >= 0) { agents[existingIndex] = seated; }
        else { agents.Add(seated); }

        var participants = WithParticipants(State.Participants, new Participant.Assistant(trimmedId));
        _mutating = true;
        try
        {
            await Persist(State with
            {
                Participants = participants,
                Agents = [.. agents],
            });
        }
        finally { _mutating = false; }
        await Reconsider();
    }

    public async Task Post(string text, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        AuthorizeOwnerOnly();
        await Fresh();
        await EnsureImportedAsync();

        var entryId = EntryIds.Post(id);
        if (_mutating) { throw new InvalidOperationException("The conversation is answering; post again when the run ends."); }
        if (State.Log.Any(item => item.Entry.Id == entryId)) { return; }
        if (_active is not null || State.ActiveRun is not null) { throw new InvalidOperationException("The conversation is answering; post again when the run ends."); }

        var masked = await Runner.Mask(text);
        var agentId = State.Agents.FirstOrDefault()?.AgentId ?? DefaultAgentId;
        var entry = new Said(entryId, new Participant.Assistant(agentId), Clock.GetUtcNow(), masked);
        var nextLog = ConversationLog.Append(State.Log, State.Revision + 1, entry);
        var participants = WithParticipants(State.Participants, new Participant.Assistant(agentId));

        _mutating = true;
        try
        {
            await Persist(State with
            {
                Participants = participants,
                Log = nextLog,
            });
        }
        finally { _mutating = false; }
    }

    public async Task Say(string text, ModelId? model = null)
    {
        if (!IsSameBrainCaller())
        {
            await SayAsFriendAsync(text);
            return;
        }

        if (string.IsNullOrEmpty(text) || text.Length > MaxSayCharacters)
        {
            throw new ArgumentException($"Message text must contain 1 to {MaxSayCharacters} characters.", nameof(text));
        }

        await Fresh();
        await EnsureImportedAsync();
        EnsureUnblocked();
        EnsureIdle();

        var masked = await Runner.Mask(text);
        var runId = Guid.NewGuid().ToString("N");
        var now = Clock.GetUtcNow();
        var speaker = new Participant.Human(CallerAccount());
        var said = new Said(EntryIds.Said(runId), speaker, now, masked);
        var nextLog = ConversationLog.Append(State.Log, State.Revision + 1, said);
        var participants = WithParticipants(State.Participants, speaker);
        var activeRun = new AgentRun(runId, AgentRunStatus.Running, masked, now)
        {
            TraceId = TraceContext.Current?.TraceId,
        };

        await Persist(State with
        {
            Participants = participants,
            Log = nextLog,
            ActiveRun = activeRun,
            Replacing = null,
        });

        await TouchSessionListBestEffortAsync(masked);

        try
        {
            await Context.Get<IConversationDriver>(Id).Drive(runId, model?.Value, null);
        }
        catch
        {
            try { await Persist(State with { ActiveRun = null, Replacing = null }); }
            catch (Exception undoError) { Logger.LogWarning(undoError, "Failed to clear active conversation run {RunId} after hand-off failure.", runId); Abandon(undoError, runId); }
            throw;
        }
    }

    private async Task SayAsFriendAsync(string text)
    {
        await Fresh();
        var friend = await GroupAdmission.Admit(Context.Caller, Id.Brain, State.Participants, Friends, Owners);
        if (string.IsNullOrEmpty(text) || text.Length > MaxSayCharacters)
        {
            throw new ArgumentException($"Message text must contain 1 to {MaxSayCharacters} characters.", nameof(text));
        }

        var masked = await Runner.Mask(text);
        if (_write is { } pending) { await pending.Task; }

        var said = new Said(EntryIds.Said(Guid.NewGuid().ToString("N")), new Participant.Human(friend), Clock.GetUtcNow(), masked);
        var nextLog = ConversationLog.Append(State.Log, State.Revision + 1, said);
        await Persist(State with { Log = nextLog });
    }

    public Task Stop()
    {
        AuthorizeOwnerOnly();
        if (_active is { } active) { active.Cancel(); }
        else if (State.ActiveRun is { } pending) { _cancelBeforeDrive = pending.RunId; }
        return Task.CompletedTask;
    }

    public async Task Regenerate(EntryId reply)
    {
        AuthorizeOwnerOnly();
        await Fresh();
        await EnsureImportedAsync();
        EnsureUnblocked();
        EnsureIdle();

        var targetRunId = EntryIds.RunOf(reply);
        if (string.IsNullOrEmpty(targetRunId) || reply != EntryIds.Reply(targetRunId))
        {
            throw new ArgumentException("Regenerate requires a reply entry id.", nameof(reply));
        }

        var latestRun = State.Runs.LastOrDefault(static r => !r.Posted);
        if (latestRun is null || !string.Equals(latestRun.RunId, targetRunId, StringComparison.Ordinal) || ConversationLog.IsSuperseded(State.Log, reply))
        {
            throw new InvalidOperationException("Only the latest run's reply can be regenerated.");
        }

        var newRunId = Guid.NewGuid().ToString("N");
        var now = Clock.GetUtcNow();
        var activeRun = new AgentRun(newRunId, AgentRunStatus.Running, latestRun.UserText, now)
        {
            TraceId = TraceContext.Current?.TraceId,
        };

        await Persist(State with
        {
            ActiveRun = activeRun,
            Replacing = reply,
        });

        var modelOverride = latestRun.Status != AgentRunStatus.Failed
            && latestRun.Model is { Provider: not AiProvider.Host, Model.Length: > 0 } prevModel
            ? prevModel.Model
            : null;
        try
        {
            await Context.Get<IConversationDriver>(Id).Drive(newRunId, modelOverride, reply.Value);
        }
        catch
        {
            try { await Persist(State with { ActiveRun = null, Replacing = null }); }
            catch (Exception undoError) { Logger.LogWarning(undoError, "Failed to clear active regeneration run {RunId} after hand-off failure.", newRunId); Abandon(undoError, newRunId); }
            throw;
        }
    }

    public async Task<ConversationView> Read(long? sinceRevision = null)
    {
        if (!IsSameBrainCaller())
        {
            await Fresh();
            if (_write is { } pendingWrite) { await pendingWrite.Task; }
            var committed = Committed;
            await GroupAdmission.Admit(Context.Caller, Id.Brain, committed.Participants, Friends, Owners);
            var shared = ConversationLog.Visible(committed.Log, sinceRevision, owner: false);
            return new ConversationView(ConvId, committed.Revision, [.. committed.Participants], shared, Active: null, Runs: [], Blocked: false, Contexts: []);
        }

        await Fresh();
        await EnsureImportedAsync();
        if (_write is { } pending) { await pending.Task; }
        var state = Committed;
        var visible = ConversationLog.Visible(state.Log, sinceRevision, owner: true);
        return new ConversationView(
            ConvId,
            state.Revision,
            [.. state.Participants],
            visible,
            state.ActiveRun,
            [.. state.Runs],
            _blocked,
            AllContexts(state))
        {
            Replacing = state.Replacing,
            BlockedReason = _blocked ? _interrupt : null,
        };
    }

    public async Task Invite(AccountId friend)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(friend.Value);
        var owner = await RequireOwnerAsPersonAsync();
        await Fresh();
        await EnsureImportedAsync();

        var friends = Friends;
        if (!await friends.IsFriend(friend))
        {
            throw FriendRelay.Refused();
        }
        var roster = await friends.List();
        var target = roster.FirstOrDefault(row => row.Account == friend)
            ?? throw FriendRelay.Refused();

        var participants = WithParticipants(State.Participants, new Participant.Human(owner), new Participant.Human(friend));
        await Persist(State with { Participants = participants });

        var title = await ReadTitleBestEffortAsync();
        var invite = new GroupInvite(Id.Brain, ConvId, title, owner);
        await SiloNeurons.Get<IFriends>(services, FriendsDefaults.Id(target.Brain.Value)).Invited(invite);
    }

    public async Task Drive(string runId, string? model, string? replaces)
    {
        if (State.ActiveRun?.RunId != runId || _active is not null)
        {
            Logger.LogWarning("Conversation run {RunId} was no longer active when its driver arrived.", runId);
            return;
        }

        var seated = State.Agents.FirstOrDefault() ?? new SeatedAgent(DefaultAgentId, new AgentDefinition());
        var definition = seated.Definition;
        var agentParticipant = new Participant.Assistant(seated.AgentId);
        EntryId? replacingId = string.IsNullOrWhiteSpace(replaces) ? null : new EntryId(replaces);

        using var timeout = new CancellationTokenSource(definition.Timeout);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var timedOut = $"The run exceeded its {definition.Timeout} timeout.";
        _active = lifetime;
        if (_cancelBeforeDrive == runId) { lifetime.Cancel(); }
        _cancelBeforeDrive = null;

        var turnState = new TurnState(State.ActiveRun);
        var committed = false;

        var sink = new TurnSink(async updatedRun =>
        {
            await Persist(State with { ActiveRun = updatedRun });
        });

        try
        {
            var priorLog = PriorLogForTurn(State.Log);
            var window = ModelContext.From(priorLog);
            var input = new AiMessage("user", [new AiText(turnState.Run.UserText)]);

            if (AgentHistoryCompactor.TryParseCompactCommand(turnState.Run.UserText, out var compactFocus))
            {
                var previousCount = window.Messages.Length;
                var compacted = await AgentHistoryCompactor.CompactAsync(
                    window.Messages,
                    definition,
                    window.Summary,
                    compactFocus,
                    force: true,
                    Scope,
                    services,
                    Logger,
                    lifetime.Token);
                var maskedSummary = string.IsNullOrWhiteSpace(compacted.Summary) ? (window.Summary ?? "") : await Runner.Mask(compacted.Summary);
                var replyText = previousCount == 0
                    ? "No conversation messages to compact yet."
                    : $"Compacted {compacted.EvictedCount} of {previousCount} message(s) ({compacted.Messages.Length} kept in active window).\n{maskedSummary}";
                var maskedReply = await Runner.Mask(replyText);
                var now = Clock.GetUtcNow();
                turnState.Run = turnState.Run with { Status = AgentRunStatus.Completed, EndedAt = now, Reply = maskedReply };

                var entriesToAppend = new List<ConversationEntry>(2);
                if (maskedSummary.Length > 0 && compacted.EvictedCount > 0)
                {
                    var upTo = UpToEntryId(window, compacted.EvictedCount, runId);
                    entriesToAppend.Add(new Summarized(EntryIds.Summary(runId), agentParticipant, now, maskedSummary, upTo));
                }
                entriesToAppend.Add(new Said(EntryIds.Reply(runId), agentParticipant, now, maskedReply, Replaces: replacingId));

                var nextLog = ConversationLog.Append(State.Log, State.Revision + 1, [.. entriesToAppend]);
                await Persist(State with
                {
                    Log = nextLog,
                    ActiveRun = null,
                    Replacing = null,
                    Runs = Keep(State.Runs, turnState.Run),
                });
                if (maskedSummary.Length > 0) { await Runner.WriteSummary(definition.Contexts, maskedSummary); }
                await Runner.RecordMilestone(definition.Contexts, null, $"Decision: Compacted {compacted.EvictedCount} message(s) into conversation summary.");
                committed = true;
                return;
            }

            await Runner.Hydrate(definition.Contexts, turnState.Run.UserText);
            var preTurnMax = Math.Max(1, Math.Min(definition.MaxHistoryMessages, definition.CompactionThreshold) - 1);
            var bounded = AgentHistoryCompactor.BoundWindow(
                window.Messages,
                definition with { MaxHistoryMessages = Math.Max(2, preTurnMax), CompactionThreshold = Math.Max(2, preTurnMax) },
                Math.Max(0, TurnRunner.MaxHistoryCharacters - AgentHistoryCompactor.MessageSize(input)),
                window.Summary);

            var turnModel = !string.IsNullOrWhiteSpace(model) ? new ModelSelection(model) : definition.Model;
            var plan = new TurnPlan(
                Scope,
                FullKey,
                runId,
                bounded.Messages,
                bounded.Summary,
                input,
                definition.Instructions,
                definition.Contracts,
                definition.Contexts,
                turnModel,
                definition.Options,
                definition.MaxModelCalls,
                Streaming: true)
            {
                AgentName = seated.AgentId,
            };

            await foreach (var ev in Runner.Stream(plan, turnState, sink, lifetime.Token))
            {
                switch (ev)
                {
                    case AgentTurnEvent.ToolStarted tool:
                    {
                        var called = new Called(EntryIds.Call(runId, tool.CallId), agentParticipant, Clock.GetUtcNow(), tool.Name, tool.Arguments);
                        var nextLog = ConversationLog.Append(State.Log, State.Revision + 1, called);
                        await Persist(State with { Log = nextLog, ActiveRun = turnState.Run });
                        break;
                    }
                    case AgentTurnEvent.ToolCompleted tool:
                    {
                        var refusal = TurnRunner.ToolRefusal(tool.Result);
                        var ok = refusal is null;
                        var card = ok ? ConversationCards.FromResult(tool.Result) : null;
                        var compactedResult = TurnRunner.CompactToolResult(tool.Result);
                        var returned = new Returned(EntryIds.Result(runId, tool.CallId), agentParticipant, Clock.GetUtcNow(), tool.Name, compactedResult, ok, card);
                        var nextLog = ConversationLog.Append(State.Log, State.Revision + 1, returned);
                        await Persist(State with { Log = nextLog, ActiveRun = turnState.Run });
                        break;
                    }
                    case AgentTurnEvent.ToolFailed tool:
                    {
                        var errorJson = TurnRunner.SerializeToolError(tool.Code, tool.Message);
                        var returned = new Returned(EntryIds.Result(runId, tool.CallId), agentParticipant, Clock.GetUtcNow(), tool.Name, errorJson, Ok: false, Card: null);
                        var nextLog = ConversationLog.Append(State.Log, State.Revision + 1, returned);
                        await Persist(State with { Log = nextLog, ActiveRun = turnState.Run });
                        break;
                    }
                    case AgentTurnEvent.Completed done:
                    {
                        var combinedWindow = (AiMessage[])[.. window.Messages, input, .. done.Messages];
                        var compacted = await AgentHistoryCompactor.CompactAsync(
                            combinedWindow,
                            definition,
                            window.Summary,
                            focusInstructions: null,
                            force: false,
                            Scope,
                            services,
                            Logger,
                            lifetime.Token);
                        var nextSummary = string.IsNullOrWhiteSpace(compacted.Summary)
                            ? window.Summary
                            : (compacted.Summary == window.Summary ? window.Summary : await Runner.Mask(compacted.Summary));
                        var summarized = nextSummary != window.Summary && nextSummary is { Length: > 0 };

                        var now = Clock.GetUtcNow();
                        var newEntries = new List<ConversationEntry>(2);
                        if (summarized)
                        {
                            var upTo = UpToEntryId(window, compacted.EvictedCount, runId);
                            newEntries.Add(new Summarized(EntryIds.Summary(runId), agentParticipant, now, nextSummary!, upTo));
                        }
                        ModelId? usedModel = turnState.Run.Model is { Model.Length: > 0 } resolved ? new ModelId(resolved.Model) : null;
                        newEntries.Add(new Said(EntryIds.Reply(runId), agentParticipant, now, turnState.Visible, usedModel, replacingId));

                        var nextLog = ConversationLog.Append(State.Log, State.Revision + 1, [.. newEntries]);
                        await Persist(State with
                        {
                            Log = nextLog,
                            ActiveRun = null,
                            Replacing = null,
                            Runs = Keep(State.Runs, turnState.Run),
                        });
                        if (summarized) { await Runner.WriteSummary(definition.Contexts, nextSummary!); }
                        await Runner.RecordMilestone(definition.Contexts, turnState.Run.UserText, turnState.Visible);
                        committed = true;
                        break;
                    }
                    case AgentTurnEvent.TurnFailed failed:
                    {
                        turnState.Failure = failed.Cancelled && timeout.IsCancellationRequested
                            ? new(timedOut, Cancelled: false) { Failure = AgentFailure.TimedOut }
                            : failed;
                        break;
                    }
                }
            }
        }
        catch (Exception error)
        {
            Logger.LogWarning(error, "Conversation turn {RunId} ended with an error.", runId);
        }
        finally
        {
            try
            {
                if (!committed)
                {
                    var expired = timeout.IsCancellationRequested;
                    var failure = turnState.Failure;
                    var cancelled = !expired && (failure is not null ? failure.Cancelled : lifetime.IsCancellationRequested);
                    var error = expired ? timedOut
                        : failure?.Message ?? "Execution failed before committing its result.";
                    try { await Runner.Refresh(turnState, final: true); error = await Runner.Mask(error); }
                    catch (Exception maskError) { Logger.LogWarning(maskError, "Conversation run {RunId} could not mask its outcome.", runId); error = "Execution failed before committing its result."; }

                    var failureKind = expired
                        ? AgentFailure.TimedOut
                        : (cancelled ? AgentFailure.Unknown : (failure?.Failure ?? AgentFailure.Unknown));
                    var closedRun = Close(turnState.Streamed, cancelled ? AgentRunStatus.Cancelled : AgentRunStatus.Failed, error, cancelled ? "tool_cancelled" : "tool_interrupted")
                        with
                    { Failure = failureKind };
                    try
                    {
                        await Persist(State with
                        {
                            ActiveRun = null,
                            Replacing = null,
                            Runs = Keep(State.Runs, closedRun),
                        });
                    }
                    catch (Exception persistError)
                    {
                        Logger.LogWarning(persistError, "Failed to persist final conversation run {RunId}.", runId);
                        Abandon(persistError, runId);
                    }
                }
            }
            finally { _active = null; }
        }
    }

    private async Task TouchSessionListBestEffortAsync(string text)
    {
        if (!Sessions.IsId(ConvId.Value)) { return; }
        try
        {
            await Context.Get<ISessionList>(new NeuronId(Id.Brain, Sessions.Type, ISessionList.DefaultKey))
                .Touch(ConvId.Value, text);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Logger.LogDebug(error, "Session list could not be touched for conversation {ConversationId}.", ConvId);
        }
    }

    private async Task<string> ReadTitleBestEffortAsync()
    {
        if (!Sessions.IsId(ConvId.Value)) { return ConvId.Value; }
        try
        {
            var session = await Context.Get<ISessionList>(new NeuronId(Id.Brain, Sessions.Type, ISessionList.DefaultKey))
                .Get(ConvId.Value);
            return !string.IsNullOrWhiteSpace(session?.Title) ? session.Title : Sessions.NewChat;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Logger.LogDebug(error, "Session list could not be read for conversation {ConversationId}.", ConvId);
            return Sessions.NewChat;
        }
    }

    // Prior history strictly before the latest person prompt (covers both Say and Regenerate without duplicating plan.Input).
    private static LoggedEntry[] PriorLogForTurn(LoggedEntry[] log)
    {
        for (var i = log.Length - 1; i >= 0; i--)
        {
            if (log[i].Entry is Said { By: Participant.Human })
            {
                return log[..i];
            }
        }
        return log;
    }

    private static EntryId UpToEntryId(ModelWindow window, int evictedCount, string runId)
    {
        if (evictedCount > 0 && evictedCount <= window.Sources.Length)
        {
            return window.Sources[evictedCount - 1];
        }
        if (window.Sources.Length > 0)
        {
            return window.Sources[^1];
        }
        return EntryIds.Said(runId);
    }

    private static string[] AllContexts(ConversationState state)
        => [.. state.Agents.SelectMany(static a => a.Definition.Contexts).Distinct(StringComparer.Ordinal)];

    private static Participant[] WithParticipants(Participant[] existing, params Participant[] additions)
    {
        List<Participant>? list = null;
        foreach (var addition in additions)
        {
            var present = list is null ? existing.Contains(addition) : list.Contains(addition);
            if (!present)
            {
                list ??= [.. existing];
                list.Add(addition);
            }
        }
        return list is null ? existing : [.. list];
    }

    private static void ValidateDefinition(AgentDefinition definition)
    {
        if (definition.MaxModelCalls is < 1 or > 512 || definition.Timeout <= TimeSpan.Zero || definition.Timeout > TimeSpan.FromMinutes(60)
            || definition.MaxHistoryMessages is < 2 or > 4096
            || definition.CompactionThreshold is < 2 or > 4096
            || definition.CompactionRecentWindow is < 1 or > 4096)
        {
            throw new ArgumentException("Invalid agent execution limits.", nameof(definition));
        }

        if (definition.Instructions.Length > 32_000 || definition.Contracts.Length > 128 || definition.Contracts.Any(string.IsNullOrWhiteSpace)
            || definition.Contracts.Distinct(StringComparer.Ordinal).Count() != definition.Contracts.Length
            || definition.Contexts.Length > 32 || definition.Contexts.Any(key => string.IsNullOrWhiteSpace(key) || key.Length > 512))
        {
            throw new ArgumentException("Instructions, contracts or contexts are invalid.", nameof(definition));
        }
    }

    private static AgentDefinition NormalizeDefinition(AgentDefinition definition) => definition with
    {
        Contracts = [.. definition.Contracts],
        Capabilities = [.. definition.Capabilities],
        RoutingExamples = [.. definition.RoutingExamples],
        Contexts = [.. definition.Contexts],
    };

    private static bool SameDefinition(AgentDefinition a, AgentDefinition b)
        => string.Equals(a.DisplayName, b.DisplayName, StringComparison.Ordinal)
            && string.Equals(a.Description, b.Description, StringComparison.Ordinal)
            && string.Equals(a.Instructions, b.Instructions, StringComparison.Ordinal)
            && Equals(a.Model, b.Model)
            && Equals(a.Options, b.Options)
            && a.MaxModelCalls == b.MaxModelCalls
            && a.Timeout == b.Timeout
            && a.MaxHistoryMessages == b.MaxHistoryMessages
            && a.CompactionThreshold == b.CompactionThreshold
            && a.CompactionRecentWindow == b.CompactionRecentWindow
            && a.UseModelSummarizer == b.UseModelSummarizer
            && a.Contracts.SequenceEqual(b.Contracts, StringComparer.Ordinal)
            && a.Capabilities.SequenceEqual(b.Capabilities, StringComparer.Ordinal)
            && a.RoutingExamples.SequenceEqual(b.RoutingExamples, StringComparer.Ordinal)
            && a.Contexts.SequenceEqual(b.Contexts, StringComparer.Ordinal);

    private void Abandon(Exception error, string runId)
    {
        Logger.LogError(error, "Conversation run {RunId} could not store its outcome; the conversation recovers from storage on its next call.", runId);
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

    private async Task Persist(ConversationState next)
    {
        var previous = _state;
        var writing = _write = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _beforeWrite = previous;
        _state = next with { Revision = previous.Revision + 1, Blocked = _blocked };
        try
        {
            await Store(_state);
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

        try { await Publish(new ConversationChanged(ConvId, _state.Revision)); }
        catch (Exception error) { Logger.LogWarning(error, "ConversationChanged signal delivery failed; consumers can reconcile through Read."); }
    }
}
