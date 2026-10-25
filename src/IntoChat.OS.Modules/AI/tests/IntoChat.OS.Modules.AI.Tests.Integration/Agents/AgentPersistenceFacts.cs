using System.Collections.Concurrent;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace IntoChat.OS.Modules.AI.Tests.Integration.Agents;

public sealed class AgentPersistenceFacts
{
    [Fact, Trait("host", "own")]
    public async Task PostingReservesMutationUntilItsWriteCommits()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var writes = new HeldWrite("completion", false);
        await using var brain = await AgentFacts.Builder(StubChatClient.Replying("reply"))
            .ConfigureServices(services =>
            {
                var original = services.Last(service => service.ServiceType == typeof(IGrainStorage)
                    && Equals(service.ServiceKey, IntoChatOSNames.LegacyClusterGrainStorage));
                services.AddKeyedSingleton<IGrainStorage>(IntoChatOSNames.LegacyClusterGrainStorage, (services, key) =>
                    new GatedStorage((IGrainStorage)original.KeyedImplementationFactory!(services, key), writes));
            }).StartAsync(ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("posting"));
        var posting = agent.Post("greeting", "first");
        try
        {
            await writes.Entered.Task.WaitAsync(ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Post("greeting", "first"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Clear().WaitAsync(TimeSpan.FromSeconds(5), ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Configure(new(), 0));
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Start(AgentTurn.Text("hello")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Post("second", "second"));
        }
        finally { writes.Release.TrySetResult(); }
        await posting;
        Assert.Equal("post/first", Assert.Single((await agent.Read()).Runs).RunId);
        await agent.Post("second", "second");
        Assert.Equal(2, (await agent.Transcript()).Length);
    }

    [Trait("host", "own")]
    [Theory]
    [InlineData("admission", false)]
    [InlineData("admission", true)]
    [InlineData("stream", false)]
    [InlineData("stream", true)]
    [InlineData("completion", false)]
    [InlineData("completion", true)]
    public async Task InterleavingReadersProjectOnlyCommittedVersions(string phase, bool fail)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var writes = new HeldWrite(phase, fail);
        await using var brain = await AgentFacts.Builder(StubChatClient.Replying("reply")).WithModule<FlutterModule>()
            // Storage faults are in-process: the agent's writes are held or failed under the kernel's wrapper.
            .ConfigureServices(services =>
            {
                var original = services.Last(service => service.ServiceType == typeof(IGrainStorage)
                    && Equals(service.ServiceKey, IntoChatOSNames.LegacyClusterGrainStorage));
                services.AddKeyedSingleton<IGrainStorage>(IntoChatOSNames.LegacyClusterGrainStorage, (services, key) =>
                    new GatedStorage((IGrainStorage)original.KeyedImplementationFactory!(services, key), writes));
            }).StartAsync(ct);
        var source = AgentFacts.Key("committed");
        var agent = brain.Get<IAgent>(source);
        var chat = brain.Get<IChat>(AgentFacts.Key("projection"));
        var running = AgentFacts.Collect(agent, AgentTurn.Text("hello", runId: "held-run"), ct);
        Task<AgentState> reading;
        Task<AiMessage[]> transcript;
        try
        {
            await writes.Entered.Task.WaitAsync(ct);
            reading = agent.Read();
            transcript = agent.Transcript();
            // A completion being written is not yet a replayable result either.
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Start(AgentTurn.Text("hello", runId: "held-run")));
            Assert.False(reading.IsCompleted);
            Assert.False(transcript.IsCompleted);
        }
        finally { writes.Release.TrySetResult(); }

        var observed = await reading;
        var committed = writes.Commits[observed.Version];
        Same(committed.ActiveRun, observed.ActiveRun);
        Same(committed.Runs, observed.Runs);
        if (fail) { Assert.Empty(await transcript); }
        else { Assert.Contains((await transcript).Length, new[] { 0, 2 }); }
        await chat.Project(Projection(source, observed));
        if (fail) { Assert.NotNull(await Record.ExceptionAsync(async () => await running)); }
        else { await running; }
        var terminal = await agent.Read();
        Assert.True(terminal.Version >= observed.Version);
        if (terminal.Version == observed.Version) { Same(terminal.Runs, observed.Runs); }
        Assert.Null(terminal.ActiveRun);
        Assert.Equal(fail ? AgentRunStatus.Failed : AgentRunStatus.Completed, Assert.Single(terminal.Runs).Status);
        await chat.Project(Projection(source, terminal));
        Assert.Equal(fail ? "failed" : "completed", Assert.Single((await chat.Read()).Projection!.Messages).Status);

        await brain.DeactivateAsync(agent, ct);
        var restored = await agent.Read();
        Assert.Equal(terminal.Version, restored.Version);
        Same(terminal.Runs, restored.Runs);
        Assert.Equal(fail ? 0 : 2, (await agent.Transcript()).Length);
    }

    // Runs hold arrays, which records compare by reference: equal runs serialize alike.
    private static void Same<T>(T expected, T actual)
        => Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected), System.Text.Json.JsonSerializer.Serialize(actual));

    private static ChatProjection Projection(string source, AgentState state)
        => new(source, 0, state.Version, [.. state.Runs.Concat(state.ActiveRun is { } active ? [active] : Array.Empty<AgentRun>())
            .Select(run => new ChatEntry(run.RunId, IntoChat.OS.Modules.Flutter.Chat.ChatRole.Assistant, run.Reply ?? "")
            { Status = run.Status.ToString().ToLowerInvariant() })]);

    private sealed class HeldWrite(string phase, bool fail)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentDictionary<long, AgentStorage> Commits { get; } = new() { [0] = new() };
        private bool _used;

        public async Task Before(AgentStorage next)
        {
            var matches = phase switch
            {
                "admission" => next.ActiveRun is not null,
                "stream" => next.ActiveRun?.Reply is not null,
                _ => next.Runs.Length > 0,
            };
            if (_used || !matches) { return; }
            _used = true;
            Entered.TrySetResult();
            await Release.Task;
            if (fail) { throw new IOException("Injected single write failure."); }
        }
    }

    private sealed class GatedStorage(IGrainStorage inner, HeldWrite writes) : IGrainStorage
    {
        public Task ReadStateAsync<T>(string name, GrainId id, IGrainState<T> state) => inner.ReadStateAsync(name, id, state);
        public Task ClearStateAsync<T>(string name, GrainId id, IGrainState<T> state) => inner.ClearStateAsync(name, id, state);

        public async Task WriteStateAsync<T>(string name, GrainId id, IGrainState<T> state)
        {
            if (name == "agent") { await writes.Before((AgentStorage)(object)state.State!); }
            await inner.WriteStateAsync(name, id, state);
            if (name == "agent")
            {
                var committed = (AgentStorage)(object)state.State!;
                writes.Commits[committed.Version] = committed;
            }
        }
    }
}
