using System.Security.Cryptography;

namespace DigitalBrain.Platform.Sequencer;

// This machine's signing key: it signs the head records it writes (Q10), so the sequencer cannot forge them.
// Stage A keeps the PKCS#8 key in a file; Task 19 moves it into the machine's TPM.
internal sealed class MachineIdentity : IDisposable
{
    private readonly ECDsa _key;

    private MachineIdentity(ECDsa key)
    {
        _key = key;
        PublicKey = key.ExportSubjectPublicKeyInfo();
        MachineId = IdOf(PublicKey);
    }

    public static string IdOf(byte[] publicKey) => MachineKeys.IdOf(publicKey);

    public string MachineId { get; }

    public byte[] PublicKey { get; }

    public static MachineIdentity LoadOrCreate(string path)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(path)) { key.ImportPkcs8PrivateKey(File.ReadAllBytes(path), out _); }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, key.ExportPkcs8PrivateKey());
        }
        return new MachineIdentity(key);
    }

    public byte[] Sign(byte[] data) => _key.SignData(data, HashAlgorithmName.SHA256);

    public static bool Verify(byte[] publicKey, byte[] data, byte[] signature) => MachineKeys.Verify(publicKey, data, signature);

    public void Dispose() => _key.Dispose();
}
