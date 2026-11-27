using System.Collections.Concurrent;
using DigitalBrain.Capabilities.Secrets;

namespace DigitalBrain.Identity.Local.Vault;

/// <summary>
/// Thread-safe in-memory <see cref="ISecretStoreBackend"/> for unit testing and ephemeral standalone hosts.
/// </summary>
public sealed class InMemorySecretStoreBackend : ISecretStoreBackend
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, SecretEntry>> _owners = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<SecretEntry?> GetAsync(string owner, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();
        if (_owners.TryGetValue(owner, out var entries) && entries.TryGetValue(name, out var entry))
        {
            return ValueTask.FromResult<SecretEntry?>(entry);
        }

        return ValueTask.FromResult<SecretEntry?>(null);
    }

    /// <inheritdoc />
    public ValueTask SetAsync(string owner, string name, string label, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        cancellationToken.ThrowIfCancellationRequested();
        var entries = _owners.GetOrAdd(owner, _ => new ConcurrentDictionary<string, SecretEntry>(StringComparer.Ordinal));
        entries[name] = new SecretEntry(name, label ?? "", value);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveAsync(string owner, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();
        if (_owners.TryGetValue(owner, out var entries))
        {
            return ValueTask.FromResult(entries.TryRemove(name, out _));
        }

        return ValueTask.FromResult(false);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ListValuesAsync(string owner, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_owners.TryGetValue(owner, out var entries))
        {
            return ValueTask.FromResult<IReadOnlyList<string>>([]);
        }

        return ValueTask.FromResult<IReadOnlyList<string>>([.. entries.Values.Select(e => e.Value)]);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SecretEntry>> ListEntriesAsync(string owner, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_owners.TryGetValue(owner, out var entries))
        {
            return ValueTask.FromResult<IReadOnlyList<SecretEntry>>([]);
        }

        return ValueTask.FromResult<IReadOnlyList<SecretEntry>>([.. entries.Values]);
    }
}
