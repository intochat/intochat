using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.Security.KeyVault.Secrets;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Identity.Local.Custody;
using IntoChat.OS.Modules.Infrastructure.Secrets.Contracts;

namespace IntoChat.OS.Modules.Infrastructure.Secrets;

/// <summary>Configuration options for <see cref="AzureKeyVaultSecretStoreBackend"/>.</summary>
public sealed class AzureKeyVaultSecretStoreOptions
{
    public const string SectionName = "IntoChatOS:Secrets:AzureKeyVault";

    public Uri? VaultUri { get; set; }

    public string DeploymentId { get; set; } = "";

    public string OwnerKind { get; set; } = SecretCoordinate.DefaultOwnerKind;

    public void Validate()
    {
        if (VaultUri is null || !VaultUri.IsAbsoluteUri || !string.Equals(VaultUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{SectionName}:VaultUri must be a valid HTTPS Key Vault URI.");
        }

        if (string.IsNullOrWhiteSpace(DeploymentId))
        {
            throw new InvalidOperationException($"{SectionName}:DeploymentId must be configured.");
        }
    }
}

/// <summary>Production <see cref="IAzureKeyVaultSecretClient"/> backed by <see cref="SecretClient"/>.</summary>
internal sealed class AzureSdkKeyVaultSecretClient(SecretClient client) : IAzureKeyVaultSecretClient
{
    private readonly SecretClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public async ValueTask<AzureKeyVaultSecretItem?> GetSecretAsync(string secretName, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client.GetSecretAsync(secretName, cancellationToken: cancellationToken).ConfigureAwait(false);
            var secret = response.Value;
            return new AzureKeyVaultSecretItem(
                secret.Name,
                secret.Value,
                new Dictionary<string, string>(secret.Properties.Tags, StringComparer.Ordinal));
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async ValueTask SetSecretAsync(string secretName, string value, IDictionary<string, string> tags, CancellationToken cancellationToken = default)
    {
        var secret = new KeyVaultSecret(secretName, value);
        foreach (var (k, v) in tags)
        {
            secret.Properties.Tags[k] = v;
        }

        await _client.SetSecretAsync(secret, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> DeleteSecretAsync(string secretName, CancellationToken cancellationToken = default)
    {
        try
        {
            var operation = await _client.StartDeleteSecretAsync(secretName, cancellationToken).ConfigureAwait(false);
            await operation.WaitForCompletionAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    public async ValueTask RecoverDeletedSecretAsync(string secretName, CancellationToken cancellationToken = default)
    {
        var operation = await _client.StartRecoverDeletedSecretAsync(secretName, cancellationToken).ConfigureAwait(false);
        await operation.WaitForCompletionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<AzureKeyVaultSecretItem>> ListSecretsByPrefixAsync(string namePrefix, CancellationToken cancellationToken = default)
    {
        var results = new List<AzureKeyVaultSecretItem>();
        await foreach (var props in _client.GetPropertiesOfSecretsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (props.Enabled == false || !props.Name.StartsWith(namePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var item = await GetSecretAsync(props.Name, cancellationToken).ConfigureAwait(false);
            if (item is not null)
            {
                results.Add(item);
            }
        }

        return results;
    }
}

/// <summary>
/// <see cref="ISecretStoreBackend"/> storing secrets in Azure Key Vault using deterministic, collision-free
/// coordinates (<see cref="SecretCoordinate"/>) and AEAD envelope sealing via <see cref="IMasterKeyCustody"/>.
/// </summary>
internal sealed class AzureKeyVaultSecretStoreBackend : ISecretStoreBackend
{
    private readonly IAzureKeyVaultSecretClient _client;
    private readonly IMasterKeyCustody _custody;
    private readonly AzureKeyVaultSecretStoreOptions _options;

    public AzureKeyVaultSecretStoreBackend(
        IAzureKeyVaultSecretClient client,
        IMasterKeyCustody custody,
        AzureKeyVaultSecretStoreOptions options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _custody = custody ?? throw new ArgumentNullException(nameof(custody));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async ValueTask<SecretEntry?> GetAsync(string owner, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var coordinate = SecretCoordinate.Format(_options.DeploymentId, owner, name, _options.OwnerKind);
        var item = await _client.GetSecretAsync(coordinate, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        var entry = Unseal(owner, name, item.Value);
        return string.Equals(entry.Name, name, StringComparison.Ordinal) ? entry : null;
    }

    public async ValueTask SetAsync(string owner, string name, string label, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var coordinate = SecretCoordinate.Format(_options.DeploymentId, owner, name, _options.OwnerKind);
        var sealedValue = Seal(owner, name, label ?? "", value);
        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ic-deployment"] = _options.DeploymentId,
            ["ic-owner-kind"] = _options.OwnerKind,
            ["ic-name"] = name,
        };
        try
        {
            await _client.SetSecretAsync(coordinate, sealedValue, tags, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 409 || string.Equals(ex.ErrorCode, "Conflict", StringComparison.OrdinalIgnoreCase))
        {
            // Recover soft-deleted secret during Azure Key Vault's retention window and retry Set once.
            await _client.RecoverDeletedSecretAsync(coordinate, cancellationToken).ConfigureAwait(false);
            await _client.SetSecretAsync(coordinate, sealedValue, tags, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<bool> RemoveAsync(string owner, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var coordinate = SecretCoordinate.Format(_options.DeploymentId, owner, name, _options.OwnerKind);
        return await _client.DeleteSecretAsync(coordinate, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<string>> ListValuesAsync(string owner, CancellationToken cancellationToken = default)
    {
        var entries = await ListEntriesAsync(owner, cancellationToken).ConfigureAwait(false);
        return [.. entries.Select(e => e.Value)];
    }

    public async ValueTask<IReadOnlyList<SecretEntry>> ListEntriesAsync(string owner, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        var prefix = SecretCoordinate.OwnerPrefix(_options.DeploymentId, owner, _options.OwnerKind);
        var items = await _client.ListSecretsByPrefixAsync(prefix, cancellationToken).ConfigureAwait(false);
        var entries = new List<SecretEntry>(items.Count);
        foreach (var item in items)
        {
            if (!item.Tags.TryGetValue("ic-name", out var secretName) || string.IsNullOrWhiteSpace(secretName))
            {
                continue;
            }

            entries.Add(Unseal(owner, secretName, item.Value));
        }

        return entries;
    }

    private string Seal(string owner, string name, string label, string value)
    {
        var aad = SecretCoordinate.FormatAad(_options.DeploymentId, owner, name);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new SealedVaultEnvelope(name, label, value), SecretBackendJsonContext.Default.SealedVaultEnvelope);
        return Convert.ToBase64String(_custody.Wrap(owner, aad, payload));
    }

    private SecretEntry Unseal(string owner, string name, string sealedBase64)
    {
        var aad = SecretCoordinate.FormatAad(_options.DeploymentId, owner, name);
        var cipherBytes = Convert.FromBase64String(sealedBase64);
        var plainBytes = _custody.Unwrap(owner, aad, cipherBytes);
        var envelope = JsonSerializer.Deserialize(plainBytes, SecretBackendJsonContext.Default.SealedVaultEnvelope)
            ?? throw new CryptographicException("Invalid sealed vault envelope.");
        return new SecretEntry(envelope.Name, envelope.Label, envelope.Value);
    }
}

internal sealed record SealedVaultEnvelope(string Name, string Label, string Value);

[JsonSerializable(typeof(SealedVaultEnvelope))]
internal sealed partial class SecretBackendJsonContext : JsonSerializerContext;
