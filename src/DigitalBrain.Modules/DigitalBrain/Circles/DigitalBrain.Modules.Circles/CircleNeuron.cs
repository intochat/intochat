using DigitalBrain.Circles.Signals;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Compute;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

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

// One brain's copy of a circle. The owner's copy decides; a member's copy follows by pulling the owner's
// state whenever it is told to or asked. Nothing here awaits another copy that could await this one
// back, so the grain is not reentrant: every change is committed before any copy is told about it.
[GrainType(CircleCallGuard.CircleType)]
internal sealed class CircleNeuron(
    [PersistentState("circles.circle", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<CircleRecord> store,
    ILogger<CircleNeuron> logger)
    : Neuron<CircleRecord>(store), ICircle
{
    private CircleAddress Address => CircleAddress.Of(this.GetPrimaryKeyString());
    private string Brain => Address.Brain;
    private string Name => Address.Name;

    public async Task<string[]> Members()
    {
        RequireBrain();
        await Follow();
        return Current().Members;
    }

    public async Task<CircleState> State()
    {
        RequireBrain();
        await Follow();
        return Describe(Exists ? Snapshot : throw Missing());
    }

    public Task<CircleState> Roster() => Task.FromResult(Describe(Exists ? Snapshot : throw Missing()));

    public async Task Invite(string brainId)
    {
        RequireBrain();
        if (!BrainScope.IsValidId(brainId)) { throw new ArgumentException("That is not a brain id.", nameof(brainId)); }
        var next = Owned();
        if (next.Members.Contains(brainId) || next.Invited.Contains(brainId))
        { throw new InvalidOperationException($"{brainId} is already in {Name} or invited to it."); }
        next.Invited = [.. next.Invited, brainId];
        // The invited brain hears first, so a refused invitation is never recorded; the owner is busy
        // meanwhile (not reentrant), so nothing can change under the record being built.
        await GrainFactory.GetGrain<ICircle>(ICircle.Key(brainId, Name, Brain)).Invited();
        await Commit(next, [], tell: false);
    }

    public async Task Invited()
    {
        var from = Caller();
        // Interleaves with a pull in flight (so the owner is never kept waiting on a member that waits on the
        // owner), but not with the write that pull ends in.
        if (running is { } writing) { try { await writing; } catch { /* the pull's failure is the pull's; this invitation proceeds */ } }
        // The owner is the authority: a copy left over from an earlier membership gives way to the new invitation.
        if (Exists && Snapshot.Owner.Length == 0 && Snapshot.InvitedBy == from) { return; }
        await Save(new CircleRecord { Name = Name, InvitedBy = from }, new CircleInvited(Name, from));
        await Touched("invited", "invited");
    }

    public async Task<CircleState> Accept()
    {
        RequireBrain();
        if (!Exists || Snapshot.InvitedBy is not { } owner || Snapshot.Owner.Length > 0)
        { throw new InvalidOperationException($"{Brain} has no invitation to {Name}."); }
        var joined = await GrainFactory.GetGrain<ICircle>(ICircle.Key(owner, Name)).Joined();
        await Follow(joined);
        return Describe(Current());
    }

    public async Task<CircleState> Joined()
    {
        var joiner = Caller();
        var next = Owned();
        if (!next.Invited.Contains(joiner)) { throw new InvalidOperationException($"{joiner} was not invited to {Name}."); }
        next.Invited = [.. next.Invited.Where(invited => invited != joiner)];
        next.Members = [.. next.Members, joiner];
        return await Commit(next, [], tell: true);
    }

    public async Task Leave(string brainId)
    {
        RequireBrain();
        if (brainId == Brain)
        {
            if (!Exists) { throw Missing(); }
            if (Address.IsOwnerCopy) { throw new InvalidOperationException($"The owner cannot leave {Name}."); }
            var owner = Snapshot.Owner.Length > 0 ? Snapshot.Owner : Snapshot.InvitedBy!;
            await GrainFactory.GetGrain<ICircle>(ICircle.Key(owner, Name)).Left();
            await Clear(new CircleChanged(Name, [], [], 0));
            await Touched("left", "");
            return;
        }
        var next = Owned();
        if (!next.Members.Contains(brainId) && !next.Invited.Contains(brainId))
        { throw new InvalidOperationException($"{brainId} is not in {Name}."); }
        await Drop(next, brainId);
    }

    public async Task Left()
    {
        var leaver = Caller();
        var next = Owned();
        if (next.Members.Contains(leaver) || next.Invited.Contains(leaver)) { await Drop(next, leaver, tellLeaver: false); }
    }

    public Task<CircleState?> StateForCaller()
    {
        var asker = Caller();
        if (!Address.IsOwnerCopy || !Exists) { throw new InvalidOperationException($"{Brain} does not run {Name}."); }
        return Task.FromResult<CircleState?>(Snapshot.Members.Contains(asker) || Snapshot.Invited.Contains(asker) ? Describe(Snapshot) : null);
    }

    public Task Changed() => Follow();

    public async Task Share(string appName)
    {
        RequireBrain();
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        var next = Owned();
        if (next.SharedApps.Contains(appName)) { return; }
        next.SharedApps = [.. next.SharedApps, appName];
        await Commit(next, [], tell: true);
    }

    public async Task Unshare(string appName)
    {
        RequireBrain();
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        var next = Owned();
        if (!next.SharedApps.Contains(appName)) { return; }
        next.SharedApps = [.. next.SharedApps.Where(shared => shared != appName)];
        await Commit(next, [], tell: true);
    }

    public async Task SetComputeShare(int percent)
    {
        RequireBrain();
        if (percent is < 0 or > ICircle.MaxComputeSharePercent)
        { throw new ArgumentOutOfRangeException(nameof(percent), percent, $"A circle may share 0 to {ICircle.MaxComputeSharePercent}% of the owner's compute."); }
        var next = Owned();
        next.ComputeSharePercent = percent;
        await Commit(next, [], tell: true);
    }

    // A call from outside is acted on only by the brain that holds this copy.
    private void RequireBrain()
    {
        if (!CallerContextStamper.TryGet(out _) || BrainScope.CurrentId() != Brain)
        { throw new UnauthorizedAccessException($"{this.GetPrimaryKeyString()} belongs to brain {Brain}."); }
    }

    // The brain the kernel says made an [Internal] call (see CircleCallGuard).
    private static string Caller() => RequestContext.Get(CircleCallGuard.From) as string
        ?? throw new UnauthorizedAccessException("This call does not come from a copy of the circle.");

    private KeyNotFoundException Missing() => new($"{Brain} is not in a circle named {Name}.");

    private CircleRecord Current()
        => Exists && Snapshot.Owner.Length > 0 ? Snapshot : throw Missing();

    // The owner's record to change: a copy of this one, or the circle's first state when the owner starts it.
    private CircleRecord Owned()
    {
        if (!Address.IsOwnerCopy) { throw new InvalidOperationException($"Only the owner of {Name} can change it."); }
        return Exists ? Snapshot.Clone() : new CircleRecord { Name = Name, Owner = Brain, Members = [Brain] };
    }

    // The kernel knows the share and the members before the change is saved, and the change is saved
    // before anyone is told.
    private async Task<CircleState> Commit(CircleRecord next, string[] also, bool tell)
    {
        next.Version++;
        await GrainFactory.GetGrain<ICircleShares>(this.GetPrimaryKeyString()).Configure(next.ComputeSharePercent, next.Members);
        await Save(next, Changed(next));
        await Touched("changed", "owner");
        var state = Describe(next);
        if (tell) { await Tell(next.Members.Where(member => member != Brain).Concat(also)); }
        return state;
    }

    private async Task Drop(CircleRecord next, string brainId, bool tellLeaver = true)
    {
        next.Members = [.. next.Members.Where(member => member != brainId)];
        next.Invited = [.. next.Invited.Where(invited => invited != brainId)];
        await Commit(next, tellLeaver ? [brainId] : [], tell: true);
    }

    // Pulls the owner's state into this copy: newer state replaces it, no longer being named clears it.
    private async Task Follow()
    {
        if (!Exists || Address.IsOwnerCopy || Snapshot.Owner.Length == 0) { return; }
        CircleState? latest;
        try { latest = await GrainFactory.GetGrain<ICircle>(Address.OwnerKey).StateForCaller(); }
        catch (Exception error)
        {
            logger.LogWarning(error, "Circle {Circle} could not pull the state of {Owner}", this.GetPrimaryKeyString(), Address.Owner);
            return;
        }
        // An invitation may have replaced this copy while the owner was being asked.
        if (!Exists || Snapshot.Owner.Length == 0) { return; }
        if (latest is null) { await Writing(async () => { await Clear(new CircleChanged(Name, [], [], 0)); await Touched("left", ""); }); }
        else if (latest.Version > Snapshot.Version) { await Writing(() => Follow(latest)); }
    }

    private Task? running;

    private async Task Writing(Func<Task> write)
    {
        var task = write();
        running = task;
        try { await task; }
        finally { if (running == task) { running = null; } }
    }

    private async Task Follow(CircleState state)
    {
        await Save(new CircleRecord
        {
            Name = state.Name, Owner = state.Owner, Members = state.Members, SharedApps = state.SharedApps,
            ComputeSharePercent = state.ComputeSharePercent, Invited = state.Invited, Version = state.Version,
        }, new CircleChanged(Name, state.Members, state.SharedApps, state.ComputeSharePercent));
        await Touched("changed", "member");
    }

    // One-way: a brain that misses it pulls on its next read.
    private async Task Tell(IEnumerable<string> brains)
    {
        foreach (var brain in brains.Distinct())
        {
            try { await GrainFactory.GetGrain<ICircle>(ICircle.Key(brain, Name, Brain)).Changed(); }
            catch (Exception error) { logger.LogWarning(error, "Circle {Circle} could not tell {Brain}", this.GetPrimaryKeyString(), brain); }
        }
    }

    // The brain's index publishes every change to its circles (CircleTouched), after the copy is durable.
    private Task Touched(string kind, string role)
        => GrainFactory.GetGrain<ICircles>(ICircles.Key(Brain)).Touch(Name, Address.Owner, kind, role);

    private CircleChanged Changed(CircleRecord record) => new(Name, record.Members, record.SharedApps, record.ComputeSharePercent);

    private static CircleState Describe(CircleRecord record) => new(record.Name, record.Owner, record.Members, record.SharedApps,
        record.ComputeSharePercent, record.Invited, record.InvitedBy, record.Version);
}
