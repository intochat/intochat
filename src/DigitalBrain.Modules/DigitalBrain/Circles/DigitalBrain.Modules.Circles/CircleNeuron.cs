using DigitalBrain.Circles.Signals;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Compute;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Circles;

[GenerateSerializer, Alias("circles.circle-record")]
internal sealed class CircleRecord
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public string Owner { get; set; } = "";
    [Id(2)] public string[] Members { get; set; } = [];
    [Id(3)] public string[] SharedApps { get; set; } = [];
    [Id(4)] public int ComputeSharePercent { get; set; }
    [Id(5)] public string[] Invited { get; set; } = [];
    [Id(6)] public string? InvitedBy { get; set; }
    [Id(7)] public long Version { get; set; }

    public CircleRecord Clone() => (CircleRecord)MemberwiseClone();
}

// One brain's copy of a circle. The owner's copy decides and hands each change to the members' copies
// (Apply); a member's copy only follows. Reentrant because copies in different brains call each other
// while a call is in flight (accepting calls the owner, who in turn tells the other members).
[Orleans.Concurrency.Reentrant]
[GrainType("circles.circle")]
internal sealed class CircleNeuron(
    [PersistentState("circles.circle", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<CircleRecord> store,
    ICircleShares shares, ILogger<CircleNeuron> logger)
    : Neuron<CircleRecord>(store), ICircle
{
    private string Key => this.GetPrimaryKeyString();
    private string Brain => BrainKey.BrainOf(Key);
    private string Name => Key[(Brain.Length + "/circles/".Length)..];

    public Task<string[]> Members() => Task.FromResult(Current().Members);

    public Task<CircleState> State() => Task.FromResult(Describe(Exists ? Snapshot : throw new KeyNotFoundException($"{Brain} is not in a circle named {Name}.")));

    public async Task Invite(string brainId)
    {
        if (!BrainScope.IsValidId(brainId)) { throw new ArgumentException("That is not a brain id.", nameof(brainId)); }
        var next = Owned();
        if (next.Members.Contains(brainId) || next.Invited.Contains(brainId))
        { throw new InvalidOperationException($"{brainId} is already in {Name} or invited to it."); }
        // The invited brain hears first: an invitation nobody received is not recorded.
        await GrainFactory.GetGrain<ICircle>(ICircle.Key(brainId, Name)).Invited(Brain);
        next.Invited = [.. next.Invited, brainId];
        await Commit(next, exclude: null);
    }

    public async Task Invited(string from)
    {
        if (Exists && Snapshot.Owner.Length > 0)
        { throw new InvalidOperationException($"{Brain} already has a circle named {Name}."); }
        if (Exists && Snapshot.InvitedBy == from) { return; }
        await Save(new CircleRecord { Name = Name, InvitedBy = from }, new CircleInvited(Name, from));
    }

    public async Task<CircleState> Accept()
    {
        if (!Exists || Snapshot.InvitedBy is not { } owner || Snapshot.Owner.Length > 0)
        { throw new InvalidOperationException($"{Brain} has no invitation to {Name}."); }
        var joined = await GrainFactory.GetGrain<ICircle>(ICircle.Key(owner, Name)).Joined(Brain);
        await Follow(joined);
        return Describe(Current());
    }

    public async Task<CircleState> Joined(string brainId)
    {
        var next = Owned();
        if (!next.Invited.Contains(brainId)) { throw new InvalidOperationException($"{brainId} was not invited to {Name}."); }
        next.Invited = [.. next.Invited.Where(invited => invited != brainId)];
        next.Members = [.. next.Members, brainId];
        return await Commit(next, exclude: brainId);
    }

    public async Task Leave(string brainId)
    {
        if (brainId == Brain)
        {
            if (!Exists) { throw new KeyNotFoundException($"{Brain} is not in a circle named {Name}."); }
            if (Snapshot.Owner == Brain) { throw new InvalidOperationException($"The owner cannot leave {Name}."); }
            var owner = Snapshot.Owner.Length > 0 ? Snapshot.Owner : Snapshot.InvitedBy!;
            await GrainFactory.GetGrain<ICircle>(ICircle.Key(owner, Name)).Left(Brain);
            await Clear(new CircleChanged(Name, [], [], 0));
            return;
        }
        var next = Owned();
        if (!next.Members.Contains(brainId) && !next.Invited.Contains(brainId))
        { throw new InvalidOperationException($"{brainId} is not in {Name}."); }
        await Remove(next, brainId, informLeaver: true);
    }

    public async Task Left(string brainId)
    {
        var next = Owned();
        if (brainId == Brain) { throw new InvalidOperationException($"The owner cannot leave {Name}."); }
        if (next.Members.Contains(brainId) || next.Invited.Contains(brainId)) { await Remove(next, brainId, informLeaver: false); }
    }

    public async Task Share(string appName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        var next = Owned();
        if (next.SharedApps.Contains(appName)) { return; }
        next.SharedApps = [.. next.SharedApps, appName];
        await Commit(next, exclude: null);
    }

    public async Task Unshare(string appName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        var next = Owned();
        if (!next.SharedApps.Contains(appName)) { return; }
        next.SharedApps = [.. next.SharedApps.Where(shared => shared != appName)];
        await Commit(next, exclude: null);
    }

    public async Task SetComputeShare(int percent)
    {
        if (percent is < 0 or > ICircle.MaxComputeSharePercent)
        { throw new ArgumentOutOfRangeException(nameof(percent), percent, $"A circle may share 0 to {ICircle.MaxComputeSharePercent}% of the owner's compute."); }
        var next = Owned();
        await shares.Configure(Key, CallerContextStamper.Require().AccountId, percent);
        next.ComputeSharePercent = percent;
        await Commit(next, exclude: null);
    }

    public async Task Apply(CircleState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!Exists || state.Version <= Snapshot.Version) { return; }
        if (!state.Members.Contains(Brain)) { await Clear(new CircleChanged(Name, [], [], 0)); return; }
        await Follow(state);
    }

    private CircleRecord Current()
        => Exists && Snapshot.Owner.Length > 0 ? Snapshot : throw new KeyNotFoundException($"{Brain} is not in a circle named {Name}.");

    // The owner's record to change: a copy of this one, or the circle's first state when the owner starts it.
    private CircleRecord Owned()
    {
        if (!Exists) { return new CircleRecord { Name = Name, Owner = Brain, Members = [Brain] }; }
        if (Snapshot.Owner != Brain) { throw new InvalidOperationException($"Only the owner of {Name} can change it."); }
        return Snapshot.Clone();
    }

    private async Task<CircleState> Commit(CircleRecord next, string? exclude)
    {
        next.Version++;
        await Save(next, Changed(next));
        var state = Describe(next);
        await Tell(state, next.Members.Where(member => member != Brain && member != exclude));
        return state;
    }

    private async Task Remove(CircleRecord next, string brainId, bool informLeaver)
    {
        next.Members = [.. next.Members.Where(member => member != brainId)];
        next.Invited = [.. next.Invited.Where(invited => invited != brainId)];
        var state = await Commit(next, exclude: null);
        if (informLeaver) { await Tell(state, [brainId]); }
    }

    private Task Follow(CircleState state)
    {
        var next = new CircleRecord
        {
            Name = state.Name, Owner = state.Owner, Members = state.Members, SharedApps = state.SharedApps,
            ComputeSharePercent = state.ComputeSharePercent, Invited = state.Invited, Version = state.Version,
        };
        return Save(next, Changed(next));
    }

    private async Task Tell(CircleState state, IEnumerable<string> brains)
    {
        foreach (var brain in brains)
        {
            try { await GrainFactory.GetGrain<ICircle>(ICircle.Key(brain, Name)).Apply(state); }
            catch (Exception error) { logger.LogWarning(error, "Circle {Circle} could not tell {Brain} of version {Version}", Key, brain, state.Version); }
        }
    }

    private CircleChanged Changed(CircleRecord record) => new(Name, record.Members, record.SharedApps, record.ComputeSharePercent);

    private static CircleState Describe(CircleRecord record) => new(record.Name, record.Owner, record.Members, record.SharedApps,
        record.ComputeSharePercent, record.Invited, record.InvitedBy, record.Version);
}
