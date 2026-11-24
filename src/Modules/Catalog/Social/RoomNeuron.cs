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

    public async Task Invite(string friendAccount, BrainId friendBrain, Role role = Role.Editor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(friendAccount);
        ArgumentException.ThrowIfNullOrWhiteSpace(friendBrain.Value);
        RequireOwner();

        var account = friendAccount.Trim();
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

        var author = ResolveCallerAccount();
        var revision = State.Revision + 1;
        var message = new RoomMessage(revision, author, text.Trim(), Context.Time.GetUtcNow());

        await Store(State with
        {
            Revision = revision,
            Messages = [.. State.Messages, message],
        }).ConfigureAwait(true);

        await NudgeParticipantsAsync(revision, ct).ConfigureAwait(true);
    }

    public Task<RoomView> Read(long? sinceRevision = null, CancellationToken ct = default)
    {
        var messages = sinceRevision is { } since
            ? State.Messages.Where(m => m.Revision > since).ToArray()
            : State.Messages;

        var now = Context.Time.GetUtcNow();
        var present = _presence
            .Where(kv => now - kv.Value <= PresenceTtl)
            .Select(static kv => kv.Key)
            .OrderBy(static a => a, StringComparer.Ordinal)
            .ToArray();

        return Task.FromResult(new RoomView(State.Revision, messages, State.PinnedScreens, present));
    }

    public async Task<PublishedView> Act(string screenType, string screenKey, ScreenAction action, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(screenType);
        ArgumentException.ThrowIfNullOrWhiteSpace(screenKey);
        ArgumentNullException.ThrowIfNull(action);

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

    public Task Here(CancellationToken ct = default)
    {
        var account = ResolveCallerAccount();
        _presence[account] = Context.Time.GetUtcNow();
        return Task.CompletedTask;
    }

    private bool IsPinned(string screenType, string screenKey)
        => State.PinnedScreens.Any(p =>
            string.Equals(p.ScreenType, screenType, StringComparison.Ordinal)
            && string.Equals(p.ScreenKey, screenKey, StringComparison.Ordinal));

    private void RequireOwner()
        => RequireSameBrain(() => new UnauthorizedAccessException($"Only brain '{Id.Brain.Value}' may administer room '{Id.Key}'."));

    private string ResolveCallerAccount()
        => RequireCaller().For?.Account ?? Id.Brain.Value;

    private IRooms RoomsOf(BrainId brain)
        => Context.Get<IRooms>(new BindingTarget.Offered(brain, new NeuronId(brain, "social.rooms", IRooms.DefaultKey)));

    private async Task NudgeParticipantsAsync(long revision, CancellationToken ct)
    {
        foreach (var participant in State.Participants)
        {
            await RoomsOf(participant.Brain).Changed(Id, revision, ct).ConfigureAwait(true);
        }
    }

    private IDisposable? AsSocialModule()
    {
        var edge = services?.GetService(typeof(EdgeAuthority)) as EdgeAuthority;
        return edge?.Enter(edge.Module("social.room", Id.Brain));
    }
}
