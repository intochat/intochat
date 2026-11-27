using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Identity.Local.Custody;

namespace DigitalBrain.Identity.Local.Vault;

/// <summary>
/// Encrypted local-file <see cref="ISecretStoreBackend"/> for standalone, CLI, or non-silo hosts.
/// Persists one AES-GCM sealed JSON vault file per owner under a root directory, using <see cref="IMasterKeyCustody"/>
/// with AAD bound to the owner identifier, <c>0600</c> Unix permissions, and atomic temp-file replacement.
/// </summary>
public sealed class LocalFileSecretStoreBackend : ISecretStoreBackend
{
    private const string VaultFileContextPrefix = "local-file-vault|v1|";
    private readonly string _directory;
    private readonly IMasterKeyCustody _custody;
    private readonly Lock _gate = new();

    /// <summary>Creates a local-file secret store backend rooted at <paramref name="directory"/>.</summary>
    public LocalFileSecretStoreBackend(string directory, IMasterKeyCustody custody)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(custody);
        _directory = Path.GetFullPath(directory);
        _custody = custody;
    }

    /// <inheritdoc />
    public ValueTask<SecretEntry?> GetAsync(string owner, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var entries = LoadOwnerEntries(owner);
            return ValueTask.FromResult(entries.TryGetValue(name, out var entry) ? entry : null);
        }
    }

    /// <inheritdoc />
    public ValueTask SetAsync(string owner, string name, string label, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var entries = LoadOwnerEntries(owner);
            entries[name] = new SecretEntry(name, label ?? "", value);
            SaveOwnerEntries(owner, entries);
            return ValueTask.CompletedTask;
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveAsync(string owner, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var entries = LoadOwnerEntries(owner);
            if (!entries.Remove(name))
            {
                return ValueTask.FromResult(false);
            }

            SaveOwnerEntries(owner, entries);
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ListValuesAsync(string owner, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var entries = LoadOwnerEntries(owner);
            return ValueTask.FromResult<IReadOnlyList<string>>([.. entries.Values.Select(e => e.Value)]);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SecretEntry>> ListEntriesAsync(string owner, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var entries = LoadOwnerEntries(owner);
            return ValueTask.FromResult<IReadOnlyList<SecretEntry>>([.. entries.Values]);
        }
    }

    private string PathFor(string owner)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner))).ToLowerInvariant();
        return Path.Combine(_directory, hash + ".vault.sealed");
    }

    private Dictionary<string, SecretEntry> LoadOwnerEntries(string owner)
    {
        var path = PathFor(owner);
        if (!File.Exists(path))
        {
            return new Dictionary<string, SecretEntry>(StringComparer.Ordinal);
        }

        var sealedBytes = File.ReadAllBytes(path);
        var plainBytes = _custody.Unwrap(owner, VaultFileContextPrefix + owner, sealedBytes);
        var list = JsonSerializer.Deserialize(plainBytes, LocalFileVaultJsonContext.Default.ListLocalFileStoredEntry) ?? [];
        var map = new Dictionary<string, SecretEntry>(list.Count, StringComparer.Ordinal);
        foreach (var item in list)
        {
            map[item.Name] = new SecretEntry(item.Name, item.Label, item.Value);
        }

        return map;
    }

    private void SaveOwnerEntries(string owner, Dictionary<string, SecretEntry> entries)
    {
        System.IO.Directory.CreateDirectory(_directory);
        var path = PathFor(owner);
        var list = entries.Values.Select(e => new LocalFileStoredEntry(e.Name, e.Label, e.Value)).ToList();
        var plainBytes = JsonSerializer.SerializeToUtf8Bytes(list, LocalFileVaultJsonContext.Default.ListLocalFileStoredEntry);
        var sealedBytes = _custody.Wrap(owner, VaultFileContextPrefix + owner, plainBytes);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(sealedBytes);
            stream.Flush(flushToDisk: true);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(tmp, path, overwrite: true);
    }
}

internal sealed record LocalFileStoredEntry(string Name, string Label, string Value);

[JsonSerializable(typeof(List<LocalFileStoredEntry>))]
internal sealed partial class LocalFileVaultJsonContext : JsonSerializerContext;
