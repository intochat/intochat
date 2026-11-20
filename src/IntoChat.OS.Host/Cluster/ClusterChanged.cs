using DigitalBrain;

namespace IntoChat.OS.Host.Cluster;

[Signal("orleans.cluster.changed")]
public sealed record ClusterChanged(ClusterSnapshot Snapshot) : DigitalBrain.Signal;
