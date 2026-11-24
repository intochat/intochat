using System.Text.Json;
using DigitalBrain.Flows;
using IntoChat.OS.Modules.Apps.Flows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.Aspire.Deployment;

// The restart a roll step issued, saved with the step's wait: a later step waits for readiness since At and never restarts again.
internal sealed record RestartIssued(string Instance, DateTimeOffset At);

// A roll's own state. Remaining is the instances to move as the deployment listed them; Order is the roll order the "order" step fixes
// (the silo hosting the flow last) and never changes after it. Rolled is the instances already ready on TargetSha.
internal sealed class DeployInner
{
    public string TargetSha { get; set; } = "";

    public string[] Remaining { get; set; } = [];

    public string[] Order { get; set; } = [];

    public string[] Rolled { get; set; } = [];

    public string? PreviousSha { get; set; }

    public RestartIssued? RestartIssued { get; set; }
}

// One roll onto TargetSha: "order", then per instance "roll:{x}:mark" (saves RestartIssued, no side effect), "roll:{x}:restart"
// (live.json, then drain and restart only if the instance's process started at or before the mark; a one-minute schedule wait) and
// "roll:{x}:ready" (ready since the mark; never restarts), then "handover". The mark is durable before the restart is issued, so a
// restart step run again (cut off by its own silo's restart) sees the restarted process and issues nothing; an instance whose start
// time is unknown is never restarted and fails the roll. A step that fails throws FlowFailed; going
// back is a second DeployFlow, started by the deployment when this one ends Failed.
[DigitalBrain.Neurons.Neuron(Alias)]
internal sealed class DeployFlow(IClusterHost host, IOptions<AspireOptions> options) : Flow<DeployInner>, IFlowStarter
{
    public const string Alias = "flow.deploy";

    public const string OrderStep = "order";

    public const string Handover = "handover";

    public static string MarkStep(string instance) => $"roll:{instance}:mark";

    public static string RestartStep(string instance) => $"roll:{instance}:restart";

    public static string ReadyStep(string instance) => $"roll:{instance}:ready";

    // The instance a roll step names: "roll:{x}:restart" or "roll:{x}:ready".
    public static string? InstanceOf(string step)
    {
        if (!step.StartsWith("roll:", StringComparison.Ordinal)) { return null; }
        var end = step.LastIndexOf(':');
        return end > "roll:".Length ? step["roll:".Length..end] : null;
    }

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // The pause between a restart and its readiness check, as the roll reminder gave between steps.
    internal static readonly TimeSpan Pause = TimeSpan.FromMinutes(1);

    protected override IReadOnlyList<string> Steps
        => [OrderStep, .. State.Inner.Order.SelectMany(name => new[] { MarkStep(name), RestartStep(name), ReadyStep(name) }), Handover];

    public Task StartFromJson(string title, string owner, string? parentFlowId, string innerJson)
        => Start(title, owner, parentFlowId, JsonSerializer.Deserialize<DeployInner>(innerJson, Json) ?? new DeployInner());

    public Task<FlowHead> Head() => Task.FromResult(new FlowHead([.. Steps], State.Owner, State.TraceId, State.ParentFlowId, State.Started,
        JsonSerializer.Serialize(State.Inner, Json), State.Title, State.Status, [.. State.Done], State.Ended, null));

    private IClusterHost Host => host;

    private TimeSpan Budget => options.Value.SiloStepBudget;

    protected override async Task<StepResult<DeployInner>> Run(string step, DeployInner inner, CancellationToken ct)
    {
        if (step == OrderStep) { return new StepResult<DeployInner>(Ordered(inner)); }
        if (step == Handover) { return new StepResult<DeployInner>(inner); }
        if (InstanceOf(step) is not { } name) { throw new FlowFailed(step, $"Unknown step {step}."); }
        if (step == MarkStep(name)) { return new StepResult<DeployInner>(With(inner, restart: new RestartIssued(name, Context.Time.GetUtcNow()))); }
        if (step == RestartStep(name))
        {
            var next = await Restart(step, name, inner, ct);
            return new StepResult<DeployInner>(next, new Waiting(step, WaitReason.Schedule, Context.Time.GetUtcNow() + Pause, null));
        }
        return new StepResult<DeployInner>(await Ready(step, name, inner, ct));
    }

    // The silo hosting this flow goes last: its restart ends this activation, and the steps after it run on the restarted silo.
    private DeployInner Ordered(DeployInner inner)
    {
        if (inner.Order.Length > 0) { return inner; }
        var self = Host.Self;
        return With(inner, order: [.. inner.Remaining.Where(name => name != self), .. inner.Remaining.Where(name => name == self).Take(1)]);
    }

    private async Task<DeployInner> Restart(string step, string name, DeployInner inner, CancellationToken ct)
    {
        if (inner.RestartIssued is not { } mark || mark.Instance != name) { throw new FlowFailed(step, $"{name} has no restart mark"); }
        var host = Host;
        var target = inner.TargetSha;
        await Guard(step, name, target, async () => { if (await host.CurrentSha(ct) != target) { await host.SetLive(target, ct); } }, "live.json could not be written");
        // A process started after the mark is the restart this step issued before it was cut off. An unknown start is never restarted:
        // the roll fails cleanly rather than risk a second restart.
        DateTimeOffset? started = null;
        await Guard(step, name, target, async () => started = await host.StartedAt(name, ct), $"instance {name} start time could not be read");
        if (started is not { } start) { throw new FlowFailed(step, $"{name} never became ready on {target}: its start time is unknown"); }
        if (start > mark.At) { return inner; }
        await Guard(step, name, target, async () =>
        {
            await host.DrainInstance(name, ct);
            var instances = await host.Instances(ct);
            if (instances.Any(peer => peer != name && AspireCliClusterHost.RoleOf(peer) == AspireCliClusterHost.RoleOf(name)))
            { await host.WaitOutOfFront(name, Budget, ct); }
        }, $"instance {name} could not drain");
        var budget = Budget;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(budget);
        try { await host.RestartInstance(name, bounded.Token); }
        catch (OperationCanceledException) when (bounded.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new FlowFailed(step, $"{name} never became ready on {target}: instance {name} did not restart within {budget}");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new FlowFailed(step, $"{name} never became ready on {target}: instance {name} failed to restart: {error.Message}");
        }
        return inner;
    }

    // Another instance is waited for since the mark. The flow's own silo is ready when this process started after the mark; until the
    // budget is spent the step is interrupted (the old process still running it is about to stop, and the nudge runs it again), then
    // the roll fails rather than end Steady on a silo still running the old build.
    private async Task<DeployInner> Ready(string step, string name, DeployInner inner, CancellationToken ct)
    {
        var since = inner.RestartIssued is { } issued && issued.Instance == name ? issued.At : DateTimeOffset.MinValue;
        var host = Host;
        if (name != host.Self)
        {
            await Guard(step, name, inner.TargetSha, () => host.WaitInstance(name, since, Budget, ct), null);
            // Ready is not enough: the process answering must be one started after the mark.
            DateTimeOffset? started = null;
            await Guard(step, name, inner.TargetSha, async () => started = await host.StartedAt(name, ct), $"instance {name} start time could not be read");
            if (started is not { } start || start <= since) { throw new FlowFailed(step, $"{name} never became ready on {inner.TargetSha}: it did not restart"); }
        }
        else if (host.SelfStarted <= since)
        {
            if (Context.Time.GetUtcNow() - since < Budget) { throw new OperationCanceledException($"{name} has not restarted since {since:O}"); }
            throw new FlowFailed(step, $"{name} never became ready on {inner.TargetSha}: its process did not restart within {Budget}");
        }
        return With(inner, rolled: inner.Rolled.Contains(name) ? inner.Rolled : [.. inner.Rolled, name], restart: null, clearRestart: true);
    }

    private static async Task Guard(string step, string name, string target, Func<Task> work, string? what)
    {
        try { await work(); }
        catch (Exception error) when (error is not OperationCanceledException and not FlowFailed)
        {
            var why = error is InstanceNeverReadyException ? error.Message : $"{what ?? $"instance {name} readiness check failed"}: {error.Message}";
            throw new FlowFailed(step, $"{name} never became ready on {target}: {why}");
        }
    }

    private static DeployInner With(DeployInner inner, string[]? order = null, string[]? rolled = null, RestartIssued? restart = null, bool clearRestart = false) => new()
    {
        TargetSha = inner.TargetSha,
        PreviousSha = inner.PreviousSha,
        Remaining = inner.Remaining,
        Order = order ?? inner.Order,
        Rolled = rolled ?? inner.Rolled,
        RestartIssued = clearRestart ? null : restart ?? inner.RestartIssued,
    };
}
