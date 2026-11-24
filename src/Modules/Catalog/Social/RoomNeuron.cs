using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Units;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Modules.Apps.Social;

internal sealed record RoomParticipant(string Account, BrainId Brain, Role Role);

internal sealed record RoomState(
    long Revision,
    RoomMessage[] Messages,
    PinnedScreen[] PinnedScreens,
    RoomParticipant[] Participants)
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
    private static readonly TimeSpan PresenceTtl = TimeSpan.FromMinutes(2);
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
        if (State.Participants.All(p => !string.Equals(p.Account, account, StringComparison.Ordinal))) { return; }
        await Store(State with
        {
            Participants = [.. State.Participants.Where(p => !string.Equals(p.Account, account, StringComparison.Ordinal))],
        }).ConfigureAwait(true);
    }

    public async Task Invite(string friendAccount, BrainId friendBrain, Role role = Role.Editor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(friendAccount);
        ArgumentException.ThrowIfNullOrWhiteSpace(friendBrain.Value);
        RequireOwner();

        var account = friendAccount.Trim();
        await HearTheRoster().ConfigureAwait(true);
        await Context.Offers.Offer(Id, account, role, ct).ConfigureAwait(true);

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
        if (IsPinned(type, key))
        {
            return;
        }

        var nextPinned = State.PinnedScreens.Append(new PinnedScreen(type, key)).ToArray();
        await Store(State with { PinnedScreens = nextPinned }).ConfigureAwait(true);
    }

    public async Task Say(string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var author = await RequireSeat().ConfigureAwait(true);
        var revision = State.Revision + 1;
        var message = new RoomMessage(revision, author, text.Trim(), Context.Time.GetUtcNow());

        await Store(State with
        {
            Revision = revision,
            Messages = [.. State.Messages, message],
        }).ConfigureAwait(true);

        await NudgeParticipantsAsync(revision, ct).ConfigureAwait(true);
    }

    public async Task<RoomView> Read(long? sinceRevision = null, CancellationToken ct = default)
    {
        await RequireSeat().ConfigureAwait(true);
        var messages = sinceRevision is { } since
            ? State.Messages.Where(m => m.Revision > since).ToArray()
            : State.Messages;

        var now = Context.Time.GetUtcNow();
        var present = _presence
            .Where(kv => now - kv.Value <= PresenceTtl)
            .Select(static kv => kv.Key)
            .OrderBy(static a => a, StringComparer.Ordinal)
            .ToArray();

        return new RoomView(State.Revision, messages, State.PinnedScreens, present);
    }

    public async Task<PublishedView> Act(string screenType, string screenKey, ScreenAction action, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(screenType);
        ArgumentException.ThrowIfNullOrWhiteSpace(screenKey);
        ArgumentNullException.ThrowIfNull(action);
        await RequireSeat().ConfigureAwait(true);

        var type = screenType.Trim();
        var key = screenKey.Trim();
        if (!IsPinned(type, key))
        {
            throw new ActivationRefusedException(
                ActivationRefusedException.UnknownChild,
                $"Screen '{type}/{key}' is not pinned in room '{Id.Key}'.");
        }

        PublishedView view;
        using (AsSocialModule())
        {
            var screen = Context.Get<IScreen>(new NeuronId(Id.Brain, type, key));
            await screen.Act(action).ConfigureAwait(true);
            view = await screen.Read().ConfigureAwait(true)
                ?? throw new InvalidOperationException($"Pinned screen '{type}/{key}' produced no published view.");
        }

        var revision = State.Revision + 1;
        await Store(State with { Revision = revision }).ConfigureAwait(true);
        await NudgeParticipantsAsync(revision, ct).ConfigureAwait(true);
        return view;
    }

    public async Task Here(CancellationToken ct = default)
    {
        var account = await RequireSeat().ConfigureAwait(true);
        _presence[account] = Context.Time.GetUtcNow();
    }

    private bool IsPinned(string screenType, string screenKey)
        => State.PinnedScreens.Any(p =>
            string.Equals(p.ScreenType, screenType, StringComparison.Ordinal)
            && string.Equals(p.ScreenKey, screenKey, StringComparison.Ordinal));

    private void RequireOwner()
        => RequireSameBrain(() => new UnauthorizedAccessException($"Only brain '{Id.Brain.Value}' may administer room '{Id.Key}'."));

    private string ResolveCallerAccount()
        => RequireCaller().For?.Account ?? Id.Brain.Value;

    // The host's own side, or a person the host invited, calling from the brain the invitation names, who the host's
    // roster lists as a friend now. An offer is not an invitation, and it can outlive a friendship; the seat can too,
    // until the room hears of the removal. One message for every refusal.
    private async Task<string> RequireSeat()
    {
        if (Context.Caller is not { } caller || caller.Brain == Id.Brain) { return ResolveCallerAccount(); }

        var account = caller.For?.Account;
        if (string.IsNullOrEmpty(account)
            || !State.Participants.Any(p => string.Equals(p.Account, account, StringComparison.Ordinal) && p.Brain == caller.Brain)
            || !await RoomFriends.IsFriend(services, Id.Brain, new AccountId(account)).ConfigureAwait(true))
        {
            throw new UnauthorizedAccessException($"Room '{Id.Key}' admits its host and the friends its host invited.");
        }
        return account;
    }

    private IRooms RoomsOf(BrainId brain)
        => Context.Get<IRooms>(new BindingTarget.Offered(brain, new NeuronId(brain, "social.rooms", IRooms.DefaultKey)));

    // A participant's index refuses the notice when its owner no longer lists the host as a friend. That ends nothing
    // here: the message is stored and the others are told.
    private async Task NudgeParticipantsAsync(long revision, CancellationToken ct)
    {
        foreach (var participant in State.Participants)
        {
            try { await RoomsOf(participant.Brain).Changed(Id, revision, ct).ConfigureAwait(true); }
            catch (UnauthorizedAccessException refused)
            {
                OsConsoleLog.For<RoomNeuron>().LogSwallowed(refused, "a participant's room index refused the notice of a change");
            }
        }
    }

    private IDisposable? AsSocialModule()
    {
        var edge = services?.GetService(typeof(EdgeAuthority)) as EdgeAuthority;
        return edge?.Enter(edge.Module(RoomFriends.RoomType, Id.Brain));
    }
}
