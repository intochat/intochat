using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Runtime.Orleans;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Authority.Identity;

/// <summary>
/// Proves physical or local-session presence on an Open-posture host so unproved loopback or MCP traffic
/// never silently inherits <c>Actor.Person</c>.
/// </summary>
public interface IPresenceProof
{
    /// <summary>Canonical ASCII presence token suitable for <c>X-IntoChat-Presence</c> or <c>#presence=</c> bootstrap.</summary>
    string Token { get; }

    /// <summary>Verifies a candidate <c>X-IntoChat-Presence</c> header or <c>POST /auth/local-presence</c> exchange token in constant time.</summary>
    bool MatchesHeader(string? candidate);

    /// <summary>Mints the <c>IntoChatPresence</c> cookie value derived from this host's secret.</summary>
    string IssueCookieValue();

    /// <summary>Verifies a candidate <c>IntoChatPresence</c> cookie value in constant time.</summary>
    bool MatchesCookie(string? cookieValue);
}

public sealed class PresenceProofValidator : IPresenceProof
{
    public const string HeaderName = "X-IntoChat-Presence";
    public const string CookieName = "IntoChatPresence";
    public const string ExchangePath = "/auth/local-presence";
    public const string EnvironmentVariable = "INTOCHAT_PRESENCE_TOKEN";
    public const string ConfigurationKey = "IntoChatOS:Authority:PresenceToken";
    public const int SecretByteCount = 32;
    private const int MaxCandidateChars = 512;

    private static readonly byte[] CookieContext = "intochat-local-presence-cookie-v1"u8.ToArray();

    private readonly byte[] _secretBytes;
    private readonly byte[] _tokenUtf8;
    private readonly byte[] _cookieHmacUtf8;

    public PresenceProofValidator(IConfiguration? configuration = null, RuntimeOptions? runtimeOptions = null)
        : this(LoadMaterial(configuration, runtimeOptions))
    {
    }

    public PresenceProofValidator(ReadOnlySpan<byte> rawSecretBytes)
    {
        if (rawSecretBytes.Length < SecretByteCount)
        {
            throw new ArgumentException($"Presence secret must be at least {SecretByteCount} bytes.", nameof(rawSecretBytes));
        }

        _secretBytes = rawSecretBytes.ToArray();
        Token = Base64Url.EncodeToString(_secretBytes);
        _tokenUtf8 = Encoding.UTF8.GetBytes(Token);
        _cookieHmacUtf8 = Encoding.UTF8.GetBytes(ComputeCookieMac(_secretBytes));
    }

    private PresenceProofValidator((byte[] SecretBytes, string CanonicalToken) material)
    {
        _secretBytes = material.SecretBytes;
        Token = material.CanonicalToken;
        _tokenUtf8 = Encoding.UTF8.GetBytes(Token);
        _cookieHmacUtf8 = Encoding.UTF8.GetBytes(ComputeCookieMac(_secretBytes));
    }

    public string Token { get; }

    public string IssueCookieValue() => Encoding.UTF8.GetString(_cookieHmacUtf8);

    public bool MatchesHeader(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var trimmed = candidate.AsSpan().Trim();
        if (trimmed.Length is 0 or > MaxCandidateChars)
        {
            return false;
        }

        // 1. Constant-time UTF-8 comparison against the canonical token string.
        Span<byte> utf8 = stackalloc byte[Encoding.UTF8.GetMaxByteCount(trimmed.Length)];
        var utf8Len = Encoding.UTF8.GetBytes(trimmed, utf8);
        var exactMatch = CryptographicOperations.FixedTimeEquals(utf8[..utf8Len], _tokenUtf8);

        // 2. Constant-time 32-byte payload comparison across Base64Url / Base64 / Hex encodings so a raw 32-byte
        // ~/.intochat/run/presence file and Base64/Base64Url/Hex headers interoperate without format friction.
        Span<byte> decoded = stackalloc byte[MaxCandidateChars];
        var decodedMatch = TryDecodeSecretBytes(trimmed, decoded, out var decodedLen)
            && CryptographicOperations.FixedTimeEquals(decoded[..decodedLen], _secretBytes);

        return exactMatch | decodedMatch;
    }

    public bool MatchesCookie(string? cookieValue)
    {
        if (string.IsNullOrWhiteSpace(cookieValue))
        {
            return false;
        }

        var trimmed = cookieValue.AsSpan().Trim();
        if (trimmed.Length is 0 or > MaxCandidateChars)
        {
            return false;
        }

        Span<byte> utf8 = stackalloc byte[Encoding.UTF8.GetMaxByteCount(trimmed.Length)];
        var utf8Len = Encoding.UTF8.GetBytes(trimmed, utf8);
        var hmacMatch = CryptographicOperations.FixedTimeEquals(utf8[..utf8Len], _cookieHmacUtf8);
        var tokenMatch = MatchesHeader(cookieValue);
        return hmacMatch | tokenMatch;
    }

    public static bool IsSameOriginRequest(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Headers.TryGetValue("Sec-Fetch-Site", out var fetchSite)
            && fetchSite.Count > 0
            && string.Equals(fetchSite[0], "cross-site", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (request.Headers.TryGetValue("Origin", out var origins) && origins.Count > 0 && !string.IsNullOrWhiteSpace(origins[0]))
        {
            var rawOrigin = origins[0]!;
            if (!Uri.TryCreate(rawOrigin, UriKind.Absolute, out var originUri))
            {
                return false;
            }

            if (!IsSameHostAndPort(originUri, request))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSameHostAndPort(Uri candidate, HttpRequest request)
    {
        if (!request.Host.HasValue)
        {
            return IsLoopbackHost(candidate.Host);
        }

        var reqHost = request.Host.Host;
        var hostMatches = string.Equals(candidate.Host, reqHost, StringComparison.OrdinalIgnoreCase);
        if (!hostMatches)
        {
            return false;
        }

        if (request.Host.Port is { } reqPort && !candidate.IsDefaultPort)
        {
            return candidate.Port == reqPort;
        }

        return true;
    }

    private static bool IsLoopbackHost(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "[::1]", StringComparison.OrdinalIgnoreCase);

    private static string ComputeCookieMac(byte[] key)
    {
        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(key, CookieContext, mac);
        return Base64Url.EncodeToString(mac);
    }

    private static (byte[] SecretBytes, string CanonicalToken) LoadMaterial(IConfiguration? configuration, RuntimeOptions? runtimeOptions)
    {
        var configured = configuration?[EnvironmentVariable]
            ?? configuration?[ConfigurationKey]
            ?? Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return ParseTextMaterial(configured);
        }

        var filePath = ResolvePresenceFilePath(configuration, runtimeOptions);
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            var rawBytes = File.ReadAllBytes(filePath);
            if (rawBytes.Length > 0)
            {
                return ParseFileMaterial(rawBytes);
            }
        }

        var generated = RandomNumberGenerator.GetBytes(SecretByteCount);
        return (generated, Base64Url.EncodeToString(generated));
    }

    private static string? ResolvePresenceFilePath(IConfiguration? configuration, RuntimeOptions? runtimeOptions)
    {
        try
        {
            if (configuration is not null)
            {
                var options = runtimeOptions ?? new RuntimeOptions { HomeKey = "IntoChatOS:Home", DefaultHomeDirectoryName = ".intochat" };
                var homeRoot = configuration[options.HomeKey];
                if (!string.IsNullOrWhiteSpace(homeRoot))
                {
                    return Path.Combine(homeRoot, "run", "presence");
                }
            }

            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrWhiteSpace(userHome) ? null : Path.Combine(userHome, ".intochat", "run", "presence");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static (byte[] SecretBytes, string CanonicalToken) ParseFileMaterial(byte[] rawBytes)
    {
        // Exact 32 raw bytes written directly to ~/.intochat/run/presence
        if (rawBytes.Length == SecretByteCount && !IsAllPrintableAscii(rawBytes))
        {
            return (rawBytes, Base64Url.EncodeToString(rawBytes));
        }

        var text = Encoding.UTF8.GetString(rawBytes).Trim();
        if (text.Length > 0 && IsAllPrintableAscii(rawBytes))
        {
            return ParseTextMaterial(text);
        }

        if (rawBytes.Length >= SecretByteCount)
        {
            return (rawBytes, Base64Url.EncodeToString(rawBytes));
        }

        // Derive a full 32-byte key if file had fewer than 32 non-empty bytes
        var derived = SHA256.HashData(rawBytes);
        return (derived, text.Length > 0 ? text : Base64Url.EncodeToString(derived));
    }

    private static (byte[] SecretBytes, string CanonicalToken) ParseTextMaterial(string rawText)
    {
        var trimmed = rawText.Trim();
        Span<byte> decoded = stackalloc byte[MaxCandidateChars];
        if (TryDecodeSecretBytes(trimmed.AsSpan(), decoded, out var decodedLen) && decodedLen >= SecretByteCount)
        {
            return (decoded[..decodedLen].ToArray(), trimmed);
        }

        var utf8 = Encoding.UTF8.GetBytes(trimmed);
        var secretBytes = utf8.Length >= SecretByteCount ? utf8 : SHA256.HashData(utf8);
        return (secretBytes, trimmed);
    }

    private static bool IsAllPrintableAscii(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (b is (byte)'\r' or (byte)'\n' or (byte)'\t')
            {
                continue;
            }

            if (b is < 0x21 or > 0x7E)
            {
                return false;
            }
        }

        return true;
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

    private static bool IsHex(ReadOnlySpan<char> chars)
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
