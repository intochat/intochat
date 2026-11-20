using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace IntoChat.OS.Authority.Identity;

// The presence-proof protocol other hosts speak: where the token travels, how it is configured, and the hash a
// launcher hands the kernel instead of the token. Verifying a token is the identity module's (PresenceProofValidator).
public static class PresenceProof
{
    public const string HeaderName = "X-IntoChat-Presence";
    public const string LegacyHeaderName = "X-Presence-Token";
    public const string CookieName = "IntoChatPresence";
    public const string ExchangePath = "/auth/local-presence";
    public const string ConfigurationKey = "IntoChatOS:Authority:PresenceToken";
    public const string TokenHashConfigurationKey = "IntoChatOS:Security:PresenceTokenHash";
    public const string TokenHashEnvironmentVariable = "IntoChatOS__Security__PresenceTokenHash";
    public const string TokenFileConfigurationKey = "IntoChatOS:Security:PresenceTokenFile";
    public const int SecretByteCount = 32;
    public const int MinimumUtf8SecretBytes = 16;
    public const int MaxCandidateChars = 512;

    public static string ComputeTokenHash(ReadOnlySpan<byte> rawSecretBytes)
    {
        if (rawSecretBytes.Length < MinimumUtf8SecretBytes)
        {
            throw new ArgumentException($"Presence secret must be at least {MinimumUtf8SecretBytes} bytes.", nameof(rawSecretBytes));
        }

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(rawSecretBytes, digest);
        return Convert.ToHexStringLower(digest);
    }

    public static string ComputeTokenHash(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var trimmed = token.AsSpan().Trim();
        if (trimmed.Length is 0 or > MaxCandidateChars)
        {
            throw new ArgumentException("Presence token length is out of bounds.", nameof(token));
        }

        Span<byte> extracted = stackalloc byte[Encoding.UTF8.GetMaxByteCount(trimmed.Length)];
        if (!TryExtractSecretBytes(trimmed, extracted, out var len))
        {
            throw new ArgumentException("Presence token does not contain valid secret material.", nameof(token));
        }

        return ComputeTokenHash(extracted[..len]);
    }

    // The secret a token carries: hex, Base64Url or Base64 of at least SecretByteCount bytes, else its UTF-8 bytes.
    public static bool TryExtractSecretBytes(ReadOnlySpan<char> text, Span<byte> destination, out int bytesWritten)
    {
        if (TryDecodeSecretBytes(text, destination, out bytesWritten))
        {
            return true;
        }

        var utf8ByteCount = Encoding.UTF8.GetByteCount(text);
        if (utf8ByteCount >= MinimumUtf8SecretBytes && utf8ByteCount <= destination.Length)
        {
            bytesWritten = Encoding.UTF8.GetBytes(text, destination);
            return true;
        }

        bytesWritten = 0;
        return false;
    }

    private static bool TryDecodeSecretBytes(ReadOnlySpan<char> text, Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = 0;

        // Hex (64+ hex chars -> >= 32 bytes)
        if (text.Length >= SecretByteCount * 2 && text.Length % 2 == 0 && IsHex(text))
        {
            var status = Convert.FromHexString(text, destination, out _, out bytesWritten);
            if (status == System.Buffers.OperationStatus.Done && bytesWritten >= SecretByteCount)
            {
                return true;
            }
        }

        // Base64Url
        if (Base64Url.IsValid(text))
        {
            if (Base64Url.TryDecodeFromChars(text, destination, out bytesWritten) && bytesWritten >= SecretByteCount)
            {
                return true;
            }
        }

        // Standard Base64
        if (Convert.TryFromBase64Chars(text, destination, out bytesWritten) && bytesWritten >= SecretByteCount)
        {
            return true;
        }

        bytesWritten = 0;
        return false;
    }

    public static bool IsHex(ReadOnlySpan<char> chars)
    {
        foreach (var c in chars)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
