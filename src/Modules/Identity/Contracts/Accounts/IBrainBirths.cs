using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Identity;

namespace IntoChat.OS.Modules.Identity.Contracts.Accounts;

// Creates the brain neuron for a provisioned brain; idempotent.
public interface IBrainBirths
{
    Task<Member> Born(BrainProvision provision);

    Task<Member> BornEphemeral(Member owner, BrainResidency.Ephemeral residency)
        => Born(new BrainProvision(owner, true));
}
