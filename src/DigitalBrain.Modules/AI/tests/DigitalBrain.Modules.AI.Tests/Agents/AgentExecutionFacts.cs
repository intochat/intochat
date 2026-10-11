using DigitalBrain.AI;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Models;
using DigitalBrain.Framework.Metering;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.AI.Tests.Agents;

public sealed class AgentExecutionFacts
{
    // The framework classifies an agent by its key's brain; a user calling from one brain never
    // reaches another brain's agent, so the model is never called for it.
    [Fact]
    public async Task AnAgentKeyedUnderAnotherBrainIsRefusedForTheCaller()
    {
        var ct = TestContext.Current.CancellationToken;
        var model = StubChatClient.Replying("scoped answer");
        await using var brain = await AgentFacts.Brain(model, ct);
        using var caller = brain.Owner.AsCaller();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AgentFacts.Collect(brain.Get<IAgent>("elsewhere/agents/x"), AgentTurn.Text("question"), ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => brain.Get<IAgent>("elsewhere/agents/x").Start(AgentTurn.Text("question")));
        Assert.Empty(model.Requests);
        var events = await AgentFacts.Collect(brain.Get<IAgent>(AgentFacts.Key("x")), AgentTurn.Text("question"), ct);
        Assert.Single(events.OfType<AgentTurnEvent.Finished>());
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task MeteringStorageFailureDoesNotInvalidateASuccessfulExecution()
    {
        var ct = TestContext.Current.CancellationToken;
        var sink = new FailingUsageSink();
        // The usage sink is in-process storage: a failing one stands in for a storage fault.
        await using var brain = await AgentFacts.Builder(StubChatClient.Replying("kept answer"))
            .ConfigureServices(services => services.AddSingleton<IIntentUsageSink>(sink)).StartAsync(ct);
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
        await using var brain = await TestBrain.Create().WithModule<AIModule>().StartAsync(ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("unavailable"));
        var events = await AgentFacts.Collect(agent, AgentTurn.Text("question"), ct);
        Assert.Single(events.OfType<AgentTurnEvent.Failed>());
        Assert.Empty(events.OfType<AgentTurnEvent.Finished>());
        Assert.Equal(AgentRunStatus.Failed, Assert.Single((await agent.Read()).Runs).Status);
        Assert.False((await brain.Get<IModelCatalog>("default").List()).Automatic.Available);
    }

    // Provider-neutral sampling reaches the model on the wire.
    [Fact]
    public async Task TheAgentsSamplingOptionsReachTheModel()
    {
        var ct = TestContext.Current.CancellationToken;
        ChatOptions? seen = null;
        var client = new StubChatClient((_, options, _) => { seen = options; return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))); });
        await using var brain = await AgentFacts.Builder(client).WithOptions<AIModule, AIOptions>(options =>
        {
            options.ModelProfiles["tuned"] = new() { Provider = "OpenAI", Model = "tuned-model", Capabilities = LlmCapabilities.Tools, SupportsTemperature = true, SupportsTopP = true };
            options.Default.Model = "tuned";
        }).StartAsync(ct);
        var tuned = brain.Get<IAgent>(AgentFacts.Key("tuned"));
        await tuned.Configure(new() { Options = new(Temperature: 0.25f, TopP: 0.5f, MaxOutputTokens: 64) }, 0);
        Assert.Equal("ok", await tuned.GetResponse("hello", ct));
        Assert.Equal((0.25f, 0.5f, 64), (seen!.Temperature, seen.TopP, seen.MaxOutputTokens));
    }
}
