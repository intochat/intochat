using System.Runtime.CompilerServices;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Agents.Signals;
using Microsoft.Extensions.AI;

namespace DigitalBrain.Modules.AI.Tests.Agents;

public sealed class AgentCancellationFacts
{
    [Fact]
    public async Task CancellationClosesThePersistedToolAttempt()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = AgentFacts.Scope + "/probes/cancel-tool";
        var entered = LoopProbe.Entered.GetOrAdd(key, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        var client = new StubChatClient((_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [Probe.Call("pending", nameof(ILoopProbe.Wait), key: key)]))));
        await using var brain = await Probe.Brain(client, ct);
        using var caller = brain.Owner.AsCaller();
        var agent = brain.Get<IAgent>(AgentFacts.Key("cancel-tool"));
        await agent.Configure(new() { Contracts = [Probe.Contract] }, 0);
        var response = agent.GetResponse("wait", ct);
        // A turn that ends before the probe is entered fails the fact instead of waiting forever.
        Assert.Same(entered.Task, await Task.WhenAny(entered.Task, response));
        Assert.Equal("pending", Assert.Single((await agent.Read()).ActiveRun!.ToolCalls).CallId);
        await agent.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => response);
        var run = (await agent.Read()).Runs[^1];
        Assert.Equal(AgentRunStatus.Cancelled, run.Status);
        var call = Assert.Single(run.ToolCalls);
        Assert.Equal(("pending", "tool_cancelled", null), (call.CallId, call.Code, call.Result));
        await brain.DeactivateAsync(agent, ct);
        Assert.Equal(call, Assert.Single((await agent.Read()).Runs[^1].ToolCalls));
    }

    [Fact]
    public async Task CancelInterleavesWithInferenceAndDoesNotCommitAnIncompleteTurn()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new WaitingProvider();
        await using var brain = await AgentFacts.Brain(provider.Client, ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("cancel"));
        var response = agent.GetResponse("wait", ct);
        await provider.Entered.Task.WaitAsync(ct);
        Assert.Equal(AgentRunStatus.Running, (await agent.Read()).ActiveRun!.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(agent.Clear);
        await agent.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => response);
        Assert.Empty(await agent.Transcript());
        var state = await agent.Read();
        Assert.Null(state.ActiveRun);
        Assert.Equal(AgentRunStatus.Cancelled, state.Runs[^1].Status);
    }

    [Fact]
    public async Task ProviderFailureIsFailedAndNeverCommitsHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await AgentFacts.Brain(new WaitingProvider(fail: true).Client, ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("failed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.GetResponse("fail", ct));
        Assert.Empty(await agent.Transcript());
        var run = (await agent.Read()).Runs[^1];
        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.False(string.IsNullOrEmpty(run.Error));
    }

    [Fact]
    public async Task DelayedStopCannotCancelTheNextRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new WaitingProvider();
        await using var brain = await AgentFacts.Brain(provider.Client, ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("cancel-specific-run"));
        await using var started = await brain.Observe<TurnStarted>(agent, ct);
        var first = agent.GetResponse("first", ct);
        await provider.Entered.Task.WaitAsync(ct);
        var firstId = (await agent.Read()).ActiveRun!.RunId;
        await agent.CancelRun(firstId);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var second = agent.GetResponse("second", ct);
        var secondId = (await started.NextAsync(turn => turn.RunId != firstId, ct)).RunId;
        Assert.NotEqual(firstId, secondId);
        await agent.CancelRun(firstId);
        Assert.Equal(secondId, (await agent.Read()).ActiveRun!.RunId);
        Assert.False(second.IsCompleted);
        await agent.CancelRun(secondId);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Null((await agent.Read()).ActiveRun);
        Assert.All((await agent.Read()).Runs, run => Assert.Equal(AgentRunStatus.Cancelled, run.Status));
    }

    private sealed class WaitingProvider(bool fail = false)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StubChatClient Client => StubChatClient.StreamingOnly(Stream);

        private async IAsyncEnumerable<ChatResponseUpdate> Stream(IReadOnlyList<ChatMessage> messages, ChatOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new(ChatRole.Assistant, "partial");
            Entered.TrySetResult();
            if (fail) { throw new IOException("Provider unavailable"); }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
