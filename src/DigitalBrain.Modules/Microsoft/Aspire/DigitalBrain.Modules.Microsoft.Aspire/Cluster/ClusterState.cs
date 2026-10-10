namespace DigitalBrain.Microsoft.Aspire.Cluster;

[GenerateSerializer, Alias("aspire.cluster.state")]
internal sealed record ClusterState
{
    [Id(0)] public string CurrentSha { get; init; } = "";
    [Id(1)] public ClusterStage Stage { get; init; } = ClusterStage.Steady;
    [Id(2)] public string? TargetSha { get; init; }
    // Rolling: the instances still on the previous build, in roll order.
    [Id(3)] public string[] Remaining { get; init; } = [];
    [Id(4)] public string? PreviousSha { get; init; }
    // Rolling: the silo whose own restart was issued detached; the next activation on the target build ends the roll.
    [Id(5)] public string? Handover { get; init; }
}
