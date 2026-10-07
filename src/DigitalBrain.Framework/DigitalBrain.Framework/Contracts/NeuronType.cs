namespace DigitalBrain.Framework.Contracts;

[GenerateSerializer, Alias("registry.neuron-type")]
public sealed record NeuronType(
    [property: Id(0)] string Id,
    [property: Id(1)] string ModuleId,
    [property: Id(2)] string Name,
    [property: Id(3)] string Description,
    [property: Id(4)] IReadOnlyList<string> Methods,
    [property: Id(5)] string Contract = "");

[GenerateSerializer, Alias("registry.type-hit")]
public sealed record NeuronTypeHit([property: Id(0)] NeuronType Type, [property: Id(1)] double Score);
