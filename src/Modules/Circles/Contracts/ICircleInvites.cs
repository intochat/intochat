namespace IntoChat.OS.Modules.Circles;

public enum RedeemResult
{
    Accepted = 0,
    Repeat = 1,
    Expired = 2,
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

// One-release shim for the cloud enrollment endpoint while it migrates to IDeviceEnrollmentRedeemer.
[Obsolete("Use IDeviceEnrollmentRedeemer.")]
public interface ICircleInviteRedeemer
{
    Task<RedeemOutcome> Redeem(string code, string machineId, string machineKeyThumbprint, string machineName);
}
