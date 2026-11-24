using DigitalBrain;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Modules.Apps.Social;

internal sealed record RoomEntry(NeuronId Room, long LastRevision, DateTimeOffset UpdatedAt);

internal sealed record RoomsState(RoomEntry[] Rooms)
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
        await Store(new RoomsState(next)).ConfigureAwait(true);
    }

    public async Task Changed(NeuronId room, long revision, CancellationToken ct = default)
    {
        await RequireNoticeOf(room).ConfigureAwait(true);
        var existing = State.Rooms.FirstOrDefault(r => r.Room == room);
        var nextRevision = Math.Max(existing?.LastRevision ?? 0, revision);
        var entry = new RoomEntry(room, nextRevision, Context.Time.GetUtcNow());
        var next = State.Rooms.Where(r => r.Room != room).Append(entry).ToArray();
        await Store(new RoomsState(next)).ConfigureAwait(true);
    }

    public Task<long> LastRevision(NeuronId room, CancellationToken ct = default)
    {
        var existing = State.Rooms.FirstOrDefault(r => r.Room == room);
        return Task.FromResult(existing?.LastRevision ?? 0);
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
