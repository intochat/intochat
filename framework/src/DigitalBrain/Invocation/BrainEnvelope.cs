using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DigitalBrain.Invocation;

/// <summary>
/// Public cryptographic material of a brain: P-256 ECDSA signing key and P-256 ECDH key-agreement key,
/// each encoded as SubjectPublicKeyInfo (SPKI) DER bytes.
/// </summary>
public sealed record BrainPublicKeys(byte[] SigningKey, byte[] ExchangeKey)
{
    /// <summary>Returns <see langword="true"/> when both public keys are byte-for-byte identical.</summary>
    public bool Matches(BrainPublicKeys? other)
        => other is not null
            && SigningKey.AsSpan().SequenceEqual(other.SigningKey)
            && ExchangeKey.AsSpan().SequenceEqual(other.ExchangeKey);
}

/// <summary>
/// A signed, end-to-end encrypted envelope for cross-cluster brain-to-brain transport.
/// Intermediate relays can route on <see cref="From"/> and <see cref="To"/> without reading <see cref="Ciphertext"/>.
/// </summary>
public sealed record BrainEnvelope(
    BrainId From,
    BrainId To,
    byte[] Ciphertext,
    byte[] Signature,
    string MessageId,
    string? ReplyToMessageId = null,
    byte[]? EphemeralPublicKey = null,
    byte[]? Nonce = null,
    byte[]? Tag = null,
    DateTimeOffset? SentAtUtc = null,
    bool OneWay = false);

/// <summary>
/// Self-signed friend-code rendezvous bundle published to <c>POST /circle/v1/friends/codes</c>.
/// The <see cref="SelfSignature"/> covers the canonical UTF-8 payload under <see cref="SigningPublicKey"/> so an intermediate relay cannot MITM keys.
/// </summary>
public sealed record FriendCodeBundle(
    string CodeHash,
    BrainId BrainId,
    byte[] SigningPublicKey,
    byte[] ExchangePublicKey,
    byte[] SelfSignature,
    string? RelayOrigin = null,
    DateTimeOffset? ExpiresAt = null)
{
    /// <summary>Returns the bundle's public keys as a <see cref="BrainPublicKeys"/> value.</summary>
    public BrainPublicKeys ToPublicKeys() => new(SigningPublicKey, ExchangePublicKey);
}

/// <summary>Request payload for <c>POST /circle/v1/friends/codes/resolve</c>.</summary>
public sealed record ResolveFriendCodeRequest(string CodeHash);

/// <summary>Response payload for <c>POST /circle/v1/friends/codes/resolve</c>.</summary>
public sealed record ResolveFriendCodeResponse(
    BrainId BrainId,
    byte[] SigningPublicKey,
    byte[] ExchangePublicKey,
    byte[] SelfSignature,
    string? RelayOrigin = null,
    DateTimeOffset? ExpiresAt = null);

/// <summary>
/// Cryptographically signed proof authorizing a brain to rotate from <see cref="PreviousKeys"/> to <see cref="NewKeys"/>.
/// </summary>
public sealed record BrainKeyRotationProof(
    BrainId BrainId,
    BrainPublicKeys PreviousKeys,
    BrainPublicKeys NewKeys,
    DateTimeOffset RotatedAtUtc,
    byte[] Signature);

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
    /// Pins <paramref name="publicKeys"/> for <paramref name="brainId"/> on first use.
    /// Idempotent when identical keys are already pinned; throws <see cref="CryptographicException"/> if different keys are already pinned.
    /// </summary>
    ValueTask PinAsync(BrainId brainId, BrainPublicKeys publicKeys, CancellationToken ct = default);

    /// <summary>
    /// Verifies <paramref name="proof"/> against the currently pinned keys for <see cref="BrainKeyRotationProof.BrainId"/>
    /// and atomically rotates the pin to <see cref="BrainKeyRotationProof.NewKeys"/>.
    /// </summary>
    ValueTask RotateAsync(BrainKeyRotationProof proof, CancellationToken ct = default);
}

/// <summary>
/// Thread-safe in-memory <see cref="IBrainKeyDirectory"/> enforcing immutable TOFU key pinning and signed rotation proofs.
/// </summary>
public sealed class InMemoryPinnedBrainKeyDirectory : IBrainKeyDirectory
{
    private readonly ConcurrentDictionary<string, BrainPublicKeys> _pins = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public ValueTask<BrainPublicKeys?> GetPinnedKeysAsync(BrainId brainId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_pins.TryGetValue(brainId.Value ?? string.Empty, out var keys) ? keys : null);
    }

    /// <inheritdoc />
    public ValueTask PinAsync(BrainId brainId, BrainPublicKeys publicKeys, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(publicKeys);
        if (publicKeys.SigningKey is not { Length: > 0 } || publicKeys.ExchangeKey is not { Length: > 0 })
        {
            throw new CryptographicException("Cannot pin empty BrainPublicKeys.");
        }

        var key = brainId.Value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("BrainId must be non-empty.", nameof(brainId));
        }

        lock (_gate)
        {
            if (_pins.TryGetValue(key, out var existing))
            {
                if (!existing.Matches(publicKeys))
                {
                    throw new CryptographicException(
                        $"Public keys for brain '{key}' are already pinned and cannot be silently replaced without a signed key-rotation proof.");
                }

                return ValueTask.CompletedTask;
            }

            _pins[key] = new BrainPublicKeys((byte[])publicKeys.SigningKey.Clone(), (byte[])publicKeys.ExchangeKey.Clone());
            return ValueTask.CompletedTask;
        }
    }

    /// <inheritdoc />
    public ValueTask RotateAsync(BrainKeyRotationProof proof, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(proof);

        var key = proof.BrainId.Value ?? string.Empty;
        lock (_gate)
        {
            if (!_pins.TryGetValue(key, out var current))
            {
                throw new CryptographicException($"Cannot rotate keys for brain '{key}': no keys are currently pinned.");
            }

            if (!BrainKeyRotationSigner.Verify(proof, current))
            {
                throw new CryptographicException($"Key rotation proof for brain '{key}' failed signature verification.");
            }

            _pins[key] = new BrainPublicKeys((byte[])proof.NewKeys.SigningKey.Clone(), (byte[])proof.NewKeys.ExchangeKey.Clone());
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Asymmetric key pair owned by a brain: P-256 <see cref="ECDsa"/> for signing and P-256 <see cref="ECDiffieHellman"/> for key agreement.
/// </summary>
public sealed class BrainKeyPair : IDisposable
{
    private bool _disposed;

    /// <summary>Creates a key pair from existing P-256 signing and key-agreement keys.</summary>
    public BrainKeyPair(ECDsa signingKey, ECDiffieHellman exchangeKey)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        ArgumentNullException.ThrowIfNull(exchangeKey);
        SigningKey = signingKey;
        ExchangeKey = exchangeKey;
    }

    /// <summary>The P-256 ECDSA signing key.</summary>
    public ECDsa SigningKey { get; }

    /// <summary>The P-256 ECDH key-agreement key.</summary>
    public ECDiffieHellman ExchangeKey { get; }

    /// <summary>Generates a fresh P-256 signing and key-agreement key pair.</summary>
    public static BrainKeyPair Generate()
        => new(ECDsa.Create(ECCurve.NamedCurves.nistP256), ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>Exports the public half of this key pair for directory publication.</summary>
    public BrainPublicKeys ExportPublicKeys()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new(SigningKey.ExportSubjectPublicKeyInfo(), ExchangeKey.ExportSubjectPublicKeyInfo());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        SigningKey.Dispose();
        ExchangeKey.Dispose();
    }
}

/// <summary>
/// <see cref="IBrainKeySigner"/> backed by an in-memory <see cref="BrainKeyPair"/> (for tests and local harnesses).
/// </summary>
public sealed class BrainKeyPairSigner(BrainKeyPair keys) : IBrainKeySigner
{
    private readonly BrainKeyPair _keys = keys ?? throw new ArgumentNullException(nameof(keys));

    /// <inheritdoc />
    public ValueTask<BrainPublicKeys> GetPublicKeysAsync(BrainId brainId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_keys.ExportPublicKeys());
    }

    /// <inheritdoc />
    public ValueTask<byte[]> SignDataAsync(BrainId brainId, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_keys.SigningKey.SignData(data.Span, HashAlgorithmName.SHA256));
    }

    /// <inheritdoc />
    public ValueTask<byte[]> DeriveSharedSecretAsync(BrainId brainId, ReadOnlyMemory<byte> peerEphemeralPublicKeySpki, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var peerPublic = ECDiffieHellman.Create();
        peerPublic.ImportSubjectPublicKeyInfo(peerEphemeralPublicKeySpki.Span, out _);
        return ValueTask.FromResult(_keys.ExchangeKey.DeriveRawSecretAgreement(peerPublic.PublicKey));
    }
}

/// <summary>
/// Creates and verifies self-signed <see cref="FriendCodeBundle"/> payloads over the canonical UTF-8 string:
/// <c>"intochat-friend-code-v1\n{brainId}\n{Base64Url(signingPublicKey)}\n{Base64Url(exchangePublicKey)}\n{codeHash}\n{expiresAtUnixSeconds}"</c>.
/// </summary>
public static class FriendCodeBundleSigner
{
    /// <summary>Canonical domain prefix for friend-code bundle self-signatures.</summary>
    public const string DomainPrefix = "intochat-friend-code-v1";

    /// <summary>Signs a new <see cref="FriendCodeBundle"/> using <paramref name="keys"/>.</summary>
    public static FriendCodeBundle Sign(
        string codeHash,
        BrainId brainId,
        BrainKeyPair keys,
        DateTimeOffset expiresAt,
        string? relayOrigin = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codeHash);
        ArgumentNullException.ThrowIfNull(keys);

        var publicKeys = keys.ExportPublicKeys();
        var canonical = BuildCanonicalBytes(codeHash, brainId, publicKeys.SigningKey, publicKeys.ExchangeKey, expiresAt);
        var signature = keys.SigningKey.SignData(canonical, HashAlgorithmName.SHA256);
        return new FriendCodeBundle(
            CodeHash: codeHash,
            BrainId: brainId,
            SigningPublicKey: publicKeys.SigningKey,
            ExchangePublicKey: publicKeys.ExchangeKey,
            SelfSignature: signature,
            RelayOrigin: relayOrigin,
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
        ArgumentException.ThrowIfNullOrWhiteSpace(codeHash);
        ArgumentNullException.ThrowIfNull(signer);

        var publicKeys = await signer.GetPublicKeysAsync(brainId, ct).ConfigureAwait(false);
        var canonical = BuildCanonicalBytes(codeHash, brainId, publicKeys.SigningKey, publicKeys.ExchangeKey, expiresAt);
        var signature = await signer.SignDataAsync(brainId, canonical, ct).ConfigureAwait(false);
        return new FriendCodeBundle(
            CodeHash: codeHash,
            BrainId: brainId,
            SigningPublicKey: publicKeys.SigningKey,
            ExchangePublicKey: publicKeys.ExchangeKey,
            SelfSignature: signature,
            RelayOrigin: relayOrigin,
            ExpiresAt: expiresAt);
    }

    /// <summary>
    /// Verifies the bundle's required fields, optional expiration window, and self-signature under <see cref="FriendCodeBundle.SigningPublicKey"/>.
    /// </summary>
    public static bool Verify(FriendCodeBundle? bundle, DateTimeOffset? nowUtc = null)
    {
        if (bundle is null
            || string.IsNullOrWhiteSpace(bundle.CodeHash)
            || string.IsNullOrWhiteSpace(bundle.BrainId.Value)
            || bundle.SigningPublicKey is not { Length: > 0 }
            || bundle.ExchangePublicKey is not { Length: > 0 }
            || bundle.SelfSignature is not { Length: > 0 }
            || bundle.ExpiresAt is not { } expiresAt)
        {
            return false;
        }

        if (nowUtc is { } now && now > expiresAt)
        {
            return false;
        }

        try
        {
            var canonical = BuildCanonicalBytes(
                bundle.CodeHash,
                bundle.BrainId,
                bundle.SigningPublicKey,
                bundle.ExchangePublicKey,
                expiresAt);

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
            throw new CryptographicException("FriendCodeBundle failed self-signature or freshness verification.");
        }
    }

    /// <summary>Builds the canonical UTF-8 string covered by <see cref="FriendCodeBundle.SelfSignature"/>.</summary>
    public static string BuildCanonicalPayload(
        string codeHash,
        BrainId brainId,
        ReadOnlySpan<byte> signingPublicKey,
        ReadOnlySpan<byte> exchangePublicKey,
        DateTimeOffset expiresAt)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{DomainPrefix}\n{brainId.Value ?? string.Empty}\n{Base64Url.EncodeToString(signingPublicKey)}\n{Base64Url.EncodeToString(exchangePublicKey)}\n{codeHash}\n{expiresAt.ToUnixTimeSeconds()}");

    /// <summary>Builds the canonical UTF-8 byte representation covered by <see cref="FriendCodeBundle.SelfSignature"/>.</summary>
    public static byte[] BuildCanonicalBytes(
        string codeHash,
        BrainId brainId,
        ReadOnlySpan<byte> signingPublicKey,
        ReadOnlySpan<byte> exchangePublicKey,
        DateTimeOffset expiresAt)
        => Encoding.UTF8.GetBytes(BuildCanonicalPayload(codeHash, brainId, signingPublicKey, exchangePublicKey, expiresAt));
}

/// <summary>
/// Creates and verifies signed <see cref="BrainKeyRotationProof"/> records for explicit key rotation.
/// </summary>
public static class BrainKeyRotationSigner
{
    private static ReadOnlySpan<byte> DomainSeparator => "DigitalBrain.BrainKeyRotation.v1"u8;

    /// <summary>Signs a key rotation proof using the brain's previous key pair.</summary>
    public static BrainKeyRotationProof Sign(
        BrainId brainId,
        BrainKeyPair previousKeys,
        BrainPublicKeys newKeys,
        DateTimeOffset rotatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(previousKeys);
        ArgumentNullException.ThrowIfNull(newKeys);

        var prevPublic = previousKeys.ExportPublicKeys();
        var canonical = BuildCanonicalBytes(brainId, prevPublic, newKeys, rotatedAtUtc);
        var signature = previousKeys.SigningKey.SignData(canonical, HashAlgorithmName.SHA256);
        return new BrainKeyRotationProof(brainId, prevPublic, newKeys, rotatedAtUtc, signature);
    }

    /// <summary>Verifies that <paramref name="proof"/> was signed by <paramref name="currentPinnedKeys"/>.</summary>
    public static bool Verify(BrainKeyRotationProof? proof, BrainPublicKeys? currentPinnedKeys)
    {
        if (proof is null
            || currentPinnedKeys is null
            || !currentPinnedKeys.Matches(proof.PreviousKeys)
            || proof.NewKeys is not { SigningKey.Length: > 0, ExchangeKey.Length: > 0 }
            || proof.Signature is not { Length: > 0 })
        {
            return false;
        }

        try
        {
            var canonical = BuildCanonicalBytes(proof.BrainId, proof.PreviousKeys, proof.NewKeys, proof.RotatedAtUtc);
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(currentPinnedKeys.SigningKey, out _);
            return verifier.VerifyData(canonical, proof.Signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static byte[] BuildCanonicalBytes(
        BrainId brainId,
        BrainPublicKeys previousKeys,
        BrainPublicKeys newKeys,
        DateTimeOffset rotatedAtUtc)
    {
        var brainBytes = Encoding.UTF8.GetBytes(brainId.Value ?? string.Empty);
        var total =
            4 + DomainSeparator.Length +
            4 + brainBytes.Length +
            4 + previousKeys.SigningKey.Length +
            4 + previousKeys.ExchangeKey.Length +
            4 + newKeys.SigningKey.Length +
            4 + newKeys.ExchangeKey.Length +
            8;

        var buffer = new byte[total];
        var offset = 0;
        BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, DomainSeparator);
        BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, brainBytes);
        BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, previousKeys.SigningKey);
        BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, previousKeys.ExchangeKey);
        BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, newKeys.SigningKey);
        BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, newKeys.ExchangeKey);
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(offset, 8), rotatedAtUtc.ToUnixTimeMilliseconds());
        return buffer;
    }
}

/// <summary>
/// Seals and opens <see cref="BrainEnvelope"/> payloads using ephemeral P-256 ECDH + HKDF-SHA256 + AES-256-GCM
/// and authenticates every envelope field (including <see cref="BrainEnvelope.SentAtUtc"/> and <see cref="BrainEnvelope.OneWay"/>) with P-256 ECDSA.
/// </summary>
public static class BrainEnvelopeSealer
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int AesKeySize = 32;
    private static ReadOnlySpan<byte> HkdfInfo => "DigitalBrain.BrainEnvelope.v1"u8;

    /// <summary>Default maximum allowed clock skew (±5 minutes) for <see cref="BrainEnvelope.SentAtUtc"/>.</summary>
    public static readonly TimeSpan DefaultMaxClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> to <paramref name="recipientPublicKeys"/> and signs the complete envelope
    /// with <paramref name="senderKeys"/>.
    /// </summary>
    public static BrainEnvelope Seal(
        BrainId from,
        BrainId to,
        string messageId,
        ReadOnlySpan<byte> plaintext,
        BrainKeyPair senderKeys,
        BrainPublicKeys recipientPublicKeys,
        string? replyToMessageId = null,
        DateTimeOffset? sentAtUtc = null,
        bool oneWay = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        ArgumentNullException.ThrowIfNull(senderKeys);
        ArgumentNullException.ThrowIfNull(recipientPublicKeys);

        var timestamp = (sentAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var (ephemeralPublicKey, nonce, tag, ciphertext, signingInput) = EncryptAndPrepareSigningInput(
            from,
            to,
            messageId,
            plaintext,
            recipientPublicKeys,
            replyToMessageId,
            timestamp,
            oneWay);

        var signature = senderKeys.SigningKey.SignData(signingInput, HashAlgorithmName.SHA256);

        return new BrainEnvelope(
            From: from,
            To: to,
            Ciphertext: ciphertext,
            Signature: signature,
            MessageId: messageId,
            ReplyToMessageId: replyToMessageId,
            EphemeralPublicKey: ephemeralPublicKey,
            Nonce: nonce,
            Tag: tag,
            SentAtUtc: timestamp,
            OneWay: oneWay);
    }

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> to <paramref name="recipientPublicKeys"/> and signs the complete envelope
    /// using a non-exportable <see cref="IBrainKeySigner"/>.
    /// </summary>
    public static async ValueTask<BrainEnvelope> SealAsync(
        BrainId from,
        BrainId to,
        string messageId,
        ReadOnlyMemory<byte> plaintext,
        IBrainKeySigner senderSigner,
        BrainPublicKeys recipientPublicKeys,
        string? replyToMessageId = null,
        DateTimeOffset? sentAtUtc = null,
        bool oneWay = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        ArgumentNullException.ThrowIfNull(senderSigner);
        ArgumentNullException.ThrowIfNull(recipientPublicKeys);

        var timestamp = (sentAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var (ephemeralPublicKey, nonce, tag, ciphertext, signingInput) = EncryptAndPrepareSigningInput(
            from,
            to,
            messageId,
            plaintext.Span,
            recipientPublicKeys,
            replyToMessageId,
            timestamp,
            oneWay);

        var signature = await senderSigner.SignDataAsync(from, signingInput, ct).ConfigureAwait(false);

        return new BrainEnvelope(
            From: from,
            To: to,
            Ciphertext: ciphertext,
            Signature: signature,
            MessageId: messageId,
            ReplyToMessageId: replyToMessageId,
            EphemeralPublicKey: ephemeralPublicKey,
            Nonce: nonce,
            Tag: tag,
            SentAtUtc: timestamp,
            OneWay: oneWay);
    }

    /// <summary>
    /// Verifies the sender's ECDSA signature FIRST, then derives the shared secret and decrypts <paramref name="envelope"/>.
    /// </summary>
    /// <exception cref="CryptographicException">Thrown when the signature is invalid, required fields are missing, or decryption fails.</exception>
    public static byte[] Open(
        BrainEnvelope envelope,
        BrainKeyPair recipientKeys,
        BrainPublicKeys expectedSenderPublicKeys)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(recipientKeys);
        ArgumentNullException.ThrowIfNull(expectedSenderPublicKeys);

        VerifySignatureAndFields(envelope, expectedSenderPublicKeys);

        using var ephemeralPublic = ECDiffieHellman.Create();
        ephemeralPublic.ImportSubjectPublicKeyInfo(envelope.EphemeralPublicKey!, out _);

        byte[]? sharedSecret = null;
        try
        {
            sharedSecret = recipientKeys.ExchangeKey.DeriveRawSecretAgreement(ephemeralPublic.PublicKey);
            return DecryptWithSharedSecret(envelope, sharedSecret);
        }
        finally
        {
            if (sharedSecret is not null) { CryptographicOperations.ZeroMemory(sharedSecret); }
        }
    }

    /// <summary>
    /// Verifies the sender's ECDSA signature FIRST, then derives the shared secret inside the non-exportable
    /// <see cref="IBrainKeySigner"/> boundary and decrypts <paramref name="envelope"/>.
    /// </summary>
    /// <exception cref="CryptographicException">Thrown when the signature is invalid, required fields are missing, or decryption fails.</exception>
    public static async ValueTask<byte[]> OpenAsync(
        BrainEnvelope envelope,
        IBrainKeySigner recipientSigner,
        BrainPublicKeys expectedSenderPublicKeys,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(recipientSigner);
        ArgumentNullException.ThrowIfNull(expectedSenderPublicKeys);

        VerifySignatureAndFields(envelope, expectedSenderPublicKeys);

        byte[]? sharedSecret = null;
        try
        {
            sharedSecret = await recipientSigner.DeriveSharedSecretAsync(envelope.To, envelope.EphemeralPublicKey!, ct).ConfigureAwait(false);
            return DecryptWithSharedSecret(envelope, sharedSecret);
        }
        finally
        {
            if (sharedSecret is not null) { CryptographicOperations.ZeroMemory(sharedSecret); }
        }
    }

    private static (byte[] EphemeralPublicKey, byte[] Nonce, byte[] Tag, byte[] Ciphertext, byte[] SigningInput) EncryptAndPrepareSigningInput(
        BrainId from,
        BrainId to,
        string messageId,
        ReadOnlySpan<byte> plaintext,
        BrainPublicKeys recipientPublicKeys,
        string? replyToMessageId,
        DateTimeOffset sentAtUtc,
        bool oneWay)
    {
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var recipientExchange = ECDiffieHellman.Create();
        recipientExchange.ImportSubjectPublicKeyInfo(recipientPublicKeys.ExchangeKey, out _);

        var ephemeralPublicKey = ephemeral.ExportSubjectPublicKeyInfo();
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        var associatedData = BuildAssociatedData(from, to, messageId, replyToMessageId, sentAtUtc, oneWay);

        byte[]? sharedSecret = null;
        var aesKey = new byte[AesKeySize];
        try
        {
            sharedSecret = ephemeral.DeriveRawSecretAgreement(recipientExchange.PublicKey);
            HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, aesKey, salt: nonce, info: HkdfInfo);

            using var aes = new AesGcm(aesKey, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        }
        finally
        {
            if (sharedSecret is not null) { CryptographicOperations.ZeroMemory(sharedSecret); }
            CryptographicOperations.ZeroMemory(aesKey);
        }

        var signingInput = BuildSigningInput(from, to, messageId, replyToMessageId, sentAtUtc, oneWay, ephemeralPublicKey, nonce, tag, ciphertext);
        return (ephemeralPublicKey, nonce, tag, ciphertext, signingInput);
    }

    private static void VerifySignatureAndFields(BrainEnvelope envelope, BrainPublicKeys expectedSenderPublicKeys)
    {
        if (string.IsNullOrEmpty(envelope.MessageId)
            || envelope.EphemeralPublicKey is null
            || envelope.Nonce is null
            || envelope.Nonce.Length != NonceSize
            || envelope.Tag is null
            || envelope.Tag.Length != TagSize
            || envelope.Ciphertext is null
            || envelope.Signature is null)
        {
            throw new CryptographicException("BrainEnvelope is missing required cryptographic fields.");
        }

        var signingInput = BuildSigningInput(
            envelope.From,
            envelope.To,
            envelope.MessageId,
            envelope.ReplyToMessageId,
            envelope.SentAtUtc,
            envelope.OneWay,
            envelope.EphemeralPublicKey,
            envelope.Nonce,
            envelope.Tag,
            envelope.Ciphertext);

        using var senderVerify = ECDsa.Create();
        senderVerify.ImportSubjectPublicKeyInfo(expectedSenderPublicKeys.SigningKey, out _);
        if (!senderVerify.VerifyData(signingInput, envelope.Signature, HashAlgorithmName.SHA256))
        {
            throw new CryptographicException("BrainEnvelope signature verification failed.");
        }
    }

    private static byte[] DecryptWithSharedSecret(BrainEnvelope envelope, byte[] sharedSecret)
    {
        var plaintext = new byte[envelope.Ciphertext.Length];
        var associatedData = BuildAssociatedData(
            envelope.From,
            envelope.To,
            envelope.MessageId,
            envelope.ReplyToMessageId,
            envelope.SentAtUtc,
            envelope.OneWay);

        var aesKey = new byte[AesKeySize];
        try
        {
            HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, aesKey, salt: envelope.Nonce!, info: HkdfInfo);

            using var aes = new AesGcm(aesKey, TagSize);
            aes.Decrypt(envelope.Nonce!, envelope.Ciphertext, envelope.Tag!, plaintext, associatedData);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aesKey);
        }

        return plaintext;
    }

    private static byte[] BuildAssociatedData(
        BrainId from,
        BrainId to,
        string messageId,
        string? replyToMessageId,
        DateTimeOffset? sentAtUtc,
        bool oneWay)
    {
        var fromBytes = Encoding.UTF8.GetBytes(from.Value ?? string.Empty);
        var toBytes = Encoding.UTF8.GetBytes(to.Value ?? string.Empty);
        var idBytes = Encoding.UTF8.GetBytes(messageId);
        var replyBytes = replyToMessageId is null ? [] : Encoding.UTF8.GetBytes(replyToMessageId);

        var total = 4 + fromBytes.Length + 4 + toBytes.Length + 4 + idBytes.Length + 4 + replyBytes.Length + 1 + 8 + 1;
        var buffer = new byte[total];
        var offset = 0;
        WriteLengthPrefixed(buffer, ref offset, fromBytes);
        WriteLengthPrefixed(buffer, ref offset, toBytes);
        WriteLengthPrefixed(buffer, ref offset, idBytes);
        WriteLengthPrefixed(buffer, ref offset, replyBytes);
        buffer[offset++] = sentAtUtc.HasValue ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(offset, 8), sentAtUtc?.ToUniversalTime().ToUnixTimeMilliseconds() ?? 0L);
        offset += 8;
        buffer[offset] = oneWay ? (byte)1 : (byte)0;
        return buffer;
    }

    private static byte[] BuildSigningInput(
        BrainId from,
        BrainId to,
        string messageId,
        string? replyToMessageId,
        DateTimeOffset? sentAtUtc,
        bool oneWay,
        byte[] ephemeralPublicKey,
        byte[] nonce,
        byte[] tag,
        byte[] ciphertext)
    {
        var fromBytes = Encoding.UTF8.GetBytes(from.Value ?? string.Empty);
        var toBytes = Encoding.UTF8.GetBytes(to.Value ?? string.Empty);
        var idBytes = Encoding.UTF8.GetBytes(messageId);
        var replyBytes = replyToMessageId is null ? [] : Encoding.UTF8.GetBytes(replyToMessageId);

        var total =
            4 + fromBytes.Length +
            4 + toBytes.Length +
            4 + idBytes.Length +
            4 + replyBytes.Length +
            1 + 8 + 1 +
            4 + ephemeralPublicKey.Length +
            4 + nonce.Length +
            4 + tag.Length +
            4 + ciphertext.Length;

        var buffer = new byte[total];
        var offset = 0;
        WriteLengthPrefixed(buffer, ref offset, fromBytes);
        WriteLengthPrefixed(buffer, ref offset, toBytes);
        WriteLengthPrefixed(buffer, ref offset, idBytes);
        WriteLengthPrefixed(buffer, ref offset, replyBytes);
        buffer[offset++] = sentAtUtc.HasValue ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(offset, 8), sentAtUtc?.ToUniversalTime().ToUnixTimeMilliseconds() ?? 0L);
        offset += 8;
        buffer[offset++] = oneWay ? (byte)1 : (byte)0;
        WriteLengthPrefixed(buffer, ref offset, ephemeralPublicKey);
        WriteLengthPrefixed(buffer, ref offset, nonce);
        WriteLengthPrefixed(buffer, ref offset, tag);
        WriteLengthPrefixed(buffer, ref offset, ciphertext);
        return buffer;
    }

    internal static void WriteLengthPrefixed(byte[] destination, ref int offset, ReadOnlySpan<byte> value)
    {
        BinaryPrimitives.WriteInt32BigEndian(destination.AsSpan(offset, 4), value.Length);
        offset += 4;
        value.CopyTo(destination.AsSpan(offset));
        offset += value.Length;
    }
}
