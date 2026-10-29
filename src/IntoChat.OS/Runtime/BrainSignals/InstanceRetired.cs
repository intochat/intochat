namespace IntoChat.OS;

/// <summary>A neuron of this brain stopped existing. Published by the brain's <see cref="IBrain"/>.</summary>
/// <param name="Type">The neuron's grain type.</param>
/// <param name="Key">The neuron's key.</param>
[DigitalBrain.Signal("kernel.instance-retired")]
public sealed record InstanceRetired(string Type, string Key) : DigitalBrain.Signal;
