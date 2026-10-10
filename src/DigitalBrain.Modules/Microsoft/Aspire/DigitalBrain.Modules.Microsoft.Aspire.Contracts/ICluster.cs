using DigitalBrain;
using Orleans.Concurrency;
using Orleans.Metadata;

namespace DigitalBrain.Microsoft.Aspire;

[GenerateSerializer, Alias("aspire.cluster.stage")]
public enum ClusterStage { Steady, Restarting, Rolling }

[GenerateSerializer, Alias("aspire.cluster.silo")]
public sealed record SiloView([property: Id(0)] string Name, [property: Id(1)] string Sha, [property: Id(2)] string State, [property: Id(3)] string? Health);

[GenerateSerializer, Alias("aspire.cluster.snapshot")]
public sealed record ClusterSnapshot(
    [property: Id(0)] string CurrentSha,
    [property: Id(1)] ClusterStage Stage,
    [property: Id(2)] string? TargetSha,
    [property: Id(3)] SiloView[] Silos,
    [property: Id(4)] string[] Remaining);

[Alias("aspire.cluster"), DefaultGrainType("aspire.cluster")]
public interface ICluster : INeuron
{
    [ReadOnly, AlwaysInterleave] Task<ClusterSnapshot> Read();
    [AlwaysInterleave] Task<SiloView[]> Silos(CancellationToken cancellationToken = default);
    [ResponseTimeout("00:10:00")] Task<ClusterSnapshot> Restart(string sha);
    // Instance by instance onto a compatible build: the MCP clients, then the silos, the one running this call last.
    [ResponseTimeout("00:30:00")] Task<ClusterSnapshot> Roll(string sha);
    static string Key(string brainId) => $"{brainId}/aspire/cluster";
}
