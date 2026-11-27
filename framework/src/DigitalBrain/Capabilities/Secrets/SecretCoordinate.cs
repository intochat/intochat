using System.Security.Cryptography;
using System.Text;

namespace DigitalBrain.Capabilities.Secrets;

/// <summary>
/// Deterministic, collision-free external vault secret coordinates and AEAD associated-data bindings.
/// Produces lowercase alphanumeric-hyphen names safe for Azure Key Vault, Bitwarden Secrets Manager,
/// and other external secret stores: <c>{prefix}-{deploymentHash8}-{ownerKind}-{sha256(ownerId)[0..16]}-{sha256(name)[0..16]}</c>.
/// </summary>
public static class SecretCoordinate
{
    /// <summary>Default product-neutral coordinate prefix (<c>db</c>).</summary>
    public const string DefaultPrefix = "db";

    /// <summary>Default owner kind segment when no explicit kind is provided.</summary>
    public const string DefaultOwnerKind = "owner";

    /// <summary>
    /// Computes the canonical external secret coordinate:
    /// <c>{prefix}-{deploymentHash8}-{ownerKind}-{sha256(ownerId)[0..16]}-{sha256(name)[0..16]}</c>.
    /// </summary>
    public static string Format(string deploymentId, string ownerId, string name, string ownerKind = DefaultOwnerKind, string prefix = DefaultPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalizedPrefix = NormalizeToken(prefix, DefaultPrefix, 8);
        var kind = NormalizeToken(ownerKind, DefaultOwnerKind, 16);
        var depHash = HashHex(deploymentId, 8);
        var ownerHash = HashHex(ownerId, 16);
        var nameHash = HashHex(name, 16);
        return $"{normalizedPrefix}-{depHash}-{kind}-{ownerHash}-{nameHash}";
    }

    /// <summary>
    /// Computes the owner prefix <c>{prefix}-{deploymentHash8}-{ownerKind}-{sha256(ownerId)[0..16]}-</c>
    /// used to filter external vault secrets belonging to one owner in one deployment.
    /// </summary>
    public static string OwnerPrefix(string deploymentId, string ownerId, string ownerKind = DefaultOwnerKind, string prefix = DefaultPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var normalizedPrefix = NormalizeToken(prefix, DefaultPrefix, 8);
        var kind = NormalizeToken(ownerKind, DefaultOwnerKind, 16);
        var depHash = HashHex(deploymentId, 8);
        var ownerHash = HashHex(ownerId, 16);
        return $"{normalizedPrefix}-{depHash}-{kind}-{ownerHash}-";
    }

    /// <summary>
    /// Builds a length-prefixed authenticated associated-data (AAD) binding string tying ciphertext to a specific
    /// deployment, owner, and secret name so cross-tenant or cross-secret ciphertext substitution and delimiter
    /// ambiguity both fail authentication.
    /// </summary>
    public static string FormatAad(string deploymentId, string ownerId, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return $"vault-secret|v1|{deploymentId.Length}:{deploymentId}|{ownerId.Length}:{ownerId}|{name.Length}:{name}";
    }

    private static string NormalizeToken(string? raw, string fallback, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        Span<char> buffer = stackalloc char[Math.Min(raw.Length, maxLength)];
        var length = 0;
        foreach (var ch in raw)
        {
            if (length >= buffer.Length)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(ch))
            {
                buffer[length++] = char.ToLowerInvariant(ch);
            }
        }

        return length == 0 ? fallback : new string(buffer[..length]);
    }

    private static string HashHex(string input, int hexChars)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(input), hash);
        return Convert.ToHexString(hash[..(hexChars / 2)]).ToLowerInvariant();
    }
}
