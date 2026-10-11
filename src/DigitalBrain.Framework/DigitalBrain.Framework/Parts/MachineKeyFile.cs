using System.Security.Cryptography;

namespace DigitalBrain.Framework.Parts;

// The file a machine (or the trust host) keeps its P-256 signing key in, and the id derived from a public key. One
// format for the platform's FileMachineSigningKey and for the AppHost, which creates the trust host's key to pin it
// on every machine (R96).
public static class MachineKeyFile
{
    // PKCS#8, created on first use.
    public static ECDsa LoadOrCreate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(path)) { key.ImportPkcs8PrivateKey(File.ReadAllBytes(path), out _); }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllBytes(path, key.ExportPkcs8PrivateKey());
        }
        return key;
    }

    // A machine's id: the first 16 hex digits of SHA-256 over its SubjectPublicKeyInfo.
    public static string IdOf(byte[] publicKey) => Convert.ToHexStringLower(SHA256.HashData(publicKey))[..16];

    public static string IdOfFile(string path)
    {
        using var key = LoadOrCreate(path);
        return IdOf(key.ExportSubjectPublicKeyInfo());
    }
}
