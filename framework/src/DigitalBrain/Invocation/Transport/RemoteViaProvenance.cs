using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DigitalBrain.Invocation;

/// <summary>
/// Cryptographically signed provenance attesting that <see cref="Via"/> in <see cref="SenderBrain"/>
/// initiated a cross-brain relay call to <see cref="Target"/> on behalf of <see cref="CallerAccount"/>.
/// Carried strictly inside the E2EE <see cref="RemoteInvocationRequest"/> ciphertext (zero plaintext relay headers).
/// </summary>
public sealed record RemoteViaProvenance(
    NeuronId Via,
    BrainId SenderBrain,
    NeuronId Target,
    string CallerAccount,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    byte[] Signature);

/// <summary>
/// Signs and verifies <see cref="RemoteViaProvenance"/> attestations with NIST P-256 ECDSA over a length-prefixed
/// domain-separated canonical layout bound to sender brain, Via neuron, target neuron, caller account, and expiry.
/// </summary>
public static class RemoteViaProvenanceSigner
{
    /// <summary>Maximum permitted lifetime for a signed <see cref="RemoteViaProvenance"/> attestation (10 minutes).</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(10);

    private static ReadOnlySpan<byte> DomainSeparator => "DigitalBrain.RemoteViaProvenance.v1"u8;

    /// <summary>Signs a <see cref="RemoteViaProvenance"/> using an in-memory <see cref="BrainKeyPair"/>.</summary>
    public static RemoteViaProvenance Sign(
        NeuronId via,
        BrainId senderBrain,
        NeuronId target,
        string callerAccount,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc,
        BrainKeyPair keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ValidateSignArgs(via, senderBrain, callerAccount, issuedAtUtc, expiresAtUtc);
        var canonical = BuildCanonicalBytes(via, senderBrain, target, callerAccount, issuedAtUtc, expiresAtUtc);
        var signature = keys.SigningKey.SignData(canonical, HashAlgorithmName.SHA256);
        return new RemoteViaProvenance(via, senderBrain, target, callerAccount, issuedAtUtc.ToUniversalTime(), expiresAtUtc.ToUniversalTime(), signature);
    }

    /// <summary>Signs a <see cref="RemoteViaProvenance"/> inside the non-exportable <see cref="IBrainKeySigner"/> boundary.</summary>
    public static async ValueTask<RemoteViaProvenance> SignAsync(
        NeuronId via,
        BrainId senderBrain,
        NeuronId target,
        string callerAccount,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc,
        IBrainKeySigner signer,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signer);
        ValidateSignArgs(via, senderBrain, callerAccount, issuedAtUtc, expiresAtUtc);
        var canonical = BuildCanonicalBytes(via, senderBrain, target, callerAccount, issuedAtUtc, expiresAtUtc);
        var signature = await signer.SignDataAsync(senderBrain, canonical, ct).ConfigureAwait(false);
        return new RemoteViaProvenance(via, senderBrain, target, callerAccount, issuedAtUtc.ToUniversalTime(), expiresAtUtc.ToUniversalTime(), signature);
    }

    /// <summary>
    /// Verifies that <paramref name="provenance"/> is cryptographically valid under <paramref name="expectedSenderPublicKeys"/>,
    /// bound to <paramref name="expectedSenderBrain"/>, <paramref name="expectedTarget"/>, and <paramref name="expectedCallerAccount"/>,
    /// has <see cref="RemoteViaProvenance.Via"/> hosted in <paramref name="expectedSenderBrain"/>, and is within its freshness window.
    /// </summary>
    public static bool Verify(
        RemoteViaProvenance? provenance,
        BrainId expectedSenderBrain,
        NeuronId expectedTarget,
        string expectedCallerAccount,
        BrainPublicKeys expectedSenderPublicKeys,
        DateTimeOffset nowUtc,
        TimeSpan maxClockSkew)
    {
        if (provenance is null
            || expectedSenderPublicKeys is null
            || string.IsNullOrWhiteSpace(expectedSenderBrain.Value)
            || string.IsNullOrWhiteSpace(expectedCallerAccount)
            || provenance.SenderBrain != expectedSenderBrain
            || provenance.Via.Brain != expectedSenderBrain
            || string.IsNullOrWhiteSpace(provenance.Via.Type)
            || string.IsNullOrWhiteSpace(provenance.Via.Key)
            || provenance.Target != expectedTarget
            || !string.Equals(provenance.CallerAccount, expectedCallerAccount, StringComparison.Ordinal)
            || provenance.Signature is not { Length: > 0 })
        {
            return false;
        }

        var issued = provenance.IssuedAtUtc.ToUniversalTime();
        var expires = provenance.ExpiresAtUtc.ToUniversalTime();
        var now = nowUtc.ToUniversalTime();
        var lifetime = expires - issued;
        if (lifetime <= TimeSpan.Zero || lifetime > MaxLifetime)
        {
            return false;
        }

        if (expires <= now || issued > now + maxClockSkew)
        {
            return false;
        }

        try
        {
            BrainPublicKeyValidation.ValidateNistP256SigningKey(expectedSenderPublicKeys.SigningKey);
            var canonical = BuildCanonicalBytes(provenance.Via, provenance.SenderBrain, provenance.Target, provenance.CallerAccount, issued, expires);
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(expectedSenderPublicKeys.SigningKey, out _);
            return verifier.VerifyData(canonical, provenance.Signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static void ValidateSignArgs(
        NeuronId via,
        BrainId senderBrain,
        string callerAccount,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        if (via.Brain != senderBrain)
        {
            throw new UnauthorizedAccessException($"Cannot attest Via neuron '{via}' outside sender brain '{senderBrain.Value}'.");
        }

        if (string.IsNullOrWhiteSpace(via.Type) || string.IsNullOrWhiteSpace(via.Key))
        {
            throw new ArgumentException("Via neuron must have a non-empty type and key.", nameof(via));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(callerAccount);

        var lifetime = expiresAtUtc - issuedAtUtc;
        if (lifetime <= TimeSpan.Zero || lifetime > MaxLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAtUtc), $"Via provenance lifetime must be between 1 tick and {MaxLifetime}.");
        }
    }

    private static byte[] BuildCanonicalBytes(
        NeuronId via,
        BrainId senderBrain,
        NeuronId target,
        string callerAccount,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        var viaBytes = Encoding.UTF8.GetBytes(via.ToString());
        var senderBytes = Encoding.UTF8.GetBytes(senderBrain.Value ?? string.Empty);
        var targetBytes = Encoding.UTF8.GetBytes(target.ToString());
        var accountBytes = Encoding.UTF8.GetBytes(callerAccount);

        var total =
            4 + DomainSeparator.Length +
            4 + viaBytes.Length +
            4 + senderBytes.Length +
            4 + targetBytes.Length +
            4 + accountBytes.Length +
            8 + 8;

        var buffer = new byte[total];
        var offset = 0;
        BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, DomainSeparator);
        BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, viaBytes);
        BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, senderBytes);
        BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, targetBytes);
        BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, accountBytes);
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(offset, 8), issuedAtUtc.ToUniversalTime().ToUnixTimeMilliseconds());
        offset += 8;
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(offset, 8), expiresAtUtc.ToUniversalTime().ToUnixTimeMilliseconds());
        return buffer;
    }
}
