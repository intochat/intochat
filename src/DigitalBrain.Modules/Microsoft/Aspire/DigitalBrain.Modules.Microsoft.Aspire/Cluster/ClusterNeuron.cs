using DigitalBrain.Framework;
using DigitalBrain.Microsoft.Aspire.Candidates;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace DigitalBrain.Microsoft.Aspire.Cluster;

[GrainType("aspire.cluster")]
internal sealed class ClusterNeuron(
    [PersistentState("state", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<ClusterState> store,
    IClusterHost host,
    ICandidateHost candidates,
    ILogger<ClusterNeuron> log)
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
        var previous = Snapshot.CurrentSha;
        await Save(Snapshot with { Stage = ClusterStage.Restarting, TargetSha = sha }, new ClusterChanged(previous, ClusterStage.Restarting, sha));
        try
        {
            await host.SetLive(sha, CancellationToken.None);
            await host.RestartSilos(CancellationToken.None);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The silos did not start moving: the live build stays what runs, and the caller learns the restart failed.
            log.LogWarning(error, "Cluster {Cluster} could not restart onto {Target}.", this.GetPrimaryKeyString(), sha);
            if (previous.Length > 0) { await host.SetLive(previous, CancellationToken.None); }
            await Save(new ClusterState { CurrentSha = previous }, new ClusterChanged(previous, ClusterStage.Steady, null));
            throw;
        }
        return Describe();
    }

    // Runs on every activation: completes a restart the previous build began, and sweeps container orphans.
    internal async Task Reconcile(CancellationToken ct)
    {
        var current = await host.CurrentSha(ct);
        string? finished = null;
        string? abandoned = null;
        if (Snapshot.Stage == ClusterStage.Restarting && current == Snapshot.TargetSha)
        {
            finished = Snapshot.TargetSha;
            await Save(new ClusterState { CurrentSha = current }, new ClusterChanged(current, ClusterStage.Steady, null));
        }
        else if (Snapshot.Stage == ClusterStage.Restarting)
        {
            // live.json is written before the silos restart, so a different build means that restart never took.
            log.LogWarning("Cluster {Cluster} restarted towards {Target} but runs {Current}.", this.GetPrimaryKeyString(), Snapshot.TargetSha, current);
            abandoned = Snapshot.TargetSha;
            await Save(new ClusterState { CurrentSha = current }, new ClusterChanged(current, ClusterStage.Steady, null));
        }
        else if (Snapshot.CurrentSha != current)
        {
            await SaveQuietly(Snapshot with { CurrentSha = current });
        }
        // The candidate whose Promote was cut off by the restart ends here; its container is removed with it.
        if (finished is not null) { await Guarded(() => GrainFactory.GetGrain<ICandidate>(ICandidate.Key(BrainId, finished)).Finish(), "finish the promoted candidate"); }
        if (abandoned is not null)
        {
            await Guarded(() => GrainFactory.GetGrain<ICandidate>(ICandidate.Key(BrainId, abandoned)).Abandon($"the cluster restarted onto {current}, not {abandoned}"), "fail the abandoned promotion");
        }
        await Guarded(() => GrainFactory.GetGrain<ICandidates>(ICandidates.Key(BrainId)).Heal(), "heal the candidate queue");
        await Guarded(async () => await candidates.Orphans(await GrainFactory.GetGrain<ICandidates>(ICandidates.Key(BrainId)).Known(), ct), "sweep orphan candidate containers");
    }

    private async Task Guarded(Func<Task> work, string what)
    {
        try { await work(); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogWarning(error, "Cluster {Cluster} could not {Work}.", this.GetPrimaryKeyString(), what);
        }
    }

    private ClusterSnapshot Describe() => new(Snapshot.CurrentSha, Snapshot.Stage, Snapshot.TargetSha, []);
}

internal sealed class ClusterReconcile : IActivationHandler<ClusterNeuron>
{
    public Task Activated(ClusterNeuron neuron, CancellationToken cancellationToken) => neuron.Reconcile(cancellationToken);
}
