using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Contracts;
using DigitalBrain.Identity.Local.Custody;
using DigitalBrain.Neurons;

namespace DigitalBrain.Identity.Local.Vault;

/// <summary>One principal's secret vault in the system brain.</summary>
[NeuronAlias(IVault.Type), DefaultNeuronType(IVault.Type)]
public interface IVault
{
    /// <summary>The frozen neuron type name.</summary>
    public const string Type = "identity.vault";

    /// <summary>Formats the full neuron key for <paramref name="principal"/>.</summary>
    public static string Key(string principal) => "system/" + principal;

    /// <summary>Stores <paramref name="value"/> under <paramref name="name"/>.</summary>
    Task<SecretRef> Set(string name, string label, string value);

    /// <summary>Removes <paramref name="name"/> if present.</summary>
    Task Remove(string name);

    /// <summary>Resolves the plaintext value behind <paramref name="secret"/>.</summary>
    Task<string> Resolve(SecretRef secret);

    /// <summary>Lists all plaintext secret values in the vault for kernel masking only.</summary>
    Task<string[]> Values();
}

/// <summary>Emitted when a vault secret is stored or removed.</summary>
[Signal("identity.vault-changed")]
public sealed record VaultChanged(string Name) : Signal;

/// <summary>Persisted state of a <see cref="VaultNeuron"/>.</summary>
[NeuronAlias("mydata.state")]
public sealed class VaultState
{
    /// <summary>The owning principal identifier.</summary>
    public string Owner { get; set; } = "";

    /// <summary>The wrapped owner key (<c>mk4:...</c>; dual-opens legacy <c>mk3:...</c>).</summary>
    public string WrappedOwnerKey { get; set; } = "";

    /// <summary>Stored fields by secret name.</summary>
    public Dictionary<string, SecretRecord> Fields { get; set; } = [];
}

/// <summary>A persisted field record inside <see cref="VaultState"/>.</summary>
[NeuronAlias("mydata.field-record")]
public sealed class SecretRecord
{
    /// <summary>The field name or path.</summary>
    public string FieldPath { get; set; } = "";

    /// <summary>The numeric field kind.</summary>
    public int Kind { get; set; }

    /// <summary>Whether the field holds a secret.</summary>
    public bool IsSecret { get; set; }

    /// <summary>The human-readable label.</summary>
    public string Label { get; set; } = "";

    /// <summary>Whether a value is stored.</summary>
    public bool IsSet { get; set; }

    /// <summary>Sealed non-secret value, if any.</summary>
    public string SealedValue { get; set; } = "";

    /// <summary>Sealed secret ciphertext.</summary>
    public string SealedSecret { get; set; } = "";
}

/// <summary>
/// Plain neuron hosting one principal's secret vault. Serves as the single authorization gate,
/// capability-policy gate, <see cref="VaultChanged"/> signal publisher, and redaction-cache
/// write-through coordinator over either the default sealed <see cref="VaultState"/> or a
/// pluggable <see cref="ISecretStoreBackend"/>.
/// </summary>
[Neuron(IVault.Type, State = "mydata.state")]
public sealed class VaultNeuron(
    IMasterKeyCustody custody,
    MasterKeyWrapper? keyWrapper = null,
    ISecretStoreBackend? backend = null,
    IBrainCapabilityPolicy? capabilityPolicy = null,
    CachingSecretValueSource? redactionCache = null) : Neuron<VaultState>, IVault
{
    private const int SecretFieldKind = 1;
    private readonly MasterKeyWrapper _keys = keyWrapper ?? new MasterKeyWrapper(custody);
    private readonly ISecretStoreBackend? _backend = backend;
    private readonly IBrainCapabilityPolicy? _capabilityPolicy = capabilityPolicy;
    private readonly CachingSecretValueSource? _redactionCache = redactionCache;

    /// <inheritdoc />
    public async Task<SecretRef> Set(string name, string label, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!VaultPolicy.CanStore(Context.Caller, Principal()))
        {
            throw new InvalidOperationException("Only the owner can store a secret.");
        }

        await EnforceDurableSecretsPolicyAsync(Context.Caller, name).ConfigureAwait(true);

        var resolvedLabel = label ?? "";
        if (_backend is not null)
        {
            await _backend.SetAsync(Principal(), name, resolvedLabel, value).ConfigureAwait(true);
            var nextExternal = Copy(State);
            nextExternal.Owner = Principal();
            await Save(nextExternal, new VaultChanged(name)).ConfigureAwait(true);
        }
        else
        {
            var next = Copy(State);
            next.Owner = Principal();
            var ownerKey = OwnerKey(next);
            next.Fields[name] = new SecretRecord
            {
                FieldPath = name,
                Kind = SecretFieldKind,
                IsSecret = true,
                Label = resolvedLabel,
                IsSet = true,
                SealedSecret = SecretCipher.Seal(ownerKey, value),
            };
            await Save(next, new VaultChanged(name)).ConfigureAwait(true);
        }

        _redactionCache?.NotifySet(Principal(), value);
        return new SecretRef
        {
            Reference = $"secret://{Principal()}/{name}",
            Label = resolvedLabel,
            Status = SecretStatus.Set,
        };
    }

    /// <inheritdoc />
    public async Task Remove(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!VaultPolicy.CanRemove(Context.Caller, Principal()))
        {
            throw new InvalidOperationException("Only the owner or platform can remove a secret.");
        }

        if (_backend is not null)
        {
            var removed = await _backend.RemoveAsync(Principal(), name).ConfigureAwait(true);
            if (!removed)
            {
                return;
            }

            var nextExternal = Copy(State);
            nextExternal.Owner = Principal();
            await Save(nextExternal, new VaultChanged(name)).ConfigureAwait(true);
            _redactionCache?.NotifyRemove(Principal());
            return;
        }

        if (!State.Fields.ContainsKey(name))
        {
            return;
        }

        var next = Copy(State);
        next.Fields.Remove(name);
        await Save(next, new VaultChanged(name)).ConfigureAwait(true);
        _redactionCache?.NotifyRemove(Principal());
    }

    /// <inheritdoc />
    public async Task<string> Resolve(SecretRef secret)
    {
        if (!VaultPolicy.CanResolve(Context.Caller, Principal()))
        {
            throw new UnauthorizedAccessException("Only a trusted outbound call can resolve a secret.");
        }

        ArgumentNullException.ThrowIfNull(secret);
        var prefix = $"secret://{Principal()}/";
        if (secret.Reference is null || !secret.Reference.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The secret belongs to another owner.");
        }

        var name = secret.Reference[prefix.Length..];
        if (string.IsNullOrEmpty(name))
        {
            throw new InvalidOperationException("The secret is not present.");
        }

        if (_backend is not null)
        {
            var entry = await _backend.GetAsync(Principal(), name).ConfigureAwait(true)
                ?? throw new InvalidOperationException("The secret is not present.");
            return entry.Value;
        }

        if (!State.Fields.TryGetValue(name, out var record) || !record.IsSecret)
        {
            throw new InvalidOperationException("The secret is not present.");
        }

        var ownerKey = _keys.Unwrap(State.WrappedOwnerKey);
        return SecretCipher.Open(ownerKey, record.SealedSecret);
    }

    /// <inheritdoc />
    public async Task<string[]> Values()
    {
        // Strictly Kernel-only: used exclusively by SecretRedactor through the vault value source.
        if (!VaultPolicy.CanReadValues(Context.Caller))
        {
            throw new UnauthorizedAccessException("Only the platform's secret redactor lists a vault's values.");
        }

        if (_backend is not null)
        {
            var externalValues = await _backend.ListValuesAsync(Principal()).ConfigureAwait(true);
            return [.. externalValues];
        }

        if (string.IsNullOrEmpty(State.WrappedOwnerKey))
        {
            return [];
        }

        var ownerKey = _keys.Unwrap(State.WrappedOwnerKey);
        return
        [
            .. State.Fields.Values
                .Where(record => record.IsSecret && !string.IsNullOrEmpty(record.SealedSecret))
                .Select(record => SecretCipher.Open(ownerKey, record.SealedSecret)),
        ];
    }

    private async Task EnforceDurableSecretsPolicyAsync(Caller? caller, string secretName)
    {
        if (_capabilityPolicy is null || caller is null)
        {
            return;
        }

        var brainId = caller.Brain.Value;
        if (string.IsNullOrWhiteSpace(brainId) || string.Equals(brainId, BrainId.Kernel.Value, StringComparison.Ordinal))
        {
            return;
        }

        var decision = await _capabilityPolicy.EvaluateAsync(brainId, BrainCapability.DurableSecrets, secretName).ConfigureAwait(true);
        if (!decision.Allowed)
        {
            throw new ClaimRequiredException(decision);
        }
    }

    private byte[] OwnerKey(VaultState state)
    {
        if (!string.IsNullOrEmpty(state.WrappedOwnerKey))
        {
            return _keys.Unwrap(state.WrappedOwnerKey);
        }

        var key = SecretCipher.NewKey();
        state.WrappedOwnerKey = _keys.Wrap(key);
        return key;
    }

    private string Principal() => Context.Id.Key;

    private static VaultState Copy(VaultState state) => new()
    {
        Owner = state.Owner,
        WrappedOwnerKey = state.WrappedOwnerKey,
        Fields = new Dictionary<string, SecretRecord>(state.Fields, StringComparer.Ordinal),
    };
}
