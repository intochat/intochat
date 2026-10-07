using DigitalBrain.AI;
using DigitalBrain.AI.Agents;
using Microsoft.Extensions.AI;
using Xunit;

namespace DigitalBrain.Modules.AI.Tests.Agents;

public sealed class AgentHistoryFacts
{
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

    // Replies with every user and assistant text joined by '|', so the fact sees what the model was given.
    private static StubChatClient HistoryClient() => new((messages, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
        string.Join("|", messages.Where(message => message.Role != ChatRole.System).Select(message => message.Text))))));
}
