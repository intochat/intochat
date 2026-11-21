using DigitalBrain.Access;

namespace IntoChat.OS.Modules.Circles;

public sealed record MintInvite(
    // Clamped to InviteCode.MinValidity..MaxValidity.
    TimeSpan ValidFor,
    // "member" or "admin".
    string Role = "member",
    // The minting account is bound to the machine that redeems; otherwise the machine waits for ClaimMachine.
    bool BindCaller = false);

// The code is returned once and never stored.
public sealed record MintedInvite(string Code, DateTimeOffset ExpiresAt);

public sealed record RedeemInvite(
    string CodeHash,
    string MachineId,
    string MachineKeyThumbprint,
    string MachineName);

public enum RedeemResult
{
    Accepted = 0,
    // The same machine and key redeemed this code within InviteRepeatWindow: the same outcome again.
    Repeat = 1,
    Expired = 2,
    // Unknown, used by another machine, revoked, or a repeat outside the window.
    Rejected = 3,
}

public sealed record RedeemOutcome(
    RedeemResult Result,
    string CircleId = "",
    string CircleName = "",
    string Role = "",
    string? AccountId = null,
    bool PendingAccount = false)
{
    public static RedeemOutcome Rejected { get; } = new(RedeemResult.Rejected);
    public static RedeemOutcome Expired { get; } = new(RedeemResult.Expired);
}

public sealed record CircleMachine(
    string MachineId,
    string MachineName,
    string MachineKeyThumbprint,
    string Role,
    string? AccountId,
    DateTimeOffset AcceptedAt);

// D-03/D-10: `{circle}/invites` in a circle brain. Owners and admins mint single-use, expiring codes; a machine redeems
// a code once (through ICircleInviteRedeemer), binding its machine id and key; an account binds at mint or by claim.
// A plain neuron (06-08).
[DigitalBrain.Contracts.NeuronAlias("circles.invites"), DigitalBrain.Contracts.DefaultNeuronType("circles.invites")]
public interface ICircleInvites
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

// One-release shim for the cloud enrollment endpoint while it migrates to IDeviceEnrollmentRedeemer.
[Obsolete("Use IDeviceEnrollmentRedeemer.")]
public interface ICircleInviteRedeemer
{
    Task<RedeemOutcome> Redeem(string code, string machineId, string machineKeyThumbprint, string machineName);
}
