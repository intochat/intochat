namespace DigitalBrain.Capabilities.Secrets;

/// <summary>
/// A secret entry stored by an <see cref="ISecretStoreBackend"/>.
/// <see cref="ToString"/> never prints <see cref="Value"/>.
/// </summary>
public sealed record SecretEntry(string Name, string Label, string Value)
{
    /// <summary>Returns the secret name and label, never the value.</summary>
    public override string ToString() => string.IsNullOrEmpty(Label) ? Name : $"{Name} ({Label})";
}

/// <summary>
/// Pure asynchronous storage driver SPI for secret vaults, keyed by <c>(owner, name)</c>.
/// Implementations never inspect <see cref="DigitalBrain.Access.Caller"/> or perform authorization;
/// authorization, capability policy enforcement, signal publication, and redaction cache updates
/// happen exclusively in the vault gate above this SPI.
/// </summary>
public interface ISecretStoreBackend
{
    /// <summary>Retrieves the secret <paramref name="name"/> for <paramref name="owner"/>, or <see langword="null"/> when absent.</summary>
    ValueTask<SecretEntry?> GetAsync(string owner, string name, CancellationToken cancellationToken = default);

    /// <summary>Stores or overwrites <paramref name="value"/> under <paramref name="name"/> for <paramref name="owner"/>.</summary>
    ValueTask SetAsync(string owner, string name, string label, string value, CancellationToken cancellationToken = default);

    /// <summary>Removes <paramref name="name"/> for <paramref name="owner"/>; returns <see langword="true"/> if an entry existed.</summary>
    ValueTask<bool> RemoveAsync(string owner, string name, CancellationToken cancellationToken = default);

    /// <summary>Lists all plaintext secret values for <paramref name="owner"/> (for kernel secret redaction only).</summary>
    ValueTask<IReadOnlyList<string>> ListValuesAsync(string owner, CancellationToken cancellationToken = default);

    /// <summary>Lists all secret entries for <paramref name="owner"/>.</summary>
    ValueTask<IReadOnlyList<SecretEntry>> ListEntriesAsync(string owner, CancellationToken cancellationToken = default);
}
