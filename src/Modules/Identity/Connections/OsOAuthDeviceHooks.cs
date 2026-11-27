using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Runtime.Http.Connections.OAuth;

namespace IntoChat.OS.Modules.Identity.Connections;

// Thin IntoChat OS adapters wiring Identity's AuthorityNeurons and IBrainCapabilityPolicy
// into the framework-neutral DigitalBrain.Runtime.Http OAuthDeviceFlow.
internal sealed class OsOAuthDeviceGrantAcceptor(AuthorityNeurons neurons) : IOAuthDeviceGrantAcceptor
{
    public Task<ConnectionView> AcceptGrantAsync(string connectionKey, OAuthGrant grant, CancellationToken ct = default)
        => neurons.ConnectionInbound(connectionKey).AcceptGrant(grant);
}

internal sealed class OsOAuthDeviceAuthorizationGate(IBrainCapabilityPolicy? capabilityPolicy = null) : IOAuthDeviceAuthorizationGate
{
    public async ValueTask AuthorizeAsync(BrainId brainId, string connectorId, CancellationToken ct = default)
    {
        if (capabilityPolicy is null)
        {
            return;
        }

        var decision = await capabilityPolicy.EvaluateAsync(brainId.Value, BrainCapability.ExternalConnector, connectorId, ct).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            throw new ClaimRequiredException(decision);
        }
    }
}
