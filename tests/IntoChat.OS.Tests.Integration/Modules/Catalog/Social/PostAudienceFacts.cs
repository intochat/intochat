using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps.Social;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.Tests.Integration.Journeys;
using IntoChat.OS.Tests.Support.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Social;

// Who sees a post. A post carries an audience rule (public, friends, or a frozen list of accounts); a post stored before
// the rule existed carries only its frozen account list. The post store applies the rule on every path a person of another
// brain can reach (Since, React, Comment), and asks the roster on each call whether that person is a friend now. The kernel
// admits a person of another brain only with an offer on the store; these facts also show a person the kernel would admit
// being refused by the store itself.
public sealed class PostAudienceFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NeuronId PostsOf(Person author) => new(author.Brain, "social.posts", IPosts.DefaultKey);

    private static IFriends Roster(Person person) => person.Get<IFriends>(FriendsDefaults.Id(person.Brain.Value));

    private static async Task<string[]> Texts(IPosts posts) => [.. (await posts.Since(0, Ct)).Posts.Select(static post => post.Text)];

    private static async Task Eventually(JourneyOS os, Func<Task<bool>> holds, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            await os.Idle();
            if (await holds()) { return; }
            Assert.True(DateTime.UtcNow < deadline, $"Not within ten seconds: {what}");
            await Task.Delay(50, Ct);
        }
    }

    private static async Task<bool> Reads(IPosts posts, string text)
    {
        try { return (await Texts(posts)).Contains(text); }
        catch (UnauthorizedAccessException) { return false; }
    }

    // One post of each audience, published by Alice while Bob, Carol and Dave are her friends.
    private static async Task PublishOneOfEach(Person alice, Person bob)
    {
        var posts = alice.Get<IPosts>(PostsOf(alice));
        await posts.Publish(new PostDraft("legacy list", [bob.Account]), Ct);
        await posts.Publish(new PostDraft("public", [], Audience: new Audience.Public()), Ct);
        await posts.Publish(new PostDraft("friends", [], Audience: new Audience.Friends()), Ct);
        await posts.Publish(new PostDraft("only bob", [], Audience: new Audience.Only([new AccountId(bob.Account)])), Ct);
        await posts.Publish(new PostDraft("owner only", [], Audience: new Audience.Only([])), Ct);
    }

    private static readonly string[] All = ["legacy list", "public", "friends", "only bob", "owner only"];

    // The matrix: every audience value, seen by the owner, by an app and an agent inside the brain, by a friend the post
    // names, by a friend it does not name, by a removed friend and by a stranger.
    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task EachAudienceIsSeenByThoseItAdmitsAndByNoOneElse(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var (alice, bob, carol, dave, mallory) = (os.Person("alice"), os.Person("bob"), os.Person("carol"), os.Person("dave"), os.Person("mallory"));
        await alice.Befriend(bob, Ct);
        await alice.Befriend(carol, Ct);
        await alice.Befriend(dave, Ct);
        await PublishOneOfEach(alice, bob);
        var store = PostsOf(alice);

        // The owner, and what acts for her inside her brain, see every post.
        Assert.Equal(All, await Texts(alice.Get<IPosts>(store)));
        using (alice.EnterAsApp("notes")) { Assert.Equal(All, await Texts(alice.Get<IPosts>(store))); }
        using (os.Runtime.Edge.Enter(os.Runtime.Edge.Agent(new Actor.Person(alice.Name, alice.Account), alice.Brain)))
        {
            Assert.Equal(All, await Texts(os.Runtime.Get<IPosts>(store)));
        }

        // A friend the posts name.
        await Eventually(os, () => Reads(bob.Get<IPosts>(store), "friends"), "Bob holds an offer on Alice's posts");
        Assert.Equal(["legacy list", "public", "friends", "only bob"], await Texts(bob.Get<IPosts>(store)));

        // A friend no list names: the two rules that mean "my friends", and nothing addressed to somebody else.
        await Eventually(os, () => Reads(carol.Get<IPosts>(store), "friends"), "Carol holds an offer on Alice's posts");
        Assert.Equal(["public", "friends"], await Texts(carol.Get<IPosts>(store)));

        // A removed friend: nothing, whatever the audience, on every path.
        await Eventually(os, () => Reads(dave.Get<IPosts>(store), "friends"), "Dave holds an offer on Alice's posts");
        var friendsPost = (await alice.Get<IPosts>(store).Since(0, Ct)).Posts.Single(static post => post.Text == "friends").Id;
        await Roster(alice).Unfriend(new AccountId(dave.Account));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => dave.Get<IPosts>(store).Since(0, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => dave.Get<IPosts>(store).React(friendsPost, "like", Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => dave.Get<IPosts>(store).Comment(friendsPost, "hi", Ct));

        // A stranger brain: nothing.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mallory.Get<IPosts>(store).Since(0, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mallory.Get<IPosts>(store).React(friendsPost, "like", Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mallory.Get<IPosts>(store).Comment(friendsPost, "hi", Ct));

        // Nobody but the brain's own side publishes.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bob.Get<IPosts>(store).Publish(new PostDraft("mine now", []), Ct));
        var seen = (await alice.Get<IPosts>(store).Since(0, Ct)).Posts;
        Assert.Equal(All, seen.Select(static post => post.Text));
        Assert.All(seen, static post => Assert.Empty(post.Reactions));
        Assert.All(seen, static post => Assert.Empty(post.Comments));
    }

    // The kernel's gate is the offer. A person who holds an offer and is not a friend passes the gate; the store refuses
    // them itself, on every path, for every audience. The host's friend check answers yes to everyone while the offer is
    // made and asks the rosters again afterwards.
    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task AnOfferIsNotEnoughWithoutFriendship(NeuronRuntimeKind kind)
    {
        var everyone = new Switch();
        await using var os = await Journey.Start(
            kind,
            fakes => fakes.Configure(services => services.AddSingleton<IFriendCheck>(
                host => new YesWhile(everyone, new FriendsFriendCheck(host.GetRequiredService<DigitalBrain.Neurons.INeuronProxies>())))),
            Ct);
        var (alice, bob, mallory) = (os.Person("alice"), os.Person("bob"), os.Person("mallory"));
        await alice.Befriend(bob, Ct);
        var store = PostsOf(alice);
        everyone.On = true;
        // Every audience names Mallory or means everybody: only friendship is missing.
        var posts = alice.Get<IPosts>(store);
        await posts.Publish(new PostDraft("legacy list", [mallory.Account]), Ct);
        await posts.Publish(new PostDraft("public", [], Audience: new Audience.Public()), Ct);
        await posts.Publish(new PostDraft("friends", [], Audience: new Audience.Friends()), Ct);
        var only = await posts.Publish(new PostDraft("only mallory", [], Audience: new Audience.Only([new AccountId(mallory.Account)])), Ct);
        everyone.On = false;
        using (os.Runtime.Edge.EnterKernel(alice.Brain))
        {
            var offers = os.Runtime.Get<IOffers>(new NeuronId(alice.Brain, "component.offers", IOffers.DefaultKey));
            Assert.Equal(Role.Editor, await offers.RoleOf(store, mallory.Account, Ct));
        }

        var visitor = mallory.Get<IPosts>(store);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => visitor.Since(0, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => visitor.React(only, "like", Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => visitor.Comment(only, "hi", Ct));
        Assert.All((await posts.Since(0, Ct)).Posts, static post => { Assert.Empty(post.Reactions); Assert.Empty(post.Comments); });

        // Friendship was the only thing missing.
        await alice.Befriend(mallory, Ct);
        Assert.Equal(["legacy list", "public", "friends", "only mallory"], await Texts(visitor));
    }

    // A post for "my friends" follows the roster: a friend made after it was published sees it, a removed friend does not,
    // and a friend made again sees it again. A list is frozen: a later friend is not on it.
    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task APostForFriendsFollowsTheRosterAndAListIsFrozen(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var (alice, bob, carol) = (os.Person("alice"), os.Person("bob"), os.Person("carol"));
        await alice.Befriend(bob, Ct);
        await PublishOneOfEach(alice, bob);
        var store = PostsOf(alice);

        // Carol is no friend yet.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => carol.Get<IPosts>(store).Since(0, Ct));

        // Carol redeems Alice's code: Alice's roster records the friend during a call from Carol's brain, and Alice's post
        // store, which only hears of it, lets Carol in.
        await alice.Befriend(carol, Ct);
        await Eventually(os, () => Reads(carol.Get<IPosts>(store), "friends"), "the post store heard of the new friend and offered itself");
        Assert.Equal(["public", "friends"], await Texts(carol.Get<IPosts>(store)));

        await Roster(alice).Unfriend(new AccountId(bob.Account));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bob.Get<IPosts>(store).Since(0, Ct));

        // Friends again, this time with Alice redeeming Bob's code: the lists that name Bob still do.
        await bob.Befriend(alice, Ct);
        await Eventually(os, () => Reads(bob.Get<IPosts>(store), "only bob"), "the post store offered itself to the friend made again");
        Assert.Equal(["legacy list", "public", "friends", "only bob"], await Texts(bob.Get<IPosts>(store)));
    }

    // The post store has been asleep since the posts were published. A friend's read wakes it.
    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task AFriendsReadWakesASleepingStore(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var (alice, bob) = (os.Person("alice"), os.Person("bob"));
        await alice.Befriend(bob, Ct);
        await PublishOneOfEach(alice, bob);
        var store = PostsOf(alice);
        await Eventually(os, () => Reads(bob.Get<IPosts>(store), "friends"), "Bob holds an offer on Alice's posts");

        await os.Runtime.Deactivate(store);
        Assert.Equal(["legacy list", "public", "friends", "only bob"], await Texts(bob.Get<IPosts>(store)));
    }

    // A friend reacts to and comments on what they can see. A post they cannot see answers like a post that is not there.
    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task AHiddenPostAnswersLikeAMissingOne(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var (alice, bob) = (os.Person("alice"), os.Person("bob"));
        await alice.Befriend(bob, Ct);
        await PublishOneOfEach(alice, bob);
        var store = PostsOf(alice);
        var mine = (await alice.Get<IPosts>(store).Since(0, Ct)).Posts;
        var (hidden, shown) = (mine.Single(static post => post.Text == "owner only").Id, mine.Single(static post => post.Text == "only bob").Id);

        var visitor = bob.Get<IPosts>(store);
        await visitor.React(shown, "like", Ct);
        await visitor.Comment(shown, "nice", Ct);
        var missing = await Assert.ThrowsAsync<KeyNotFoundException>(() => visitor.React("no-such-post", "like", Ct));
        var refused = await Assert.ThrowsAsync<KeyNotFoundException>(() => visitor.React(hidden, "like", Ct));
        Assert.Equal(missing.Message.Replace("no-such-post", hidden, StringComparison.Ordinal), refused.Message);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => visitor.Comment(hidden, "let me in", Ct));

        var after = (await alice.Get<IPosts>(store).Since(0, Ct)).Posts;
        Assert.Equal(bob.Account, Assert.Single(after.Single(post => post.Id == shown).Reactions).Account);
        Assert.Equal("nice", Assert.Single(after.Single(post => post.Id == shown).Comments).Text);
        Assert.Empty(after.Single(post => post.Id == hidden).Reactions);
        Assert.Empty(after.Single(post => post.Id == hidden).Comments);
    }

    // What a post can hold: a chosen id, a title, an app with its files, embedded apps, a fork trace, a date. Publishing
    // under the same id replaces the post and keeps what readers added to it.
    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task APostHoldsWhatAProfilePostHeldAndIsReplacedByItsId(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var (alice, bob) = (os.Person("alice"), os.Person("bob"));
        await alice.Befriend(bob, Ct);
        var posts = alice.Get<IPosts>(PostsOf(alice));
        var at = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var draft = new PostDraft(
            "An app to fork",
            [],
            new AppDefinitionShare("customer-notes", "Customer Notes", [new SharedSourceFile("App.cs", "// unit source")]),
            Id: "post-notes",
            Title: "Notes",
            Audience: new Audience.Friends(),
            EmbeddedApps: [new PostEmbeddedApp("main", "customer-notes", "Customer Notes", "alice/app/customer-notes")],
            ForkedFrom: "@ada/post-1",
            CreatedAt: at,
            TemplateSource: "// template");

        Assert.Equal("post-notes", await posts.Publish(draft, Ct));
        await bob.Get<IPosts>(PostsOf(alice)).React("post-notes", "like", Ct);
        Assert.Equal("post-notes", await posts.Publish(draft with { Text = "An app to fork, again", CreatedAt = null }, Ct));

        var stored = Assert.Single((await posts.Since(0, Ct)).Posts);
        Assert.Equal(("post-notes", "An app to fork, again", "Notes", "@ada/post-1", "// template", at), (stored.Id, stored.Text, stored.Title, stored.ForkedFrom, stored.TemplateSource, stored.CreatedAt));
        Assert.Equal(2, stored.Sequence);
        Assert.IsType<Audience.Friends>(stored.Audience);
        Assert.Equal("customer-notes", stored.SharedApp!.AppId);
        Assert.Equal("// unit source", Assert.Single(stored.SharedApp.Files).Content);
        Assert.Equal("alice/app/customer-notes", Assert.Single(stored.EmbeddedApps!).TargetNeuronUri);
        Assert.Equal(bob.Account, Assert.Single(stored.Reactions).Account);
        // A reader with a cursor gets the new version, once.
        var seenByBob = Assert.Single((await bob.Get<IPosts>(PostsOf(alice)).Since(1, Ct)).Posts);
        Assert.Equal("An app to fork, again", seenByBob.Text);
    }

    // The post store of a brain as the first version wrote it (the pinned PostsState.before-audience.json): every post
    // reads back, is shown to the accounts its frozen list names and to no one else, and the store goes on from there.
    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task PostsStoredByTheFirstVersionStillReadAndKeepTheirFrozenAudience(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var (ada, ben, eve, kim) = (os.Person("ada"), os.Person("ben"), os.Person("eve"), os.Person("kim"));
        var store = PostsOf(ada);
        await os.Runtime.SeedState(store, FirstVersionPosts, Ct);
        await ada.Befriend(ben, Ct);
        await ada.Befriend(kim, Ct);
        // What the first version did when it published to Ben: it offered the store to him.
        await ada.Offers.Offer(store, ben.Account, Role.Editor, Ct);
        await ada.Offers.Offer(store, kim.Account, Role.Editor, Ct);

        var mine = (await ada.Get<IPosts>(store).Since(0, Ct)).Posts;
        Assert.Equal(["p1", "p2"], mine.Select(static post => post.Id));
        Assert.All(mine, static post => Assert.Null(post.Audience));
        Assert.Equal("ben", Assert.Single(mine[0].Reactions).Account);
        Assert.Equal("Nice", Assert.Single(mine[0].Comments).Text);
        Assert.Equal("customer-notes", mine[1].SharedApp!.AppId);

        // Ben is on both lists. Kim is a friend with an offer and on neither. Eve is on a list and is no friend.
        Assert.Equal(["p1", "p2"], (await ben.Get<IPosts>(store).Since(0, Ct)).Posts.Select(static post => post.Id));
        Assert.Empty((await kim.Get<IPosts>(store).Since(0, Ct)).Posts);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => eve.Get<IPosts>(store).Since(0, Ct));

        // The sequence goes on after the stored one.
        await ada.Get<IPosts>(store).Publish(new PostDraft("after the upgrade", [ben.Account]), Ct);
        Assert.Equal([1L, 2L, 3L], (await ada.Get<IPosts>(store).Since(0, Ct)).Posts.Select(static post => post.Sequence));
    }

    // tests/IntoChat.OS.Tests.Unit/Modules/Catalog/Friends/Golden/PostsState.before-audience.json, as pinned by 84b289787.
    private const string FirstVersionPosts = """{"posts":[{"id":"p1","author":"ada","text":"Hello Ben","audienceAccounts":["ben"],"reactions":[{"account":"ben","kind":"like","at":"2026-01-02T03:05:05+00:00"}],"comments":[{"id":"c1","author":"ben","text":"Nice","at":"2026-01-02T03:06:05+00:00"}],"sequence":1,"createdAt":"2026-01-02T03:04:05+00:00","sharedApp":null},{"id":"p2","author":"ada","text":"My app","audienceAccounts":["ben","eve"],"reactions":[],"comments":[],"sequence":2,"createdAt":"2026-01-02T04:04:05+00:00","sharedApp":{"appId":"customer-notes","title":"Customer Notes","files":[{"path":"App.cs","content":"// unit source"}]}}],"nextSequence":2}""";

    private sealed class Switch
    {
        public volatile bool On;
    }

    private sealed class YesWhile(Switch everyone, IFriendCheck rosters) : IFriendCheck
    {
        public Task<bool> IsFriend(BrainId owner, string account, CancellationToken ct = default)
            => everyone.On ? Task.FromResult(true) : rosters.IsFriend(owner, account, ct);
    }
}
