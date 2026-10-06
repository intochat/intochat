namespace DigitalBrain;

/// <summary>
/// A neuron's name inside its brain. Identity as a value: it names a neuron, it grants nothing.
/// </summary>
/// <remarks>
/// Exists so that no code outside the brain builds or parses grain keys. The brain alone maps an
/// id to a grain (<see cref="IDigitalBrain.Get{T}"/>); everything else, signals and synapses
/// included, carries this value. A string converts implicitly, so a program writes
/// <c>brain.Get&lt;IButton&gt;("save")</c>.
/// </remarks>
[GenerateSerializer, Alias("neuron.id"), Immutable]
public readonly record struct NeuronId
{
    /// <summary>The name, unique within its brain.</summary>
    [Id(0)] public string Name { get; }

    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or contains a control character.</exception>
    public NeuronId(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Any(char.IsControl))
        { throw new ArgumentException("A neuron name has no control characters.", nameof(name)); }
        Name = name;
    }

    public static implicit operator NeuronId(string name)
    {
        return new(name);
    }

    public override string ToString() => Name;
}
