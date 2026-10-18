namespace IntoChat.OS;

/// <summary>A neuron of this brain wrote its state for the first time. Published by the brain's <see cref="IBrain"/>.</summary>
/// <param name="Type">The neuron's grain type.</param>
/// <param name="Key">The neuron's key.</param>
[GenerateSerializer, Alias("kernel.instance-created")]
public sealed record InstanceCreated([property: Id(0)] string Type, [property: Id(1)] string Key) : Signal;
