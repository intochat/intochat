using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DigitalBrain.Invocation;

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
