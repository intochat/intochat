using System.Security.Cryptography;
using System.Text;

namespace IntoChat.Cloud.Auth.Core;

/// <summary>W4 D7: HMAC-SHA256 under a per-purpose derived key; stored as ph1:{purpose}:{base64}.</summary>
public sealed class PurposeHasher
{
    private const string KeyDerivationLabel = "IntoChat:purpose-hasher:v1:";
    private readonly byte[] _purposeKey;
    private readonly string _prefix;

    public PurposeHasher(string purpose, byte[] rootKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentNullException.ThrowIfNull(rootKey);
        if (rootKey.Length < 32)
        {
            throw new ArgumentException("Root key must be at least 32 bytes.", nameof(rootKey));
        }

        Purpose = purpose;
        _prefix = $"ph1:{purpose}:";
        _purposeKey = HMACSHA256.HashData(rootKey, Encoding.UTF8.GetBytes(KeyDerivationLabel + purpose));
    }

    public string Purpose { get; }

    public string Hash(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return _prefix + Convert.ToBase64String(HMACSHA256.HashData(_purposeKey, Encoding.UTF8.GetBytes(value)));
    }

    public bool Verify(string value, string? stored)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(stored) || !stored.StartsWith(_prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(Hash(value)));
    }
}
