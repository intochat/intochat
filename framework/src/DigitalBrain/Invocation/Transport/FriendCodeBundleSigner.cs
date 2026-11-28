using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DigitalBrain.Invocation;

/// <summary>
/// Creates and verifies self-signed <see cref="FriendCodeBundle"/> payloads over the 7-field length-prefixed binary layout
/// with domain separator <c>"DigitalBrain.FriendCode.v1"</c>.
/// </summary>
public static class FriendCodeBundleSigner
{
    /// <summary>Canonical domain separator for friend-code bundle self-signatures.</summary>
    public const string DomainPrefix = "DigitalBrain.FriendCode.v1";

    /// <summary>Maximum allowed lifetime for a friend-code bundle (24 hours).</summary>
    public static readonly TimeSpan MaxBundleLifetime = TimeSpan.FromHours(24);

    private static ReadOnlySpan<byte> DomainSeparatorBytes => "DigitalBrain.FriendCode.v1"u8;

    /// <summary>Computes the 64-character lowercase hex SHA-256 <c>codeHash</c> for a raw friend code.</summary>
    public static string ComputeCodeHash(string rawCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawCode);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawCode.Trim()));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Returns <see langword="true"/> when <paramref name="codeHash"/> is a 64-character lowercase hex SHA-256 digest.</summary>
    public static bool IsLowercaseSha256Hex(string? codeHash)
    {
        if (codeHash is not { Length: 64 })
        {
            return false;
        }

        foreach (var ch in codeHash)
        {
            if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Signs a new <see cref="FriendCodeBundle"/> using <paramref name="keys"/>.</summary>
    public static FriendCodeBundle Sign(
        string codeHash,
        BrainId brainId,
        BrainKeyPair keys,
        DateTimeOffset expiresAt,
        string? relayOrigin = null)
    {
        if (!IsLowercaseSha256Hex(codeHash))
        {
            throw new ArgumentException("codeHash must be a 64-character lowercase hex SHA-256 digest.", nameof(codeHash));
        }

        ArgumentNullException.ThrowIfNull(keys);

        var publicKeys = keys.ExportPublicKeys();
        var trimmedOrigin = relayOrigin?.Trim();
        var canonical = BuildCanonicalBytes(codeHash, brainId, publicKeys.SigningKey, publicKeys.ExchangeKey, expiresAt, trimmedOrigin);
        var signature = keys.SigningKey.SignData(canonical, HashAlgorithmName.SHA256);
        return new FriendCodeBundle(
            CodeHash: codeHash,
            BrainId: brainId,
            SigningPublicKey: publicKeys.SigningKey,
            ExchangePublicKey: publicKeys.ExchangeKey,
            SelfSignature: signature,
            RelayOrigin: trimmedOrigin,
            ExpiresAt: expiresAt);
    }

    /// <summary>Signs a new <see cref="FriendCodeBundle"/> using a non-exportable <see cref="IBrainKeySigner"/>.</summary>
    public static async ValueTask<FriendCodeBundle> SignAsync(
        string codeHash,
        BrainId brainId,
        IBrainKeySigner signer,
        DateTimeOffset expiresAt,
        string? relayOrigin = null,
        CancellationToken ct = default)
    {
        if (!IsLowercaseSha256Hex(codeHash))
        {
            throw new ArgumentException("codeHash must be a 64-character lowercase hex SHA-256 digest.", nameof(codeHash));
        }

        ArgumentNullException.ThrowIfNull(signer);

        var publicKeys = await signer.GetPublicKeysAsync(brainId, ct).ConfigureAwait(false);
        var trimmedOrigin = relayOrigin?.Trim();
        var canonical = BuildCanonicalBytes(codeHash, brainId, publicKeys.SigningKey, publicKeys.ExchangeKey, expiresAt, trimmedOrigin);
        var signature = await signer.SignDataAsync(brainId, canonical, ct).ConfigureAwait(false);
        return new FriendCodeBundle(
            CodeHash: codeHash,
            BrainId: brainId,
            SigningPublicKey: publicKeys.SigningKey,
            ExchangePublicKey: publicKeys.ExchangeKey,
            SelfSignature: signature,
            RelayOrigin: trimmedOrigin,
            ExpiresAt: expiresAt);
    }

    /// <summary>
    /// Verifies the bundle's required fields, strict NIST P-256 keys, expiration window (≤ 24h), and self-signature.
    /// </summary>
    public static bool Verify(FriendCodeBundle? bundle, DateTimeOffset? nowUtc = null)
    {
        if (bundle is null
            || !IsLowercaseSha256Hex(bundle.CodeHash)
            || string.IsNullOrWhiteSpace(bundle.BrainId.Value)
            || bundle.SigningPublicKey is not { Length: > 0 }
            || bundle.ExchangePublicKey is not { Length: > 0 }
            || bundle.SelfSignature is not { Length: > 0 }
            || bundle.ExpiresAt is not { } expiresAt)
        {
            return false;
        }

        if (nowUtc is { } now && (expiresAt <= now || expiresAt > now + MaxBundleLifetime))
        {
            return false;
        }

        try
        {
            BrainPublicKeyValidation.ValidateNistP256SigningKey(bundle.SigningPublicKey);
            BrainPublicKeyValidation.ValidateNistP256ExchangeKey(bundle.ExchangePublicKey);

            var canonical = BuildCanonicalBytes(
                bundle.CodeHash,
                bundle.BrainId,
                bundle.SigningPublicKey,
                bundle.ExchangePublicKey,
                expiresAt,
                bundle.RelayOrigin);

            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(bundle.SigningPublicKey, out _);
            return verifier.VerifyData(canonical, bundle.SelfSignature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Verifies <paramref name="bundle"/> or throws <see cref="CryptographicException"/>.</summary>
    public static void VerifyOrThrow(FriendCodeBundle? bundle, DateTimeOffset? nowUtc = null)
    {
        if (!Verify(bundle, nowUtc))
        {
            throw new CryptographicException("FriendCodeBundle failed self-signature, curve, or freshness verification.");
        }
    }

    /// <summary>
    /// Builds the 7-field length-prefixed binary representation covered by <see cref="FriendCodeBundle.SelfSignature"/>:
    /// 1. DomainSeparator ("DigitalBrain.FriendCode.v1"), 2. brainId, 3. signingPublicKey, 4. exchangePublicKey,
    /// 5. codeHash, 6. expiresAtUnixSeconds (8-byte BE Int64), 7. relayOrigin (trimmed or empty).
    /// </summary>
    public static byte[] BuildCanonicalBytes(
        string codeHash,
        BrainId brainId,
        ReadOnlySpan<byte> signingPublicKey,
        ReadOnlySpan<byte> exchangePublicKey,
        DateTimeOffset expiresAt,
        string? relayOrigin = null)
    {
        var brainBytes = Encoding.UTF8.GetBytes(brainId.Value ?? string.Empty);
        var codeHashBytes = Encoding.UTF8.GetBytes(codeHash);
        var relayOriginBytes = Encoding.UTF8.GetBytes(relayOrigin?.Trim() ?? string.Empty);

        var total =
            4 + DomainSeparatorBytes.Length +
            4 + brainBytes.Length +
            4 + signingPublicKey.Length +
            4 + exchangePublicKey.Length +
            4 + codeHashBytes.Length +
            8 +
            4 + relayOriginBytes.Length;

        var buffer = new byte[total];
        var offset = 0;
        BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, DomainSeparatorBytes);
        BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, brainBytes);
        BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, signingPublicKey);
        BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, exchangePublicKey);
        BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, codeHashBytes);
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(offset, 8), expiresAt.ToUnixTimeSeconds());
        offset += 8;
        BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, relayOriginBytes);
        return buffer;
    }
}
