using System.Security.Cryptography;
using DigitalBrain.Capabilities.Connections.Sealing;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Identity.Local.Custody;

namespace DigitalBrain.Identity.Local.Vault;

/// <summary>
/// Read-only lookup for legacy per-brain storage keys (<c>brain.key</c>) used strictly during
/// <c>brain:</c> dual-open migration. Implementations must never create a key or directory on miss.
/// </summary>
public interface ILegacyBrainKeyReader
{
    /// <summary>Returns the existing storage key for <paramref name="brainId"/>, or <see langword="null"/> if absent.</summary>
    Task<byte[]?> TryGetExistingAsync(string brainId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Seals and opens connection and brain-scoped secrets using <see cref="IMasterKeyCustody"/>.
/// Brain-scoped writes emit the <c>brain2:</c> wire prefix; <see cref="Open(string, SealedSecret)"/>
/// transparently dual-opens legacy <c>brain:</c> envelopes (both <see cref="IMasterKeyCustody"/> and
/// read-only <see cref="ILegacyBrainKeyReader"/> formats) without ever creating phantom keys.
/// </summary>
public sealed class LocalSecretSealer(
    IMasterKeyCustody custody,
    MasterKeyWrapper? keyWrapper = null,
    ILegacyBrainKeyReader? legacyBrainKeys = null) : ISecretSealer, IBrainSecretSealer
{
    private const string BrainSecretContext = "brain-secret";
    private const string LegacyV1Marker = "v1:";

    private readonly IMasterKeyCustody _custody = custody ?? throw new ArgumentNullException(nameof(custody));
    private readonly MasterKeyWrapper _keys = keyWrapper ?? new MasterKeyWrapper(custody ?? throw new ArgumentNullException(nameof(custody)));
    private readonly ILegacyBrainKeyReader? _legacyBrainKeys = legacyBrainKeys;

    /// <inheritdoc />
    public SealedSecret Seal(string name, string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        var key = SecretCipher.NewKey();
        return new SealedSecret
        {
            KeyId = _keys.Wrap(key),
            Cipher = SecretCipher.Seal(key, plaintext),
        };
    }

    /// <inheritdoc />
    public string Open(SealedSecret secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return SecretCipher.Open(_keys.Unwrap(secret.KeyId), secret.Cipher);
    }

    /// <inheritdoc />
    public Task<SealedSecret> Seal(string brainId, string name, string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        var dataKey = SecretCipher.NewKey();
        var wrappedKey = _custody.Wrap(brainId, BrainSecretContext, dataKey);
        return Task.FromResult(new SealedSecret
        {
            KeyId = BrainSeal.KeyPrefixV2 + Convert.ToBase64String(wrappedKey),
            Cipher = SecretCipher.Seal(dataKey, plaintext),
        });
    }

    /// <inheritdoc />
    public async Task<string> Open(string brainId, SealedSecret secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentNullException.ThrowIfNull(secret);
        if (!BrainSeal.IsBrainSealed(secret))
        {
            throw new ArgumentException("A brain-sealed secret is required.", nameof(secret));
        }

        if (secret.KeyId.StartsWith(BrainSeal.KeyPrefixV2, StringComparison.Ordinal))
        {
            var wrappedKey = Convert.FromBase64String(secret.KeyId[BrainSeal.KeyPrefixV2.Length..]);
            var dataKey = _custody.Unwrap(brainId, BrainSecretContext, wrappedKey);
            return SecretCipher.Open(dataKey, secret.Cipher);
        }

        // Legacy "brain:" dual-open path (never creates a phantom brain.key on unwrap).
        var payload = secret.KeyId[BrainSeal.KeyPrefix.Length..];
        if (payload.StartsWith(LegacyV1Marker, StringComparison.Ordinal))
        {
            return await OpenWithLegacyBrainKeyAsync(brainId, payload[LegacyV1Marker.Length..], secret.Cipher).ConfigureAwait(false);
        }

        try
        {
            var wrappedKey = Convert.FromBase64String(payload);
            var dataKey = _custody.Unwrap(brainId, BrainSecretContext, wrappedKey);
            return SecretCipher.Open(dataKey, secret.Cipher);
        }
        catch (CryptographicException) when (_legacyBrainKeys is not null)
        {
            return await OpenWithLegacyBrainKeyAsync(brainId, payload, secret.Cipher).ConfigureAwait(false);
        }
    }

    private async Task<string> OpenWithLegacyBrainKeyAsync(string brainId, string sealedDataKey, string cipher)
    {
        if (_legacyBrainKeys is null)
        {
            throw new CryptographicException("No legacy brain key reader is configured to open this legacy brain-sealed secret.");
        }

        var brainKey = await _legacyBrainKeys.TryGetExistingAsync(brainId).ConfigureAwait(false)
            ?? throw new CryptographicException($"Legacy brain storage key for '{brainId}' does not exist.");
        var dataKey = Convert.FromBase64String(SecretCipher.Open(brainKey, sealedDataKey));
        return SecretCipher.Open(dataKey, cipher);
    }
}
