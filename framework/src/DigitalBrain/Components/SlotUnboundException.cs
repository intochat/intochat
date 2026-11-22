namespace DigitalBrain.Components;

/// <summary>A component's declared slot has no binding in the current activation.</summary>
/// <param name="component">The component that owns the neuron.</param>
/// <param name="slot">The unbound slot name.</param>
public sealed class SlotUnboundException(ComponentId component, string slot)
    : InvalidOperationException($"Slot '{slot}' is not bound on component '{component}'.")
{
    /// <summary>The component that owns the neuron.</summary>
    public ComponentId Component { get; } = component;

    /// <summary>The unbound slot name.</summary>
    public string Slot { get; } = slot;
}
