using DigitalBrain.Apps;
using static DigitalBrain.Circles.Tests.CircleBrainRooms;

namespace DigitalBrain.Circles.Tests;

public sealed class FriendsPublicationFacts
{
    [Fact(Timeout = 120_000)]
    public async Task AMembersFriendsAppPublishesItsOwnPostOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var alice = await brain.PersonAsync("alice", ct);
        var bob = await brain.PersonAsync("bob", ct);
        var circle = await Create(brain, alice);
        await Join(brain, circle, alice, bob);
        await Own(brain, bob, "friends");
        var chat = brain.Get<ICircleChat>(ICircleChat.Key(circle));
        using (bob.AsAppIn(circle, IApp.Key(circle, "circleroom")))
        { await chat.Say("circle app", "room-request"); }
        using (bob.AsApp(IApp.Key(bob.BrainId, "friends")))
        {
            await chat.PublishPost(bob.Key("posts/p1"), "request");
            await chat.PublishPost(bob.Key("posts/p1"), "request");
        }
        var lines = await alice.In(circle, () => chat.Read(0));
        Assert.Equal([new CircleLine(bob.AccountId, "circle app", 1), new CircleLine(bob.AccountId, "post " + bob.Key("posts/p1"), 2)], lines);
    }

    [Fact(Timeout = 120_000)]
    public async Task OldPostRequestsStayIdempotentAfterTheChatReceiptWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var alice = await brain.PersonAsync("alice", ct);
        var circle = await Create(brain, alice);
        await Own(brain, alice, "friends");
        var chat = brain.Get<ICircleChat>(ICircleChat.Key(circle));
        await alice.In(circle, () => chat.Say("ordinary", "ordinary-request"));
        using (alice.AsApp(IApp.Key(alice.BrainId, "friends")))
        {
            for (var i = 0; i < 65; i++) { await chat.PublishPost(alice.Key("posts/p" + i), "r" + i); }
            await chat.PublishPost(alice.Key("posts/p0"), "r0");
        }
        await alice.In(circle, () => chat.Say("ordinary", "ordinary-request"));
        Assert.Equal(66, (await alice.In(circle, () => chat.Read(0))).Length);
    }

    [Fact(Timeout = 120_000)]
    public async Task ANonmemberAndARemovedMemberCannotPublishEvenAReplay()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var alice = await brain.PersonAsync("alice", ct);
        var bob = await brain.PersonAsync("bob", ct);
        var circle = await Create(brain, alice);
        await Own(brain, bob, "friends");
        var chat = brain.Get<ICircleChat>(ICircleChat.Key(circle));
        using (bob.AsApp(IApp.Key(bob.BrainId, "friends")))
        { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => chat.PublishPost(bob.Key("posts/p1"), "request")); }
        await Join(brain, circle, alice, bob);
        using (bob.AsApp(IApp.Key(bob.BrainId, "friends")))
        { await chat.PublishPost(bob.Key("posts/p1"), "request"); }
        await alice.In(circle, () => Circle(brain, circle).Remove(bob.AccountId));
        using (bob.AsApp(IApp.Key(bob.BrainId, "friends")))
        { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => chat.PublishPost(bob.Key("posts/p1"), "request")); }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bob.In(circle, () => chat.Read(0)));
        Assert.Single(await alice.In(circle, () => chat.Read(0)));
    }

    [Fact(Timeout = 120_000)]
    public async Task FriendsPublicationRefusesAnotherAuthorOrAppOrScope()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var alice = await brain.PersonAsync("alice", ct);
        var bob = await brain.PersonAsync("bob", ct);
        var circle = await Create(brain, alice);
        await Join(brain, circle, alice, bob);
        await Own(brain, bob, "friends");
        var chat = brain.Get<ICircleChat>(ICircleChat.Key(circle));
        using (bob.AsApp(IApp.Key(bob.BrainId, "friends")))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => chat.PublishPost(alice.Key("posts/p1"), "foreign"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => chat.PublishPost(bob.Key("posts/../private"), "traversal"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => chat.Say("ordinary cross-brain chat", "say"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => chat.Read(0));
        }
        using (bob.AsApp(IApp.Key(bob.BrainId, "notes")))
        { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => chat.PublishPost(bob.Key("posts/p1"), "app")); }
        using (bob.AsAppIn(alice.BrainId, IApp.Key(alice.BrainId, "friends")))
        { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => chat.PublishPost(alice.Key("posts/p1"), "scope")); }
        using (bob.AsCaller())
        { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => chat.PublishPost(bob.Key("posts/p1"), "person")); }
        Assert.Empty(await alice.In(circle, () => chat.Read(0)));
    }
}
