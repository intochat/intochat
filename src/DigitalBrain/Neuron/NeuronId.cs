namespace DigitalBrain;

/// <summary>
/// A neuron's name inside its brain. Identity as a value: it names a neuron, it grants nothing.
/// </summary>
/// <remarks>
/// Exists so that no code outside the brain builds or parses grain keys. The brain alone maps an
/// id to a grain (<see cref="IBrain.Neuron{T}"/>); everything else, signals and synapses included,
/// carries this value. Names have no path separators or control characters so they compose into
/// keys and routes unchanged.
/// </remarks>
[GenerateSerializer, Alias("neuron.id"), Immutable]
public readonly record struct NeuronId
{
    /// <summary>The name, unique within its brain.</summary>
    [Id(0)] public string Name { get; }

    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is empty, or contains a path separator or a control character.
    /// </exception>
    public NeuronId(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Any(c => char.IsControl(c) || c is '/' or '\\'))
        { throw new ArgumentException("A neuron name has no path separators or control characters.", nameof(name)); }
        Name = name;
    }

    public override string ToString() => Name;
}
