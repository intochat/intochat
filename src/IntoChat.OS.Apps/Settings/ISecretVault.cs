namespace IntoChat.OS.Apps.Settings;

/// <summary>
/// Probes whether a named secret is sealed/configured without revealing raw secret values.
/// </summary>
public interface ISecretVault
{
    Task<bool> HasSecretAsync(string secretName, CancellationToken cancellationToken = default);

    Task<string> GetSecretStatusAsync(string secretName, CancellationToken cancellationToken = default);
}

/// <summary>
/// In-memory or fallback secret vault implementation for testing and runtime redaction.
/// </summary>
public sealed class MemorySecretVault : ISecretVault
{
    private readonly HashSet<string> _configuredSecrets;

    public MemorySecretVault(IEnumerable<string>? configuredSecrets = null)
    {
        _configuredSecrets = configuredSecrets is not null
            ? new HashSet<string>(configuredSecrets, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public void SetSecret(string secretName) => _configuredSecrets.Add(secretName);

    public void RemoveSecret(string secretName) => _configuredSecrets.Remove(secretName);

    public Task<bool> HasSecretAsync(string secretName, CancellationToken cancellationToken = default)
        => Task.FromResult(_configuredSecrets.Contains(secretName));

    public Task<string> GetSecretStatusAsync(string secretName, CancellationToken cancellationToken = default)
        => Task.FromResult(_configuredSecrets.Contains(secretName) ? "Configured" : "Not set");
}
