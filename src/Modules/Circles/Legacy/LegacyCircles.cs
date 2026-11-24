using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Neurons;
using DigitalBrain.State;
using IntoChat.OS.Host.Primitives;

namespace IntoChat.OS.Modules.Circles;

[ExportableState]
internal sealed class CircleRecord
{
    public string Name { get; set; } = "";
    public string Owner { get; set; } = "";
    public string[] Members { get; set; } = [];
    public string[] SharedApps { get; set; } = [];
    public int ComputeSharePercent { get; set; }
    public string[] Invited { get; set; } = [];
    public long Version { get; set; }
    public CircleBinding[] Bindings { get; set; } = [];
    public string[] Admins { get; set; } = [];
}

internal sealed record CircleBinding(string AccountId, string AppName, string Slot, string ConnectionKey);

[ExportableState]
internal sealed class CircleChatRecord
{
    public CircleLine[] Lines { get; set; } = [];
    public long Last { get; set; }
    public string[] Said { get; set; } = [];
    public string[] Published { get; set; } = [];
}

[ExportableState]
internal sealed class CircleFactoryRecord
{
    public CircleEntry[] Created { get; set; } = [];
}

internal sealed class CircleInvitesRecord
{
    public CircleCodeInvite[] Invites { get; set; } = [];
}

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

[Neuron(CircleGrainType.Name)]
internal sealed class LegacyCircleNeuron : Neuron<CircleRecord>, ILegacyCircle
{
    public new Task<CircleState> State()
    {
        var record = base.State;
        if (Context.Caller is not { For.Account: var account } caller
            || caller.Brain != Id.Brain
            || !record.Members.Contains(account, StringComparer.Ordinal))
        {
            throw new UnauthorizedAccessException($"The caller is not a member of circle {Id.Brain.Value}.");
        }

        return Task.FromResult(new CircleState(
            record.Name,
            record.Owner,
            [.. record.Members],
            [.. record.Invited],
            [.. record.SharedApps],
            record.ComputeSharePercent,
            record.Version));
    }
}

[Neuron("circles.chat")]
internal sealed partial class LegacyCircleChatNeuron : Neuron<CircleChatRecord>, ILegacyCircleChat
{
    [DigitalBrain.Slot(Key = "circle")]
    private partial ILegacyCircle Circle { get; }

    public async Task<CircleLine[]> Read(long since)
    {
        if (Context.Caller is not { For.Account: var account } caller || caller.Brain != Id.Brain)
        {
            throw new UnauthorizedAccessException($"The caller is not in circle {Id.Brain.Value}.");
        }

        var circle = await Circle.State();
        if (!circle.Members.Contains(account, StringComparer.Ordinal))
        {
            throw new UnauthorizedAccessException($"The caller is not a member of circle {Id.Brain.Value}.");
        }

        return [.. State.Lines.Where(line => line.Sequence > since)];
    }
}

[Neuron("circles.factory")]
internal sealed class LegacyCircleIndexNeuron(ICapabilityBroker? broker = null) : Neuron<CircleFactoryRecord>, ILegacyCircleIndex
{
    public async Task<CircleEntry[]> Circles()
    {
        var caller = RequireSameBrain(() => new UnauthorizedAccessException("Only the personal brain's owner lists its legacy circles."));

        var principal = caller.For?.Principal ?? (caller.Actor as Actor.Person)?.Principal ?? "";
        if (broker is null || string.IsNullOrEmpty(principal))
        {
            return [.. State.Created];
        }

        var brains = await (await broker.Identity()).BrainsOf(principal);
        return
        [
            .. brains
                .Where(entry => entry.Kind == BrainKind.Circle)
                .Select(entry => State.Created.FirstOrDefault(created => created.BrainId == entry.Id) ?? new CircleEntry(entry.Id, entry.Id)),
        ];
    }
}

[Neuron("circles.invites")]
internal sealed class LegacyCircleMachinesNeuron : Neuron<CircleInvitesRecord>, ILegacyCircleMachines
{
    public Task<CircleMachine[]> Machines()
        => Task.FromResult(State.Invites
            .Where(known => known is { AcceptedAt: not null, Revoked: false, MachineId.Length: > 0, MachineKeyThumbprint.Length: > 0 })
            .Select(invite => new CircleMachine(
                invite.MachineId!,
                invite.MachineName,
                invite.MachineKeyThumbprint!,
                invite.Role,
                invite.AccountId,
                invite.AcceptedAt!.Value))
            .ToArray());
}
