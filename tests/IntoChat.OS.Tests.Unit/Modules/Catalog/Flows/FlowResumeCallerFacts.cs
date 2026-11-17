using DigitalBrain;
using DigitalBrain.Flows;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps.Flows;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Flows;

public sealed class ResumeProbeInner
{
    public string? RanAs { get; set; }

    public string? ChildFlowId { get; set; }
}

// Waits on a schedule, then (in a later turn, after a restart) records who it runs as and starts a child note through the flows index.
[DigitalBrain.Neurons.Neuron("test.resume-probe")]
public sealed class ResumeProbeFlow : Flow<ResumeProbeInner>, IFlowStarter
{
    protected override IReadOnlyList<string> Steps => ["sleep", "record"];

    public Task StartFromJson(string title, string owner, string? parentFlowId, string innerJson)
        => Start(title, owner, parentFlowId, new ResumeProbeInner());

    public Task<FlowHead> Head() => Task.FromResult(new FlowHead([.. Steps], State.Owner, State.TraceId, State.ParentFlowId, State.Started,
        System.Text.Json.JsonSerializer.Serialize(State.Inner, NoteFlow.Json), State.Title, State.Status, [.. State.Done], State.Ended, null));

    protected override async Task<StepResult<ResumeProbeInner>> Run(string step, ResumeProbeInner inner, CancellationToken ct)
    {
        if (step == "sleep")
        {
            return new StepResult<ResumeProbeInner>(inner, new Waiting("sleep", WaitReason.Schedule, Context.Time.GetUtcNow() + TimeSpan.FromMinutes(1), null));
        }
        var child = await Context.Get<IFlows>(IFlows.Key(Id.Brain.Value)).Start(NoteFlow.Alias, "child", null, "{}", ct);
        return new StepResult<ResumeProbeInner>(new ResumeProbeInner { RanAs = Context.Caller?.Actor.GetType().Name, ChildFlowId = child });
    }
}

// Owner decision 3 (17 Nov): work resumed from storage runs as the scheduler of its brain. No person is stored, the kernel is not
// the default, and FlowsNeuron's old "system" owner fallback is gone.
public sealed class FlowResumeCallerFacts
{
    private static readonly BrainId Brain = new("owner");

    [Fact]
    public async Task FlowResumedAfterRestartRunsAsTheScheduler()
    {
        await using var os = TestOS.Local(o => o.Services = new ServiceCollection().BuildServiceProvider())
            .Register<FlowsNeuron>().Register<NoteFlow>().Register<ResumeProbeFlow>();
        var probe = new NeuronId(Brain, "test.resume-probe", "p1");

        using (os.Edge.Enter(os.Edge.Person("ann", "ann", Brain)))
        {
            await os.Get<IFlowStarter>(probe).StartFromJson("Probe", "ann", null, "{}");
        }
        await os.Idle();
        // The flow's activation ends, as a host restart ends it; its expiry reminder outlives it, as the runtime's reminders do
        // (TestOS.Restart also drops reminders, so it cannot stand in here). The reminder's tick brings the flow back.
        await os.Deactivate(probe);
        os.Clock.Advance(TimeSpan.FromMinutes(2));
        await os.Idle();

        var head = await ReadHead(os, probe);
        Assert.Equal(DigitalBrain.Flows.FlowStatus.Completed, head.Status);
        var inner = System.Text.Json.JsonSerializer.Deserialize<ResumeProbeInner>(head.InnerJson, NoteFlow.Json)!;
        Assert.Equal(nameof(DigitalBrain.Access.Actor.Scheduler), inner.RanAs);
        var child = await ReadHead(os, NeuronId.Parse(inner.ChildFlowId!));
        Assert.Equal("scheduler", child.Owner);
    }

    [Fact]
    public async Task AFlowIsNotStartedWithoutACaller()
    {
        await using var os = TestOS.Local(o => o.Services = new ServiceCollection().BuildServiceProvider())
            .Register<FlowsNeuron>().Register<NoteFlow>();
        var flows = os.Get<IFlows>(IFlows.Key(Brain.Value));

        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => flows.Start(NoteFlow.Alias, "orphan", null, "{}", TestContext.Current.CancellationToken));
    }

    private static async Task<FlowHead> ReadHead(TestOS os, NeuronId id)
    {
        using (os.Edge.Enter(os.Edge.Person("ann", "ann", Brain)))
        {
            return await os.Get<IFlowStarter>(id).Head();
        }
    }
}
