using System.Security.Cryptography;
using DigitalBrain.Identity.Local.Custody;

namespace DigitalBrain.Identity.Local.Vault;

/// <summary>
/// Wraps and unwraps per-owner vault keys under the host master key.
/// New writes always emit the authenticated <c>mk4:</c> envelope through <see cref="IMasterKeyCustody"/>
/// (using <see cref="ConfiguredMasterKeyCustody"/> when constructed from raw master key bytes), guaranteeing
/// identical <c>mk4:</c> wire compatibility regardless of constructor.
/// <see cref="Unwrap"/> transparently dual-opens legacy <c>mk3:</c> envelopes produced by either
/// <see cref="IMasterKeyCustody"/> (<c>SegmentSeal</c> with AAD) or legacy raw AES-GCM without AAD.
/// </summary>
public sealed class MasterKeyWrapper
{
    /// <summary>Current wire-format prefix emitted by <see cref="Wrap"/>.</summary>
    public const string CurrentPrefix = "mk4:";

    /// <summary>Legacy wire-format prefix accepted by <see cref="Unwrap"/> for zero-wipe dual-open migration.</summary>
    public const string LegacyPrefix = "mk3:";

    private const string CustodyAccount = "local";
    private const string ContextNameV4 = "DigitalBrain.OwnerKeys.v4";
    private const string ContextNameV3 = "DigitalBrain.OwnerKeys.v3";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly IMasterKeyCustody _custody;
    private readonly byte[]? _legacyV3Key;

    // HKDF domain separator for legacy mk3: raw AES-GCM dual-open. Never rename: changing it makes existing sealed owner keys underivable.
    internal static ReadOnlySpan<byte> OwnerKeysContext => "DigitalBrain.OwnerKeys.v3"u8;

    /// <summary>
    /// Creates a wrapper backed by <paramref name="custody"/>, with an optional <paramref name="legacyMasterKey"/>
    /// used solely as a read-only fallback when unwrapping legacy raw-AES-GCM <c>mk3:</c> envelopes.
    /// </summary>
    public MasterKeyWrapper(IMasterKeyCustody custody, ReadOnlySpan<byte> legacyMasterKey = default)
    {
        ArgumentNullException.ThrowIfNull(custody);
        _custody = custody;
        if (!legacyMasterKey.IsEmpty)
        {
            _legacyV3Key = HKDF.DeriveKey(HashAlgorithmName.SHA256, legacyMasterKey.ToArray(), 32, info: OwnerKeysContext.ToArray());
        }
    }

    /// <summary>
    /// Creates a wrapper from a raw <paramref name="masterKey"/> backed by <see cref="ConfiguredMasterKeyCustody"/>,
    /// retaining legacy <c>mk3:</c> raw AES-GCM dual-open support.
    /// </summary>
    public MasterKeyWrapper(ReadOnlySpan<byte> masterKey)
    {
        if (masterKey.IsEmpty)
        {
            throw new ArgumentException("The master key cannot be empty.", nameof(masterKey));
        }

        var raw = masterKey.ToArray();
        _custody = new ConfiguredMasterKeyCustody(raw);
        _legacyV3Key = HKDF.DeriveKey(HashAlgorithmName.SHA256, raw, 32, info: OwnerKeysContext.ToArray());
    }

    /// <summary>Wraps <paramref name="key"/> into an <c>mk4:</c>-prefixed base64 envelope via <see cref="IMasterKeyCustody"/>.</summary>
    public string Wrap(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return CurrentPrefix + Convert.ToBase64String(_custody.Wrap(CustodyAccount, ContextNameV4, key));
    }

    /// <summary>
    /// Unwraps an <c>mk4:</c> or legacy <c>mk3:</c> base64 envelope back into the raw owner key.
    /// </summary>
    public byte[] Unwrap(string wrapped)
    {
        ArgumentNullException.ThrowIfNull(wrapped);
        if (wrapped.StartsWith(CurrentPrefix, StringComparison.Ordinal))
        {
            var payload = DecodeBase64(wrapped[CurrentPrefix.Length..]);
            return _custody.Unwrap(CustodyAccount, ContextNameV4, payload);
        }

        if (wrapped.StartsWith(LegacyPrefix, StringComparison.Ordinal))
        {
            var payload = DecodeBase64(wrapped[LegacyPrefix.Length..]);
            return UnwrapLegacyV3(payload);
        }

        throw new CryptographicException("Owner key is not wrapped by the configured master key (expected mk4: or mk3:).");
    }

    private byte[] UnwrapLegacyV3(byte[] payload)
    {
        try
        {
            return _custody.Unwrap(CustodyAccount, ContextNameV3, payload);
        }
        catch (CryptographicException) when (_legacyV3Key is not null)
        {
            // Fall through to legacy raw AES-GCM (no AAD) produced by ConfiguredKeyWrapper / raw MasterKeyWrapper.
        }

        if (payload.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Truncated master-key wrapped owner key.");
        }

        var key = new byte[payload.Length - NonceSize - TagSize];
        using var aes = new AesGcm(_legacyV3Key!, TagSize);
        aes.Decrypt(payload.AsSpan(0, NonceSize), payload.AsSpan(NonceSize + TagSize), payload.AsSpan(NonceSize, TagSize), key);
        return key;
    }

    private static byte[] DecodeBase64(string base64)
    {
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException error)
        {
            throw new CryptographicException("Malformed master-key wrapped owner key.", error);
        }
    }
}
