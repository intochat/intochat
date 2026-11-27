using DigitalBrain;
using IntoChat.OS.Modules.Apps.Flows;

namespace IntoChat.OS.Modules.Devices.Link;

/// <summary>
/// Cross-module bridge exposing <see cref="IConnectedComputeRouter.ResolveEngineeringRouteAsync"/>
/// to <c>IntoChat.Aspire</c>'s <c>ConnectedDeviceCandidateHost</c> without introducing a project reference
/// from <c>IntoChat.Aspire</c> to <c>IntoChat.OS.Modules.AI</c>.
/// </summary>
internal sealed class ConnectedCandidateRelayBridge(IConnectedComputeRouter router) : IConnectedCandidateRelay
{
    public async Task<ConnectedCandidateTarget?> TrySelectEligibleDeviceAsync(
        BrainId brainId,
        CancellationToken ct = default)
    {
        var decision = await router.ResolveEngineeringRouteAsync(brainId, ct).ConfigureAwait(false);
        if (decision.Target != ComputeExecutionTarget.ConnectedClient
            || decision.Node is null
            || !decision.Node.IsEligibleForConnectedEngineering)
        {
            return null;
        }

        return new ConnectedCandidateTarget(
            decision.Node.MachineId,
            decision.Node.DeviceName,
            decision.Reason);
    }
}
