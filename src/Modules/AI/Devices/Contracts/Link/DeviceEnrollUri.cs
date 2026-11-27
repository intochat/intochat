using System.Web;
using IntoChat.OS;

namespace IntoChat.OS.Modules.Devices.Link;

/// <summary>
/// Parses, validates, and constructs <c>intochat://enroll?server=...&amp;code=...</c> deep links
/// for Option A 1-click browser-to-desktop pairing. Never logs or persists raw enrollment codes.
/// </summary>
public static class DeviceEnrollUri
{
    public const string Scheme = "intochat";
    public const string EnrollHost = "enroll";
    public const string BrowserLinkPath = "/account/link-device";
    public const string DefaultCloudUrl = "https://intochat.io";

    public static Uri BuildBrowserLinkUrl(Uri? cloudUrl = null)
    {
        var baseUri = cloudUrl ?? new Uri(DefaultCloudUrl, UriKind.Absolute);
        var normalized = baseUri.AbsoluteUri.TrimEnd('/');
        return new Uri(normalized + BrowserLinkPath, UriKind.Absolute);
    }

    public static Uri Build(Uri server, Code code)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (!ValidateServer(server, out var serverError))
        {
            throw new ArgumentException(serverError, nameof(server));
        }

        if (!ValidateCode(code.Value, out var normalizedCode, out var codeError))
        {
            throw new ArgumentException(codeError, nameof(code));
        }

        var serverParam = Uri.EscapeDataString(server.AbsoluteUri.TrimEnd('/'));
        var codeParam = Uri.EscapeDataString(normalizedCode.Value);
        return new Uri($"{Scheme}://{EnrollHost}?server={serverParam}&code={codeParam}", UriKind.Absolute);
    }

    public static bool TryParse(string? rawUri, out Uri? server, out Code code, out string? error)
    {
        server = null;
        code = default;
        error = null;

        if (string.IsNullOrWhiteSpace(rawUri))
        {
            error = "Enrollment URI is required.";
            return false;
        }

        var trimmed = rawUri.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
        {
            error = "Enrollment URI is not a valid absolute URI.";
            return false;
        }

        if (!string.Equals(parsed.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Enrollment URI must use the '{Scheme}://' scheme.";
            return false;
        }

        if (!string.Equals(parsed.Host, EnrollHost, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Enrollment URI host must be '{EnrollHost}'.";
            return false;
        }

        var query = HttpUtility.ParseQueryString(parsed.Query);
        var rawServer = query["server"];
        var rawCode = query["code"];

        if (!TryParseServer(rawServer, out server, out error))
        {
            return false;
        }

        if (!ValidateCode(rawCode, out code, out error))
        {
            server = null;
            return false;
        }

        return true;
    }

    public static bool TryParseServer(string? rawServer, out Uri? server, out string? error)
    {
        server = null;
        error = null;

        if (string.IsNullOrWhiteSpace(rawServer))
        {
            error = "Server URL is required.";
            return false;
        }

        if (!Uri.TryCreate(rawServer.Trim(), UriKind.Absolute, out var candidate))
        {
            error = "Server URL must be an absolute https:// URI (or loopback http:// for local development).";
            return false;
        }

        if (!ValidateServer(candidate, out error))
        {
            return false;
        }

        server = candidate;
        return true;
    }

    public static bool ValidateServer(Uri? server, out string? error)
    {
        error = null;
        if (server is null || !server.IsAbsoluteUri)
        {
            error = "Server URL must be an absolute https:// URI (or loopback http:// for local development).";
            return false;
        }

        if (!string.IsNullOrEmpty(server.UserInfo))
        {
            error = "Server URL must not contain embedded credentials.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(server.Host))
        {
            error = "Server URL must include a host name.";
            return false;
        }

        var isHttps = string.Equals(server.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isLoopbackHttp = string.Equals(server.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && server.IsLoopback;
        if (!isHttps && !isLoopbackHttp)
        {
            error = "Server URL must use https:// (plain http:// is allowed only for localhost/loopback development).";
            return false;
        }

        return true;
    }

    public static bool ValidateCode(string? rawCode, out Code code, out string? error)
    {
        code = default;
        error = null;

        if (string.IsNullOrWhiteSpace(rawCode))
        {
            error = "Pairing code is required.";
            return false;
        }

        var trimmed = rawCode.Trim();
        if (Code.TryParse(trimmed, out var canonical))
        {
            code = canonical;
            return true;
        }

        // Accept 6..32 character alphanumeric/hyphen pairing codes (Option A 6-char manual fallback).
        Span<char> buffer = stackalloc char[32];
        var len = 0;
        foreach (var ch in trimmed)
        {
            if (ch is '-' or ' ')
            {
                continue;
            }

            if (!char.IsAsciiLetterOrDigit(ch) || len >= buffer.Length)
            {
                error = "Pairing code contains invalid characters.";
                return false;
            }

            buffer[len++] = char.ToUpperInvariant(ch);
        }

        if (len < 6)
        {
            error = "Pairing code must be at least 6 characters.";
            return false;
        }

        code = new Code(new string(buffer[..len]));
        return true;
    }
}
