using IntoChat.OS;
using IntoChat.OS.AI.Inference;
using IntoChat.OS.Flutter.Chat;
using Xunit;

namespace IntoChat.OS.Kernel.Tests.Unit;

public sealed class OsInoChatsFacts
{
    [Fact]
    public void TitlesAreOneLineClippedAndNeverEmpty()
    {
        Assert.Equal(new string('a', 52), InoChats.Title(new string('a', 5_000)));
        Assert.Equal("New chat", InoChats.Title("  \n "));
        Assert.Equal("New chat", InoChats.Title(null));
        Assert.Equal("a b c", InoChats.Title("a\nb  c"));
        Assert.Equal("bell", InoChats.Title("bell\u0007"));
        Assert.Equal(51, InoChats.Title(new string('a', 51) + "\U0001F600").Length);
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

        await InoChats.Backfill(index, InoChats.AgentKey(data, spoken), Read);
        await InoChats.Backfill(index, InoChats.AgentKey(data, silent), Read);
        await InoChats.Backfill(index, InoChats.AgentKey(data, listed), Read);
        await InoChats.Backfill(index, InoChats.AgentKey(data, "main"), Read);
        await index.Rename(spoken, "Renamed");
        await InoChats.Backfill(index, InoChats.AgentKey(data, spoken), Read);
        await InoChats.Backfill(index, InoChats.AgentKey(data, "main"), Read);

        Assert.Equal("Renamed", index.Entries[spoken].Title);
        Assert.Equal("Untitled", index.Entries[silent].Title);
        Assert.Equal("Already here", index.Entries[listed].Title);
        Assert.Equal("hello there", index.Entries["main"].Title);
        Assert.Equal(3, reads.Count);
        Assert.Equal(4, index.Created);
    }

    private sealed class FakeIndex : IChatIndex
    {
        public Dictionary<string, ChatIndexEntry> Entries { get; } = [];
        public int Created { get; private set; }

        public Task<ChatIndexEntry> Create(string id, string title)
        {
            Created++;
            if (!Entries.TryGetValue(id, out var entry)) { Entries[id] = entry = new(id, title, DateTimeOffset.UtcNow, false); }
            return Task.FromResult(entry);
        }

        public Task<ChatIndexEntry?> Get(string id) => Task.FromResult(Entries.GetValueOrDefault(id));
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
