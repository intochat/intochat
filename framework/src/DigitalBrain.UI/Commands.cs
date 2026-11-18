using System.Text.Json;

namespace DigitalBrain.UI;

/// <summary>A typed instruction a view sends to a neuron: the signal to deliver and where.</summary>
/// <param name="Target">A neuron address relative to the view's neuron; <c>"."</c> is the neuron itself, <c>"dark-mode"</c> a child.</param>
/// <param name="Signal">The alias of the signal to deliver.</param>
/// <param name="Args">The signal payload as JSON (web casing), if any.</param>
public sealed record Command(string Target, string Signal, JsonElement? Args = null)
{
    /// <inheritdoc />
    public bool Equals(Command? other) => other is not null && Target == other.Target && Signal == other.Signal && Args?.GetRawText() == other.Args?.GetRawText();

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Target, Signal, Args?.GetRawText());
}

/// <summary>Gives a control property one example value (JSON) for the showcase; repeat for several.</summary>
/// <param name="exampleJson">The example as JSON.</param>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property, AllowMultiple = true)]
public sealed class ShowcaseAttribute(string exampleJson) : Attribute
{
    /// <summary>The example as JSON.</summary>
    public string ExampleJson { get; } = exampleJson;
}

/// <summary>Marks a control property as client-local state that is not part of the persisted view.</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
public sealed class EphemeralAttribute : Attribute;

/// <summary>Names a domain signal a control neuron publishes when it saves, for the generator and the showcase catalog; repeat for several.</summary>
/// <param name="signal">The signal record type.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class EmitsAttribute(Type signal) : Attribute
{
    /// <summary>The signal record type.</summary>
    public Type Signal { get; } = signal;
}
