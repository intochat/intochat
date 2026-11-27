using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DigitalBrain.Invocation;

/// <summary>
/// End-to-end encrypted media/attachment blob sealer using per-blob random AES-256-GCM content keys,
/// per-recipient P-256 ECDH + HKDF-SHA256 + AES-256-GCM key wrapping, and P-256 ECDSA sender authentication.
/// Reuses <see cref="IBrainKeySigner"/>, <see cref="BrainKeyPair"/>, <see cref="BrainPublicKeys"/>,
/// and <see cref="BrainPublicKeyValidation"/> without exposing raw private keys.
/// </summary>
public static class BrainBlobSealer
{
    private const byte FormatVersion = 1;
    private const int ContentKeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int Sha256Size = 32;

    private static ReadOnlySpan<byte> Magic => "DBLB"u8;
    private static ReadOnlySpan<byte> ContentDomain => "DigitalBrain.BrainBlob.v1"u8;
    private static ReadOnlySpan<byte> KeyWrapInfo => "DigitalBrain.BrainBlobKeyWrap.v1"u8;

    /// <summary>
    /// Seals <paramref name="plaintext"/> for 1..64 <paramref name="recipients"/> using <paramref name="senderKeys"/>.
    /// </summary>
    public static byte[] Seal(
        BrainId senderBrain,
        BrainKeyPair senderKeys,
        ReadOnlySpan<byte> plaintext,
        IReadOnlyList<(BrainId BrainId, BrainPublicKeys PublicKeys)> recipients)
    {
        ArgumentNullException.ThrowIfNull(senderKeys);
        return SealCore(
            senderBrain,
            plaintext,
            recipients,
            signingInput => senderKeys.SigningKey.SignData(signingInput, HashAlgorithmName.SHA256));
    }

    /// <summary>
    /// Seals <paramref name="plaintext"/> for 1..64 <paramref name="recipients"/> using a non-exportable <see cref="IBrainKeySigner"/>.
    /// </summary>
    public static async ValueTask<byte[]> SealAsync(
        BrainId senderBrain,
        IBrainKeySigner senderSigner,
        ReadOnlyMemory<byte> plaintext,
        IReadOnlyList<(BrainId BrainId, BrainPublicKeys PublicKeys)> recipients,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(senderSigner);

        var (headerBeforeSig, ciphertext) = PrepareSealedBlob(senderBrain, plaintext.Span, recipients);
        var signature = await senderSigner.SignDataAsync(senderBrain, headerBeforeSig, ct).ConfigureAwait(false);
        return AssembleContainer(headerBeforeSig, signature, ciphertext);
    }

    /// <summary>
    /// Verifies the sender's ECDSA signature and ciphertext SHA-256 FIRST, unwraps the per-blob content key
    /// for <paramref name="recipientBrain"/>, and decrypts the blob using <paramref name="recipientKeys"/>.
    /// </summary>
    public static byte[] Unseal(
        ReadOnlySpan<byte> sealedBlob,
        BrainId recipientBrain,
        BrainKeyPair recipientKeys,
        BrainPublicKeys expectedSenderPublicKeys)
    {
        ArgumentNullException.ThrowIfNull(recipientKeys);
        ArgumentNullException.ThrowIfNull(expectedSenderPublicKeys);

        var parsed = ParseAndVerify(sealedBlob, recipientBrain, expectedSenderPublicKeys);

        using var ephemeralPublic = ECDiffieHellman.Create();
        ephemeralPublic.ImportSubjectPublicKeyInfo(parsed.RecipientEntry.EphemeralPublicKeySpki, out _);

        byte[]? sharedSecret = null;
        try
        {
            sharedSecret = recipientKeys.ExchangeKey.DeriveRawSecretAgreement(ephemeralPublic.PublicKey);
            return UnwrapAndDecrypt(parsed, recipientBrain, sharedSecret);
        }
        finally
        {
            if (sharedSecret is not null)
            {
                CryptographicOperations.ZeroMemory(sharedSecret);
            }
        }
    }

    /// <summary>
    /// Verifies the sender's ECDSA signature and ciphertext SHA-256 FIRST, unwraps the per-blob content key
    /// inside the non-exportable <see cref="IBrainKeySigner"/> boundary, and decrypts the blob.
    /// </summary>
    public static async ValueTask<byte[]> UnsealAsync(
        ReadOnlyMemory<byte> sealedBlob,
        BrainId recipientBrain,
        IBrainKeySigner recipientSigner,
        BrainPublicKeys expectedSenderPublicKeys,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recipientSigner);
        ArgumentNullException.ThrowIfNull(expectedSenderPublicKeys);

        var parsed = ParseAndVerify(sealedBlob.Span, recipientBrain, expectedSenderPublicKeys);

        byte[]? sharedSecret = null;
        try
        {
            sharedSecret = await recipientSigner
                .DeriveSharedSecretAsync(recipientBrain, parsed.RecipientEntry.EphemeralPublicKeySpki, ct)
                .ConfigureAwait(false);
            return UnwrapAndDecrypt(parsed, recipientBrain, sharedSecret);
        }
        finally
        {
            if (sharedSecret is not null)
            {
                CryptographicOperations.ZeroMemory(sharedSecret);
            }
        }
    }

    private static byte[] SealCore(
        BrainId senderBrain,
        ReadOnlySpan<byte> plaintext,
        IReadOnlyList<(BrainId BrainId, BrainPublicKeys PublicKeys)> recipients,
        Func<byte[], byte[]> sign)
    {
        var (headerBeforeSig, ciphertext) = PrepareSealedBlob(senderBrain, plaintext, recipients);
        var signature = sign(headerBeforeSig);
        return AssembleContainer(headerBeforeSig, signature, ciphertext);
    }

    private static (byte[] HeaderBeforeSig, byte[] Ciphertext) PrepareSealedBlob(
        BrainId senderBrain,
        ReadOnlySpan<byte> plaintext,
        IReadOnlyList<(BrainId BrainId, BrainPublicKeys PublicKeys)> recipients)
    {
        if (string.IsNullOrWhiteSpace(senderBrain.Value))
        {
            throw new ArgumentException("Sender BrainId must be non-empty.", nameof(senderBrain));
        }

        ArgumentNullException.ThrowIfNull(recipients);
        if (recipients.Count == 0 || recipients.Count > BrainBlobLimits.MaxRecipients)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recipients),
                recipients.Count,
                $"Recipients count must be between 1 and {BrainBlobLimits.MaxRecipients}.");
        }

        if (plaintext.Length > BrainBlobLimits.MaxBodyBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(plaintext),
                plaintext.Length,
                $"Plaintext exceeds maximum blob size of {BrainBlobLimits.MaxBodyBytes} bytes.");
        }

        var orderedRecipients = new List<(string BrainId, BrainPublicKeys PublicKeys)>(recipients.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (brainId, publicKeys) in recipients)
        {
            if (string.IsNullOrWhiteSpace(brainId.Value) || brainId.Value.Length > BrainBlobLimits.MaxRecipientBrainIdLength)
            {
                throw new ArgumentException("Each recipient BrainId must be non-empty and within length limits.", nameof(recipients));
            }

            if (!seen.Add(brainId.Value))
            {
                throw new ArgumentException($"Duplicate recipient BrainId '{brainId.Value}'.", nameof(recipients));
            }

            ArgumentNullException.ThrowIfNull(publicKeys);
            BrainPublicKeyValidation.ValidateNistP256ExchangeKey(publicKeys.ExchangeKey);
            orderedRecipients.Add((brainId.Value, publicKeys));
        }

        orderedRecipients.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.BrainId, b.BrainId));

        var contentKey = new byte[ContentKeySize];
        var contentNonce = new byte[NonceSize];
        var contentTag = new byte[TagSize];
        var ciphertext = new byte[plaintext.Length];
        var wrappedEntries = new List<WrappedRecipientEntry>(orderedRecipients.Count);

        try
        {
            RandomNumberGenerator.Fill(contentKey);
            RandomNumberGenerator.Fill(contentNonce);

            var contentAad = BuildContentAad(senderBrain.Value, contentNonce);
            using (var aes = new AesGcm(contentKey, TagSize))
            {
                aes.Encrypt(contentNonce, plaintext, ciphertext, contentTag, contentAad);
            }

            foreach (var (recipientId, publicKeys) in orderedRecipients)
            {
                wrappedEntries.Add(WrapContentKeyForRecipient(
                    senderBrain.Value,
                    recipientId,
                    publicKeys.ExchangeKey,
                    contentNonce,
                    contentKey));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contentKey);
        }

        var ciphertextSha256 = SHA256.HashData(ciphertext);
        var headerBeforeSig = SerializeHeaderPrefix(
            senderBrain.Value,
            contentNonce,
            contentTag,
            ciphertextSha256,
            wrappedEntries);

        return (headerBeforeSig, ciphertext);
    }

    private static WrappedRecipientEntry WrapContentKeyForRecipient(
        string senderBrainId,
        string recipientBrainId,
        byte[] recipientExchangeSpki,
        byte[] contentNonce,
        byte[] contentKey)
    {
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var recipientExchange = ECDiffieHellman.Create();
        recipientExchange.ImportSubjectPublicKeyInfo(recipientExchangeSpki, out _);

        var ephemeralSpki = ephemeral.ExportSubjectPublicKeyInfo();
        var wrapNonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(wrapNonce);
        var wrapTag = new byte[TagSize];
        var wrappedKey = new byte[ContentKeySize];
        var wrapAad = BuildWrapAad(senderBrainId, recipientBrainId, contentNonce);

        byte[]? sharedSecret = null;
        var wrapKey = new byte[ContentKeySize];
        try
        {
            sharedSecret = ephemeral.DeriveRawSecretAgreement(recipientExchange.PublicKey);
            HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, wrapKey, salt: wrapNonce, info: KeyWrapInfo);

            using var aes = new AesGcm(wrapKey, TagSize);
            aes.Encrypt(wrapNonce, contentKey, wrappedKey, wrapTag, wrapAad);
        }
        finally
        {
            if (sharedSecret is not null)
            {
                CryptographicOperations.ZeroMemory(sharedSecret);
            }

            CryptographicOperations.ZeroMemory(wrapKey);
        }

        return new WrappedRecipientEntry(recipientBrainId, ephemeralSpki, wrapNonce, wrapTag, wrappedKey);
    }

    private static byte[] SerializeHeaderPrefix(
        string senderBrainId,
        byte[] contentNonce,
        byte[] contentTag,
        byte[] ciphertextSha256,
        List<WrappedRecipientEntry> recipients)
    {
        var senderBytes = Encoding.UTF8.GetBytes(senderBrainId);
        var total = Magic.Length + 1 + 4 + senderBytes.Length + NonceSize + TagSize + Sha256Size + 2;
        var recipientIdBytes = new byte[recipients.Count][];

        for (var i = 0; i < recipients.Count; i++)
        {
            var idBytes = Encoding.UTF8.GetBytes(recipients[i].RecipientBrainId);
            recipientIdBytes[i] = idBytes;
            total += 4 + idBytes.Length
                + 4 + recipients[i].EphemeralPublicKeySpki.Length
                + NonceSize
                + TagSize
                + ContentKeySize;
        }

        var buffer = new byte[total];
        var offset = 0;

        Magic.CopyTo(buffer.AsSpan(offset));
        offset += Magic.Length;
        buffer[offset++] = FormatVersion;

        BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, senderBytes);
        contentNonce.CopyTo(buffer.AsSpan(offset));
        offset += NonceSize;
        contentTag.CopyTo(buffer.AsSpan(offset));
        offset += TagSize;
        ciphertextSha256.CopyTo(buffer.AsSpan(offset));
        offset += Sha256Size;

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), (ushort)recipients.Count);
        offset += 2;

        for (var i = 0; i < recipients.Count; i++)
        {
            var entry = recipients[i];
            BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, recipientIdBytes[i]);
            BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, entry.EphemeralPublicKeySpki);
            entry.WrapNonce.CopyTo(buffer.AsSpan(offset));
            offset += NonceSize;
            entry.WrapTag.CopyTo(buffer.AsSpan(offset));
            offset += TagSize;
            entry.WrappedKey.CopyTo(buffer.AsSpan(offset));
            offset += ContentKeySize;
        }

        return buffer;
    }

    private static byte[] AssembleContainer(byte[] headerBeforeSig, byte[] signature, byte[] ciphertext)
    {
        var total = headerBeforeSig.Length + 4 + signature.Length + ciphertext.Length;
        if (total > BrainBlobLimits.MaxBodyBytes)
        {
            throw new InvalidOperationException(
                $"Sealed blob size ({total} bytes) exceeds maximum allowed relay size ({BrainBlobLimits.MaxBodyBytes} bytes).");
        }

        var result = new byte[total];
        var offset = 0;
        headerBeforeSig.CopyTo(result.AsSpan(offset));
        offset += headerBeforeSig.Length;
        BrainEnvelopeSealer.WriteLengthPrefixed(result, ref offset, signature);
        ciphertext.CopyTo(result.AsSpan(offset));
        return result;
    }

    private static ParsedBlob ParseAndVerify(
        ReadOnlySpan<byte> sealedBlob,
        BrainId recipientBrain,
        BrainPublicKeys expectedSenderPublicKeys)
    {
        if (string.IsNullOrWhiteSpace(recipientBrain.Value))
        {
            throw new ArgumentException("Recipient BrainId must be non-empty.", nameof(recipientBrain));
        }

        BrainPublicKeyValidation.ValidateNistP256SigningKey(expectedSenderPublicKeys.SigningKey);

        var minSize = Magic.Length + 1 + 4 + NonceSize + TagSize + Sha256Size + 2 + 4;
        if (sealedBlob.Length < minSize)
        {
            throw new CryptographicException("Sealed blob is truncated or malformed.");
        }

        var offset = 0;
        if (!sealedBlob.Slice(offset, Magic.Length).SequenceEqual(Magic))
        {
            throw new CryptographicException("Sealed blob has an unrecognized format header.");
        }

        offset += Magic.Length;
        var version = sealedBlob[offset++];
        if (version != FormatVersion)
        {
            throw new CryptographicException($"Unsupported sealed blob format version {version}.");
        }

        var senderBytes = ReadLengthPrefixed(sealedBlob, ref offset, BrainBlobLimits.MaxRecipientBrainIdLength * 4);
        var senderBrainId = Encoding.UTF8.GetString(senderBytes);
        if (string.IsNullOrWhiteSpace(senderBrainId))
        {
            throw new CryptographicException("Sealed blob sender BrainId is empty.");
        }

        var contentNonce = ReadFixed(sealedBlob, ref offset, NonceSize);
        var contentTag = ReadFixed(sealedBlob, ref offset, TagSize);
        var expectedCiphertextSha256 = ReadFixed(sealedBlob, ref offset, Sha256Size);

        if (offset + 2 > sealedBlob.Length)
        {
            throw new CryptographicException("Sealed blob recipient count is truncated.");
        }

        var recipientCount = BinaryPrimitives.ReadUInt16BigEndian(sealedBlob.Slice(offset, 2));
        offset += 2;
        if (recipientCount == 0 || recipientCount > BrainBlobLimits.MaxRecipients)
        {
            throw new CryptographicException($"Sealed blob recipient count {recipientCount} is out of bounds.");
        }

        WrappedRecipientEntry? matchedRecipient = null;
        for (var i = 0; i < recipientCount; i++)
        {
            var recipientIdSpan = ReadLengthPrefixed(sealedBlob, ref offset, BrainBlobLimits.MaxRecipientBrainIdLength * 4);
            var recipientId = Encoding.UTF8.GetString(recipientIdSpan);
            var ephemeralSpki = ReadLengthPrefixed(sealedBlob, ref offset, 512);
            var wrapNonce = ReadFixed(sealedBlob, ref offset, NonceSize);
            var wrapTag = ReadFixed(sealedBlob, ref offset, TagSize);
            var wrappedKey = ReadFixed(sealedBlob, ref offset, ContentKeySize);

            if (matchedRecipient is null && string.Equals(recipientId, recipientBrain.Value, StringComparison.Ordinal))
            {
                matchedRecipient = new WrappedRecipientEntry(
                    recipientId,
                    ephemeralSpki.ToArray(),
                    wrapNonce.ToArray(),
                    wrapTag.ToArray(),
                    wrappedKey.ToArray());
            }
        }

        var signedHeaderLength = offset;
        var signature = ReadLengthPrefixed(sealedBlob, ref offset, 512);
        var ciphertext = sealedBlob[offset..];

        // 1. Verify ciphertext digest matches signed header.
        Span<byte> actualCiphertextSha256 = stackalloc byte[Sha256Size];
        SHA256.HashData(ciphertext, actualCiphertextSha256);
        if (!CryptographicOperations.FixedTimeEquals(actualCiphertextSha256, expectedCiphertextSha256))
        {
            throw new CryptographicException("Sealed blob ciphertext integrity check failed.");
        }

        // 2. Verify sender's P-256 ECDSA signature FIRST before any key agreement or decryption.
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(expectedSenderPublicKeys.SigningKey, out _);
        if (!verifier.VerifyData(sealedBlob[..signedHeaderLength], signature, HashAlgorithmName.SHA256))
        {
            throw new CryptographicException("Sealed blob sender signature verification failed.");
        }

        if (matchedRecipient is null)
        {
            throw new CryptographicException($"Brain '{recipientBrain.Value}' is not an authorized recipient of this sealed blob.");
        }

        BrainPublicKeyValidation.ValidateNistP256ExchangeKey(matchedRecipient.EphemeralPublicKeySpki);

        return new ParsedBlob(
            senderBrainId,
            contentNonce.ToArray(),
            contentTag.ToArray(),
            matchedRecipient,
            ciphertext.ToArray());
    }

    private static byte[] UnwrapAndDecrypt(ParsedBlob parsed, BrainId recipientBrain, byte[] sharedSecret)
    {
        var wrapKey = new byte[ContentKeySize];
        var contentKey = new byte[ContentKeySize];
        var plaintext = new byte[parsed.Ciphertext.Length];

        try
        {
            HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, wrapKey, salt: parsed.RecipientEntry.WrapNonce, info: KeyWrapInfo);

            var wrapAad = BuildWrapAad(parsed.SenderBrainId, recipientBrain.Value, parsed.ContentNonce);
            using (var wrapAes = new AesGcm(wrapKey, TagSize))
            {
                wrapAes.Decrypt(
                    parsed.RecipientEntry.WrapNonce,
                    parsed.RecipientEntry.WrappedKey,
                    parsed.RecipientEntry.WrapTag,
                    contentKey,
                    wrapAad);
            }

            var contentAad = BuildContentAad(parsed.SenderBrainId, parsed.ContentNonce);
            using (var contentAes = new AesGcm(contentKey, TagSize))
            {
                contentAes.Decrypt(
                    parsed.ContentNonce,
                    parsed.Ciphertext,
                    parsed.ContentTag,
                    plaintext,
                    contentAad);
            }

            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapKey);
            CryptographicOperations.ZeroMemory(contentKey);
        }
    }

    private static byte[] BuildContentAad(string senderBrainId, byte[] contentNonce)
    {
        var senderBytes = Encoding.UTF8.GetBytes(senderBrainId);
        var buffer = new byte[4 + ContentDomain.Length + 4 + senderBytes.Length + contentNonce.Length];
        var offset = 0;
        BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, ContentDomain);
        BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, senderBytes);
        contentNonce.CopyTo(buffer.AsSpan(offset));
        return buffer;
    }

    private static byte[] BuildWrapAad(string senderBrainId, string recipientBrainId, byte[] contentNonce)
    {
        var senderBytes = Encoding.UTF8.GetBytes(senderBrainId);
        var recipientBytes = Encoding.UTF8.GetBytes(recipientBrainId);
        var buffer = new byte[4 + KeyWrapInfo.Length + 4 + senderBytes.Length + 4 + recipientBytes.Length + contentNonce.Length];
        var offset = 0;
        BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, KeyWrapInfo);
        BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, senderBytes);
        BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, recipientBytes);
        contentNonce.CopyTo(buffer.AsSpan(offset));
        return buffer;
    }

    private static ReadOnlySpan<byte> ReadLengthPrefixed(ReadOnlySpan<byte> source, ref int offset, int maxLength)
    {
        if (offset + 4 > source.Length)
        {
            throw new CryptographicException("Sealed blob field length prefix is truncated.");
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(source.Slice(offset, 4));
        offset += 4;
        if (length < 0 || length > maxLength || offset + length > source.Length)
        {
            throw new CryptographicException("Sealed blob field length is invalid or truncated.");
        }

        var slice = source.Slice(offset, length);
        offset += length;
        return slice;
    }

    private static ReadOnlySpan<byte> ReadFixed(ReadOnlySpan<byte> source, ref int offset, int length)
    {
        if (offset + length > source.Length)
        {
            throw new CryptographicException("Sealed blob fixed-length field is truncated.");
        }

        var slice = source.Slice(offset, length);
        offset += length;
        return slice;
    }

    private sealed record WrappedRecipientEntry(
        string RecipientBrainId,
        byte[] EphemeralPublicKeySpki,
        byte[] WrapNonce,
        byte[] WrapTag,
        byte[] WrappedKey);

    private sealed record ParsedBlob(
        string SenderBrainId,
        byte[] ContentNonce,
        byte[] ContentTag,
        WrappedRecipientEntry RecipientEntry,
        byte[] Ciphertext);
}
