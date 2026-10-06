namespace DigitalBrain;

[GenerateSerializer, Alias("synapse.snapshot")]
public sealed record SynapseSnapshot(
    [property: Id(0)] string Id,
    [property: Id(1)] string Program,
    [property: Id(2)] bool Alive,
    [property: Id(3)] SynapseWatch[] Watches,
    [property: Id(4)] string? LastError);
