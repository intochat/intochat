using DigitalBrain.Circles.Signals;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Compute;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Identity;

namespace DigitalBrain.Circles;

[GenerateSerializer, Alias("circles.circle-record")]
internal sealed class CircleRecord
{
    [Id(0)] public string Name { get; set; } = "";
    // The creator's account.
    [Id(1)] public string Owner { get; set; } = "";
    [Id(2)] public string[] Members { get; set; } = [];
    [Id(3)] public string[] SharedApps { get; set; } = [];
    [Id(4)] public int ComputeSharePercent { get; set; }
    [Id(5)] public string[] Invited { get; set; } = [];
    [Id(7)] public long Version { get; set; }
    // The circle core's contributed machines, each with the member account that contributed it.
    [Id(8)] public CircleMachine[] Machines { get; set; } = [];
    [Id(9)] public CircleSchedule[] Schedules { get; set; } = [];

    public CircleRecord Clone() => (CircleRecord)MemberwiseClone();
}

[GenerateSerializer, Alias("circles.circle-machine")]
internal sealed record CircleMachine([property: Id(0)] string MachineId, [property: Id(1)] string AccountId);

// The circle core `{circle}/circle` (T26-1): the circle brain's own state. Members are accounts the trust side records
// as memberships (R129), its machines are contributed (R128), and every part key of the circle brain is wrapped to
// those machines (T26-5). Not reentrant: every change is committed before anyone hears of it.
[GrainType(CircleGrainType.Name)]
internal sealed class CircleNeuron(
    [PersistentState("circles.circle", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<CircleRecord> store,
    IIdentityClient identity,
    ICirclePartKeys keys,
    DigitalBrain.Apps.FeatureLoader? loader = null)
    : Neuron<CircleRecord>(store), ICircle
{
    private const string CoreSuffix = "/circle";

    private string CircleBrain => this.GetPrimaryKeyString()[..^CoreSuffix.Length];

    public Task<CircleState> State() => Task.FromResult(Describe(Core()));

    public async Task Found(string name, string machineId)
    {
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
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        RequirePerson();
        var next = MemberChange();
        if (next.Members.Contains(accountId) || next.Invited.Contains(accountId))
        { throw new InvalidOperationException($"{accountId} is already in {next.Name} or invited to it."); }
        next.Invited = [.. next.Invited, accountId];
        await CommitCore(next);
    }

    // R129: the invitation is the authority for a membership; the caller's machine joins the circle with it.
    public async Task<CircleState> Accept()
    {
        RequirePerson();
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
        {
            await identity.WithdrawMachine(CircleBrain, machine.MachineId);
            await keys.RevokeLeases(CircleBrain, machine.MachineId);
        }
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

    // R126: inviting and accepting are a person's, on the /brains/{circle} route; an app's script is refused them.
    private static void RequirePerson()
    {
        var caller = Caller();
        if (caller.Kind == CallerKind.App || caller.StampedBy == TrustedEdge.AppProxy)
        { throw new UnauthorizedAccessException("Inviting to a circle and accepting an invitation are a person's own; a script may not."); }
    }

    private static CallerContext Caller()
        => CallerContextStamper.TryGet(out var caller) ? caller : throw new UnauthorizedAccessException("A circle acts only on a stamped caller.");

    public async Task Share(string appName, string[] connectionIds) => await ShareIntoCircle(appName, connectionIds ?? []);

    private async Task ShareIntoCircle(string appName, string[] connectionIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        var next = MemberChange();
        var caller = Caller();
        if (loader is null) { throw new InvalidOperationException($"Sharing into {next.Name} needs the Apps module."); }
        var source = await SharerBrain(caller, appName);
        // The sharer acts in its own brain: the same principal and account, stamped for that brain (R124, R148).
        var app = GrainFactory.GetGrain<DigitalBrain.Apps.IApp>(DigitalBrain.Apps.IApp.Key(source, appName));
        var shared = await AsSharer(caller, source, app.Read);
        var bundle = await AsSharer(caller, source, app.Export);
        await loader.Load(CircleBrain, appName, bundle,
            new DigitalBrain.Apps.AppOrigin(caller.AccountId, new DigitalBrain.Apps.AppSource.Brain(caller.AccountId, source), appName, bundle.Version), CancellationToken.None);
        var circleApp = DigitalBrain.Apps.IApp.Key(CircleBrain, appName);
        var slots = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var slot in shared.Manifest.Slots)
        {
            if (!shared.Connections.TryGetValue(slot.Name, out var id) || id.Length == 0) { continue; }
            var owned = ConnectionKeys.For(source, slot.Connector, id);
            if (connectionIds.Contains(id, StringComparer.Ordinal))
            {
                // R147: the circle's own copy holds the definition; Task 28 seals its credential under the circle key.
                var view = await AsSharer(caller, source, () => GrainFactory.GetConnection(owned).Read());
                var copy = ConnectionKeys.For(CircleBrain, slot.Connector, id);
                await GrainFactory.GetConnection(copy).Adopt(view.AuthMethodId, view.Account);
                slots[slot.Name] = copy;
            }
            else
            {
                await AsSharer(caller, source, async () => { await GrainFactory.GetConnection(owned).ShareInto(CircleBrain, circleApp); return true; });
                slots[slot.Name] = owned;
            }
        }
        if (slots.Count > 0) { await GrainFactory.GetGrain<DigitalBrain.Apps.IApp>(circleApp).Configure([], slots); }
        if (!next.SharedApps.Contains(appName)) { next.SharedApps = [.. next.SharedApps, appName]; }
        await CommitCore(next);
    }

    // R148: the sharer's personal brain that has the app; none or several is refused.
    private async Task<string> SharerBrain(CallerContext caller, string appName)
    {
        List<string> holding = [];
        foreach (var brain in (await identity.BrainsOf(caller.PrincipalId)).Where(entry => entry.Kind == BrainKind.Personal))
        {
            var installed = await AsSharer(caller, brain.Id, async () =>
                (await GrainFactory.GetGrain<DigitalBrain.Apps.IApp>(DigitalBrain.Apps.IApp.Key(brain.Id, appName)).Read()).Manifest.Title.Length > 0);
            if (installed) { holding.Add(brain.Id); }
        }
        return holding.Count switch
        {
            1 => holding[0],
            0 => throw new InvalidOperationException($"{caller.AccountId} has no app named {appName} to share."),
            _ => throw new InvalidOperationException($"{caller.AccountId} has {appName} in several brains ({string.Join(", ", holding)}); share it from one."),
        };
    }

    private static async Task<T> AsSharer<T>(CallerContext caller, string brainId, Func<Task<T>> call)
    {
        CallerContextStamper.Stamp(caller with { BrainId = brainId });
        try { return await call(); }
        finally { CallerContextStamper.Stamp(caller); }
    }

    public async Task SetComputeShare(int percent)
    {
        if (percent is < 0 or > ICircle.MaxComputeSharePercent)
        { throw new ArgumentOutOfRangeException(nameof(percent), percent, $"A circle may share 0 to {ICircle.MaxComputeSharePercent}% of its creator's compute."); }
        var core = MemberChange();
        if (Caller().AccountId != core.Owner) { throw new UnauthorizedAccessException($"Only {core.Name}'s creator, who pays, sets its compute share."); }
        core.ComputeSharePercent = percent;
        await CommitCore(core);
    }

    private static CircleChanged Changed(CircleRecord record)
        => new(record.Name, record.Members, record.SharedApps, record.ComputeSharePercent, [.. record.Machines.Select(machine => machine.MachineId)]);

    private static CircleState Describe(CircleRecord record) => new(record.Name, record.Owner, record.Members,
        [.. record.Machines.Select(machine => machine.MachineId)], record.Invited, record.SharedApps, record.ComputeSharePercent, record.Schedules, record.Version);
}
