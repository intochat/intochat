namespace IntoChat.OS.Framework.Compute;

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
    [property: Id(2)] long Count);

[GenerateSerializer, Alias("compute.cost-summary")]
public sealed record CostSummary(
    [property: Id(0)] long BalanceMicros,
    [property: Id(1)] long SpentMicros,
    [property: Id(2)] CostRow[] Rows);
