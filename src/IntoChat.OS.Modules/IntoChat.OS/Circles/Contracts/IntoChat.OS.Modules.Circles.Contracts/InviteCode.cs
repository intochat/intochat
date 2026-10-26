using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace IntoChat.OS.Modules.Circles;

// D-03: a circle invite code is 12 Crockford base32 characters (60 bits), shown once as ABCD-EFGH-JKMN. Only an
// HMAC-SHA256 of the normalized code is ever stored (D-10).
public static class InviteCode
{
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int Length = 12;
    public static readonly TimeSpan MinValidity = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaxValidity = TimeSpan.FromDays(7);

    public static string Generate()
    {
        Span<char> chars = stackalloc char[Length];
        for (var i = 0; i < Length; i++) { chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]; }
        return Format(chars);
    }

    public static TimeSpan Clamp(TimeSpan validFor)
        => validFor < MinValidity ? MinValidity : validFor > MaxValidity ? MaxValidity : validFor;

    // Accepts any case, missing dashes and surrounding spaces; I and L read as 1, O as 0. Returns the dashed form.
    public static bool TryNormalize(string? input, [NotNullWhen(true)] out string? code)
    {
        code = null;
        if (input is null) { return false; }
        Span<char> chars = stackalloc char[Length];
        var count = 0;
        foreach (var raw in input.Trim())
        {
            if (raw == '-') { continue; }
            var c = char.ToUpperInvariant(raw) switch { 'I' or 'L' => '1', 'O' => '0', var other => other };
            if (Alphabet.IndexOf(c) < 0 || count == Length) { return false; }
            chars[count++] = c;
        }
        if (count != Length) { return false; }
        code = Format(chars);
        return true;
    }

    // Base64url HMAC-SHA256 of the normalized code; throws for a malformed code.
    public static string Hash(string code, ReadOnlySpan<byte> key)
    {
        if (!TryNormalize(code, out var normalized)) { throw new FormatException("Not an invite code."); }
        var mac = HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(normalized.Replace("-", "", StringComparison.Ordinal)));
        return Base64Url(mac);
    }

    public static bool HashesEqual(string? left, string? right)
        => left is not null && right is not null
           && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));

    // Base64url SHA-256 of a SubjectPublicKeyInfo: the machine key thumbprint an invite binds.
    public static string Thumbprint(ReadOnlySpan<byte> subjectPublicKeyInfo) => Base64Url(SHA256.HashData(subjectPublicKeyInfo));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Format(ReadOnlySpan<char> chars)
        => new StringBuilder(14).Append(chars[..4]).Append('-').Append(chars[4..8]).Append('-').Append(chars[8..]).ToString();
}
