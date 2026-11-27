using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DigitalBrain.Invocation;

/// <summary>
/// Public cryptographic material of a brain: P-256 ECDSA signing key and P-256 ECDH key-agreement key,
/// each encoded as SubjectPublicKeyInfo (SPKI) DER bytes.
/// </summary>
public sealed record BrainPublicKeys(byte[] SigningKey, byte[] ExchangeKey);

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
    DateTimeOffset? SentAtUtc = null);

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
/// Seals and opens <see cref="BrainEnvelope"/> payloads using ephemeral P-256 ECDH + HKDF-SHA256 + AES-256-GCM
/// and authenticates every envelope field with P-256 ECDSA.
/// </summary>
public static class BrainEnvelopeSealer
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int AesKeySize = 32;
    private static ReadOnlySpan<byte> HkdfInfo => "DigitalBrain.BrainEnvelope.v1"u8;

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
        DateTimeOffset? sentAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        ArgumentNullException.ThrowIfNull(senderKeys);
        ArgumentNullException.ThrowIfNull(recipientPublicKeys);

        var timestamp = (sentAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();

        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var recipientExchange = ECDiffieHellman.Create();
        recipientExchange.ImportSubjectPublicKeyInfo(recipientPublicKeys.ExchangeKey, out _);

        var ephemeralPublicKey = ephemeral.ExportSubjectPublicKeyInfo();
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        var associatedData = BuildAssociatedData(from, to, messageId, replyToMessageId, timestamp);

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

        var signingInput = BuildSigningInput(from, to, messageId, replyToMessageId, timestamp, ephemeralPublicKey, nonce, tag, ciphertext);
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
            SentAtUtc: timestamp);
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
            envelope.EphemeralPublicKey,
            envelope.Nonce,
            envelope.Tag,
            envelope.Ciphertext);

        using (var senderVerify = ECDsa.Create())
        {
            senderVerify.ImportSubjectPublicKeyInfo(expectedSenderPublicKeys.SigningKey, out _);
            if (!senderVerify.VerifyData(signingInput, envelope.Signature, HashAlgorithmName.SHA256))
            {
                throw new CryptographicException("BrainEnvelope signature verification failed.");
            }
        }

        using var ephemeralPublic = ECDiffieHellman.Create();
        ephemeralPublic.ImportSubjectPublicKeyInfo(envelope.EphemeralPublicKey, out _);

        var plaintext = new byte[envelope.Ciphertext.Length];
        var associatedData = BuildAssociatedData(
            envelope.From,
            envelope.To,
            envelope.MessageId,
            envelope.ReplyToMessageId,
            envelope.SentAtUtc);

        byte[]? sharedSecret = null;
        var aesKey = new byte[AesKeySize];
        try
        {
            sharedSecret = recipientKeys.ExchangeKey.DeriveRawSecretAgreement(ephemeralPublic.PublicKey);
            HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, aesKey, salt: envelope.Nonce, info: HkdfInfo);

            using var aes = new AesGcm(aesKey, TagSize);
            aes.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, plaintext, associatedData);
        }
        finally
        {
            if (sharedSecret is not null) { CryptographicOperations.ZeroMemory(sharedSecret); }
            CryptographicOperations.ZeroMemory(aesKey);
        }

        return plaintext;
    }

    private static byte[] BuildAssociatedData(
        BrainId from,
        BrainId to,
        string messageId,
        string? replyToMessageId,
        DateTimeOffset? sentAtUtc)
    {
        var fromBytes = Encoding.UTF8.GetBytes(from.Value ?? string.Empty);
        var toBytes = Encoding.UTF8.GetBytes(to.Value ?? string.Empty);
        var idBytes = Encoding.UTF8.GetBytes(messageId);
        var replyBytes = replyToMessageId is null ? [] : Encoding.UTF8.GetBytes(replyToMessageId);
        var timestampSize = sentAtUtc.HasValue ? 9 : 1;

        var total = 4 + fromBytes.Length + 4 + toBytes.Length + 4 + idBytes.Length + 4 + replyBytes.Length + timestampSize;
        var buffer = new byte[total];
        var offset = 0;
        WriteLengthPrefixed(buffer, ref offset, fromBytes);
        WriteLengthPrefixed(buffer, ref offset, toBytes);
        WriteLengthPrefixed(buffer, ref offset, idBytes);
        WriteLengthPrefixed(buffer, ref offset, replyBytes);
        WriteOptionalTimestamp(buffer, ref offset, sentAtUtc);
        return buffer;
    }

    private static byte[] BuildSigningInput(
        BrainId from,
        BrainId to,
        string messageId,
        string? replyToMessageId,
        DateTimeOffset? sentAtUtc,
        byte[] ephemeralPublicKey,
        byte[] nonce,
        byte[] tag,
        byte[] ciphertext)
    {
        var fromBytes = Encoding.UTF8.GetBytes(from.Value ?? string.Empty);
        var toBytes = Encoding.UTF8.GetBytes(to.Value ?? string.Empty);
        var idBytes = Encoding.UTF8.GetBytes(messageId);
        var replyBytes = replyToMessageId is null ? [] : Encoding.UTF8.GetBytes(replyToMessageId);
        var timestampSize = sentAtUtc.HasValue ? 9 : 1;

        var total =
            4 + fromBytes.Length +
            4 + toBytes.Length +
            4 + idBytes.Length +
            4 + replyBytes.Length +
            timestampSize +
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
        WriteOptionalTimestamp(buffer, ref offset, sentAtUtc);
        WriteLengthPrefixed(buffer, ref offset, ephemeralPublicKey);
        WriteLengthPrefixed(buffer, ref offset, nonce);
        WriteLengthPrefixed(buffer, ref offset, tag);
        WriteLengthPrefixed(buffer, ref offset, ciphertext);
        return buffer;
    }

    private static void WriteOptionalTimestamp(byte[] destination, ref int offset, DateTimeOffset? sentAtUtc)
    {
        if (sentAtUtc.HasValue)
        {
            destination[offset++] = 1;
            BinaryPrimitives.WriteInt64BigEndian(destination.AsSpan(offset, 8), sentAtUtc.Value.UtcTicks);
            offset += 8;
        }
        else
        {
            destination[offset++] = 0;
        }
    }

    private static void WriteLengthPrefixed(byte[] destination, ref int offset, ReadOnlySpan<byte> value)
    {
        BinaryPrimitives.WriteInt32BigEndian(destination.AsSpan(offset, 4), value.Length);
        offset += 4;
        value.CopyTo(destination.AsSpan(offset));
        offset += value.Length;
    }
}
