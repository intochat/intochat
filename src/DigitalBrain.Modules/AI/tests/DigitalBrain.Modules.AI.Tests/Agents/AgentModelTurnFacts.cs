using DigitalBrain.AI;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Models.OpenAI;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;
using DigitalBrain.Testing.Model;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.AI.Tests.Agents;

// Whole agent turns against a provider model: the tool loop, failures and inference options all
// travel the real OpenAI adapter to the fake.
public sealed class AgentModelTurnFacts
{
    private const string Luna = "gpt-5.6-luna";

    private static Task<ModuleBrain> Brain(FakeModelServer model, CancellationToken ct)
    {
        AgentFacts.StampUser();
        return ModuleTest.Create().WithModule<AIModule>().WithFakeModel(model)
            .ConfigureSilo(silo => silo.Services.AddSingleton(new ContractCatalog([typeof(INeuron).Assembly, typeof(ILoopProbe).Assembly])))
            .StartAsync(ct);
    }

    [Fact]
    public async Task AModelPlaysAToolCallThenAnswers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        model.Script(Luna,
            FakeReply.Tool("neurons_call", $$"""{"contract":"{{Probe.Contract}}","key":"research/probes/main","method":"Echo","arguments":"[\"ping\"]"}"""),
            "Done.");
        await using var brain = await Brain(model, ct);

        var events = await AgentFacts.Collect(brain.Get<IAgent>(AgentFacts.Key("tool-turn")),
            AgentTurn.Text("go", model: nameof(IGpt56Luna), contracts: [Probe.Contract]), ct);

        Assert.Equal("neurons_call", Assert.Single(events.OfType<AgentTurnEvent.ToolStarted>()).Name);
        Assert.Contains("echo:ping", Assert.Single(events.OfType<AgentTurnEvent.ToolCompleted>()).Result, StringComparison.Ordinal);
        Assert.Equal("Done.", Assert.Single(events.OfType<AgentTurnEvent.Text>()).Content);
        Assert.Single(events.OfType<AgentTurnEvent.Finished>());
        Assert.Equal(2, model.Requests(Luna).Count);
        Assert.Contains("echo:ping", model.Requests(Luna)[1].GetRawText(), StringComparison.Ordinal);
    }

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
