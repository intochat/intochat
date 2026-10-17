using DigitalBrain.Apps;
using static DigitalBrain.Circles.Tests.CircleBrainRooms;

namespace DigitalBrain.Circles.Tests;

public sealed class CirclePublicationFacts
{
    [Fact]
    public async Task AMemberBringsAnExactReleaseWithoutThePublishersConnectionsAndAnOutsiderCannot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var vlad = await brain.PersonAsync("vlad", ct);
        var alice = await brain.PersonAsync("alice", ct);
        var bob = await brain.PersonAsync("bob", ct);
        var outsider = await brain.PersonAsync("outsider", ct);
        await Own(brain, vlad, "echo", "private", new() { ["preview.txt"] = "A" });
        var source = brain.Get<IApp>(IApp.Key(vlad.BrainId, "echo"));
        await vlad.In(vlad.BrainId, () => source.Publish());
        var release = Assert.Single(await source.PublishedReleases()).Reference;
        await vlad.In(vlad.BrainId, () => source.WriteFile("preview.txt", "B", Guid.NewGuid()));
        await vlad.In(vlad.BrainId, () => source.Publish());
        var room = await Create(brain, alice);
        await Join(brain, room, alice, bob);
        var operation = Guid.NewGuid();
        var copy = await bob.In(room, () => Circle(brain, room).BringPublication(release, "shared-echo", operation));
        Assert.Equal("A", copy.Files["preview.txt"]);
        Assert.Empty(copy.Connections);
        Assert.Equal("vlad", copy.Origin!.Publisher);
        Assert.Equal(bob.AccountId, copy.Origin.InstalledByAccount);
        await bob.In(room, () => Circle(brain, room).BringPublication(release, "shared-echo", operation));
        Assert.Single((await alice.In(room, () => Circle(brain, room).State())).SharedApps, name => name == "shared-echo");
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => outsider.In(room, () => Circle(brain, room).BringPublication(release, "outsider-copy", Guid.NewGuid())));
        await alice.In(room, () => Circle(brain, room).Remove(bob.AccountId));
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => bob.In(room, () => Circle(brain, room).BringPublication(release, "shared-echo", operation)));
    }

    [Fact]
    public async Task ThePersonalFeedKeepsBothReleasesAndForksTheSelectedOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var vlad = await brain.PersonAsync("vlad", ct);
        var reader = await brain.PersonAsync("reader", ct);
        await Own(brain, vlad, "echo", files: new() { ["preview.txt"] = "A" });
        var source = brain.Get<IApp>(IApp.Key(vlad.BrainId, "echo"));
        await vlad.In(vlad.BrainId, () => source.Publish());
        await vlad.In(vlad.BrainId, () => source.WriteFile("preview.txt", "B", Guid.NewGuid()));
        await vlad.In(vlad.BrainId, () => source.Publish());
        var factory = brain.Get<ICircleFactory>(ICircleFactory.Key(reader.BrainId));
        var feed = await reader.In(reader.BrainId, () => factory.Publications("vlad"));
        Assert.Equal("reader", feed.PrincipalId);
        Assert.Equal(2, feed.Items.Length);
        Assert.Equal("B", feed.Items[0].Preview);
        var selected = Assert.Single(feed.Items, item => item.Preview == "A");
        var copy = await reader.In(reader.BrainId, () => factory.Fork(selected.Reference, "my-echo", Guid.NewGuid()));
        Assert.Equal("A", copy.Files["preview.txt"]);
        Assert.Equal(selected.Reference.Version, copy.Version);
        Assert.Empty((await reader.In(reader.BrainId, () => factory.Publications("nobody"))).Items);
    }
}
