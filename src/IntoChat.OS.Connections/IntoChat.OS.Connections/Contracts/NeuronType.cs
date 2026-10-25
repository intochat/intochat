using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;

namespace IntoChat.OS.Connections.Contracts;

// A capability the brain can search and call. A kernel neuron type has no Key; a live tool (from an
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
    [property: Id(7)] string? Tool = null)
{
    // A kernel neuron type as a searchable capability: no Key, called on any instance of it.
    public static NeuronType From(TypeEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var name = entry.Name[(entry.Name.LastIndexOf('.') + 1)..];
        return new(entry.Alias, entry.Provider, name.StartsWith('I') ? name[1..] : name, entry.Description, entry.Shape, entry.Name);
    }
}

[GenerateSerializer, Alias("registry.type-hit")]
public sealed record NeuronTypeHit([property: Id(0)] NeuronType Type, [property: Id(1)] double Score);
