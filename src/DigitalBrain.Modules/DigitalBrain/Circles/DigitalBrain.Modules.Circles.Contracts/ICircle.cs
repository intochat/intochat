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
// as trust-side memberships of the circle brain; contributed machines are the circle's own machines (R128).
//
// Until the circle app split (Task 27 Step 6) the same grain type also serves the older per-brain copies
// `{brain}/circles/{name}` and their [Internal] copy protocol; those keys answer as before.
// The alias and grain type literals must equal DigitalBrain.Framework.CircleGrainType.Name (a Circles fact asserts it).
[Alias("circles.circle"), DefaultGrainType("circles.circle")]
public interface ICircle : INeuron
{
    const int MaxComputeSharePercent = 25;

    // The circle core: an account id. A copy: a brain id.
    Task Invite(string accountId);
    // The caller's account must be invited; it becomes a member and contributes the caller's machine (R129).
    [Public] Task<CircleState> Accept();
    // The caller's account must be bound to the machine.
    Task Contribute(string machineId);
    // RemoveMember, drop its machines; the creator cannot be removed, and the creator leaving deletes the circle (R129).
    Task Remove(string accountId);
    Task Leave(string brainId);
    Task<string[]> Members();
    Task Share(string appName);
    Task Unshare(string appName);
    // 0 to MaxComputeSharePercent of the creator's budget; the platform's own 25% fee is fixed and not a setting.
    Task SetComputeShare(int percent);
    Task<CircleState> State();

    // The circle brain's birth (Created) names the circle and its first machine.
    [Internal] Task Found(string name, string machineId);

    // Owner copy -> invited brain's copy. Interleaves so a former member still pulling from the owner never blocks a new invitation.
    [Internal, Orleans.Concurrency.AlwaysInterleave] Task Invited();
    // Invited brain's copy -> owner copy; the joining brain is the kernel-stamped caller.
    [Internal] Task<CircleState> Joined();
    [Internal] Task Left();
    // A member's copy asks for the owner's state; null once it is no longer a member.
    [Internal] Task<CircleState?> StateForCaller();
    // The owner's copy tells a member's copy to pull; losing the call costs nothing, the next pull heals.
    [Internal, Orleans.Concurrency.OneWay] Task Changed();
    // This copy's chat asks who is in the circle, as this copy last knew it.
    [Internal] Task<CircleState> Roster();

    static bool IsValidName(string? name) => name is not null && NamePattern.IsMatch(name);

    // The circle brain's core neuron.
    static string Key(string circleBrainId) => $"{circleBrainId}/circle";

    // The owner's copy.
    static string Key(string brainId, string name) => Key(brainId, name, brainId);

    // A copy held by `brainId` of the circle `owner` runs; the owner's own copy has no suffix.
    static string Key(string brainId, string name, string owner)
    {
        if (!IsValidName(name)) { throw new ArgumentException("A circle name is 1 to 40 of a-z, 0-9 and '-'.", nameof(name)); }
        return brainId == owner ? $"{brainId}/circles/{name}" : $"{brainId}/circles/{name}@{owner}";
    }

    private static readonly Regex NamePattern = new("^[a-z0-9-]{1,40}$", RegexOptions.CultureInvariant);
}
