using System.Reflection;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.UI;

namespace IntoChat.OS.Modules.Apps.Social;

internal sealed record RoomParticipant(string Account, BrainId Brain, Role Role);

internal sealed record RoomState(
    long Revision,
    RoomMessage[] Messages,
    PinnedScreen[] PinnedScreens,
    RoomParticipant[] Participants,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RoomKind? Kind = null)
{
    public RoomState() : this(0, [], [], []) { }
}

// Who a brain's roster lists as a friend, asked as the social module of that brain: the call being served may come from
// a person of another brain. No friend check in the host, or a roster that refuses, reads as "not a friend".
internal static class RoomFriends
{
    public const string RoomType = "social.room";

    public static async Task<bool> IsFriend(IServiceProvider? services, BrainId brain, AccountId account)
    {
        if (services?.GetService(typeof(IFriendCheck)) is not IFriendCheck roster) { return false; }
        var edge = services.GetService(typeof(EdgeAuthority)) as EdgeAuthority;
        using var module = edge?.Enter(edge.Module(RoomType, brain));
        return await roster.IsFriend(brain, account.Value).ConfigureAwait(true);
    }
}

// A room of people talking, in its host's brain. The kernel admits a person of another brain who holds an offer on the
// room; the room then admits only a person the host invited who is the host's friend at that moment.
[Neuron(RoomFriends.RoomType)]
internal sealed class RoomNeuron(IServiceProvider? services = null) : Neuron<RoomState>, IRoom
{
    private const int MaxAgentContextMessages = 30;
    private static readonly TimeSpan PresenceTtl = TimeSpan.FromMinutes(2);

    private static readonly string[] HostPrivatePrefixes =
    [
        "os.",
        "identity.",
        "social.",
        "component.",
        "synapse.",
        "flow.",
        "runtime.",
        "system.",
        "postgres.",
        "storage.",
        "device.",
        "mcp.",
        "ai.",
        "auth.",
    ];

    private static readonly HashSet<string> HostPrivateScreenTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "files",
        "app.files",
        "ino",
        "app.ino",
        "settings",
        "app.settings",
        "tasks",
        "app.tasks",
        "engineer",
        "app.engineer",
        "creator",
        "app.creator",
        "desktop",
        "app.desktop",
        "friends",
        "app.friends",
        "devui",
        "app.devui",
        "orleans",
        "app.orleans",
        "aspire",
        "app.aspire",
        "widgetbook",
        "app.widgetbook",
        "approvals",
        "flows",
        "vault",
        "secrets",
    };

    private static readonly HashSet<string> HostPrivateCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "System",
        "Developer",
        "Intelligence",
    };

    private readonly Dictionary<string, DateTimeOffset> _presence = new(StringComparer.Ordinal);

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await base.OnActivate(cancellationToken).ConfigureAwait(true);
        if (State.Participants.Length > 0) { await HearTheRoster().ConfigureAwait(true); }
    }

    // Only the roster's own fact is heard: a signal of the same name from another neuron never arrives here.
    private Task HearTheRoster() => Context.Wires.Ensure<FriendRemoved>(FriendsDefaults.Id(Id.Brain.Value));

    // The roster removed a friend: their seat goes. Until this turn runs, RequireSeat refuses them.
    [On]
    public async Task On(FriendRemoved removed)
    {
        ArgumentNullException.ThrowIfNull(removed);
        var account = removed.Account.Value;
        _presence.Remove(account);
        await Context.Offers.Revoke(Id, account).ConfigureAwait(true);
        foreach (var pinned in State.PinnedScreens)
        {
            await Context.Offers.Revoke(new NeuronId(Id.Brain, pinned.ScreenType, pinned.ScreenKey), account).ConfigureAwait(true);
        }

        if (State.Participants.All(p => !string.Equals(p.Account, account, StringComparison.Ordinal))) { return; }
        await Store(State with
        {
            Participants = [.. State.Participants.Where(p => !string.Equals(p.Account, account, StringComparison.Ordinal))],
        }).ConfigureAwait(true);
    }

    public async Task Configure(string title, RoomKind kind, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        RequireOwner();

        var cleanTitle = title.Trim();
        await Store(State with { Title = cleanTitle, Kind = kind }).ConfigureAwait(true);
    }

    public async Task Invite(string friendAccount, BrainId friendBrain, Role role = Role.Editor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(friendAccount);
        ArgumentException.ThrowIfNullOrWhiteSpace(friendBrain.Value);
        RequireOwner();

        var account = friendAccount.Trim();
        await HearTheRoster().ConfigureAwait(true);
        await Context.Offers.Offer(Id, account, role, ct).ConfigureAwait(true);
        foreach (var pinned in State.PinnedScreens)
        {
            await Context.Offers.Offer(new NeuronId(Id.Brain, pinned.ScreenType, pinned.ScreenKey), account, role, ct).ConfigureAwait(true);
        }

        var updatedParticipants = State.Participants
            .Where(p => !string.Equals(p.Account, account, StringComparison.Ordinal))
            .Append(new RoomParticipant(account, friendBrain, role))
            .ToArray();

        await Store(State with { Participants = updatedParticipants }).ConfigureAwait(true);
        await RoomsOf(friendBrain).Invited(Id, ct).ConfigureAwait(true);
    }

    public async Task Pin(string screenType, string screenKey = "default", CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(screenType);
        ArgumentException.ThrowIfNullOrWhiteSpace(screenKey);
        RequireOwner();

        var type = screenType.Trim();
        var key = screenKey.Trim();
        RequirePinnableScreen(type);

        if (IsPinned(type, key))
        {
            return;
        }

        var revision = State.Revision + 1;
        var nextPinned = State.PinnedScreens.Append(new PinnedScreen(type, key)).ToArray();
        await Store(State with { Revision = revision, PinnedScreens = nextPinned }).ConfigureAwait(true);

        var screenId = new NeuronId(Id.Brain, type, key);
        foreach (var participant in State.Participants)
        {
            await Context.Offers.Offer(screenId, participant.Account, participant.Role, ct).ConfigureAwait(true);
        }

        await NudgeAllAsync(revision, ct).ConfigureAwait(true);
    }

    public async Task Say(string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var author = await RequireSeat(requireEditor: true).ConfigureAwait(true);
        var trimmed = text.Trim();
        var revision = State.Revision + 1;
        var sender = new SenderIdentity(author, author, SenderKind.Human);
        var message = new RoomMessage(revision, author, trimmed, Context.Time.GetUtcNow(), sender);

        await Store(State with
        {
            Revision = revision,
            Messages = [.. State.Messages, message],
        }).ConfigureAwait(true);

        await NudgeAllAsync(revision, ct).ConfigureAwait(true);

        // Room-scoped @Ino triggers ONLY on explicit @Ino mention; normal human messages trigger 0 agent turns.
        if (RoomAgentDefinition.IsMentioned(trimmed))
        {
            await RespondAsRoomInoAsync(ct).ConfigureAwait(true);
        }
    }

    public async Task<RoomView> Read(long? sinceRevision = null, CancellationToken ct = default)
    {
        var viewerAccount = await RequireSeat(requireEditor: false).ConfigureAwait(true);
        var rawMessages = sinceRevision is { } since
            ? State.Messages.Where(m => m.Revision > since).ToArray()
            : State.Messages;

        var messages = rawMessages
            .Select(m => ResolveSenderForViewer(m, viewerAccount))
            .ToArray();

        var now = Context.Time.GetUtcNow();
        var present = _presence
            .Where(kv => now - kv.Value <= PresenceTtl)
            .Select(static kv => kv.Key)
            .OrderBy(static a => a, StringComparer.Ordinal)
            .ToArray();

        var participants = State.Participants
            .Select(static p => p.Account)
            .OrderBy(static a => a, StringComparer.Ordinal)
            .ToArray();

        return new RoomView(
            State.Revision,
            messages,
            State.PinnedScreens,
            present,
            Title: State.Title,
            Kind: State.Kind,
            Participants: participants.Length > 0 ? participants : null);
    }

    public async Task<PublishedView> Act(string screenType, string screenKey, DigitalBrain.UI.Command command, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(screenType);
        ArgumentException.ThrowIfNullOrWhiteSpace(screenKey);
        ArgumentNullException.ThrowIfNull(command);

        await RequireSeat(requireEditor: true).ConfigureAwait(true);

        var type = screenType.Trim();
        var key = screenKey.Trim();
        if (!IsPinned(type, key))
        {
            throw new ActivationRefusedException(
                ActivationRefusedException.UnknownChild,
                $"Screen '{type}/{key}' is not pinned in room '{Id.Key}'.");
        }

        RequirePinnableScreen(type);

        // Preserve the real authenticated caller (no module privilege elevation).
        var screen = Context.Get<IScreen>(new NeuronId(Id.Brain, type, key));
        await screen.Dispatch(command).ConfigureAwait(true);
        var view = await screen.Read().ConfigureAwait(true)
            ?? throw new InvalidOperationException($"Pinned screen '{type}/{key}' produced no published view.");

        var revision = State.Revision + 1;
        await Store(State with { Revision = revision }).ConfigureAwait(true);
        await NudgeAllAsync(revision, ct).ConfigureAwait(true);
        return view;
    }

    public async Task Here(CancellationToken ct = default)
    {
        var account = await RequireSeat(requireEditor: false).ConfigureAwait(true);
        _presence[account] = Context.Time.GetUtcNow();
    }

    public async Task Leave(CancellationToken ct = default)
    {
        var account = await RequireSeat(requireEditor: false).ConfigureAwait(true);
        _presence.Remove(account);
        if (IsSameBrainCaller())
        {
            return;
        }

        var updated = State.Participants
            .Where(p => !string.Equals(p.Account, account, StringComparison.Ordinal))
            .ToArray();
        var revision = State.Revision + 1;
        await Store(State with { Revision = revision, Participants = updated }).ConfigureAwait(true);
        await NudgeAllAsync(revision, ct).ConfigureAwait(true);
    }

    private async Task RespondAsRoomInoAsync(CancellationToken ct)
    {
        var definition = RoomAgentDefinition.Create(State.Title);
        // Defense-in-depth invariants: room agent never injects personal contexts or component operations.
        if (definition.Contexts.Length != 0 || definition.IncludeComponentOperations)
        {
            throw new InvalidOperationException("RoomAgentDefinition violated private-brain isolation invariants.");
        }

        string? replyText = null;
        if (services?.GetService(typeof(IModelInference)) is IModelInference inference)
        {
            var aiMessages = new List<AiMessage>(MaxAgentContextMessages + 1)
            {
                new("system", [new AiText(definition.Instructions)]),
            };
            foreach (var msg in State.Messages.TakeLast(MaxAgentContextMessages))
            {
                var sender = msg.Sender ?? new SenderIdentity(msg.Author, msg.Author, SenderKind.Human);
                var role = sender.Kind == SenderKind.Agent ? "assistant" : "user";
                var attributed = sender.Kind == SenderKind.Agent
                    ? msg.Text
                    : $"[{sender.DisplayName} ({sender.AccountId})]: {msg.Text}";
                aiMessages.Add(new AiMessage(role, [new AiText(attributed)]) { Sender = sender });
            }

            try
            {
                var result = await inference.Generate(
                    new InferenceRequest(aiMessages, definition.Model, definition.Options),
                    model: null,
                    ct,
                    brainId: Id.Brain.Value).ConfigureAwait(true);
                replyText = string.Concat(result.Messages
                    .SelectMany(static m => m.Content)
                    .OfType<AiText>()
                    .Select(static t => t.Text)).Trim();
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                OsConsoleLog.For<RoomNeuron>().LogSwallowed(error, "room-scoped @Ino inference failed");
                replyText = "I could not reach a model right now. Try mentioning @Ino again in a moment.";
            }
        }

        if (string.IsNullOrWhiteSpace(replyText))
        {
            return;
        }

        var revision = State.Revision + 1;
        var reply = new RoomMessage(
            revision,
            RoomAgentDefinition.AgentId,
            replyText,
            Context.Time.GetUtcNow(),
            RoomAgentDefinition.AgentSender);

        await Store(State with
        {
            Revision = revision,
            Messages = [.. State.Messages, reply],
        }).ConfigureAwait(true);

        await NudgeAllAsync(revision, ct).ConfigureAwait(true);
    }

    private static RoomMessage ResolveSenderForViewer(RoomMessage message, string viewerAccount)
    {
        if (message.Sender is { Kind: SenderKind.Agent })
        {
            return message;
        }

        var accountId = message.Sender?.AccountId ?? message.Author;
        var displayName = message.Sender?.DisplayName ?? message.Author;
        var kind = string.Equals(accountId, viewerAccount, StringComparison.Ordinal)
            ? SenderKind.Self
            : SenderKind.Human;
        return message with { Sender = new SenderIdentity(accountId, displayName, kind) };
    }

    private bool IsPinned(string screenType, string screenKey)
        => State.PinnedScreens.Any(p =>
            string.Equals(p.ScreenType, screenType, StringComparison.Ordinal)
            && string.Equals(p.ScreenKey, screenKey, StringComparison.Ordinal));

    private void RequirePinnableScreen(string screenType)
    {
        if (HostPrivateScreenTypes.Contains(screenType)
            || HostPrivatePrefixes.Any(prefix => screenType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new UnauthorizedAccessException($"Screen type '{screenType}' is host-private and cannot be pinned in room '{Id.Key}'.");
        }

        var screenClass = Context.ClassOf(screenType);
        if (screenClass is null || !typeof(IScreen).IsAssignableFrom(screenClass))
        {
            throw new UnauthorizedAccessException($"Screen type '{screenType}' is not a registered pinnable screen in room '{Id.Key}'.");
        }

        if (!screenClass.IsDefined(typeof(ShareableAttribute), inherit: false))
        {
            throw new UnauthorizedAccessException($"Screen type '{screenType}' is not marked [Shareable] and cannot be pinned in room '{Id.Key}'.");
        }

        if (screenClass.IsDefined(typeof(InternalAttribute), inherit: false))
        {
            throw new UnauthorizedAccessException($"Screen type '{screenType}' is internal and cannot be pinned in room '{Id.Key}'.");
        }

        if (screenClass.GetCustomAttribute<ScreenAttribute>(inherit: false) is { Category: { Length: > 0 } category }
            && HostPrivateCategories.Contains(category))
        {
            throw new UnauthorizedAccessException($"Screen type '{screenType}' (category '{category}') is host-private and cannot be pinned in room '{Id.Key}'.");
        }
    }

    private void RequireOwner()
        => RequireSameBrain(() => new UnauthorizedAccessException($"Only brain '{Id.Brain.Value}' may administer room '{Id.Key}'."));

    // The host's own side, or a person the host invited, calling from the brain the invitation names, who the host's
    // roster lists as a friend now. An offer is not an invitation, and it can outlive a friendship; the seat can too,
    // until the room hears of the removal. One message for every refusal.
    private async Task<string> RequireSeat(bool requireEditor = false)
    {
        var caller = RequireCaller();
        if (IsSameBrainCaller())
        {
            return caller.For?.Account ?? Id.Brain.Value;
        }

        var account = caller.For?.Account;
        if (string.IsNullOrEmpty(account))
        {
            throw new UnauthorizedAccessException($"Room '{Id.Key}' admits its host and the friends its host invited.");
        }

        var participant = State.Participants.FirstOrDefault(p =>
            string.Equals(p.Account, account, StringComparison.Ordinal) && p.Brain == caller.Brain);
        if (participant is null
            || !await RoomFriends.IsFriend(services, Id.Brain, new AccountId(account)).ConfigureAwait(true))
        {
            throw new UnauthorizedAccessException($"Room '{Id.Key}' admits its host and the friends its host invited.");
        }

        if (requireEditor && participant.Role != Role.Editor)
        {
            throw new UnauthorizedAccessException($"Room '{Id.Key}' requires Editor role for this action.");
        }

        return account;
    }

    private IRooms RoomsOf(BrainId brain)
        => brain == Id.Brain
            ? Context.Get<IRooms>(new NeuronId(brain, "social.rooms", IRooms.DefaultKey))
            : Context.Get<IRooms>(new BindingTarget.Offered(brain, new NeuronId(brain, "social.rooms", IRooms.DefaultKey)));

    // Notifies both the host's own room index (when hosted) and every participant's room index.
    private async Task NudgeAllAsync(long revision, CancellationToken ct)
    {
        if (Context.ClassOf("social.rooms") is not null)
        {
            try
            {
                await RoomsOf(Id.Brain).Changed(Id, revision, ct).ConfigureAwait(true);
            }
            catch (UnauthorizedAccessException refused)
            {
                OsConsoleLog.For<RoomNeuron>().LogSwallowed(refused, "the host's room index refused the notice of a change");
            }
        }

        foreach (var participant in State.Participants)
        {
            try { await RoomsOf(participant.Brain).Changed(Id, revision, ct).ConfigureAwait(true); }
            catch (UnauthorizedAccessException refused)
            {
                OsConsoleLog.For<RoomNeuron>().LogSwallowed(refused, "a participant's room index refused the notice of a change");
            }
        }
    }
}
