using DigitalBrain.AI;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Scripted;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.AI.Tests;

public sealed class ScriptedTurnFacts
{
    [Fact]
    public async Task AScriptedProfilePlaysAToolCallThenAnswersWithoutAnyProvider()
    {
        var ct = TestContext.Current.CancellationToken;
        var echo = AIFunctionFactory.Create((string word) => "echo:" + word, "echo");
        await using var brain = await ModuleTest.Create().WithModule<AIModule>()
            .ConfigureSilo(silo => silo.Services.AddSingleton<IAgentToolFactory>(new FixedTools(echo))).StartAsync(ct);
        var model = brain.Get<IScriptedLLM>("turn-model");
        await model.Script(["""{"tool":"echo","arguments":{"word":"ping"}}""", "Done."]);

        var events = new List<AgentTurnEvent>();
        await foreach (var item in brain.Get<IAgentExecution>("scripted").Run(new("agent", "run", "scope", [], "go",
            new ModelSelection(IScriptedLLM.ModelPrefix + "turn-model"), ToolNames: ["echo"]), "intent", ct))
        { events.Add(item); }

        Assert.Equal("echo", Assert.Single(events.OfType<AgentTurnEvent.ToolStarted>()).Name);
        Assert.Contains("echo:ping", Assert.Single(events.OfType<AgentTurnEvent.ToolCompleted>()).Result, StringComparison.Ordinal);
        Assert.Equal("Done.", Assert.Single(events.OfType<AgentTurnEvent.Text>()).Content);
        Assert.Single(events.OfType<AgentTurnEvent.Finished>());
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

        var events = new List<AgentTurnEvent>();
        await foreach (var item in brain.Get<IAgentExecution>("scripted-empty").Run(new("agent", "run", "scope", [], "go",
            new ModelSelection(IScriptedLLM.ModelPrefix + "empty-model")), "intent", ct))
        { events.Add(item); }

        Assert.Contains("ran out of replies", Assert.Single(events.OfType<AgentTurnEvent.Failed>()).Message, StringComparison.Ordinal);
    }

    private sealed class FixedTools(params AIFunction[] tools) : IAgentToolFactory
    {
        public IReadOnlyList<AIFunction> Create(Func<AgentToolContext> context) => tools;
    }
}
