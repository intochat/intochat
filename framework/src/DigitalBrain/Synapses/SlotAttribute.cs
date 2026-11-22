namespace DigitalBrain;

/// <summary>Marks a synapse parameter or component neuron property as a capability or neuron slot.</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property, Inherited = false)]
public sealed class SlotAttribute : Attribute
{
    /// <summary>The slot name; the parameter or camelCase property name when null.</summary>
    public string? Name { get; init; }

    /// <summary>Optional default neuron key within the installing brain when no explicit binding is installed.</summary>
    public string? Key { get; set; }

    /// <summary>Whether activation refuses when no binding or default exists. Defaults to <see langword="true"/>.</summary>
    public bool Required { get; set; } = true;
}
