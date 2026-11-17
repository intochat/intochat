using DigitalBrain.Access;
using DigitalBrain.Neurons;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Contracts;
using DigitalBrain.Identity.Local.Custody;

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

    /// <summary>Lists all plaintext secret values in the vault for masking.</summary>
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

    /// <summary>The wrapped owner key (`mk3:...`).</summary>
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

/// <summary>Plain neuron hosting one principal's secret vault.</summary>
[Neuron(IVault.Type, State = "mydata.state")]
public sealed class VaultNeuron(IMasterKeyCustody custody) : Neuron<VaultState>, IVault
{
    private const int SecretFieldKind = 1;
    private readonly MasterKeyWrapper _keys = new(custody);

    /// <inheritdoc />
    public async Task<SecretRef> Set(string name, string label, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (Context.Caller is not { } caller || (ActsFor(caller) is { } principal && principal != Principal()))
        {
            throw new InvalidOperationException("Only the owner can store a secret.");
        }

        var next = Copy(State);
        next.Owner = Principal();
        var ownerKey = OwnerKey(next);
        next.Fields[name] = new SecretRecord
        {
            FieldPath = name,
            Kind = SecretFieldKind,
            IsSecret = true,
            Label = label ?? "",
            IsSet = true,
            SealedSecret = SecretCipher.Seal(ownerKey, value),
        };
        await Save(next, new VaultChanged(name)).ConfigureAwait(true);
        return new SecretRef
        {
            Reference = $"secret://{Principal()}/{name}",
            Label = label ?? "",
            Status = SecretStatus.Set,
        };
    }

    /// <inheritdoc />
    public async Task Remove(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!IsOwner(Context.Caller) && !IsKernel(Context.Caller))
        {
            throw new InvalidOperationException("Only the owner or platform can remove a secret.");
        }

        if (!State.Fields.ContainsKey(name))
        {
            return;
        }

        var next = Copy(State);
        next.Fields.Remove(name);
        await Save(next, new VaultChanged(name)).ConfigureAwait(true);
    }

    /// <inheritdoc />
    public Task<string> Resolve(SecretRef secret)
    {
        // An outbound call: an app acting for a person (the app proxy stamps it), or platform code in the kernel scope.
        if (!(Context.Caller?.Actor is Actor.App app && app.For.Principal == Principal()) && Context.Caller?.Actor is not Actor.Kernel)
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
        if (string.IsNullOrEmpty(name) || !State.Fields.TryGetValue(name, out var record) || !record.IsSecret)
        {
            throw new InvalidOperationException("The secret is not present.");
        }

        var ownerKey = _keys.Unwrap(State.WrappedOwnerKey);
        return Task.FromResult(SecretCipher.Open(ownerKey, record.SealedSecret));
    }

    /// <inheritdoc />
    public Task<string[]> Values()
    {
        if (!IsOwner(Context.Caller) && !IsKernel(Context.Caller))
        {
            throw new UnauthorizedAccessException("Only the vault's owner can list its values.");
        }

        if (string.IsNullOrEmpty(State.WrappedOwnerKey))
        {
            return Task.FromResult<string[]>([]);
        }

        var ownerKey = _keys.Unwrap(State.WrappedOwnerKey);
        return Task.FromResult<string[]>(
            [.. State.Fields.Values
                .Where(record => record.IsSecret && !string.IsNullOrEmpty(record.SealedSecret))
                .Select(record => SecretCipher.Open(ownerKey, record.SealedSecret))]);
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

    // The owner calling for themselves, as the stamped caller says; an agent, an app or a relay acting for them is not the owner.
    private bool IsOwner(Caller? caller) => caller is { Actor: Actor.Person person, Direct: true } && person.Principal == Principal();

    private static bool IsKernel(Caller? caller) => caller?.Actor is Actor.Kernel;

    // Whom the caller acts for: the person, or the person an app or agent acts for; null for the kernel, a module or the scheduler.
    private static string? ActsFor(Caller caller) => caller.Actor switch
    {
        Actor.Person person => person.Principal,
        Actor.App app => app.For.Principal,
        Actor.Agent agent => agent.For.Principal,
        _ => null,
    };

    private static VaultState Copy(VaultState state) => new()
    {
        Owner = state.Owner,
        WrappedOwnerKey = state.WrappedOwnerKey,
        Fields = new Dictionary<string, SecretRecord>(state.Fields, StringComparer.Ordinal),
    };
}
