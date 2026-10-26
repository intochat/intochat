using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Circles;

internal sealed class CircleInvitesRecord
{
    public CircleCodeInvite[] Invites { get; set; } = [];
}

// The circle side of the invitation record (D-10): the fields Authority's Invitation gained, held by the circles
// module, which does not reference Authority. CodeHash only; the code is shown once at mint and never stored.
internal sealed record CircleCodeInvite
{
    public required string Id { get; init; }
    public required string CodeHash { get; init; }
    public required string Role { get; init; }
    public required string IssuedBy { get; init; }
    public string? AccountId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public string? MachineId { get; init; }
    public string? MachineKeyThumbprint { get; init; }
    public string MachineName { get; init; } = "";
    public DateTimeOffset? AcceptedAt { get; init; }
    public bool Revoked { get; init; }
    public string CircleName { get; init; } = "";
}

// A plain neuron (06-08) at "{circle}/invites". Its record saves without a signal: invites are no one's to watch.
[DigitalBrain.Neuron("circles.invites")]
internal sealed class CircleInvitesNeuron : DigitalBrain.Neuron, ICircleInvites
{
    // Facts replace the clock under this key; the deployment uses the system clock.
    public const string ClockKey = "circles.invites";

    private CircleInvitesRecord Snapshot = new();

    private string CircleBrain => Id.Brain.Value;

    private DateTimeOffset Now => (Context.Services.GetKeyedService<TimeProvider>(ClockKey) ?? TimeProvider.System).GetUtcNow();

    private ICircle Circle => Get<ICircle>(new DigitalBrain.NeuronId(Id.Brain, CircleGrainType.Name, "circle"));

    // The invite key and the code index live in the system brain: module plumbing reached through the host seam, which a
    // neuron's own Get (same brain only) does not cross.
    private IntoChat.OS.Runtime.IPlainNeurons Plain => Context.Services.GetRequiredService<IntoChat.OS.Runtime.IPlainNeurons>();

    protected override async Task OnActivate(CancellationToken cancellationToken)
        => Snapshot = await Context.State.Load<CircleInvitesRecord>() ?? new();

    private async Task SaveQuietly(CircleInvitesRecord next)
    {
        await Context.State.Save(next, []);
        Snapshot = next;
    }

    public async Task<MintedInvite> Mint(MintInvite request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Role is not ("member" or "admin")) { throw new ArgumentException("An invite role is member or admin.", nameof(request)); }
        var caller = await Steward();
        var now = Now;
        var expiresAt = now + InviteCode.Clamp(request.ValidFor);
        var code = InviteCode.Generate();
        var hash = InviteCode.Hash(code, await Plain.Get<IInviteKey>(IInviteKey.Key).Get());
        var invite = new CircleCodeInvite
        {
            Id = Guid.NewGuid().ToString("N"),
            CodeHash = hash,
            Role = request.Role,
            IssuedBy = caller.AccountId,
            AccountId = request.BindCaller ? caller.AccountId : null,
            CreatedAt = now,
            ExpiresAt = expiresAt,
            CircleName = await Circle.Name(),
        };
        // Unredeemed invites past their expiry carry nothing worth keeping.
        var kept = Snapshot.Invites.Where(known => known.AcceptedAt is not null || known.ExpiresAt > now);
        await SaveQuietly(new CircleInvitesRecord { Invites = [.. kept, invite] });
        await Plain.Get<IInviteCode>(IInviteCode.Key(hash)).Bind(CircleBrain, invite.Id, expiresAt);
        return new MintedInvite(code, expiresAt);
    }

    // One turn: the check and the binding cannot interleave with another redeem of the same invite.
    public async Task<RedeemOutcome> Redeem(string inviteId, RedeemInvite request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var invite = Snapshot.Invites.FirstOrDefault(known => known.Id == inviteId);
        if (invite is null || invite.Revoked || !InviteCode.HashesEqual(invite.CodeHash, request.CodeHash)) { return RedeemOutcome.Rejected; }
        var now = Now;
        if (invite.AcceptedAt is { } acceptedAt)
        {
            return invite.MachineId == request.MachineId && invite.MachineKeyThumbprint == request.MachineKeyThumbprint
                   && now - acceptedAt <= ICircleInvites.RepeatWindow
                ? Outcome(invite, RedeemResult.Repeat)
                : RedeemOutcome.Rejected;
        }
        if (now >= invite.ExpiresAt) { return RedeemOutcome.Expired; }
        if (string.IsNullOrWhiteSpace(request.MachineId) || string.IsNullOrWhiteSpace(request.MachineKeyThumbprint)
            || Snapshot.Invites.Any(known => known.MachineId == request.MachineId && !known.Revoked))
        { return RedeemOutcome.Rejected; }
        var accepted = invite with
        {
            MachineId = request.MachineId,
            MachineKeyThumbprint = request.MachineKeyThumbprint,
            MachineName = request.MachineName ?? "",
            AcceptedAt = now,
        };
        await SaveQuietly(new CircleInvitesRecord { Invites = [.. Snapshot.Invites.Select(known => known.Id == inviteId ? accepted : known)] });
        return Outcome(accepted, RedeemResult.Accepted);
    }

    public async Task<CircleMachine> ClaimMachine(string machineId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        var caller = await MemberCaller();
        var invite = Machine(machineId);
        if (invite.AccountId == caller.AccountId) { return Describe(invite); }
        if (invite.AccountId is not null) { throw new InvalidOperationException("This machine is already bound to an account."); }
        var claimed = invite with { AccountId = caller.AccountId };
        await SaveQuietly(new CircleInvitesRecord { Invites = [.. Snapshot.Invites.Select(known => known.Id == invite.Id ? claimed : known)] });
        return Describe(claimed);
    }

    public async Task RevokeMachine(string machineId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        await Steward();
        var invite = Machine(machineId);
        await SaveQuietly(new CircleInvitesRecord
        { Invites = [.. Snapshot.Invites.Select(known => known.Id == invite.Id ? known with { Revoked = true } : known)] });
    }

    public async Task<CircleMachine[]> ListMachines()
    {
        await MemberCaller();
        return [.. Snapshot.Invites.Where(known => known is { AcceptedAt: not null, Revoked: false }).Select(Describe)];
    }

    private CircleCodeInvite Machine(string machineId)
        => Snapshot.Invites.FirstOrDefault(known => known.MachineId == machineId && known is { AcceptedAt: not null, Revoked: false })
           ?? throw new KeyNotFoundException("No machine with that id is in this circle.");

    private RedeemOutcome Outcome(CircleCodeInvite invite, RedeemResult result)
        => new(result, CircleBrain, invite.CircleName, invite.Role, invite.AccountId, invite.AccountId is null);

    private static CircleMachine Describe(CircleCodeInvite invite)
        => new(invite.MachineId!, invite.MachineName, invite.MachineKeyThumbprint!, invite.Role, invite.AccountId, invite.AcceptedAt!.Value);

    // D-10: owners and admins mint and revoke; a person, never a script (R126).
    private async Task<CallerContext> Steward()
    {
        var caller = await MemberCaller();
        if (await Circle.RoleOf(caller.AccountId) is not ("owner" or "admin"))
        { throw new UnauthorizedAccessException("Only a circle owner or an admin manages its invite codes."); }
        return caller;
    }

    private async Task<CallerContext> MemberCaller()
    {
        if (!CallerContextStamper.TryGet(out var caller)) { throw new UnauthorizedAccessException("Circle invites act only on a stamped caller."); }
        if (caller.Kind == CallerKind.App || caller.StampedBy == TrustedEdge.AppProxy)
        { throw new UnauthorizedAccessException("Circle invites are a person's own; a script may not."); }
        if (caller.BrainId != CircleBrain || await Circle.RoleOf(caller.AccountId) is "")
        { throw new UnauthorizedAccessException("The caller is not a member of this circle."); }
        return caller;
    }
}
