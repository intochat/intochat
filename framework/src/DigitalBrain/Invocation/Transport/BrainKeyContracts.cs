using System.Security.Cryptography;

namespace DigitalBrain.Invocation;

/// <summary>
/// Non-exportable cryptographic operations for a brain's identity keys.
/// Implementations perform signing and ECDH shared-secret derivation inside the key custody boundary
/// without exposing raw private key material to callers.
/// </summary>
public interface IBrainKeySigner
{
    /// <summary>Returns the brain's public keys (SPKI DER bytes).</summary>
    ValueTask<BrainPublicKeys> GetPublicKeysAsync(BrainId brainId, CancellationToken ct = default);

    /// <summary>Signs <paramref name="data"/> using the brain's private signing key inside the custody boundary.</summary>
    ValueTask<byte[]> SignDataAsync(BrainId brainId, ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>Derives an ECDH shared secret with <paramref name="peerEphemeralPublicKeySpki"/> inside the custody boundary.</summary>
    ValueTask<byte[]> DeriveSharedSecretAsync(BrainId brainId, ReadOnlyMemory<byte> peerEphemeralPublicKeySpki, CancellationToken ct = default);
}

/// <summary>
/// Durable or in-memory directory of pinned remote brain public keys enforcing immutable TOFU pinning
/// and signed key-rotation proofs.
/// </summary>
public interface IBrainKeyDirectory
{
    /// <summary>Returns the pinned public keys for <paramref name="brainId"/>, or <see langword="null"/> if none are pinned.</summary>
    ValueTask<BrainPublicKeys?> GetPinnedKeysAsync(BrainId brainId, CancellationToken ct = default);

    /// <summary>
    /// Returns the pinned public keys for <paramref name="remoteBrain"/> explicitly scoped to <paramref name="localBrain"/>,
    /// without relying on ambient caller state.
    /// </summary>
    ValueTask<BrainPublicKeys?> GetPinnedKeysForAsync(BrainId localBrain, BrainId remoteBrain, CancellationToken ct = default)
        => GetPinnedKeysAsync(remoteBrain, ct);

    /// <summary>
    /// Pins <paramref name="publicKeys"/> for <paramref name="brainId"/> on first use.
    /// Idempotent when identical keys are already pinned; throws <see cref="CryptographicException"/> if different keys are already pinned.
    /// </summary>
    ValueTask PinAsync(BrainId brainId, BrainPublicKeys publicKeys, CancellationToken ct = default);

    /// <summary>
    /// Pins <paramref name="publicKeys"/> for <paramref name="remoteBrain"/> explicitly scoped to <paramref name="localBrain"/>.
    /// </summary>
    ValueTask PinForAsync(BrainId? localBrain, BrainId remoteBrain, BrainPublicKeys publicKeys, CancellationToken ct = default)
        => PinAsync(remoteBrain, publicKeys, ct);

    /// <summary>
    /// Registers a transient, rollbackable handshake key for <paramref name="remoteBrain"/> scoped to <paramref name="localBrain"/>
    /// until the returned scope is disposed. Fails closed with <see cref="CryptographicException"/> if different immutable keys
    /// are already pinned, and never commits an immutable pin on failure.
    /// </summary>
    ValueTask<IDisposable> BeginTransientHandshakeKeyAsync(BrainId localBrain, BrainId remoteBrain, BrainPublicKeys candidateKeys, CancellationToken ct = default);

    /// <summary>
    /// Verifies <paramref name="proof"/> against the currently pinned keys for <see cref="BrainKeyRotationProof.BrainId"/>
    /// and atomically rotates the pin to <see cref="BrainKeyRotationProof.NewKeys"/>.
    /// </summary>
    ValueTask RotateAsync(BrainKeyRotationProof proof, CancellationToken ct = default);
}

/// <summary>
/// Strict NIST P-256 curve validation helpers for imported SPKI public keys.
/// </summary>
public static class BrainPublicKeyValidation
{
    /// <summary>OID for NIST P-256 (secp256r1 / prime256v1).</summary>
    public const string NistP256Oid = "1.2.840.10045.3.1.7";

    /// <summary>Validates that <paramref name="spki"/> is an exact-length NIST P-256 ECDSA SPKI public key.</summary>
    public static void ValidateNistP256SigningKey(ReadOnlySpan<byte> spki)
    {
        if (spki.IsEmpty)
        {
            throw new CryptographicException("Signing public key cannot be empty.");
        }

        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(spki, out var bytesRead);
        if (bytesRead != spki.Length || key.KeySize != 256)
        {
            throw new CryptographicException("Signing public key must be a valid NIST P-256 SPKI DER key.");
        }

        var parameters = key.ExportParameters(includePrivateParameters: false);
        if (!string.Equals(parameters.Curve.Oid?.Value, NistP256Oid, StringComparison.Ordinal))
        {
            throw new CryptographicException($"Signing public key curve OID '{parameters.Curve.Oid?.Value}' is not NIST P-256.");
        }
    }

    /// <summary>Validates that <paramref name="spki"/> is an exact-length NIST P-256 ECDH SPKI public key.</summary>
    public static void ValidateNistP256ExchangeKey(ReadOnlySpan<byte> spki)
    {
        if (spki.IsEmpty)
        {
            throw new CryptographicException("Exchange public key cannot be empty.");
        }

        using var key = ECDiffieHellman.Create();
        key.ImportSubjectPublicKeyInfo(spki, out var bytesRead);
        if (bytesRead != spki.Length || key.KeySize != 256)
        {
            throw new CryptographicException("Exchange public key must be a valid NIST P-256 SPKI DER key.");
        }

        var parameters = key.ExportParameters(includePrivateParameters: false);
        if (!string.Equals(parameters.Curve.Oid?.Value, NistP256Oid, StringComparison.Ordinal))
        {
            throw new CryptographicException($"Exchange public key curve OID '{parameters.Curve.Oid?.Value}' is not NIST P-256.");
        }
    }

    /// <summary>Validates both keys of <paramref name="publicKeys"/> as strict NIST P-256 SPKI DER keys.</summary>
    public static void ValidateNistP256(BrainPublicKeys publicKeys)
    {
        ArgumentNullException.ThrowIfNull(publicKeys);
        ValidateNistP256SigningKey(publicKeys.SigningKey);
        ValidateNistP256ExchangeKey(publicKeys.ExchangeKey);
    }
}
