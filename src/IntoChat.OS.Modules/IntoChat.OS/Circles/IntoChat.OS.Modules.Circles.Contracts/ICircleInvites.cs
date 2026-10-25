using Orleans.Metadata;
using DigitalBrain.Access;

namespace IntoChat.OS.Modules.Circles;

[GenerateSerializer, Alias("circles.mint-invite")]
public sealed record MintInvite(
    // Clamped to InviteCode.MinValidity..MaxValidity.
    [property: Id(0)] TimeSpan ValidFor,
    // "member" or "admin".
    [property: Id(1)] string Role = "member",
    // The minting account is bound to the machine that redeems; otherwise the machine waits for ClaimMachine.
    [property: Id(2)] bool BindCaller = false);

// The code is returned once and never stored.
[GenerateSerializer, Alias("circles.minted-invite")]
public sealed record MintedInvite([property: Id(0)] string Code, [property: Id(1)] DateTimeOffset ExpiresAt);

[GenerateSerializer, Alias("circles.redeem-invite")]
public sealed record RedeemInvite(
    [property: Id(0)] string CodeHash,
    [property: Id(1)] string MachineId,
    [property: Id(2)] string MachineKeyThumbprint,
    [property: Id(3)] string MachineName);

[GenerateSerializer, Alias("circles.redeem-result")]
public enum RedeemResult
{
    Accepted = 0,
    // The same machine and key redeemed this code within InviteRepeatWindow: the same outcome again.
    Repeat = 1,
    Expired = 2,
    // Unknown, used by another machine, revoked, or a repeat outside the window.
    Rejected = 3,
}

[GenerateSerializer, Alias("circles.redeem-outcome")]
public sealed record RedeemOutcome(
    [property: Id(0)] RedeemResult Result,
    [property: Id(1)] string CircleId = "",
    [property: Id(2)] string CircleName = "",
    [property: Id(3)] string Role = "",
    [property: Id(4)] string? AccountId = null,
    [property: Id(5)] bool PendingAccount = false)
{
    public static RedeemOutcome Rejected { get; } = new(RedeemResult.Rejected);
    public static RedeemOutcome Expired { get; } = new(RedeemResult.Expired);
}

[GenerateSerializer, Alias("circles.circle-machine")]
public sealed record CircleMachine(
    [property: Id(0)] string MachineId,
    [property: Id(1)] string MachineName,
    [property: Id(2)] string MachineKeyThumbprint,
    [property: Id(3)] string Role,
    [property: Id(4)] string? AccountId,
    [property: Id(5)] DateTimeOffset AcceptedAt);

// D-03/D-10: `{circle}/invites` in a circle brain. Owners and admins mint single-use, expiring codes; a machine redeems
// a code once (through ICircleInviteRedeemer), binding its machine id and key; an account binds at mint or by claim.
[Alias("circles.invites"), DefaultGrainType("circles.invites")]
public interface ICircleInvites : INeuron
{
    static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(5);

    Task<MintedInvite> Mint(MintInvite request);
    // The signed-in caller, a member of the circle, binds itself to a machine that redeemed without an account.
    Task<CircleMachine> ClaimMachine(string machineId);
    Task RevokeMachine(string machineId);
    Task<CircleMachine[]> ListMachines();

    // Reached from the code index only (InviteCodeNeuron), in one turn of this neuron.
    [Internal] Task<RedeemOutcome> Redeem(string inviteId, RedeemInvite request);

    static string Key(string circleBrainId) => $"{circleBrainId}/invites";
}

// In-process redeem for the cloud enrollment module (10-05): normalizes and hashes the presented code with the
// deployment's invite key and redeems it atomically in the owning circle brain.
public interface ICircleInviteRedeemer
{
    Task<RedeemOutcome> Redeem(string code, string machineId, string machineKeyThumbprint, string machineName);
}
