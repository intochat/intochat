using DigitalBrain.Circles.Signals;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Compute;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Framework.Platform.Identity;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace DigitalBrain.Circles;

[GenerateSerializer, Alias("circles.circle-record")]
internal sealed class CircleRecord
{
    [Id(0)] public string Name { get; set; } = "";
    // The circle core: the creator's account. A copy: the owner brain.
    [Id(1)] public string Owner { get; set; } = "";
    [Id(2)] public string[] Members { get; set; } = [];
    [Id(3)] public string[] SharedApps { get; set; } = [];
    [Id(4)] public int ComputeSharePercent { get; set; }
    [Id(5)] public string[] Invited { get; set; } = [];
    [Id(6)] public string? InvitedBy { get; set; }
    [Id(7)] public long Version { get; set; }
    // The circle core's contributed machines, each with the member account that contributed it.
    [Id(8)] public CircleMachine[] Machines { get; set; } = [];
    [Id(9)] public CircleSchedule[] Schedules { get; set; } = [];

    public CircleRecord Clone() => (CircleRecord)MemberwiseClone();
}

[GenerateSerializer, Alias("circles.circle-machine")]
internal sealed record CircleMachine([property: Id(0)] string MachineId, [property: Id(1)] string AccountId);

// The circle core `{circle}/circle` (T26-1), or one brain's copy of a circle `{brain}/circles/{name}` (the copy
// protocol, kept until the circle app split). The core is the circle brain's own state: members are accounts the
// trust side records as memberships (R129), its machines are contributed (R128). A copy follows the owner's copy by
// pulling. Nothing here awaits another copy that could await this one back, so the grain is not reentrant: every
// change is committed before any copy is told about it.
[GrainType(CircleCallGuard.CircleType)]
internal sealed class CircleNeuron(
    [PersistentState("circles.circle", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<CircleRecord> store,
    IIdentityClient identity,
    ICirclePartKeys keys,
    ILogger<CircleNeuron> logger)
    : Neuron<CircleRecord>(store), ICircle
{
    private const string CoreSuffix = "/circle";

    private bool IsCore => CircleAddress.TryParse(this.GetPrimaryKeyString()) is null;
    private string CircleBrain => this.GetPrimaryKeyString()[..^CoreSuffix.Length];

    private CircleAddress Address => CircleAddress.Of(this.GetPrimaryKeyString());
    private string Brain => Address.Brain;
    private string Name => Address.Name;

    public async Task<string[]> Members()
    {
        if (IsCore) { return Core().Members; }
        RequireBrain();
        await Follow();
        return Current().Members;
    }

    public async Task<CircleState> State()
    {
        if (IsCore) { return Describe(Core()); }
        RequireBrain();
        await Follow();
        return Describe(Exists ? Snapshot : throw Missing());
    }

    public Task<CircleState> Roster() => Task.FromResult(Describe(IsCore ? Core() : Exists ? Snapshot : throw Missing()));

    // The circle core

    public async Task Found(string name, string machineId)
    {
        if (!IsCore) { throw new InvalidOperationException($"{this.GetPrimaryKeyString()} is a circle copy, not a circle core."); }
        if (!ICircle.IsValidName(name)) { throw new ArgumentException("A circle name is 1 to 40 of a-z, 0-9 and '-'.", nameof(name)); }
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        if (Exists) { return; }
        var creator = Caller().AccountId;
        // Inside the brain's Create turn: the compute share (which reads the brain) is stated on the first change.
        var founded = new CircleRecord { Name = name, Owner = creator, Members = [creator], Machines = [new(machineId, creator)], Version = 1 };
        await Save(founded, Changed(founded));
    }

    public async Task Invite(string accountId)
    {
        if (!IsCore) { await InviteCopy(accountId); return; }
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        var next = MemberChange();
        if (next.Members.Contains(accountId) || next.Invited.Contains(accountId))
        { throw new InvalidOperationException($"{accountId} is already in {next.Name} or invited to it."); }
        next.Invited = [.. next.Invited, accountId];
        await CommitCore(next);
    }

    // R129: the invitation is the authority for a membership; the caller's machine joins the circle with it.
    public async Task<CircleState> Accept()
    {
        if (!IsCore) { return await AcceptCopy(); }
        var caller = Caller();
        var record = Core();
        if (caller.BrainId != CircleBrain || !record.Invited.Contains(caller.AccountId))
        { throw new InvalidOperationException($"{caller.AccountId} has no invitation to {record.Name}."); }
        var machine = caller.ForwardedBy ?? identity.MachineId ?? throw new InvalidOperationException("This host is not a machine; it contributes none.");
        await identity.EnsureMember(new Member
        {
            PrincipalId = caller.PrincipalId, AccountId = caller.AccountId, BrainId = CircleBrain,
            Role = MemberRole.Member, DisplayName = caller.PrincipalId, JoinedAt = DateTimeOffset.UtcNow,
        });
        await identity.ContributeMachine(CircleBrain, machine);
        await keys.ShareWith(CircleBrain, machine);
        var next = record.Clone();
        next.Invited = [.. next.Invited.Where(invited => invited != caller.AccountId)];
        next.Members = [.. next.Members, caller.AccountId];
        if (!next.Machines.Any(known => known.MachineId == machine)) { next.Machines = [.. next.Machines, new(machine, caller.AccountId)]; }
        return await CommitCore(next);
    }

    public async Task Contribute(string machineId)
    {
        if (!IsCore) { throw new InvalidOperationException($"{this.GetPrimaryKeyString()} is a circle copy; machines are contributed to a circle core."); }
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        var caller = Caller();
        var next = MemberChange();
        if (next.Machines.Any(known => known.MachineId == machineId)) { return; }
        await identity.ContributeMachine(CircleBrain, machineId);
        await keys.ShareWith(CircleBrain, machineId);
        next.Machines = [.. next.Machines, new(machineId, caller.AccountId)];
        await CommitCore(next);
    }

    // R129: the creator removes anyone; a member removes itself. The creator leaving deletes the circle.
    public async Task Remove(string accountId)
    {
        if (!IsCore) { throw new InvalidOperationException($"{this.GetPrimaryKeyString()} is a circle copy; members are removed from a circle core."); }
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        var caller = Caller();
        var next = MemberChange();
        if (caller.AccountId != next.Owner && caller.AccountId != accountId)
        { throw new UnauthorizedAccessException($"Only {next.Name}'s creator removes another member."); }
        if (accountId == next.Owner)
        {
            if (caller.AccountId != next.Owner) { throw new InvalidOperationException($"The creator of {next.Name} cannot be removed."); }
            await Delete(next);
            return;
        }
        if (!next.Members.Contains(accountId) && !next.Invited.Contains(accountId)) { throw new InvalidOperationException($"{accountId} is not in {next.Name}."); }
        await Drop(next, accountId);
        // T26-5: the machines that remain get new part keys; nothing written from here on opens under the old ones.
        await keys.Rotate(CircleBrain);
        await CommitCore(next);
    }

    private async Task Drop(CircleRecord next, string accountId)
    {
        if (next.Members.Contains(accountId)) { await identity.RemoveMember(CircleBrain, accountId); }
        foreach (var machine in next.Machines.Where(known => known.AccountId == accountId))
        { await identity.WithdrawMachine(CircleBrain, machine.MachineId); }
        next.Members = [.. next.Members.Where(member => member != accountId)];
        next.Invited = [.. next.Invited.Where(invited => invited != accountId)];
        next.Machines = [.. next.Machines.Where(known => known.AccountId != accountId)];
    }

    // The creator's leave: every other member and every machine goes, and the circle's state with them.
    private async Task Delete(CircleRecord next)
    {
        foreach (var member in next.Members.Concat(next.Invited).Where(member => member != next.Owner).Distinct().ToArray()) { await Drop(next, member); }
        await keys.Rotate(CircleBrain);
        foreach (var machine in next.Machines) { await identity.WithdrawMachine(CircleBrain, machine.MachineId); }
        await GrainFactory.GetGrain<ICircleShares>(this.GetPrimaryKeyString()).Configure(0, []);
        await Clear(new CircleChanged(next.Name, [], [], 0, []));
    }

    private CircleRecord Core() => Exists ? Snapshot : throw new KeyNotFoundException($"Circle {CircleBrain} has not been founded.");

    // A change to the circle core is a member's.
    private CircleRecord MemberChange()
    {
        var record = Core();
        var caller = Caller();
        if (caller.BrainId != CircleBrain || !record.Members.Contains(caller.AccountId))
        { throw new UnauthorizedAccessException($"{caller.AccountId} is not a member of {record.Name}."); }
        return record.Clone();
    }

    private async Task<CircleState> CommitCore(CircleRecord next)
    {
        next.Version++;
        await GrainFactory.GetGrain<ICircleShares>(this.GetPrimaryKeyString()).Configure(next.ComputeSharePercent, next.Members);
        await Save(next, Changed(next));
        return Describe(next);
    }

    private static CallerContext Caller()
        => CallerContextStamper.TryGet(out var caller) ? caller : throw new UnauthorizedAccessException("A circle acts only on a stamped caller.");

    // The per-brain copies (until the circle app split)

    private async Task InviteCopy(string brainId)
    {
        RequireBrain();
        if (!BrainScope.IsValidId(brainId)) { throw new ArgumentException("That is not a brain id.", nameof(brainId)); }
        if (!await identity.BrainExists(brainId))
        { throw new ArgumentException($"There is no brain {brainId} to invite.", nameof(brainId)); }
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
        var from = CopyCaller();
        // Interleaves with a pull in flight (so the owner is never kept waiting on a member that waits on the
        // owner), but not with the write that pull ends in.
        if (running is { } writing) { try { await writing; } catch { /* the pull's failure is the pull's; this invitation proceeds */ } }
        // The owner is the authority: a copy left over from an earlier membership gives way to the new invitation.
        if (Exists && Snapshot.Owner.Length == 0 && Snapshot.InvitedBy == from) { return; }
        await Save(new CircleRecord { Name = Name, InvitedBy = from }, new CircleInvited(Name, from));
        await Touched("invited", "invited");
    }

    private async Task<CircleState> AcceptCopy()
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
        var joiner = CopyCaller();
        var next = Owned();
        if (!next.Invited.Contains(joiner)) { throw new InvalidOperationException($"{joiner} was not invited to {Name}."); }
        next.Invited = [.. next.Invited.Where(invited => invited != joiner)];
        next.Members = [.. next.Members, joiner];
        return await Commit(next, [], tell: true);
    }

    public async Task Leave(string brainId)
    {
        if (IsCore) { throw new InvalidOperationException("A member leaves a circle brain through Remove."); }
        RequireBrain();
        if (brainId == Brain)
        {
            if (!Exists) { throw Missing(); }
            if (Address.IsOwnerCopy) { throw new InvalidOperationException($"The owner cannot leave {Name}."); }
            var owner = Snapshot.Owner.Length > 0 ? Snapshot.Owner : Snapshot.InvitedBy!;
            await GrainFactory.GetGrain<ICircle>(ICircle.Key(owner, Name)).Left();
            await Clear(new CircleChanged(Name, [], [], 0, []));
            await Touched("left", "");
            return;
        }
        var next = Owned();
        if (!next.Members.Contains(brainId) && !next.Invited.Contains(brainId))
        { throw new InvalidOperationException($"{brainId} is not in {Name}."); }
        await Drop(next, brainId, tellLeaver: true);
    }

    public async Task Left()
    {
        var leaver = CopyCaller();
        var next = Owned();
        if (next.Members.Contains(leaver) || next.Invited.Contains(leaver)) { await Drop(next, leaver, tellLeaver: false); }
    }

    public Task<CircleState?> StateForCaller()
    {
        var asker = CopyCaller();
        if (!Address.IsOwnerCopy || !Exists) { throw new InvalidOperationException($"{Brain} does not run {Name}."); }
        return Task.FromResult<CircleState?>(Snapshot.Members.Contains(asker) || Snapshot.Invited.Contains(asker) ? Describe(Snapshot) : null);
    }

    public Task Changed() => Follow();

    public async Task Share(string appName)
    {
        if (IsCore) { throw new InvalidOperationException("Sharing into a circle brain is not available yet."); }
        RequireBrain();
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        var next = Owned();
        if (next.SharedApps.Contains(appName)) { return; }
        next.SharedApps = [.. next.SharedApps, appName];
        await Commit(next, [], tell: true);
    }

    public async Task Unshare(string appName)
    {
        if (IsCore) { throw new InvalidOperationException("Sharing into a circle brain is not available yet."); }
        RequireBrain();
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        var next = Owned();
        if (!next.SharedApps.Contains(appName)) { return; }
        next.SharedApps = [.. next.SharedApps.Where(shared => shared != appName)];
        await Commit(next, [], tell: true);
    }

    public async Task SetComputeShare(int percent)
    {
        if (percent is < 0 or > ICircle.MaxComputeSharePercent)
        { throw new ArgumentOutOfRangeException(nameof(percent), percent, $"A circle may share 0 to {ICircle.MaxComputeSharePercent}% of its creator's compute."); }
        if (IsCore)
        {
            var core = MemberChange();
            if (Caller().AccountId != core.Owner) { throw new UnauthorizedAccessException($"Only {core.Name}'s creator, who pays, sets its compute share."); }
            core.ComputeSharePercent = percent;
            await CommitCore(core);
            return;
        }
        RequireBrain();
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
    private static string CopyCaller() => RequestContext.Get(CircleCallGuard.From) as string
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

    private async Task Drop(CircleRecord next, string brainId, bool tellLeaver)
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
        if (latest is null) { await Writing(async () => { await Clear(new CircleChanged(Name, [], [], 0, [])); await Touched("left", ""); }); }
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
            Name = state.Name, Owner = state.Creator, Members = state.Members, SharedApps = state.SharedApps,
            ComputeSharePercent = state.ComputeSharePercent, Invited = state.Invited, Version = state.Version,
        }, new CircleChanged(Name, state.Members, state.SharedApps, state.ComputeSharePercent, []));
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

    private static CircleChanged Changed(CircleRecord record)
        => new(record.Name, record.Members, record.SharedApps, record.ComputeSharePercent, [.. record.Machines.Select(machine => machine.MachineId)]);

    private static CircleState Describe(CircleRecord record) => new(record.Name, record.Owner, record.Members,
        [.. record.Machines.Select(machine => machine.MachineId)], record.Invited, record.SharedApps, record.ComputeSharePercent, record.Schedules, record.Version);
}
