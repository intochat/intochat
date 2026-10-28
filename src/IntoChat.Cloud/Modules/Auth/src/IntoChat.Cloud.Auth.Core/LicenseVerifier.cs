using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IntoChat.Cloud.Auth.Core;

public static class LicenseFailure
{
    public const string Missing = "missing";
    public const string Invalid = "invalid";
    public const string Expired = "expired";
}

public sealed record LicenseVerification(bool Ok, string? Reason, string? Org, string? LicenseId, DateTimeOffset? ExpiresAt)
{
    internal static LicenseVerification Fail(string reason) => new(false, reason, null, null, null);
}

public sealed class LicenseVerifier : IDisposable
{
    private const string P256Oid = "1.2.840.10045.3.1.7";
    private const string Issuer = "intochat";
    private const string Product = "auth";
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, ECDsa> _keys = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    internal LicenseVerifier(IEnumerable<(string Kid, string SpkiBase64)> keys, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        foreach (var (kid, spki) in keys)
        {
            var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spki), out _);
            if (ecdsa.ExportParameters(false).Curve.Oid.Value != P256Oid)
            {
                ecdsa.Dispose();
                throw new ArgumentException($"Licence key '{kid}' is not a P-256 key.");
            }

            _keys[kid] = ecdsa;
        }
    }

    public static LicenseVerifier WithEmbeddedKeys(TimeProvider? timeProvider = null) =>
        new(LicenseKeys.All, timeProvider ?? TimeProvider.System);

    public LicenseVerification Verify(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return LicenseVerification.Fail(LicenseFailure.Missing);
        }

        try
        {
            return VerifyCore(token.Trim());
        }
        // The same five failures as before, caught by type (an os rule: no broad catch without logging).
        catch (FormatException) { return LicenseVerification.Fail(LicenseFailure.Invalid); }
        catch (JsonException) { return LicenseVerification.Fail(LicenseFailure.Invalid); }
        catch (CryptographicException) { return LicenseVerification.Fail(LicenseFailure.Invalid); }
        catch (InvalidOperationException) { return LicenseVerification.Fail(LicenseFailure.Invalid); }
        catch (KeyNotFoundException) { return LicenseVerification.Fail(LicenseFailure.Invalid); }
    }

    public void Dispose()
    {
        foreach (var key in _keys.Values)
        {
            key.Dispose();
        }
    }

    private LicenseVerification VerifyCore(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0))
        {
            return LicenseVerification.Fail(LicenseFailure.Invalid);
        }

        var headerBytes = DecodeBase64Url(parts[0]);
        var payloadBytes = DecodeBase64Url(parts[1]);
        var signature = DecodeBase64Url(parts[2]);
        if (headerBytes is null || payloadBytes is null || signature is null || signature.Length != 64)
        {
            return LicenseVerification.Fail(LicenseFailure.Invalid);
        }

        if (!TryReadKid(headerBytes, out var kid) || !_keys.TryGetValue(kid, out var key))
        {
            return LicenseVerification.Fail(LicenseFailure.Invalid);
        }

        var signedData = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        if (!key.VerifyData(signedData, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            return LicenseVerification.Fail(LicenseFailure.Invalid);
        }

        return EvaluateClaims(payloadBytes);
    }

    private LicenseVerification EvaluateClaims(byte[] payloadBytes)
    {
        using var document = JsonDocument.Parse(payloadBytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || ReadString(root, "iss") != Issuer
            || ReadString(root, "sub") is not { Length: > 0 } licenseId
            || ReadString(root, "org") is not { Length: > 0 } org
            || !HasProduct(root)
            || !TryReadUnix(root, "exp", out var expires))
        {
            return LicenseVerification.Fail(LicenseFailure.Invalid);
        }

        var now = _timeProvider.GetUtcNow();
        if (root.TryGetProperty("nbf", out _))
        {
            if (!TryReadUnix(root, "nbf", out var notBefore) || notBefore > now + ClockSkew)
            {
                return LicenseVerification.Fail(LicenseFailure.Invalid);
            }
        }

        return now > expires + ClockSkew
            ? LicenseVerification.Fail(LicenseFailure.Expired)
            : new LicenseVerification(true, null, org, licenseId, expires);
    }

    private static bool TryReadKid(byte[] headerBytes, out string kid)
    {
        kid = string.Empty;
        using var header = JsonDocument.Parse(headerBytes);
        if (header.RootElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? alg = null;
        string? foundKid = null;
        foreach (var property in header.RootElement.EnumerateObject())
        {
            switch (property.Name)
            {
                case "alg":
                    alg = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                    break;
                case "kid":
                    foundKid = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                    break;
                case "typ":
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        return false;
                    }

                    break;
                default:
                    return false;
            }
        }

        if (alg != "ES256" || string.IsNullOrEmpty(foundKid))
        {
            return false;
        }

        kid = foundKid;
        return true;
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool HasProduct(JsonElement root) =>
        root.TryGetProperty("products", out var products)
        && products.ValueKind == JsonValueKind.Array
        && products.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == Product);

    private static bool TryReadUnix(JsonElement root, string name, out DateTimeOffset value)
    {
        value = default;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var seconds))
        {
            return false;
        }

        if (seconds is < 0 or > 253_402_300_799)
        {
            return false;
        }

        value = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return true;
    }

    private static byte[]? DecodeBase64Url(string segment)
    {
        foreach (var c in segment)
        {
            var valid = char.IsAsciiLetterOrDigit(c) || c is '-' or '_';
            if (!valid)
            {
                return null;
            }
        }

        var padded = segment.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            0 => padded,
            2 => padded + "==",
            3 => padded + "=",
            _ => string.Empty,
        };
        if (padded.Length == 0)
        {
            return null;
        }

        return Convert.TryFromBase64String(padded, new byte[padded.Length], out _)
            ? Convert.FromBase64String(padded)
            : null;
    }
}