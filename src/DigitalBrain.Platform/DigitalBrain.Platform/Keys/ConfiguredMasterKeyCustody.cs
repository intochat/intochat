using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Platform.Storage;
using DigitalBrain.Framework.Parts;

namespace DigitalBrain.Platform.Keys;

// Development custody: one configured root, an HKDF-derived master key per account. Not user-held; Task 19 is.
internal sealed class ConfiguredMasterKeyCustody(byte[] root) : IMasterKeyCustody
{
    public byte[] Wrap(string accountId, string context, byte[] secret) => SegmentSeal.Close(Master(accountId), context, secret);

    public byte[] Unwrap(string accountId, string context, byte[] wrapped) => SegmentSeal.Open(Master(accountId), context, wrapped);

    private byte[] Master(string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, root, 32, info: Encoding.UTF8.GetBytes($"master|{accountId}"));
    }
}
