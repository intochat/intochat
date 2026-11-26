using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace IntoChat.OS.Host.Execution;

/// <summary>
/// Builds and validates URL-safe, collision-resistant Kata MicroVM session identifiers (<c>identifier</c>)
/// for Azure Container Apps Dynamic Sessions so every Plane 1 execution and Plane 2 candidate run
/// receives an isolated microVM session.
/// </summary>
public static partial class AcaSessionIsolation
{
    public const int MaxSessionIdLength = 64;
    public const string Plane1Prefix = "p1-";
    public const string Plane2Prefix = "p2-";

    /// <summary>
    /// Creates an isolated Plane 1 session ID bound to <paramref name="brainId"/>, <paramref name="appKey"/>,
    /// and a fresh cryptographically random nonce so separate runs never share microVM memory or disk.
    /// </summary>
    public static string BuildPlane1SessionId(string brainId, string appKey, string? nonce = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);

        var brainSlug = SanitizeSlug(brainId, maxLength: 18);
        var appSlug = SanitizeSlug(appKey, maxLength: 20);
        var effectiveNonce = string.IsNullOrWhiteSpace(nonce)
            ? RandomNumberGenerator.GetHexString(12, lowercase: true)
            : SanitizeSlug(nonce, maxLength: 16);

        var id = $"{Plane1Prefix}{brainSlug}-{appSlug}-{effectiveNonce}";
        if (id.Length > MaxSessionIdLength)
        {
            id = id[..MaxSessionIdLength].TrimEnd('-');
        }

        ValidateSessionId(id);
        return id;
    }

    /// <summary>
    /// Creates an isolated Plane 2 candidate session ID bound to <paramref name="candidateSha"/>
    /// and a unique nonce (with optional stage qualifier such as <c>gate</c>, <c>pub</c>, or <c>run</c>).
    /// </summary>
    public static string BuildPlane2SessionId(string candidateSha, string stage = "run", string? nonce = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);

        var shaSlug = SanitizeSlug(candidateSha, maxLength: 20);
        var stageSlug = SanitizeSlug(stage, maxLength: 10);
        var effectiveNonce = string.IsNullOrWhiteSpace(nonce)
            ? RandomNumberGenerator.GetHexString(12, lowercase: true)
            : SanitizeSlug(nonce, maxLength: 16);

        var id = $"{Plane2Prefix}{stageSlug}-{shaSlug}-{effectiveNonce}";
        if (id.Length > MaxSessionIdLength)
        {
            id = id[..MaxSessionIdLength].TrimEnd('-');
        }

        ValidateSessionId(id);
        return id;
    }

    /// <summary>Extracts the candidate SHA from a Plane 2 session identifier (<c>p2-{stage}-{sha}-{nonce}</c>).</summary>
    public static bool TryParsePlane2CandidateSha(string? sessionId, out string sha)
    {
        sha = string.Empty;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var match = Plane2SessionPattern().Match(sessionId);
        if (!match.Success)
        {
            return false;
        }

        sha = match.Groups["sha"].Value;
        return !string.IsNullOrWhiteSpace(sha);
    }

    /// <summary>Validates that a session identifier contains only safe characters and cannot inject path or query tokens.</summary>
    public static void ValidateSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || sessionId.Length > MaxSessionIdLength
            || sessionId.Contains("..", StringComparison.Ordinal)
            || !ValidSessionIdPattern().IsMatch(sessionId))
        {
            throw new ArgumentException(
                "Session identifier must be 1-64 characters matching [a-zA-Z0-9][a-zA-Z0-9._-]* without '..'.",
                nameof(sessionId));
        }
    }

    private static string SanitizeSlug(string value, int maxLength)
    {
        var sb = new StringBuilder(Math.Min(value.Length, maxLength));
        var lastWasDash = false;
        foreach (var ch in value.Trim())
        {
            if (sb.Length >= maxLength)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasDash = false;
            }
            else if (!lastWasDash && sb.Length > 0)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length == 0)
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
            return hash[..Math.Min(8, maxLength)];
        }

        return slug;
    }

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$")]
    private static partial Regex ValidSessionIdPattern();

    [GeneratedRegex("^p2-[a-z0-9]+-(?<sha>[a-z0-9]+)-[a-z0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Plane2SessionPattern();
}
