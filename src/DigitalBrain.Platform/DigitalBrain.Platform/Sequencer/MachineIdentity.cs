using DigitalBrain.Framework.Parts;

namespace DigitalBrain.Platform.Sequencer;

// This machine's identity: it signs the head records it writes (Q10), so the sequencer cannot forge them. The key
// behind it is the machine's IMachineSigningKey (R35): a file by default, the TPM under the Windows module. Beside it
// the machine registers its agreement key (R127), which circle part keys are wrapped to.
internal sealed class MachineIdentity(IMachineSigningKey key, IMachineAgreementKey agreement) : IDisposable
{
    public static string IdOf(byte[] publicKey) => MachineKeys.IdOf(publicKey);

    public string MachineId { get; } = IdOf(key.PublicKey);

    public byte[] PublicKey { get; } = key.PublicKey;

    public byte[] AgreementKey { get; } = agreement.PublicKey;

    public IMachineAgreementKey Agreement => agreement;

    // The agreement key sits beside the signing key's file.
    public static MachineIdentity LoadOrCreate(string path)
        => new(FileMachineSigningKey.LoadOrCreate(path), SoftwareMachineAgreementKey.LoadOrCreate(AgreementPath(path)));

    public static string AgreementPath(string signingKeyPath) => $"{signingKeyPath}.agreement";

    public byte[] Sign(byte[] data) => key.Sign(data);

    public static bool Verify(byte[] publicKey, byte[] data, byte[] signature) => MachineKeys.Verify(publicKey, data, signature);

    public void Dispose()
    {
        (key as IDisposable)?.Dispose();
        (agreement as IDisposable)?.Dispose();
    }
}
