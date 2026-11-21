using IntoChat.OS.Modules.Devices;

namespace IntoChat.OS.Modules.Circles;

#pragma warning disable CS0618
internal sealed class CircleInviteRedeemer(IDeviceEnrollmentRedeemer devices) : ICircleInviteRedeemer
{
    public async Task<RedeemOutcome> Redeem(string code, string machineId, string machineKeyThumbprint, string machineName)
    {
        var outcome = await devices.Redeem(code, machineId, machineKeyThumbprint, machineName).ConfigureAwait(false);
        return new RedeemOutcome(
            (RedeemResult)(int)outcome.Result,
            CircleId: outcome.Brain,
            CircleName: outcome.DeviceName,
            Role: outcome.Result is EnrollmentResult.Accepted or EnrollmentResult.Repeat ? "owner" : "",
            AccountId: string.IsNullOrEmpty(outcome.Account) ? null : outcome.Account,
            PendingAccount: false);
    }
}
#pragma warning restore CS0618
