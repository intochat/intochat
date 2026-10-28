using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;

namespace IntoChat.OS.Framework;

// identity@1 and its grants are single-connection capabilities: the brain argument is not a directory key.
public static class CapabilityIdentity
{
    private static readonly BrainId Scope = new("system");

    public static Task<IIdentity> Identity(this ICapabilityBroker broker, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(broker);
        return broker.Resolve<IIdentity>(Scope, cancellationToken);
    }

    public static Task<IGrants> Grants(this ICapabilityBroker broker, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(broker);
        return broker.Resolve<IGrants>(Scope, cancellationToken);
    }
}
