namespace IntoChat.OS.Modules.Infrastructure.Secrets.Contracts;

/// <summary>A secret item returned by an Azure Key Vault client.</summary>
internal sealed record AzureKeyVaultSecretItem(string Name, string Value, IReadOnlyDictionary<string, string> Tags)
{
    /// <summary>Returns the secret name only; never prints the value.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// Thin asynchronous abstraction over <c>Azure.Security.KeyVault.Secrets.SecretClient</c>
/// so <c>AzureKeyVaultSecretStoreBackend</c> is unit-testable without live Azure connectivity.
/// </summary>
internal interface IAzureKeyVaultSecretClient
{
    /// <summary>Gets the secret with <paramref name="secretName"/>, or <see langword="null"/> if not found.</summary>
    ValueTask<AzureKeyVaultSecretItem?> GetSecretAsync(string secretName, CancellationToken cancellationToken = default);

    /// <summary>Creates or updates <paramref name="secretName"/> with <paramref name="value"/> and <paramref name="tags"/>.</summary>
    ValueTask SetSecretAsync(string secretName, string value, IDictionary<string, string> tags, CancellationToken cancellationToken = default);

    /// <summary>Deletes <paramref name="secretName"/>; returns <see langword="true"/> if it existed.</summary>
    ValueTask<bool> DeleteSecretAsync(string secretName, CancellationToken cancellationToken = default);

    /// <summary>Lists all enabled secrets whose names start with <paramref name="namePrefix"/>.</summary>
    ValueTask<IReadOnlyList<AzureKeyVaultSecretItem>> ListSecretsByPrefixAsync(string namePrefix, CancellationToken cancellationToken = default);
}

/// <summary>A secret record returned by a Bitwarden Secrets Manager client.</summary>
internal sealed record BitwardenSecretRecord(string Id, string Key, string Value, string Note)
{
    /// <summary>Returns the secret key only; never prints the value.</summary>
    public override string ToString() => Key;
}

/// <summary>
/// Thin asynchronous abstraction over Bitwarden Secrets Manager so <c>BitwardenSecretStoreBackend</c>
/// runs deterministically in unit tests and NativeAOT builds without requiring the native FFI binary at test time.
/// </summary>
internal interface IBitwardenSecretsClient
{
    /// <summary>Gets the secret matching <paramref name="key"/>, or <see langword="null"/> if absent.</summary>
    ValueTask<BitwardenSecretRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Creates or updates the secret for <paramref name="key"/>.</summary>
    ValueTask<BitwardenSecretRecord> UpsertAsync(string key, string value, string note, CancellationToken cancellationToken = default);

    /// <summary>Deletes the secret matching <paramref name="key"/>; returns <see langword="true"/> if it existed.</summary>
    ValueTask<bool> DeleteByKeyAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Lists all secrets whose keys start with <paramref name="keyPrefix"/>.</summary>
    ValueTask<IReadOnlyList<BitwardenSecretRecord>> ListByKeyPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default);
}
