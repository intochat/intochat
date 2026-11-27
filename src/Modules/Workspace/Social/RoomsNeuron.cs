using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Modules.Apps.Social;

internal sealed record RoomEntry(
    NeuronId Room,
    long LastRevision,
    DateTimeOffset UpdatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RoomKind? Kind = null);

// A friend's conversation the person was invited to. The conversation is kept by its id text.
internal sealed record ConversationPointer(BrainId Brain, string Conversation, string Title, string By);

// Conversations is null in an index stored before the invitations to friends' conversations moved here from the roster.
internal sealed record RoomsState(RoomEntry[] Rooms, ConversationPointer[]? Conversations = null)
{
    public RoomsState() : this([]) { }
}

// A person's index of the rooms they own or were invited to. The contract is shareable and not offered, so the kernel lets any
// brain call Invited and Changed: the index itself decides who may write into it.
[Neuron("social.rooms")]
internal sealed class RoomsNeuron(IServiceProvider? services = null) : Neuron<RoomsState>, IRooms
{
    public Task<RoomSummary[]> List(CancellationToken ct = default)
    {
        _ = RequireSameBrain(FriendRelay.Refused);
        var summaries = State.Rooms
            .OrderByDescending(static r => r.UpdatedAt)
            .Select(r => new RoomSummary(
                r.Room,
                string.IsNullOrWhiteSpace(r.Title) ? r.Room.Key : r.Title,
                r.Kind ?? RoomKind.Group,
                r.LastRevision,
                r.UpdatedAt,
                IsHost: r.Room.Brain == Id.Brain))
            .ToArray();
        return Task.FromResult(summaries);
    }

    public async Task<NeuronId> Create(string title, RoomKind kind = RoomKind.Group, string? roomKey = null, CancellationToken ct = default)
    {
        _ = RequireSameBrain(FriendRelay.Refused);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var cleanTitle = title.Trim();
        var key = string.IsNullOrWhiteSpace(roomKey)
            ? $"{(kind == RoomKind.Direct ? "dm" : "room")}-{Guid.NewGuid():N}"
            : roomKey.Trim();
        var roomId = new NeuronId(Id.Brain, RoomFriends.RoomType, key);

        var existing = State.Rooms.FirstOrDefault(r => r.Room == roomId);
        var entry = new RoomEntry(roomId, existing?.LastRevision ?? 0, Context.Time.GetUtcNow(), cleanTitle, kind);
        var next = State.Rooms.Where(r => r.Room != roomId).Append(entry).ToArray();
        await Save(State with { Rooms = next }, new RoomsIndexChanged(Id.Brain, roomId, "created")).ConfigureAwait(true);
        return roomId;
    }

    public async Task Invite(NeuronId room, string friendAccount, BrainId friendBrain, Role role = Role.Editor, CancellationToken ct = default)
    {
        _ = RequireSameBrain(FriendRelay.Refused);
        ArgumentException.ThrowIfNullOrWhiteSpace(friendAccount);
        ArgumentException.ThrowIfNullOrWhiteSpace(friendBrain.Value);
        if (room.Brain != Id.Brain)
        {
            throw new UnauthorizedAccessException($"Only brain '{room.Brain.Value}' may invite participants to room '{room.Key}'.");
        }

        var existing = State.Rooms.FirstOrDefault(r => r.Room == room);
        var entry = new RoomEntry(room, existing?.LastRevision ?? 0, Context.Time.GetUtcNow(), existing?.Title, existing?.Kind);
        var next = State.Rooms.Where(r => r.Room != room).Append(entry).ToArray();
        await Save(State with { Rooms = next }, new RoomsIndexChanged(Id.Brain, room, "invited")).ConfigureAwait(true);
    }

    public async Task Leave(NeuronId room, CancellationToken ct = default)
    {
        _ = RequireSameBrain(FriendRelay.Refused);
        if (State.Rooms.All(r => r.Room != room))
        {
            return;
        }

        var next = State.Rooms.Where(r => r.Room != room).ToArray();
        await Save(State with { Rooms = next }, new RoomsIndexChanged(Id.Brain, room, "left")).ConfigureAwait(true);
    }

    public async Task Invited(NeuronId room, CancellationToken ct = default)
    {
        await RequireNoticeOf(room).ConfigureAwait(true);
        var existing = State.Rooms.FirstOrDefault(r => r.Room == room);
        var entry = new RoomEntry(room, existing?.LastRevision ?? 0, Context.Time.GetUtcNow(), existing?.Title, existing?.Kind);
        var next = State.Rooms.Where(r => r.Room != room).Append(entry).ToArray();
        using var scope = OwnModuleIfForeign();
        await Save(State with { Rooms = next }, new RoomsIndexChanged(Id.Brain, room, "invited")).ConfigureAwait(true);
    }

    public async Task Changed(NeuronId room, long revision, CancellationToken ct = default)
    {
        await RequireNoticeOf(room).ConfigureAwait(true);
        var existing = State.Rooms.FirstOrDefault(r => r.Room == room);
        var nextRevision = Math.Max(existing?.LastRevision ?? 0, revision);
        var entry = new RoomEntry(room, nextRevision, Context.Time.GetUtcNow(), existing?.Title, existing?.Kind);
        var next = State.Rooms.Where(r => r.Room != room).Append(entry).ToArray();
        using var scope = OwnModuleIfForeign();
        await Save(State with { Rooms = next }, new RoomChanged(room, nextRevision)).ConfigureAwait(true);
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
        _ = RequireSameBrain(FriendRelay.Refused);
        return Task.FromResult((State.Conversations ?? [])
            .Select(static pointer => new ConversationInvite(pointer.Brain, new ConversationId { Value = pointer.Conversation }, pointer.Title, new AccountId(pointer.By)))
            .ToArray());
    }

    private IDisposable? OwnModuleIfForeign()
    {
        if (Context.Caller is { Brain: var brain } && brain == Id.Brain)
        {
            return null;
        }
        var edge = services?.GetService(typeof(EdgeAuthority)) as EdgeAuthority;
        return edge?.Enter(edge.Module("social.rooms", Id.Brain));
    }

    private static ConversationPointer Pointer(ConversationInvite invite)
        => new(invite.Brain, invite.Conversation.Value ?? "", invite.Title ?? "", invite.By.Value ?? "");

    private static bool Same(ConversationPointer one, ConversationPointer other)
        => one.Brain == other.Brain && string.Equals(one.Conversation, other.Conversation, StringComparison.Ordinal);

    // The call the kernel stamped came through the proxy of this brain's own roster, inside the cluster.
    private bool FromOwnRoster()
        => IsSameBrainCaller() && Context.Caller is { Inside: true, Via: { } via } && via == FriendsDefaults.Id(Id.Brain.Value);

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
            if (caller.Inside && caller.Via is { } ownRoom && string.Equals(ownRoom.Type, RoomFriends.RoomType, StringComparison.Ordinal) && ownRoom == room)
            {
                return;
            }
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
