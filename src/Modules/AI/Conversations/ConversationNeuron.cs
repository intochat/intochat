using System.Diagnostics;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Components;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Host.Conversations;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.AI.Attachments;
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
    : InterleavingRunHost<ConversationState>(redactor, clock, services, (ILogger?)logger ?? OsConsoleLog.For<ConversationNeuron>()), IConversation, IConversationDriver
{
    private const int MaxSayCharacters = 32_000;
    private const string ConversationsKeyPrefix = "conversations/";

    private Task? _importing;

    private ConversationDefaults Defaults => Services.GetService<ConversationDefaults>() ?? ConversationDefaults.Generic;
    private string DefaultAgentId => Defaults.DefaultAgentId;
    private IBrainOwners Owners => Services.GetService<IBrainOwners>() ?? throw FriendRelay.Refused();
    private IFriends Friends => Context.Get<IFriends>(FriendsDefaults.Id(Id.Brain.Value));
    private IOffers Offers => Context.Offers;

    protected override string HostKind => "Conversation";
    protected override string BusyMessage => "This conversation already has an active run.";

    protected override AgentRun? ActiveRunOf(ConversationState state) => state.ActiveRun;
    protected override bool BlockedOf(ConversationState state) => state.Blocked;
    protected override string[] ContextsOf(ConversationState state) => AllContexts(state);

    protected override ConversationState WithInterruptedRun(ConversationState state, AgentRun closedRun)
        => state with { ActiveRun = null, Replacing = null, Runs = Keep(state.Runs, closedRun) };

    protected override ConversationState PrepareWrite(ConversationState previous, ConversationState next, bool blocked, bool bumpRevision)
        => next with { Revision = bumpRevision ? previous.Revision + 1 : previous.Revision, Blocked = blocked };

    protected override Task OnStateLoaded(CancellationToken cancellationToken) => SyncPersistedOffersAsync(cancellationToken);

    protected override async Task AfterCommit(ConversationState committed, bool announce)
    {
        if (!announce) { return; }
        try { await Publish(new ConversationChanged(ConvId, committed.Revision)); }
        catch (Exception error) { Logger.LogWarning(error, "ConversationChanged signal delivery failed; consumers can reconcile through Read."); }
    }

    private ConversationId ConvId
    {
        get
        {
            var raw = Id.Key.StartsWith(ConversationsKeyPrefix, StringComparison.Ordinal)
                ? Id.Key[ConversationsKeyPrefix.Length..]
                : Id.Key.Replace('/', '-');
            return ConversationId.TryParse(raw, out var parsed) ? parsed : ConversationId.Parse("main");
        }
    }

    private async Task SyncPersistedOffersAsync(CancellationToken cancellationToken)
    {
        if (State.Participants.Length == 0) { return; }
        var owners = Services.GetService<IBrainOwners>();
        if (owners is null) { return; }
        var ownerAccount = await owners.OwnerAccount(Id.Brain, cancellationToken);
        var offers = Offers;
        foreach (var human in State.Participants.OfType<Participant.Human>())
        {
            if (string.IsNullOrWhiteSpace(human.Account.Value)
                || string.Equals(human.Account.Value, ownerAccount, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                if (await offers.RoleOf(Id, human.Account.Value, cancellationToken) is null)
                {
                    await offers.Offer(Id, human.Account.Value, DigitalBrain.Components.Role.Editor, cancellationToken);
                }
            }
            catch (UnauthorizedAccessException error)
            {
                // Persisted participant is no longer a confirmed friend; leave without an offer.
                Logger.LogSwallowed(error, "conversation participant offer refused");
            }
        }
    }

    private void AuthorizeOwnerOnly() => RequireSameBrain(FriendRelay.Refused);

    private async Task<AccountId> RequireOwnerAsPersonAsync()
    {
        var caller = Context.Caller;
        if (caller is not { Actor: Actor.Person person }
            || string.IsNullOrEmpty(person.Account)
            || caller.Brain != Id.Brain)
        {
            throw FriendRelay.Refused();
        }
        var allowedSurfaces = Defaults.AllowedInviteSurfaces;
        if (caller.Via is { } via && (via.Brain != Id.Brain || allowedSurfaces is null || !allowedSurfaces.Contains(via.Type, StringComparer.Ordinal)))
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
                UpdateLocalState(static s => s with { Imported = true });
                return;
            }

            var legacyPrefix = Defaults.LegacyAgentKeyPrefix;
            if (string.IsNullOrWhiteSpace(legacyPrefix))
            {
                UpdateLocalState(static s => s with { Imported = true });
                return;
            }

            var rawId = Id.Key[ConversationsKeyPrefix.Length..];
            var oldAgent = Context.Get<IAgent>(new NeuronId(Id.Brain, AgentSignals.NeuronType, legacyPrefix + rawId));
            var oldState = await oldAgent.Read();
            if (oldState.Revision == 0 && oldState.Runs.Length == 0 && string.IsNullOrWhiteSpace(oldState.Summary))
            {
                UpdateLocalState(static s => s with { Imported = true });
                return;
            }

            var transcript = oldState.Runs.Length > 0 ? await oldAgent.Transcript() : [];
            var ownerAccount = CallerAccount();
            var (importedEntries, importedRuns) = LegacyImport.From(oldState, transcript, ownerAccount, DefaultAgentId);

            var nextAgents = State.Agents.Any(a => string.Equals(a.AgentId, DefaultAgentId, StringComparison.Ordinal))
                ? State.Agents
                : [.. State.Agents, new SeatedAgent(DefaultAgentId, AgentDefinitionRules.Normalize(oldState.Definition))];

            await EnsureContextWires([.. nextAgents.SelectMany(a => a.Definition.Contexts).Distinct(StringComparer.Ordinal)]);

            var nextParticipants = WithParticipants(State.Participants, new Participant.Human(ownerAccount), new Participant.Assistant(DefaultAgentId));
            var nextRevision = State.Revision + 1;
            var nextLog = importedEntries.Length > 0
                ? ConversationLog.Append(State.Log, nextRevision, importedEntries)
                : State.Log;
            var nextRuns = importedRuns.Length > KeptRuns
                ? importedRuns[(importedRuns.Length - KeptRuns)..]
                : importedRuns;

            SetBlockedCache(oldState.Blocked);
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
        AgentDefinitionRules.Validate(definition);
        await Fresh();

        var trimmedId = agentId.Trim();
        var normalized = AgentDefinitionRules.Normalize(definition);
        var existingIndex = Array.FindIndex(State.Agents, a => string.Equals(a.AgentId, trimmedId, StringComparison.Ordinal));
        if (existingIndex >= 0 && AgentDefinitionRules.AreEqual(State.Agents[existingIndex].Definition, normalized))
        {
            return;
        }

        EnsureIdle();
        await EnsureContextWires(normalized.Contexts);

        var agents = State.Agents.ToList();
        var seated = new SeatedAgent(trimmedId, normalized);
        if (existingIndex >= 0) { agents[existingIndex] = seated; }
        else { agents.Add(seated); }

        var participants = WithParticipants(State.Participants, new Participant.Assistant(trimmedId));
        await Mutate(() => Persist(State with
        {
            Participants = participants,
            Agents = [.. agents],
        }));
        await Reconsider();
    }

    public async Task Post(string text, string id, Visibility visibility = Visibility.Everyone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        AuthorizeOwnerOnly();
        await Fresh();
        await EnsureImportedAsync();

        var entryId = EntryIds.Post(id);
        if (IsMutating) { throw new InvalidOperationException("The conversation is answering; post again when the run ends."); }
        if (State.Log.Any(item => item.Entry.Id == entryId)) { return; }
        EnsureIdle("The conversation is answering; post again when the run ends.");

        var masked = await Runner.Mask(text);
        var agentId = State.Agents.FirstOrDefault()?.AgentId ?? DefaultAgentId;
        var entry = new Said(entryId, new Participant.Assistant(agentId), Clock.GetUtcNow(), masked, Visible: visibility);
        var nextLog = ConversationLog.Append(State.Log, State.Revision + 1, entry);
        var participants = WithParticipants(State.Participants, new Participant.Assistant(agentId));

        await Mutate(() => Persist(State with
        {
            Participants = participants,
            Log = nextLog,
        }));
    }

    public async Task Voiced(string heard, string reply, ModelId model, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(heard);
        ArgumentException.ThrowIfNullOrWhiteSpace(reply);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (string.IsNullOrWhiteSpace(model.Value))
        {
            throw new ArgumentException("Voice exchange requires a model id.", nameof(model));
        }

        if (Context.Caller is { Actor: not Actor.Person })
        {
            throw new DigitalBrain.Rules.RuleViolationException(
                new DigitalBrain.Rules.Rejected("voiced", ["person-only"], ["Only a person participating in the conversation may record a voice exchange."]));
        }

        AuthorizeOwnerOnly();
        await Fresh();
        await EnsureImportedAsync();
        EnsureIdle();

        var heardId = EntryIds.Heard(id);
        var spokenId = EntryIds.Spoken(id);
        if (State.Log.Any(item => item.Entry.Id == heardId || item.Entry.Id == spokenId))
        {
            return;
        }

        var maskedHeard = await Mutate(async () =>
        {
            var masked = await Runner.Mask(heard);
            var maskedReply = await Runner.Mask(reply);
            var now = Clock.GetUtcNow();
            var speaker = new Participant.Human(CallerAccount());
            var agentId = State.Agents.FirstOrDefault()?.AgentId ?? DefaultAgentId;
            var assistant = new Participant.Assistant(agentId);
            var heardEntry = new Said(heardId, speaker, now, masked);
            var spokenEntry = new Said(spokenId, assistant, now, maskedReply, model);
            var nextLog = ConversationLog.Append(State.Log, State.Revision + 1, heardEntry, spokenEntry);
            var participants = WithParticipants(State.Participants, speaker, assistant);

            await Persist(State with
            {
                Participants = participants,
                Log = nextLog,
            });
            return masked;
        });

        await TouchSessionListBestEffortAsync(maskedHeard);
    }

    public async Task Say(string text, ModelId? model = null, AttachmentRef[]? attachments = null)
    {
        // The owner's view, or a friend's; a call with no caller is neither.
        if (RequireCaller().Brain != Id.Brain)
        {
            await SayAsFriendAsync(text, attachments);
            return;
        }

        var hasAttachments = attachments is { Length: > 0 };
        if (hasAttachments)
        {
            AttachmentValidator.ValidateTurnAttachments(attachments);
        }

        var normalizedText = text ?? string.Empty;
        if ((!hasAttachments && string.IsNullOrEmpty(normalizedText)) || normalizedText.Length > MaxSayCharacters)
        {
            throw new ArgumentException($"Message text must contain 1 to {MaxSayCharacters} characters.", nameof(text));
        }

        await Fresh();
        await EnsureImportedAsync();
        EnsureUnblocked();
        EnsureIdle();
        await EnforceTrialTurnPolicyAsync();

        var sanitizedAttachments = hasAttachments
            ? await SanitizeAndAuthorizeAttachmentsAsync(attachments!)
            : null;
        var masked = string.IsNullOrEmpty(normalizedText) ? string.Empty : await Runner.Mask(normalizedText);
        var runId = Guid.NewGuid().ToString("N");
        var now = Clock.GetUtcNow();
        var speaker = new Participant.Human(CallerAccount());
        var said = new Said(EntryIds.Said(runId), speaker, now, masked, Attachments: sanitizedAttachments);
        var nextLog = ConversationLog.Append(State.Log, State.Revision + 1, said);
        var participants = WithParticipants(State.Participants, speaker);
        var runSummaryText = SummarizePromptText(masked, sanitizedAttachments);
        var activeRun = new AgentRun(runId, AgentRunStatus.Running, runSummaryText, now)
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

        await TouchSessionListBestEffortAsync(runSummaryText);

        try
        {
            await Context.Get<IConversationDriver>(Id).Drive(runId, model?.Value, null);
        }
        catch
        {
            await UndoHandOffAsync(State with { ActiveRun = null, Replacing = null }, runId);
            throw;
        }
    }

    private async Task<AttachmentRef[]> SanitizeAndAuthorizeAttachmentsAsync(AttachmentRef[] attachments)
    {
        // Fail closed: never accept attachments without verifying ownership and reconstructing metadata from the brain's store.
        var store = Services.GetService<FileAttachmentStore>()
            ?? throw new InvalidOperationException("Attachment store is unavailable; attachments cannot be verified.");

        var result = new AttachmentRef[attachments.Length];
        long totalStoredBytes = 0;
        for (var i = 0; i < attachments.Length; i++)
        {
            var item = attachments[i];
            var meta = await store.TryReadMetadataAsync(Id.Brain, item.Id, CancellationToken.None)
                ?? throw new UnauthorizedAccessException($"Attachment '{item.Id}' is not accessible from this brain.");

            var maxForKind = AttachmentValidator.MaxBytesForKind(meta.Kind);
            if (meta.SizeBytes <= 0 || meta.SizeBytes > maxForKind)
            {
                throw new ArgumentException($"Stored attachment '{meta.Id}' exceeds the {maxForKind} byte limit.", nameof(attachments));
            }

            totalStoredBytes += meta.SizeBytes;
            if (totalStoredBytes > AttachmentValidator.MaxTotalTurnAttachmentBytes)
            {
                throw new ArgumentException(
                    $"Combined stored attachments exceed the {AttachmentValidator.MaxTotalTurnAttachmentBytes / (1024 * 1024)} MiB per-turn limit.",
                    nameof(attachments));
            }

            var safeFileName = AttachmentValidator.SanitizeFileName(meta.FileName);
            var safeMime = AttachmentValidator.NormalizeMediaType(meta.MediaType);
            var maskedTranscript = string.IsNullOrWhiteSpace(meta.Transcript)
                ? null
                : await Runner.Mask(meta.Transcript);

            // Reconstruct strictly from server-stored metadata (never trust client-supplied sizes, MIME, URL, or transcripts).
            result[i] = new AttachmentRef(
                Id: meta.Id,
                Kind: meta.Kind,
                FileName: safeFileName,
                MediaType: safeMime,
                SizeBytes: meta.SizeBytes,
                Url: $"/ai/attachments/{meta.Id}",
                DurationMs: meta.DurationMs,
                Transcript: maskedTranscript,
                Sha256: meta.Sha256);
        }

        return result;
    }

    private static string SummarizePromptText(string maskedText, AttachmentRef[]? attachments)
    {
        if (!string.IsNullOrWhiteSpace(maskedText))
        {
            return maskedText;
        }

        if (attachments is { Length: > 0 })
        {
            var firstTranscript = attachments.FirstOrDefault(static a => !string.IsNullOrWhiteSpace(a.Transcript))?.Transcript;
            if (!string.IsNullOrWhiteSpace(firstTranscript))
            {
                return firstTranscript;
            }

            return attachments.Length == 1
                ? $"[{attachments[0].Kind}: {attachments[0].FileName}]"
                : $"[{attachments.Length} attachments]";
        }

        return string.Empty;
    }

    private async Task SayAsFriendAsync(string text, AttachmentRef[]? attachments = null)
    {
        await Fresh();
        var account = Context.Caller?.For?.Account;
        if (string.IsNullOrWhiteSpace(account))
        {
            throw FriendRelay.Refused();
        }
        var friend = new AccountId(account);
        if (attachments is { Length: > 0 })
        {
            throw new UnauthorizedAccessException("Cross-brain friend messages cannot attach owner brain blobs directly.");
        }
        if (string.IsNullOrEmpty(text) || text.Length > MaxSayCharacters)
        {
            throw new ArgumentException($"Message text must contain 1 to {MaxSayCharacters} characters.", nameof(text));
        }

        var masked = await Runner.Mask(text);
        await AwaitPendingWrite();

        var said = new Said(EntryIds.Said(Guid.NewGuid().ToString("N")), new Participant.Human(friend), Clock.GetUtcNow(), masked);
        var nextLog = ConversationLog.Append(State.Log, State.Revision + 1, said);
        await Persist(State with { Log = nextLog });
    }

    public Task Stop()
    {
        AuthorizeOwnerOnly();
        CancelActiveRun();
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

        await EnforceTrialTurnPolicyAsync();

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
            await UndoHandOffAsync(State with { ActiveRun = null, Replacing = null }, newRunId);
            throw;
        }
    }

    public async Task<ConversationView> Read(long? sinceRevision = null)
    {
        // The owner's view, or a friend's; a call with no caller is neither.
        if (RequireCaller().Brain != Id.Brain)
        {
            var committed = await ReadCommittedAsync();
            var shared = ConversationLog.Visible(committed.Log, sinceRevision, owner: false);
            return new ConversationView(ConvId, committed.Revision, [.. committed.Participants], shared, Active: null, Runs: [], Blocked: false, Contexts: []);
        }

        await Fresh();
        await EnsureImportedAsync();
        await AwaitPendingWrite();
        var state = Committed;
        var visible = ConversationLog.Visible(state.Log, sinceRevision, owner: true);
        return new ConversationView(
            ConvId,
            state.Revision,
            [.. state.Participants],
            visible,
            state.ActiveRun,
            [.. state.Runs],
            IsBlocked,
            AllContexts(state))
        {
            Replacing = state.Replacing,
            BlockedReason = IsBlocked ? InterruptReason : null,
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
        await Offers.Offer(Id, friend.Value, DigitalBrain.Components.Role.Editor);

        var title = await ReadTitleBestEffortAsync();
        var invite = new GroupInvite(Id.Brain, ConvId, title, owner);
        await SiloNeurons.Get<IFriends>(Services, FriendsDefaults.Id(target.Brain.Value)).Invited(invite);
    }

    public async Task Drive(string runId, string? model, string? replaces)
    {
        if (State.ActiveRun?.RunId != runId || IsActive)
        {
            Logger.LogWarning("Conversation run {RunId} was no longer active when its driver arrived.", runId);
            return;
        }

        var seated = State.Agents.FirstOrDefault() ?? new SeatedAgent(DefaultAgentId, new AgentDefinition());
        var definition = seated.Definition;
        var agentParticipant = new Participant.Assistant(seated.AgentId);
        EntryId? replacingId = string.IsNullOrWhiteSpace(replaces) ? null : new EntryId(replaces);

        using var activeRun = BeginRun(runId, definition.Timeout);
        var turnState = new TurnState(State.ActiveRun);
        var committed = false;
        var deltaCount = 0;
        var storeCount = 0;
        double? firstTokenMs = null;
        var streamStarted = false;
        var streamStart = 0L;
        var replyEntry = EntryIds.Reply(runId);

        var sink = new ConversationTurnSink(
            async updatedRun =>
            {
                storeCount++;
                await Persist(State with { ActiveRun = updatedRun }, announce: false);
            },
            async (delta, at) =>
            {
                deltaCount++;
                firstTokenMs ??= Math.Round(Clock.GetElapsedTime(streamStart).TotalMilliseconds, 1);
                try { await PublishEphemeral(new TextDelta(ConvId, replyEntry, delta) { At = at }); }
                catch (Exception error) { Logger.LogDebug(error, "TextDelta ephemeral offer failed for run {RunId}; clients heal through Read.", runId); }
            });

        try
        {
            var priorLog = PriorLogForTurn(State.Log);
            var window = ModelContext.From(priorLog);
            var latestHumanSaid = LatestHumanSaidForTurn(State.Log);
            var input = latestHumanSaid is { Attachments.Length: > 0 }
                ? await BuildMultimodalInputAsync(latestHumanSaid, activeRun.Token)
                : new AiMessage("user", [new AiText(turnState.Run.UserText)]);

            if (AgentHistoryCompactor.TryParseCompactCommand(turnState.Run.UserText, out var compactFocus))
            {
                var previousCount = window.Messages.Length;
                var compacted = await CompactHistoryAsync(window.Messages, definition, window.Summary, compactFocus, force: true, activeRun.Token);
                var maskedSummary = string.IsNullOrWhiteSpace(compacted.Summary) ? (window.Summary ?? "") : await Runner.Mask(compacted.Summary);
                var replyText = FormatCompactReply(previousCount, compacted.EvictedCount, compacted.Messages.Length, maskedSummary);
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
            var bounded = BoundPreTurnHistory(window.Messages, definition, input, window.Summary);
            var turnModel = !string.IsNullOrWhiteSpace(model)
                ? (definition.Model is not null ? definition.Model with { Model = model } : new ModelSelection(model))
                : definition.Model;
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
                IncludeComponentOperations = definition.IncludeComponentOperations,
                AlwaysOnOperationApps = definition.OperationApps,
                ToolSources = definition.ToolSources,
            };

            streamStart = Clock.GetTimestamp();
            streamStarted = true;
            await foreach (var ev in Runner.Stream(plan, turnState, sink, activeRun.Token))
            {
                switch (ev)
                {
                    case AgentTurnEvent.ToolStarted tool:
                        {
                            var called = new Called(EntryIds.Call(runId, tool.CallId), agentParticipant, Clock.GetUtcNow(), tool.Name, tool.Arguments);
                            var nextLog = ConversationLog.Append(State.Log, State.Revision + 1, called);
                            storeCount++;
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
                            storeCount++;
                            await Persist(State with { Log = nextLog, ActiveRun = turnState.Run });
                            break;
                        }
                    case AgentTurnEvent.ToolFailed tool:
                        {
                            var errorJson = TurnRunner.SerializeToolError(tool.Code, tool.Message);
                            var returned = new Returned(EntryIds.Result(runId, tool.CallId), agentParticipant, Clock.GetUtcNow(), tool.Name, errorJson, Ok: false, Card: null);
                            var nextLog = ConversationLog.Append(State.Log, State.Revision + 1, returned);
                            storeCount++;
                            await Persist(State with { Log = nextLog, ActiveRun = turnState.Run });
                            break;
                        }
                    case AgentTurnEvent.Completed done:
                        {
                            var combinedWindow = (AiMessage[])[.. window.Messages, input, .. done.Messages];
                            var compacted = await CompactHistoryAsync(combinedWindow, definition, window.Summary, focusInstructions: null, force: false, activeRun.Token);
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
                            storeCount++;
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
                        turnState.Failure = activeRun.NormalizeFailure(failed);
                        break;
                }
            }
        }
        catch (Exception error)
        {
            Logger.LogWarning(error, "Conversation turn {RunId} ended with an error.", runId);
        }
        finally
        {
            if (!committed)
            {
                var closed = await FinalizeFailedRunAsync(activeRun, turnState, sink, runId);
                storeCount++;
                await TryPersistClosedRunAsync(
                    State with
                    {
                        ActiveRun = null,
                        Replacing = null,
                        Runs = Keep(State.Runs, closed.Run),
                    },
                    runId);
            }
            if (streamStarted)
            {
                var maskMs = Math.Round(Stopwatch.GetElapsedTime(0, turnState.MaskTicks).TotalMilliseconds, 1);
                Logger.LogInformation(
                    "Run {RunId} streamed {Deltas} deltas in {Stores} stores; first token {FirstTokenMs} ms; masking {MaskMs} ms",
                    runId, deltaCount, storeCount, firstTokenMs ?? 0d, maskMs);
            }
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

    private static Said? LatestHumanSaidForTurn(LoggedEntry[] log)
    {
        for (var i = log.Length - 1; i >= 0; i--)
        {
            if (log[i].Entry is Said { By: Participant.Human } said)
            {
                return said;
            }
        }
        return null;
    }

    private async Task<AiMessage> BuildMultimodalInputAsync(Said said, CancellationToken ct)
    {
        var store = Services.GetService<FileAttachmentStore>();
        var resolvedImages = new Dictionary<string, AiImage>(StringComparer.Ordinal);
        if (store is not null && said.Attachments is { Length: > 0 })
        {
            foreach (var attachment in said.Attachments)
            {
                if (string.Equals(attachment.Kind, AttachmentValidator.KindImage, StringComparison.Ordinal))
                {
                    var dataUri = await store.TryReadImageDataUriAsync(Id.Brain, attachment.Id, ct);
                    if (!string.IsNullOrWhiteSpace(dataUri))
                    {
                        resolvedImages[attachment.Id] = new AiImage(dataUri, attachment.MediaType);
                    }
                }
            }
        }

        var content = ModelContext.ProjectSaidContent(
            said,
            a => resolvedImages.TryGetValue(a.Id, out var img) ? img : null);
        return new AiMessage("user", content);
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

    private sealed class ConversationTurnSink(Func<AgentRun, Task> store, Func<string, int, Task> shown) : ITurnSink
    {
        public Task Store(AgentRun run) => store(run);
        public bool Streams => true;
        public Task Shown(string delta, int at) => shown(delta, at);
    }
}
