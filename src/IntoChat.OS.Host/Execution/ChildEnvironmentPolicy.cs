using System.Diagnostics;

namespace IntoChat.OS.Host.Execution;

/// <summary>
/// Centralized child-process environment scrubbing policy shared across desktop, kernel,
/// sandbox runners, candidate hosts, and Aspire hosting so parent custody secrets never leak
/// into child processes.
/// </summary>
public static class ChildEnvironmentPolicy
{
    private static readonly string[] ExactBlockedNames =
    [
        "IntoChatOS__MasterKey",
        "INTOCHATOS_MASTER_KEY",
        "INTOCHAT_PRESENCE_TOKEN",
        "IntoChatOS__Security__PresenceTokenHash",
        "IntoChatOS__Security__PresenceTokenFile",
        "IntoChatOS__Authority__PresenceToken",
    ];

    private static readonly string[] BlockedPrefixes =
    [
        "IntoChatOS__",
        "SANDBOX_",
        "DIGITALBRAIN_",
    ];

    private static readonly string[] BlockedSubstrings =
    [
        "MASTERKEY",
        "MASTER_KEY",
        "PRESENCE_TOKEN",
        "PRESENCE_HASH",
        "CONNECTIONSTRING",
        "CONNECTION_STRING",
        "PRIVATE_KEY",
        "API_KEY",
        "SECRET",
        "PASSWORD",
        "ACCESS_KEY",
        "AUTH_TOKEN",
        "CREDENTIAL",
        "SERVICE_ASSERTION",
    ];

    private static readonly string[] BlockedSuffixes =
    [
        "_KEY",
        "_TOKEN",
        "_SECRET",
        "_PASSWORD",
        "_APIKEY",
        "_CREDENTIAL",
        "_CREDENTIALS",
    ];

    /// <summary>Returns true when an environment variable name belongs to custody secrets, tokens, or host config prefixes.</summary>
    public static bool IsBlockedSecretVariableName(string? name)
    {
        if (IsCustodyOrCredentialName(name))
        {
            return true;
        }

        foreach (var prefix in BlockedPrefixes)
        {
            if (name!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns true when an environment variable name is a custody key, presence token/hash, or credential.</summary>
    public static bool IsCustodyOrCredentialName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        foreach (var exact in ExactBlockedNames)
        {
            if (string.Equals(name, exact, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var suffix in BlockedSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var part in BlockedSubstrings)
        {
            if (name.Contains(part, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Removes blocked secret environment variables from a <see cref="ProcessStartInfo"/>.</summary>
    public static void Scrub(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ScrubCore(startInfo.Environment, IsBlockedSecretVariableName);
    }

    /// <summary>Removes blocked secret environment variables from a nullable string environment dictionary.</summary>
    public static void Scrub(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ScrubCore(environment, IsBlockedSecretVariableName);
    }

    /// <summary>Removes blocked secret environment variables from a string environment dictionary.</summary>
    public static void ScrubDictionary(IDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ScrubCore(environment, IsBlockedSecretVariableName);
    }

    /// <summary>Removes blocked secret environment variables from an Aspire object-valued environment dictionary.</summary>
    public static void ScrubObjects(IDictionary<string, object> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ScrubCore(environment, IsBlockedSecretVariableName);
    }

    /// <summary>
    /// Removes custody keys, presence proofs, and credentials while preserving non-secret
    /// host-copied options for isolated candidate containers.
    /// </summary>
    public static void ScrubCustodyAndSecretVariables(IDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ScrubCore(environment, IsCustodyOrCredentialName);
    }

    private static void ScrubCore<TValue>(IDictionary<string, TValue> environment, Func<string?, bool> predicate)
    {
        List<string>? toRemove = null;
        foreach (var key in environment.Keys)
        {
            if (predicate(key))
            {
                toRemove ??= [];
                toRemove.Add(key);
            }
        }

        if (toRemove is null)
        {
            return;
        }

        foreach (var key in toRemove)
        {
            environment.Remove(key);
        }
    }
}
