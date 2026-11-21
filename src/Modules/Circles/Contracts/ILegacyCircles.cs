using DigitalBrain.Access;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Circles;

public sealed record CircleState(
    string Name,
    string Creator,
    string[] Members,
    string[] Invited,
    string[] SharedApps,
    int ComputeSharePercent,
    long Version);

public sealed record CircleLine(
    string Speaker,
    string Text,
    long Sequence);

public sealed record CircleEntry(string BrainId, string Name);

public sealed record CircleMachine(
    string MachineId,
    string MachineName,
    string MachineKeyThumbprint,
    string Role,
    string? AccountId,
    DateTimeOffset AcceptedAt);

// Read-only legacy circle contracts. Removed after two releases (see OWED.md).
[NeuronAlias("circles.circle"), DefaultNeuronType("circles.circle")]
public interface ILegacyCircle
{
    Task<CircleState> State();
}

[NeuronAlias("circles.chat"), DefaultNeuronType("circles.chat")]
public interface ILegacyCircleChat
{
    Task<CircleLine[]> Read(long since);
}

[NeuronAlias("circles.factory"), DefaultNeuronType("circles.factory")]
public interface ILegacyCircleIndex
{
    Task<CircleEntry[]> Circles();
}

[NeuronAlias("circles.invites"), DefaultNeuronType("circles.invites"), Internal]
public interface ILegacyCircleMachines
{
    Task<CircleMachine[]> Machines();
}
