using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Signalling;
using DigitalBrain.Testing;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Host.Surface;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.Tests;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.AI.Tests.Integration.Conversations;

public sealed class ConversationNeuronFacts(AiFixture fixture) : IClassFixture<AiFixture>
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task SayAppendsBothSidesAndClosesTheRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await Start(StubChatClient.Replying("ok"), ct);
        using var person = runtime.As(Who.Person);
        var convId = ConversationId.New();
        var id = runtime.Id(IConversation.Type, IConversation.KeyOf(convId));
        var conversation = runtime.Get<IConversation>(id);

        await conversation.Seat("ino", new AgentDefinition { Instructions = "x" });
        await conversation.Say("hello");
        var view = await WaitUntilIdle(conversation, minRuns: 1, ct);

        await runtime.WaitForSignals<ConversationChanged>(id, 3, Within, ct);
        Assert.Equal(["hello", "ok"], view.Entries.OfType<Said>().Select(s => s.Text));
        Assert.Null(view.Active);
        Assert.Equal(AgentRunStatus.Completed, Assert.Single(view.Runs).Status);
    }

    [Fact]
    public async Task ToolStepsAreLoggedAndPaired()
    {
        var ct = TestContext.Current.CancellationToken;
        var callStep = 0;
        var brainName = string.Empty;
        var client = new StubChatClient((messages, options, _) =>
        {
            if (callStep++ == 0)
            {
                var call = new FunctionCallContent("c-1", "agent_context_read", new Dictionary<string, object?> { ["key"] = $"{brainName}/context/test" });
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done with tool")));
        });

        await using var runtime = await Start(client, ct, wireSurface: true);
        brainName = runtime.Brain.Value;
        using var person = runtime.As(Who.Person);
        var convId = ConversationId.New();
        var id = runtime.Id(IConversation.Type, IConversation.KeyOf(convId));
        var conversation = runtime.Get<IConversation>(id);

        await conversation.Seat("ino", new AgentDefinition
        {
            Instructions = "use tools",
            Contracts = [typeof(IAgentContext).FullName!],
        });
        await conversation.Say("read context");
        var view = await WaitUntilIdle(conversation, minRuns: 1, ct);

        var called = Assert.Single(view.Entries.OfType<Called>());
        var returned = Assert.Single(view.Entries.OfType<Returned>());
        Assert.Equal("c-1", EntryIds.CallOf(called.Id));
        Assert.Equal(EntryIds.CallOf(called.Id), EntryIds.CallOf(returned.Id));
        Assert.Equal(called.Tool, returned.Tool);
    }

    [Fact]
    public async Task ASecretSplitAcrossChunksIsNeverStored()
    {
        var ct = TestContext.Current.CancellationToken;
        var firstChunkSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<ChatResponseUpdate> Stream(
            IReadOnlyList<ChatMessage> _,
            ChatOptions? __,
            [EnumeratorCancellation] CancellationToken token)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "key sk_");
            firstChunkSent.TrySetResult();
            await releaseRest.Task.WaitAsync(token);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "live_x9");
            yield return new ChatResponseUpdate(ChatRole.Assistant, ".");
        }

        await using var runtime = await Start(StubChatClient.StreamingOnly(Stream), ct, secret: "sk_live_x9");
        using var person = runtime.As(Who.Person);
        var convId = ConversationId.New();
        var id = runtime.Id(IConversation.Type, IConversation.KeyOf(convId));
        var conversation = runtime.Get<IConversation>(id);

        await conversation.Seat("ino", new AgentDefinition { Instructions = "x" });
        await conversation.Say("reveal");
        await firstChunkSent.Task.WaitAsync(Within, ct);

        var mid = await conversation.Read();
        Assert.DoesNotContain("sk_live", mid.Active?.Reply ?? string.Empty, StringComparison.Ordinal);

        releaseRest.TrySetResult();
        var view = await WaitUntilIdle(conversation, minRuns: 1, ct);

        var allText = AllText(view);
        Assert.DoesNotContain(allText, t => t.Contains("sk_live", StringComparison.Ordinal));
        Assert.Contains(view.Entries.OfType<Said>(), s => s.Text == "key ****.");
    }

    [Fact]
    public async Task StopKeepsThePartialTextAsACancelledRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var partialSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<ChatResponseUpdate> Stream(
            IReadOnlyList<ChatMessage> _,
            ChatOptions? __,
            [EnumeratorCancellation] CancellationToken token)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "partial words");
            partialSent.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }

        await using var runtime = await Start(StubChatClient.StreamingOnly(Stream), ct);
        using var person = runtime.As(Who.Person);
        var convId = ConversationId.New();
        var id = runtime.Id(IConversation.Type, IConversation.KeyOf(convId));
        var conversation = runtime.Get<IConversation>(id);

        await conversation.Seat("ino", new AgentDefinition { Instructions = "x" });
        await conversation.Say("long answer");
        await partialSent.Task.WaitAsync(Within, ct);

        // Wait until partial text is stored in ActiveRun before stopping.
        await WaitUntil(async () => (await conversation.Read()).Active?.Reply == "partial words", ct);
        await conversation.Stop();

        var view = await WaitUntilIdle(conversation, minRuns: 1, ct);
        var run = Assert.Single(view.Runs);
        Assert.Equal(AgentRunStatus.Cancelled, run.Status);
        Assert.Equal("partial words", run.Reply);
    }

    [Fact]
    public async Task RegenerateReplacesTheLatestReply()
    {
        var ct = TestContext.Current.CancellationToken;
        var turn = 0;
        var client = new StubChatClient((_, _, _) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ++turn == 1 ? "first" : "second"))));

        await using var runtime = await Start(client, ct);
        using var person = runtime.As(Who.Person);
        var convId = ConversationId.New();
        var id = runtime.Id(IConversation.Type, IConversation.KeyOf(convId));
        var conversation = runtime.Get<IConversation>(id);

        await conversation.Seat("ino", new AgentDefinition { Instructions = "x" });
        await conversation.Say("hello");
        var first = await WaitUntilIdle(conversation, minRuns: 1, ct);
        var firstRunId = Assert.Single(first.Runs).RunId;

        await conversation.Regenerate(EntryIds.Reply(firstRunId));
        var second = await WaitUntilIdle(conversation, minRuns: 2, ct);

        var said = second.Entries.OfType<Said>().Last();
        Assert.Equal("second", said.Text);
        Assert.Equal(EntryIds.Reply(firstRunId), said.Replaces);
        await Assert.ThrowsAsync<InvalidOperationException>(() => conversation.Regenerate(EntryIds.Reply(firstRunId)));
    }

    [Fact]
    public async Task RegenerateRetriesAFailedRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var turn = 0;
        var client = new StubChatClient((_, _, _) =>
        {
            if (++turn == 1)
            {
                throw new InvalidOperationException("temporary failure");
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "recovered")));
        });

        await using var runtime = await Start(client, ct);
        using var person = runtime.As(Who.Person);
        var convId = ConversationId.New();
        var id = runtime.Id(IConversation.Type, IConversation.KeyOf(convId));
        var conversation = runtime.Get<IConversation>(id);

        await conversation.Seat("ino", new AgentDefinition { Instructions = "x" });
        await conversation.Say("try again");
        var failed = await WaitUntilIdle(conversation, minRuns: 1, ct);
        var failedRun = Assert.Single(failed.Runs);
        Assert.Equal(AgentRunStatus.Failed, failedRun.Status);

        await conversation.Regenerate(EntryIds.Reply(failedRun.RunId));
        var retried = await WaitUntilIdle(conversation, minRuns: 2, ct);
        var reply = retried.Entries.OfType<Said>().Last();
        Assert.Equal("recovered", reply.Text);
        Assert.Equal(EntryIds.Reply(failedRun.RunId), reply.Replaces);
    }

    [Fact]
    public async Task ReadSinceReturnsOnlyNewEntries()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await Start(StubChatClient.Replying("ok"), ct);
        using var person = runtime.As(Who.Person);
        var convId = ConversationId.New();
        var id = runtime.Id(IConversation.Type, IConversation.KeyOf(convId));
        var conversation = runtime.Get<IConversation>(id);

        await conversation.Seat("ino", new AgentDefinition { Instructions = "x" });
        await conversation.Say("one");
        var before = await WaitUntilIdle(conversation, minRuns: 1, ct);

        await conversation.Say("two");
        await WaitUntilIdle(conversation, minRuns: 2, ct);

        var delta = await conversation.Read(before.Revision);
        Assert.NotEmpty(delta.Entries);
        Assert.All(delta.Entries, e => Assert.DoesNotContain(e.Id, before.Entries.Select(x => x.Id)));
    }

    [Fact]
    public async Task AnotherBrainCannotReadOrSay()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await Start(StubChatClient.Replying("ok"), ct);
        var convId = ConversationId.New();
        var id = runtime.Id(IConversation.Type, IConversation.KeyOf(convId));
        var conversation = runtime.Get<IConversation>(id);

        using (runtime.As(Who.Person))
        {
            await conversation.Seat("ino", new AgentDefinition { Instructions = "x" });
        }

        using var other = runtime.Edge.Enter(runtime.Edge.Person("other-person", "other-account", new BrainId("another-brain")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => conversation.Read());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => conversation.Say("hi"));
    }

    [Fact]
    public async Task InviteWaitsForFriends()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await Start(StubChatClient.Replying("ok"), ct);
        using var person = runtime.As(Who.Person);
        var id = runtime.Id(IConversation.Type, IConversation.KeyOf(ConversationId.New()));
        var conversation = runtime.Get<IConversation>(id);

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => conversation.Invite(new AccountId("a2")));
        Assert.Contains("S8", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOldInoConversationIsImportedOnFirstRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var turn = 0;
        var client = new StubChatClient((_, _, _) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ++turn == 1 ? "r1" : "r2"))));

        await using var runtime = await Start(client, ct);
        using var person = runtime.As(Who.Person);
        var convId = ConversationId.New();
        var oldAgentId = runtime.Id("agent", "ino/conversations/" + convId.Value);
        var oldAgent = runtime.Get<IAgent>(oldAgentId);

        await oldAgent.Start(AgentTurn.Text("one"));
        await runtime.WaitForSignals<TurnCompleted>(oldAgentId, 1, Within, ct);
        await oldAgent.Start(AgentTurn.Text("two"));
        await runtime.WaitForSignals<TurnCompleted>(oldAgentId, 2, Within, ct);

        var conversation = runtime.Get<IConversation>(runtime.Id(IConversation.Type, IConversation.KeyOf(convId)));
        var view = await conversation.Read();

        Assert.Equal(["one", "r1", "two", "r2"], view.Entries.OfType<Said>().Select(s => s.Text));
        Assert.Equal(2, view.Runs.Length);
    }

    [Fact]
    public async Task ImportLeavesTheOldAgentUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var writes = new CountingAgentWrites();
        await using var runtime = await Start(StubChatClient.Replying("old reply"), ct, agentWrites: writes);
        using var person = runtime.As(Who.Person);
        var convId = ConversationId.New();
        var oldAgentId = runtime.Id("agent", "ino/conversations/" + convId.Value);
        var oldAgent = runtime.Get<IAgent>(oldAgentId);

        await oldAgent.Start(AgentTurn.Text("legacy prompt"));
        await runtime.WaitForSignals<TurnCompleted>(oldAgentId, 1, Within, ct);
        var before = await oldAgent.Read();
        writes.Reset();

        var conversation = runtime.Get<IConversation>(runtime.Id(IConversation.Type, IConversation.KeyOf(convId)));
        var view = await conversation.Read();

        Assert.Equal(["legacy prompt", "old reply"], view.Entries.OfType<Said>().Select(s => s.Text));
        Assert.Equal(0, writes.Count);
        Assert.Equal(before.Version, (await oldAgent.Read()).Version);
    }

    [Fact]
    public async Task AnInterruptBlocksAndItsRemovalUnblocks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await Start(StubChatClient.Replying("ok"), ct);
        using var person = runtime.As(Who.Person);
        var convId = ConversationId.New();
        var id = runtime.Id(IConversation.Type, IConversation.KeyOf(convId));
        var conversation = runtime.Get<IConversation>(id);
        var posture = runtime.Get<IAgentContext>(runtime.Id("agent-context", "context/security.posture"));

        await conversation.Seat("ino", new AgentDefinition { Instructions = "x", Contexts = ["context/security.posture"] });
        await posture.Set(new ContextEntry("halt", "policy hold", [], [ContextTags.Interrupt]));

        await WaitUntil(async () => (await conversation.Read()).Blocked, ct);
        var blockedErr = await Assert.ThrowsAsync<InvalidOperationException>(() => conversation.Say("blocked turn"));
        Assert.StartsWith("Blocked: ", blockedErr.Message, StringComparison.Ordinal);

        await posture.Remove("halt");
        await WaitUntil(async () => !(await conversation.Read()).Blocked, ct);
        await conversation.Say("unblocked turn");
        var view = await WaitUntilIdle(conversation, minRuns: 1, ct);
        Assert.Equal(["unblocked turn", "ok"], view.Entries.OfType<Said>().Select(s => s.Text));
    }

    [Fact]
    public async Task PostIsRecordedOncePerId()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await Start(StubChatClient.Replying("ok"), ct);
        using var person = runtime.As(Who.Person);
        var convId = ConversationId.New();
        var conversation = runtime.Get<IConversation>(runtime.Id(IConversation.Type, IConversation.KeyOf(convId)));

        await conversation.Seat("ino", new AgentDefinition { Instructions = "x" });
        await conversation.Post("Welcome!", "greeting");
        await conversation.Post("Welcome!", "greeting");

        var view = await conversation.Read();
        var post = Assert.Single(view.Entries.OfType<Said>());
        Assert.Equal(EntryIds.Post("greeting"), post.Id);
        Assert.Equal("Welcome!", post.Text);
    }

    [Fact]
    public async Task EveryChunkBecomesADeltaInOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        static async IAsyncEnumerable<ChatResponseUpdate> Stream(
            IReadOnlyList<ChatMessage> _,
            ChatOptions? __,
            [EnumeratorCancellation] CancellationToken token)
        {
            for (var i = 0; i < 200; i++)
            {
                token.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, $"w{i} ");
            }
            await Task.CompletedTask;
        }

        await using var runtime = await Start(StubChatClient.StreamingOnly(Stream), ct);
        using var person = runtime.As(Who.Person);
        var convId = ConversationId.New();
        var id = runtime.Id(IConversation.Type, IConversation.KeyOf(convId));
        var conversation = runtime.Get<IConversation>(id);

        var deltas = new List<TextDelta>();
        await using var listen = await runtime.Services.GetRequiredService<IClusterListeners>()
            .Listen<TextDelta>("test-deltas", (pub, delta, _) =>
            {
                if (pub == id) { lock (deltas) { deltas.Add(delta); } }
                return Task.CompletedTask;
            });

        await conversation.Seat("ino", new AgentDefinition { Instructions = "x" });
        await conversation.Say("stream 200");
        var view = await WaitUntilIdle(conversation, minRuns: 1, ct);

        var runId = Assert.Single(view.Runs).RunId;
        var finalReply = view.Entries.OfType<Said>().Last(e => e.By is Participant.Assistant);
        TextDelta[] snapshot;
        lock (deltas) { snapshot = [.. deltas]; }

        Assert.Equal(200, snapshot.Length);
        Assert.Equal(finalReply.Text, string.Concat(snapshot.Select(d => d.Delta)));
        Assert.Equal(snapshot.Select(d => d.At), snapshot.Select((_, i) => snapshot.Take(i).Sum(x => x.Delta.Length)));
        Assert.All(snapshot, d => Assert.Equal(EntryIds.Reply(runId), d.Entry));
    }

    [Fact]
    public async Task TextOnlyProgressPublishesNoConversationChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        static async IAsyncEnumerable<ChatResponseUpdate> Stream(
            IReadOnlyList<ChatMessage> _,
            ChatOptions? __,
            [EnumeratorCancellation] CancellationToken token)
        {
            for (var i = 0; i < 50; i++)
            {
                token.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, $"w{i} ");
                if (i == 25) { await Task.Delay(TurnRunner.ProgressInterval + TimeSpan.FromMilliseconds(50), token); }
            }
        }

        await using var runtime = await Start(StubChatClient.StreamingOnly(Stream), ct);
        using var person = runtime.As(Who.Person);
        var convId = ConversationId.New();
        var id = runtime.Id(IConversation.Type, IConversation.KeyOf(convId));
        var conversation = runtime.Get<IConversation>(id);

        await conversation.Seat("ino", new AgentDefinition { Instructions = "x" });
        var before = (await conversation.Read()).Revision;

        await conversation.Say("stream");
        await WaitUntilIdle(conversation, minRuns: 1, ct);

        var changed = await runtime.Signals<ConversationChanged>(id, ct);
        Assert.Equal(2, changed.Count(c => c.Revision > before));
    }

    [Fact]
    public async Task ProgressIsStillStoredWhileStreaming()
    {
        var ct = TestContext.Current.CancellationToken;
        var sentThree = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<ChatResponseUpdate> Stream(
            IReadOnlyList<ChatMessage> _,
            ChatOptions? __,
            [EnumeratorCancellation] CancellationToken token)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "w0 ");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "w1 ");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "w2 ");
            sentThree.TrySetResult();
            await release.Task.WaitAsync(token);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
        }

        await using var runtime = await Start(StubChatClient.StreamingOnly(Stream), ct);
        using var person = runtime.As(Who.Person);
        var convId = ConversationId.New();
        var id = runtime.Id(IConversation.Type, IConversation.KeyOf(convId));
        var conversation = runtime.Get<IConversation>(id);

        await conversation.Seat("ino", new AgentDefinition { Instructions = "x" });
        await conversation.Say("hold");
        await sentThree.Task.WaitAsync(Within, ct);

        await WaitUntil(async () => (await conversation.Read()).Active?.Reply == "w0 w1 w2 ", ct);
        Assert.Equal("w0 w1 w2 ", (await conversation.Read()).Active!.Reply);

        release.TrySetResult();
        await WaitUntilIdle(conversation, minRuns: 1, ct);
    }

    [Fact]
    public async Task DeltasHoldBackASecretTail()
    {
        var ct = TestContext.Current.CancellationToken;
        static async IAsyncEnumerable<ChatResponseUpdate> Stream(
            IReadOnlyList<ChatMessage> _,
            ChatOptions? __,
            [EnumeratorCancellation] CancellationToken token)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "key sk_");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "live_x9");
            yield return new ChatResponseUpdate(ChatRole.Assistant, ".");
            await Task.CompletedTask;
        }

        await using var runtime = await Start(StubChatClient.StreamingOnly(Stream), ct, secret: "sk_live_x9");
        using var person = runtime.As(Who.Person);
        var convId = ConversationId.New();
        var id = runtime.Id(IConversation.Type, IConversation.KeyOf(convId));
        var conversation = runtime.Get<IConversation>(id);

        var deltas = new List<TextDelta>();
        await using var listen = await runtime.Services.GetRequiredService<IClusterListeners>()
            .Listen<TextDelta>("test-secret-deltas", (pub, delta, _) =>
            {
                if (pub == id) { lock (deltas) { deltas.Add(delta); } }
                return Task.CompletedTask;
            });

        await conversation.Seat("ino", new AgentDefinition { Instructions = "x" });
        await conversation.Say("reveal");
        await WaitUntilIdle(conversation, minRuns: 1, ct);

        TextDelta[] snapshot;
        lock (deltas) { snapshot = [.. deltas]; }

        Assert.DoesNotContain(snapshot, d => d.Delta.Contains("sk_", StringComparison.Ordinal));
        Assert.Equal("key ****.", string.Concat(snapshot.Select(d => d.Delta)));
    }

    [Fact]
    public async Task DeltasHoldBackASecretSplitMidStream()
    {
        var ct = TestContext.Current.CancellationToken;
        static async IAsyncEnumerable<ChatResponseUpdate> Stream(
            IReadOnlyList<ChatMessage> _,
            ChatOptions? __,
            [EnumeratorCancellation] CancellationToken token)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "prefix text is long key sk_");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "live_x9");
            yield return new ChatResponseUpdate(ChatRole.Assistant, " and lots of trailing text.");
            await Task.CompletedTask;
        }

        await using var runtime = await Start(StubChatClient.StreamingOnly(Stream), ct, secret: "sk_live_x9");
        using var person = runtime.As(Who.Person);
        var convId = ConversationId.New();
        var id = runtime.Id(IConversation.Type, IConversation.KeyOf(convId));
        var conversation = runtime.Get<IConversation>(id);

        var deltas = new List<TextDelta>();
        await using var listen = await runtime.Services.GetRequiredService<IClusterListeners>()
            .Listen<TextDelta>("test-midstream-secret-deltas", (pub, delta, _) =>
            {
                if (pub == id) { lock (deltas) { deltas.Add(delta); } }
                return Task.CompletedTask;
            });

        await conversation.Seat("ino", new AgentDefinition { Instructions = "x" });
        await conversation.Say("reveal midstream");
        var view = await WaitUntilIdle(conversation, minRuns: 1, ct);

        TextDelta[] snapshot;
        lock (deltas) { snapshot = [.. deltas]; }

        Assert.True(snapshot.Length > 1);
        Assert.DoesNotContain(snapshot, d => d.Delta.Contains("sk", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot, d => d.Delta.Contains("live_x9", StringComparison.Ordinal));
        Assert.Equal(
            "prefix text is long key **** and lots of trailing text.",
            string.Concat(snapshot.Select(d => d.Delta)));
        Assert.Equal(snapshot.Select(d => d.At), snapshot.Select((_, i) => snapshot.Take(i).Sum(x => x.Delta.Length)));
        Assert.Equal("prefix text is long key **** and lots of trailing text.", view.Entries.OfType<Said>().Last().Text);
    }

    [Fact]
    public async Task TextDeltaIsNeverJournaled()
    {
        var ct = TestContext.Current.CancellationToken;
        static async IAsyncEnumerable<ChatResponseUpdate> Stream(
            IReadOnlyList<ChatMessage> _,
            ChatOptions? __,
            [EnumeratorCancellation] CancellationToken token)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "hello ");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "world");
            await Task.CompletedTask;
        }

        await using var runtime = await Start(StubChatClient.StreamingOnly(Stream), ct);
        using var person = runtime.As(Who.Person);
        var convId = ConversationId.New();
        var id = runtime.Id(IConversation.Type, IConversation.KeyOf(convId));
        var conversation = runtime.Get<IConversation>(id);

        await conversation.Seat("ino", new AgentDefinition { Instructions = "x" });
        await conversation.Say("ephemeral check");
        await WaitUntilIdle(conversation, minRuns: 1, ct);

        var journal = runtime.Services.GetRequiredService<IBrainJournalWriter>();
        Assert.Null(await SignalLog.Newest(journal, id, "conversation.text-delta", ct));
    }

    [Fact]
    public async Task StopCancelsTheProviderStream()
    {
        var ct = TestContext.Current.CancellationToken;
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<ChatResponseUpdate> Stream(
            IReadOnlyList<ChatMessage> _,
            ChatOptions? __,
            [EnumeratorCancellation] CancellationToken token)
        {
            using var registration = token.Register(() => observed.TrySetResult());
            yield return new ChatResponseUpdate(ChatRole.Assistant, "a ");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "b ");
            held.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }

        await using var runtime = await Start(StubChatClient.StreamingOnly(Stream), ct);
        using var person = runtime.As(Who.Person);
        var convId = ConversationId.New();
        var id = runtime.Id(IConversation.Type, IConversation.KeyOf(convId));
        var conversation = runtime.Get<IConversation>(id);

        var deltas = new List<TextDelta>();
        await using var listen = await runtime.Services.GetRequiredService<IClusterListeners>()
            .Listen<TextDelta>("test-stop-deltas", (pub, delta, _) =>
            {
                if (pub == id) { lock (deltas) { deltas.Add(delta); } }
                return Task.CompletedTask;
            });

        await conversation.Seat("ino", new AgentDefinition { Instructions = "x" });
        await conversation.Say("go");
        await held.Task.WaitAsync(Within, ct);

        await conversation.Stop();
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);

        var view = await WaitUntilIdle(conversation, minRuns: 1, ct);
        var run = Assert.Single(view.Runs);
        Assert.Equal((AgentRunStatus.Cancelled, "a b "), (run.Status, run.Reply));

        int count;
        lock (deltas) { count = deltas.Count; }
        Assert.True(count >= 2);
        await Task.Delay(300, ct);
        lock (deltas) { Assert.Equal(count, deltas.Count); }
    }

    [Fact]
    public async Task TextDeltaReachesASessionSynapseOverSignalLogLive()
    {
        var ct = TestContext.Current.CancellationToken;
        static async IAsyncEnumerable<ChatResponseUpdate> Stream(
            IReadOnlyList<ChatMessage> _,
            ChatOptions? __,
            [EnumeratorCancellation] CancellationToken token)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "one ");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "two ");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "three");
            await Task.CompletedTask;
        }

        await using var runtime = await Start(StubChatClient.StreamingOnly(Stream), ct);
        using var person = runtime.As(Who.Person);
        var convId = ConversationId.New();
        var id = runtime.Id(IConversation.Type, IConversation.KeyOf(convId));
        var conversation = runtime.Get<IConversation>(id);

        await conversation.Seat("ino", new AgentDefinition { Instructions = "x" });
        var beforeRevision = (await conversation.Read()).Revision;

        var received = new List<(string Alias, long Seq, string? Delta, int? At, string? Entry)>();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Within);

        await foreach (var sig in SignalLog.Live(
            runtime.Services,
            id,
            ["conversation.text-delta", "conversation.changed"],
            "test-live-" + Guid.NewGuid().ToString("N"),
            () => conversation.Say("stream live"),
            limit.Token))
        {
            if (sig.Alias == "conversation.text-delta")
            {
                var delta = sig.Payload.GetProperty("delta").GetString();
                var at = sig.Payload.GetProperty("at").GetInt32();
                var entry = sig.Payload.GetProperty("entry").GetString();
                received.Add((sig.Alias, sig.Sequence, delta, at, entry));
            }
            else if (sig.Alias == "conversation.changed")
            {
                var rev = sig.Payload.GetProperty("revision").GetInt64();
                if (rev >= beforeRevision + 2)
                {
                    break;
                }
            }
        }

        Assert.Equal(3, received.Count);
        Assert.All(received, r => Assert.Equal(0L, r.Seq));
        Assert.Equal(["one ", "two ", "three"], received.Select(r => r.Delta));
        Assert.Equal([0, 4, 8], received.Select(r => r.At));
        Assert.All(received, r => Assert.EndsWith("/reply", r.Entry ?? string.Empty, StringComparison.Ordinal));
    }

    private static IEnumerable<string> AllText(ConversationView view)
    {
        foreach (var entry in view.Entries)
        {
            switch (entry)
            {
                case Said s: yield return s.Text; break;
                case Called c: yield return c.ArgsJson; break;
                case Returned r:
                    yield return r.ResultJson;
                    if (r.Card?.NodeJson is { } node) { yield return node; }
                    break;
                case Summarized sum: yield return sum.Summary; break;
            }
        }
        foreach (var run in view.Runs)
        {
            if (run.Reply is { } r) { yield return r; }
            if (run.Error is { } e) { yield return e; }
            foreach (var tc in run.ToolCalls)
            {
                yield return tc.Arguments;
                if (tc.Result is { } res) { yield return res; }
                if (tc.Error is { } err) { yield return err; }
            }
        }
    }

    private static async Task<ConversationView> WaitUntilIdle(IConversation conversation, int minRuns, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < Within)
        {
            var view = await conversation.Read();
            if (view.Active is null && view.Runs.Length >= minRuns)
            {
                return view;
            }
            await Task.Delay(30, ct);
        }
        return await conversation.Read();
    }

    private static async Task WaitUntil(Func<Task<bool>> predicate, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < Within)
        {
            if (await predicate())
            {
                return;
            }
            await Task.Delay(30, ct);
        }
        Assert.True(await predicate(), "Condition was not met within timeout.");
    }

    private async Task<INeuronRuntime> Start(
        StubChatClient client,
        CancellationToken ct,
        string? secret = null,
        AgentWrites? agentWrites = null,
        bool wireSurface = false)
        => await fixture.Start(NeuronRuntimeKind.Silo, ct, siloServices: services =>
        {
            services.AddSingleton<IChatClient>(client);
            if (wireSurface)
            {
                var compiled = new CompiledTypes([typeof(IAgentContext).Assembly]);
                var observer = new StubBrainObserver(compiled.Entries);
                services.AddSingleton(sp => new OsNeuronSurface(
                    sp.GetRequiredService<INeuronProxies>(),
                    observer,
                    new NeuronTypeSearch(observer, [], null),
                    plain: sp));
            }
            else
            {
                services.AddSingleton(new OsNeuronSurface(null!, null!, null!));
            }
            services.AddSingleton<IAiCredentials, NoProviders>();
            if (secret is not null)
            {
                services.AddSingleton<ISecretValueSource>(_ => new StaticSecretSource(secret));
                services.AddSingleton<ISecretRedactor, SecretRedactor>();
            }
            else
            {
                services.AddSingleton<ISecretRedactor, NoSecrets>();
            }
            if (agentWrites is not null)
            {
                services.AddSingleton(agentWrites);
            }
            services.AddOptions<AIOptions>();
            services.AddSingleton<ModelCatalog>();
        });

    private sealed class CountingAgentWrites : AgentWrites
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public void Reset() => Interlocked.Exchange(ref _count, 0);
        public override Task Before(AgentStorage next)
        {
            Interlocked.Increment(ref _count);
            return Task.CompletedTask;
        }
    }

    private sealed class StaticSecretSource(params string[] secrets) : ISecretValueSource
    {
        public ValueTask<IReadOnlyList<string>> CurrentValues(CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<string>>(secrets);
    }

    private sealed class NoProviders : IAiCredentials
    {
        public Task<IntegrationSnapshot> StatusOf(string integrationId, CancellationToken cancellationToken = default)
            => Task.FromResult(new IntegrationSnapshot { IntegrationId = integrationId, Status = IntegrationStatus.Unconfigured });
        public Task<long> GenerationOf(string integrationId, CancellationToken cancellationToken = default) => Task.FromResult(0L);
        public Task<ReleasedIntegration> MaterializeAsync(string integrationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ReleaseSecretAsync(string integrationId, string field, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> BrainKey(string brainId, AiProvider provider, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class NoSecrets : ISecretRedactor
    {
        public ValueTask<string?> Redact(string? text, CancellationToken cancellationToken) => ValueTask.FromResult(text);
        public ValueTask<int> TailLength(CancellationToken cancellationToken) => ValueTask.FromResult(0);
    }

    private sealed class StubBrainObserver(BrainType[] types) : IBrainObserver
    {
        public IAsyncEnumerable<BrainInstance> Instances<T>(CancellationToken cancellationToken) where T : class
            => AsyncEnumerable.Empty<BrainInstance>();
        public IAsyncEnumerable<BrainInstance> Instances(string? type, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<BrainInstance>();
        public Task<BrainType[]> Types(CancellationToken cancellationToken = default) => Task.FromResult(types);
        public Task<string[]> Brains(CancellationToken cancellationToken = default) => Task.FromResult<string[]>([]);
        public Task<ObservedWire[]> Wires(CancellationToken cancellationToken = default) => Task.FromResult<ObservedWire[]>([]);
        public IAsyncEnumerable<SignalDelivery> Deliveries(CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<SignalDelivery>();
        public Task<JsonElement?> State(NeuronId id, CancellationToken cancellationToken = default)
            => Task.FromResult<JsonElement?>(null);
    }
}
