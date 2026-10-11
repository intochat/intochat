using DigitalBrain.AI;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Models.OpenAI;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;
using DigitalBrain.Testing.Model;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.AI.Tests.Agents;

// Whole agent turns against a provider model: the tool loop and failures all travel the real
// OpenAI adapter to the fake.
public sealed class AgentModelTurnFacts
{
    private const string Luna = "gpt-5.6-luna";

    // The probe's contract is in the host's compiled layer, which no module of the test assembly registers.
    private static Task<ITestBrain> Brain(FakeModelServer model, CancellationToken ct)
        => TestBrain.Create().WithModule<AIModule>().WithFakeModel(model)
            .ConfigureServices(services => services.AddSingleton(new DigitalBrain.Framework.CompiledTypes([typeof(INeuron).Assembly, typeof(ILoopProbe).Assembly])))
            .StartAsync(ct);

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
