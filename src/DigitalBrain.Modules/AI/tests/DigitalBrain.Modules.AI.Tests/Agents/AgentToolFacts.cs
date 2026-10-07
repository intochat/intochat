using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Agents.Context;
using DigitalBrain.AI.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DigitalBrain.Modules.AI.Tests.Agents;

public sealed class AgentToolFacts
{
    [Fact]
    public async Task AnUnserializableResultStillTerminatesTheToolAttempt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await AgentFacts.Brain(ScriptedClient(), ct, services => services.AddSingleton<IAgentToolFactory>(new UnserializableTool()));
        var agent = brain.Get<IAgent>(AgentFacts.Key("main"));
        var events = await AgentFacts.Collect(agent, AgentTurn.Text("ask", tools: ["lookup"]), ct);
        Assert.Single(events.OfType<AgentTurnEvent.ToolStarted>());
        Assert.Single(events.OfType<AgentTurnEvent.ToolFailed>());
        Assert.Single(events.OfType<AgentTurnEvent.Failed>());
        Assert.Empty(events.OfType<AgentTurnEvent.ToolCompleted>());
        Assert.Equal("tool_failed", Assert.Single((await agent.Read()).Runs[^1].ToolCalls).Code);
    }

    private sealed class UnserializableTool : IAgentToolFactory
    {
        public IReadOnlyList<AIFunction> Create(Func<AgentToolContext> context) =>
            [AIFunctionFactory.Create(() => new { unsupported = typeof(AgentToolFacts) }, new AIFunctionFactoryOptions
            { Name = "lookup", MarshalResult = static (value, _, _) => ValueTask.FromResult(value) })];
    }

    [Fact]
    public async Task ToolDoesNotExecuteUntilTheAgentHasRecordedItsStart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await AgentFacts.Brain(ScriptedClient(), ct, services => services.AddSingleton<IAgentToolFactory, RecordedStartTools>());
        var events = await AgentFacts.Collect(brain.Get<IAgent>(AgentFacts.Key("main")), AgentTurn.Text("ask", tools: ["lookup"]), ct);
        Assert.Equal("\"recorded\"", Assert.Single(events.OfType<AgentTurnEvent.ToolCompleted>()).Result);
    }

    // The tool reads the agent while the turn runs: its own call must already be in the active run.
    private sealed class RecordedStartTools(IGrainFactory grains) : IAgentToolFactory
    {
        public IReadOnlyList<AIFunction> Create(Func<AgentToolContext> context) =>
            [AIFunctionFactory.Create(async () =>
            {
                var call = context().CallId;
                var run = (await grains.GetGrain<IAgent>(AgentFacts.Key("main")).Read()).ActiveRun;
                return run?.ToolCalls.Any(item => item.CallId == call) == true ? "recorded" : "unrecorded";
            }, "lookup")];
    }

    [Fact]
    public async Task ToolsUseActualCallIdentityAndNeverShareRunContext()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await AgentFacts.Brain(ScriptedClient(), ct, services => services.AddSingleton<IAgentToolFactory>(new Tools()));
        async Task<string> Run(string scope)
        {
            var events = await AgentFacts.Collect(brain.Get<IAgent>(scope + "/agents/main"), AgentTurn.Text("ask", runId: "run-" + scope, tools: ["lookup"]), ct);
            Assert.Single(events.OfType<AgentTurnEvent.ToolCompleted>());
            return string.Concat(events.OfType<AgentTurnEvent.Text>().Select(item => item.Content));
        }
        var results = await Task.WhenAll(Run("alpha"), Run("beta"));
        Assert.Contains("alpha/run-alpha/actual-call", results[0], StringComparison.Ordinal);
        Assert.Contains("beta/run-beta/actual-call", results[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingSelectedToolFailsExplicitly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await AgentFacts.Brain(ScriptedClient(), ct);
        var events = await AgentFacts.Collect(brain.Get<IAgent>(AgentFacts.Key("main")), AgentTurn.Text("ask", tools: ["missing"]), ct);
        Assert.Contains("missing", Assert.Single(events.OfType<AgentTurnEvent.Failed>()).Message, StringComparison.Ordinal);
        Assert.Empty(events.OfType<AgentTurnEvent.Finished>());
    }

    [Fact]
    public async Task DelayedToolFailureNeverFinishesTheRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await AgentFacts.Brain(ScriptedClient(), ct, services => services.AddSingleton<IAgentToolFactory>(new Tools(fail: true)));
        var events = await AgentFacts.Collect(brain.Get<IAgent>(AgentFacts.Key("main")), AgentTurn.Text("ask", tools: ["lookup"]), ct);
        Assert.Single(events.OfType<AgentTurnEvent.Failed>());
        Assert.Empty(events.OfType<AgentTurnEvent.Finished>());
        Assert.Empty(events.OfType<AgentTurnEvent.ToolCompleted>());
    }

    private sealed class Tools(bool fail = false) : IAgentToolFactory
    {
        public IReadOnlyList<AIFunction> Create(Func<AgentToolContext> context) =>
            [AIFunctionFactory.Create(async () =>
            {
                await Task.Yield();
                if (fail) { throw new IOException("Tool failed after starting"); }
                var value = context();
                return value.ScopeId + "/" + value.RunId + "/" + value.CallId;
            }, "lookup")];
    }

    [Fact]
    public async Task RepeatedUnavailableCallsStopAtTheModelCallBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        var calls = 0;
        var client = new StubChatClient((_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("unknown-" + Interlocked.Increment(ref calls), "unknown", new Dictionary<string, object?>())]))));
        await using var brain = await AgentFacts.Brain(client, ct, services => services.AddSingleton<IAgentToolFactory>(new Tools()));
        var agent = brain.Get<IAgent>(AgentFacts.Key("main"));
        await agent.Configure(new() { MaxModelCalls = 2 }, 0);
        var events = await AgentFacts.Collect(agent, AgentTurn.Text("ask", tools: ["lookup"]), ct);
        Assert.Contains("budget", Assert.Single(events.OfType<AgentTurnEvent.Failed>()).Message, StringComparison.Ordinal);
        Assert.Equal(2, calls);
        Assert.Equal(2, events.OfType<AgentTurnEvent.ToolStarted>().Count());
        Assert.Equal(2, events.OfType<AgentTurnEvent.ToolCompleted>().Count());
        Assert.All(events.OfType<AgentTurnEvent.ToolCompleted>(), item => Assert.Contains("tool_unavailable", item.Result, StringComparison.Ordinal));
        Assert.Empty(events.OfType<AgentTurnEvent.Finished>());
    }

    [Fact]
    public async Task SelectedContextsReachTheModelBeforeTheMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = StubChatClient.Replying("done");
        await using var brain = await AgentFacts.Brain(client, ct);
        await brain.Get<IAgentContext>(AgentFacts.Key("main") + "/context").Set(new("capabilities", "Available: invoices", []));
        await brain.Get<IAgentContext>(AgentFacts.Key("other") + "/context").Set(new("unselected", "must not appear", []));
        var agent = brain.Get<IAgent>(AgentFacts.Key("main"));
        await agent.Configure(new() { Contexts = [AgentFacts.Key("main") + "/context"] }, 0);
        await AgentFacts.Collect(agent, AgentTurn.Text("ask"), ct);
        var messages = Assert.Single(client.Requests);
        Assert.Equal([ChatRole.System, ChatRole.User], messages.Select(message => message.Role));
        Assert.Equal("Available: invoices", messages[0].Text);
    }

    [Fact]
    public async Task AMissingContextIsSkippedAndTheOthersReachTheModel()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = StubChatClient.Replying("done");
        await using var brain = await AgentFacts.Brain(client, ct);
        await brain.Get<IAgentContext>(AgentFacts.Key("main") + "/context").Set(new("capabilities", "Available: invoices", []));
        var agent = brain.Get<IAgent>(AgentFacts.Key("main"));
        // The first key names a context nobody ever wrote: it contributes nothing and the turn goes on.
        await agent.Configure(new() { Contexts = [AgentFacts.Key("nowhere") + "/context", AgentFacts.Key("main") + "/context"] }, 0);
        var events = await AgentFacts.Collect(agent, AgentTurn.Text("ask"), ct);
        Assert.Single(events.OfType<AgentTurnEvent.Finished>());
        var messages = Assert.Single(client.Requests);
        Assert.Equal([ChatRole.System, ChatRole.User], messages.Select(message => message.Role));
        Assert.Equal("Available: invoices", messages[0].Text);
    }

    // A user stamped into one brain cannot read another brain's context: the read throws, the
    // agent logs and skips it, and the model still gets the context it may read.
    [Fact]
    public async Task AContextThatFailsToReadIsSkippedAndTheOthersReachTheModel()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = StubChatClient.Replying("done");
        await using var brain = await AgentFacts.Brain(client, ct);
        await brain.Get<IAgentContext>("elsewhere/agents/x/context").Set(new("secret", "must not appear", []));
        await brain.Get<IAgentContext>(AgentFacts.Key("main") + "/context").Set(new("capabilities", "Available: invoices", []));
        var agent = brain.Get<IAgent>(AgentFacts.Key("main"));
        await agent.Configure(new() { Contexts = ["elsewhere/agents/x/context", AgentFacts.Key("main") + "/context"] }, 0);
        AgentFacts.StampUser();
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => brain.Get<IAgentContext>("elsewhere/agents/x/context").Read());
            var events = await AgentFacts.Collect(agent, AgentTurn.Text("ask"), ct);
            Assert.Single(events.OfType<AgentTurnEvent.Finished>());
        }
        finally { Orleans.Runtime.RequestContext.Clear(); }
        var messages = Assert.Single(client.Requests);
        Assert.Equal([ChatRole.System, ChatRole.User], messages.Select(message => message.Role));
        Assert.Equal("Available: invoices", messages[0].Text);
    }

    [Fact]
    public async Task ATurnReadsTheLatestContext()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = StubChatClient.Replying("done");
        await using var brain = await AgentFacts.Brain(client, ct);
        var context = brain.Get<IAgentContext>(AgentFacts.Key("main") + "/context");
        var agent = brain.Get<IAgent>(AgentFacts.Key("main"));
        await agent.Configure(new() { Contexts = [AgentFacts.Key("main") + "/context"] }, 0);
        await context.Set(new("capabilities", "first", []));
        await AgentFacts.Collect(agent, AgentTurn.Text("ask"), ct);
        await context.Set(new("capabilities", "second", []));
        await AgentFacts.Collect(agent, AgentTurn.Text("again"), ct);
        Assert.Contains(client.Requests[^1], message => message.Role == ChatRole.System && message.Text == "second");
        Assert.DoesNotContain(client.Requests[^1], message => message.Role == ChatRole.System && message.Text == "first");
    }

    [Fact]
    public async Task ContextToolsJoinTheTurnOnlyWhenTheHostRegistersThem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await AgentFacts.Brain(ScriptedClient(), ct, services => services.AddSingleton<IAgentToolFactory>(new Tools()));
        await brain.Get<IAgentContext>(AgentFacts.Key("main") + "/context").Set(new("capabilities", "Available: lookup", ["lookup", "not_registered"]));
        var agent = brain.Get<IAgent>(AgentFacts.Key("main"));
        await agent.Configure(new() { Contexts = [AgentFacts.Key("main") + "/context"] }, 0);
        var events = await AgentFacts.Collect(agent, AgentTurn.Text("ask"), ct);
        Assert.Equal("lookup", Assert.Single(events.OfType<AgentTurnEvent.ToolCompleted>()).Name);
    }

    private static StubChatClient ScriptedClient(string toolName = "lookup") => new((messages, _, _) =>
    {
        var result = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
        return Task.FromResult(new ChatResponse(result is null
            ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("actual-call", toolName, new Dictionary<string, object?>())])
            : new ChatMessage(ChatRole.Assistant, result.Result!.ToString())));
    });
}
