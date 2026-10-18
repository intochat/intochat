using DigitalBrain.AI;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Inference;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DigitalBrain.Modules.AI.Tests.Integration.Agents;

public sealed class AgentHistoryFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task ConfigurationAndTranscriptSurviveReactivationAndClearIsExplicit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await AgentFacts.Brain(HistoryClient(), ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("durable"));
        await agent.Configure(new() { DisplayName = "Writer", Instructions = "Write carefully" }, 0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Configure(new(), 0));
        await agent.GetResponse("remember", ct);
        await brain.DeactivateAsync(agent, ct);
        Assert.Equal("Writer", (await agent.Read()).Definition.DisplayName);
        var transcript = await agent.Transcript();
        Assert.Equal(["user", "assistant"], transcript.Select(message => message.Role));
        Assert.Equal("remember", Assert.IsType<AiText>(Assert.Single(transcript[0].Content)).Text);
        Assert.Equal("remember", Assert.IsType<AiText>(Assert.Single(transcript[1].Content)).Text);
        Assert.Equal(AgentRunStatus.Completed, Assert.Single((await agent.Read()).Runs).Status);
        await agent.Clear();
        Assert.Empty(await agent.Transcript());
        Assert.Empty((await agent.Read()).Runs);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task StreamingCommitsTheTranscriptOnlyAfterCompletion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await AgentFacts.Brain(HistoryClient(), ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("stream"));
        var text = "";
        await foreach (var delta in agent.GetResponseStream("hello", ct)) { text += delta; }
        Assert.Equal("hello", text);
        Assert.Equal(2, (await agent.Transcript()).Length);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task SubsequentQuestionsIncludeTheCommittedTranscriptAndOtherAgentsStayIsolated()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await AgentFacts.Brain(HistoryClient(), ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("history"));
        Assert.Equal("first", await agent.GetResponse("first", ct));
        Assert.Equal("first|first|second", (await agent.Ask("second", ct)).Text);
        Assert.Equal("other", await brain.Get<IAgent>(AgentFacts.Key("separate")).GetResponse("other", ct));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ReplayRequiresTheSameTextAndReturnsTheStoredReply()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = StubChatClient.Replying("stored");
        await using var brain = await AgentFacts.Brain(client, ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("replay"));
        await AgentFacts.Collect(agent, AgentTurn.Text("hello", runId: "run-1"), ct);
        var replay = await AgentFacts.Collect(agent, AgentTurn.Text("hello", runId: "run-1"), ct);
        Assert.Equal("stored", Assert.Single(replay.OfType<AgentTurnEvent.Text>()).Content);
        Assert.Single(replay.OfType<AgentTurnEvent.Finished>());
        Assert.Single(client.Requests);
        await Assert.ThrowsAsync<InvalidOperationException>(() => AgentFacts.Collect(agent, AgentTurn.Text("different", runId: "run-1"), ct));
        Assert.Single((await agent.Read()).Runs);
        Assert.Equal(2, (await agent.Transcript()).Length);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task TheTranscriptFoldsOverflowIntoTheSummary()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = StubChatClient.Replying("ok");
        await using var brain = await AgentFacts.Brain(client, ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("bounded"));
        await agent.Configure(new() { MaxHistoryMessages = 4 }, 0);
        for (var i = 0; i < 4; i++) { await agent.GetResponse("message " + i, ct); }
        Assert.Equal(4, (await agent.Transcript()).Length);
        Assert.Contains("message 0", (await agent.Read()).Summary, StringComparison.Ordinal);
        Assert.Contains(client.Requests[^1], message => message.Role == ChatRole.System && message.Text.Contains("message 0", StringComparison.Ordinal));
    }

    // A tool turn is four messages; a three-message budget must cut it whole, never between the
    // call and its result, or providers reject every later turn.
    [Trait("host", "own")]
    [Fact]
    public async Task TrimmingNeverSplitsAToolCallFromItsResult()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new StubChatClient((messages, _, _) =>
        {
            var asked = messages.Last(message => message.Role == ChatRole.User).Text;
            var answered = messages.SkipWhile(message => message.Role != ChatRole.User || message.Text != asked).Any(message => message.Role == ChatRole.Tool);
            return Task.FromResult(new ChatResponse(asked == "use the tool" && !answered
                ? new ChatMessage(ChatRole.Assistant, [Probe.Call("call-1", nameof(ILoopProbe.Echo), "[\"found\"]")])
                : new ChatMessage(ChatRole.Assistant, "done: " + asked)));
        });
        await using var brain = await Probe.Brain(client, ct);
        using var caller = brain.Owner.AsCaller();
        var agent = brain.Get<IAgent>(AgentFacts.Key("trimmed"));
        await agent.Configure(new() { MaxHistoryMessages = 3, Contracts = [Probe.Contract] }, 0);
        await agent.GetResponse("use the tool", ct);
        await agent.GetResponse("second", ct);
        await agent.GetResponse("third", ct);
        Assert.All(client.Requests.Skip(2), Paired);
        Assert.Equal(ChatRole.User, (await agent.Transcript()).Select(message => new ChatRole(message.Role)).First());
        Assert.Contains("use the tool", (await agent.Read()).Summary, StringComparison.Ordinal);
    }

    // A capability listing or a table query can return far more than a model needs on later turns:
    // the turn that called it sees all of it, the committed transcript keeps a bounded preview.
    [Trait("host", "own")]
    [Fact]
    public async Task ALargeToolResultIsCommittedAsABoundedPreviewAndLaterRequestsStayWithinTheBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new StubChatClient((messages, _, _) =>
        {
            var asked = messages.Last(message => message.Role == ChatRole.User).Text;
            var answered = messages.SkipWhile(message => message.Role != ChatRole.User || message.Text != asked).Any(message => message.Role == ChatRole.Tool);
            return Task.FromResult(new ChatResponse(asked == "dump everything" && !answered
                ? new ChatMessage(ChatRole.Assistant, [Probe.Call("call-1", nameof(ILoopProbe.Dump))])
                : new ChatMessage(ChatRole.Assistant, "done: " + asked)));
        });
        await using var brain = await Probe.Brain(client, ct);
        using var caller = brain.Owner.AsCaller();
        var agent = brain.Get<IAgent>(AgentFacts.Key("large-result"));
        await agent.Configure(new() { Contracts = [Probe.Contract] }, 0);
        await agent.GetResponse("dump everything", ct);
        Assert.True(Size(client.Requests[1]) > LoopProbe.DumpCharacters);
        await agent.GetResponse("second", ct);
        await agent.GetResponse("third", ct);

        var stored = Assert.Single((await agent.Transcript()).SelectMany(message => message.Content).OfType<AiToolResult>());
        Assert.True(stored.ResultJson.Length < AgentNeuron.MaxStoredToolResultCharacters + 200);
        using (var preview = System.Text.Json.JsonDocument.Parse(stored.ResultJson))
        {
            Assert.True(preview.RootElement.GetProperty("truncated").GetBoolean());
            Assert.Equal(LoopProbe.DumpCharacters + 2, preview.RootElement.GetProperty("characters").GetInt32());
        }
        Assert.All(client.Requests.Skip(2), request =>
        {
            Assert.True(Size(request) <= AgentNeuron.MaxHistoryCharacters);
            Paired(request);
        });
    }

    // Count alone does not bound a transcript: a few long turns fold into the summary on a turn boundary.
    [Trait("host", "own")]
    [Fact]
    public async Task HistoryBeyondTheCharacterBudgetFoldsIntoTheSummary()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = StubChatClient.Replying("ok");
        await using var brain = await AgentFacts.Brain(client, ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("long-turns"));
        for (var i = 0; i < 6; i++) { await agent.GetResponse($"turn {i} " + new string('x', 30_000), ct); }
        Assert.True(Size(client.Requests[^1]) <= AgentNeuron.MaxHistoryCharacters);
        Assert.Equal(ChatRole.User, client.Requests[^1].First(message => message.Role != ChatRole.System).Role);
        Assert.Contains("turn 0", (await agent.Read()).Summary, StringComparison.Ordinal);
        var transcript = await agent.Transcript();
        Assert.Equal("user", transcript[0].Role);
        Assert.True(transcript.Length < 12);
        Assert.Contains(client.Requests[^1], message => message.Role == ChatRole.System && message.Text.Contains("turn 0", StringComparison.Ordinal));
    }

    private static void Paired(ChatMessage[] request)
    {
        var history = request.Where(message => message.Role != ChatRole.System).ToArray();
        Assert.Equal(ChatRole.User, history[0].Role);
        var calls = history.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Select(call => call.CallId).ToArray();
        var results = history.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Select(result => result.CallId).ToArray();
        Assert.Equal(calls.Order(), results.Order());
    }

    // The text a request carries outside its system messages, measured as the agent measures history.
    private static long Size(ChatMessage[] request) => request.Where(message => message.Role != ChatRole.System)
        .SelectMany(message => message.Contents).Sum(content => (long)(content switch
        {
            TextContent text => text.Text.Length,
            FunctionCallContent call => call.Name.Length + System.Text.Json.JsonSerializer.Serialize(call.Arguments).Length,
            FunctionResultContent result => System.Text.Json.JsonSerializer.Serialize(result.Result).Length,
            _ => 0,
        }));

    // Replies with every user and assistant text joined by '|', so the fact sees what the model was given.
    private static StubChatClient HistoryClient() => new((messages, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
        string.Join("|", messages.Where(message => message.Role != ChatRole.System).Select(message => message.Text))))));
}
