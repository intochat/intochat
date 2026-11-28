using System.Security.Cryptography;

namespace DigitalBrain.Invocation;

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
