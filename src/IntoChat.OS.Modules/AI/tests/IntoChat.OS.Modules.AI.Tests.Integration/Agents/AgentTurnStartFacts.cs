using System.Text.Json;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Storage;

namespace IntoChat.OS.Modules.AI.Tests.Integration.Agents;

// A script cannot hold a stream across the edge, so a turn starts with a run id and reports
// itself as signals the script subscribes to; the agent keeps the transcript.
public sealed class AgentTurnStartFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task StartAnswersARunIdAndPublishesTheTextThenCompletion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await AgentFacts.Brain(StubChatClient.Replying("hello there"), ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("main"));
        await using var signals = await brain.Observe<Signal>(agent, ct);
        var runId = await agent.Start(AgentTurn.Text("hi", instructions: "Be brief."));
        var seen = await UntilEnd(signals, runId, ct);
        Assert.Equal("hi", Assert.IsType<TurnStarted>(seen[0]).UserText);
        var completed = Assert.IsType<TurnCompleted>(seen[^1]);
        Assert.Equal("hello there", completed.Reply);
        var text = Assert.Single(seen.OfType<TurnProgressed>(), step => step.Kind == "text");
        Assert.Equal(("hello there", runId, 1), (text.Text, text.RunId, text.Sequence));
        var state = await agent.Read();
        var startedVersion = SignalVersion(seen[0]);
        Assert.True(startedVersion > 0);
        Assert.True(SignalVersion(text) > startedVersion);
        Assert.Equal(state.Version, SignalVersion(completed));
        Assert.All(seen, signal => Assert.InRange(SignalVersion(signal), startedVersion, state.Version));
        Assert.Null(state.ActiveRun);
        Assert.Equal(("hi", "hello there"), (Assert.Single(state.Runs).UserText, state.Runs[0].Reply));
        Assert.Equal(2, (await agent.Transcript()).Length);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AFailingTurnPublishesFailedAndKeepsTheFailureInTheRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await AgentFacts.Brain(StubChatClient.Failing("provider down"), ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("main"));
        await using var signals = await brain.Observe<Signal>(agent, ct);
        var runId = await agent.Start(AgentTurn.Text("hi"));
        var failed = Assert.IsType<TurnFailed>((await UntilEnd(signals, runId, ct))[^1]);
        Assert.Contains("provider down", failed.Error, StringComparison.Ordinal);
        Assert.False(failed.Cancelled);
        var state = await agent.Read();
        Assert.Equal(state.Version, SignalVersion(failed));
        var run = Assert.Single(state.Runs);
        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Contains("provider down", run.Error, StringComparison.Ordinal);
        Assert.Empty(await agent.Transcript());
    }

    [Trait("host", "own")]
    [Fact]
    public async Task StartReplaysACompletedRunIdWithTheSameSignalsAndRefusesAnotherText()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = StubChatClient.Replying("once");
        await using var brain = await AgentFacts.Brain(client, ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("main"));
        await using var signals = await brain.Observe<Signal>(agent, ct);
        Assert.Equal("run-1", await agent.Start(AgentTurn.Text("hi", runId: "run-1")));
        await UntilEnd(signals, "run-1", ct);
        Assert.Equal("run-1", await agent.Start(AgentTurn.Text("hi", runId: "run-1")));
        var replay = await UntilEnd(signals, "run-1", ct);
        var version = (await agent.Read()).Version;
        Assert.All(replay, signal => Assert.Equal(version, SignalVersion(signal)));
        Assert.IsType<TurnStarted>(replay[0]);
        Assert.Equal("once", Assert.IsType<TurnCompleted>(replay[^1]).Reply);
        Assert.Single(client.Requests);
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Start(AgentTurn.Text("other", runId: "run-1")));
        Assert.Single((await agent.Read()).Runs);
    }

    [Trait("host", "own")]
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
            Assert.NotEmpty(await second.WaitAsync(TimeSpan.FromSeconds(5), ct));
        }
        finally { release.TrySetResult(); }
    }

    private static long SignalVersion(Signal signal)
    {
        var payload = JsonSerializer.SerializeToElement(signal, signal.GetType());
        Assert.True(payload.TryGetProperty("Version", out var version), "Lifecycle signals must identify their persisted conversation version.");
        return version.GetInt64();
    }

    // A second message while the agent's turn still runs is refused, never interleaved.
    [Trait("host", "own")]
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

    // Cancel can arrive after Start recorded the run and before its driver picked it up; the
    // driver then ends the run as cancelled instead of answering it.
    [Trait("host", "own")]
    [Fact]
    public async Task CancelBeforeTheDriverStartsEndsTheStartedRunCancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        var writes = new AgentWrites();
        await using var brain = await Brain(StubChatClient.Replying("never"), writes, ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("main"));
        await using var signals = await brain.Observe<Signal>(agent, ct);
        var held = writes.HoldNext();
        var start = agent.Start(AgentTurn.Text("hi", runId: "run-1"));
        await held.WaitAsync(ct);
        await agent.Cancel();
        writes.Release();
        Assert.Equal("run-1", await start);
        var failed = Assert.IsType<TurnFailed>((await UntilEnd(signals, "run-1", ct))[^1]);
        Assert.True(failed.Cancelled);
        var state = await agent.Read();
        Assert.Null(state.ActiveRun);
        Assert.Equal(AgentRunStatus.Cancelled, Assert.Single(state.Runs).Status);
        Assert.Empty(await agent.Transcript());
    }

    // A run that outlives its Timeout failed; only a caller's Cancel or an abandoned stream is a cancellation.
    [Trait("host", "own")]
    [Fact]
    public async Task ARunThatOutlivesItsTimeoutFailsRatherThanCancels()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new StubChatClient(async (_, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "never"));
        });
        await using var brain = await AgentFacts.Brain(client, ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("main"));
        await agent.Configure(new() { Timeout = TimeSpan.FromMilliseconds(200) }, 0);
        await using var signals = await brain.Observe<Signal>(agent, ct);
        var runId = await agent.Start(AgentTurn.Text("slow"));
        var failed = Assert.IsType<TurnFailed>((await UntilEnd(signals, runId, ct))[^1]);
        Assert.False(failed.Cancelled);
        Assert.Contains("timeout", failed.Error, StringComparison.Ordinal);
        Assert.Equal(AgentRunStatus.Failed, Assert.Single((await agent.Read()).Runs).Status);
        var streamed = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.GetResponse("slow again", ct));
        Assert.Contains("timeout", streamed.Message, StringComparison.Ordinal);
    }

    // A turn whose outcome cannot be stored still announces its end, and the agent recovers from
    // storage instead of refusing every later operation.
    [Trait("host", "own")]
    [Fact]
    public async Task AStorageFaultAtTheEndOfATurnStillEndsItAndTheAgentRecovers()
    {
        var ct = TestContext.Current.CancellationToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new AgentWrites();
        var client = new StubChatClient(async (messages, _, _) =>
        {
            if (messages[^1].Text == "first") { entered.TrySetResult(); await release.Task; }
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"));
        });
        await using var brain = await Brain(client, writes, ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("main"));
        await using var signals = await brain.Observe<Signal>(agent, ct);
        var runId = await agent.Start(AgentTurn.Text("first"));
        await entered.Task.WaitAsync(ct);
        writes.Fail = true;
        release.TrySetResult();
        var failed = Assert.IsType<TurnFailed>((await UntilEnd(signals, runId, ct))[^1]);
        Assert.False(failed.Cancelled);
        // A projection must re-read even if it already rendered the last committed version:
        // that read reactivates the agent and recovers the uncommitted terminal outcome.
        Assert.Equal(0, SignalVersion(failed));
        writes.Fail = false;
        var state = await agent.Read();
        Assert.Null(state.ActiveRun);
        Assert.Equal(AgentRunStatus.Interrupted, Assert.Single(state.Runs).Status);
        Assert.Empty(await agent.Transcript());
        Assert.Equal("done", await agent.GetResponse("second", ct));
    }

    // Storage faults are in-process: the agent's writes are held or failed under the kernel's wrapper.
    private static Task<ITestBrain> Brain(StubChatClient client, AgentWrites writes, CancellationToken ct)
        => AgentFacts.Builder(client).ConfigureServices(services =>
        {
            var original = services.Last(service => service.ServiceType == typeof(IGrainStorage) && Equals(service.ServiceKey, IntoChatOSNames.LegacyClusterGrainStorage));
            services.AddKeyedSingleton<IGrainStorage>(IntoChatOSNames.LegacyClusterGrainStorage, (services, key) =>
                new AgentWriteStorage((IGrainStorage)original.KeyedImplementationFactory!(services, key), writes));
        }).StartAsync(ct);

    // Controls the agent's own writes: fail them, or hold the next one until released.
    private sealed class AgentWrites
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _held;
        public volatile bool Fail;

        public Task HoldNext()
        {
            _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _held.Task;
        }

        public void Release() => _gate.TrySetResult();

        public async Task Write()
        {
            if (Fail) { throw new IOException("Injected storage failure."); }
            if (Interlocked.Exchange(ref _held, null) is { } held)
            {
                held.TrySetResult();
                await _gate.Task;
            }
        }
    }

    private sealed class AgentWriteStorage(IGrainStorage inner, AgentWrites writes) : IGrainStorage
    {
        public Task ReadStateAsync<T>(string name, GrainId id, IGrainState<T> state) => inner.ReadStateAsync(name, id, state);

        public async Task WriteStateAsync<T>(string name, GrainId id, IGrainState<T> state)
        {
            if (name == "agent") { await writes.Write(); }
            await inner.WriteStateAsync(name, id, state);
        }

        public Task ClearStateAsync<T>(string name, GrainId id, IGrainState<T> state) => inner.ClearStateAsync(name, id, state);
    }

    // Every signal of the run, from its start to its completion or failure.
    internal static async Task<List<Signal>> UntilEnd(SignalProbe<Signal> signals, string runId, CancellationToken ct)
    {
        var seen = new List<Signal>();
        while (true)
        {
            var signal = await signals.NextAsync(ct: ct);
            switch (signal)
            {
                case TurnStarted started when started.RunId == runId: seen.Add(signal); break;
                case TurnProgressed progressed when progressed.RunId == runId: seen.Add(signal); break;
                case TurnCompleted completed when completed.RunId == runId: seen.Add(signal); return seen;
                case TurnFailed failed when failed.RunId == runId: seen.Add(signal); return seen;
            }
        }
    }
}
