using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Authority.Identity;

// R57: the trust side records who owns a brain; the brain neuron is born here, on the machine, and the machine
// binds itself to the brain's account first (R55) so it can act for it at the trust side.
internal sealed class BrainBirths(AuthorityNeurons neurons, EdgeAuthority edge) : IBrainBirths
{
    public async Task<Member> Born(BrainProvision provision)
    {
        ArgumentNullException.ThrowIfNull(provision);
        var owner = provision.Member;
        // Trust provisioning may have committed before local binding or creation failed.
        // Both local steps are idempotent; NewBrain describes trust state, not their completion.
        // As its owner: the creation handlers (the OS defaults' installs) act in the owner's brain, also at boot,
        // where no call stamped a caller.
        using (edge.Enter(AccountSession.ForMember(edge, owner)))
        { await neurons.Brain(BrainScope.Create(owner.AccountId, owner.BrainId).Id).Create(new Owner(owner.AccountId)); }
        return owner;
    }
}
