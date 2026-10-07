using DigitalBrain.AI.Agents;
using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;
using Xunit;

namespace DigitalBrain.Modules.AI.Tests.Agents;

public sealed class AgentRecoveryFacts
{
    // Storage holds a run the previous activation never closed; waking the agent must not replay
    // its external work, only record that its outcome is unknown.
    [Fact]
    public async Task ActivationMarksAnUncommittedRunInterruptedWithoutReplayingExternalWork()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = StubChatClient.Replying("never");
        await using var brain = await AgentFacts.Brain(client, ct);
        var key = AgentFacts.Key("recovered");
        var storage = brain.SiloServices.GetRequiredKeyedService<IGrainStorage>(DigitalBrainNames.DefaultGrainStorage);
        await storage.WriteStateAsync("agent", GrainId.Create("agent", key), new GrainState<AgentStorage>(new AgentStorage
        {
            ActiveRun = new AgentRun("interrupted-run", AgentRunStatus.Running, "hello", DateTimeOffset.UtcNow)
            { ToolCalls = [new AgentToolCall("call", "lookup", "{}")] },
        }));

        var state = await brain.Get<IAgent>(key).Read();

        Assert.Null(state.ActiveRun);
        var run = Assert.Single(state.Runs);
        Assert.Equal(("interrupted-run", AgentRunStatus.Interrupted), (run.RunId, run.Status));
        Assert.Contains("outcomes may be unknown", run.Error, StringComparison.Ordinal);
        Assert.Equal("tool_interrupted", Assert.Single(run.ToolCalls).Code);
        Assert.Empty(await brain.Get<IAgent>(key).Transcript());
        Assert.Empty(client.Requests);
    }
}
