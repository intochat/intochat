using DigitalBrain.AI;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Models.Scripted;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.AI.Tests.Agents;

public sealed class ScriptedTurnFacts
{
    [Fact]
    public async Task AScriptedProfilePlaysAToolCallThenAnswersWithoutAnyProvider()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Probe.Brain(StubChatClient.Replying("unused"), ct);
        // The scripted model is test infrastructure outside the brain: it is scripted unstamped.
        Orleans.Runtime.RequestContext.Clear();
        var model = brain.Get<IScriptedLLM>("turn-model");
        await model.Script([$$$"""{"tool":"neurons_call","arguments":{"contract":"{{{Probe.Contract}}}","key":"research/probes/main","method":"Echo","arguments":"[\"ping\"]"}}""", "Done."]);
        AgentFacts.StampUser();

        var events = await AgentFacts.Collect(brain.Get<IAgent>(AgentFacts.Key("scripted")),
            AgentTurn.Text("go", model: IScriptedLLM.ModelPrefix + "turn-model", contracts: [Probe.Contract]), ct);

        Assert.Equal("neurons_call", Assert.Single(events.OfType<AgentTurnEvent.ToolStarted>()).Name);
        Assert.Contains("echo:ping", Assert.Single(events.OfType<AgentTurnEvent.ToolCompleted>()).Result, StringComparison.Ordinal);
        Assert.Equal("Done.", Assert.Single(events.OfType<AgentTurnEvent.Text>()).Content);
        Assert.Single(events.OfType<AgentTurnEvent.Finished>());
        Orleans.Runtime.RequestContext.Clear();
        var prompts = await model.Prompts();
        Assert.Equal(2, prompts.Count);
        Assert.Contains("echo:ping", prompts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AScriptedModelThatRunsOutOfRepliesFailsTheTurn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AIModule>().StartAsync(ct);
        await brain.Get<IScriptedLLM>("empty-model").Script([]);

        var events = await AgentFacts.Collect(brain.Get<IAgent>(AgentFacts.Key("scripted-empty")),
            AgentTurn.Text("go", model: IScriptedLLM.ModelPrefix + "empty-model"), ct);

        Assert.Contains("ran out of replies", Assert.Single(events.OfType<AgentTurnEvent.Failed>()).Message, StringComparison.Ordinal);
    }

    // A scripted model plays its replies whatever sampling the definition asks for; the publish
    // gate drives such turns, so inference options must never stop them.
    [Fact]
    public async Task AScriptedTurnRunsWhateverInferenceOptionsTheDefinitionCarries()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AIModule>().StartAsync(ct);
        await brain.Get<IScriptedLLM>("tuned-model").Script(["Tuned."]);
        var agent = brain.Get<IAgent>(AgentFacts.Key("tuned"));
        await agent.Configure(new() { Options = new(Temperature: 0.2f, TopP: 0.9f, MaxOutputTokens: 64) }, 0);

        var events = await AgentFacts.Collect(agent, AgentTurn.Text("go", model: IScriptedLLM.ModelPrefix + "tuned-model"), ct);

        Assert.Empty(events.OfType<AgentTurnEvent.Failed>());
        Assert.Equal("Tuned.", Assert.Single(events.OfType<AgentTurnEvent.Text>()).Content);
    }
}
