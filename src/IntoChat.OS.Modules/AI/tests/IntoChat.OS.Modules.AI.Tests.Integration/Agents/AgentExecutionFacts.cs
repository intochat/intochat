using System.Collections.Concurrent;
using IntoChat.OS.AI;
using IntoChat.OS.AI.Agents;
using IntoChat.OS.AI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.AI.Tests.Integration.Agents;

public sealed class AgentExecutionFacts(AIHost host)
{
    // The framework classifies an agent by its key's brain; a user calling from one brain never
    // reaches another brain's agent, so the model is never called for it.
    [Trait("host", "own")]
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

    // Model usage is priced per provider call by the kernel's cost meter, so a run has nothing left
    // to flush when it ends: a completed run warns about nothing.
    [Trait("host", "own")]
    [Fact]
    public async Task ACompletedRunLogsNoWarning()
    {
        var ct = TestContext.Current.CancellationToken;
        var warnings = new WarningLog();
        await using var brain = await AgentFacts.Builder(StubChatClient.Replying("quiet answer"))
            .ConfigureServices(services => services.AddLogging(logging => logging.AddProvider(warnings))).StartAsync(ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("quiet"));

        var events = await AgentFacts.Collect(agent, AgentTurn.Text("question"), ct);

        Assert.Single(events.OfType<AgentTurnEvent.Finished>());
        Assert.Equal(AgentRunStatus.Completed, Assert.Single((await agent.Read()).Runs).Status);
        Assert.Empty(warnings.From(typeof(AgentNeuron).FullName!));
    }

    private sealed class WarningLog : ILoggerProvider
    {
        private readonly ConcurrentQueue<(string Category, string Text)> _warnings = new();

        public string[] From(string category) => [.. _warnings.Where(warning => warning.Category == category).Select(warning => warning.Text)];

        public ILogger CreateLogger(string categoryName) => new Sink(categoryName, _warnings);

        public void Dispose() { }

        private sealed class Sink(string category, ConcurrentQueue<(string, string)> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel)) { warnings.Enqueue((category, $"{formatter(state, exception)} {exception?.Message}")); }
            }
        }
    }

    [Fact]
    public async Task ExecutionWithoutAProviderReturnsFailureInsteadOfSuccess()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var agent = brain.Get<IAgent>(AgentFacts.Key("unavailable"));
        var events = await AgentFacts.Collect(agent, AgentTurn.Text("question"), ct);
        Assert.Single(events.OfType<AgentTurnEvent.Failed>());
        Assert.Empty(events.OfType<AgentTurnEvent.Finished>());
        Assert.Equal(AgentRunStatus.Failed, Assert.Single((await agent.Read()).Runs).Status);
        Assert.False((await brain.Get<IModelCatalog>("default").List()).Automatic.Available);
    }

    // Provider-neutral sampling reaches the model on the wire.
    [Trait("host", "own")]
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
