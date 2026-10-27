using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Context;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Modules.AI.Tests.Integration.Agents;

public sealed class AgentToolFacts
{
    [Trait("host", "own")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoverableToolRefusalsRetainTheirResultAndDiagnosticWithoutFailingTheTurn(bool unavailable)
    {
        var ct = TestContext.Current.CancellationToken;
        var request = unavailable
            ? new FunctionCallContent("refused-call", "unknown", new Dictionary<string, object?>())
            : Probe.Call("refused-call", "MissingMethod");
        await using var brain = await Probe.Brain(ScriptedClient(request), ct);
        using var caller = brain.Owner.AsCaller();
        var agent = brain.Get<IAgent>(AgentFacts.Key("main"));
        var events = await AgentFacts.Collect(agent, AgentTurn.Text("ask", contracts: [Probe.Contract]), ct);
        var result = Assert.Single(events.OfType<AgentTurnEvent.ToolCompleted>()).Result;
        using var payload = System.Text.Json.JsonDocument.Parse(result);
        Assert.True(payload.RootElement.GetProperty("isError").GetBoolean());
        var run = Assert.Single((await agent.Read()).Runs);
        Assert.Equal(AgentRunStatus.Completed, run.Status);
        var call = Assert.Single(run.ToolCalls);
        Assert.Equal(result, call.Result);
        Assert.Equal(payload.RootElement.GetProperty("message").GetString(), call.Error);
        Assert.Equal(unavailable ? "tool_unavailable" : "tool_error", call.Code);
        Assert.Single(events.OfType<AgentTurnEvent.Finished>());
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ErrorShapedTextInsideASuccessfulResultIsNotAToolFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Probe.Brain(ScriptedClient(Probe.Call("echo-call", nameof(ILoopProbe.Echo),
            System.Text.Json.JsonSerializer.Serialize(new[] { "{\"isError\":true,\"message\":\"quoted data\"}" }))), ct);
        using var caller = brain.Owner.AsCaller();
        var agent = brain.Get<IAgent>(AgentFacts.Key("main"));
        await AgentFacts.Collect(agent, AgentTurn.Text("ask", contracts: [Probe.Contract]), ct);
        var call = Assert.Single(Assert.Single((await agent.Read()).Runs).ToolCalls);
        Assert.NotNull(call.Result);
        Assert.Null(call.Error);
        Assert.Null(call.Code);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ToolDoesNotExecuteUntilTheAgentHasRecordedItsStart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Probe.Brain(ScriptedClient(Probe.Call("actual-call", nameof(ILoopProbe.SeenByAgent), $"[\"{AgentFacts.Key("main")}\"]")), ct);
        using var caller = brain.Owner.AsCaller();
        var events = await AgentFacts.Collect(brain.Get<IAgent>(AgentFacts.Key("main")), AgentTurn.Text("ask", contracts: [Probe.Contract]), ct);
        Assert.Equal("\"recorded\"", Assert.Single(events.OfType<AgentTurnEvent.ToolCompleted>()).Result);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task EachTurnMarksItsCallsWithItsOwnConversation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Probe.Brain(ScriptedClient(Probe.Call("actual-call", nameof(ILoopProbe.Conversation))), ct);
        using var caller = brain.Owner.AsCaller();
        async Task<string> Run(string name)
        {
            var events = await AgentFacts.Collect(brain.Get<IAgent>(AgentFacts.Key(name)), AgentTurn.Text("ask", runId: "run-" + name, contracts: [Probe.Contract]), ct);
            return Assert.Single(events.OfType<AgentTurnEvent.ToolCompleted>()).Result;
        }
        var results = await Task.WhenAll(Run("alpha"), Run("beta"));
        Assert.Equal($"\"{AgentFacts.Key("alpha")}\"", results[0]);
        Assert.Equal($"\"{AgentFacts.Key("beta")}\"", results[1]);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task DelayedToolFailureNeverFinishesTheRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Probe.Brain(ScriptedClient(Probe.Call("actual-call", nameof(ILoopProbe.Fail))), ct);
        using var caller = brain.Owner.AsCaller();
        var events = await AgentFacts.Collect(brain.Get<IAgent>(AgentFacts.Key("main")), AgentTurn.Text("ask", contracts: [Probe.Contract]), ct);
        Assert.Single(events.OfType<AgentTurnEvent.Failed>());
        Assert.Empty(events.OfType<AgentTurnEvent.Finished>());
        Assert.Empty(events.OfType<AgentTurnEvent.ToolCompleted>());
    }

    [Trait("host", "own")]
    [Fact]
    public async Task RepeatedUnavailableCallsStopAtTheModelCallBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        var calls = 0;
        var client = new StubChatClient((_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("unknown-" + Interlocked.Increment(ref calls), "unknown", new Dictionary<string, object?>())]))));
        await using var brain = await Probe.Brain(client, ct);
        using var caller = brain.Owner.AsCaller();
        var agent = brain.Get<IAgent>(AgentFacts.Key("main"));
        await agent.Configure(new() { MaxModelCalls = 2 }, 0);
        var events = await AgentFacts.Collect(agent, AgentTurn.Text("ask", contracts: [Probe.Contract]), ct);
        Assert.Contains("budget", Assert.Single(events.OfType<AgentTurnEvent.Failed>()).Message, StringComparison.Ordinal);
        Assert.Equal(2, calls);
        Assert.Equal(2, events.OfType<AgentTurnEvent.ToolStarted>().Count());
        Assert.Equal(2, events.OfType<AgentTurnEvent.ToolCompleted>().Count());
        Assert.All(events.OfType<AgentTurnEvent.ToolCompleted>(), item => Assert.Contains("tool_unavailable", item.Result, StringComparison.Ordinal));
        Assert.Empty(events.OfType<AgentTurnEvent.Finished>());
    }

    [Trait("host", "own")]
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

    [Trait("host", "own")]
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

    // A user calling from one brain cannot read another brain's context: the read throws, the
    // agent logs and skips it, and the model still gets the context it may read.
    [Trait("host", "own")]
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
        using (brain.Owner.AsCaller())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => brain.Get<IAgentContext>("elsewhere/agents/x/context").Read());
            var events = await AgentFacts.Collect(agent, AgentTurn.Text("ask"), ct);
            Assert.Single(events.OfType<AgentTurnEvent.Finished>());
        }
        var messages = Assert.Single(client.Requests);
        Assert.Equal([ChatRole.System, ChatRole.User], messages.Select(message => message.Role));
        Assert.Equal("Available: invoices", messages[0].Text);
    }

    [Trait("host", "own")]
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

    [Trait("host", "own")]
    [Fact]
    public async Task ContextContractsJoinTheTurnOnlyWhenTheHostDeploysThem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Probe.Brain(ScriptedClient(Probe.Call("actual-call", nameof(ILoopProbe.Echo), "[\"ping\"]")), ct);
        using var caller = brain.Owner.AsCaller();
        await brain.Get<IAgentContext>(AgentFacts.Key("main") + "/context").Set(new("capabilities", "Available: probe", [Probe.Contract, "IntoChat.OS.Nope.INope"]));
        var agent = brain.Get<IAgent>(AgentFacts.Key("main"));
        await agent.Configure(new() { Contexts = [AgentFacts.Key("main") + "/context"] }, 0);
        var events = await AgentFacts.Collect(agent, AgentTurn.Text("ask"), ct);
        Assert.Equal("\"echo:ping\"", Assert.Single(events.OfType<AgentTurnEvent.ToolCompleted>()).Result);
    }

    private static StubChatClient ScriptedClient(FunctionCallContent call) => new((messages, _, _) =>
    {
        var result = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
        return Task.FromResult(new ChatResponse(result is null
            ? new ChatMessage(ChatRole.Assistant, [call])
            : new ChatMessage(ChatRole.Assistant, result.Result!.ToString())));
    });
}
