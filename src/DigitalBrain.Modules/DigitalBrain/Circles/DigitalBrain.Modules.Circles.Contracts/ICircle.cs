using System.Text.RegularExpressions;
using Orleans.Metadata;

namespace DigitalBrain.Circles;

[GenerateSerializer, Alias("circles.schedule")]
public sealed record CircleSchedule(
    [property: Id(0)] string MachineId,
    [property: Id(1)] string Cron);

[GenerateSerializer, Alias("circles.state")]
public sealed record CircleState(
    [property: Id(0)] string Name,
    // The account that created the circle and pays its compute.
    [property: Id(1)] string Creator,
    // Account ids.
    [property: Id(2)] string[] Members,
    // Contributed machine ids.
    [property: Id(3)] string[] Machines,
    // Account ids.
    [property: Id(4)] string[] Invited,
    [property: Id(5)] string[] SharedApps,
    [property: Id(6)] int ComputeSharePercent,
    [property: Id(7)] CircleSchedule[] Schedules,
    [property: Id(8)] long Version);

// T26-1: a circle is a brain of kind Circle; this is its core neuron `{circle}/circle`. Members are accounts, recorded
// as trust-side memberships of the circle brain; contributed machines are the circle's own machines (R128). Invite
// and Accept are a person's, on the /brains/{circle} route; a script is refused them (R126).
// The alias and grain type literals must equal DigitalBrain.Framework.CircleGrainType.Name (a Circles fact asserts it).
[Alias("circles.circle"), DefaultGrainType("circles.circle")]
public interface ICircle : INeuron
{
    const int MaxComputeSharePercent = 25;

    Task Invite(string accountId);
    // The caller's account must be invited; it becomes a member and contributes the caller's machine (R129).
    [Public] Task<CircleState> Accept();
    // The caller's account must be bound to the machine.
    Task Contribute(string machineId);
    // RemoveMember, drop its machines, rotate every circle part key; the creator cannot be removed, and the creator
    // leaving deletes the circle (R129).
    Task Remove(string accountId);
    // R124/R132: installs the caller's own app into the circle brain (compile-gated Export, Origin = the sharer's app). The
    // connections named in `connectionIds` are bound as the circle's own copies; every other bound slot stays the
    // sharer's connection, reached only through the binding recorded on it (R134).
    Task Share(string appName, string[] connectionIds);
    // 0 to MaxComputeSharePercent of the creator's budget; the platform's own 25% fee is fixed and not a setting.
    Task SetComputeShare(int percent);
    Task<CircleState> State();

    // The circle brain's birth (Created) names the circle and its first machine.
    [Internal] Task Found(string name, string machineId);

    static bool IsValidName(string? name) => name is not null && NamePattern.IsMatch(name);

    static string Key(string circleBrainId) => $"{circleBrainId}/circle";

    private static readonly Regex NamePattern = new("^[a-z0-9-]{1,40}$", RegexOptions.CultureInvariant);
}
