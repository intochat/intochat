using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps;

// A script reaction publishes AppRequested off the HTTP call, so the listener often has no stamp.
// The brain owner is the same person the open posture would have stamped. Until the directory names one (the owner is
// provisioned a lifecycle stage after the module hosts start), a host acts as the platform of its brain: a user stamp
// made of the brain id is nobody's account, and the authority denies it as outside the brain.
internal static class HostCaller
{
    public static async Task<CapabilityCaller?> ForBrain(IServiceProvider services, string brain)
    {
        if (CallerContextStamper.TryGet(out var caller) && caller.BrainId == brain) { return caller; }
        if (string.IsNullOrWhiteSpace(brain)) { return null; }
        var fallback = CapabilityCallers.Platform(brain, brain, brain);
        CallerContextStamper.Stamp(fallback);
        if (services.GetService<ICapabilityBroker>() is { } broker)
        {
            var directory = await broker.Identity();
            if (await directory.OwnerOfBrain(brain) is { } account)
            {
                var ownerCaller = CapabilityCallers.Of(account.OwnerPrincipalId, account.AccountId, brain);
                CallerContextStamper.Stamp(ownerCaller);
                return ownerCaller;
            }
        }
        return fallback;
    }
}
