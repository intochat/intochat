using System.Diagnostics;
using System.Reflection;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Components;
using DigitalBrain.Contracts;
using DigitalBrain.Invocation;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Host.Conversations;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Attachments;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.AI.Conversations;

[Neuron(IConversation.Type)]
internal sealed class ConversationNeuron(
    IServiceProvider services,
    ISecretRedactor? redactor = null,
    TimeProvider? clock = null,
    ILogger<ConversationNeuron>? logger = null)
    : InterleavingRunHost<ConversationState>(
        redactor ?? (services is not null ? new SecretRedactor(services) : NoOpRedactor.Instance),
        clock ?? services?.GetService<TimeProvider>() ?? DefaultConversationClock.Instance,
        services!,
        (ILogger?)logger ?? OsConsoleLog.For<ConversationNeuron>()), IConversation, IConversationDriver
{
    private sealed class NoOpRedactor : ISecretRedactor
    {
        public static NoOpRedactor Instance { get; } = new();
        public ValueTask<string?> Redact(string? text, CancellationToken cancellationToken) => ValueTask.FromResult(text);
        public ValueTask<int> TailLength(CancellationToken cancellationToken) => ValueTask.FromResult(0);
    }
    private const int MaxSayCharacters = 32_000;
    private const int MaxActivityPreviewCharacters = 256;
    private const int MaxEmojiCharacters = 32;
    private const int MaxRoomAgentContextMessages = 30;
    private const string ConversationsKeyPrefix = ConversationMigrationService.ConversationsKeyPrefix;
    private static readonly TimeSpan PresenceTtl = TimeSpan.FromMinutes(2);

    private readonly Dictionary<string, DateTimeOffset> _presence = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sharedWriteGate = new(1, 1);
    private Task? _importing;
    private (long Revision, string Text)? _pendingActivity;

    private ConversationDefaults Defaults => Services.GetService<ConversationDefaults>() ?? ConversationDefaults.Generic;
    private string DefaultAgentId => Defaults.DefaultAgentId;
    private IOffers Offers => Context.Offers;
    private IConversationMigrationService? MigrationService
        => Services.GetService<IConversationMigrationService>();

    protected override string HostKind => "Conversation";
    protected override string BusyMessage => "This conversation already has an active run.";

    protected override AgentRun? ActiveRunOf(ConversationState state) => state.ActiveRun;
    protected override bool BlockedOf(ConversationState state) => state.Blocked;
    protected override string[] ContextsOf(ConversationState state) => AllContexts(state);

    protected override ConversationState WithInterruptedRun(ConversationState state, AgentRun closedRun)
        => state with { ActiveRun = null, Replacing = null, Runs = Keep(state.Runs, closedRun) };

    protected override ConversationState PrepareWrite(ConversationState previous, ConversationState next, bool blocked, bool bumpRevision)
        => next with { Revision = bumpRevision ? previous.Revision + 1 : previous.Revision, Blocked = blocked };

    protected override async Task OnStateLoaded(CancellationToken cancellationToken)
    {
        if (State.Members.Length > 0)
        {
            await HearTheRosterAsync();
        }
        await SyncPersistedOffersAsync(cancellationToken);
    }

    protected override async Task AfterCommit(ConversationState committed, bool announce)
    {
        var activityText = _pendingActivity is { } pending && pending.Revision == committed.Revision ? pending.Text : null;
        if (activityText is not null) { _pendingActivity = null; }
        if (!announce) { return; }
        try { await Publish(new ConversationChanged(ConvId, committed.Revision) { ActivityText = activityText }); }
        catch (Exception error) { Logger.LogWarning(error, "ConversationChanged signal delivery failed; consumers can reconcile through Read."); }
    }

    private async Task PersistWithActivityAsync(ConversationState next, string activityText)
    {
        var nextRevision = State.Revision + 1;
        _pendingActivity = (nextRevision, activityText.Length <= MaxActivityPreviewCharacters ? activityText : activityText[..MaxActivityPreviewCharacters]);
        try { await Persist(next); }
        finally
        {
            if (_pendingActivity is { } pending && pending.Revision == nextRevision) { _pendingActivity = null; }
        }
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

    private Task HearTheRosterAsync()
        => Context.Wires.Ensure<FriendRemoved>(FriendsDefaults.Id(Id.Brain.Value));

    [On]
    public async Task On(FriendRemoved removed)
    {
        ArgumentNullException.ThrowIfNull(removed);
        await Fresh();
        var account = removed.Account.Value;
        _presence.Remove(account);
        await Offers.Revoke(Id, account);
        foreach (var pinned in State.PinnedScreens)
        {
            await Offers.Revoke(new NeuronId(Id.Brain, pinned.ScreenType, pinned.ScreenKey), account);
        }

        if (State.Members.All(m => !string.Equals(m.Account, account, StringComparison.Ordinal)))
        {
            return;
        }

        await Persist(State with
        {
            Members = [.. State.Members.Where(m => !string.Equals(m.Account, account, StringComparison.Ordinal))],
            Participants = [.. State.Participants.Where(p => p is not Participant.Human h || !string.Equals(h.Account.Value, account, StringComparison.Ordinal))],
        }, announce: false);
    }

    private async Task SyncPersistedOffersAsync(CancellationToken cancellationToken)
    {
        if (State.TrustPosture != ThreadTrustPosture.CrossBrainShared || State.Members.Length == 0)
        {
            return;
        }

        var owners = Services.GetService<IBrainOwners>();
        if (owners is null) { return; }
        var ownerAccount = await owners.OwnerAccount(Id.Brain, cancellationToken);
        var offers = Offers;
        foreach (var member in State.Members)
        {
            if (string.IsNullOrWhiteSpace(member.Account)
                || string.Equals(member.Account, ownerAccount, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                if (await offers.RoleOf(Id, member.Account, cancellationToken) is null)
                {
                    await offers.Offer(Id, member.Account, member.Role, cancellationToken);
                }
            }
            catch (UnauthorizedAccessException error)
            {
                Logger.LogSwallowed(error, "conversation member offer refused");
            }
        }
    }

    private void AuthorizeOwnerOnly() => RequireSameBrain(FriendRelay.Refused);

    private AccountId CallerAccount()
    {
        var account = Context.Caller?.For?.Account;
        return !string.IsNullOrWhiteSpace(account) ? new AccountId(account) : new AccountId(Id.Brain.Value);
    }

    private bool IsPostureLocked =>
        State.PostureLocked
        || State.Log.Length > 0
        || State.Agents.Length > 0
        || State.Participants.Length > 0
        || State.Members.Length > 0
        || State.PinnedScreens.Length > 0;

    private static void ValidateKindMatchesPosture(ThreadTrustPosture posture, ThreadKind kind)
    {
        var valid = posture switch
        {
            ThreadTrustPosture.OwnerTrusted => kind is ThreadKind.DirectAssistant or ThreadKind.Squad,
            ThreadTrustPosture.CrossBrainShared => kind is ThreadKind.DirectPerson or ThreadKind.SharedRoom,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException($"ThreadKind '{kind}' is incompatible with ThreadTrustPosture '{posture}'.", nameof(kind));
        }
    }

    public async Task Configure(ThreadTrustPosture posture, ThreadKind kind, string? title = null, string? leadAgentId = null)
    {
        AuthorizeOwnerOnly();
        ValidateKindMatchesPosture(posture, kind);
        await Fresh();

        if (IsPostureLocked && State.TrustPosture != posture)
        {
            throw new InvalidOperationException(
                $"ThreadTrustPosture is immutable after creation and cannot be changed from '{State.TrustPosture}' to '{posture}'.");
        }

        var cleanTitle = string.IsNullOrWhiteSpace(title) ? State.Title : title.Trim();
        var cleanLead = string.IsNullOrWhiteSpace(leadAgentId) ? State.LeadAgentId : leadAgentId.Trim();

        await Persist(State with
        {
            TrustPosture = posture,
            Kind = kind,
            PostureLocked = true,
            Title = cleanTitle,
            LeadAgentId = cleanLead,
        }, announce: false);
    }

    private Task EnsureImportedAsync()
    {
        if (State.Imported || State.TrustPosture == ThreadTrustPosture.CrossBrainShared)
        {
            return Task.CompletedTask;
        }
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
            var migrationService = MigrationService;
            if (migrationService is null)
            {
                UpdateLocalState(static s => s with { Imported = true });
                return;
            }

            var ownerAccount = CallerAccount();
            var migrated = await migrationService.MigrateLegacyAgentAsync(Id, ownerAccount, Context.Get<IAgent>);
            if (migrated.Status == ConversationMigrationStatus.Failed) { return; }
            if (migrated.Status == ConversationMigrationStatus.NothingToMigrate)
            {
                UpdateLocalState(static s => s with { Imported = true });
                return;
            }

            var nextState = ConversationMigrationService.ApplyToState(State, migrated, ownerAccount, DefaultAgentId, KeptRuns) with
            {
                PostureLocked = true,
            };
            await EnsureContextWires(AllContexts(nextState));
            SetBlockedCache(migrated.Blocked);
            await Persist(nextState);
            await Reconsider();
        }
        finally { _importing = null; }
    }

    public async Task Seat(string agentId, AgentDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        AgentDefinitionRules.Validate(definition);
        AuthorizeOwnerOnly();
        await Fresh();

        var trimmedId = agentId.Trim();
        var normalized = AgentDefinitionRules.Normalize(definition);
        if (State.TrustPosture == ThreadTrustPosture.CrossBrainShared)
        {
            normalized = SharedRoomAgentPolicy.Confine(normalized, State.Title);
            SharedRoomAgentPolicy.AssertConfined(normalized);
        }

        var existingIndex = Array.FindIndex(State.Agents, a => string.Equals(a.AgentId, trimmedId, StringComparison.Ordinal));
        if (existingIndex >= 0 && AgentDefinitionRules.AreEqual(State.Agents[existingIndex].Definition, normalized))
        {
            return;
        }

        EnsureIdle();
        if (State.TrustPosture == ThreadTrustPosture.OwnerTrusted)
        {
            await EnsureContextWires(normalized.Contexts);
        }

        var agents = State.Agents.ToList();
        var seated = new SeatedAgent(trimmedId, normalized);
        if (existingIndex >= 0) { agents[existingIndex] = seated; }
        else { agents.Add(seated); }

        var participants = WithParticipants(State.Participants, new Participant.Assistant(trimmedId));
        await Mutate(() => Persist(State with
        {
            PostureLocked = true,
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
        var agentDisplay = State.Agents.FirstOrDefault()?.Definition.DisplayName ?? agentId;
        var entry = new Said(entryId, new Participant.Assistant(agentId), Clock.GetUtcNow(), masked, Sender: new SenderIdentity(agentId, agentDisplay, SenderKind.Agent), Visible: visibility);
        var nextLog = ConversationLog.Append(State.Log, State.Revision + 1, entry);
        var participants = WithParticipants(State.Participants, new Participant.Assistant(agentId));

        await Mutate(() => Persist(State with
        {
            PostureLocked = true,
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

        await Mutate(async () =>
        {
            var masked = await Runner.Mask(heard);
            var maskedReply = await Runner.Mask(reply);
            var now = Clock.GetUtcNow();
            var callerAccount = CallerAccount();
            var speaker = new Participant.Human(callerAccount);
            var agentId = State.Agents.FirstOrDefault()?.AgentId ?? DefaultAgentId;
            var agentDisplay = State.Agents.FirstOrDefault()?.Definition.DisplayName ?? agentId;
            var assistant = new Participant.Assistant(agentId);
            var heardEntry = new Said(heardId, speaker, now, masked, Sender: new SenderIdentity(callerAccount.Value, callerAccount.Value, SenderKind.Self));
            var spokenEntry = new Said(spokenId, assistant, now, maskedReply, model, Sender: new SenderIdentity(agentId, agentDisplay, SenderKind.Agent));
            var nextLog = ConversationLog.Append(State.Log, State.Revision + 1, heardEntry, spokenEntry);
            var participants = WithParticipants(State.Participants, speaker, assistant);

            await PersistWithActivityAsync(
                State with
                {
                    PostureLocked = true,
                    Participants = participants,
                    Log = nextLog,
                },
                masked);
        });
    }

    public async Task Say(
        string text,
        ModelId? model = null,
        AttachmentRef[]? attachments = null,
        string? messageId = null,
        EntryId? replyTo = null)
    {
        await Fresh();

        if (State.TrustPosture == ThreadTrustPosture.CrossBrainShared)
        {
            await SayInSharedThreadAsync(text, attachments, messageId, replyTo);
            return;
        }

        // OwnerTrusted thread: strictly owner-only.
        if (RequireCaller().Brain != Id.Brain)
        {
            throw new UnauthorizedAccessException("Personal OwnerTrusted conversations refuse foreign callers.");
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

        await EnsureImportedAsync();

        var callerAccount = CallerAccount();
        var speaker = new Participant.Human(callerAccount);
        var cleanMessageId = string.IsNullOrWhiteSpace(messageId) ? null : messageId.Trim();
        if (cleanMessageId is not null && ConversationLog.HasMessageId(State.Log, cleanMessageId, speaker))
        {
            return;
        }

        EnsureUnblocked();
        EnsureIdle();
        await EnforceTrialTurnPolicyAsync();

        var sanitizedAttachments = hasAttachments
            ? await SanitizeAndAuthorizeAttachmentsAsync(attachments!)
            : null;
        var masked = string.IsNullOrEmpty(normalizedText) ? string.Empty : await Runner.Mask(normalizedText);
        var runId = Guid.NewGuid().ToString("N");
        var now = Clock.GetUtcNow();
        var said = new Said(
            EntryIds.Said(runId),
            speaker,
            now,
            masked,
            Sender: new SenderIdentity(callerAccount.Value, callerAccount.Value, SenderKind.Self),
            Attachments: sanitizedAttachments,
            MessageId: cleanMessageId,
            ReplyTo: replyTo);
        var nextLog = ConversationLog.Append(State.Log, State.Revision + 1, said);
        var participants = WithParticipants(State.Participants, speaker);
        var runSummaryText = SummarizePromptText(masked, sanitizedAttachments);
        var activeRun = new AgentRun(runId, AgentRunStatus.Running, runSummaryText, now)
        {
            TraceId = TraceContext.Current?.TraceId,
        };

        var targetAgentId = ResolveSquadTargetAgent(masked);

        await PersistWithActivityAsync(
            State with
            {
                PostureLocked = true,
                Participants = participants,
                Log = nextLog,
                ActiveRun = activeRun,
                Replacing = null,
            },
            runSummaryText);

        try
        {
            await Context.Get<IConversationDriver>(Id).Drive(runId, model?.Value, null, targetAgentId, depth: 0);
        }
        catch
        {
            await UndoHandOffAsync(State with { ActiveRun = null, Replacing = null }, runId);
            throw;
        }
    }

    private async Task SayInSharedThreadAsync(
        string text,
        AttachmentRef[]? attachments,
        string? messageId,
        EntryId? replyTo)
    {
        var hasAttachments = attachments is { Length: > 0 };
        var trimmed = text?.Trim() ?? string.Empty;
        if (!hasAttachments && string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("Shared message requires non-empty text or at least one attachment.", nameof(text));
        }

        if (trimmed.Length > MaxSayCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(text), $"Message exceeds the {MaxSayCharacters}-character limit.");
        }

        var author = await RequireSeat(requireEditor: true);
        if (hasAttachments)
        {
            AttachmentValidator.ValidateTurnAttachments(attachments);
        }

        var speaker = new Participant.Human(new AccountId(author));
        var cleanMessageId = string.IsNullOrWhiteSpace(messageId) ? null : messageId.Trim();
        if (cleanMessageId is not null && ConversationLog.HasMessageId(State.Log, cleanMessageId, speaker))
        {
            return;
        }

        var masked = string.IsNullOrEmpty(trimmed) ? string.Empty : await Runner.Mask(trimmed);
        AttachmentRef[]? sanitizedAttachments = null;
        if (hasAttachments)
        {
            sanitizedAttachments = IsSameBrainCaller()
                ? await SealHostSharedAttachmentsAsync(attachments!)
                : await ReceiveForeignSharedAttachmentsAsync(attachments!, RequireCaller().Brain);
        }

        long committedRevision;
        await _sharedWriteGate.WaitAsync();
        try
        {
            await AwaitPendingWrite();
            if (cleanMessageId is not null && ConversationLog.HasMessageId(State.Log, cleanMessageId, speaker))
            {
                return;
            }

            var said = new Said(
                EntryIds.Said(Guid.NewGuid().ToString("N")),
                speaker,
                Clock.GetUtcNow(),
                masked,
                Sender: new SenderIdentity(author, author, SenderKind.Human),
                Attachments: sanitizedAttachments,
                MessageId: cleanMessageId,
                ReplyTo: replyTo);
            var nextLog = ConversationLog.Append(State.Log, State.Revision + 1, said);
            var participants = WithParticipants(State.Participants, speaker);

            using (OwnModuleIfForeign())
            {
                await Persist(State with
                {
                    PostureLocked = true,
                    Participants = participants,
                    Log = nextLog,
                });
            }

            committedRevision = State.Revision;
        }
        finally
        {
            _sharedWriteGate.Release();
        }

        await NudgeDirectoriesAsync(committedRevision);

        // Shared rooms enforce explicit @agentId mention trigger ONLY; normal human messages trigger 0 agent turns.
        if (!string.IsNullOrEmpty(masked) && ResolveMentionedSharedAgent(masked) is { } mentionedAgent)
        {
            using var module = OwnModuleIfForeign();
            _ = Context.Get<IConversationDriver>(Id).DriveSharedRoomAgent(committedRevision, mentionedAgent);
        }
    }

    private async Task<AttachmentRef[]> SealHostSharedAttachmentsAsync(AttachmentRef[] attachments)
    {
        var sanitized = await SanitizeAndAuthorizeAttachmentsAsync(attachments);
        if (State.Members.Length == 0)
        {
            return sanitized;
        }

        var sharedRelay = Services.GetService<SharedAttachmentRelay>();
        var signer = Services.GetService<IBrainKeySigner>();
        if (sharedRelay is not { IsAvailable: true } || signer is null)
        {
            return sanitized;
        }

        var recipients = await ResolveSharedBlobRecipientsAsync(signer);
        if (recipients.Count == 0)
        {
            return sanitized;
        }

        var sealedRefs = new AttachmentRef[sanitized.Length];
        for (var i = 0; i < sanitized.Length; i++)
        {
            sealedRefs[i] = await sharedRelay.SealAndUploadAsync(Id.Brain, sanitized[i], recipients);
        }

        return sealedRefs;
    }

    private async Task<AttachmentRef[]> ReceiveForeignSharedAttachmentsAsync(AttachmentRef[] attachments, BrainId senderBrain)
    {
        var sharedRelay = Services.GetService<SharedAttachmentRelay>();
        if (sharedRelay is not { IsAvailable: true })
        {
            throw new UnauthorizedAccessException("Cross-brain friend messages cannot attach owner brain blobs directly.");
        }

        var senderKeys = await ResolvePeerPublicKeysAsync(senderBrain)
            ?? throw new UnauthorizedAccessException($"No pinned public keys are known for sender brain '{senderBrain.Value}'.");

        var materialized = new AttachmentRef[attachments.Length];
        for (var i = 0; i < attachments.Length; i++)
        {
            var item = attachments[i];
            if (string.IsNullOrWhiteSpace(item.BlobId)
                || string.IsNullOrWhiteSpace(item.BlobSha256)
                || !string.Equals(item.SenderBrainId, senderBrain.Value, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("Cross-brain friend messages must reference E2EE relay blobs signed by the sender brain.");
            }

            var maskedTranscript = string.IsNullOrWhiteSpace(item.Transcript)
                ? null
                : await Runner.Mask(item.Transcript);

            var (saved, _) = await sharedRelay.DownloadAndUnsealAsync(
                Id.Brain,
                item,
                expectedSenderKeys: senderKeys,
                redactedTranscript: maskedTranscript,
                persistToLocalStore: true,
                conversationId: ConvId);
            materialized[i] = saved;
        }

        return materialized;
    }

    private async Task<List<(BrainId BrainId, BrainPublicKeys PublicKeys)>> ResolveSharedBlobRecipientsAsync(IBrainKeySigner signer)
    {
        var result = new List<(BrainId BrainId, BrainPublicKeys PublicKeys)>(State.Members.Length + 1);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var myKeys = await signer.GetPublicKeysAsync(Id.Brain);
        if (seen.Add(Id.Brain.Value))
        {
            result.Add((Id.Brain, myKeys));
        }

        foreach (var member in State.Members)
        {
            if (string.IsNullOrWhiteSpace(member.Brain.Value) || !seen.Add(member.Brain.Value))
            {
                continue;
            }

            var memberKeys = await ResolvePeerPublicKeysAsync(member.Brain)
                ?? throw new UnauthorizedAccessException($"Cannot seal shared attachment: no pinned public keys for participant brain '{member.Brain.Value}'.");
            result.Add((member.Brain, memberKeys));
        }

        return result;
    }

    private async Task<BrainPublicKeys?> ResolvePeerPublicKeysAsync(BrainId peerBrain)
    {
        if (Services.GetService<IBrainKeyDirectory>() is { } directory)
        {
            using (OwnModuleIfForeign())
            {
                if (await directory.GetPinnedKeysForAsync(Id.Brain, peerBrain) is { } pinned)
                {
                    return pinned;
                }
            }
        }

        try
        {
            using var module = OwnModuleIfForeign();
            var friends = await Context.Get<IFriends>(FriendsDefaults.Id(Id.Brain.Value)).List();
            return friends.FirstOrDefault(friend => friend.Brain == peerBrain)?.PublicKeys;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Logger.LogSwallowed(error, "resolving peer public keys from friend roster");
            return null;
        }
    }

    private string? ResolveMentionedSharedAgent(string text)
    {
        var mentions = SharedRoomAgentPolicy.ExtractMentions(text);
        if (mentions.Length == 0)
        {
            return null;
        }

        foreach (var mention in mentions)
        {
            var seated = State.Agents.FirstOrDefault(a => string.Equals(a.AgentId, mention, StringComparison.OrdinalIgnoreCase));
            if (seated is not null)
            {
                return seated.AgentId;
            }
            if (SharedRoomAgentPolicy.IsMentioned("@" + mention, SharedRoomAgentPolicy.DefaultRoomAgentId))
            {
                return SharedRoomAgentPolicy.DefaultRoomAgentId;
            }
        }
        return null;
    }

    private string ResolveSquadTargetAgent(string text)
    {
        if (State.Agents.Length > 1 || State.Kind == ThreadKind.Squad)
        {
            var mentions = SharedRoomAgentPolicy.ExtractMentions(text);
            foreach (var mention in mentions)
            {
                var matched = State.Agents.FirstOrDefault(a => string.Equals(a.AgentId, mention, StringComparison.OrdinalIgnoreCase));
                if (matched is not null)
                {
                    return matched.AgentId;
                }
            }

            if (!string.IsNullOrWhiteSpace(State.LeadAgentId))
            {
                var lead = State.Agents.FirstOrDefault(a => string.Equals(a.AgentId, State.LeadAgentId, StringComparison.OrdinalIgnoreCase));
                if (lead is not null)
                {
                    return lead.AgentId;
                }
            }
        }

        return State.Agents.FirstOrDefault()?.AgentId ?? DefaultAgentId;
    }

    private string? ResolveSquadHandoffAgent(string replyText, string currentAgentId)
    {
        if (State.TrustPosture != ThreadTrustPosture.OwnerTrusted || State.Kind != ThreadKind.Squad || State.Agents.Length < 2)
        {
            return null;
        }

        var mentions = SharedRoomAgentPolicy.ExtractMentions(replyText);
        foreach (var mention in mentions)
        {
            var matched = State.Agents.FirstOrDefault(a =>
                string.Equals(a.AgentId, mention, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(a.AgentId, currentAgentId, StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
            {
                return matched.AgentId;
            }
        }
        return null;
    }

    public async Task Invite(AccountId friend, BrainId? friendBrain = null, Role role = Role.Editor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(friend.Value);
        AuthorizeOwnerOnly();
        await Fresh();

        if (State.TrustPosture != ThreadTrustPosture.CrossBrainShared)
        {
            throw new UnauthorizedAccessException(
                "OwnerTrusted conversations cannot invite external friends; create a CrossBrainShared thread instead.");
        }

        if (friendBrain is not { } targetBrain || string.IsNullOrWhiteSpace(targetBrain.Value))
        {
            throw new ArgumentException("Inviting a friend to a CrossBrainShared thread requires their BrainId.", nameof(friendBrain));
        }

        var account = friend.Value.Trim();
        await HearTheRosterAsync();
        await Offers.Offer(Id, account, role);
        foreach (var pinned in State.PinnedScreens)
        {
            await Offers.Offer(new NeuronId(Id.Brain, pinned.ScreenType, pinned.ScreenKey), account, role);
        }

        var updatedMembers = State.Members
            .Where(m => !string.Equals(m.Account, account, StringComparison.Ordinal))
            .Append(new SeatedMember(account, targetBrain, role))
            .ToArray();
        var participants = WithParticipants(State.Participants, new Participant.Human(new AccountId(account)));

        await Persist(State with
        {
            PostureLocked = true,
            Members = updatedMembers,
            Participants = participants,
        }, announce: false);

        await DirectoryOf(targetBrain).Invited(Id, State.Title, State.Kind);
    }

    public async Task Pin(string screenType, string screenKey = "default")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(screenType);
        ArgumentException.ThrowIfNullOrWhiteSpace(screenKey);
        AuthorizeOwnerOnly();
        await Fresh();

        var type = screenType.Trim();
        var key = screenKey.Trim();
        RequirePinnableScreen(type);

        if (IsPinned(type, key))
        {
            return;
        }

        var nextPinned = State.PinnedScreens.Append(new PinnedScreen(type, key)).ToArray();
        await Persist(State with { PostureLocked = true, PinnedScreens = nextPinned });

        var screenId = new NeuronId(Id.Brain, type, key);
        foreach (var member in State.Members)
        {
            await Offers.Offer(screenId, member.Account, member.Role);
        }

        await NudgeDirectoriesAsync(State.Revision);
    }

    public async Task<PublishedView> Act(string screenType, string screenKey, DigitalBrain.UI.Command command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(screenType);
        ArgumentException.ThrowIfNullOrWhiteSpace(screenKey);
        ArgumentNullException.ThrowIfNull(command);

        await Fresh();
        await RequireSeat(requireEditor: true);

        var type = screenType.Trim();
        var key = screenKey.Trim();
        if (!IsPinned(type, key))
        {
            throw new ActivationRefusedException(
                ActivationRefusedException.UnknownChild,
                $"Screen '{type}/{key}' is not pinned in thread '{Id.Key}'.");
        }

        RequirePinnableScreen(type);

        // Preserve the real authenticated caller (Caller.For.Account) with zero module privilege elevation.
        var screen = Context.Get<IScreen>(new NeuronId(Id.Brain, type, key));
        await screen.Dispatch(command);
        var view = await screen.Read()
            ?? throw new InvalidOperationException($"Pinned screen '{type}/{key}' produced no published view.");

        using (OwnModuleIfForeign())
        {
            await Persist(State);
        }
        await NudgeDirectoriesAsync(State.Revision);
        return view;
    }

    public async Task Here()
    {
        await Fresh();
        var account = await RequireSeat(requireEditor: false);
        _presence[account] = Clock.GetUtcNow();
    }

    public async Task Leave()
    {
        await Fresh();
        var account = await RequireSeat(requireEditor: false);
        _presence.Remove(account);
        if (IsSameBrainCaller())
        {
            return;
        }

        using (OwnModuleIfForeign())
        {
            await Offers.Revoke(Id, account);
            foreach (var pinned in State.PinnedScreens)
            {
                await Offers.Revoke(new NeuronId(Id.Brain, pinned.ScreenType, pinned.ScreenKey), account);
            }

            var updatedMembers = State.Members
                .Where(m => !string.Equals(m.Account, account, StringComparison.Ordinal))
                .ToArray();
            var updatedParticipants = State.Participants
                .Where(p => p is not Participant.Human h || !string.Equals(h.Account.Value, account, StringComparison.Ordinal))
                .ToArray();
            await Persist(State with { Members = updatedMembers, Participants = updatedParticipants });
        }

        await NudgeDirectoriesAsync(State.Revision);
    }

    public async Task React(EntryId target, string emoji, bool remove = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(emoji);
        var cleanEmoji = emoji.Trim();
        if (cleanEmoji.Length > MaxEmojiCharacters)
        {
            throw new ArgumentException($"Reaction emoji cannot exceed {MaxEmojiCharacters} characters.", nameof(emoji));
        }

        await Fresh();
        var account = await RequireSeat(requireEditor: true);
        await AwaitPendingWrite();

        var nextLog = ConversationLog.Replace(State.Log, target, State.Revision + 1, said =>
        {
            if (said.Deleted)
            {
                throw new InvalidOperationException("Cannot react to a deleted message.");
            }
            var updated = ApplyReaction(said.Reactions ?? [], cleanEmoji, account, remove);
            return said with { Reactions = updated.Length > 0 ? updated : null };
        });

        using (OwnModuleIfForeign())
        {
            await Persist(State with { Log = nextLog });
        }
        if (State.TrustPosture == ThreadTrustPosture.CrossBrainShared)
        {
            await NudgeDirectoriesAsync(State.Revision);
        }
    }

    public async Task Edit(EntryId target, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var trimmed = text.Trim();
        if (trimmed.Length > MaxSayCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(text), $"Message exceeds the {MaxSayCharacters}-character limit.");
        }

        await Fresh();
        var account = await RequireSeat(requireEditor: true);
        await AwaitPendingWrite();

        var masked = await Runner.Mask(trimmed);
        var now = Clock.GetUtcNow();
        var nextLog = ConversationLog.Replace(State.Log, target, State.Revision + 1, said =>
        {
            if (said.Deleted)
            {
                throw new InvalidOperationException("Cannot edit a deleted message.");
            }
            if (said.By is not Participant.Human human || !string.Equals(human.Account.Value, account, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("Only the original author may edit a message.");
            }
            return said with { Text = masked, EditedAt = now };
        });

        using (OwnModuleIfForeign())
        {
            await Persist(State with { Log = nextLog });
        }
        if (State.TrustPosture == ThreadTrustPosture.CrossBrainShared)
        {
            await NudgeDirectoriesAsync(State.Revision);
        }
    }

    public async Task Delete(EntryId target)
    {
        await Fresh();
        var account = await RequireSeat(requireEditor: true);
        var isOwner = IsSameBrainCaller();
        await AwaitPendingWrite();

        var now = Clock.GetUtcNow();
        var nextLog = ConversationLog.Replace(State.Log, target, State.Revision + 1, said =>
        {
            var isAuthor = said.By is Participant.Human human
                && string.Equals(human.Account.Value, account, StringComparison.Ordinal);
            if (!isAuthor && !isOwner)
            {
                throw new UnauthorizedAccessException("Only the message author or thread owner may delete a message.");
            }
            return said with
            {
                Text = string.Empty,
                Attachments = null,
                Reactions = null,
                EditedAt = now,
                Deleted = true,
            };
        });

        using (OwnModuleIfForeign())
        {
            await Persist(State with { Log = nextLog });
        }
        if (State.TrustPosture == ThreadTrustPosture.CrossBrainShared)
        {
            await NudgeDirectoriesAsync(State.Revision);
        }
    }

    private static MessageReaction[] ApplyReaction(MessageReaction[] existing, string emoji, string account, bool remove)
    {
        var list = existing.ToList();
        var idx = list.FindIndex(r => string.Equals(r.Emoji, emoji, StringComparison.Ordinal));
        if (remove)
        {
            if (idx < 0) { return existing; }
            var remaining = list[idx].Accounts.Where(a => !string.Equals(a, account, StringComparison.Ordinal)).ToArray();
            if (remaining.Length == 0) { list.RemoveAt(idx); }
            else { list[idx] = list[idx] with { Accounts = remaining }; }
            return [.. list];
        }

        if (idx < 0)
        {
            list.Add(new MessageReaction(emoji, [account]));
            return [.. list];
        }

        if (list[idx].Accounts.Contains(account, StringComparer.Ordinal))
        {
            return existing;
        }

        list[idx] = list[idx] with { Accounts = [.. list[idx].Accounts, account] };
        return [.. list];
    }

    private async Task<AttachmentRef[]> SanitizeAndAuthorizeAttachmentsAsync(AttachmentRef[] attachments)
    {
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
        await Fresh();

        var isSameBrain = RequireCaller().Brain == Id.Brain;
        if (!isSameBrain)
        {
            if (State.TrustPosture != ThreadTrustPosture.CrossBrainShared)
            {
                throw new UnauthorizedAccessException("Personal OwnerTrusted conversations refuse foreign callers.");
            }
            var viewerAccount = await RequireSeat(requireEditor: false);
            var committed = await ReadCommittedAsync();
            var shared = ConversationLog.Visible(committed.Log, sinceRevision, owner: false)
                .Select(e => ResolveSenderForViewer(e, viewerAccount))
                .ToArray();
            return new ConversationView(ConvId, committed.Revision, [.. committed.Participants], shared, Active: null, Runs: [], Blocked: false, Contexts: [])
            {
                TrustPosture = committed.TrustPosture,
                Kind = committed.Kind,
                Title = committed.Title,
                LeadAgentId = committed.LeadAgentId,
                PinnedScreens = committed.PinnedScreens,
                PresentAccounts = ActivePresence(),
            };
        }

        await EnsureImportedAsync();
        await AwaitPendingWrite();
        var state = Committed;
        var ownerAccount = CallerAccount().Value;
        var visible = ConversationLog.Visible(state.Log, sinceRevision, owner: true);
        if (state.TrustPosture == ThreadTrustPosture.CrossBrainShared)
        {
            visible = [.. visible.Select(e => ResolveSenderForViewer(e, ownerAccount))];
        }

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
            TrustPosture = state.TrustPosture,
            Kind = state.Kind,
            Title = state.Title,
            LeadAgentId = state.LeadAgentId,
            PinnedScreens = state.PinnedScreens,
            PresentAccounts = ActivePresence(),
        };
    }

    public async Task DriveSharedRoomAgent(long triggerRevision, string agentId)
    {
        if (Context.Caller is { } caller
            && caller.Brain != Id.Brain
            && !(caller.Inside && caller.Via == Id))
        {
            throw new UnauthorizedAccessException($"Thread '{Id.Key}' refused direct driver invocation.");
        }

        await Fresh();
        if (State.TrustPosture != ThreadTrustPosture.CrossBrainShared)
        {
            return;
        }

        var seated = State.Agents.FirstOrDefault(a => string.Equals(a.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
        var definition = seated is not null
            ? SharedRoomAgentPolicy.Confine(seated.Definition, State.Title)
            : SharedRoomAgentPolicy.CreateRoomAgent(State.Title);
        SharedRoomAgentPolicy.AssertConfined(definition);

        var resolvedAgentId = seated?.AgentId ?? SharedRoomAgentPolicy.DefaultRoomAgentId;
        var sender = seated is not null
            ? new SenderIdentity(resolvedAgentId, definition.DisplayName, SenderKind.Agent, RoomScoped: true)
            : SharedRoomAgentPolicy.RoomAgentSender;

        var snapshot = State.Log
            .Select(static l => l.Entry)
            .OfType<Said>()
            .Where(static s => !s.Deleted && s.Visible == Visibility.Everyone)
            .TakeLast(MaxRoomAgentContextMessages)
            .ToArray();

        string? replyText = null;
        if (Services.GetService<IModelInference>() is { } inference)
        {
            var aiMessages = new List<AiMessage>(snapshot.Length + 1)
            {
                new("system", [new AiText(definition.Instructions)]),
            };
            foreach (var msg in snapshot)
            {
                var msgSender = msg.Sender ?? ResolveDefaultSender(msg);
                var role = msgSender.Kind == SenderKind.Agent ? "assistant" : "user";
                var body = msgSender.Kind == SenderKind.Agent
                    ? msg.Text
                    : FormatAttributedMessage(msgSender, msg.Text);
                aiMessages.Add(new AiMessage(role, [new AiText(body)]) { Sender = msgSender });
            }

            try
            {
                var result = await inference.Generate(
                    new InferenceRequest(aiMessages, definition.Model, definition.Options),
                    model: null,
                    CancellationToken.None,
                    brainId: Id.Brain.Value);
                replyText = string.Concat(result.Messages
                    .SelectMany(static m => m.Content)
                    .OfType<AiText>()
                    .Select(static t => t.Text)).Trim();
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Logger.LogSwallowed(error, "room-scoped agent inference failed");
                replyText = "I could not reach a model right now. Try mentioning the assistant again in a moment.";
            }
        }

        if (string.IsNullOrWhiteSpace(replyText))
        {
            return;
        }

        var masked = await Runner.Mask(replyText);
        long committedRevision;
        await _sharedWriteGate.WaitAsync();
        try
        {
            await AwaitPendingWrite();
            var assistantParticipant = new Participant.Assistant(resolvedAgentId);
            var reply = new Said(
                EntryIds.Reply(Guid.NewGuid().ToString("N")),
                assistantParticipant,
                Clock.GetUtcNow(),
                masked,
                Sender: sender);

            var nextLog = ConversationLog.Append(State.Log, State.Revision + 1, reply);
            var participants = WithParticipants(State.Participants, assistantParticipant);
            await Persist(State with { Log = nextLog, Participants = participants });
            committedRevision = State.Revision;
        }
        finally
        {
            _sharedWriteGate.Release();
        }
        await NudgeDirectoriesAsync(committedRevision);
    }

    public async Task Drive(string runId, string? model, string? replaces, string? targetAgentId = null, int depth = 0)
    {
        if (State.ActiveRun?.RunId != runId || IsActive)
        {
            Logger.LogWarning("Conversation run {RunId} was no longer active when its driver arrived.", runId);
            return;
        }

        var seated = (!string.IsNullOrWhiteSpace(targetAgentId)
            ? State.Agents.FirstOrDefault(a => string.Equals(a.AgentId, targetAgentId, StringComparison.OrdinalIgnoreCase))
            : null)
            ?? State.Agents.FirstOrDefault()
            ?? new SeatedAgent(DefaultAgentId, new AgentDefinition());
        var definition = seated.Definition;
        var agentParticipant = new Participant.Assistant(seated.AgentId);
        EntryId? replacingId = string.IsNullOrWhiteSpace(replaces) ? null : new EntryId(replaces);

        var committed = false;
        string? handoffTarget = null;
        string? handoffText = null;

        using (var activeRun = BeginRun(runId, definition.Timeout))
        {
            var turnState = new TurnState(State.ActiveRun);
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
                var latestHumanSender = LatestHumanSender(State.Log);
                var latestHumanSaid = LatestHumanSaidForTurn(State.Log);
                var input = depth == 0 && latestHumanSaid is { Attachments.Length: > 0 }
                    ? (await BuildMultimodalInputAsync(latestHumanSaid, activeRun.Token)) with { Sender = latestHumanSender }
                    : new AiMessage("user", [new AiText(turnState.Run.UserText)]) { Sender = latestHumanSender };

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
                                newEntries.Add(new Said(EntryIds.Reply(runId), agentParticipant, now, turnState.Visible, usedModel, replacingId, new SenderIdentity(seated.AgentId, definition.DisplayName, SenderKind.Agent)));

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

                                // Multi-agent Squad handoff with strict maxDepth = 3 loop guard.
                                if (depth + 1 < SharedRoomAgentPolicy.MaxSquadHandoffDepth
                                    && ResolveSquadHandoffAgent(turnState.Visible, seated.AgentId) is { } nextAgent)
                                {
                                    handoffTarget = nextAgent;
                                    handoffText = turnState.Visible;
                                }
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

        if (committed && handoffTarget is not null && handoffText is not null && !IsBlocked)
        {
            var nextRunId = Guid.NewGuid().ToString("N");
            var handoffRun = new AgentRun(nextRunId, AgentRunStatus.Running, handoffText, Clock.GetUtcNow())
            {
                TraceId = TraceContext.Current?.TraceId,
            };
            await Persist(State with { ActiveRun = handoffRun, Replacing = null });
            try
            {
                await Context.Get<IConversationDriver>(Id).Drive(nextRunId, model, null, handoffTarget, depth + 1);
            }
            catch (Exception error)
            {
                Logger.LogSwallowed(error, "squad handoff dispatch failed");
                await UndoHandOffAsync(State with { ActiveRun = null, Replacing = null }, nextRunId);
            }
        }
    }

    private string[] ActivePresence()
    {
        var now = Clock.GetUtcNow();
        return _presence
            .Where(kv => now - kv.Value <= PresenceTtl)
            .Select(static kv => kv.Key)
            .OrderBy(static a => a, StringComparer.Ordinal)
            .ToArray();
    }

    private bool IsPinned(string screenType, string screenKey)
        => State.PinnedScreens.Any(p =>
            string.Equals(p.ScreenType, screenType, StringComparison.Ordinal)
            && string.Equals(p.ScreenKey, screenKey, StringComparison.Ordinal));

    private void RequirePinnableScreen(string screenType)
    {
        if (SharedRoomAgentPolicy.IsHostPrivateScreenType(screenType))
        {
            throw new UnauthorizedAccessException($"Screen type '{screenType}' is host-private and cannot be pinned in thread '{Id.Key}'.");
        }

        var screenClass = Context.ClassOf(screenType);
        if (screenClass is null || !typeof(IScreen).IsAssignableFrom(screenClass))
        {
            throw new UnauthorizedAccessException($"Screen type '{screenType}' is not a registered pinnable screen in thread '{Id.Key}'.");
        }

        if (!screenClass.IsDefined(typeof(ShareableAttribute), inherit: false))
        {
            throw new UnauthorizedAccessException($"Screen type '{screenType}' is not marked [Shareable] and cannot be pinned in thread '{Id.Key}'.");
        }

        if (screenClass.IsDefined(typeof(InternalAttribute), inherit: false))
        {
            throw new UnauthorizedAccessException($"Screen type '{screenType}' is internal and cannot be pinned in thread '{Id.Key}'.");
        }

        if (screenClass.GetCustomAttribute<ScreenAttribute>(inherit: false) is { Category: { Length: > 0 } category }
            && SharedRoomAgentPolicy.IsHostPrivateCategory(category))
        {
            throw new UnauthorizedAccessException($"Screen type '{screenType}' (category '{category}') is host-private and cannot be pinned in thread '{Id.Key}'.");
        }
    }

    private async Task<string> RequireSeat(bool requireEditor = false)
    {
        var caller = RequireCaller();
        if (IsSameBrainCaller())
        {
            return caller.For?.Account ?? Id.Brain.Value;
        }

        if (State.TrustPosture != ThreadTrustPosture.CrossBrainShared)
        {
            throw new UnauthorizedAccessException($"Thread '{Id.Key}' admits only its owner.");
        }

        var account = caller.For?.Account;
        if (string.IsNullOrEmpty(account))
        {
            throw new UnauthorizedAccessException($"Thread '{Id.Key}' admits its host and the friends its host invited.");
        }

        var member = State.Members.FirstOrDefault(m =>
            string.Equals(m.Account, account, StringComparison.Ordinal) && m.Brain == caller.Brain);
        if (member is null || !await IsFriendAsync(new AccountId(account)))
        {
            throw new UnauthorizedAccessException($"Thread '{Id.Key}' admits its host and the friends its host invited.");
        }

        if (requireEditor && member.Role != Role.Editor)
        {
            throw new UnauthorizedAccessException($"Thread '{Id.Key}' requires Editor role for this action.");
        }

        return account;
    }

    private async Task<bool> IsFriendAsync(AccountId account)
    {
        if (Services.GetService<IFriendCheck>() is not { } roster) { return false; }
        var edge = Services.GetService<EdgeAuthority>();
        using var module = edge?.Enter(edge.Module(IConversation.Type, Id.Brain));
        return await roster.IsFriend(Id.Brain, account.Value);
    }

    private IDisposable? OwnModuleIfForeign()
    {
        if (Context.Caller is { Brain: var brain } && brain == Id.Brain)
        {
            return null;
        }
        var edge = Services.GetService<EdgeAuthority>();
        return edge?.Enter(edge.Module(IConversation.Type, Id.Brain));
    }

    private IThreadDirectory DirectoryOf(BrainId brain)
        => brain == Id.Brain
            ? Context.Get<IThreadDirectory>(new NeuronId(brain, Threads.Type, IThreadDirectory.DefaultKey))
            : Context.Get<IThreadDirectory>(new BindingTarget.Offered(brain, new NeuronId(brain, Threads.Type, IThreadDirectory.DefaultKey)));

    private async Task NudgeDirectoriesAsync(long revision)
    {
        if (Context.ClassOf(Threads.Type) is not null)
        {
            try
            {
                await DirectoryOf(Id.Brain).Changed(Id, revision);
            }
            catch (UnauthorizedAccessException refused)
            {
                Logger.LogSwallowed(refused, "the host's thread directory refused the notice of a change");
            }
        }

        if (State.Members.Length == 0)
        {
            return;
        }

        // Cross-brain directory nudges originate from this host brain's ConversationNeuron (Id.Brain).
        // When a foreign guest triggered the write, re-bind the ambient caller to this host brain's owner
        // so SealedRelayBrainTransport seals the nudge from Id.Brain with valid RemoteViaProvenance.
        IDisposable? hostCallerScope = null;
        if (!IsSameBrainCaller() && Services.GetService<EdgeAuthority>() is { } edge)
        {
            var hostAccount = Services.GetService<IBrainOwners>() is { } owners
                ? await owners.OwnerAccount(Id.Brain)
                : null;
            var resolvedHostAccount = !string.IsNullOrWhiteSpace(hostAccount) ? hostAccount : Id.Brain.Value;
            hostCallerScope = edge.Enter(edge.Person(resolvedHostAccount, resolvedHostAccount, Id.Brain));
        }

        using (hostCallerScope)
        {
            foreach (var member in State.Members)
            {
                try
                {
                    await DirectoryOf(member.Brain).Changed(Id, revision);
                }
                catch (UnauthorizedAccessException refused)
                {
                    Logger.LogSwallowed(refused, "a participant's thread directory refused the notice of a change");
                }
            }
        }
    }

    private static ConversationEntry ResolveSenderForViewer(ConversationEntry entry, string viewerAccount)
    {
        if (entry is not Said said)
        {
            return entry;
        }
        if (said.Sender is { Kind: SenderKind.Agent } || said.By is Participant.Assistant)
        {
            return said;
        }

        var accountId = said.Sender?.AccountId ?? (said.By is Participant.Human h ? h.Account.Value : "unknown");
        var displayName = said.Sender?.DisplayName ?? accountId;
        var kind = string.Equals(accountId, viewerAccount, StringComparison.Ordinal)
            ? SenderKind.Self
            : SenderKind.Human;
        return said with { Sender = new SenderIdentity(accountId, displayName, kind) };
    }

    private static SenderIdentity ResolveDefaultSender(Said said)
        => said.By switch
        {
            Participant.Human human => new SenderIdentity(human.Account.Value, human.Account.Value, SenderKind.Human),
            Participant.Assistant assistant => new SenderIdentity(assistant.AgentId, assistant.AgentId, SenderKind.Agent),
            _ => new SenderIdentity("unknown", "Unknown", SenderKind.Human),
        };

    private static string FormatAttributedMessage(SenderIdentity sender, string text)
    {
        var safeName = SanitizeSpeakerToken(sender.DisplayName);
        var safeId = SanitizeSpeakerToken(sender.AccountId);
        var framedBody = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("\n", "\n  ", StringComparison.Ordinal);
        return $"[{safeName} ({safeId})]: {framedBody}";
    }

    private static string SanitizeSpeakerToken(string value)
        => value.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("[", "", StringComparison.Ordinal)
            .Replace("]", "", StringComparison.Ordinal)
            .Trim();

    private static SenderIdentity? LatestHumanSender(LoggedEntry[] log)
    {
        for (var i = log.Length - 1; i >= 0; i--)
        {
            if (log[i].Entry is Said { By: Participant.Human human, Deleted: false } said)
            {
                return said.Sender ?? new SenderIdentity(human.Account.Value, human.Account.Value, SenderKind.Human);
            }
        }
        return null;
    }

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
            if (log[i].Entry is Said { By: Participant.Human, Deleted: false } said)
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
        => state.TrustPosture == ThreadTrustPosture.CrossBrainShared
            ? []
            : [.. state.Agents.SelectMany(static a => a.Definition.Contexts).Distinct(StringComparer.Ordinal)];

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
