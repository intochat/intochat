using System.Buffers.Text;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
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
    public const string LegacyHeaderName = "X-Presence-Token";
    public const string CookieName = "IntoChatPresence";
    internal static readonly RuntimeOptions DefaultRuntimeOptions = new() { HomeKey = "IntoChatOS:Home", DefaultHomeDirectoryName = ".intochat" };
    public const string ExchangePath = "/auth/local-presence";
    public const string ConfigurationKey = "IntoChatOS:Authority:PresenceToken";
    public const string TokenHashConfigurationKey = "IntoChatOS:Security:PresenceTokenHash";
    public const string TokenHashEnvironmentVariable = "IntoChatOS__Security__PresenceTokenHash";
    public const string TokenFileConfigurationKey = "IntoChatOS:Security:PresenceTokenFile";
    public const int SecretByteCount = 32;
    private const int MinimumUtf8SecretBytes = 16;
    private const int MaxCandidateChars = 512;

    private static readonly byte[] CookieContext = "intochat-local-presence-cookie-v1"u8.ToArray();

    private readonly byte[] _secretHash;
    private readonly string? _canonicalToken;
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

        _secretHash = SHA256.HashData(rawSecretBytes);
        _canonicalToken = Base64Url.EncodeToString(rawSecretBytes);
        _cookieHmacUtf8 = Encoding.UTF8.GetBytes(ComputeCookieMac(_secretHash));
    }

    private PresenceProofValidator((byte[] SecretHash, string? CanonicalToken) material)
    {
        _secretHash = material.SecretHash;
        _canonicalToken = material.CanonicalToken;
        _cookieHmacUtf8 = Encoding.UTF8.GetBytes(ComputeCookieMac(_secretHash));
    }

    public string Token => _canonicalToken
        ?? throw new InvalidOperationException("Plaintext presence token is unavailable when configured by hash.");

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

        Span<byte> extracted = stackalloc byte[Encoding.UTF8.GetMaxByteCount(trimmed.Length)];
        if (!TryExtractSecretBytes(trimmed, extracted, out var extractedLen))
        {
            return false;
        }

        Span<byte> candidateHash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(extracted[..extractedLen], candidateHash);
        return CryptographicOperations.FixedTimeEquals(candidateHash, _secretHash);
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

    private static (byte[] SecretHash, string? CanonicalToken) LoadMaterial(IConfiguration? configuration, RuntimeOptions? runtimeOptions)
    {
        var configuredHash = configuration?[TokenHashConfigurationKey];
        if (!string.IsNullOrWhiteSpace(configuredHash))
        {
            var trimmedHash = configuredHash.AsSpan().Trim();
            if (trimmedHash.Length != SHA256.HashSizeInBytes * 2 || !IsHex(trimmedHash))
            {
                throw new InvalidOperationException("Presence token hash must be a 64-character SHA-256 hexadecimal digest.");
            }

            return (Convert.FromHexString(trimmedHash), null);
        }

        var configured = configuration?[ConfigurationKey];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return ParseTextMaterial(configured);
        }

        var (filePath, hasExplicitLocation) = ResolvePresenceFilePath(configuration, runtimeOptions);
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            var rawBytes = File.ReadAllBytes(filePath);
            if (rawBytes.Length > 0)
            {
                return ParseFileMaterial(rawBytes);
            }
        }

        var generated = RandomNumberGenerator.GetBytes(SecretByteCount);
        if (hasExplicitLocation && !string.IsNullOrWhiteSpace(filePath))
        {
            var writtenBytes = WriteOrLoadOwnerOnlyPresenceFile(filePath, generated);
            return ParseFileMaterial(writtenBytes);
        }

        return (SHA256.HashData(generated), Base64Url.EncodeToString(generated));
    }

    private static byte[] WriteOrLoadOwnerOnlyPresenceFile(string filePath, byte[] generated)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            EnsureOwnerOnlyDirectory(directory);
        }

        var tempPath = Path.Combine(directory ?? Path.GetTempPath(), $".presence.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllBytes(tempPath, generated);
                ApplyOwnerOnlyFileAcl(tempPath);
            }
            else
            {
                using var stream = new FileStream(tempPath, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                });
                stream.Write(generated);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(tempPath, filePath, overwrite: false);
                return generated;
            }
            catch (IOException) when (File.Exists(filePath))
            {
                var existing = File.ReadAllBytes(filePath);
                return existing.Length > 0 ? existing : generated;
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static void EnsureOwnerOnlyDirectory(string directoryPath)
    {
        System.IO.Directory.CreateDirectory(directoryPath);
        if (OperatingSystem.IsWindows())
        {
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is not null)
            {
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new FileSystemAccessRule(
                    currentUser,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                new DirectoryInfo(directoryPath).SetAccessControl(security);
            }
        }
        else
        {
            File.SetUnixFileMode(directoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void ApplyOwnerOnlyFileAcl(string filePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is null)
        {
            return;
        }

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        new FileInfo(filePath).SetAccessControl(security);
    }

    private static (string? Path, bool ExplicitLocation) ResolvePresenceFilePath(IConfiguration? configuration, RuntimeOptions? runtimeOptions)
    {
        var invalidChars = Path.GetInvalidPathChars();
        if (configuration is not null)
        {
            var explicitFile = configuration[TokenFileConfigurationKey];
            if (!string.IsNullOrWhiteSpace(explicitFile) && explicitFile.IndexOfAny(invalidChars) < 0)
            {
                return (explicitFile, true);
            }

            var options = runtimeOptions ?? DefaultRuntimeOptions;
            var homeRoot = configuration[options.HomeKey];
            if (!string.IsNullOrWhiteSpace(homeRoot) && homeRoot.IndexOfAny(invalidChars) < 0)
            {
                return (Path.Combine(homeRoot, "run", "presence"), true);
            }
        }

        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(userHome) || userHome.IndexOfAny(invalidChars) >= 0
            ? (null, false)
            : (Path.Combine(userHome, ".intochat", "run", "presence"), false);
    }

    private static (byte[] SecretHash, string? CanonicalToken) ParseFileMaterial(byte[] rawBytes)
    {
        // Exact 32 raw bytes written directly to ~/.intochat/run/presence
        if (rawBytes.Length == SecretByteCount && !IsAllPrintableAscii(rawBytes))
        {
            return (SHA256.HashData(rawBytes), Base64Url.EncodeToString(rawBytes));
        }

        var text = Encoding.UTF8.GetString(rawBytes).Trim();
        if (text.Length > 0 && IsAllPrintableAscii(rawBytes))
        {
            return ParseTextMaterial(text);
        }

        if (rawBytes.Length >= SecretByteCount)
        {
            return (SHA256.HashData(rawBytes), Base64Url.EncodeToString(rawBytes));
        }

        var derived = SHA256.HashData(rawBytes);
        return (SHA256.HashData(derived), text.Length > 0 ? text : Base64Url.EncodeToString(derived));
    }

    private static (byte[] SecretHash, string? CanonicalToken) ParseTextMaterial(string rawText)
    {
        var trimmed = rawText.Trim();
        Span<byte> decoded = stackalloc byte[Encoding.UTF8.GetMaxByteCount(Math.Min(trimmed.Length, MaxCandidateChars))];
        if (TryExtractSecretBytes(trimmed.AsSpan(), decoded, out var decodedLen))
        {
            return (SHA256.HashData(decoded[..decodedLen]), trimmed);
        }

        var utf8 = Encoding.UTF8.GetBytes(trimmed);
        return (SHA256.HashData(utf8), trimmed);
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

    private static bool TryExtractSecretBytes(ReadOnlySpan<char> text, Span<byte> destination, out int bytesWritten)
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
