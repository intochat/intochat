using DigitalBrain;
using DigitalBrain.Flows;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Agents.Context.Signals;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.Apps.Flows;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Ino;

public sealed record FakeAgentState(AgentState? Agent)
{
    public FakeAgentState() : this((AgentState?)null) { }
}

public interface IFakeAgent : IAgent
{
    Task Seed(AgentState state);

    Task Publish(TurnStarted started);
}

// Stands in for the agent neuron: Read answers the seeded state, and the facts publish its turn signals.
[DigitalBrain.Neuron(AgentSignals.NeuronType)]
public sealed class FakeAgent : DigitalBrain.Neuron<FakeAgentState>, IFakeAgent
{
    public Task Seed(AgentState state) => Save(State with { Agent = state }, new AgentConfigured(Id.Key, 1));

    public Task Publish(TurnStarted started) => Save(State, started);

    public Task<AgentState> Read() => Task.FromResult(State.Agent ?? new AgentState(0, new AgentDefinition(), null, null, []));

    public Task Configure(AgentDefinition definition, long expectedRevision) => throw new NotSupportedException();

    public IAsyncEnumerable<AgentTurnEvent> Run(AgentTurn turn, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<string> Start(AgentTurn turn, string? flowId = null) => throw new NotSupportedException();

    public Task Cancel() => throw new NotSupportedException();

    public Task CancelRun(string runId) => throw new NotSupportedException();

    public Task<AiMessage[]> Transcript() => Task.FromResult<AiMessage[]>([]);

    public Task Clear() => throw new NotSupportedException();

    public Task Post(string text, string id) => throw new NotSupportedException();
}

public sealed record FakeContextState(ContextEntry[] Entries)
{
    public FakeContextState() : this([]) { }
}

// The agent context as the AI module keeps it: provenance stamped from the caller, ContextUpdated/ContextRemoved on change.
[DigitalBrain.Neuron("agent-context")]
public sealed class FakeAgentContext : DigitalBrain.Neuron<FakeContextState>, IAgentContext
{
    public Task Set(ContextEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // TestOS does not carry a stamped caller into the turn, so a fact names the trust on the entry it sets.
        var kept = entry with { Provenance = entry.Provenance ?? new EntryProvenance("", DateTimeOffset.UtcNow, ContextTrust.App) };
        var entries = State.Entries.Where(item => item.Name != entry.Name).Append(kept).ToArray();
        return Save(State with { Entries = entries }, new ContextUpdated(Id.ToString(), entries.Length, entry.Name));
    }

    public Task Remove(string name) => Save(State with { Entries = [.. State.Entries.Where(item => item.Name != name)] }, new ContextRemoved(Id.ToString(), 0, name));

    public Task<ContextSnapshot> Read() => Task.FromResult(new ContextSnapshot(0, State.Entries));
}

public sealed record FakeFlowsState(FlowView[] Flows)
{
    public FakeFlowsState() : this([]) { }
}

public interface IFakeFlows : IFlows
{
    Task Seed(FlowView flow);
}

// Stands in for the flows index: Read answers seeded views. TestOS does not carry a stamped caller into a neuron's turn, so the
// facts seed the App and Conversation that FlowsNeuron records from it (FlowsNeuronFacts proves that part on a silo).
[DigitalBrain.Neuron("flows")]
public sealed class FakeFlows : DigitalBrain.Neuron<FakeFlowsState>, IFakeFlows
{
    public Task Seed(FlowView flow) => Save(State with { Flows = [.. State.Flows.Where(item => item.FlowId != flow.FlowId), flow] }, new FlowsChanged(flow.FlowId));

    public Task<FlowView?> Read(string flowId, CancellationToken ct = default) => Task.FromResult(State.Flows.FirstOrDefault(flow => flow.FlowId == flowId));

    public Task<FlowView[]> List(FlowQuery query, CancellationToken ct = default) => Task.FromResult(State.Flows);

    public Task<string> Start(string type, string title, string? parentFlowId, string innerJson, CancellationToken ct = default, string? flowId = null) => throw new NotSupportedException();

    public Task Resume(string flowId, string step, WaitOutcome outcome, CancellationToken ct = default) => throw new NotSupportedException();

    public Task Cancel(string flowId, string reason, CancellationToken ct = default) => throw new NotSupportedException();

    public Task Relate(string flowId, FlowRelation relation, CancellationToken ct = default) => throw new NotSupportedException();
}

public sealed record FlowDonePumpState(int Sent)
{
    public FlowDonePumpState() : this(0) { }
}

public interface IFlowDonePump
{
    Task Failed(string flowId, string detail);
}

[DigitalBrain.Neuron(Alias)]
public sealed class FlowDonePump : DigitalBrain.Neuron<FlowDonePumpState>, IFlowDonePump
{
    public const string Alias = "test.presence-flow-pump";

    public Task Failed(string flowId, string detail) => Save(State with { Sent = State.Sent + 1 }, new FlowDone(flowId, FlowOutcome.Failed, detail));
}

// The presence fold on TestOS, where the clock and activations are the facts' to drive.
public sealed class PresenceFoldFacts
{
    private static readonly BrainId Brain = new("local");

    private const string Agent = "ino/conversations/main";

    private static readonly NeuronId PresenceId = IPresence.Key(Brain.Value);

    private static async Task<TestOS> Started()
    {
        var os = TestOS.Local();
        os.Register<PresenceNeuron>().Register<InoConversationsNeuron>().Register<IntoChat.OS.Modules.Apps.Approvals.ApprovalsNeuron>()
            .Register<FakeAgent>().Register<FakeAgentContext>().Register<FakeFlows>().Register<FlowDonePump>();
        await os.Get<IInoConversations>(new NeuronId(Brain, "ino.conversations", IInoConversations.DefaultKey)).Use(Agent);
        return os;
    }

    private static IFakeAgent AgentOf(TestOS os) => os.Get<IFakeAgent>(new NeuronId(Brain, AgentSignals.NeuronType, Agent));

    private static IPresence Presence(TestOS os) => os.Get<IPresence>(PresenceId);

    private static async Task<Presence> Settled(TestOS os)
    {
        await os.Idle();
        return await Presence(os).Read(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReactivationRebuildsPresenceFromAgentAndContexts()
    {
        await using var os = await Started();
        var run = new AgentRun("run-1", AgentRunStatus.Running, "hi", os.Clock.GetUtcNow())
        {
            ToolCalls = [new AgentToolCall("c1", "neuron_call", """{"method":"Write"}""")],
        };
        await AgentOf(os).Seed(new AgentState(1, new AgentDefinition { Contexts = ["context/security.posture"] }, null, run, []));
        await os.Get<IAgentContext>(new NeuronId(Brain, "agent-context", "context/security.posture"))
            .Set(new ContextEntry("stop", "Stop now.", [], [ContextTags.Interrupt], new EntryProvenance("person", DateTimeOffset.UtcNow, ContextTrust.User)));

        var paused = await Settled(os);

        Assert.Equal(PresenceState.Paused, paused.State);
        Assert.Equal("Stop now.", paused.Reason);

        // The interrupt goes while the presence is not active; it learns of it only by rebuilding.
        await os.Deactivate(PresenceId);
        await os.Idle();
        await os.Get<IAgentContext>(new NeuronId(Brain, "agent-context", "context/security.posture")).Remove("stop");
        await os.Idle();
        await os.Deactivate(PresenceId);
        await os.Idle();

        Assert.Equal(PresenceState.Working, (await Settled(os)).State);
    }

    [Fact]
    public async Task ListeningExpiresAfterTwoSeconds()
    {
        await using var os = await Started();
        await Settled(os);

        await AgentOf(os).Publish(new TurnStarted(Agent, "run-1", "hi"));
        Assert.Equal(PresenceState.Listening, (await Settled(os)).State);

        os.Clock.Advance(TimeSpan.FromSeconds(2.1));

        Assert.Equal(PresenceState.Idle, (await Settled(os)).State);
    }

    [Fact]
    public async Task AttentionExpiresAfterTenSeconds()
    {
        await using var os = await Started();
        await Settled(os);
        const string flowId = "local/test.flow/f1";
        await os.Get<IFakeFlows>(IFlows.Key(Brain.Value)).Seed(new FlowView(flowId, "test.flow", "Report", "person", "t1", FlowStatus.Waiting, ["only"], [],
            "only", null, null, null, 0, [], [], null, os.Clock.GetUtcNow(), null, null, "assistant", Agent));
        await os.Idle();

        await os.Get<IFlowDonePump>(new NeuronId(Brain, FlowDonePump.Alias, "pump")).Failed(flowId, "boom");
        var attention = await Settled(os);
        Assert.Equal(PresenceState.Attention, attention.State);
        Assert.Equal(flowId, attention.FlowId);

        os.Clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(PresenceState.Attention, (await Settled(os)).State);
        os.Clock.Advance(TimeSpan.FromSeconds(1.5));

        Assert.Equal(PresenceState.Idle, (await Settled(os)).State);
    }
}
