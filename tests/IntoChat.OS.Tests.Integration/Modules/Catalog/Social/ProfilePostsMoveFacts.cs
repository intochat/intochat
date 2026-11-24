using System.Diagnostics;
using System.Text;
using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Invocation;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps.Social;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.Tests.Integration.Journeys;
using IntoChat.OS.Tests.Support.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Social;

// The profile kept a list of posts of its own (ProfileState.Posts), which no other brain could read. The post store
// (social.posts) copies that list into itself once, on the first call of any kind it gets after the upgrade, and from then
// on it is the only place a post is written to. These facts seed both stores as an earlier version left them (the stored
// JSON, before any neuron wakes) and read back what the brain holds afterwards.
public sealed class ProfilePostsMoveFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Day0 = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static NeuronId PostsOf(Person author) => new(author.Brain, "social.posts", IPosts.DefaultKey);

    private static NeuronId ProfileOf(Person person) => new(person.Brain, "os.profile", "profile");

    private static Task Seed(JourneyOS os, NeuronId id, object state)
        => os.Runtime.SeedState(id, Encoding.UTF8.GetString(RuntimeProtocol.SerializeToUtf8Bytes(state, state.GetType())), Ct);

    private static ProfileState ProfileWith(params ProfilePostItem[] newestFirst) => new("ada", "Builds things", ["files"], newestFirst, "Ada");

    private static ProfilePostItem Kept(string id, int day, string text, Audience? audience = null, string circle = "")
        => new(id, text, Day0.AddDays(day), circle) { Audience = audience };

    // A profile post with everything a profile post could hold.
    private static ProfilePostItem KeptApp(string id, int day) => new(
        id,
        "An app to fork",
        Day0.AddDays(day),
        "friends",
        "Notes",
        "customer-notes",
        "// App.cs",
        [new PostEmbeddedApp("main", "customer-notes", "Customer Notes", "ada/app/customer-notes")],
        "@ben/post-9",
        new Dictionary<string, string> { ["spec.md"] = "# Notes", ["App.cs"] = "// App.cs" },
        "ada")
    { Audience = new Audience.Only([new AccountId("ben")]) };

    // The post store as the first version wrote it: a frozen account list and nothing else.
    private static SocialPost Stored(string id, long sequence, string text, params string[] accounts)
        => new(id, "ada", text, accounts, [], [], sequence, Day0.AddDays(10).AddMinutes(sequence));

    private static async Task<SocialPost[]> Read(Person reader, Person author, long cursor = 0)
        => (await reader.Get<IPosts>(PostsOf(author)).Since(cursor, Ct)).Posts;

    private static string Line(SocialPost post) => $"{post.Sequence}:{post.Id}";

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task PostsInBothStoresAreKeptAndAnIdTheStoreHoldsIsTheStores(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var ada = os.Person("ada");
        var held = Stored("post-1", 2, "the store's post-1", "ben") with { Reactions = [new PostReaction("ben", "like", Day0)] };
        await Seed(os, PostsOf(ada), new PostsState([Stored("p1", 1, "Hello Ben", "ben"), held], 2));
        var profile = ProfileWith(
            KeptApp("post-3", 3),
            Kept("post-2", 2, "For friends", new Audience.Friends()),
            Kept("", 2, "No id"),
            Kept("post-1", 1, "the profile's post-1", new Audience.Public()),
            Kept("post-2", 1, "An older entry with the same id"),
            Kept("post-0", 0, "From before audiences", circle: "family"));
        await Seed(os, ProfileOf(ada), profile);

        var posts = await Read(ada, ada);

        // What the store held keeps its place; the profile's posts follow, oldest first, under their own ids and dates.
        Assert.Equal(["1:p1", "2:post-1", "3:post-0", "4:post-2", "5:post-3"], posts.Select(Line));
        Assert.Equal(Day0, posts[2].CreatedAt);
        Assert.Equal(Day0.AddDays(2), posts[3].CreatedAt);
        Assert.Equal("For friends", posts[3].Text);
        // The id both stores held: the store's post, untouched. The profile's is not copied.
        Assert.Equal(("the store's post-1", "ben"), (posts[1].Text, Assert.Single(posts[1].Reactions).Account));
        Assert.Null(posts[1].Audience);

        // Everything a profile post held came along.
        var app = posts[4];
        Assert.Equal(("ada", "An app to fork", "Notes", "@ben/post-9", Day0.AddDays(3)), (app.Author, app.Text, app.Title, app.ForkedFrom, app.CreatedAt));
        Assert.Equal(("customer-notes", "Notes"), (app.SharedApp!.AppId, app.SharedApp.Title));
        Assert.Equal(["App.cs:// App.cs", "spec.md:# Notes"], app.SharedApp.Files.Select(static file => $"{file.Path}:{file.Content}"));
        Assert.Equal("ada/app/customer-notes", Assert.Single(app.EmbeddedApps!).TargetNeuronUri);
        Assert.Null(app.TemplateSource);

        // Every copied post is for its owner only, whatever the profile said about it.
        Assert.All(posts[2..], static post =>
        {
            Assert.Empty(Assert.IsType<Audience.Only>(post.Audience).Accounts);
            Assert.Empty(post.AudienceAccounts);
            Assert.Empty(post.Reactions);
        });

        // Nothing was taken out of the profile: a brain that goes back to the earlier version still finds its posts there.
        var after = await ada.Get<IProfile>(ProfileOf(ada)).Read();
        Assert.Equal(profile.Posts.Select(static post => $"{post.Id}|{post.Content}|{post.CreatedAt:O}"), after.Posts.Select(static post => $"{post.Id}|{post.Content}|{post.CreatedAt:O}"));
        Assert.IsType<Audience.Friends>(after.Posts[1].Audience);
        Assert.Equal("// App.cs", after.Posts[0].UnitFiles!["App.cs"]);
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task PostsOnlyInTheProfileAreCopiedInOrderOfDate(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var ada = os.Person("ada");
        // Two posts with one date: the profile's list is newest first, so the later entry is the older post.
        await Seed(os, ProfileOf(ada), ProfileWith(Kept("c", 5, "c"), Kept("b2", 1, "b2"), Kept("b1", 1, "b1"), Kept("a", 0, "a")));

        Assert.Equal(["1:a", "2:b1", "3:b2", "4:c"], (await Read(ada, ada)).Select(Line));
        await ada.Get<IPosts>(PostsOf(ada)).Publish(new PostDraft("new", []), Ct);
        Assert.Equal([1L, 2L, 3L, 4L, 5L], (await Read(ada, ada)).Select(static post => post.Sequence));
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task PostsOnlyInTheStoreStayAsTheyAre(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var ada = os.Person("ada");
        await Seed(os, PostsOf(ada), new PostsState([Stored("p1", 1, "one", "ben"), Stored("p2", 2, "two")], 2));

        var posts = await Read(ada, ada);
        Assert.Equal(["1:p1", "2:p2"], posts.Select(Line));
        Assert.All(posts, static post => Assert.Null(post.Audience));
        await ada.Get<IPosts>(PostsOf(ada)).Publish(new PostDraft("three", []), Ct);
        Assert.Equal([1L, 2L, 3L], (await Read(ada, ada)).Select(static post => post.Sequence));
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ABrainWithNoPostsAnywhereStartsEmpty(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var ada = os.Person("ada");

        Assert.Empty(await Read(ada, ada));
        await ada.Get<IPosts>(PostsOf(ada)).Publish(new PostDraft("first", []), Ct);
        Assert.Equal(["1:" + (await Read(ada, ada))[0].Id], (await Read(ada, ada)).Select(Line));
    }

    // Ten times what a profile could hold (it kept 200), each with a source file. The copy is one pass and one write: a
    // copy that wrote the store once per post, or searched the store once per post, would not finish in this time.
    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ALargeProfileIsCopiedInOnePass(NeuronRuntimeKind kind)
    {
        const int Count = 2000;
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var ada = os.Person("ada");
        var source = new string('x', 2000);
        // Newest first, as the profile kept them; every seventh date repeats.
        var kept = Enumerable.Range(0, Count)
            .Select(index => new ProfilePostItem($"post-{index:D5}", $"text {index}", Day0.AddMinutes(index - index % 7), "", UnitFiles: new Dictionary<string, string> { ["App.cs"] = source }))
            .Reverse()
            .ToArray();
        await Seed(os, PostsOf(ada), new PostsState([.. Enumerable.Range(1, 100).Select(index => Stored($"p{index}", index, "held"))], 100));
        await Seed(os, ProfileOf(ada), ProfileWith(kept));

        var watch = Stopwatch.StartNew();
        var posts = await Read(ada, ada);
        watch.Stop();

        Assert.Equal(100 + Count, posts.Length);
        Assert.Equal(Enumerable.Range(1, 100 + Count).Select(static index => (long)index), posts.Select(static post => post.Sequence));
        Assert.Equal(Enumerable.Range(0, Count).Select(static index => $"post-{index:D5}"), posts[100..].Select(static post => post.Id));
        Assert.All(posts[100..], post => Assert.Equal(source, Assert.Single(post.SharedApp!.Files).Content));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"Copying {Count} profile posts took {watch.Elapsed}.");

        // And it was done once: the next read copies nothing.
        Assert.Equal(100 + Count, (await Read(ada, ada)).Length);
    }

    // The copy reads the profile, then asks who owns the brain, then writes. Here the second step fails once: what a stop
    // between the read and the write leaves behind. Nothing was written, the store still serves what it held, and the next
    // call makes the same copy an uninterrupted one would have made.
    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ACopyInterruptedBetweenReadingAndWritingStartsOverOnTheNextCall(NeuronRuntimeKind kind)
    {
        var owners = new Owners();
        await using var os = await Journey.Start(kind, fakes => fakes.Configure(services => services.AddSingleton<IBrainOwners>(owners)), Ct);
        var ada = os.Person("ada");
        await Seed(os, PostsOf(ada), new PostsState([Stored("p1", 1, "held")], 1));
        await Seed(os, ProfileOf(ada), ProfileWith(Kept("b", 1, "b"), Kept("a", 0, "a")));

        owners.Failing = true;
        Assert.Equal(["1:p1"], (await Read(ada, ada)).Select(Line));
        Assert.True(owners.Failed > 0, "The copy did not reach the step that was to fail.");
        // Still nothing written, also for a store that wakes again.
        await os.Runtime.Deactivate(PostsOf(ada));
        Assert.Equal(["1:p1"], (await Read(ada, ada)).Select(Line));

        owners.Failing = false;
        Assert.Equal(["1:p1", "2:a", "3:b"], (await Read(ada, ada)).Select(Line));
        Assert.All((await Read(ada, ada))[1..], static post => Assert.Equal("ada", post.Author));
    }

    // Once copied, never again: not on the next call, not after the store slept, not when a post was replaced or added.
    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task TheSecondCallCopiesNothing(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var ada = os.Person("ada");
        await Seed(os, ProfileOf(ada), ProfileWith(Kept("b", 1, "b"), Kept("a", 0, "a")));
        var store = ada.Get<IPosts>(PostsOf(ada));

        Assert.Equal(["1:a", "2:b"], (await Read(ada, ada)).Select(Line));
        Assert.Equal(["1:a", "2:b"], (await Read(ada, ada)).Select(Line));
        // The owner replaces a copied post: it is hers to change, and the copy does not bring the old one back.
        await store.Publish(new PostDraft("a, rewritten", [], Id: "a"), Ct);
        await os.Runtime.Deactivate(PostsOf(ada));
        await store.Publish(new PostDraft("c", [], Id: "c"), Ct);

        var posts = await Read(ada, ada);
        Assert.Equal(["2:b", "3:a", "4:c"], posts.Select(Line));
        Assert.Equal(("a, rewritten", Day0), (posts[1].Text, posts[1].CreatedAt));
    }

    // The profile's own post methods are how a post was written before. After the copy a post written to the profile's
    // list would be seen by nothing, so they store nothing and say where to publish; that holds before the copy too.
    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task APostWrittenThroughTheProfileIsRefusedAndNothingIsStored(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var (ada, eve) = (os.Person("ada"), os.Person("eve"));
        await Seed(os, ProfileOf(ada), ProfileWith(Kept("a", 0, "a")));
        var profile = ada.Get<IProfile>(ProfileOf(ada));

        // Eve's brain has not copied anything yet; Ada's has.
        Assert.Equal(["1:a"], (await Read(ada, ada)).Select(Line));
        foreach (var person in (Person[])[ada, eve])
        {
            var old = person.Get<IProfile>(ProfileOf(person));
            var refusals = new[]
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => old.PublishAppPost(Kept("late", 9, "written the old way"))),
                await Assert.ThrowsAsync<InvalidOperationException>(() => old.PublishPost("late-2", "written the old way")),
                await Assert.ThrowsAsync<InvalidOperationException>(() => old.ForkPost("a", "late-3", "Remix")),
            };
            Assert.All(refusals, static refusal => Assert.Contains("IPosts.Publish", refusal.Message, StringComparison.Ordinal));
        }

        Assert.Equal(["a"], (await profile.Read()).Posts.Select(static post => post.Id));
        Assert.Empty((await eve.Get<IProfile>(ProfileOf(eve)).Read()).Posts);
        Assert.Equal(["1:a"], (await Read(ada, ada)).Select(Line));
        Assert.Empty(await Read(eve, eve));

        // The same post through the post store, under the id the old path would have used.
        await ada.Get<IPosts>(PostsOf(ada)).Publish(new PostDraft("written the new way", [], Id: "late"), Ct);
        Assert.Equal(["1:a", "2:late"], (await Read(ada, ada)).Select(Line));
    }

    // Ben is Ada's friend and could read the posts her store listed him on. His read is the call that makes Ada's brain
    // copy her profile posts. He sees what he saw before, then and afterwards, and none of the copied posts, on any path;
    // two of those the profile had marked "friends" and "public".
    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task AFriendSeesTheSamePostsBeforeAndAfterTheCopyAndNoneOfTheCopiedOnes(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var (ada, ben) = (os.Person("ada"), os.Person("ben"));
        await Seed(os, PostsOf(ada), new PostsState([Stored("p1", 1, "Hello Ben", "ben"), Stored("p2", 2, "For Eve", "eve"), Stored("p3", 3, "Ben and Eve", "ben", "eve")], 3));
        await Seed(os, ProfileOf(ada), ProfileWith(
            KeptApp("post-3", 3),
            Kept("post-2", 2, "For friends", new Audience.Friends()),
            Kept("post-1", 1, "For everyone", new Audience.Public())));
        await ada.Befriend(ben, Ct);
        // What the first version did when it published to Ben.
        await ada.Offers.Offer(PostsOf(ada), ben.Account, Role.Editor, Ct);

        // Before anyone else touched the store.
        Assert.Equal(["1:p1", "3:p3"], (await Read(ben, ada)).Select(Line));

        // The copy is made: the owner finds her profile posts in the store on her first look.
        Assert.Equal(["1:p1", "2:p2", "3:p3", "4:post-1", "5:post-2", "6:post-3"], (await Read(ada, ada)).Select(Line));

        // After.
        Assert.Equal(["1:p1", "3:p3"], (await Read(ben, ada)).Select(Line));
        Assert.Empty(await Read(ben, ada, cursor: 3));
        var visitor = ben.Get<IPosts>(PostsOf(ada));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => visitor.React("post-2", "like", Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => visitor.Comment("post-1", "hi", Ct));
        Assert.All((await Read(ada, ada))[3..], static post => { Assert.Empty(post.Reactions); Assert.Empty(post.Comments); });

        // A post is shown to friends when its owner publishes it so: under the same id, through the post store.
        await ada.Get<IPosts>(PostsOf(ada)).Publish(new PostDraft("For friends", [], Id: "post-2", Audience: new Audience.Friends()), Ct);
        Assert.Equal(["1:p1", "3:p3", "7:post-2"], (await Read(ben, ada)).Select(Line));
    }

    // The portable archive keeps its file shape: the profile's post list in it now comes from the post store, and an
    // archive's posts go into the post store, for the owner only, under their ids and dates. An archive written before
    // posts moved has the same shape and imports the same way.
    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task TheArchiveCarriesThePostStoresPostsInTheShapeItAlwaysHad(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var (ada, bob) = (os.Person("ada"), os.Person("bob"));
        await Seed(os, ProfileOf(ada), ProfileWith(KeptApp("post-3", 3), Kept("post-1", 1, "Hello", new Audience.Friends())));
        var store = ada.Get<IPosts>(PostsOf(ada));
        await store.Publish(new PostDraft("After the move", [], Id: "post-9", Audience: new Audience.Friends()), Ct);

        var snapshot = PortableBrainArchive.FromJson(PortableBrainArchive.ToJson(await ExportOf(ada, withPosts: true)));
        Assert.Equal(["post-9", "post-3", "post-1"], snapshot.Profile.Posts.Select(static post => post.Id));
        var app = snapshot.Profile.Posts[1];
        Assert.Equal(("An app to fork", "Notes", "customer-notes", "// App.cs", "@ben/post-9", Day0.AddDays(3)), (app.Content, app.Title, app.TemplateAppId, app.TemplateSource, app.ForkedFrom, app.CreatedAt));
        Assert.Equal("# Notes", app.UnitFiles!["spec.md"]);
        Assert.Equal("customer-notes", Assert.Single(app.EmbeddedApps!).AppId);
        Assert.IsType<Audience.Friends>(snapshot.Profile.Posts[0].Audience);

        // Into another brain: the post store gets them, the profile's own list stays empty, and no one else sees them.
        await ImportInto(bob, snapshot, withPosts: true);
        var restored = await Read(bob, bob);
        Assert.Equal(["1:post-1", "2:post-3", "3:post-9"], restored.Select(Line));
        Assert.Equal(Day0.AddDays(3), restored[1].CreatedAt);
        Assert.Equal(["App.cs", "spec.md"], restored[1].SharedApp!.Files.Select(static file => file.Path));
        Assert.All(restored, static post => Assert.Empty(Assert.IsType<Audience.Only>(post.Audience).Accounts));
        Assert.Empty((await bob.Get<IProfile>(ProfileOf(bob)).Read()).Posts);

        // An archive as it was written before posts moved: the profile's list itself.
        var carol = os.Person("carol");
        var earlier = PortableBrainArchive.FromJson(PortableBrainArchive.ToJson(await ExportOf(ada, withPosts: false)));
        Assert.Equal(["post-3", "post-1"], earlier.Profile.Posts.Select(static post => post.Id));
        var lost = await Assert.ThrowsAsync<InvalidOperationException>(() => ImportInto(carol, earlier, withPosts: false));
        Assert.Contains("2 post(s)", lost.Message, StringComparison.Ordinal);
        await ImportInto(carol, earlier, withPosts: true);
        Assert.Equal(["1:post-1", "2:post-3"], (await Read(carol, carol)).Select(Line));
    }

    private static Task<PortableBrainSnapshot> ExportOf(Person person, bool withPosts) => PortableBrainArchive.ExportAsync(
        person.Brain.Value,
        person.Get<IUserSettings>(new NeuronId(person.Brain, "os.settings", "settings")),
        person.Get<IProfile>(ProfileOf(person)),
        person.Get<ISynapseStudio>(ISynapseStudio.Key(person.Brain.Value)),
        withPosts ? person.Get<IPosts>(PostsOf(person)) : null);

    private static Task ImportInto(Person person, PortableBrainSnapshot snapshot, bool withPosts) => PortableBrainArchive.ImportAsync(
        snapshot,
        person.Get<IUserSettings>(new NeuronId(person.Brain, "os.settings", "settings")),
        person.Get<IProfile>(ProfileOf(person)),
        person.Get<ISynapseStudio>(ISynapseStudio.Key(person.Brain.Value)),
        withPosts ? person.Get<IPosts>(PostsOf(person)) : null);

    // Who owns a brain, as the journey host answers it (the account of the same name), with a switch that makes it fail.
    private sealed class Owners : IBrainOwners
    {
        public volatile bool Failing;
        public volatile int Failed;

        public Task<string?> OwnerAccount(BrainId brain, CancellationToken cancellationToken = default)
        {
            if (!Failing) { return Task.FromResult<string?>(brain.Value); }
            Failed++;
            throw new IOException("The owner of the brain could not be read.");
        }
    }
}
