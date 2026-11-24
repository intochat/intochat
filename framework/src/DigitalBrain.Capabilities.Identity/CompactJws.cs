using System.Security.Cryptography;
using System.Text;

namespace DigitalBrain.Capabilities.Identity;

// The compact JWS the issuer tokens share: base64url (no padding) header and payload, ES256 over "header.payload" as a 64-byte
// IEEE P1363 signature. Each token type names its own "typ", so one type is never accepted as another.
internal static class CompactJws
{
    public static string Sign(byte[] header, byte[] payload, ECDsa key)
    {
        var body = Encode(header) + "." + Encode(payload);
        return body + "." + Encode(key.SignData(Encoding.ASCII.GetBytes(body), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    // The three parts of a token within the size cap, or null.
    public static string[]? Split(string? token, int maxLength)
    {
        if (string.IsNullOrEmpty(token) || token.Length > maxLength) { return null; }
        var parts = token.Split('.');
        return parts.Length != 3 || parts.Any(part => part.Length == 0) ? null : parts;
    }

    public static bool SignatureHolds(string[] parts, ECDsa key)
    {
        var signature = Decode(parts[2]);
        return signature.Length == 64 &&
            key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public static string Encode(ReadOnlySpan<byte> value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string value)
    {
        if (value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_') || value.Length % 4 == 1)
        { throw new FormatException("Invalid base64url."); }
        return Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
    }
}
