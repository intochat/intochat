using System.Text.Json;

namespace DigitalBrain.UI;

/// <summary>A typed instruction a view sends to a neuron: the signal to deliver and where.</summary>
/// <param name="Target">A neuron address relative to the view's neuron; <c>"."</c> is the neuron itself, <c>"dark-mode"</c> a child.</param>
/// <param name="Signal">The alias of the signal to deliver.</param>
/// <param name="Args">The signal payload as JSON (web casing), if any.</param>
public sealed record Command(string Target, string Signal, JsonElement? Args = null);

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
