using System.Diagnostics;
using System.Runtime.CompilerServices;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Agents.Signals;
using DigitalBrain.Framework;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace DigitalBrain.Modules.AI.Tests.Integration.Agents;

public sealed class AgentStreamingFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task AProviderThatBreaksAfterABurstOfTextFailsTheTurn()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new BurstProvider();
        await using var brain = await AgentFacts.Brain(provider.Client, ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("burst"));
        await using var progress = await brain.Observe<TurnProgressed>(agent, ct);
        var run = AgentFacts.Collect(agent, AgentTurn.Text("hello"), ct);
        // The fake server breaks the connection with a reset, and a reset discards bytes the adapter has not read yet.
        // The provider breaks only once the agent has announced text, so the burst is known to have crossed the wire.
        while ((await progress.NextAsync(ct: ct)).Kind != "text") { }
        provider.Break.TrySetResult();
        var events = await run;
        // Text the adapter read before the connection broke is delivered, and the failure after it.
        Assert.NotEmpty(events.OfType<AgentTurnEvent.Text>());
        Assert.Single(events.OfType<AgentTurnEvent.Failed>());
        Assert.Empty(events.OfType<AgentTurnEvent.Finished>());
    }

    [Trait("host", "own")]
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

    // Each stored progress step is a write of the whole agent and a journal entry, so a reply must
    // not cost one per chunk: progress is stored at most once per interval and always at the end.
    [Trait("host", "own")]
    [Fact]
    public async Task AFiveHundredChunkStreamStoresAndAnnouncesItsProgressABoundedNumberOfTimes()
    {
        var ct = TestContext.Current.CancellationToken;
        string[] chunks = [.. Enumerable.Range(0, 500).Select(i => $"w{i} ")];
        await using var brain = await AgentFacts.Brain(StubChatClient.StreamingOnly((_, _, _) => Chunks(chunks)), ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("story"));
        await using var signals = await brain.Observe<Signal>(agent, ct);
        var before = (await agent.Read()).Version;
        var clock = Stopwatch.StartNew();
        var runId = await agent.Start(AgentTurn.Text("write a story"));
        var seen = await AgentTurnStartFacts.UntilEnd(signals, runId, ct);
        var intervals = (int)(clock.Elapsed / AgentNeuron.ProgressInterval);
        var reply = string.Concat(chunks);
        Assert.Equal(reply, Assert.IsType<TurnCompleted>(seen[^1]).Reply);
        var text = seen.OfType<TurnProgressed>().Where(step => step.Kind == "text").ToArray();
        // In sequence, the text steps are the reply: coalescing loses no character.
        Assert.Equal(reply, string.Concat(text.Select(step => step.Text)));
        Assert.Equal(text.Select(step => step.Sequence).Order(), text.Select(step => step.Sequence));
        // The first words at once, one step per elapsed interval, the rest at completion.
        Assert.InRange(text.Length, 2, intervals + 2);
        var state = await agent.Read();
        // Admission, model selection, the first words, one write per elapsed interval, completion.
        Assert.InRange(state.Version - before, 4, intervals + 4);
        Assert.Equal(reply, Assert.Single(state.Runs).Reply);
    }

    // A consumer projects progress by re-reading the agent (the Assistant transcript does). Text that
    // arrived before the provider stalled is stored and announced within an interval, not at the next chunk.
    [Trait("host", "own")]
    [Fact]
    public async Task TextThatArrivedBeforeTheProviderStalledIsReadableBeforeTheTurnEnds()
    {
        var ct = TestContext.Current.CancellationToken;
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var brain = await AgentFacts.Brain(StubChatClient.StreamingOnly((_, _, token) => Stalling(resume.Task, token)), ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("stalled"));
        await using var signals = await brain.Observe<Signal>(agent, ct);
        var runId = await agent.Start(AgentTurn.Text("tell me a story"));
        try
        {
            var shown = "";
            while (shown != "Once upon a time")
            {
                shown += ((TurnProgressed)await signals.NextAsync(signal => signal is TurnProgressed { Kind: "text" } step && step.RunId == runId, ct)).Text;
            }
            var midway = await agent.Read();
            Assert.Equal(("Once upon a time", AgentRunStatus.Running), (midway.ActiveRun?.Reply, midway.ActiveRun?.Status));
        }
        // A stalled provider left waiting would hold the host open past the failure.
        finally { resume.TrySetResult(); }
        var completed = (TurnCompleted)await signals.NextAsync(signal => signal is TurnCompleted done && done.RunId == runId, ct);
        Assert.Equal("Once upon a time, the end.", completed.Reply);
        Assert.Equal("Once upon a time, the end.", Assert.Single((await agent.Read()).Runs).Reply);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Chunks(string[] chunks)
    {
        foreach (var chunk in chunks) { yield return new(ChatRole.Assistant, chunk); }
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Stalling(Task resume, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new(ChatRole.Assistant, "Once ");
        yield return new(ChatRole.Assistant, "upon ");
        yield return new(ChatRole.Assistant, "a time");
        await resume.WaitAsync(cancellationToken);
        yield return new(ChatRole.Assistant, ", the end.");
    }

    private sealed class BurstProvider
    {
        public TaskCompletionSource Break { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StubChatClient Client => StubChatClient.StreamingOnly(Stream);

        private async IAsyncEnumerable<ChatResponseUpdate> Stream(IReadOnlyList<ChatMessage> messages, ChatOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var i = 0; i < 64; i++) { yield return new(ChatRole.Assistant, "x"); }
            await Break.Task.WaitAsync(cancellationToken);
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
