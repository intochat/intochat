using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Trust;

namespace DigitalBrain.Platform.Identity;

// R57: the trust side records who owns a brain; the brain neuron is born here, on the machine, and the machine
// binds itself to the brain's account first (R55) so it can act for it at the trust side.
internal sealed class BrainBirths(IGrainFactory grains, MachineRegistrar registrar)
{
    public async Task<Member> Born(BrainProvision provision)
    {
        ArgumentNullException.ThrowIfNull(provision);
        var owner = provision.Member;
        if (!provision.NewBrain) { return owner; }
        await registrar.Bind(owner.AccountId);
        await grains.GetGrain<IBrain>(BrainScope.Create(owner.AccountId, owner.BrainId).Id).Create(new Owner(owner.AccountId));
        return owner;
    }
}
