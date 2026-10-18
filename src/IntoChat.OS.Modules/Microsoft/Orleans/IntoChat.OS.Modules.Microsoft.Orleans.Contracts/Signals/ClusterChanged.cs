using IntoChat.OS;

namespace IntoChat.OS.Microsoft.Orleans;

[GenerateSerializer, Alias("orleans.cluster.changed")]
public sealed record ClusterChanged([property: Id(0)] ClusterSnapshot Snapshot) : Signal;
