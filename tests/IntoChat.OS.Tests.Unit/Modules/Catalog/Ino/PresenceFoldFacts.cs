using DigitalBrain;
using DigitalBrain.Flows;
using DigitalBrain.Testing;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Agents.Context.Signals;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Ino;

public sealed record FakeConversationState(ConversationView? View)
{
    public FakeConversationState() : this((ConversationView?)null) { }
}

public interface IFakeConversation : IConversation
{
    Task Seed(ConversationView view);
}

// Stands in for the conversation neuron: Read answers the seeded view and publishes ConversationChanged.
[DigitalBrain.Neurons.Neuron(IConversation.Type)]
public sealed class FakeConversation : DigitalBrain.Neurons.Neuron<FakeConversationState>, IFakeConversation
{
    private ConversationId ConvId
        => ConversationId.Parse(Id.Key.StartsWith("conversations/", StringComparison.Ordinal) ? Id.Key["conversations/".Length..] : Id.Key);

    public Task Seed(ConversationView view)
        => Save(State with { View = view }, new ConversationChanged(view.Id, view.Revision));

    public Task<ConversationView> Read(long? sinceRevision = null)
        => Task.FromResult(State.View ?? new ConversationView(ConvId, 0, [], [], null, [], false, []));

    public Task Seat(string agentId, AgentDefinition definition) => Task.CompletedTask;
    public Task Say(string text, ModelId? model = null) => throw new NotSupportedException();
    public Task Stop() => Task.CompletedTask;
    public Task Regenerate(EntryId reply) => throw new NotSupportedException();
    public Task Post(string text, string id) => Task.CompletedTask;
    public Task Invite(AccountId friend) => throw new NotSupportedException();
}

public sealed record FakeContextState(ContextEntry[] Entries)
{
    public FakeContextState() : this([]) { }
}

// The agent context as the AI module keeps it: provenance stamped from the caller, ContextUpdated/ContextRemoved on change.
[DigitalBrain.Neurons.Neuron("agent-context")]
public sealed class FakeAgentContext : DigitalBrain.Neurons.Neuron<FakeContextState>, IAgentContext
{
    public Task Set(ContextEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var kept = entry with { Provenance = entry.Provenance ?? new EntryProvenance("", DateTimeOffset.UtcNow, ContextTrust.App) };
        var entries = State.Entries.Where(item => item.Name != entry.Name).Append(kept).ToArray();
        return Save(State with { Entries = entries }, new ContextUpdated(Id.ToString(), entries.Length, entry.Name));
    }

    public Task Remove(string name) => Save(State with { Entries = [.. State.Entries.Where(item => item.Name != name)] }, new ContextRemoved(Id.ToString(), 0, name));

    public Task<ContextSnapshot> Read() => Task.FromResult(new ContextSnapshot(0, State.Entries));

    public async Task<bool> Upsert(ContextEntry entry) { await Set(entry); return true; }

    public async Task<bool> Withdraw(string name) { await Remove(name); return true; }
}

public sealed record FakeFlowsState(FlowView[] Flows)
{
    public FakeFlowsState() : this([]) { }
}

public interface IFakeFlows : IFlows
{
    Task Seed(FlowView flow);
}

[DigitalBrain.Neurons.Neuron("flows")]
public sealed class FakeFlows : DigitalBrain.Neurons.Neuron<FakeFlowsState>, IFakeFlows
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

[DigitalBrain.Neurons.Neuron(Alias)]
public sealed class FlowDonePump : DigitalBrain.Neurons.Neuron<FlowDonePumpState>, IFlowDonePump
{
    public const string Alias = "test.presence-flow-pump";

    public Task Failed(string flowId, string detail) => Save(State with { Sent = State.Sent + 1 }, new FlowDone(flowId, FlowOutcome.Failed, detail));
}

// The presence fold on TestOS, where the clock and activations are the facts' to drive.
public sealed class PresenceFoldFacts
{
    private static readonly BrainId Brain = new("local");
    private const string ConvId = "main";
    private const string LegacyAgent = "ino/conversations/main";
    private static readonly NeuronId PresenceId = IPresence.Key(Brain.Value);

    private static async Task<TestOS> Started()
    {
        var os = TestOS.Local();
        os.Register<PresenceNeuron>()
            .Register<SessionListNeuron>()
            .Register<InoConversationsNeuron>()
            .Register<IntoChat.OS.Modules.Apps.Approvals.ApprovalsNeuron>()
            .Register<FakeConversation>()
            .Register<FakeAgentContext>()
            .Register<FakeFlows>()
            .Register<FlowDonePump>();
        await os.Get<ISessionList>(new NeuronId(Brain, Sessions.Type, ISessionList.DefaultKey)).Open(ConvId);
        return os;
    }

    private static IFakeConversation ConversationOf(TestOS os)
        => os.Get<IFakeConversation>(new NeuronId(Brain, IConversation.Type, IConversation.KeyOf(ConversationId.Parse(ConvId))));

    private static IPresence Presence(TestOS os) => os.Get<IPresence>(PresenceId);

    private static async Task<Presence> Settled(TestOS os)
    {
        await os.Idle();
        return await Presence(os).Read(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReactivationRebuildsPresenceFromConversationAndContexts()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        await using var os = await Started();
        var run = new AgentRun("run-1", AgentRunStatus.Running, "hi", os.Clock.GetUtcNow())
        {
            ToolCalls = [new AgentToolCall("c1", "files_write", """{"name":"a.txt"}""")],
        };
        await ConversationOf(os).Seed(new ConversationView(
            ConversationId.Parse(ConvId), 1, [], [], run, [], false, ["context/security.posture"]));
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

        var working = await Settled(os);
        Assert.Equal(PresenceState.Working, working.State);
        Assert.Equal("files_write", working.Reason);
    }

    [Fact]
    public async Task ListeningExpiresAfterTwoSeconds()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        await using var os = await Started();
        await Settled(os);

        var run = new AgentRun("run-1", AgentRunStatus.Running, "hi", os.Clock.GetUtcNow());
        await ConversationOf(os).Seed(new ConversationView(ConversationId.Parse(ConvId), 1, [], [], run, [], false, []));
        Assert.Equal(PresenceState.Listening, (await Settled(os)).State);

        os.Clock.Advance(TimeSpan.FromSeconds(2.1));

        Assert.Equal(PresenceState.Idle, (await Settled(os)).State);
    }

    [Fact]
    public async Task AttentionExpiresAfterTenSeconds()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        await using var os = await Started();
        await Settled(os);
        const string flowId = "local/test.flow/f1";
        await os.Get<IFakeFlows>(IFlows.Key(Brain.Value)).Seed(new FlowView(flowId, "test.flow", "Report", "person", "t1", FlowStatus.Waiting, ["only"], [],
            "only", null, null, null, 0, [], [], null, os.Clock.GetUtcNow(), null, null, "assistant", LegacyAgent));
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
