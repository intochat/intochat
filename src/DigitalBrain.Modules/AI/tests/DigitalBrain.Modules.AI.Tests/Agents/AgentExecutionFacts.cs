using DigitalBrain.AI;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Models;
using DigitalBrain.Framework.Metering;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.AI.Tests.Agents;

public sealed class AgentExecutionFacts
{
    // The framework classifies an agent by its key's brain; a user stamped into one brain never
    // reaches another brain's agent, so the model is never called for it.
    [Fact]
    public async Task AnAgentKeyedUnderAnotherBrainIsRefusedForAStampedUser()
    {
        var ct = TestContext.Current.CancellationToken;
        var model = StubChatClient.Replying("scoped answer");
        await using var brain = await AgentFacts.Brain(model, ct);
        AgentFacts.StampUser();
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AgentFacts.Collect(brain.Get<IAgent>("elsewhere/agents/x"), AgentTurn.Text("question"), ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => brain.Get<IAgent>("elsewhere/agents/x").Start(AgentTurn.Text("question")));
            Assert.Empty(model.Requests);
            var events = await AgentFacts.Collect(brain.Get<IAgent>(AgentFacts.Key("x")), AgentTurn.Text("question"), ct);
            Assert.Single(events.OfType<AgentTurnEvent.Finished>());
            Assert.Single(model.Requests);
        }
        finally { Orleans.Runtime.RequestContext.Clear(); }
    }

    [Fact]
    public async Task MeteringStorageFailureDoesNotInvalidateASuccessfulExecution()
    {
        var ct = TestContext.Current.CancellationToken;
        var sink = new FailingUsageSink();
        await using var brain = await AgentFacts.Brain(StubChatClient.Replying("kept answer"), ct, services => services.AddSingleton<IIntentUsageSink>(sink));
        var agent = brain.Get<IAgent>(AgentFacts.Key("metering-failure"));
        var events = await AgentFacts.Collect(agent, AgentTurn.Text("question"), ct);
        Assert.Equal(1, sink.FlushCalls);
        Assert.Equal("kept answer", Assert.Single(events.OfType<AgentTurnEvent.Text>()).Content);
        Assert.Single(events.OfType<AgentTurnEvent.Finished>());
        Assert.Empty(events.OfType<AgentTurnEvent.Failed>());
        Assert.Equal(AgentRunStatus.Completed, Assert.Single((await agent.Read()).Runs).Status);
    }

    private sealed class FailingUsageSink : IIntentUsageSink
    {
        private int _flushCalls;
        public int FlushCalls => _flushCalls;
        public Task RecordAsync(string intentId, IIntentUsageEntry entry, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task FlushAsync(IntentUsageBatch intent, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _flushCalls);
            throw new IOException("Usage storage unavailable");
        }
    }

    [Fact]
    public async Task ExecutionWithoutAProviderReturnsFailureInsteadOfSuccess()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AIModule>().StartAsync(ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("unavailable"));
        var events = await AgentFacts.Collect(agent, AgentTurn.Text("question"), ct);
        Assert.Single(events.OfType<AgentTurnEvent.Failed>());
        Assert.Empty(events.OfType<AgentTurnEvent.Finished>());
        Assert.Equal(AgentRunStatus.Failed, Assert.Single((await agent.Read()).Runs).Status);
        Assert.False((await brain.Get<IModelCatalog>("default").List()).Automatic.Available);
    }

    // The deployment's own client honors provider-neutral sampling; a provider-specific setting it
    // cannot honor fails the turn instead of being dropped.
    [Fact]
    public async Task TheHostClientCarriesSamplingAndRefusesProviderSettings()
    {
        var ct = TestContext.Current.CancellationToken;
        ChatOptions? seen = null;
        var client = new StubChatClient((_, options, _) => { seen = options; return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))); });
        await using var brain = await AgentFacts.Brain(client, ct);
        var tuned = brain.Get<IAgent>(AgentFacts.Key("tuned"));
        await tuned.Configure(new() { Options = new(Temperature: 0.25f, TopP: 0.5f, MaxOutputTokens: 64) }, 0);
        Assert.Equal("ok", await tuned.GetResponse("hello", ct));
        Assert.Equal((0.25f, 0.5f, 64), (seen!.Temperature, seen.TopP, seen.MaxOutputTokens));

        var specific = brain.Get<IAgent>(AgentFacts.Key("specific"));
        await specific.Configure(new() { Options = new(ProviderOptions: new() { ["parallel_tool_calls"] = "false" }) }, 0);
        var events = await AgentFacts.Collect(specific, AgentTurn.Text("hello"), ct);
        Assert.Contains("Host", Assert.Single(events.OfType<AgentTurnEvent.Failed>()).Message, StringComparison.Ordinal);
        Assert.Single(client.Requests);
    }
}
