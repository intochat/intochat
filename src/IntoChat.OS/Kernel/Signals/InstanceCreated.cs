namespace IntoChat.OS;

/// <summary>A neuron of this brain wrote its state for the first time. Published by the brain's <see cref="IBrain"/>.</summary>
/// <param name="Type">The neuron's grain type.</param>
/// <param name="Key">The neuron's key.</param>
[DigitalBrain.Signal("kernel.instance-created")]
public sealed record InstanceCreated(string Type, string Key) : DigitalBrain.Signal;
