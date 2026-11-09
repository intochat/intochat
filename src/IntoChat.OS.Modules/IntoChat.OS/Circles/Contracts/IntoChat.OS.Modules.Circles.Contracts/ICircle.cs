using System.Text.RegularExpressions;
using DigitalBrain.Access;

namespace IntoChat.OS.Modules.Circles;

public sealed record CircleState(
    string Name,
    // The account that created the circle and pays its compute.
    string Creator,
    // Account ids.
    string[] Members,
    // Account ids.
    string[] Invited,
    string[] SharedApps,
    int ComputeSharePercent,
    long Version);

// T26-1: a circle is a brain of kind Circle; this is its core neuron `{circle}/circle`. Members are accounts, recorded
// as memberships of the circle brain. Invite
// and Accept are a person's, on the /brains/{circle} route; a script is refused them (R126).
// The alias and neuron type literals must equal IntoChat.OS.Connections.CircleGrainType.Name (a Circles fact asserts it).
// A plain neuron (06-08).
[DigitalBrain.Contracts.NeuronAlias("circles.circle"), DigitalBrain.Contracts.DefaultNeuronType("circles.circle")]
public interface ICircle
{
    const int MaxComputeSharePercent = 25;

    Task Invite(string accountId);
    // The caller's account must be invited; it becomes a member.
    [Public] Task<CircleState> Accept();
    // Revokes membership and shared connections. The creator leaving deletes the circle.
    Task Remove(string accountId);
    // R124/R132: shares the caller's app into the circle brain by reference grant.
    // Connections named in `connectionIds` are shared by reference Grant on the owner's brain;
    // zero credentials or assemblies leave the owner's brain.
    Task Share(string appName, string[] connectionIds);
    [DigitalBrain.CallTimeout(600_000)] Task<IntoChat.OS.Modules.Apps.AppSnapshot> BringPublication(IntoChat.OS.Modules.Apps.AppPublicationRef publication, string targetName, Guid operationId);
    // 0 to MaxComputeSharePercent of the creator's budget; the platform's own 25% fee is fixed and not a setting.
    Task SetComputeShare(int percent);
    Task<CircleState> State();

    // D-10: the creator makes a member an admin (or not); owners and admins mint invite codes.
    Task SetAdmin(string accountId, bool admin);
    // "owner", "admin", "member", or "" for an account outside the circle.
    [Internal] Task<string> RoleOf(string accountId);
    [Internal] Task<string> Name();

    // The circle brain's birth names the circle.
    [Internal] Task Found(string name);

    static bool IsValidName(string? name) => name is not null && NamePattern.IsMatch(name);

    static string Key(string circleBrainId) => $"{circleBrainId}/circle";

    private static readonly Regex NamePattern = new("^[a-z0-9-]{1,40}$", RegexOptions.CultureInvariant);
}
