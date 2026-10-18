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
    public BrainId? Brain => ScopeOf(Key) is { } brain ? new BrainId(brain) : null;

    public string StorageKey => $"{NeuronType}/{Key}";

    public override string ToString() => StorageKey;

    // The same rule as Framework BrainKey.ScopeOf: the first '/'-segment when it is a valid brain id.
    private static string? ScopeOf(string? key)
    {
        if (key is null) { return null; }
        var separator = key.IndexOf('/', StringComparison.Ordinal);
        if (separator <= 0) { return null; }
        var scope = key[..separator];
        return scope.Length <= 200 && scope[0] != '@' && !scope.Any(c => char.IsControl(c) || c is '\\' or ':') && !string.IsNullOrWhiteSpace(scope)
            ? scope : null;
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
