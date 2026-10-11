using System.Security.Cryptography;

namespace DigitalBrain.Framework.Parts;

// The file a machine (or the trust host) keeps its P-256 signing key in, and the id derived from a public key. One
// format for the platform's FileMachineSigningKey and for the AppHost, which creates the trust host's key to pin it
// on every machine (R96).
public static class MachineKeyFile
{
    // PKCS#8, created on first use. Creation is atomic: a key written to a temporary file is moved into place only if
    // none exists, so two processes racing (the AppHost and the trust host) both end up with the one that won.
    public static ECDsa LoadOrCreate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            using (var created = ECDsa.Create(ECCurve.NamedCurves.nistP256)) { File.WriteAllBytes(temp, created.ExportPkcs8PrivateKey()); }
            try { File.Move(temp, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { }
            finally { File.Delete(temp); }
        }
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(File.ReadAllBytes(path), out _);
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
