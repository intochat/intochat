namespace DigitalBrain.Capabilities;

/// <summary>Deterministic principal formatting for installed apps (<c>app:{brain}/apps/{name}</c>).</summary>
public static class AppPrincipal
{
    /// <summary>Prefix for app principal identifiers.</summary>
    public const string Prefix = "app:";

    /// <summary>Formats the deterministic principal identifier for an app key.</summary>
    public static string Id(string appKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        return Prefix + appKey;
    }

    /// <summary>Returns true when <paramref name="principalId"/> is an app principal.</summary>
    public static bool IsApp(string? principalId) => principalId is { Length: > 4 } && principalId.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Extracts the app key from an app principal identifier, or null when not an app principal.</summary>
    public static string? AppKeyOf(string? principalId) => IsApp(principalId) ? principalId![Prefix.Length..] : null;
}

