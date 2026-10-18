using DigitalBrain;
using Orleans.Concurrency;
using Orleans.Metadata;

namespace DigitalBrain.Microsoft.Aspire;

[GenerateSerializer, Alias("aspire.deployment.stage")]
public enum DeploymentStage { Steady, Restarting, Rolling, RollingBack }

[GenerateSerializer, Alias("aspire.deployment.snapshot")]
public sealed record DeploymentSnapshot(
    [property: Id(0)] string CurrentSha,
    [property: Id(1)] DeploymentStage Stage,
    [property: Id(2)] string? TargetSha,
    [property: Id(3)] string[] Remaining,
    [property: Id(4)] string? Failure);

[Alias("aspire.deployment"), DefaultGrainType("aspire.deployment")]
public interface IDeployment : INeuron
{
    [ReadOnly, AlwaysInterleave] Task<DeploymentSnapshot> Read();
    [ResponseTimeout("00:10:00")] Task<DeploymentSnapshot> Restart(string sha);
    // Instance by instance onto a compatible build: the MCP clients, then the silos, the one running the roll last.
    // Answers once the roll is under way; its end is DeploymentChanged(Steady): on sha, or back on the previous build
    // with the Failure that sent it back, which Read() reports. Callers follow a roll through Read()/DeploymentChanged and never resend Roll
    // while it runs: a step holds the cluster for up to two SiloStepBudgets, and a resend queued behind it outlives
    // the default response timeout. Refused unless the calling process is one of the cluster's instances.
    // Who is up is not this neuron's: the membership is DigitalBrain.Microsoft.Orleans.ICluster.
    Task<DeploymentSnapshot> Roll(string sha);
    static string Key(string brainId) => $"{brainId}/{Relative}";
    // The id inside its brain.
    const string Relative = "aspire/deployment";
}
