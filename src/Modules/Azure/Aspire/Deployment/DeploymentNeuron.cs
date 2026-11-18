using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Flows;
using DigitalBrain.Neurons;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Microsoft.Aspire.Candidates;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Microsoft.Aspire.Deployment;

// A plain neuron (06-08): reminders through Context.Reminders, the retained last DeploymentChanged is the runtime's.
[DigitalBrain.Neurons.Neuron("aspire.deployment")]
internal sealed class DeploymentNeuron(
    IClusterHost host,
    IntoChat.OS.Modules.Microsoft.Orleans.IClusterMembershipFeed membership,
    ILogger<DeploymentNeuron> log)
    : DigitalBrain.Neurons.Neuron<AspireDeploymentState>, IDeployment, IDeploymentDriver
{
    // The state, loaded on activation; a step that moves nothing visible saves it without a signal.
    private AspireDeploymentState Snapshot => State;
    private string BrainId => Id.Brain.Value;
    private T Peer<T>(string relative) where T : class
        => Get<T>(DigitalBrain.Contracts.PlainNeuronContracts.IdOf(typeof(T), $"{BrainId}/{relative}"));
    private readonly Guid activation = Guid.NewGuid();
    private IAsyncDisposable? membershipSubscription;
    private long membershipVersion = long.MinValue;

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
            log.LogWarning(error, "Cluster {Deployment} could not restart onto {Target}.", Id.ToString(), sha);
            if (previous.Length > 0) { await host.SetLive(previous, CancellationToken.None); }
            await Save(new AspireDeploymentState { CurrentSha = previous }, Changed(previous, DeploymentStage.Steady));
            throw;
        }
        return Describe();
    }

    private static bool Moving(DeploymentStage stage) => stage is DeploymentStage.Rolling or DeploymentStage.RollingBack;

    // Validates and records the roll, points live.json at sha, starts its DeployFlow and answers: the flow drives the steps, this
    // neuron folds the flow's signals into AspireDeploymentState.
    public async Task<DeploymentSnapshot> Roll(string sha)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);
        if (Snapshot.Stage == DeploymentStage.Restarting) { throw new InvalidOperationException($"the cluster is restarting onto {Snapshot.TargetSha}"); }
        if (Snapshot.Stage == DeploymentStage.RollingBack) { throw new InvalidOperationException($"the cluster is rolling back onto {Snapshot.TargetSha}"); }
        if (Snapshot.Stage == DeploymentStage.Rolling && Snapshot.TargetSha != sha) { throw new InvalidOperationException($"the cluster is rolling onto {Snapshot.TargetSha}"); }
        if (Snapshot.Stage == DeploymentStage.Rolling) { return Describe(); }
        if (Snapshot.CurrentSha == sha) { return Describe(); }
        // A roll can only go back to a build that runs; the first move off source is a Restart.
        if (Snapshot.CurrentSha.Length == 0) { throw new InvalidOperationException("the cluster runs no live build to roll back to; restart onto the first one"); }
        var previous = Snapshot.CurrentSha;
        var instances = await host.Instances(CancellationToken.None);
        // The roll ends on the silo running it: a process that is none of the instances could never hand over.
        if (host.Self is not { } self || !instances.Contains(self)) { throw new InvalidOperationException("this process is not a cluster instance"); }
        await Context.Wires.EnsureBrainWide(typeof(FlowStep), typeof(FlowDone));
        var key = FlowKey(sha);
        await Save(new AspireDeploymentState { CurrentSha = previous, Stage = DeploymentStage.Rolling, TargetSha = sha, PreviousSha = previous, Remaining = instances, FlowId = FlowIdOf(key) },
            Changed(previous, DeploymentStage.Rolling));
        try
        {
            await host.SetLive(sha, CancellationToken.None);
            await StartFlow(key, $"Roll onto {sha}");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogWarning(error, "Cluster {Deployment} could not start rolling onto {Target}.", Id.ToString(), sha);
            await Guarded(() => host.SetLive(previous, CancellationToken.None), "point live.json back");
            await Save(new AspireDeploymentState { CurrentSha = previous, Failure = error.Message }, Changed(previous, DeploymentStage.Steady));
            throw;
        }
        return Describe();
    }

    // The flow's key: unique per roll, so a roll onto a build rolled before (or rolled back from) starts a new flow.
    private static string FlowKey(string sha) => $"deploy-{sha}-{Guid.NewGuid():N}";

    private string FlowIdOf(string key) => new NeuronId(Id.Brain, DeployFlow.Alias, key).ToString();

    private Task StartFlow(string key, string title)
    {
        var inner = new DeployInner { TargetSha = Snapshot.TargetSha!, PreviousSha = Snapshot.PreviousSha, Remaining = Snapshot.Remaining };
        return Get<IntoChat.OS.Modules.Apps.Flows.IFlows>(IntoChat.OS.Modules.Apps.Flows.IFlows.Key(BrainId))
            .Start(DeployFlow.Alias, title, null, System.Text.Json.JsonSerializer.Serialize(inner, DeployFlow.Json), CancellationToken.None, key);
    }

    private bool Ours(string flowId) => Moving(Snapshot.Stage) && Snapshot.FlowId == flowId;

    // An instance's ready step completing moved it; a roll step failing names the instance the roll back starts with.
    [On]
    public async Task OnDeployFlowStep(FlowStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (!Ours(step.FlowId) || DeployFlow.InstanceOf(step.Step) is not { } name) { return; }
        if (step.Outcome == StepOutcome.Failed)
        {
            await SaveQuietly(Snapshot with { FailedInstance = name });
            return;
        }
        if (step.Outcome != StepOutcome.Completed || step.Step != DeployFlow.ReadyStep(name) || !Snapshot.Remaining.Contains(name)) { return; }
        await SaveQuietly(Snapshot with { Remaining = [.. Snapshot.Remaining.Where(remaining => remaining != name)], Rolled = [.. Snapshot.Rolled, name] });
    }

    // The flow's end is the roll's end. A roll that fails goes back over every instance it moved, the failed one first, as a second
    // DeployFlow onto PreviousSha. A roll back that fails as well stops there, Steady on the build it was going back to, naming both.
    [On]
    public async Task OnDeployFlowDone(FlowDone done)
    {
        ArgumentNullException.ThrowIfNull(done);
        if (!Ours(done.FlowId)) { return; }
        if (done.Outcome == FlowOutcome.Completed) { await Finish(Snapshot.Failure); return; }
        var reason = done.Detail ?? done.Outcome.ToString();
        if (Snapshot.Stage == DeploymentStage.RollingBack)
        {
            log.LogError("Cluster {Deployment}: rolling back failed: {Reason}.", Id.ToString(), reason);
            await Finish($"{Snapshot.Failure}; rolling back: {reason}");
            return;
        }
        log.LogWarning("Cluster {Deployment}: {Reason}; rolling back to {Previous}.", Id.ToString(), reason, Snapshot.PreviousSha);
        // The failed instance (named by its failed step) first, then the moved ones newest first: silos come back before the MCP
        // servers that wait for them to start. The rollback flow still moves the silo hosting it last.
        var failed = Snapshot.FailedInstance;
        string[] back = [.. failed is null ? Array.Empty<string>() : [failed], .. Snapshot.Rolled.Reverse().Where(rolled => rolled != failed)];
        var key = FlowKey(Snapshot.PreviousSha!);
        await Save(Snapshot with { Stage = DeploymentStage.RollingBack, TargetSha = Snapshot.PreviousSha, PreviousSha = Snapshot.TargetSha, Remaining = back, Rolled = [], Handover = null, HandoverAt = null, FailedInstance = null, Failure = reason, FlowId = FlowIdOf(key) },
            Changed(Snapshot.CurrentSha, DeploymentStage.RollingBack));
        try { await StartFlow(key, $"Roll back onto {Snapshot.TargetSha}"); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogError(error, "Cluster {Deployment} could not start rolling back.", Id.ToString());
            await Finish($"{reason}; rolling back: {error.Message}");
        }
    }

    private ICandidate CandidatePeer(string sha) => Peer<ICandidate>(ICandidate.Relative(sha));

    private async Task Finish(string? failure)
    {
        var current = Snapshot.TargetSha!;
        var rolledForward = Snapshot.Stage == DeploymentStage.Rolling;
        await Save(new AspireDeploymentState { CurrentSha = current, Failure = failure }, Changed(current, DeploymentStage.Steady));
        if (rolledForward) { await Guarded(() => CandidatePeer(current).Finish(), "finish the promoted candidate"); }
    }

    // Runs on every activation: completes a restart the previous build began, and sweeps container orphans.
    // The reminder the roll loop drove itself with before rolls were flows; an activation stops what is left of it.
    internal const string LegacyRollReminder = "aspire.deployment.roll";

    internal async Task Reconcile(CancellationToken ct)
    {
        if ((await Context.Reminders.Names()).Contains(LegacyRollReminder)) { await Context.Reminders.Stop(LegacyRollReminder); }
        var current = await host.CurrentSha(ct);
        string? finished = null;
        string? abandoned = null;
        if (Moving(Snapshot.Stage))
        {
            // A roll outlives its activation: its flow continues by itself and this neuron folds its signals. A roll the reminder loop
            // drove has no flow: one that reached its own silo's handover on its target is complete; any other ends Steady on what runs.
            if (Snapshot.FlowId is null && Snapshot is { Remaining.Length: 0, Handover: not null } && current == Snapshot.TargetSha) { await Finish(Snapshot.Failure); }
            else if (Snapshot.FlowId is null) { await Save(new AspireDeploymentState { CurrentSha = current, Failure = "the roll was interrupted" }, Changed(current, DeploymentStage.Steady)); }
        }
        else if (Snapshot.Stage == DeploymentStage.Restarting && current == Snapshot.TargetSha)
        {
            finished = Snapshot.TargetSha;
            await Save(new AspireDeploymentState { CurrentSha = current }, Changed(current, DeploymentStage.Steady));
        }
        else if (Snapshot.Stage == DeploymentStage.Restarting)
        {
            // live.json is written before the silos restart, so a different build means that restart never took.
            log.LogWarning("Cluster {Deployment} restarted towards {Target} but runs {Current}.", Id.ToString(), Snapshot.TargetSha, current);
            abandoned = Snapshot.TargetSha;
            await Save(new AspireDeploymentState { CurrentSha = current }, Changed(current, DeploymentStage.Steady));
        }
        else if (Snapshot.CurrentSha != current)
        {
            await SaveQuietly(Snapshot with { CurrentSha = current });
        }
        // The candidate whose Promote was cut off by the restart ends here; its container is removed with it.
        if (finished is not null) { await Guarded(() => CandidatePeer(finished).Finish(), "finish the promoted candidate"); }
        if (abandoned is not null)
        {
            await Guarded(() => CandidatePeer(abandoned).Abandon($"the cluster restarted onto {current}, not {abandoned}"), "fail the abandoned promotion");
        }
        await Guarded(() => Peer<ICandidates>(ICandidates.Relative).Heal(), "heal the candidate queue");
        var receiver = Get<IDeploymentDriver>(Id);
        membershipSubscription = membership.Subscribe(snapshot => receiver.MembershipChanged(activation, snapshot));
        try { await MembershipChanged(activation, membership.Current); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogWarning(error, "Cluster {Deployment} could not read its initial membership.", Id.ToString());
            await MembershipChanged(activation, null);
        }
    }

    private async Task Guarded(Func<Task> work, string what)
    {
        try { await work(); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogWarning(error, "Cluster {Deployment} could not {Work}.", Id.ToString(), what);
        }
    }

    // The last silos this activation read and the last DeploymentChanged it published; both are rebuilt by the initial
    // membership snapshot after an activation, which publishes once.
    private SiloState[] silos = [];
    private DeploymentChanged? last;

    private DeploymentChanged Changed(string sha, DeploymentStage stage) => new(stage.ToString(), sha, silos);

    // What was published is what committed: a refused write publishes nothing.
    private async Task SaveQuietly(AspireDeploymentState next)
    {
        await Store(next);
    }

    private async Task Save(AspireDeploymentState next, DeploymentChanged changed)
    {
        await base.Save(next, changed);
        last = changed;
    }

    // Membership enters on the grain scheduler only when Orleans reports a change.
    // An old callback queued during deactivation cannot overwrite the next activation.
    public async Task MembershipChanged(Guid expectedActivation, IntoChat.OS.Modules.Microsoft.Orleans.ClusterSnapshot? membershipSnapshot)
    {
        if (expectedActivation != activation) { return; }
        if (membershipSnapshot is not null && membershipSnapshot.Version < membershipVersion) { return; }
        SiloState[] read = [.. (membershipSnapshot?.Silos ?? []).OrderBy(silo => silo.Name, StringComparer.Ordinal)
            .Select(silo => new SiloState(silo.Name, silo.Sha, silo.Status, ""))];
        var next = new DeploymentChanged(Snapshot.Stage.ToString(), Snapshot.CurrentSha, read);
        if (last is { } previous && previous.Stage == next.Stage && previous.Sha == next.Sha && previous.Silos.SequenceEqual(next.Silos))
        {
            if (membershipSnapshot is not null) { membershipVersion = membershipSnapshot.Version; }
            return;
        }
        // Journaled with the unchanged state, so the brain's log holds the last DeploymentChanged for a later reader (06-08).
        await base.Save(State, [next]);
        silos = read;
        last = next;
        if (membershipSnapshot is not null) { membershipVersion = membershipSnapshot.Version; }
    }

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await Reconcile(cancellationToken);
    }

    protected override async Task OnDeactivate(DigitalBrain.Neurons.DeactivationCause cause, CancellationToken cancellationToken)
    {
        if (membershipSubscription is not null) { await membershipSubscription.DisposeAsync(); }
    }

    private DeploymentSnapshot Describe() => new(Snapshot.CurrentSha, Snapshot.Stage, Snapshot.TargetSha, Snapshot.Remaining, Snapshot.Failure);
}
