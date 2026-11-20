using DigitalBrain.Access;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Authority.Integrations;

internal static class IntegrationOperatorGate
{
    // Deployment registrations belong to the operator; a per-account Owner role is not that.
    // The operator is the Basic bootstrap credential when one is configured, the synthetic owner
    // in the Open posture, and nobody in a Secured deployment without a bootstrap credential.
    public static bool Allows(Caller? caller, AuthOptions auth)
    {
        ArgumentNullException.ThrowIfNull(auth);
        // A person at the authenticated edge themselves: not an agent, an app or a relay for them.
        if (caller is not { Actor: Actor.Person person, Direct: true })
        {
            return false;
        }

        if (!string.IsNullOrEmpty(auth.Username) && !string.IsNullOrEmpty(auth.Password))
        {
            return string.Equals(person.Principal, auth.Username, StringComparison.Ordinal);
        }

        return auth.Posture == IdentityPosture.Open
            && string.Equals(person.Principal, AccountSession.DefaultLogin, StringComparison.Ordinal);
    }
}
