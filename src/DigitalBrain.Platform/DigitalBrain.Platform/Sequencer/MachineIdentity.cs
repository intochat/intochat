using DigitalBrain.Framework.Parts;

namespace DigitalBrain.Platform.Sequencer;

// This machine's identity: it signs the head records it writes (Q10), so the sequencer cannot forge them. The key
// behind it is the machine's IMachineSigningKey (R35): a file by default, the TPM under the Windows module.
internal sealed class MachineIdentity(IMachineSigningKey key) : IDisposable
{
    public static string IdOf(byte[] publicKey) => MachineKeys.IdOf(publicKey);

    public string MachineId { get; } = IdOf(key.PublicKey);

    public byte[] PublicKey { get; } = key.PublicKey;

    public static MachineIdentity LoadOrCreate(string path) => new(FileMachineSigningKey.LoadOrCreate(path));

    public byte[] Sign(byte[] data) => key.Sign(data);

    public static bool Verify(byte[] publicKey, byte[] data, byte[] signature) => MachineKeys.Verify(publicKey, data, signature);

    public void Dispose() => (key as IDisposable)?.Dispose();
}
