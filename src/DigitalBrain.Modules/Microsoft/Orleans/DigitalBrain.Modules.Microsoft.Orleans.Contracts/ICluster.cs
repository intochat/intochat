using DigitalBrain;
using Orleans.Concurrency;
using Orleans.Metadata;

namespace DigitalBrain.Microsoft.Orleans;

[GenerateSerializer, Alias("orleans.cluster.silo")]
public sealed record SiloView(
    [property: Id(0)] string Name,
    [property: Id(1)] string Address,
    [property: Id(2)] string Status,
    [property: Id(3)] string? Role,
    [property: Id(4)] string Sha);

[GenerateSerializer, Alias("orleans.cluster.snapshot")]
public sealed record ClusterSnapshot([property: Id(0)] SiloView[] Silos, [property: Id(1)] long Version);

// The membership of the cluster this brain runs on, as Orleans sees it. A roll or restart is the Aspire
// module's act (IDeployment); this neuron only reports who is up.
[Alias("orleans.cluster"), DefaultGrainType("orleans.cluster")]
public interface ICluster : INeuron
{
    [ReadOnly, AlwaysInterleave] Task<ClusterSnapshot> Read();
    static string Key(string brainId) => $"{brainId}/orleans/cluster";
}
