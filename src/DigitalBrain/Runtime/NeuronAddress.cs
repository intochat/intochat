namespace DigitalBrain.Runtime;

// The runtime SPI of the clean-core plan (section 2.3): the only interfaces a runtime implements.

public readonly record struct BrainId(string Value)
{
    public override string ToString() => Value;
}

// Key is the full neuron key, including any "{brain}/" prefix. Brain is derived from it and is null for
// brain-less neurons (platform, cluster, ledger). StorageKey is the string GrainId.ToString() gives today.
public readonly record struct NeuronAddress(string NeuronType, string Key)
{
    public BrainId? Brain => Key is not null && ScopeOf(Key) is { } brain ? new BrainId(brain) : null;

    public string StorageKey => $"{NeuronType}/{Key}";

    public override string ToString() => StorageKey;

    // The one brain-id rule (Framework BrainScope.IsValidId and BrainKey.ScopeOf delegate here): 1-200 characters,
    // no control characters, no path separators and no ':'; a leading '@' is reserved for keys outside every brain.
    public static bool IsBrainId([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 200 && value[0] != '@' && !value.Any(c => char.IsControl(c) || c is '/' or '\\' or ':');

    // The brain a key belongs to: its first '/'-segment when that is a brain id. A key with no '/' names no brain.
    public static string? ScopeOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var separator = key.IndexOf('/', StringComparison.Ordinal);
        return separator > 0 && IsBrainId(key[..separator]) ? key[..separator] : null;
    }
}

// Marks a runtime endpoint that is not a neuron: a secondary face (IGmailInbound), an index (IConnectionIndex),
// a reader. It is reached through INeuronResolver, never through a grain factory.
public interface IRuntimeEndpoint;

// Implemented by each runtime: the only place an id becomes a reference.
public interface INeuronResolver
{
    // Composes "{brain}/{relative}". T is an INeuron or IRuntimeEndpoint contract.
    T Resolve<T>(BrainId brain, NeuronId relative) where T : class;

    // Brain-less neurons and the transitional raw-key path; Framework and Authority only.
    T ResolveKey<T>(string fullKey) where T : class;
}
