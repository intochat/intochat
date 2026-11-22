namespace DigitalBrain.Components;

/// <summary>One slot a component neuron declares it needs bound at activation.</summary>
/// <param name="Name">The lower-camel-case slot name.</param>
/// <param name="Contract">The contract interface type required by the slot.</param>
/// <param name="Required">Whether activation refuses when no binding or default exists.</param>
/// <param name="Key">Optional neuron key within the installing brain when no explicit binding is installed.</param>
public sealed record SlotNeed(string Name, Type Contract, bool Required = true, string? Key = null);
