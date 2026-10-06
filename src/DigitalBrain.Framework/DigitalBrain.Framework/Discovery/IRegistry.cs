namespace DigitalBrain.Framework;

public interface IRegistry
{
    Task<RegistryInventory> Persisted(string? typeId = null, CancellationToken ct = default);
    Task<RegistryInventory> Active(string? typeId = null, CancellationToken ct = default);
}

[GenerateSerializer, Alias("registry.inventory")]
public sealed record RegistryInventory(
    [property: Id(0)] NeuronRecord[] Records,
    [property: Id(1)] string[] Domains,
    [property: Id(2)] RegistryDiscoveryError[] Errors);

[GenerateSerializer, Alias("registry.neuron-record")]
public sealed record NeuronRecord(
    [property: Id(0)] string Domain,
    [property: Id(1)] string TypeId,
    [property: Id(2)] string Key,
    [property: Id(3)] string Display);

public interface IRegistryInventoryProvider
{
    string Domain { get; }
    Task<NeuronRecord[]> Persisted(string? typeId, CancellationToken ct);
    Task<NeuronRecord[]> Active(string? typeId, CancellationToken ct);
}
