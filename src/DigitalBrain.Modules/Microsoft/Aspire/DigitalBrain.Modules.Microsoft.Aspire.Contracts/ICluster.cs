using DigitalBrain;
using Orleans.Concurrency;
using Orleans.Metadata;

namespace DigitalBrain.Microsoft.Aspire;

[GenerateSerializer, Alias("aspire.cluster.stage")]
public enum ClusterStage { Steady, Restarting, Rolling, RollingBack }

[GenerateSerializer, Alias("aspire.cluster.silo")]
public sealed record SiloView([property: Id(0)] string Name, [property: Id(1)] string Sha, [property: Id(2)] string State, [property: Id(3)] string? Health);

[GenerateSerializer, Alias("aspire.cluster.snapshot")]
public sealed record ClusterSnapshot(
    [property: Id(0)] string CurrentSha,
    [property: Id(1)] ClusterStage Stage,
    [property: Id(2)] string? TargetSha,
    [property: Id(3)] string[] Remaining,
    [property: Id(4)] string? Failure);

[Alias("aspire.cluster"), DefaultGrainType("aspire.cluster")]
public interface ICluster : INeuron
{
    [ReadOnly, AlwaysInterleave] Task<ClusterSnapshot> Read();
    [AlwaysInterleave] Task<SiloView[]> Silos(CancellationToken cancellationToken = default);
    [ResponseTimeout("00:10:00")] Task<ClusterSnapshot> Restart(string sha);
    // Instance by instance onto a compatible build: the MCP clients, then the silos, the one running the roll last.
    // Answers once the roll is under way; its end is ClusterChanged(Steady): on sha, or back on the previous build
    // with the Failure that sent it back, which Read() reports. Callers follow a roll through Read()/ClusterChanged and never resend Roll
    // while it runs: a step holds the cluster for up to two SiloStepBudgets, and a resend queued behind it outlives
    // the default response timeout. Refused unless the calling process is one of the cluster's instances.
    Task<ClusterSnapshot> Roll(string sha);
    static string Key(string brainId) => $"{brainId}/aspire/cluster";
}
