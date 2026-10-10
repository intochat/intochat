using DigitalBrain.Framework;
using DigitalBrain.Microsoft.Aspire.Candidates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace DigitalBrain.Microsoft.Aspire.Cluster;

[GrainType("aspire.cluster")]
internal sealed class ClusterNeuron(
    [PersistentState("state", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<ClusterState> store,
    IClusterHost host,
    ICandidateHost candidates,
    IOptions<AspireOptions> options,
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

    // Every step is persisted before its side effect: the silo running this grain restarts last, and the next
    // activation (or a repeated Roll of the same sha) continues from Remaining.
    public async Task<ClusterSnapshot> Roll(string sha)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);
        if (Snapshot.Stage == ClusterStage.Restarting) { throw new InvalidOperationException($"the cluster is restarting onto {Snapshot.TargetSha}"); }
        if (Snapshot.Stage == ClusterStage.Rolling && Snapshot.TargetSha != sha) { throw new InvalidOperationException($"the cluster is rolling onto {Snapshot.TargetSha}"); }
        if (Snapshot.Stage == ClusterStage.Steady && Snapshot.CurrentSha == sha) { return Describe(); }
        if (Snapshot.Stage == ClusterStage.Steady)
        {
            var previous = Snapshot.CurrentSha;
            var instances = await host.Instances(CancellationToken.None);
            await Save(Snapshot with { Stage = ClusterStage.Rolling, TargetSha = sha, PreviousSha = previous, Remaining = instances, Handover = null },
                new ClusterChanged(previous, ClusterStage.Rolling, sha));
            try { await host.SetLive(sha, CancellationToken.None); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                log.LogWarning(error, "Cluster {Cluster} could not point the live build at {Target}.", this.GetPrimaryKeyString(), sha);
                await Save(new ClusterState { CurrentSha = previous }, new ClusterChanged(previous, ClusterStage.Steady, null));
                throw;
            }
        }
        var self = host.Self;
        foreach (var name in Snapshot.Remaining.Where(name => name != self).ToArray())
        {
            await host.RestartSilo(name, CancellationToken.None);
            try
            {
                await host.WaitSilo(name, options.Value.SiloStepBudget, CancellationToken.None);
            }
            catch (TimeoutException error)
            {
                await RollBack(name, error);
            }
            await SaveQuietly(Snapshot with { Remaining = [.. Snapshot.Remaining.Where(remaining => remaining != name)] });
        }
        if (Snapshot.Remaining.Length == 0)
        {
            await Complete(Snapshot.TargetSha!);
            return Describe();
        }
        // Only this silo is left: its restart ends this activation, and the next one, on the new build, ends the roll.
        await SaveQuietly(Snapshot with { Handover = self });
        await host.RestartSilo(self!, CancellationToken.None);
        return Describe();
    }

    private async Task RollBack(string name, TimeoutException error)
    {
        var target = Snapshot.TargetSha;
        var previous = Snapshot.PreviousSha ?? "";
        log.LogWarning(error, "Cluster {Cluster}: {Instance} never became ready on {Target}; rolling back to {Previous}.", this.GetPrimaryKeyString(), name, target, previous);
        if (previous.Length > 0) { await host.SetLive(previous, CancellationToken.None); }
        await host.RestartSilo(name, CancellationToken.None);
        await Save(new ClusterState { CurrentSha = previous }, new ClusterChanged(previous, ClusterStage.Steady, null));
        throw new InvalidOperationException($"silo {name} never became healthy on {target}", error);
    }

    private async Task Complete(string sha)
    {
        await Save(new ClusterState { CurrentSha = sha }, new ClusterChanged(sha, ClusterStage.Steady, null));
        await Guarded(() => GrainFactory.GetGrain<ICandidate>(ICandidate.Key(BrainId, sha)).Finish(), "finish the promoted candidate");
    }

    // Runs on every activation: completes a restart the previous build began, and sweeps container orphans.
    internal async Task Reconcile(CancellationToken ct)
    {
        var current = await host.CurrentSha(ct);
        string? finished = null;
        string? abandoned = null;
        if (Snapshot.Stage == ClusterStage.Rolling && Snapshot.Handover is not null && current == Snapshot.TargetSha)
        {
            await Complete(current);
        }
        else if (Snapshot.Stage == ClusterStage.Rolling && current != Snapshot.TargetSha)
        {
            // live.json goes to the target before any instance restarts and back only on rollback: the roll is over.
            log.LogWarning("Cluster {Cluster} rolled towards {Target} but runs {Current}.", this.GetPrimaryKeyString(), Snapshot.TargetSha, current);
            await Save(new ClusterState { CurrentSha = current }, new ClusterChanged(current, ClusterStage.Steady, null));
        }
        else if (Snapshot.Stage == ClusterStage.Rolling)
        {
            // A roll cut off between instances keeps its Remaining; Roll of the same sha resumes it.
        }
        else if (Snapshot.Stage == ClusterStage.Restarting && current == Snapshot.TargetSha)
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

    private ClusterSnapshot Describe() => new(Snapshot.CurrentSha, Snapshot.Stage, Snapshot.TargetSha, [], Snapshot.Remaining);
}

internal sealed class ClusterReconcile : IActivationHandler<ClusterNeuron>
{
    public Task Activated(ClusterNeuron neuron, CancellationToken cancellationToken) => neuron.Reconcile(cancellationToken);
}
