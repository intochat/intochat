using DigitalBrain;
using DigitalBrain.Neurons;

namespace IntoChat.OS.Modules.Apps.Social;

internal sealed record RoomEntry(NeuronId Room, long LastRevision, DateTimeOffset UpdatedAt);

internal sealed record RoomsState(RoomEntry[] Rooms)
{
    public RoomsState() : this([]) { }
}

[Neuron("social.rooms")]
internal sealed class RoomsNeuron : Neuron<RoomsState>, IRooms
{
    public async Task Invited(NeuronId room, CancellationToken ct = default)
    {
        var existing = State.Rooms.FirstOrDefault(r => r.Room == room);
        var entry = new RoomEntry(room, existing?.LastRevision ?? 0, Context.Time.GetUtcNow());
        var next = State.Rooms.Where(r => r.Room != room).Append(entry).ToArray();
        await Store(new RoomsState(next)).ConfigureAwait(true);
    }

    public async Task Changed(NeuronId room, long revision, CancellationToken ct = default)
    {
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
}
