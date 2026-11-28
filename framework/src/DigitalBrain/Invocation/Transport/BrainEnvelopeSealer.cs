using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DigitalBrain.Invocation;

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
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(offset, 8), sentAtUtc?.ToUniversalTime().UtcTicks ?? 0L);
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
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(offset, 8), sentAtUtc?.ToUniversalTime().UtcTicks ?? 0L);
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
