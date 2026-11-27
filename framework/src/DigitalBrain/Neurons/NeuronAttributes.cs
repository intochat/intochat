namespace DigitalBrain.Neurons;

/// <summary>Marks a neuron class. The type name is frozen: it is part of the storage key.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class NeuronAttribute(string type) : Attribute
{
    /// <summary>The frozen neuron type name.</summary>
    public string Type { get; } = type;

    /// <summary>The frozen persisted state name, or null for the default. Set it when a class moves so stored keys never move.</summary>
    public string? State { get; init; }
}

/// <summary>Marks a reaction: a method that runs when a signal arrives through a synapse. Signals arrive deduped and ordered per source.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class OnAttribute : Attribute
{
    /// <summary>Marks a reaction or app operation.</summary>
    public OnAttribute() { }
}

/// <summary>Names a static predicate on the class that decides, per call, whether the call may interleave.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class InterleavesWhenAttribute(string predicate) : Attribute
{
    /// <summary>The name of the static predicate.</summary>
    public string Predicate { get; } = predicate;
}

/// <summary>Opts a neuron class into reentrancy: any call may interleave with any other.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class ReentrantAttribute : Attribute;

/// <summary>Marks a neuron class that keeps no state: the host may run many activations of it and route calls to any.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class StatelessNeuronAttribute : Attribute;

/// <summary>
/// Declares that a component reacts to a signal from a source type (e.g., via <c>Follow</c>).
/// Declarative auto-wake targets singleton <c>"default"</c>-keyed neurons and applies to durable
/// journaled signals (ephemeral offers are delivered only to active subscribers).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class ReactsAttribute(Type source, Type signal) : Attribute
{
    /// <summary>The source contract or neuron type that emits the signal.</summary>
    public Type Source { get; } = source;

    /// <summary>The signal type reacted to.</summary>
    public Type Signal { get; } = signal;
}
