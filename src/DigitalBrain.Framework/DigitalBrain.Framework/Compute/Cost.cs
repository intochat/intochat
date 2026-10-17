namespace DigitalBrain.Framework.Compute;

public enum CostKind
{
    Model = 0,
    Script = 1,
    Http = 2,
    Storage = 3,
    Operation = 4,
}

// Money is micro-credits: 1 credit = 1e-6 USD. Micros is what was charged, fee included; 0 when the
// work ran locally or on the payer's own connection.
[GenerateSerializer, Alias("compute.cost")]
public sealed record Cost
{
    [Id(0)] public required string TraceId { get; init; }
    [Id(1)] public required string CostId { get; init; }
    [Id(2)] public required CostKind Kind { get; init; }
    [Id(3)] public required string Item { get; init; }
    [Id(4)] public required decimal Quantity { get; init; }
    [Id(5)] public required string Unit { get; init; }
    [Id(6)] public required long Micros { get; init; }
    [Id(7)] public string? Synapse { get; init; }
    [Id(8)] public string? App { get; init; }
    [Id(9)] public string? Neuron { get; init; }
    [Id(10)] public DateTimeOffset At { get; init; }
}
