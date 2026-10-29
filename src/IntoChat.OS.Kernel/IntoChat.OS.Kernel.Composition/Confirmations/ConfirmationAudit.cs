using DigitalBrain.Confirmation;

namespace IntoChat.OS.Kernel.Composition.Confirmations;

/// <summary>
/// The append-only confirmation audit (T-07-26). One <see cref="ConfirmationResolved"/> per outcome, journaled with the
/// state change. The record names the target by <see cref="ConfirmationPlan.TargetLabel"/> only.
/// </summary>
internal static class ConfirmationAudit
{
    public static ConfirmationResolved Record(ConfirmationRequested request, ConfirmationResult result, ConfirmationPrincipal? approver, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        return new(request.PlanId, request.Plan.Action, request.Plan.TargetLabel, request.Requester, approver, request.Policy,
            result.Outcome, result.Reason, result.Skipped, at);
    }
}
