using DigitalBrain;
using IntoChat.OS;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.Flutter.Chat;
using Xunit;

namespace IntoChat.OS.Kernel.Tests.Unit;

public sealed class OsInoChatsFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TitlesAreOneLineClippedAndNeverEmpty()
    {
        Assert.Equal(new string('a', 52), InoChats.Title(new string('a', 5_000)));
        Assert.Equal("New chat", InoChats.Title("  \n "));
        Assert.Equal("New chat", InoChats.Title(null));
        Assert.Equal("a b c", InoChats.Title("a\nb  c"));
        Assert.Equal("bell", InoChats.Title("bell\u0007"));
        Assert.Equal(51, InoChats.Title(new string('a', 51) + "\U0001F600").Length);
        Assert.Equal("ab", InoChats.Title("a‮b​⁦﻿"));
        Assert.Equal("New chat", InoChats.Title("​‮ ⁩﻿"));
        Assert.Equal("New chat", InoChats.Title("‍"));
        var family = "\U0001F468‍\U0001F469‍\U0001F467";
        Assert.Equal(new string('a', 51), InoChats.Title(new string('a', 51) + family));
        Assert.Equal(new string('a', 44) + family, InoChats.Title(new string('a', 44) + family + "b"));
        Assert.Equal(new string('a', 51), InoChats.Title(new string('a', 51) + "éx"));
        Assert.Equal(new string('a', 50) + "é", InoChats.Title(new string('a', 50) + "éx"));
    }

    [Fact]
    public void IdsAndAgentKeysRoundTrip()
    {
        var id = "c-" + Guid.NewGuid().ToString("N");
        Assert.Equal("brain/apps/ino-data/conversations/main", InoChats.AgentKey("brain/apps/ino-data", "main"));
        Assert.Equal(id, InoChats.IdOf(InoChats.AgentKey("brain/apps/ino-data", id)));
        Assert.Equal("main", InoChats.IdOf("brain/apps/ino/conversations/main"));
    }

    [Fact]
    public async Task BackfillAddsMissingConversationsOnceWithFirstMessageTitles()
    {
        var data = "brain-" + Guid.NewGuid().ToString("N") + "/apps/ino-data";
        var index = new FakeIndex();
        var reads = new List<string>();
        var spoken = "c-" + Guid.NewGuid().ToString("N");
        var silent = "c-" + Guid.NewGuid().ToString("N");
        var listed = "c-" + Guid.NewGuid().ToString("N");
        await index.Create(listed, "Already here");
        Task<AiMessage[]> Read(string agent)
        {
            reads.Add(agent);
            return Task.FromResult<AiMessage[]>(agent.EndsWith(spoken, StringComparison.Ordinal)
                ? [new("assistant", [new AiText("Hi")]), new("user", [new AiText("  book a\nflight  ")]), new("user", [new AiText("later")])]
                : agent.EndsWith("main", StringComparison.Ordinal) ? [new("user", [new AiText("hello there")])] : []);
        }

        await InoChats.TryBackfill(index, InoChats.AgentKey(data, spoken), Read, Ct);
        await InoChats.TryBackfill(index, InoChats.AgentKey(data, silent), Read, Ct);
        await InoChats.TryBackfill(index, InoChats.AgentKey(data, listed), Read, Ct);
        await InoChats.TryBackfill(index, InoChats.AgentKey(data, "main"), Read, Ct);
        await index.Rename(spoken, "Renamed");
        await InoChats.TryBackfill(index, InoChats.AgentKey(data, spoken), Read, Ct);
        await InoChats.TryBackfill(index, InoChats.AgentKey(data, "main"), Read, Ct);

        Assert.Equal("Renamed", index.Entries[spoken].Title);
        Assert.Equal("Untitled", index.Entries[silent].Title);
        Assert.Equal("Already here", index.Entries[listed].Title);
        Assert.Equal("hello there", index.Entries["main"].Title);
        Assert.Equal(3, reads.Count);
        Assert.Equal(4, index.Created);
    }

    // Upgrade from 3.1.3: every conversation agent under {DataKey}/conversations/ is found (main and each c-*),
    // newest first and at most MaxListed, ignoring other agents and keys that are not chat ids.
    [Fact]
    public void ConversationsUnderTheDataKeyAreFoundNewestFirstAndBounded()
    {
        var data = "brain/apps/ino-data";
        var at = DateTimeOffset.UnixEpoch;
        var older = "c-" + Guid.NewGuid().ToString("N");
        var newer = "c-" + Guid.NewGuid().ToString("N");
        BrainInstance[] instances =
        [
            new("agent", InoChats.AgentKey(data, older), at.AddMinutes(1), null),
            new("agent", InoChats.AgentKey(data, "main"), at, null),
            new("agent", InoChats.AgentKey(data, newer), at.AddMinutes(2), null),
            new("agent", "brain/apps/other/conversations/main", at.AddMinutes(3), null),
            new("agent", data + "/conversations/not-an-id", at.AddMinutes(4), null),
            new("agent", data + "/conversations/main/nested", at.AddMinutes(5), null),
        ];
        Assert.Equal([InoChats.AgentKey(data, newer), InoChats.AgentKey(data, older), InoChats.AgentKey(data, "main")],
            InoChats.ConversationKeys(instances, data));
        var many = Enumerable.Range(0, ChatIndex.MaxListed + 20)
            .Select(i => new BrainInstance("agent", InoChats.AgentKey(data, "c-" + i.ToString("x32")), at.AddSeconds(i), null)).ToArray();
        var found = InoChats.ConversationKeys(many, data);
        Assert.Equal(ChatIndex.MaxListed, found.Count);
        Assert.Equal(InoChats.AgentKey(data, "c-" + (ChatIndex.MaxListed + 19).ToString("x32")), found[0]);
    }

    // Every found conversation gets its entry, titled from its first user message, else "Untitled".
    [Fact]
    public async Task BackfillAllIndexesEveryConversation()
    {
        var data = "brain-" + Guid.NewGuid().ToString("N") + "/apps/ino-data";
        var index = new FakeIndex();
        var first = "c-" + Guid.NewGuid().ToString("N");
        var second = "c-" + Guid.NewGuid().ToString("N");
        Task<AiMessage[]> Read(string agent) => Task.FromResult<AiMessage[]>(agent.EndsWith(first, StringComparison.Ordinal)
            ? [new("user", [new AiText("Draft a letter")])] : agent.EndsWith("main", StringComparison.Ordinal) ? [new("user", [new AiText("hello")])] : []);

        Assert.Equal(3, await InoChats.TryBackfillAll(index, [InoChats.AgentKey(data, second), InoChats.AgentKey(data, first), InoChats.AgentKey(data, "main")], Read, Ct));
        Assert.Equal("Draft a letter", index.Entries[first].Title);
        Assert.Equal("Untitled", index.Entries[second].Title);
        Assert.Equal("hello", index.Entries["main"].Title);
    }

    [Fact]
    public async Task AFailingIndexNeverThrowsAndRetriesOnTheNextCompose()
    {
        var agent = InoChats.AgentKey("brain-" + Guid.NewGuid().ToString("N") + "/apps/ino", "main");
        var failing = new FakeIndex { Fail = true };
        var reads = 0;
        Task<AiMessage[]> Read(string _) { reads++; return Task.FromResult<AiMessage[]>([]); }

        Assert.False(await InoChats.TryBackfill(failing, agent, Read, Ct));
        Assert.False(await InoChats.TryBackfill(failing, "not-a-conversation", Read, Ct));
        failing.Fail = false;
        Assert.True(await InoChats.TryBackfill(failing, agent, Read, Ct));
        Assert.Equal("Untitled", failing.Entries["main"].Title);
        Assert.Equal(1, reads);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        failing.Fail = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InoChats.TryBackfill(failing, InoChats.AgentKey("x/apps/ino", "c-" + Guid.NewGuid().ToString("N")), Read, cancelled.Token));
    }

    [Fact]
    public async Task AHungBackfillIsAbandonedAfterTheTimeoutSoInoKeepsAnswering()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), InoChats.BackfillTimeout);
        var agent = InoChats.AgentKey("brain-" + Guid.NewGuid().ToString("N") + "/apps/ino", "c-" + Guid.NewGuid().ToString("N"));
        var hung = new FakeIndex { Hang = true };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(await InoChats.Within(TimeSpan.FromMilliseconds(100),
            token => InoChats.TryBackfill(hung, agent, _ => Task.FromResult<AiMessage[]>([]), token), Ct));
        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        // Not marked done: the next compose tries again and succeeds once the index answers.
        hung.Hang = false;
        Assert.True(await InoChats.Within(InoChats.BackfillTimeout,
            token => InoChats.TryBackfill(hung, agent, _ => Task.FromResult<AiMessage[]>([]), token), Ct));
        Assert.Equal("Untitled", hung.Entries[InoChats.IdOf(agent)].Title);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InoChats.Within(InoChats.BackfillTimeout, _ => Task.FromResult(true), cancelled.Token));
    }

    // A hung conversation read times out alone: the others are indexed, the pass completes for this brain (the hung
    // agent counts as attempted), and another brain still back-fills its own conversations.
    [Fact]
    public async Task AHungConversationReadDoesNotStallThePassAndAnotherBrainStillBackfills()
    {
        var first = "brain-" + Guid.NewGuid().ToString("N") + "/apps/ino-data";
        var second = "brain-" + Guid.NewGuid().ToString("N") + "/apps/ino-data";
        var hung = InoChats.AgentKey(first, "c-" + Guid.NewGuid().ToString("N"));
        var fine = InoChats.AgentKey(first, "c-" + Guid.NewGuid().ToString("N"));
        var other = InoChats.AgentKey(second, "main");
        var index = new FakeIndex();
        var lists = 0;
        Task<AiMessage[]> Read(string agent) => agent == hung ? new TaskCompletionSource<AiMessage[]>().Task : Task.FromResult<AiMessage[]>([]);
        Task<IReadOnlyList<string>> List(string data) { lists++; return Task.FromResult<IReadOnlyList<string>>(data == first ? [hung, fine] : [other]); }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(1, await InoChats.TryBackfillAll(first, _ => List(first), index, Read, Ct, TimeSpan.FromMilliseconds(100)));
        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.True(index.Entries.ContainsKey(InoChats.IdOf(fine)));
        Assert.Equal(0, await InoChats.TryBackfillAll(first, _ => List(first), index, Read, Ct, TimeSpan.FromMilliseconds(100)));
        Assert.Equal(1, lists);
        Assert.Equal(1, await InoChats.TryBackfillAll(second, _ => List(second), index, Read, Ct, TimeSpan.FromMilliseconds(100)));
        Assert.True(index.Entries.ContainsKey("main"));
    }

    // Opens never wait on back-fill: the work runs in the background, and its failure is logged, never thrown.
    [Fact]
    public async Task BackgroundWorkNeverDelaysTheCaller()
    {
        var never = new TaskCompletionSource();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var running = InoChats.Background("hung back-fill", _ => never.Task, Ct);
        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Assert.False(running.IsCompleted);
        await InoChats.Background("failing back-fill", _ => throw new InvalidOperationException("down"), Ct);
        never.SetResult();
        await running;
    }

    private sealed class FakeIndex : IChatIndex
    {
        public Dictionary<string, ChatIndexEntry> Entries { get; } = [];
        public int Created { get; private set; }
        public bool Fail { get; set; }
        public bool Hang { get; set; }

        public Task<ChatIndexEntry> Create(string id, string title)
        {
            Created++;
            if (!Entries.TryGetValue(id, out var entry)) { Entries[id] = entry = new(id, title, DateTimeOffset.UtcNow, false); }
            return Task.FromResult(entry);
        }

        public Task<ChatIndexEntry?> Get(string id) => Hang ? new TaskCompletionSource<ChatIndexEntry?>().Task
            : Fail ? throw new InvalidOperationException("index down") : Task.FromResult(Entries.GetValueOrDefault(id));
        public Task Rename(string id, string title) { Entries[id] = Entries[id] with { Title = title }; return Task.CompletedTask; }
        public Task<ChatIndexPage> List(bool archived, int skip = 0, int take = 100) => throw new NotSupportedException();
        public Task Archive(string id) => throw new NotSupportedException();
        public Task Unarchive(string id) => throw new NotSupportedException();
        public Task Touch(string id, string? firstMessage) => throw new NotSupportedException();
        public Task SetModel(string id, string? modelChoice) => throw new NotSupportedException();
    }
}
