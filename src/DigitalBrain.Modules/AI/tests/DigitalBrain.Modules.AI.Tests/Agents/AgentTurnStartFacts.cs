using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Agents.Signals;
using DigitalBrain.Framework;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.AI.Tests.Agents;

// A script cannot hold a stream across the edge, so a turn starts with a run id and reports
// itself as signals the script subscribes to; the agent keeps the transcript.
public sealed class AgentTurnStartFacts
{
    [Fact]
    public async Task StartAnswersARunIdAndPublishesTheTextThenCompletion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await AgentFacts.Brain(StubChatClient.Replying("hello there"), ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("main"));
        using var signals = brain.SiloServices.GetRequiredService<LocalSignalHub>().Subscribe<Signal>();
        var runId = await agent.Start(AgentTurn.Text("hi", instructions: "Be brief."));
        var seen = await UntilEnd(signals, runId, ct);
        Assert.Equal("hi", Assert.IsType<TurnStarted>(seen[0]).UserText);
        var completed = Assert.IsType<TurnCompleted>(seen[^1]);
        Assert.Equal("hello there", completed.Reply);
        var text = Assert.Single(seen.OfType<TurnProgressed>(), step => step.Kind == "text");
        Assert.Equal(("hello there", runId, 1), (text.Text, text.RunId, text.Sequence));
        var state = await agent.Read();
        Assert.Null(state.ActiveRun);
        Assert.Equal(("hi", "hello there"), (Assert.Single(state.Runs).UserText, state.Runs[0].Reply));
        Assert.Equal(2, (await agent.Transcript()).Length);
    }

    [Fact]
    public async Task AFailingTurnPublishesFailedAndKeepsTheFailureInTheRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await AgentFacts.Brain(StubChatClient.Failing("provider down"), ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("main"));
        using var signals = brain.SiloServices.GetRequiredService<LocalSignalHub>().Subscribe<Signal>();
        var runId = await agent.Start(AgentTurn.Text("hi"));
        var failed = Assert.IsType<TurnFailed>((await UntilEnd(signals, runId, ct))[^1]);
        Assert.Contains("provider down", failed.Error, StringComparison.Ordinal);
        Assert.False(failed.Cancelled);
        var run = Assert.Single((await agent.Read()).Runs);
        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Contains("provider down", run.Error, StringComparison.Ordinal);
        Assert.Empty(await agent.Transcript());
    }

    [Fact]
    public async Task StartReplaysACompletedRunIdWithTheSameSignalsAndRefusesAnotherText()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = StubChatClient.Replying("once");
        await using var brain = await AgentFacts.Brain(client, ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("main"));
        using var signals = brain.SiloServices.GetRequiredService<LocalSignalHub>().Subscribe<Signal>();
        Assert.Equal("run-1", await agent.Start(AgentTurn.Text("hi", runId: "run-1")));
        await UntilEnd(signals, "run-1", ct);
        Assert.Equal("run-1", await agent.Start(AgentTurn.Text("hi", runId: "run-1")));
        var replay = await UntilEnd(signals, "run-1", ct);
        Assert.IsType<TurnStarted>(replay[0]);
        Assert.Equal("once", Assert.IsType<TurnCompleted>(replay[^1]).Reply);
        Assert.Single(client.Requests);
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Start(AgentTurn.Text("other", runId: "run-1")));
        Assert.Single((await agent.Read()).Runs);
    }

    [Fact]
    public async Task StartingATurnIsNotHeldUpByAnotherAgentsRunningTurn()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new StubChatClient(async (_, _, _) => { entered.TrySetResult(); await release.Task; return new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")); });
        await using var brain = await AgentFacts.Brain(client, ct);
        try
        {
            await brain.Get<IAgent>(AgentFacts.Key("one")).Start(AgentTurn.Text("first"));
            await entered.Task.WaitAsync(ct);
            var second = brain.Get<IAgent>(AgentFacts.Key("two")).Start(AgentTurn.Text("second"));
            Assert.Same(second, await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5), ct)));
            Assert.NotEmpty(await second);
        }
        finally { release.TrySetResult(); }
    }

    // A second message while the agent's turn still runs is refused, never interleaved.
    [Fact]
    public async Task ASecondTurnIsRefusedWhileOneRuns()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new StubChatClient(async (_, _, _) => { await release.Task; return new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")); });
        await using var brain = await AgentFacts.Brain(client, ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("main"));
        try
        {
            await agent.Start(AgentTurn.Text("first"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Start(AgentTurn.Text("second"))).WaitAsync(TimeSpan.FromSeconds(5), ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => AgentFacts.Collect(agent, AgentTurn.Text("third"), ct)).WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal("first", (await agent.Read()).ActiveRun!.UserText);
        }
        finally { release.TrySetResult(); }
    }

    // Every signal of the run, from its start to its completion or failure.
    private static async Task<List<Signal>> UntilEnd(LocalSignalHub.Subscription<Signal> signals, string runId, CancellationToken ct)
    {
        var seen = new List<Signal>();
        await foreach (var signal in signals.Reader.ReadAllAsync(ct))
        {
            switch (signal)
            {
                case TurnStarted started when started.RunId == runId: seen.Add(signal); break;
                case TurnProgressed progressed when progressed.RunId == runId: seen.Add(signal); break;
                case TurnCompleted completed when completed.RunId == runId: seen.Add(signal); return seen;
                case TurnFailed failed when failed.RunId == runId: seen.Add(signal); return seen;
            }
        }
        return seen;
    }
}
