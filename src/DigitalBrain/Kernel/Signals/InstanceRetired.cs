namespace DigitalBrain;

/// <summary>A neuron of this brain stopped existing. Published by the brain's <see cref="IBrain"/>.</summary>
/// <param name="Type">The neuron's grain type.</param>
/// <param name="Key">The neuron's key.</param>
[GenerateSerializer, Alias("kernel.instance-retired")]
public sealed record InstanceRetired([property: Id(0)] string Type, [property: Id(1)] string Key) : Signal;
