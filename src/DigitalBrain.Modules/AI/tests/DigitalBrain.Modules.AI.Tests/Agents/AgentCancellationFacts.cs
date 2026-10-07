using System.Runtime.CompilerServices;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DigitalBrain.Modules.AI.Tests.Agents;

public sealed class AgentCancellationFacts
{
    [Fact]
    public async Task CancellationClosesThePersistedToolAttempt()
    {
        var ct = TestContext.Current.CancellationToken;
        var tool = new WaitingTool();
        var client = new StubChatClient((_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("pending", "wait", new Dictionary<string, object?>())]))));
        await using var brain = await AgentFacts.Brain(client, ct, services => services.AddSingleton<IAgentToolFactory>(tool));
        var agent = brain.Get<IAgent>(AgentFacts.Key("cancel-tool"));
        await agent.Configure(new() { Tools = ["wait"] }, 0);
        var response = agent.GetResponse("wait", ct);
        await tool.Entered.Task.WaitAsync(ct);
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

    private sealed class WaitingTool : IAgentToolFactory
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<AIFunction> Create(Func<AgentToolContext> context)
        {
            async Task<string> Wait(CancellationToken ct)
            { Entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return "done"; }
            return [AIFunctionFactory.Create(Wait, "wait")];
        }
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
        Assert.Contains("Provider unavailable", run.Error, StringComparison.Ordinal);
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
