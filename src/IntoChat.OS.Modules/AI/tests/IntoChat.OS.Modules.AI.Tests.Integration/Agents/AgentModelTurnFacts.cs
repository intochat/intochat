using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Models.OpenAI;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Contracts;
using IntoChat.OS.Testing.Model;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.AI.Tests.Integration.Agents;

// Whole agent turns against a provider model: the tool loop and failures all travel the real
// OpenAI adapter to the fake.
public sealed class AgentModelTurnFacts
{
    private const string Luna = "gpt-5.6-luna";

    // The probe's contract is in the host's compiled layer, which no module of the test assembly registers.
    private static Task<ITestBrain> Brain(FakeModelServer model, CancellationToken ct)
        => TestBrain.Create().WithModule<AIModule>().WithFakeModel(model).WithNeuronModule<LoopProbeNeurons>()
            .ConfigureServices(services => services.AddSingleton(new IntoChat.OS.Framework.CompiledTypes([typeof(INeuron).Assembly, typeof(ILoopProbe).Assembly])))
            .StartAsync(ct);

    [Trait("host", "own")]
    [Fact]
    public async Task ATurnReadsTheContextsItNames()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        model.Script(Luna, "Noted.");
        await using var brain = await Brain(model, ct);
        using var caller = brain.Owner.AsCaller();
        var context = brain.Get<IAgentContext>(AgentFacts.Scope + "/memory/context");
        await context.Set(new ContextEntry("active-intents", "Intent 42: ship the plan", []));

        await AgentFacts.Collect(brain.Get<IAgent>(AgentFacts.Key("context-turn")),
            AgentTurn.Text("go", model: nameof(IGpt56Luna), contexts: [AgentFacts.Scope + "/memory/context"]), ct);

        Assert.Contains("Intent 42: ship the plan", model.Requests(Luna)[0].GetRawText(), StringComparison.Ordinal);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AModelPlaysAToolCallThenAnswers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        model.Script(Luna,
            FakeReply.Tool("neurons_call", $$"""{"contract":"{{Probe.Contract}}","key":"{{AgentFacts.Scope}}/probes/main","method":"Echo","arguments":"[\"ping\"]"}"""),
            "Done.");
        await using var brain = await Brain(model, ct);
        using var caller = brain.Owner.AsCaller();

        var events = await AgentFacts.Collect(brain.Get<IAgent>(AgentFacts.Key("tool-turn")),
            AgentTurn.Text("go", model: nameof(IGpt56Luna), contracts: [Probe.Contract]), ct);

        Assert.Equal("neurons_call", Assert.Single(events.OfType<AgentTurnEvent.ToolStarted>()).Name);
        Assert.Contains("echo:ping", Assert.Single(events.OfType<AgentTurnEvent.ToolCompleted>()).Result, StringComparison.Ordinal);
        Assert.Equal("Done.", Assert.Single(events.OfType<AgentTurnEvent.Text>()).Content);
        Assert.Single(events.OfType<AgentTurnEvent.Finished>());
        Assert.Equal(2, model.Requests(Luna).Count);
        Assert.Contains("echo:ping", model.Requests(Luna)[1].GetRawText(), StringComparison.Ordinal);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AModelThatRunsOutOfRepliesFailsTheTurn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        model.Script(Luna);
        await using var brain = await Brain(model, ct);

        var events = await AgentFacts.Collect(brain.Get<IAgent>(AgentFacts.Key("empty")),
            AgentTurn.Text("go", model: nameof(IGpt56Luna)), ct);

        Assert.Contains("Status: 500", Assert.Single(events.OfType<AgentTurnEvent.Failed>()).Message, StringComparison.Ordinal);
    }
}
