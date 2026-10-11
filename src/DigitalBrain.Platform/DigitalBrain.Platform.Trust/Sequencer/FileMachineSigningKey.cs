using System.Security.Cryptography;
using DigitalBrain.Framework.Parts;

namespace DigitalBrain.Platform.Sequencer;

// The default machine key: a PKCS#8 P-256 key in a file, created on first use.
internal sealed class FileMachineSigningKey : IMachineSigningKey, IDisposable
{
    private readonly ECDsa _key;

    private FileMachineSigningKey(ECDsa key)
    {
        _key = key;
        PublicKey = key.ExportSubjectPublicKeyInfo();
    }

    public byte[] PublicKey { get; }

    public static FileMachineSigningKey LoadOrCreate(string path) => new(MachineKeyFile.LoadOrCreate(path));

    public byte[] Sign(byte[] data) => _key.SignData(data, HashAlgorithmName.SHA256);

    public void Dispose() => _key.Dispose();
}
