using DigitalBrain.Framework;
using DigitalBrain.Microsoft.Aspire.Candidates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace DigitalBrain.Microsoft.Aspire.Deployment;

[GrainType("aspire.deployment")]
internal sealed class DeploymentNeuron(
    [PersistentState("state", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<DeploymentState> store,
    IClusterHost host,
    ICandidateHost candidates,
    IOptions<AspireOptions> options,
    ILogger<DeploymentNeuron> log)
    : Neuron<DeploymentState>(store), IDeployment, IDeploymentDriver, IRemindable, IRetainsSignals
{
    private string BrainId => BrainKey.BrainOf(this.GetPrimaryKeyString());

    public Task<DeploymentSnapshot> Read() => Task.FromResult(Describe());

    public async Task<DeploymentSnapshot> Restart(string sha)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);
        if (Snapshot.Stage == DeploymentStage.Restarting && Snapshot.TargetSha == sha) { return Describe(); }
        if (Snapshot.Stage == DeploymentStage.Steady && Snapshot.CurrentSha == sha) { return Describe(); }
        // Persisted before acting: the silo running this grain may die in RestartSilos.
        var previous = Snapshot.CurrentSha;
        await Save(Snapshot with { Stage = DeploymentStage.Restarting, TargetSha = sha }, Changed(previous, DeploymentStage.Restarting));
        try
        {
            await host.SetLive(sha, CancellationToken.None);
            await host.RestartSilos(CancellationToken.None);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The silos did not start moving: the live build stays what runs, and the caller learns the restart failed.
            log.LogWarning(error, "Cluster {Deployment} could not restart onto {Target}.", this.GetPrimaryKeyString(), sha);
            if (previous.Length > 0) { await host.SetLive(previous, CancellationToken.None); }
            await Save(new DeploymentState { CurrentSha = previous }, Changed(previous, DeploymentStage.Steady));
            throw;
        }
        return Describe();
    }

    public const string RollReminder = "aspire.deployment.roll";
    private static readonly TimeSpan ReminderPeriod = TimeSpan.FromMinutes(1);

    // A Step is queued or running in this activation: a reminder tick or a resend meanwhile does nothing.
    private bool driving;
    // This activation issued its own silo's restart: it ends with that silo, and the next activation finishes.
    private bool handedOver;

    private static bool Moving(DeploymentStage stage) => stage is DeploymentStage.Rolling or DeploymentStage.RollingBack;

    // Validates and records the roll, points live.json at sha, and answers: the steps run one-way (Step).
    public async Task<DeploymentSnapshot> Roll(string sha)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);
        if (Snapshot.Stage == DeploymentStage.Restarting) { throw new InvalidOperationException($"the cluster is restarting onto {Snapshot.TargetSha}"); }
        if (Snapshot.Stage == DeploymentStage.RollingBack) { throw new InvalidOperationException($"the cluster is rolling back onto {Snapshot.TargetSha}"); }
        if (Snapshot.Stage == DeploymentStage.Rolling && Snapshot.TargetSha != sha) { throw new InvalidOperationException($"the cluster is rolling onto {Snapshot.TargetSha}"); }
        if (Snapshot.Stage == DeploymentStage.Rolling) { Drive(); return Describe(); }
        if (Snapshot.CurrentSha == sha) { return Describe(); }
        // A roll can only go back to a build that runs; the first move off source is a Restart.
        if (Snapshot.CurrentSha.Length == 0) { throw new InvalidOperationException("the cluster runs no live build to roll back to; restart onto the first one"); }
        var previous = Snapshot.CurrentSha;
        var instances = await host.Instances(CancellationToken.None);
        // The roll ends on the silo running it: a process that is none of the instances could never hand over.
        if (host.Self is not { } self || !instances.Contains(self)) { throw new InvalidOperationException("this process is not a cluster instance"); }
        // The reminder reactivates the cluster when nothing else would: after its own silo's restart, or a crash
        // mid-roll. Registered before the roll is recorded, so no recorded roll is ever without it.
        await this.RegisterOrUpdateReminder(RollReminder, ReminderPeriod, ReminderPeriod);
        await Save(new DeploymentState { CurrentSha = previous, Stage = DeploymentStage.Rolling, TargetSha = sha, PreviousSha = previous, Remaining = instances },
            Changed(previous, DeploymentStage.Rolling));
        try { await host.SetLive(sha, CancellationToken.None); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogWarning(error, "Cluster {Deployment} could not point the live build at {Target}.", this.GetPrimaryKeyString(), sha);
            await Save(new DeploymentState { CurrentSha = previous, Failure = error.Message }, Changed(previous, DeploymentStage.Steady));
            await StopReminder();
            throw;
        }
        Drive();
        return Describe();
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName != RollReminder) { return; }
        if (Moving(Snapshot.Stage)) { Drive(); }
        else { await StopReminder(); }
    }

    private void Drive()
    {
        if (driving || handedOver || !Moving(Snapshot.Stage)) { return; }
        driving = true;
        _ = this.AsReference<IDeploymentDriver>().Step();
    }

    // One instance per Step: restart it onto TargetSha, wait until it is ready, record it, hand on to the next Step.
    public async Task Step()
    {
        driving = false;
        if (handedOver || !Moving(Snapshot.Stage)) { return; }
        var target = Snapshot.TargetSha!;
        var self = host.Self;
        try
        {
            if (await host.CurrentSha(CancellationToken.None) != target) { await host.SetLive(target, CancellationToken.None); }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogWarning(error, "Cluster {Deployment} could not set live to {Target} at step.", this.GetPrimaryKeyString(), target);
            var name = Snapshot.Remaining.FirstOrDefault(n => n != self) ?? self!;
            await NeverReady(name, new InstanceNeverReadyException($"live.json could not be written: {error.Message}", error));
            return;
        }
        if (Snapshot.Handover is { } handover)
        {
            // On the handed-over silo this activation is its readiness; elsewhere that silo is waited for.
            if (handover == self || await Ready(handover, Snapshot.HandoverAt ?? DateTimeOffset.MinValue)) { await Complete(); }
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
            if (!await Drained(self!)) { return; }
            await SaveQuietly(Snapshot with { Handover = self, HandoverAt = DateTimeOffset.UtcNow, Remaining = [], Rolled = [.. Snapshot.Rolled, self!], Draining = null });
            handedOver = true;
            await host.RestartInstance(self!, CancellationToken.None);
            return;
        }
        if (!await Drained(next) || !await Moved(next)) { return; }
        await SaveQuietly(Snapshot with { Remaining = [.. Snapshot.Remaining.Where(name => name != next)], Rolled = [.. Snapshot.Rolled, next], Draining = null });
        Drive();
    }

    // Before an instance stops, the front stops routing it while another instance of its role still serves: the
    // instance drains, and the step waits until the front has taken it out. A drain that fails, or a front left with
    // nothing to route to, is a never-ready outcome: going forward it rolls back; going back it stops the roll back
    // (NeverReady), since restarting the only routable instance would take the port down.
    private async Task<bool> Drained(string name)
    {
        try
        {
            if (Snapshot.Draining != name)
            {
                await host.DrainInstance(name, CancellationToken.None);
                await SaveQuietly(Snapshot with { Draining = name });
            }
            await host.WaitOutOfFront(name, options.Value.SiloStepBudget, CancellationToken.None);
            return true;
        }
        catch (InstanceNeverReadyException error)
        {
            await NeverReady(name, error);
            return false;
        }
        catch (Exception error)
        {
            await NeverReady(name, new InstanceNeverReadyException($"instance {name} could not drain: {error.Message}", error));
            return false;
        }
    }

    // The restart itself is bounded too: an instance whose start waits on something that never comes (a failed
    // dependency) holds `aspire resource restart` open.
    private async Task<bool> Moved(string name)
    {
        var budget = options.Value.SiloStepBudget;
        var since = DateTimeOffset.UtcNow;
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
        return await Ready(name, since);
    }

    private async Task<bool> Ready(string name, DateTimeOffset since)
    {
        try
        {
            await host.WaitInstance(name, since, options.Value.SiloStepBudget, CancellationToken.None);
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
        if (Snapshot.Stage == DeploymentStage.RollingBack)
        {
            log.LogError(error, "Cluster {Deployment}: rolling back failed at {Instance}.", this.GetPrimaryKeyString(), name);
            await Finish($"{Snapshot.Failure}; rolling back: {reason}");
            return;
        }
        log.LogWarning(error, "Cluster {Deployment}: {Reason}; rolling back to {Previous}.", this.GetPrimaryKeyString(), reason, Snapshot.PreviousSha);
        // The failed instance first, then the moved ones newest first: silos come back before the MCP servers that
        // wait for them to start.
        string[] back = [name, .. Snapshot.Rolled.Reverse().Where(rolled => rolled != name)];
        await Save(Snapshot with { Stage = DeploymentStage.RollingBack, TargetSha = Snapshot.PreviousSha, PreviousSha = Snapshot.TargetSha, Remaining = back, Rolled = [], Handover = null, HandoverAt = null, Draining = null, Failure = reason },
            Changed(Snapshot.CurrentSha, DeploymentStage.RollingBack));
        Drive();
    }

    private Task Complete() => Finish(Snapshot.Failure);

    private async Task Finish(string? failure)
    {
        var current = Snapshot.TargetSha!;
        var rolledForward = Snapshot.Stage == DeploymentStage.Rolling;
        await Save(new DeploymentState { CurrentSha = current, Failure = failure }, Changed(current, DeploymentStage.Steady));
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
        this.RegisterGrainTimer(Sample, new GrainTimerCreationOptions
        {
            DueTime = options.Value.ClusterSampling,
            Period = options.Value.ClusterSampling,
            // A roll holds the grain for minutes per instance; sampling meanwhile is what shows a silo restarting.
            Interleave = true,
            KeepAlive = true,
        });
        var current = await host.CurrentSha(ct);
        string? finished = null;
        string? abandoned = null;
        if (Moving(Snapshot.Stage))
        {
            // A roll outlives its activation: this one continues at the persisted step.
            Drive();
        }
        else if (Snapshot.Stage == DeploymentStage.Restarting && current == Snapshot.TargetSha)
        {
            finished = Snapshot.TargetSha;
            await Save(new DeploymentState { CurrentSha = current }, Changed(current, DeploymentStage.Steady));
        }
        else if (Snapshot.Stage == DeploymentStage.Restarting)
        {
            // live.json is written before the silos restart, so a different build means that restart never took.
            log.LogWarning("Cluster {Deployment} restarted towards {Target} but runs {Current}.", this.GetPrimaryKeyString(), Snapshot.TargetSha, current);
            abandoned = Snapshot.TargetSha;
            await Save(new DeploymentState { CurrentSha = current }, Changed(current, DeploymentStage.Steady));
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
            log.LogWarning(error, "Cluster {Deployment} could not {Work}.", this.GetPrimaryKeyString(), what);
        }
    }

    // The last silos this activation read and the last DeploymentChanged it published; both are rebuilt by the first
    // sample after an activation, which publishes once.
    private SiloState[] silos = [];
    private DeploymentChanged? last;
    private long sequence = DateTimeOffset.UtcNow.Ticks;
    private long lastSequence;
    private bool unreadable;

    private DeploymentChanged Changed(string sha, DeploymentStage stage) => new(stage.ToString(), sha, silos);

    // What was retained is what committed: a refused write publishes nothing and retains nothing.
    private async Task Save(DeploymentState next, DeploymentChanged changed)
    {
        await base.Save(next, changed);
        last = changed;
        lastSequence = ++sequence;
    }

    public Task<RetainedSignal[]> Retained(string signal, long since)
        => Task.FromResult<RetainedSignal[]>(signal == nameof(DeploymentChanged) && last is { } current && lastSequence > since
            ? [new RetainedSignal(lastSequence, current with { Publisher = Id })]
            : []);

    // The membership is read here, once per deployment, and the strip hears about it only when it moved.
    internal async Task Sample(CancellationToken ct)
    {
        SiloState[] read;
        try
        {
            // The membership is the orleans module's: this neuron reads it, it does not ask the AppHost who is up.
            var membership = await GrainFactory.GetGrain<global::DigitalBrain.Microsoft.Orleans.ICluster>(global::DigitalBrain.Microsoft.Orleans.ICluster.Key(BrainId)).Read();
            read = [.. membership.Silos.OrderBy(silo => silo.Name, StringComparer.Ordinal).Select(silo => new SiloState(silo.Name, silo.Sha, silo.Status, ""))];
            unreadable = false;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // No membership to read means no silos to show; the strip empties once and the warning is logged once.
            if (!unreadable) { log.LogWarning(error, "Cluster {Deployment} could not read its silos.", this.GetPrimaryKeyString()); }
            unreadable = true;
            read = [];
        }
        var next = new DeploymentChanged(Snapshot.Stage.ToString(), Snapshot.CurrentSha, read);
        if (last is { } previous && previous.Stage == next.Stage && previous.Sha == next.Sha && previous.Silos.SequenceEqual(next.Silos)) { return; }
        silos = read;
        last = next;
        lastSequence = ++sequence;
        await PublishAsync(next);
    }

    private DeploymentSnapshot Describe() => new(Snapshot.CurrentSha, Snapshot.Stage, Snapshot.TargetSha, Snapshot.Remaining, Snapshot.Failure);
}

internal sealed class DeploymentReconcile : IActivationHandler<DeploymentNeuron>
{
    public Task Activated(DeploymentNeuron neuron, CancellationToken cancellationToken) => neuron.Reconcile(cancellationToken);
}
