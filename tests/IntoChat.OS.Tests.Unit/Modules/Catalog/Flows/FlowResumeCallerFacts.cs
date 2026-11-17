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
        // A flow the host stopped mid-run: the probe's stored state with the sleep step done and nothing waiting, stored for a
        // second flow (a journal record is seeded only before its neuron first saves). After the restart, a person's read wakes it;
        // the activation resumes the run, and does so as the brain's scheduler, not as that person.
        var stopped = new NeuronId(Brain, "test.resume-probe", "p2");
        var midRun = MidRun(os, probe, stopped);
        await os.Restart();
        os.SeedStoredState(stopped, midRun);
        await ReadHead(os, stopped);
        await os.Idle();

        var head = await ReadHead(os, stopped);
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

    private static string MidRun(TestOS os, NeuronId id, NeuronId target)
    {
        var load = typeof(TestJournal).GetMethod("Load", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(System.Text.Json.Nodes.JsonObject));
        var state = (System.Text.Json.Nodes.JsonObject)load.Invoke(os.Journal(id.Brain), [id])!;
        state["status"] = System.Text.Json.JsonSerializer.SerializeToNode(DigitalBrain.Flows.FlowStatus.Running, DigitalBrain.Invocation.RuntimeProtocol.Json);
        state["waiting"] = null;
        state["current"] = null;
        state["done"] = new System.Text.Json.Nodes.JsonArray("sleep");
        state["flowId"] = target.ToString();
        return state.ToJsonString();
    }

    private static async Task<FlowHead> ReadHead(TestOS os, NeuronId id)
    {
        using (os.Edge.Enter(os.Edge.Person("ann", "ann", Brain)))
        {
            return await os.Get<IFlowStarter>(id).Head();
        }
    }
}
