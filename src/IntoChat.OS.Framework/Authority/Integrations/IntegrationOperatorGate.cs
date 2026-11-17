using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Authority.Integrations;

internal static class IntegrationOperatorGate
{
    // Deployment registrations belong to the operator; a per-account Owner role is not that.
    // The operator is the Basic bootstrap credential when one is configured, the synthetic owner
    // in the Open posture, and nobody in a Secured deployment without a bootstrap credential.
    public static bool Allows(CapabilityCaller? caller, AuthOptions auth)
    {
        ArgumentNullException.ThrowIfNull(auth);
        if (caller is null || caller.Kind != CallerKind.User || caller.StampedBy != TrustedEdge.AuthenticatedHttp)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(auth.Username) && !string.IsNullOrEmpty(auth.Password))
        {
            return string.Equals(caller.PrincipalId, auth.Username, StringComparison.Ordinal);
        }

        return auth.Posture == IdentityPosture.Open
            && string.Equals(caller.PrincipalId, AccountSession.DefaultLogin, StringComparison.Ordinal);
    }
}
