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

// A circle as one brain holds it: the owner's copy is the authority, a member's copy follows it.
// The owner invites; the invited brain accepts; members leave. Everything marked [Internal] is how
// the copies in different brains talk to each other.
[Alias("circles.circle"), DefaultGrainType("circles.circle")]
public interface ICircle : INeuron
{
    const int MaxComputeSharePercent = 25;

    Task Invite(string brainId);
    Task<CircleState> Accept();
    Task Leave(string brainId);
    [Orleans.Concurrency.ReadOnly, Orleans.Concurrency.AlwaysInterleave] Task<string[]> Members();
    Task Share(string appName);
    Task Unshare(string appName);
    // 0 to MaxComputeSharePercent of the owner's budget; the platform's own 25% fee is fixed and not a setting.
    Task SetComputeShare(int percent);
    [Orleans.Concurrency.ReadOnly, Orleans.Concurrency.AlwaysInterleave] Task<CircleState> State();

    [Internal] Task Invited(string from);
    [Internal] Task<CircleState> Joined(string brainId);
    [Internal] Task Left(string brainId);
    [Internal] Task Apply(CircleState state);

    static string Key(string brainId, string name) => $"{brainId}/circles/{name}";
}
