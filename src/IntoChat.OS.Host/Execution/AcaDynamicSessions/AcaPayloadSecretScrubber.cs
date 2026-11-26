using System.Text.RegularExpressions;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Host.Execution;

/// <summary>
/// Enforces zero-secret-leakage boundaries for outbound ACA Dynamic Sessions payloads/environments
/// and inbound error/diagnostic strings returned from the management plane.
/// </summary>
public static partial class AcaPayloadSecretScrubber
{
    public const string RedactedMarker = "[REDACTED]";
    private const string PresenceTokenHashEnvVar = "IntoChatOS__Security__PresenceTokenHash";
    private const string PresenceTokenHashConfigKey = "IntoChatOS:Security:PresenceTokenHash";

    private static readonly string[] AlwaysBlockedExactNames =
    [
        "HOME",
        "USERPROFILE",
        "SSH_AUTH_SOCK",
        "SSH_AGENT_PID",
        "GIT_ASKPASS",
        "GIT_CONFIG_GLOBAL",
        "GH_TOKEN",
        "GITHUB_TOKEN",
        "NUGET_API_KEY",
        "DOCKER_AUTH_CONFIG",
        "IDENTITY_HEADER",
        "IDENTITY_ENDPOINT",
        "MSI_SECRET",
        "MSI_ENDPOINT",
        "INTOCHAT_PRESENCE_TOKEN",
        "SANDBOX_ENROLL_KEY",
        "SANDBOX_TOKEN_SIGNING_MATERIAL",
        IntoChatOSNames.MasterKeyEnvironmentVariable,
        IntoChatOSNames.MasterKeyConfigurationKey,
        PresenceTokenHashEnvVar,
        PresenceTokenHashConfigKey,
    ];

    private static readonly string[] AlwaysBlockedPrefixes =
    [
        "AWS_",
        "AZURE_",
        "GITHUB_",
        "SSH_",
        "IDENTITY_",
        "MSI_",
        "SANDBOX_ENROLL",
        "SANDBOX_TOKEN_SIGNING",
    ];

    /// <summary>Returns true if the environment variable name is a host custody, cloud identity, or ambient secret.</summary>
    public static bool IsForbiddenAmbientKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        if (ChildEnvironmentPolicy.IsCustodyOrCredentialName(name))
        {
            return true;
        }

        foreach (var exact in AlwaysBlockedExactNames)
        {
            if (string.Equals(name, exact, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var prefix in AlwaysBlockedPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if a key is strictly forbidden even in explicit candidate model secret bags
    /// (host master key, presence proofs, managed identity headers, sandbox enroll/signing keys).
    /// </summary>
    public static bool IsStrictlyForbiddenHostSecretKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        return string.Equals(name, IntoChatOSNames.MasterKeyEnvironmentVariable, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, IntoChatOSNames.MasterKeyConfigurationKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, PresenceTokenHashEnvVar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, PresenceTokenHashConfigKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "INTOCHAT_PRESENCE_TOKEN", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "IDENTITY_HEADER", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "IDENTITY_ENDPOINT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "MSI_SECRET", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "MSI_ENDPOINT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "SANDBOX_ENROLL_KEY", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "SANDBOX_TOKEN_SIGNING_MATERIAL", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "SSH_AUTH_SOCK", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "SSH_AGENT_PID", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "GITHUB_TOKEN", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "GH_TOKEN", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Copies safe non-secret host entries from <paramref name="ambientSource"/>, strips all custody/cloud/SSH/MSI
    /// secrets, and merges <paramref name="explicitAllowedOverrides"/> while still blocking host-custody secrets.
    /// </summary>
    public static Dictionary<string, string> ScrubEnvironment(
        IReadOnlyDictionary<string, string>? ambientSource,
        IReadOnlyDictionary<string, string>? explicitAllowedOverrides = null)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ambientSource is not null)
        {
            foreach (var (key, value) in ambientSource)
            {
                if (!IsForbiddenAmbientKey(key))
                {
                    result[key] = value;
                }
            }
        }

        if (explicitAllowedOverrides is not null)
        {
            foreach (var (key, value) in explicitAllowedOverrides)
            {
                if (!IsStrictlyForbiddenHostSecretKey(key))
                {
                    result[key] = value;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Redacts known secret values and common credential token patterns (Bearer JWTs, GitHub PATs, API keys)
    /// from outbound logs or exception diagnostics.
    /// </summary>
    public static string RedactSecrets(string? text, params string?[] knownSecrets)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var scrubbed = text;
        foreach (var secret in knownSecrets)
        {
            if (!string.IsNullOrWhiteSpace(secret) && secret.Length >= 4)
            {
                scrubbed = scrubbed.Replace(secret, RedactedMarker, StringComparison.Ordinal);
            }
        }

        scrubbed = BearerTokenPattern().Replace(scrubbed, "Bearer " + RedactedMarker);
        scrubbed = KnownTokenPattern().Replace(scrubbed, RedactedMarker);
        return scrubbed;
    }

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenPattern();

    [GeneratedRegex(@"\b(?:ghp_[A-Za-z0-9]{12,}|github_pat_[A-Za-z0-9_]{12,}|sk-[A-Za-z0-9_-]{12,}|AKIA[0-9A-Z]{16})\b", RegexOptions.CultureInvariant)]
    private static partial Regex KnownTokenPattern();
}
