using System.Runtime.CompilerServices;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Agents.Signals;
using DigitalBrain.Framework;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;
using Xunit;

namespace DigitalBrain.Modules.AI.Tests.Agents;

public sealed class AgentStreamingFacts
{
    [Fact]
    public async Task ProviderFailureIsDeliveredEvenAfterABurstOfText()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new BurstProvider();
        await using var brain = await AgentFacts.Brain(provider.Client, ct);
        var events = await AgentFacts.Collect(brain.Get<IAgent>(AgentFacts.Key("burst")), AgentTurn.Text("hello"), ct);
        Assert.Equal(64, events.OfType<AgentTurnEvent.Text>().Count());
        Assert.Contains("Provider stopped", Assert.Single(events.OfType<AgentTurnEvent.Failed>()).Message, StringComparison.Ordinal);
        Assert.Empty(events.OfType<AgentTurnEvent.Finished>());
    }

    [Fact]
    public async Task AbandonedStreamCancelsTheProviderAndLeavesTheTranscriptUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new WaitingStreamProvider();
        await using var brain = await AgentFacts.Brain(provider.Client, ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("abandoned"));
        await using var failures = await brain.SubscribeAsync<TurnFailed>(agent, ct);
        await using var failed = failures.ReadAllAsync(ct).GetAsyncEnumerator(ct);
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
            var storage = brain.SiloServices.GetRequiredKeyedService<IGrainStorage>(DigitalBrainNames.ClusterGrainStorage);
            await storage.ReadStateAsync("agent", GrainId.Create("agent", AgentFacts.Key("abandoned")), persisted);
            Assert.Equal("partial", persisted.State?.ActiveRun?.Reply);
            Assert.Equal(streaming.Version, persisted.State?.Version);
        }
        await provider.Cancelled.Task.WaitAsync(ct);
        Assert.True(await failed.MoveNextAsync());
        Assert.True(failed.Current.Cancelled);
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
