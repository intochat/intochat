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
    : Neuron<ClusterState>(store), ICluster, IClusterDriver, IRemindable
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

    public const string RollReminder = "aspire.cluster.roll";
    private static readonly TimeSpan ReminderPeriod = TimeSpan.FromMinutes(1);

    // A Step is queued or running in this activation: a reminder tick or a resend meanwhile does nothing.
    private bool driving;
    // This activation issued its own silo's restart: it ends with that silo, and the next activation finishes.
    private bool handedOver;

    private static bool Moving(ClusterStage stage) => stage is ClusterStage.Rolling or ClusterStage.RollingBack;

    // Validates and records the roll, points live.json at sha, and answers: the steps run one-way (Step).
    public async Task<ClusterSnapshot> Roll(string sha)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);
        if (Snapshot.Stage == ClusterStage.Restarting) { throw new InvalidOperationException($"the cluster is restarting onto {Snapshot.TargetSha}"); }
        if (Snapshot.Stage == ClusterStage.RollingBack) { throw new InvalidOperationException($"the cluster is rolling back onto {Snapshot.TargetSha}"); }
        if (Snapshot.Stage == ClusterStage.Rolling && Snapshot.TargetSha != sha) { throw new InvalidOperationException($"the cluster is rolling onto {Snapshot.TargetSha}"); }
        if (Snapshot.Stage == ClusterStage.Rolling) { Drive(); return Describe(); }
        if (Snapshot.CurrentSha == sha) { return Describe(); }
        // A roll can only go back to a build that runs; the first move off source is a Restart.
        if (Snapshot.CurrentSha.Length == 0) { throw new InvalidOperationException("the cluster runs no live build to roll back to; restart onto the first one"); }
        var previous = Snapshot.CurrentSha;
        var instances = await host.Instances(CancellationToken.None);
        await Save(new ClusterState { CurrentSha = previous, Stage = ClusterStage.Rolling, TargetSha = sha, PreviousSha = previous, Remaining = instances },
            new ClusterChanged(previous, ClusterStage.Rolling, sha));
        // The reminder reactivates the cluster when nothing else would: after its own silo's restart, or a crash mid-roll.
        await this.RegisterOrUpdateReminder(RollReminder, ReminderPeriod, ReminderPeriod);
        try { await host.SetLive(sha, CancellationToken.None); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogWarning(error, "Cluster {Cluster} could not point the live build at {Target}.", this.GetPrimaryKeyString(), sha);
            await Save(new ClusterState { CurrentSha = previous, Failure = error.Message }, new ClusterChanged(previous, ClusterStage.Steady, null, error.Message));
            await StopReminder();
            throw;
        }
        Drive();
        return Describe();
    }

    public Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName == RollReminder) { Drive(); }
        return Task.CompletedTask;
    }

    private void Drive()
    {
        if (driving || handedOver || !Moving(Snapshot.Stage)) { return; }
        driving = true;
        _ = this.AsReference<IClusterDriver>().Step();
    }

    // One instance per Step: restart it onto TargetSha, wait until it is ready, record it, hand on to the next Step.
    public async Task Step()
    {
        driving = false;
        if (handedOver || !Moving(Snapshot.Stage)) { return; }
        var target = Snapshot.TargetSha!;
        // Intent is persisted before live.json moves: a step cut off between the two points live.json here.
        try
        {
            if (await host.CurrentSha(CancellationToken.None) != target) { await host.SetLive(target, CancellationToken.None); }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogWarning(error, "Cluster {Cluster} could not set live to {Target} at step.", this.GetPrimaryKeyString(), target);
            await Save(Snapshot with { Stage = ClusterStage.Steady, Failure = error.Message }, new ClusterChanged(Snapshot.CurrentSha ?? Snapshot.PreviousSha ?? string.Empty, ClusterStage.Steady, null, error.Message));
            await StopReminder();
            return;
        }
        var self = host.Self;
        if (Snapshot.Handover is { } handover)
        {
            // On the handed-over silo this activation is its readiness; elsewhere that silo is waited for.
            if (handover == self || await Ready(handover)) { await Complete(); }
            return;
        }
        var next = Snapshot.Remaining.FirstOrDefault(name => name != self);
        if (next is null && Snapshot.Remaining.Length == 0)
        {
            await Complete();
            return;
        }
        if (next is null)
        {
            await SaveQuietly(Snapshot with { Handover = self, Remaining = [], Rolled = [.. Snapshot.Rolled, self!] });
            handedOver = true;
            await host.RestartInstance(self!, CancellationToken.None);
            return;
        }
        if (!await Moved(next)) { return; }
        await SaveQuietly(Snapshot with { Remaining = [.. Snapshot.Remaining.Where(name => name != next)], Rolled = [.. Snapshot.Rolled, next] });
        Drive();
    }

    // The restart itself is bounded too: an instance whose start waits on something that never comes (a failed
    // dependency) holds `aspire resource restart` open.
    private async Task<bool> Moved(string name)
    {
        var budget = options.Value.SiloStepBudget;
        using var bounded = new CancellationTokenSource(budget);
        try { await host.RestartInstance(name, bounded.Token); }
        catch (OperationCanceledException error) when (bounded.IsCancellationRequested)
        {
            await NeverReady(name, new InstanceNeverReadyException($"instance {name} did not restart within {budget}", error));
            return false;
        }
        catch (Exception error)
        {
            await NeverReady(name, new InstanceNeverReadyException($"instance {name} failed to restart: {error.Message}", error));
            return false;
        }
        return await Ready(name);
    }

    private async Task<bool> Ready(string name)
    {
        try
        {
            await host.WaitInstance(name, options.Value.SiloStepBudget, CancellationToken.None);
            return true;
        }
        catch (InstanceNeverReadyException error)
        {
            await NeverReady(name, error);
            return false;
        }
        catch (Exception error)
        {
            await NeverReady(name, new InstanceNeverReadyException($"instance {name} readiness check failed: {error.Message}", error));
            return false;
        }
    }

    // A roll whose instance never becomes ready goes back over every instance it moved, the failed one included.
    // A roll back that fails as well stops there, Steady on the build it was going back to, naming both failures.
    private async Task NeverReady(string name, InstanceNeverReadyException error)
    {
        var reason = $"{name} never became ready on {Snapshot.TargetSha}: {error.Message}";
        if (Snapshot.Stage == ClusterStage.RollingBack)
        {
            log.LogError(error, "Cluster {Cluster}: rolling back failed at {Instance}.", this.GetPrimaryKeyString(), name);
            await Finish($"{Snapshot.Failure}; rolling back: {reason}");
            return;
        }
        log.LogWarning(error, "Cluster {Cluster}: {Reason}; rolling back to {Previous}.", this.GetPrimaryKeyString(), reason, Snapshot.PreviousSha);
        // The failed instance first, then the moved ones newest first: silos come back before the MCP servers that
        // wait for them to start.
        string[] back = [name, .. Snapshot.Rolled.Reverse().Where(rolled => rolled != name)];
        await Save(Snapshot with { Stage = ClusterStage.RollingBack, TargetSha = Snapshot.PreviousSha, PreviousSha = Snapshot.TargetSha, Remaining = back, Rolled = [], Handover = null, Failure = reason },
            new ClusterChanged(Snapshot.CurrentSha, ClusterStage.RollingBack, Snapshot.PreviousSha, reason));
        Drive();
    }

    private Task Complete() => Finish(Snapshot.Failure);

    private async Task Finish(string? failure)
    {
        var current = Snapshot.TargetSha!;
        var rolledForward = Snapshot.Stage == ClusterStage.Rolling;
        await Save(new ClusterState { CurrentSha = current, Failure = failure }, new ClusterChanged(current, ClusterStage.Steady, null, failure));
        await StopReminder();
        if (rolledForward) { await Guarded(() => GrainFactory.GetGrain<ICandidate>(ICandidate.Key(BrainId, current)).Finish(), "finish the promoted candidate"); }
    }

    private async Task StopReminder()
    {
        if (await this.GetReminder(RollReminder) is { } reminder) { await this.UnregisterReminder(reminder); }
    }

    // Runs on every activation: completes a restart the previous build began, and sweeps container orphans.
    internal async Task Reconcile(CancellationToken ct)
    {
        var current = await host.CurrentSha(ct);
        string? finished = null;
        string? abandoned = null;
        if (Moving(Snapshot.Stage))
        {
            // A roll outlives its activation: this one continues at the persisted step.
            Drive();
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

    private ClusterSnapshot Describe() => new(Snapshot.CurrentSha, Snapshot.Stage, Snapshot.TargetSha, Snapshot.Remaining, Snapshot.Failure);
}

internal sealed class ClusterReconcile : IActivationHandler<ClusterNeuron>
{
    public Task Activated(ClusterNeuron neuron, CancellationToken cancellationToken) => neuron.Reconcile(cancellationToken);
}
