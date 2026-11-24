using DigitalBrain;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Modules.Apps.Social;

internal sealed record RoomEntry(NeuronId Room, long LastRevision, DateTimeOffset UpdatedAt);

// A friend's conversation the person was invited to. The conversation is kept by its id text.
internal sealed record ConversationPointer(BrainId Brain, string Conversation, string Title, string By);

// Conversations is null in an index stored before the invitations to friends' conversations moved here from the roster.
internal sealed record RoomsState(RoomEntry[] Rooms, ConversationPointer[]? Conversations = null)
{
    public RoomsState() : this([]) { }
}

// A person's index of the rooms they were invited to. The contract is shareable and not offered, so the kernel lets any
// brain call Invited and Changed: the index itself decides who may write into it.
[Neuron("social.rooms")]
internal sealed class RoomsNeuron(IServiceProvider? services = null) : Neuron<RoomsState>, IRooms
{
    public async Task Invited(NeuronId room, CancellationToken ct = default)
    {
        await RequireNoticeOf(room).ConfigureAwait(true);
        var existing = State.Rooms.FirstOrDefault(r => r.Room == room);
        var entry = new RoomEntry(room, existing?.LastRevision ?? 0, Context.Time.GetUtcNow());
        var next = State.Rooms.Where(r => r.Room != room).Append(entry).ToArray();
        await Store(State with { Rooms = next }).ConfigureAwait(true);
    }

    public async Task Changed(NeuronId room, long revision, CancellationToken ct = default)
    {
        await RequireNoticeOf(room).ConfigureAwait(true);
        var existing = State.Rooms.FirstOrDefault(r => r.Room == room);
        var nextRevision = Math.Max(existing?.LastRevision ?? 0, revision);
        var entry = new RoomEntry(room, nextRevision, Context.Time.GetUtcNow());
        var next = State.Rooms.Where(r => r.Room != room).Append(entry).ToArray();
        await Store(State with { Rooms = next }).ConfigureAwait(true);
    }

    public Task<long> LastRevision(NeuronId room, CancellationToken ct = default)
    {
        var existing = State.Rooms.FirstOrDefault(r => r.Room == room);
        return Task.FromResult(existing?.LastRevision ?? 0);
    }

    public async Task InvitedToConversation(ConversationInvite invite, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invite);
        await RequireInvitationTo(invite).ConfigureAwait(true);
        var pointer = Pointer(invite);
        var others = (State.Conversations ?? []).Where(existing => !Same(existing, pointer));
        await Store(State with { Conversations = [.. others, pointer] }).ConfigureAwait(true);
    }

    public async Task AdoptConversations(ConversationInvite[] invites, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invites);
        if (!FromOwnRoster()) { throw FriendRelay.Refused(); }
        var held = State.Conversations ?? [];
        var added = new List<ConversationPointer>();
        foreach (var invite in invites)
        {
            if (invite is null) { continue; }
            var pointer = Pointer(invite);
            if (!held.Any(existing => Same(existing, pointer)) && !added.Any(existing => Same(existing, pointer))) { added.Add(pointer); }
        }
        // Handing over what the index already holds writes nothing: a second attempt changes nothing.
        if (added.Count == 0) { return; }
        await Store(State with { Conversations = [.. held, .. added] }).ConfigureAwait(true);
    }

    public Task<ConversationInvite[]> Conversations(CancellationToken ct = default)
    {
        if (Context.Caller is not { } caller || caller.Brain != Id.Brain) { throw FriendRelay.Refused(); }
        return Task.FromResult((State.Conversations ?? [])
            .Select(static pointer => new ConversationInvite(pointer.Brain, new ConversationId { Value = pointer.Conversation }, pointer.Title, new AccountId(pointer.By)))
            .ToArray());
    }

    private static ConversationPointer Pointer(ConversationInvite invite)
        => new(invite.Brain, invite.Conversation.Value ?? "", invite.Title ?? "", invite.By.Value ?? "");

    private static bool Same(ConversationPointer one, ConversationPointer other)
        => one.Brain == other.Brain && string.Equals(one.Conversation, other.Conversation, StringComparison.Ordinal);

    // The call the kernel stamped came through the proxy of this brain's own roster, inside the cluster.
    private bool FromOwnRoster()
        => Context.Caller is { Inside: true, Via: { } via } caller && caller.Brain == Id.Brain && via == FriendsDefaults.Id(Id.Brain.Value);

    // An invitation is taken from this brain's roster, which has checked it, or from the conversation itself: the stamped
    // caller came through that conversation's proxy in the inviter's own brain, the inviter owns that brain and is the
    // person the invitation names, and this brain's roster lists them as a friend. The invitation's own fields prove
    // nothing; they are compared with the stamped caller.
    private async Task RequireInvitationTo(ConversationInvite invite)
    {
        if (FromOwnRoster()) { return; }
        if (services?.GetService(typeof(IBrainOwners)) is not IBrainOwners owners) { throw FriendRelay.Refused(); }
        var (account, brain) = await FriendRelay.Require(Context.Caller, IConversation.Type, owners).ConfigureAwait(true);
        if (brain == Id.Brain
            || invite.Brain != brain
            || !string.Equals(invite.By.Value, account, StringComparison.Ordinal)
            || Context.Caller?.Via is not { } relay
            || !string.Equals(relay.Key, IConversation.KeyOf(invite.Conversation), StringComparison.Ordinal)
            || !await RoomFriends.IsFriend(services, Id.Brain, new AccountId(account)).ConfigureAwait(true))
        {
            throw FriendRelay.Refused();
        }
    }

    // A notice is taken from the room itself and from no one else: the call the kernel stamped came through that room's
    // own proxy, inside the cluster, and the room lives in the brain of a friend. Whoever spoke in the room is not asked
    // for; the room named in the argument proves nothing. A call that no neuron of another brain carried is this
    // brain's own side.
    private async Task RequireNoticeOf(NeuronId room)
    {
        if (Context.Caller is not { } caller) { throw FriendRelay.Refused(); }
        if (caller.Via is not { } relay || relay.Brain == Id.Brain)
        {
            if (caller.Brain == Id.Brain) { return; }
            throw FriendRelay.Refused();
        }

        if (!caller.Inside || !string.Equals(relay.Type, RoomFriends.RoomType, StringComparison.Ordinal) || relay != room)
        {
            throw FriendRelay.Refused();
        }

        var host = services?.GetService(typeof(IBrainOwners)) is IBrainOwners owners
            ? await owners.OwnerAccount(relay.Brain).ConfigureAwait(true)
            : null;
        if (string.IsNullOrEmpty(host) || !await RoomFriends.IsFriend(services, Id.Brain, new AccountId(host)).ConfigureAwait(true))
        {
            throw FriendRelay.Refused();
        }
    }
}
