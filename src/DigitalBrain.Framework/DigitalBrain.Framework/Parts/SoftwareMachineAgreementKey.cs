using System.Security.Cryptography;

namespace DigitalBrain.Framework.Parts;

// The default agreement key (R127): a PKCS#8 P-256 key in a file, created on first use, atomically as MachineKeyFile.
public sealed class SoftwareMachineAgreementKey : IMachineAgreementKey, IDisposable
{
    private readonly ECDiffieHellman _key;

    private SoftwareMachineAgreementKey(ECDiffieHellman key)
    {
        _key = key;
        PublicKey = key.ExportSubjectPublicKeyInfo();
    }

    public byte[] PublicKey { get; }

    public static SoftwareMachineAgreementKey LoadOrCreate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            using (var created = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)) { File.WriteAllBytes(temp, created.ExportPkcs8PrivateKey()); }
            try { File.Move(temp, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { }
            finally { File.Delete(temp); }
        }
        var key = ECDiffieHellman.Create();
        key.ImportPkcs8PrivateKey(File.ReadAllBytes(path), out _);
        return new(key);
    }

    public byte[] DeriveShared(byte[] ephemeralPublicKey) => DeriveWith(_key, ephemeralPublicKey);

    // What both ends compute: SHA-256 over the raw ECDH secret with a P-256 SubjectPublicKeyInfo.
    public static byte[] DeriveWith(ECDiffieHellman own, byte[] peerPublicKey)
    {
        ArgumentNullException.ThrowIfNull(own);
        ArgumentNullException.ThrowIfNull(peerPublicKey);
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerPublicKey, out _);
        if (peer.KeySize != 256) { throw new CryptographicException("An agreement key is P-256."); }
        return own.DeriveKeyFromHash(peer.PublicKey, HashAlgorithmName.SHA256);
    }

    public void Dispose() => _key.Dispose();
}
