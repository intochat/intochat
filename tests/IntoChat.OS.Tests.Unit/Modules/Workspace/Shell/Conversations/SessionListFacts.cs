using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Invocation;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Conversations;

namespace IntoChat.OS.Tests.Unit.Modules.Shell.Conversations;

public sealed class SessionListFacts
{
    private static readonly BrainId Brain = new("owner");
    private static readonly NeuronId ListId = new(Brain, Threads.Type, IThreadDirectory.DefaultKey);

    private static TestOS CreateOs() => TestOS.Local()
        .Register(typeof(FlutterModule).Assembly);

    private static IDisposable AsPerson(TestOS os)
        => os.Edge.Enter(os.Edge.Person("person", "person", Brain));

    [Fact]
    public async Task OpenCreatesAndMakesCurrent()
    {
        await using var os = CreateOs();
        using var person = AsPerson(os);
        var directory = os.Get<IThreadDirectory>(ListId);
        var id = "c-" + new string('a', 32);

        Assert.Equal(id, (await directory.Open(id)).Id);
        Assert.Equal(id, await directory.Current());
    }

    [Fact]
    public async Task TouchTitlesADefaultEntryFromTheFirstMessage()
    {
        await using var os = CreateOs();
        using var person = AsPerson(os);
        var directory = os.Get<IThreadDirectory>(ListId);
        var id = "c-" + new string('b', 32);
        await directory.Open(id);
        await directory.Touch(id, "Plan a trip to Bergen");

        var entry = await directory.Get(id);
        Assert.NotNull(entry);
        Assert.Equal("Plan a trip to Bergen", entry!.Title);
    }

    [Fact]
    public async Task ShelfTracksFavouritesPinnedFoldersAndUnreadCounts()
    {
        await using var os = CreateOs();
        using var person = AsPerson(os);
        var directory = os.Get<IThreadDirectory>(ListId);

        var id1 = "c-" + new string('1', 32);
        var id2 = "c-" + new string('2', 32);
        await directory.Create(id1, "Squad Room", ThreadTrustPosture.OwnerTrusted, ThreadKind.Squad);
        await directory.Create(id2, "Design Notes", ThreadTrustPosture.OwnerTrusted, ThreadKind.DirectAssistant);

        await directory.SetFavourite(id1, true);
        await directory.SetPinned(id2, true);
        await directory.SetFolder(id1, "Projects");
        await directory.SetFolder(id2, "Work");

        var thread1 = new NeuronId(Brain, IConversation.Type, "conversations/" + id1);
        await directory.Changed(thread1, 5);

        var shelf = await directory.Shelf();
        Assert.Single(shelf.Favourites, e => e.Id == id1);
        Assert.Single(shelf.Pinned, e => e.Id == id2);
        Assert.Equal(["Projects", "Work"], shelf.Folders);
        Assert.Equal(5, shelf.TotalUnread);

        await directory.MarkRead(id1, 3);
        var updated = await directory.Get(id1);
        Assert.NotNull(updated);
        Assert.Equal(3, updated!.LastReadRevision);
        Assert.Equal(2, updated.UnreadCount);

        await directory.MarkRead(id1, 5);
        Assert.Equal(0, (await directory.Shelf()).TotalUnread);
    }

    [Fact]
    public async Task CreateShared_ThrowsWhenThreadKeyAlreadyBelongsToOwnerTrustedEntry()
    {
        await using var os = CreateOs();
        using var person = AsPerson(os);
        var directory = os.Get<IThreadDirectory>(ListId);

        var id = "c-" + new string('9', 32);
        await directory.Create(id, "Private Squad", ThreadTrustPosture.OwnerTrusted, ThreadKind.Squad);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => directory.CreateShared("Attempted Downgrade", ThreadKind.SharedRoom, threadKey: id));

        var entry = await directory.Get(id);
        Assert.NotNull(entry);
        Assert.Equal(ThreadTrustPosture.OwnerTrusted, entry!.TrustPosture);
        Assert.Equal("Private Squad", entry.Title);
    }

    [Fact]
    public void AnOldChatIndexStateStillLoads()
    {
        var loaded = (ThreadDirectoryState)RuntimeProtocol.Deserialize(
            """{"revision":3,"entries":[{"id":"main","title":"Trip","updatedAt":"2026-11-01T00:00:00+00:00","archived":false,"modelChoice":null}]}"""u8,
            typeof(ThreadDirectoryState))!;
        Assert.Equal(3, loaded.Revision);
        Assert.Equal("Trip", Assert.Single(loaded.Entries).Title);
        Assert.False(loaded.Imported);
        Assert.Null(loaded.Current);
    }
}
