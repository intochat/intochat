using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace IntoChat.OS.Modules.AI.Tests.Integration.Agents;

public sealed class AgentRecoveryFacts
{
    // Storage holds a run the previous activation never closed; waking the agent must not replay
    // its external work, only record that its outcome is unknown.
    [Trait("host", "own")]
    [Fact]
    public async Task ActivationMarksAnUncommittedRunInterruptedWithoutReplayingExternalWork()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = StubChatClient.Replying("never");
        await using var brain = await AgentFacts.Brain(client, ct);
        var key = AgentFacts.Key("recovered");
        // The run is planted in the module's grain storage as a crashed activation left it.
        var storage = brain.Services.GetRequiredKeyedService<IGrainStorage>(IntoChatOSNames.ClusterGrainStorage);
        await storage.WriteStateAsync("agent", AgentFacts.StateId(key), new GrainState<AgentStorage>(new AgentStorage
        {
            ActiveRun = new AgentRun("interrupted-run", AgentRunStatus.Running, "hello", DateTimeOffset.UtcNow)
            { ToolCalls = [new AgentToolCall("call", "lookup", "{}")] },
        }));

        var agent = brain.Get<IAgent>(key);
        await using var failures = await brain.Observe<TurnFailed>(agent, ct);

        // The first call wakes the agent; the run's end still reaches a caller who only has signals.
        var state = await agent.Read();
        var failed = await failures.NextAsync(ct: ct);
        Assert.Equal(("interrupted-run", false), (failed.RunId, failed.Cancelled));
        Assert.Contains("outcomes may be unknown", failed.Error, StringComparison.Ordinal);

        Assert.Null(state.ActiveRun);
        var run = Assert.Single(state.Runs);
        Assert.Equal(("interrupted-run", AgentRunStatus.Interrupted), (run.RunId, run.Status));
        Assert.Contains("outcomes may be unknown", run.Error, StringComparison.Ordinal);
        Assert.Equal("tool_interrupted", Assert.Single(run.ToolCalls).Code);
        Assert.Empty(await brain.Get<IAgent>(key).Transcript());
        Assert.Empty(client.Requests);
    }
}
