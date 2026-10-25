using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Platform.Connections;

namespace IntoChat.OS.Authority.Tests.Integration.Connections;

public sealed class ConnectionIndexFacts(AuthorityHost host)
{
    [Fact]
    public async Task TheIndexListsOnlyItsOwnBrainsKeysOnceEach()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var aliceHomeId = AuthorityHost.Unique("alice-home");
        var bobHomeId = AuthorityHost.Unique("bob-home");
        var mine = brain.Platform.Connections(aliceHomeId);
        var theirs = brain.Platform.Connections(bobHomeId);
        await mine.Track(ConnectionKeys.For(aliceHomeId, "example", "one"));
        await mine.Track(ConnectionKeys.For(aliceHomeId, "example", "one"));
        await theirs.Track(ConnectionKeys.For(bobHomeId, "example", "one"));
        Assert.Equal([ConnectionKeys.For(aliceHomeId, "example", "one")], await mine.List());
        await mine.Untrack(ConnectionKeys.For(aliceHomeId, "example", "one"));
        Assert.Empty(await mine.List());
        Assert.Single(await theirs.List());
    }

    [Fact]
    public async Task TheIndexRefusesAKeyFromAnotherBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var aliceHomeId = AuthorityHost.Unique("alice-home");
        var bobHomeId = AuthorityHost.Unique("bob-home");
        await Assert.ThrowsAsync<ArgumentException>(() => brain.Platform.Connections(aliceHomeId).Track(ConnectionKeys.For(bobHomeId, "example", "one")));
    }
}
