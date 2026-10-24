using IntoChat.OS;
using IntoChat.OS.AI.Inference;
using IntoChat.OS.Flutter.Chat;
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
        public Task<Guid> Watch(INeuronObserver observer) => throw new NotSupportedException();
        public Task<Guid> Watch(INeuronObserver observer, string[] signals) => throw new NotSupportedException();
        public Task Unwatch(INeuronObserver observer) => throw new NotSupportedException();
        public Task<SignalPage> Read(long since, int limit) => throw new NotSupportedException();
    }
}
