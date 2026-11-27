using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Contracts;
using DigitalBrain.Invocation;

namespace IntoChat.OS.Modules.Identity.Contracts.Keys;

// Non-exportable cryptographic operations executed inside BrainKeysNeuron on behalf of the Identity module.
// Private keys are unsealed ephemerally inside the neuron, used for signing or ECDH key agreement, and zeroed
// before returning; raw PKCS#8 bytes never cross the grain or module boundary.
[Internal, DefaultNeuronType(IBrainKeys.Type)]
public interface IBrainKeysCrypto
{
    Task<BrainPublicKeys> EnsurePublicKeys(CancellationToken ct = default);
    Task<byte[]> SignData(byte[] data, CancellationToken ct = default);
    Task<byte[]> DeriveSharedSecret(byte[] peerEphemeralPublicKeySpki, CancellationToken ct = default);
}
