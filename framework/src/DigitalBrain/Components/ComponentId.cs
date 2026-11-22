namespace DigitalBrain.Components;

/// <summary>
/// Stable identity of a component (e.g. an app or module) that owns a neuron class.
/// </summary>
public readonly record struct ComponentId(string Value)
{
    public override string ToString() => Value ?? string.Empty;
}
