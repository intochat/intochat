using DigitalBrain;

namespace DigitalBrain.Microsoft.Aspire;

[GenerateSerializer, Alias("aspire.cluster.changed")] public sealed record ClusterChanged([property: Id(0)] string CurrentSha, [property: Id(1)] ClusterStage Stage, [property: Id(2)] string? TargetSha) : Signal;
