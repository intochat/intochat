using System.Text.Json;
using DigitalBrain.Flows;
using IntoChat.OS.Modules.Apps.Flows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Microsoft.Aspire.Deployment;

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

// One roll onto TargetSha: "order", then per instance "roll:{x}:restart" (live.json, drain, restart, recorded as RestartIssued and
// followed by a one-minute schedule wait) and "roll:{x}:ready" (ready since the restart; never restarts), then "handover". A step that
// fails throws FlowFailed; going back is a second DeployFlow, started by the deployment when this one ends Failed. A restart step run
// again with its RestartIssued saved issues no second restart; the flow's own silo's restart is detached, so the step returns and saves
// before the silo stops, and the ready step runs on the restarted silo.
[DigitalBrain.Neuron(Alias)]
internal sealed class DeployFlow : Flow<DeployInner>, IFlowStarter
{
    public const string Alias = "flow.deploy";

    public const string OrderStep = "order";

    public const string Handover = "handover";

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
        => [OrderStep, .. State.Inner.Order.SelectMany(name => new[] { RestartStep(name), ReadyStep(name) }), Handover];

    public Task StartFromJson(string title, string owner, string? parentFlowId, string innerJson)
        => Start(title, owner, parentFlowId, JsonSerializer.Deserialize<DeployInner>(innerJson, Json) ?? new DeployInner());

    public Task<FlowHead> Head() => Task.FromResult(new FlowHead([.. Steps], State.Owner, State.TraceId, State.ParentFlowId, State.Started,
        JsonSerializer.Serialize(State.Inner, Json), State.Title, State.Status, [.. State.Done], State.Ended, null));

    private IClusterHost Host => Context.Services.GetRequiredService<IClusterHost>();

    private TimeSpan Budget => Context.Services.GetRequiredService<IOptions<AspireOptions>>().Value.SiloStepBudget;

    protected override async Task<StepResult<DeployInner>> Run(string step, DeployInner inner, CancellationToken ct)
    {
        if (step == OrderStep) { return new StepResult<DeployInner>(Ordered(inner)); }
        if (step == Handover) { return new StepResult<DeployInner>(inner); }
        if (InstanceOf(step) is not { } name) { throw new FlowFailed(step, $"Unknown step {step}."); }
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
        // Run again with its restart saved: the instance is already moving, so nothing is issued twice.
        if (inner.RestartIssued?.Instance == name) { return inner; }
        var host = Host;
        var target = inner.TargetSha;
        await Guard(step, name, target, async () => { if (await host.CurrentSha(ct) != target) { await host.SetLive(target, ct); } }, "live.json could not be written");
        await Guard(step, name, target, async () =>
        {
            await host.DrainInstance(name, ct);
            var instances = await host.Instances(ct);
            if (instances.Any(peer => peer != name && AspireCliClusterHost.RoleOf(peer) == AspireCliClusterHost.RoleOf(name)))
            { await host.WaitOutOfFront(name, Budget, ct); }
        }, $"instance {name} could not drain");
        var issued = new RestartIssued(name, Context.Time.GetUtcNow());
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
        return With(inner, restart: issued);
    }

    // On the restarted silo itself this activation is its readiness; any other instance is waited for since its restart.
    private async Task<DeployInner> Ready(string step, string name, DeployInner inner, CancellationToken ct)
    {
        if (name != Host.Self)
        {
            var since = inner.RestartIssued is { } issued && issued.Instance == name ? issued.At : DateTimeOffset.MinValue;
            await Guard(step, name, inner.TargetSha, () => Host.WaitInstance(name, since, Budget, ct), null);
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
