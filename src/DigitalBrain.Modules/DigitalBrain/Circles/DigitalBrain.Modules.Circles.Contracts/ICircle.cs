using System.Text.RegularExpressions;
using Orleans.Metadata;

namespace DigitalBrain.Circles;

[GenerateSerializer, Alias("circles.state")]
public sealed record CircleState(
    [property: Id(0)] string Name,
    [property: Id(1)] string Owner,
    [property: Id(2)] string[] Members,
    [property: Id(3)] string[] SharedApps,
    [property: Id(4)] int ComputeSharePercent,
    [property: Id(5)] string[] Invited,
    [property: Id(6)] string? InvitedBy,
    [property: Id(7)] long Version);

// A circle as one brain holds it. The owner's copy `{owner}/circles/{name}` is the authority; every
// member (and invited brain) holds a copy `{brain}/circles/{name}@{owner}` that follows it by pulling
// the owner's state. A brain acts only on its own copies; the [Internal] methods are how the copies of
// one circle talk to each other, and each is accepted only from the copy the kernel says is calling.
[Alias("circles.circle"), DefaultGrainType("circles.circle")]
public interface ICircle : INeuron
{
    const int MaxComputeSharePercent = 25;

    Task Invite(string brainId);
    Task<CircleState> Accept();
    Task Leave(string brainId);
    Task<string[]> Members();
    Task Share(string appName);
    Task Unshare(string appName);
    // 0 to MaxComputeSharePercent of the owner's budget; the platform's own 25% fee is fixed and not a setting.
    Task SetComputeShare(int percent);
    Task<CircleState> State();

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
