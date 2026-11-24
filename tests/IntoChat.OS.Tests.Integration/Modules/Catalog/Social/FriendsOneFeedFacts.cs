using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.Friends;
using IntoChat.OS.Modules.Apps.Social;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Tests.Integration.Journeys;
using IntoChat.OS.Tests.Support.Fakes;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Social;

// The Friends screen shows one list of posts, read from the post stores: the person's own posts and their friends'. A post
// with an app and a plain post are in the same list, and both are written to the post store. The host carries the shell
// module beside the journey modules, because the screen binds its text fields there.
public sealed class FriendsOneFeedFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<JourneyOS> Start(NeuronRuntimeKind kind)
        => JourneyOS.Start(kind, [.. Journey.SliceModules, typeof(FlutterModule)], static fakes => fakes.Friends(), Ct);

    private static async Task<SocialPost[]> Stored(Person reader, Person author)
        => (await reader.Get<IPosts>(new NeuronId(author.Brain, "social.posts", IPosts.DefaultKey)).Since(0, Ct)).Posts;

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task OwnPostsAndFriendsPostsAreOneListFromThePostStores(NeuronRuntimeKind kind)
    {
        await using var os = await Start(kind);
        var (alice, bob) = (os.Person("alice"), os.Person("bob"));
        await alice.Befriend(bob, Ct);

        await using var aliceApp = await alice.Open<FriendsApp>(ct: Ct);
        await aliceApp.Send(new FriendsPublish("An app for my friends", "friends", "post-a", "First app", "unit-a", "markdraw"), Ct);
        await aliceApp.Send(new FriendsPublish("An app I keep to myself", "team", "post-b", "Second app", "unit-b", "data"), Ct);
        await aliceApp.Send(new PublishSocialPost("Hello from Alice!"), Ct);

        // One card of posts, and all three in it.
        aliceApp.ShouldDisplayText("Posts");
        aliceApp.ShouldNotDisplayText("Social Feed");
        aliceApp.ShouldNotDisplayText("App-Posts");
        aliceApp.ShouldDisplayText("Hello from Alice!");
        aliceApp.ShouldDisplayText("First app");
        aliceApp.ShouldDisplayText("Second app");

        // All three are in the post store, each with the audience it was published with; the profile holds none.
        var stored = await Stored(alice, alice);
        Assert.Equal(["post-a", "post-b"], stored.Take(2).Select(static post => post.Id));
        Assert.IsType<Audience.Friends>(stored[0].Audience);
        Assert.Empty(Assert.IsType<Audience.Only>(stored[1].Audience).Accounts);
        Assert.IsType<Audience.Friends>(stored[2].Audience);
        Assert.Equal(("First app", "unit-a"), (stored[0].Title, stored[0].SharedApp!.AppId));
        Assert.Equal("markdraw", Assert.Single(stored[0].EmbeddedApps!).AppId);
        Assert.Empty((await alice.Get<IProfile>(new NeuronId(alice.Brain, "os.profile", "profile")).Read()).Posts);

        // Bob sees what is for friends, in his one list, and not what Alice kept to herself.
        await using var bobApp = await bob.Open<FriendsApp>(ct: Ct);
        await bobApp.Send(new RefreshFeed(), Ct);
        bobApp.ShouldDisplayText("Hello from Alice!");
        bobApp.ShouldDisplayText("First app");
        bobApp.ShouldNotDisplayText("Second app");
        bobApp.ShouldNotDisplayText("An app I keep to myself");

        // Alice remixes her own post: the remix is a post of the store, hers only.
        await aliceApp.Send(new FriendsForkPost("post-a", "post-a-remix", "My remix"), Ct);
        aliceApp.ShouldDisplayText("My remix");
        var remix = Assert.Single(await Stored(alice, alice), static post => post.Id == "post-a-remix");
        Assert.Equal(("My remix", "@user/post-a", "unit-a"), (remix.Title, remix.ForkedFrom, remix.SharedApp!.AppId));
        Assert.Empty(Assert.IsType<Audience.Only>(remix.Audience).Accounts);
        await bobApp.Send(new RefreshFeed(), Ct);
        bobApp.ShouldNotDisplayText("My remix");

        // Opening the screen again shows the same list.
        await aliceApp.Send(new FriendsOpen(), Ct);
        aliceApp.ShouldDisplayText("Hello from Alice!");
        aliceApp.ShouldDisplayText("First app");
        aliceApp.ShouldDisplayText("My remix");
    }
}
