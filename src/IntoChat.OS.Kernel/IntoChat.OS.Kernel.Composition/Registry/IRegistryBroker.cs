namespace IntoChat.OS.Kernel.OsRegistry;

/// <summary>
/// The three tiers of the unified operating system registry.
/// </summary>
public enum RegistryTier
{
    /// <summary>User and system UI preferences (displayName, theme, defaultModel, stylePreset, etc.).</summary>
    Settings,

    /// <summary>Brain-wide environment variables (API_HOST, DEBUG_LOGGING, etc.).</summary>
    Environment,

    /// <summary>Sealed secrets and API credentials with strict zero-raw-token leakage.</summary>
    Vault
}

/// <summary>
/// Detailed metadata descriptor of a registry entry.
/// </summary>
/// <param name="Uri">Canonical URI representation (e.g. settings://theme, env://DEBUG, vault://provider:openai).</param>
/// <param name="Key">The bare key without scheme.</param>
/// <param name="Tier">The registry tier.</param>
/// <param name="Value">The string value, or masked value if secret.</param>
/// <param name="MaskedTail">The last four characters of the secret tail, if applicable.</param>
/// <param name="IsSet">Whether the entry exists and has a configured value.</param>
/// <param name="UpdatedAt">Last updated timestamp, if tracked.</param>
public sealed record RegistryEntryDescriptor(
    string Uri,
    string Key,
    RegistryTier Tier,
    string? Value,
    string? MaskedTail,
    bool IsSet,
    DateTimeOffset? UpdatedAt);

/// <summary>
/// Canonical OS registry broker unifying settings:// (preferences), env:// (environment variables),
/// and vault:// (sealed secrets with zero-leakage enforcement).
/// </summary>
public interface IRegistryBroker
{
    /// <summary>Reads an entry value by URI (settings://key, env://key, vault://key).</summary>
    Task<string?> GetAsync(string uri, CancellationToken ct = default);

    /// <summary>Reads an entry value converted to the specified primitive or JSON-deserialized type.</summary>
    Task<T?> GetAsync<T>(string uri, CancellationToken ct = default);

    /// <summary>Retrieves descriptor metadata for an entry by URI.</summary>
    Task<RegistryEntryDescriptor?> DescribeAsync(string uri, CancellationToken ct = default);

    /// <summary>Stores or updates an entry value by URI.</summary>
    Task SetAsync(string uri, string value, CancellationToken ct = default);

    /// <summary>Removes an entry by URI.</summary>
    Task RemoveAsync(string uri, CancellationToken ct = default);

    /// <summary>Checks whether an entry exists by URI.</summary>
    Task<bool> ExistsAsync(string uri, CancellationToken ct = default);

    /// <summary>Lists entries under a scheme ("settings", "env", "vault") or across all tiers if null.</summary>
    Task<IReadOnlyList<RegistryEntryDescriptor>> ListAsync(string? schemeOrPrefix = null, CancellationToken ct = default);
}
