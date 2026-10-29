using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps;

// A script reaction publishes AppRequested off the HTTP call, so the listener often has no stamp.
// The brain owner is the same person the open posture would have stamped.
internal static class HostCaller
{
    public static async Task<CapabilityCaller?> ForBrain(IServiceProvider services, string brain)
    {
        if (CallerContextStamper.TryGet(out var caller) && caller.BrainId == brain) { return caller; }
        if (services.GetService<ICapabilityBroker>() is not { } broker) { return null; }
        var directory = await broker.Identity();
        if (await directory.OwnerOfBrain(brain) is not { } account) { return null; }
        return new CapabilityCaller(account.OwnerPrincipalId, account.AccountId, brain);
    }
}
