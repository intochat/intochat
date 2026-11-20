using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Identity.Local.Vault;

namespace IntoChat.OS.Modules.Identity.Secrets;

internal sealed class SecretsStore(IKeyWrapper keys)
{
    internal SecretRef Set(SecretsState state, string name, string label, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var ownerKey = OwnerKey(state);
        state.Fields[name] = new OsSecretRecord
        {
            FieldPath = name,
            Kind = SemanticFieldKind.Secret,
            IsSecret = true,
            Label = label,
            IsSet = true,
            SealedSecret = SecretCipher.Seal(ownerKey, value),
        };
        return SecretReferences.For(state.Owner, name, label, true);
    }

    internal string Resolve(SecretsState state, SecretRef secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var prefix = $"secret://{state.Owner}/";
        if (!secret.Reference.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The secret belongs to another owner.");
        }

        var name = secret.Reference[prefix.Length..];
        if (string.IsNullOrEmpty(name) || !state.Fields.TryGetValue(name, out var record) || !record.IsSecret)
        {
            throw new InvalidOperationException("The secret is not present.");
        }

        var ownerKey = keys.Unwrap(state.WrappedOwnerKey);
        return SecretCipher.Open(ownerKey, record.SealedSecret);
    }

    // Every plaintext value of this vault, for masking only; the caller keeps them in locals.
    internal string[] Values(SecretsState state)
    {
        if (string.IsNullOrEmpty(state.WrappedOwnerKey)) { return []; }
        var ownerKey = keys.Unwrap(state.WrappedOwnerKey);
        return [.. state.Fields.Values.Where(record => record.IsSecret && !string.IsNullOrEmpty(record.SealedSecret)).Select(record => SecretCipher.Open(ownerKey, record.SealedSecret))];
    }

    private byte[] OwnerKey(SecretsState state)
    {
        if (!string.IsNullOrEmpty(state.WrappedOwnerKey))
        {
            return keys.Unwrap(state.WrappedOwnerKey);
        }

        var key = SecretCipher.NewKey();
        state.WrappedOwnerKey = keys.Wrap(key);
        return key;
    }
}
