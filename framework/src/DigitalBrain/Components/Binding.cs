using System.Text.Json.Serialization;

namespace DigitalBrain.Components;

/// <summary>What a component's declared slot is bound to.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(Local), "local")]
[JsonDerivedType(typeof(Connection), "connection")]
[JsonDerivedType(typeof(Offered), "offered")]
public abstract record BindingTarget
{
    private BindingTarget() { }

    /// <summary>A neuron inside the installing brain.</summary>
    /// <param name="Neuron">The target neuron id.</param>
    public sealed record Local(NeuronId Neuron) : BindingTarget;

    /// <summary>A capability connection of the installing brain.</summary>
    /// <param name="ConnectionKey">The connection key.</param>
    public sealed record Connection(string ConnectionKey) : BindingTarget;

    /// <summary>A neuron offered by another brain.</summary>
    /// <param name="Owner">The brain that owns the offered neuron.</param>
    /// <param name="Neuron">The target neuron id.</param>
    public sealed record Offered(BrainId Owner, NeuronId Neuron) : BindingTarget;
}

/// <summary>One typed slot binding in an install record.</summary>
/// <param name="Slot">The lower-camel-case slot name.</param>
/// <param name="Target">What the slot is bound to.</param>
public sealed record Binding(string Slot, BindingTarget Target);
