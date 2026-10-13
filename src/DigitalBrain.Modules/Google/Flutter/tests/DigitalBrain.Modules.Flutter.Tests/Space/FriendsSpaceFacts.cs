using System.Net;
using System.Net.Http.Json;
using DigitalBrain.Apps;
using DigitalBrain.Circles;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Flame;
using DigitalBrain.Flame.Scene;
using DigitalBrain.Flame.Scene.Signals;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Card;
using DigitalBrain.Flutter.Person;
using DigitalBrain.Flutter.Space;
using DigitalBrain.Flutter.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans;

namespace DigitalBrain.Modules.Flutter.Tests.Space;

public sealed class FriendsSpaceFacts
{
    [Fact(Timeout = 120_000)]
    public async Task FriendsCanBeDerivedInsideBrainCreation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct, checkCreation: true);
        await brain.PersonAsync("new-friend", ct);
        Assert.True(brain.Services.GetRequiredService<CreationFriends>().Completed);
    }

    private sealed class CreationFriends(CircleMembers friends) : ICreationHandler
    {
        public bool Completed { get; private set; }
        public async Task Created(IBrain kernel, Created created, CancellationToken ct)
        {
            await friends.FriendsOf(kernel.GetPrimaryKeyString()).WaitAsync(TimeSpan.FromSeconds(5), ct);
            Completed = true;
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task ACircleMembersProfileAppearsInTheFriendField()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var (alice, bob, _) = await People(brain, ct);
        await Home(brain, alice);
        var scene = await Rebuild(brain, alice, ct, state => state.Definition.Nodes.Any(n => n.Id == bob.Key("profile") && n.Parameters["title"] == "Bob"));
        Assert.Equal("Bob", Assert.Single(scene.Definition.Nodes, n => n.Id == bob.Key("profile")).Parameters["title"]);
    }

    [Fact(Timeout = 120_000)]
    public async Task FocusingAFriendShowsTheirPostsAndOpeningAPostIsTheSameZoom()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var (alice, bob, circle) = await People(brain, ct);
        await Post(brain, bob, circle);
        await Show(brain, alice, bob.Key("profile"));
        var scene = await Rebuild(brain, alice, ct, state => state.Definition.Nodes.Any(n => n.Id == bob.Key("profile") && n.Parameters.ContainsKey("main")) && state.Definition.Nodes.Any(n => n.Id == bob.Key("posts/p1")));
        Assert.Contains(scene.Definition.Nodes, n => n.Id == bob.Key("posts/p1"));
        Assert.Contains("person", Assert.Single(scene.Definition.Nodes, n => n.Id == bob.Key("profile")).Parameters["main"], StringComparison.Ordinal);
        await Show(brain, alice, bob.Key("posts/p1"));
        scene = await Rebuild(brain, alice, ct, state => state.Definition.Nodes.Any(n => n.Id == bob.Key("posts/p1") && n.Parameters.ContainsKey("main")));
        Assert.Contains("card", Assert.Single(scene.Definition.Nodes, n => n.Id == bob.Key("posts/p1")).Parameters["main"], StringComparison.Ordinal);
    }

    [Fact(Timeout = 120_000)]
    public async Task AMemberWhoLeavesVanishesFromTheFieldAndFocusedPosts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var (alice, bob, circle) = await People(brain, ct);
        await Post(brain, bob, circle);
        await Show(brain, alice, bob.Key("posts/p1"));
        Assert.Contains((await Rebuild(brain, alice, ct, state => state.Definition.Nodes.Any(n => n.Id == bob.Key("posts/p1") && n.Parameters.ContainsKey("main")))).Definition.Nodes, n => n.Id == bob.Key("posts/p1"));
        using (alice.AsCallerIn(circle)) { await brain.Get<ICircle>(ICircle.Key(circle)).Remove(bob.AccountId); }
        var scene = await Rebuild(brain, alice, ct, state => !state.Definition.Nodes.Any(n => n.Id == bob.Key("posts/p1")));
        Assert.DoesNotContain(scene.Definition.Nodes, n => n.Id == bob.Key("posts/p1"));
        await Home(brain, alice);
        Assert.DoesNotContain((await Rebuild(brain, alice, ct)).Definition.Nodes, n => n.Id == bob.Key("profile"));
        await Home(brain, bob);
        Assert.DoesNotContain((await Rebuild(brain, bob, ct)).Definition.Nodes, n => n.Id == alice.Key("profile"));
        using (alice.AsCaller())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => brain.Get<IPerson>(bob.Key("profile")).Read());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => brain.Get<ICard>(bob.Key("posts/p1")).Read());
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task LeavingRebuildsBothMembersScenesWithoutAnotherNavigation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var (alice, bob, circle) = await People(brain, ct);
        await Home(brain, alice);
        await Home(brain, bob);
        await Rebuild(brain, alice, ct, state => state.Definition.Nodes.Any(n => n.Id == bob.Key("profile")));
        await Rebuild(brain, bob, ct, state => state.Definition.Nodes.Any(n => n.Id == alice.Key("profile")));
        var aliceScene = brain.Get<IScene>(alice.Key("space"));
        var bobScene = brain.Get<IScene>(bob.Key("space"));
        await using var aliceChanges = await brain.Observe<SceneChanged>(aliceScene, ct);
        await using var bobChanges = await brain.Observe<SceneChanged>(bobScene, ct);
        using (alice.AsCallerIn(circle)) { await brain.Get<ICircle>(ICircle.Key(circle)).Remove(bob.AccountId); }
        do { await aliceChanges.NextAsync(ct: ct); }
        while ((await aliceScene.Read()).Definition.Nodes.Any(n => n.Id == bob.Key("profile")));
        do { await bobChanges.NextAsync(ct: ct); }
        while ((await bobScene.Read()).Definition.Nodes.Any(n => n.Id == alice.Key("profile")));
    }

    [Fact(Timeout = 120_000)]
    public async Task SlotsReadOnlySharedPostsAndRefuseStaleAccessAfterLeaving()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var (alice, bob, circle) = await People(brain, ct);
        await Post(brain, bob, circle);
        using (bob.AsCaller()) { await brain.Get<ICard>(bob.Key("posts/private")).Set("Private", "Not published"); }
        // Even an admitted circle speaker cannot publish another author's reference.
        using (alice.AsCallerIn(circle)) { await brain.Get<ICircleChat>(ICircleChat.Key(circle)).Say("post " + bob.Key("posts/private")); }
        var space = brain.Services.GetRequiredService<IGrainFactory>().GetGrain<ISpace>(alice.BrainId);
        var nodeUrl = $"/brains/{alice.BrainId}/apps/node?kind=person&name={Uri.EscapeDataString(bob.Key("profile"))}";
        Assert.Equal(HttpStatusCode.OK, (await alice.Http.GetAsync(nodeUrl, ct)).StatusCode);
        var activation = new { kind = "collection", name = bob.Key("posts"), action = "activate", value = bob.Key("posts/p1"), revision = 0 };
        Assert.Equal(HttpStatusCode.Accepted, (await alice.Http.PostAsJsonAsync($"/brains/{alice.BrainId}/apps/event", activation, ct)).StatusCode);

        using (alice.AsCaller())
        {
            Assert.Equal("Bob", (await space.ReadProfile(bob.Key("profile"))).DisplayName);
            Assert.Equal("Hello", (await space.ReadPost(bob.Key("posts/p1"))).Title);
            Assert.Equal(bob.Key("posts/p1"), Assert.Single((await space.ReadPosts(bob.Key("posts"))).Definition.Items).Id);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => space.ReadPost(bob.Key("posts/private")));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => space.ReadProfile(bob.Key("apps/private")));
        }
        using (alice.AsCallerIn(circle)) { await brain.Get<ICircle>(ICircle.Key(circle)).Remove(bob.AccountId); }
        using (alice.AsCaller())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => space.ReadProfile(bob.Key("profile")));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => space.ReadPost(bob.Key("posts/p1")));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => space.ReadPosts(bob.Key("posts")));
            Assert.Equal(HttpStatusCode.Forbidden, (await alice.Http.GetAsync(nodeUrl, ct)).StatusCode);
        }
        using (bob.AsCaller())
        {
            var own = brain.Services.GetRequiredService<IGrainFactory>().GetGrain<ISpace>(bob.BrainId);
            Assert.Equal("Bob", (await own.ReadProfile(bob.Key("profile"))).DisplayName);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => own.ReadProfile(alice.Key("profile")));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => space.ReadProfile(bob.Key("profile")));
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task TrustRevocationDeniesReadsBeforeTheCircleSnapshotCommits()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var (alice, bob, circle) = await People(brain, ct);
        await Post(brain, bob, circle);
        await brain.Services.GetRequiredService<IIdentityClient>().RemoveMember(circle, bob.AccountId);
        using (alice.AsCallerIn(circle))
        { Assert.Contains(bob.AccountId, (await brain.Get<ICircle>(ICircle.Key(circle)).State()).Members); }
        using (alice.AsCaller())
        {
            var space = brain.Services.GetRequiredService<IGrainFactory>().GetGrain<ISpace>(alice.BrainId);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => space.ReadProfile(bob.Key("profile")));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => space.ReadPost(bob.Key("posts/p1")));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => space.ReadPosts(bob.Key("posts")));
        }
        Assert.Empty(await brain.Services.GetRequiredService<CircleMembers>().FriendsOf(alice.BrainId));
        Assert.Empty(await brain.Services.GetRequiredService<CircleMembers>().FriendsOf(bob.BrainId));
    }

    private static async Task<(DigitalBrain.Testing.Person Alice, DigitalBrain.Testing.Person Bob, string Circle)> People(ITestBrain brain, CancellationToken ct)
    {
        var alice = await brain.PersonAsync("alice", ct);
        var bob = await brain.PersonAsync("bob", ct);
        string circle;
        using (alice.AsCaller())
        { circle = await brain.Get<ICircleFactory>(ICircleFactory.Key(alice.BrainId)).Create("friends"); }
        using (alice.AsCallerIn(circle)) { await brain.Get<ICircle>(ICircle.Key(circle)).Invite(bob.AccountId); }
        using (bob.AsCallerIn(circle)) { await brain.Get<ICircle>(ICircle.Key(circle)).Accept(); }
        using (bob.AsCaller()) { await brain.Get<IPerson>(bob.Key("profile")).Set("Bob", ""); }
        return (alice, bob, circle);
    }

    private static async Task Post(ITestBrain brain, DigitalBrain.Testing.Person bob, string circle)
    {
        using (bob.AsCaller()) { await brain.Get<ICard>(bob.Key("posts/p1")).Set("Hello", "Hello circle"); }
        using (bob.AsApp(IApp.Key(bob.BrainId, "friends")))
        { await brain.Get<ICircleChat>(ICircleChat.Key(circle)).PublishPost(bob.Key("posts/p1"), "p1"); }
    }

    private static async Task Home(ITestBrain brain, DigitalBrain.Testing.Person person)
    {
        using (person.AsCaller())
        {
            var workspace = brain.Get<IWorkspace>(person.BrainId);
            await workspace.Home(Guid.NewGuid().ToString(), (await workspace.Read()).Revision);
        }
    }

    private static async Task Show(ITestBrain brain, DigitalBrain.Testing.Person person, string key)
    {
        using (person.AsCaller())
        {
            var workspace = brain.Get<IWorkspace>(person.BrainId);
            await workspace.Show(key, Guid.NewGuid().ToString(), (await workspace.Read()).Revision);
        }
    }

    private static async Task<SceneState> Rebuild(ITestBrain brain, DigitalBrain.Testing.Person person, CancellationToken ct, Func<SceneState, bool>? ready = null)
    {
        var scene = brain.Get<IScene>(person.Key("space"));
        await using var changed = await brain.Observe<SceneChanged>(scene, ct);
        await brain.Services.GetRequiredService<IGrainFactory>().GetGrain<ISpace>(person.BrainId).Rebuild();
        SceneState state;
        do { await changed.NextAsync(ct: ct); state = await scene.Read(); } while (ready is not null && !ready(state));
        return state;
    }

    private static Task<ITestBrain> Start(CancellationToken ct, bool checkCreation = false)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DigitalBrain.slnx"))) { root = root.Parent; }
        return TestBrain.Create().WithModule<AppsModule, AppsOptions>(o => o.Features = Path.Combine(root!.FullName, "features"))
            .WithModule<CirclesModule>().WithModule<FlutterModule>().WithModule<FlameModule>()
            .ConfigureServices(s => { if (checkCreation) { s.AddSingleton<CreationFriends>(); s.AddSingleton<ICreationHandler>(p => p.GetRequiredService<CreationFriends>()); } s.RemoveAll<IAppRuntime>(); s.AddSingleton<IAppRuntime, NoRuntime>(); }).StartAsync(ct);
    }

    private sealed class NoRuntime : IAppRuntime
    {
        public Task<IReadOnlyDictionary<string, string>> Deploy(string appKey, Dictionary<string, string> sources, Dictionary<string, string> configuration, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        public Task Retire(string appKey, CancellationToken ct) => Task.CompletedTask;
    }
}
