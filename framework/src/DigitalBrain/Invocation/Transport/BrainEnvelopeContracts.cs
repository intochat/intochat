using System.Text.Json.Serialization;

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
    bool OneWay = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BrainPublicKeys? SenderPublicKeys = null);

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
