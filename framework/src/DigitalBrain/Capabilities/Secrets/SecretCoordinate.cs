using System.Security.Cryptography;
using System.Text;

namespace DigitalBrain.Capabilities.Secrets;

/// <summary>
/// Deterministic, collision-free external vault secret coordinates and AEAD associated-data bindings.
/// Produces lowercase alphanumeric-hyphen names safe for Azure Key Vault, Bitwarden Secrets Manager,
/// and other external secret stores: <c>ic-{deploymentHash8}-{ownerKind}-{sha256(ownerId)[0..16]}-{sha256(name)[0..16]}</c>.
/// </summary>
public static class SecretCoordinate
{
    /// <summary>Default owner kind segment when no explicit kind is provided.</summary>
    public const string DefaultOwnerKind = "owner";

    /// <summary>
    /// Computes the canonical external secret coordinate:
    /// <c>ic-{deploymentHash8}-{ownerKind}-{sha256(ownerId)[0..16]}-{sha256(name)[0..16]}</c>.
    /// </summary>
    public static string Format(string deploymentId, string ownerId, string name, string ownerKind = DefaultOwnerKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var kind = NormalizeKind(ownerKind);
        var depHash = HashHex(deploymentId, 8);
        var ownerHash = HashHex(ownerId, 16);
        var nameHash = HashHex(name, 16);
        return $"ic-{depHash}-{kind}-{ownerHash}-{nameHash}";
    }

    /// <summary>
    /// Computes the owner prefix <c>ic-{deploymentHash8}-{ownerKind}-{sha256(ownerId)[0..16]}-</c>
    /// used to filter external vault secrets belonging to one owner in one deployment.
    /// </summary>
    public static string OwnerPrefix(string deploymentId, string ownerId, string ownerKind = DefaultOwnerKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var kind = NormalizeKind(ownerKind);
        var depHash = HashHex(deploymentId, 8);
        var ownerHash = HashHex(ownerId, 16);
        return $"ic-{depHash}-{kind}-{ownerHash}-";
    }

    /// <summary>
    /// Builds an authenticated associated-data (AAD) binding string tying ciphertext to a specific
    /// deployment, owner, and secret name so cross-tenant or cross-secret ciphertext substitution fails authentication.
    /// </summary>
    public static string FormatAad(string deploymentId, string ownerId, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return $"vault-secret|v1|{deploymentId.Length}:{deploymentId}|{ownerId.Length}:{ownerId}|{name.Length}:{name}";
    }

    private static string NormalizeKind(string? ownerKind)
    {
        if (string.IsNullOrWhiteSpace(ownerKind))
        {
            return DefaultOwnerKind;
        }

        Span<char> buffer = stackalloc char[Math.Min(ownerKind.Length, 16)];
        var length = 0;
        foreach (var ch in ownerKind)
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

        return length == 0 ? DefaultOwnerKind : new string(buffer[..length]);
    }

    private static string HashHex(string input, int hexChars)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(input), hash);
        return Convert.ToHexString(hash[..(hexChars / 2)]).ToLowerInvariant();
    }
}
