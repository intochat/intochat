using System.Runtime.CompilerServices;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Agents.Signals;
using DigitalBrain.Framework;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace DigitalBrain.Modules.AI.Tests.Agents;

public sealed class AgentStreamingFacts
{
    [Fact]
    public async Task AProviderThatBreaksAfterABurstOfTextFailsTheTurn()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new BurstProvider();
        await using var brain = await AgentFacts.Brain(provider.Client, ct);
        var events = await AgentFacts.Collect(brain.Get<IAgent>(AgentFacts.Key("burst")), AgentTurn.Text("hello"), ct);
        // Text the adapter read before the connection broke is delivered, and the failure after it.
        Assert.NotEmpty(events.OfType<AgentTurnEvent.Text>());
        Assert.Single(events.OfType<AgentTurnEvent.Failed>());
        Assert.Empty(events.OfType<AgentTurnEvent.Finished>());
    }

    [Fact]
    public async Task AbandonedStreamCancelsTheProviderAndLeavesTheTranscriptUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new WaitingStreamProvider();
        await using var brain = await AgentFacts.Brain(provider.Client, ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("abandoned"));
        await using var failures = await brain.Observe<TurnFailed>(agent, ct);
        var before = await agent.Read();
        long streamingVersion;
        await using (var stream = agent.Run(AgentTurn.Text("hello"), ct).GetAsyncEnumerator(ct))
        {
            while (await stream.MoveNextAsync() && stream.Current is not AgentTurnEvent.Text) { }
            Assert.Equal("partial", Assert.IsType<AgentTurnEvent.Text>(stream.Current).Content);
            var streaming = await agent.Read();
            Assert.Equal("partial", streaming.ActiveRun?.Reply);
            Assert.True(streaming.Version > before.Version);
            Assert.Equal(before.Revision, streaming.Revision);
            streamingVersion = streaming.Version;
            var persisted = new GrainState<AgentStorage>();
            var storage = brain.Services.GetRequiredKeyedService<IGrainStorage>(DigitalBrainNames.ClusterGrainStorage);
            await storage.ReadStateAsync("agent", GrainId.Create("agent", AgentFacts.Key("abandoned")), persisted);
            Assert.Equal("partial", persisted.State?.ActiveRun?.Reply);
            Assert.Equal(streaming.Version, persisted.State?.Version);
        }
        await provider.Cancelled.Task.WaitAsync(ct);
        Assert.True((await failures.NextAsync(ct: ct)).Cancelled);
        var state = await agent.Read();
        Assert.Null(state.ActiveRun);
        Assert.Equal(AgentRunStatus.Cancelled, Assert.Single(state.Runs).Status);
        Assert.Equal("partial", Assert.Single(state.Runs).Reply);
        Assert.True(state.Version > streamingVersion);
        Assert.Empty(await agent.Transcript());
    }

    private sealed class BurstProvider
    {
        public StubChatClient Client => StubChatClient.StreamingOnly(Stream);

        private static async IAsyncEnumerable<ChatResponseUpdate> Stream(IReadOnlyList<ChatMessage> messages, ChatOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var i = 0; i < 64; i++) { yield return new(ChatRole.Assistant, "x"); }
            await Task.CompletedTask;
            throw new IOException("Provider stopped");
        }
    }

    private sealed class WaitingStreamProvider
    {
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StubChatClient Client => StubChatClient.StreamingOnly(Stream);

        private async IAsyncEnumerable<ChatResponseUpdate> Stream(IReadOnlyList<ChatMessage> messages, ChatOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try { yield return new(ChatRole.Assistant, "partial"); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            finally { Cancelled.TrySetResult(); }
        }
    }
}
