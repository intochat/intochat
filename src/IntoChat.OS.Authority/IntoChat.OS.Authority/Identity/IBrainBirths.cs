using IntoChat.OS.Framework.Modules.Platform.Identity;

namespace IntoChat.OS.Authority.Identity;

// Creates the brain neuron for a provisioned brain; idempotent.
public interface IBrainBirths
{
    Task<Member> Born(BrainProvision provision);
}
