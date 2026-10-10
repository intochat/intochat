using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Microsoft.Aspire.Candidates;
using Orleans.Runtime;

namespace DigitalBrain.Microsoft.Aspire.Cluster;

[GrainType("aspire.cluster")]
internal sealed class ClusterNeuron(
    [PersistentState("state", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<ClusterState> store,
    IClusterHost host,
    ICandidateHost candidates)
    : Neuron<ClusterState>(store), ICluster
{
    private string BrainId => BrainKey.BrainOf(this.GetPrimaryKeyString());

    public Task<ClusterSnapshot> Read() => Task.FromResult(Describe());

    public Task<SiloView[]> Silos(CancellationToken cancellationToken = default) => host.Silos(cancellationToken);

    public async Task<ClusterSnapshot> Restart(string sha)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);
        if (Snapshot.Stage == ClusterStage.Restarting && Snapshot.TargetSha == sha) { return Describe(); }
        if (Snapshot.Stage == ClusterStage.Steady && Snapshot.CurrentSha == sha) { return Describe(); }
        // Persisted before acting: the silo running this grain may die in RestartSilos.
        await Save(Snapshot with { Stage = ClusterStage.Restarting, TargetSha = sha }, new ClusterChanged(Snapshot.CurrentSha, ClusterStage.Restarting, sha));
        await host.SetLive(sha, CancellationToken.None);
        await host.RestartSilos(CancellationToken.None);
        return Describe();
    }

    // Runs on every activation: completes a restart the previous build began, and sweeps container orphans.
    internal async Task Reconcile(CancellationToken ct)
    {
        var current = await host.CurrentSha(ct);
        if (Snapshot.Stage == ClusterStage.Restarting && current == Snapshot.TargetSha)
        {
            await Save(new ClusterState { CurrentSha = current }, new ClusterChanged(current, ClusterStage.Steady, null));
        }
        else if (Snapshot.Stage == ClusterStage.Steady && Snapshot.CurrentSha != current)
        {
            await SaveQuietly(Snapshot with { CurrentSha = current });
        }
        var known = await GrainFactory.GetGrain<ICandidates>(ICandidates.Key(BrainId)).Known();
        await candidates.Orphans(known, ct);
    }

    private ClusterSnapshot Describe() => new(Snapshot.CurrentSha, Snapshot.Stage, Snapshot.TargetSha, []);
}

internal sealed class ClusterReconcile : IActivationHandler<ClusterNeuron>
{
    public Task Activated(ClusterNeuron neuron, CancellationToken cancellationToken) => neuron.Reconcile(cancellationToken);
}
