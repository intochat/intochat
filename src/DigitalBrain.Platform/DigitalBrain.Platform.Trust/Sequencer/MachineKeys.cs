using System.Security.Cryptography;

namespace DigitalBrain.Platform.Sequencer;

// What the trust side knows of a machine: its P-256 public key and the id derived from it. It verifies; only the
// machine (MachineIdentity) signs.
internal static class MachineKeys
{
    public static string IdOf(byte[] publicKey) => Convert.ToHexStringLower(SHA256.HashData(publicKey))[..16];

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
