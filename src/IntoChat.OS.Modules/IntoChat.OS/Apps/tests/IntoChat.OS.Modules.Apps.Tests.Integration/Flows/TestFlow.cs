using System.Collections.Concurrent;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Flows;
using DigitalBrain.Operations;
using DigitalBrain.Spi;
using IntoChat.OS.Modules.Apps.Flows;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Flows;

// The module whose assembly carries the facts' flow type.
[DigitalBrain.Module("flows-facts")]
public sealed class FlowsFactsModule : DigitalBrain.Module
{
    public static ModuleManifest Manifest { get; } = new([], [], [], [], []);
}

// The facts' flow state: Wait makes "second" wait for an external resume, Gate holds "third" until the facts open it,
// StartOperation starts a leaf operation in "second" and records its id.
public sealed class TestInner
{
    public bool Wait { get; set; }

    // Makes "second" wait on an approval instead: the flow base raises the confirmation plan.
    public bool WaitApproval { get; set; }

    public string? Gate { get; set; }

    public bool StartOperation { get; set; }

    public string? OperationId { get; set; }
}

// A flow type whose head cannot be read; Reads counts the attempts per flow id.
[DigitalBrain.Neuron(Alias)]
public sealed class FailingHeadFlow : Flow<TestInner>, IFlowStarter
{
    public const string Alias = "test.failing-head";

    internal static readonly ConcurrentDictionary<string, int> Reads = new(StringComparer.Ordinal);

    protected override IReadOnlyList<string> Steps => ["only"];

    public Task StartFromJson(string title, string owner, string? parentFlowId, string innerJson) => Start(title, owner, parentFlowId, new TestInner());

    public Task<FlowHead> Head()
    {
        Reads.AddOrUpdate(Id.ToString(), 1, (_, count) => count + 1);
        throw new NotSupportedException("no head");
    }

    protected override Task<StepResult<TestInner>> Run(string step, TestInner inner, CancellationToken ct) => Task.FromResult(new StepResult<TestInner>(inner));
}

// Publishes flow.* signals as a flow would, in any order, for the facts that fold redelivered or early signals.
public interface IFlowSignalPump
{
    Task Started(string flowId, string type, string title);

    Task StepStarted(string flowId, string step);

    Task StepCompleted(string flowId, string step);

    Task Done(string flowId);

    Task Waiting(string flowId, string step);

    Task Resolved(string flowId, string step);
}

public sealed record PumpState(int Sent = 0)
{
    public PumpState() : this(0) { }
}

[DigitalBrain.Neuron(Alias)]
public sealed class FlowSignalPump : DigitalBrain.Neuron<PumpState>, IFlowSignalPump
{
    public const string Alias = "test.flow-pump";

    public Task Started(string flowId, string type, string title) => Send(new FlowStarted(flowId, type, title));

    public Task StepStarted(string flowId, string step) => Send(new FlowStep(flowId, step, StepOutcome.Started, null));

    public Task StepCompleted(string flowId, string step) => Send(new FlowStep(flowId, step, StepOutcome.Completed, null));

    public Task Done(string flowId) => Send(new FlowDone(flowId, FlowOutcome.Completed, null));

    public Task Waiting(string flowId, string step) => Send(new FlowWaiting(flowId, step, WaitReason.External, null));

    public Task Resolved(string flowId, string step) => Send(new FlowResolve(flowId, step, WaitOutcome.Arrived, null));

    private Task Send(Signal signal) => Save(State with { Sent = State.Sent + 1 }, signal);
}

public sealed record TestPlan() : OperationPlan(DryRun: false);

// Three steps; "second" optionally waits External.
[DigitalBrain.Neuron(Alias)]
public sealed class TestFlow : Flow<TestInner>, IFlowStarter
{
    public const string Alias = "test.flow";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static readonly ConcurrentDictionary<string, TaskCompletionSource> Gates = new(StringComparer.Ordinal);

    internal static readonly ConcurrentDictionary<string, IOperation> Operations = new(StringComparer.Ordinal);

    private static readonly string[] Names = ["first", "second", "third"];

    protected override IReadOnlyList<string> Steps => Names;

    internal static TaskCompletionSource Gate(string name)
        => Gates.GetOrAdd(name, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    public Task StartFromJson(string title, string owner, string? parentFlowId, string innerJson)
        => Start(title, owner, parentFlowId, JsonSerializer.Deserialize<TestInner>(innerJson, Json) ?? new TestInner());

    public Task<FlowHead> Head() => Task.FromResult(new FlowHead([.. Names], State.Owner, State.TraceId, State.ParentFlowId, State.Started,
        JsonSerializer.Serialize(State.Inner, Json), State.Title, State.Status, [.. State.Done], State.Ended, State.Inner.OperationId));

    protected override async Task<StepResult<TestInner>> Run(string step, TestInner inner, CancellationToken ct)
    {
        switch (step)
        {
            case "second" when inner.Wait:
                if (inner.StartOperation)
                {
                    var operation = await Context.Operations.Start(new TestPlan(), ct);
                    Operations[operation.Id.Value] = operation;
                    await operation.Report(0.5);
                    inner.OperationId = operation.Id.Value;
                }
                return new StepResult<TestInner>(inner, new Waiting(step, WaitReason.External, null, null));
            case "second" when inner.WaitApproval:
                return new StepResult<TestInner>(inner, new Waiting(step, WaitReason.Approval, null, null));
            case "third" when inner.Gate is { } gate:
                await Gate(gate).Task.WaitAsync(ct);
                return new StepResult<TestInner>(inner);
            default:
                return new StepResult<TestInner>(inner);
        }
    }
}
