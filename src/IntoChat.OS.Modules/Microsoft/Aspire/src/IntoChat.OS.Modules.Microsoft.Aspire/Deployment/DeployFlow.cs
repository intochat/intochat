using System.Text.Json;
using DigitalBrain.Flows;
using IntoChat.OS.Modules.Apps.Flows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Microsoft.Aspire.Deployment;

// A roll's own state. Rolled then Remaining is always the roll order, so the steps never change while the roll moves: a step moves
// its instance from Remaining to Rolled. Handover is the instance that hosted the flow when it was restarted.
internal sealed class DeployInner
{
    public string TargetSha { get; set; } = "";

    public string[] Remaining { get; set; } = [];

    public string[] Rolled { get; set; } = [];

    public string? PreviousSha { get; set; }

    public string? Handover { get; set; }

    public DateTimeOffset? HandoverAt { get; set; }
}

// One roll, instance by instance onto TargetSha: a "roll:{instance}" step per instance (the one running the flow last), each followed
// by a one-minute schedule wait, then "handover" (the instance restarted under the flow is ready). A step that fails throws FlowFailed;
// going back is a second DeployFlow, started by the deployment when this one ends Failed. Bodies are idempotent: an instance already
// on TargetSha is not restarted again, so a step cut off by its own silo's restart completes when it runs again.
[DigitalBrain.Neuron(Alias)]
internal sealed class DeployFlow : Flow<DeployInner>, IFlowStarter
{
    public const string Alias = "flow.deploy";

    public const string Handover = "handover";

    public static string RollStep(string instance) => "roll:" + instance;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // The pause between two instances, as the roll reminder gave.
    internal static readonly TimeSpan Pause = TimeSpan.FromMinutes(1);

    protected override IReadOnlyList<string> Steps => [.. State.Inner.Rolled.Concat(State.Inner.Remaining).Select(RollStep), Handover];

    public Task StartFromJson(string title, string owner, string? parentFlowId, string innerJson)
        => Start(title, owner, parentFlowId, JsonSerializer.Deserialize<DeployInner>(innerJson, Json) ?? new DeployInner());

    public Task<FlowHead> Head() => Task.FromResult(new FlowHead([.. Steps], State.Owner, State.TraceId, State.ParentFlowId, State.Started,
        JsonSerializer.Serialize(State.Inner, Json), State.Title, State.Status, [.. State.Done], State.Ended, null));

    private IClusterHost Host => Context.Services.GetRequiredService<IClusterHost>();

    private TimeSpan Budget => Context.Services.GetRequiredService<IOptions<AspireOptions>>().Value.SiloStepBudget;

    protected override async Task<StepResult<DeployInner>> Run(string step, DeployInner inner, CancellationToken ct)
    {
        if (step == Handover) { return new StepResult<DeployInner>(await HandOver(inner, ct)); }
        if (!step.StartsWith("roll:", StringComparison.Ordinal)) { throw new FlowFailed(step, $"Unknown step {step}."); }
        var next = await RollOne(step, step["roll:".Length..], inner, ct);
        return new StepResult<DeployInner>(next, new Waiting(step, WaitReason.Schedule, Context.Time.GetUtcNow() + Pause, null));
    }

    // What one Step() of the reminder loop did for one instance: live.json on TargetSha, drain, restart, wait until ready.
    private async Task<DeployInner> RollOne(string step, string name, DeployInner inner, CancellationToken ct)
    {
        var host = Host;
        var target = inner.TargetSha;
        try
        {
            if (await host.CurrentSha(ct) != target) { await host.SetLive(target, ct); }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new FlowFailed(step, $"{name} never became ready on {target}: live.json could not be written: {error.Message}");
        }
        var self = host.Self;
        // Run again after its own silo's restart (or any cut-off restart): the instance already runs TargetSha.
        if (OnTarget(name, target))
        {
            return Moved(inner, name, handover: name == self || inner.Handover == name, at: inner.HandoverAt);
        }
        await Drain(step, name, ct);
        if (name == self)
        {
            // The calling silo's own restart is detached: the next activation, on the restarted silo, runs the handover.
            var at = Context.Time.GetUtcNow();
            await Guard(step, name, target, () => host.RestartInstance(name, ct));
            return Moved(inner, name, handover: true, at);
        }
        var since = DateTimeOffset.UtcNow;
        var budget = Budget;
        using (var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            bounded.CancelAfter(budget);
            try { await host.RestartInstance(name, bounded.Token); }
            catch (OperationCanceledException) when (bounded.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new FlowFailed(step, $"{name} never became ready on {target}: instance {name} did not restart within {budget}");
            }
            catch (Exception error) when (error is not OperationCanceledException and not FlowFailed)
            {
                throw new FlowFailed(step, $"{name} never became ready on {target}: instance {name} failed to restart: {error.Message}");
            }
        }
        await Guard(step, name, target, () => host.WaitInstance(name, since, budget, ct));
        return Moved(inner, name, handover: false, null);
    }

    // A single instance restarts with a brief interruption; only one with a peer of its role waits for failover.
    private async Task Drain(string step, string name, CancellationToken ct)
    {
        var host = Host;
        await Guard(step, name, null, async () =>
        {
            await host.DrainInstance(name, ct);
            var instances = await host.Instances(ct);
            if (instances.Any(peer => peer != name && AspireCliClusterHost.RoleOf(peer) == AspireCliClusterHost.RoleOf(name)))
            { await host.WaitOutOfFront(name, Budget, ct); }
        });
    }

    private async Task<DeployInner> HandOver(DeployInner inner, CancellationToken ct)
    {
        if (inner.Handover is not { } handover || handover == Host.Self) { return inner; }
        await Guard(Handover, handover, inner.TargetSha, () => Host.WaitInstance(handover, inner.HandoverAt ?? DateTimeOffset.MinValue, Budget, ct));
        return inner;
    }

    private async Task Guard(string step, string name, string? target, Func<Task> work)
    {
        try { await work(); }
        catch (Exception error) when (error is not OperationCanceledException and not FlowFailed)
        {
            var why = error is InstanceNeverReadyException ? error.Message : $"instance {name} could not move: {error.Message}";
            throw new FlowFailed(step, $"{name} never became ready on {target ?? State.Inner.TargetSha}: {why}");
        }
    }

    // A silo that Orleans reports Active on target: the membership names silos, so MCP instances are never "already moved".
    private bool OnTarget(string name, string target)
    {
        var membership = Context.Services.GetService<IntoChat.OS.Modules.Microsoft.Orleans.IClusterMembershipFeed>();
        return membership?.Current.Silos.Any(silo => silo.Name == name && silo.Sha == target
            && string.Equals(silo.Status, "Active", StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static DeployInner Moved(DeployInner inner, string name, bool handover, DateTimeOffset? at) => new()
    {
        TargetSha = inner.TargetSha,
        PreviousSha = inner.PreviousSha,
        Remaining = [.. inner.Remaining.Where(remaining => remaining != name)],
        Rolled = inner.Rolled.Contains(name) ? inner.Rolled : [.. inner.Rolled, name],
        Handover = handover ? name : inner.Handover,
        HandoverAt = handover ? at : inner.HandoverAt,
    };
}
