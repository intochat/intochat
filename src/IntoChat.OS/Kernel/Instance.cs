namespace IntoChat.OS;

/// <summary>A neuron that exists: it has persisted state.</summary>
/// <param name="Type">The neuron's grain type.</param>
/// <param name="Key">The neuron's key: its brain, or starting with its brain and a slash.</param>
/// <param name="Created">When its state was first written.</param>
/// <param name="Creator">The principal stamped on that first write; null when the system wrote it.</param>
[GenerateSerializer, Alias("kernel.instance")]
public sealed record Instance(
    [property: Id(0)] string Type,
    [property: Id(1)] string Key,
    [property: Id(2)] DateTimeOffset Created,
    [property: Id(3)] string? Creator);
