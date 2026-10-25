namespace IntoChat.OS.Contracts.Compute;

public enum UsageGrouping
{
    Origin = 0,
    App = 1,
    Synapse = 2,
    Neuron = 3,
    Kind = 4,
}

[GenerateSerializer, Alias("compute.cost-row")]
public sealed record CostRow(
    [property: Id(0)] string Key,
    [property: Id(1)] long Micros,
    [property: Id(2)] long Count)
{
    [Id(3)] public long? KnownListPriceMicros { get; init; }
    [Id(4)] public long? UnpricedCount { get; init; }
    [Id(5)] public long? PendingCount { get; init; }
    [Id(6)] public long? UnknownCount { get; init; }
}

[GenerateSerializer, Alias("compute.cost-summary")]
public sealed record CostSummary(
    [property: Id(0)] long BalanceMicros,
    [property: Id(1)] long SpentMicros,
    [property: Id(2)] CostRow[] Rows)
{
    [Id(3)] public long? KnownListPriceMicros { get; init; }
    [Id(4)] public long? UnpricedCount { get; init; }
    [Id(5)] public long? PendingCount { get; init; }
    [Id(6)] public long? UnknownCount { get; init; }
}
