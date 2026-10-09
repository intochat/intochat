namespace DigitalBrain.Framework.Contracts;

// A capability the brain can search and call. A contract type has no Key; a runtime tool (from an
// IRuntimeCapabilitySource) is bound to one neuron Key and names its Tool, called as Run(Tool, arguments).
[GenerateSerializer, Alias("registry.neuron-type")]
public sealed record NeuronType(
    [property: Id(0)] string Id,
    [property: Id(1)] string ModuleId,
    [property: Id(2)] string Name,
    [property: Id(3)] string Description,
    [property: Id(4)] IReadOnlyList<string> Methods,
    [property: Id(5)] string Contract = "",
    [property: Id(6)] string? Key = null,
    [property: Id(7)] string? Tool = null);

[GenerateSerializer, Alias("registry.type-hit")]
public sealed record NeuronTypeHit([property: Id(0)] NeuronType Type, [property: Id(1)] double Score);
