namespace DigitalBrain.Microsoft.Aspire.Cluster;

[GenerateSerializer, Alias("aspire.cluster.state")]
internal sealed record ClusterState
{
    [Id(0)] public string CurrentSha { get; init; } = "";
    [Id(1)] public ClusterStage Stage { get; init; } = ClusterStage.Steady;
    [Id(2)] public string? TargetSha { get; init; }
}
