using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Invocation;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Conversations;

namespace IntoChat.OS.Tests.Unit.Modules.Shell.Conversations;

public sealed class SessionListFacts
{
    private static readonly BrainId Brain = new("owner");
    private static readonly NeuronId ListId = new(Brain, Sessions.Type, ISessionList.DefaultKey);

    private static TestOS CreateOs() => TestOS.Local()
        .Register(typeof(FlutterModule).Assembly)
        .Register(typeof(InoConversationsNeuron).Assembly);

    private static IDisposable AsPerson(TestOS os)
        => os.Edge.Enter(os.Edge.Person("person", "person", Brain));

    [Fact]
    public async Task OpenCreatesAndMakesCurrent()
    {
        await using var os = CreateOs();
        using var person = AsPerson(os);
        var sessions = os.Get<ISessionList>(ListId);
        var id = "c-" + new string('a', 32);

        Assert.Equal(id, (await sessions.Open(id)).Id);
        Assert.Equal(id, await sessions.Current());
    }

    [Fact]
    public async Task TheOldConversationRegistryIsImportedOnce()
    {
        await using var os = CreateOs();
        using var person = AsPerson(os);
        var legacy = os.Get<IInoConversations>(new NeuronId(Brain, "ino.conversations", IInoConversations.DefaultKey));
        var firstId = "c-" + new string('1', 32);
        var secondId = "c-" + new string('2', 32);
        await legacy.Use("ino/conversations/" + firstId);
        await legacy.Use("ino/conversations/" + secondId);

        var sessions = os.Get<ISessionList>(ListId);
        Assert.Equal(2, (await sessions.List(archived: false)).Total);
        Assert.Equal(secondId, await sessions.Current());
        await sessions.List(archived: false);
        Assert.Equal(2, (await sessions.List(archived: false)).Total);
    }

    [Fact]
    public async Task TouchTitlesADefaultEntryFromTheFirstMessage()
    {
        await using var os = CreateOs();
        using var person = AsPerson(os);
        var sessions = os.Get<ISessionList>(ListId);
        var id = "c-" + new string('b', 32);
        await sessions.Open(id);
        await sessions.Touch(id, "Plan a trip to Bergen");

        var entry = await sessions.Get(id);
        Assert.NotNull(entry);
        Assert.Equal("Plan a trip to Bergen", entry!.Title);
    }

    [Fact]
    public void AnOldChatIndexStateStillLoads()
    {
        var loaded = (SessionListState)RuntimeProtocol.Deserialize(
            """{"revision":3,"entries":[{"id":"main","title":"Trip","updatedAt":"2026-11-01T00:00:00+00:00","archived":false,"modelChoice":null}]}"""u8,
            typeof(SessionListState))!;
        Assert.Equal(3, loaded.Revision);
        Assert.Equal("Trip", Assert.Single(loaded.Entries).Title);
        Assert.False(loaded.Imported);
        Assert.Null(loaded.Current);
    }
}
