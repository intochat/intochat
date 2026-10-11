using System.Security.Cryptography;

namespace DigitalBrain.Platform.Sequencer;

// What the trust side knows of a machine: its P-256 public key and the id derived from it. It verifies; only the
// machine (MachineIdentity) signs.
internal static class MachineKeys
{
    public static string IdOf(byte[] publicKey) => DigitalBrain.Framework.Parts.MachineKeyFile.IdOf(publicKey);

    // R127: a P-256 SubjectPublicKeyInfo usable for ECDH.
    public static bool IsAgreementKey(byte[] publicKey)
    {
        if (publicKey is not { Length: > 0 }) { return false; }
        try
        {
            using var key = ECDiffieHellman.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
            return key.KeySize == 256 && key.ExportParameters(false).Curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value;
        }
        catch (CryptographicException) { return false; }
    }

    public static bool Verify(byte[] publicKey, byte[] data, byte[] signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
            return key.KeySize == 256 && key.ExportParameters(false).Curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value
                && key.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException) { return false; }
    }
}
