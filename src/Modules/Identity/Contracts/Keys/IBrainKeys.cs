using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Contracts;
using DigitalBrain.Invocation;

namespace IntoChat.OS.Modules.Identity.Contracts.Keys;

// The key pair of a brain (a signing key and a key-agreement key), at "{brain}/keys". The private halves are sealed in this
// neuron's state and never leave it: no member returns them, and what a state read shows is ciphertext. The contract offers
// public keys and operations only.
//
// It is not shareable and not offered: no caller of another brain reaches it. Inside the brain it serves one caller, the
// brain's own friend roster (neuron type RosterType), which the neuron reads from the stamped caller; an app, an agent or a
// person calling it directly is refused.
[Internal, NeuronAlias(IBrainKeys.Type), DefaultNeuronType(IBrainKeys.Type)]
public interface IBrainKeys
{
    const string Type = "identity.brain-keys";
    const string KeyPart = "keys";

    // The neuron type of a brain's friend roster.
    const string RosterType = "friends.links";

    static NeuronId Id(BrainId brain) => new(brain, Type, KeyPart);

    // The public halves of the pair this brain holds, or null while it holds none. Makes nothing.
    Task<BrainPublicKeys?> PublicKeys(CancellationToken ct = default);

    // The public halves, after making a new pair if the brain holds none.
    Task<BrainPublicKeys> Ensure(CancellationToken ct = default);

    // Takes over a pair that was kept elsewhere (the roster record of an earlier version). Accepted only while the brain holds
    // none: a brain that already holds a pair keeps it, whatever is passed. Answers the public halves of the pair now held.
    Task<BrainPublicKeys> Adopt(byte[] signingKeyPkcs8, byte[] exchangeKeyPkcs8, CancellationToken ct = default);
}
