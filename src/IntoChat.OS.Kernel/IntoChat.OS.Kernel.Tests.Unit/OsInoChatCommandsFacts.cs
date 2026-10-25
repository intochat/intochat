using IntoChat.OS;
using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Modules.Flutter.Text;
using IntoChat.OS.Modules.Flutter.TextField;
using IntoChat.OS.Modules.Flutter.Views;
using Xunit;

namespace IntoChat.OS.Kernel.Tests.Unit;

// The sidebar commands against fake neurons: they only mutate state; the single view writer republishes.
public sealed class OsInoChatCommandsFacts
{
    private const string Data = "brain/apps/ino-data";
    private static readonly DateTimeOffset Then = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly string Older = "c-" + new string('1', 32), Newer = "c-" + new string('2', 32);

    private sealed class World
    {
        public FakeIndex Index { get; } = new();
        public FakeChat Chat { get; } = new();
        public FakeText ViewState { get; } = new();
        public FakeText Status { get; } = new();
        public FakeField Rename { get; } = new();
        public DateTimeOffset Now { get; set; } = Then;
        public List<Guid> NewChats { get; } = [];

        public World()
        {
            Index.Add(Older, "Older", Then.AddMinutes(1));
            Index.Add(Newer, "Newer", Then.AddMinutes(2));
            Chat.Selected = InoChats.AgentKey(Data, Newer);
        }

        public InoChatCommands Commands => new(Index, Chat, Rename, ViewState, Status, Data,
            operation => { NewChats.Add(operation); return Task.CompletedTask; }, () => Now);

        public Task Run(string action) => Commands.Command(action, Guid.NewGuid())!();

        public async Task<InoViewState> State() => await InoViewState.Read(ViewState, Now);
    }

    [Fact]
    public async Task AnArchiveOverlappingASearchKeystrokeKeepsTheToastAndTheQuery()
    {
        var world = new World();
        // A search keystroke commits between the archive's read and its write.
        world.ViewState.BeforeNextSet = () => InoViewState.Update(world.ViewState, state => state with { Query = "new" }, world.Now);
        await world.Run("archive:" + Older);
        var state = await world.State();
        Assert.Equal(("new", InoLayout.ArchivedToast, Older), (state.Query, state.Toast!, state.UndoId!));
    }

    [Fact]
    public async Task TwoRapidMutationsAreBothInTheFinalView()
    {
        var world = new World();
        var release = new TaskCompletionSource();
        var published = new List<UiNode>();
        var calls = 0;
        var refresher = new InoRefresher(async () =>
        {
            if (Interlocked.Increment(ref calls) == 1) { await release.Task; }
            var state = await world.State();
            var chats = (await world.Index.List(false)).Entries;
            published.Add(InoView.Render(Data, new(chats, Newer, state.Query, RenamingId: state.RenamingId, Toast: state.Toast, UndoId: state.UndoId)));
        });
        var first = refresher.Refresh();
        await world.Run("rename:" + Newer);
        var second = refresher.Refresh();
        await world.Run("archive:" + Older);
        var third = refresher.Refresh();
        release.SetResult();
        await Task.WhenAll(first, second, third);
        var last = published[^1];
        Assert.Contains(last.Children, node => node.Kind == UiNodeKind.Modal);
        Assert.Contains(last.Children, node => node.Kind == UiNodeKind.Toast);
        Assert.InRange(published.Count, 1, 2);
    }

    [Fact]
    public async Task ACommittedMutationNeverThrows()
    {
        // Commands do not publish, so nothing after the commit can fail the click.
        var world = new World();
        await world.Run("rename:" + Older);
        world.Rename.Value = "Trip";
        await world.Run("rename-save");
        Assert.Equal("Trip", world.Index.Entries[Older].Title);
        Assert.Null((await world.State()).RenamingId);
    }

    [Fact]
    public async Task UndoAfterTheToastExpiredLeavesTheChatArchived()
    {
        Assert.Equal(TimeSpan.FromSeconds(4), InoLayout.ToastLifetime);
        var world = new World();
        await world.Run("archive:" + Older);
        world.Now += InoLayout.ToastLifetime + TimeSpan.FromSeconds(1);
        await world.Run("undo-archive:" + Older);
        Assert.True(world.Index.Entries[Older].Archived);
        world.Now = Then;
        await world.Run("undo-archive:" + Older);
        Assert.False(world.Index.Entries[Older].Archived);
        Assert.Null((await world.State()).Toast);
    }

    [Fact]
    public async Task ArchivingTheSelectedChatMovesTheSelectionFirst()
    {
        var world = new World();
        world.Chat.FailSelect = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Run("archive:" + Newer));
        Assert.False(world.Index.Entries[Newer].Archived);
        Assert.Equal(InoChats.AgentKey(Data, Newer), world.Chat.Selected);

        world.Chat.FailSelect = false;
        await world.Run("archive:" + Newer);
        Assert.True(world.Index.Entries[Newer].Archived);
        Assert.Equal(InoChats.AgentKey(Data, Older), world.Chat.Selected);
        await world.Run("archive:" + Older);
        Assert.Single(world.NewChats);
    }

    [Fact]
    public async Task AnInvalidTitleKeepsTheModalAndSaysSo()
    {
        var world = new World();
        await world.Run("rename:" + Older);
        Assert.Equal("Older", world.Rename.Value);
        world.Rename.Value = "  ";
        await world.Run("rename-save");
        Assert.Equal(Older, (await world.State()).RenamingId);
        Assert.Equal(InoLayout.InvalidTitle, (await world.Status.Read()).Markdown);
    }

    [Fact]
    public void QueriesAndTitlesRenderInert()
    {
        Assert.Equal("ab", InoLayout.CleanQuery("a‮b​\n"));
        Assert.Equal(InoLayout.QueryMax, InoLayout.CleanQuery(new string('q', 500)).Length);
        var hostile = "a]*[b](c) <x>";
        var root = InoView.Render(Data, new([new(Older, hostile, Then, false)], Older, ""));
        var open = root.Children[0].Children[2].Children[0].Children[0];
        Assert.Equal((UiNodeKind.Button, hostile), (open.Kind, open.Text!));
        Assert.Equal("No chats match \"a\\]\\*\".", InoLayout.NoMatchCopy("a]*"));
    }

    private abstract class FakeNeuron : INeuron
    {
        public Task<Guid> Watch(INeuronObserver observer) => throw new NotSupportedException();
        public Task<Guid> Watch(INeuronObserver observer, string[] signals) => throw new NotSupportedException();
        public Task Unwatch(INeuronObserver observer) => throw new NotSupportedException();
        public Task<SignalPage> Read(long since, int limit) => throw new NotSupportedException();
    }

    private sealed class FakeText : FakeNeuron, IText
    {
        private TextState _state = new();
        public Func<Task>? BeforeNextSet { get; set; }
        public Task Set(string markdown) { _state = new() { Version = _state.Version + 1, Markdown = markdown }; return Task.CompletedTask; }
        public async Task<bool> TrySet(string markdown, int expectedVersion)
        {
            if (BeforeNextSet is { } concurrent) { BeforeNextSet = null; await concurrent(); }
            if (_state.Version != expectedVersion) { return false; }
            await Set(markdown);
            return true;
        }
        public Task<TextState> Read() => Task.FromResult(new TextState { Version = _state.Version, Markdown = _state.Markdown });
    }

    private sealed class FakeField : FakeNeuron, ITextField
    {
        public string Value { get; set; } = "";
        public Task Configure(string label, string kind, string? submitButton = null) => Task.CompletedTask;
        public Task SetValue(string value) { Value = value; return Task.CompletedTask; }
        public Task<bool> TrySetValue(string value, int expectedVersion) => throw new NotSupportedException();
        public Task Input(string value) => throw new NotSupportedException();
        public Task<TextFieldState> Read() => Task.FromResult(new TextFieldState { Value = Value });
    }

    private sealed class FakeChat : FakeNeuron, IChat
    {
        public string Selected { get; set; } = "";
        public bool FailSelect { get; set; }
        private long _revision = 1;
        public Task<ChatSelection> SelectAgent(string agent, long expectedSelectionRevision, Guid operationId)
        {
            if (FailSelect) { throw new InvalidOperationException("selection failed"); }
            Selected = agent;
            return Task.FromResult(new ChatSelection(agent, ++_revision));
        }
        public Task<ChatState> Read() => Task.FromResult(new ChatState { Selection = new(Selected, _revision) });
        public Task Project(ChatProjection projection) => throw new NotSupportedException();
        public Task Activate(string source, long generation, string actionId) => throw new NotSupportedException();
        public Task MarkRead(string source, long generation, long version, string[] messageIds) => throw new NotSupportedException();
    }

    private sealed class FakeIndex : FakeNeuron, IChatIndex
    {
        public Dictionary<string, ChatIndexEntry> Entries { get; } = [];
        public void Add(string id, string title, DateTimeOffset at) => Entries[id] = new(id, title, at, false);
        public Task<ChatIndexPage> List(bool archived, int skip = 0, int take = 100)
        {
            ChatIndexEntry[] page = [.. Entries.Values.Where(entry => entry.Archived == archived).OrderByDescending(entry => entry.UpdatedAt)];
            return Task.FromResult(new ChatIndexPage(page, page.Length));
        }
        public Task<ChatIndexEntry> Create(string id, string title) => throw new NotSupportedException();
        public Task Rename(string id, string title) { Entries[id] = Entries[id] with { Title = ChatIndex.ValidTitle(title) }; return Task.CompletedTask; }
        public Task Archive(string id) { Entries[id] = Entries[id] with { Archived = true }; return Task.CompletedTask; }
        public Task Unarchive(string id) { Entries[id] = Entries[id] with { Archived = false }; return Task.CompletedTask; }
        public Task Touch(string id, string? firstMessage) => throw new NotSupportedException();
        public Task SetModel(string id, string? modelChoice) => throw new NotSupportedException();
        public Task<ChatIndexEntry?> Get(string id) => Task.FromResult(Entries.GetValueOrDefault(id));
    }
}
