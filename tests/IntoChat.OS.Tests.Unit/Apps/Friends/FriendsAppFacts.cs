using DigitalBrain;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Friends;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.UI;

namespace IntoChat.OS.Apps.Tests.Unit.Friends;

public sealed class FriendsAppFacts
{
    public static IEnumerable<TheoryDataRow<ScreenOperationCase>> FriendsOperations
        => ScreenBaseline.Cases(typeof(FriendsApp), os => os.Register(typeof(AppsModule).Assembly));

    [Theory, MemberData(nameof(FriendsOperations))]
    public Task FriendsBaseline(ScreenOperationCase operation) => ScreenBaseline.RunAsync(operation);

    [Fact]
    public async Task FriendsScenariosPass()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("test", "test", new BrainId("test")));
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        os.Register(typeof(FriendsApp).Assembly);
        var screen = os.Get<IScreen>(new NeuronId(new BrainId("test"), "app.friends", "default"));
        Assert.NotNull(await screen.Read());
    }

    [Fact]
    public async Task MultipleAppPostsRenderUniqueButtonKeysAndValidViewportUris()
    {
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var friends = await os.Install<FriendsApp>();

        await friends.Request("publish", new FriendsPublish("First post", "friends", "post-a", "First", "unit-a", "markdraw"));
        await friends.Request("publish", new FriendsPublish("Second post", "friends", "post-b", "Second", "unit-b", "data"));

        var view = friends.App.Render();
        var buttons = Nodes(view).OfType<Button>().ToArray();
        Assert.Contains(buttons, b => b.Key == "fork-app-template:post-a");
        Assert.Contains(buttons, b => b.Key == "fork-app-template:post-b");
        Assert.Contains(buttons, b => b.Key == "open-in-creator:post-a");
        Assert.Contains(buttons, b => b.Key == "open-in-creator:post-b");

        var refs = Nodes(view).OfType<NeuronRef>().ToArray();
        Assert.NotEmpty(refs);
        var json = System.Text.Json.JsonSerializer.Serialize(refs);
        Assert.Contains("local/apps/markdraw", json, StringComparison.Ordinal);
        Assert.Contains("local/apps/data", json, StringComparison.Ordinal);
    }

    private static Node Preview(FriendsAppState state) => IntoChat.OS.UI.ScreenPreview.With<FriendsApp, Node>(app => app.PreviewNode(state));

    private static FriendsAppState WithFriends() => new FriendsAppState() with
    {
        Roster =
        [
            new Friend(new AccountId("ben-account"), new BrainId("ben"), "Ben", default),
        ],
    };

    [Fact]
    public void Sharing_and_removing_are_offered_for_every_friend_in_the_roster()
    {
        var view = Preview(WithFriends());

        var keys = Nodes(view).OfType<Button>().Select(button => button.Key).ToArray();
        Assert.Contains("share-viewer:ben-account", keys);
        Assert.Contains("share-editor:ben-account", keys);
        Assert.Contains("unfriend:ben-account", keys);
        Assert.DoesNotContain(keys, key => key is not null && key.EndsWith(":", StringComparison.Ordinal));
        Assert.Contains("make-code", keys);
        Assert.Contains("redeem-code", keys);
        Assert.DoesNotContain("add-friend", keys);
    }

    [Fact]
    public void Shares_made_and_received_are_listed_with_what_can_be_done_to_them()
    {
        var state = WithFriends() with
        {
            SharedByMe =
            [
                new WorkspaceShare("s1", "ben-account", "/work/trip", IsFolder: true, ShareRole.Viewer, default),
                new WorkspaceShare("s3", "ben-account", "/work/moved", IsFolder: true, ShareRole.Editor, default, Broken: true),
            ],
            SharedWithMe = [new SharedPointer("ben", "ben-account", "s2", "/work/notes.md", IsFolder: false, ShareRole.Editor)],
            Opened = new FriendsOpenedShare("ben", "s2", "", CanEdit: true, [], "hello", "ben-account", DateTimeOffset.UnixEpoch),
        };

        var view = Preview(state);
        var keys = Nodes(view).OfType<Button>().Select(button => button.Key).ToArray();
        var json = System.Text.Json.JsonSerializer.Serialize(view);

        Assert.Contains("unshare:s1", keys);
        Assert.Contains("unshare:s3", keys);
        Assert.Contains("open-shared:s2", keys);
        Assert.Contains("save-shared:s2", keys);
        Assert.Contains("/work/trip", json, StringComparison.Ordinal);
        Assert.Contains("Broken", json, StringComparison.Ordinal);
        Assert.Contains("last saved by ben-account at 1970-01-01 00:00:00Z", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_viewer_sees_the_text_and_no_save()
    {
        var state = new FriendsAppState() with
        {
            SharedWithMe = [new SharedPointer("ben", "ben-account", "s2", "/work/notes.md", IsFolder: false, ShareRole.Viewer)],
            Opened = new FriendsOpenedShare("ben", "s2", "", CanEdit: false, [], "hello"),
        };

        var keys = Nodes(Preview(state)).OfType<Button>().Select(button => button.Key).ToArray();
        Assert.DoesNotContain("save-shared:s2", keys);
    }

    [Fact]
    public async Task A_step_that_is_refused_keeps_the_screen_and_says_why()
    {
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var friends = await os.Install<FriendsApp>();

        // No person is stamped here, so the friend links refuse.
        await friends.Request("make-code", new FriendsMakeCode());
        Assert.Equal(string.Empty, friends.App.State.FriendCode);
        Assert.StartsWith("Could not make a friend code", friends.App.State.Status, StringComparison.Ordinal);

        await friends.Request("share-folder", new FriendsShareFolder("ben-account", "/work/trip", "viewer"));
        Assert.StartsWith("Could not share that", friends.App.State.Status, StringComparison.Ordinal);
        Assert.Empty(friends.App.State.SharedByMe ?? []);
    }

    private static IEnumerable<Node> Nodes(object? value) => value switch
    {
        null or string => [],
        Node node => [node, .. node.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0).SelectMany(p => Nodes(p.GetValue(node)))],
        View view => Nodes(view.Root),
        System.Collections.IEnumerable items => items.Cast<object?>().SelectMany(Nodes),
        _ => [],
    };

    [Fact]
    public void FriendsAppState_AggregateExplorer_UnfoldsHealthyStateGraphAndRuleGuards()
    {
        var graph = AggregateExplorer<FriendsAppState>.ExploreScreen<FriendsApp>(maxStates: 48).EnsureHealthy();

        Assert.True(graph.IsHealthy, string.Join("\n", graph.Violations));
        Assert.Empty(graph.Violations);
        Assert.Contains(graph.States, s => s.State.SelectedScope == "all");
        Assert.Contains(graph.States, s => s.State.SelectedScope == "public");
        Assert.Contains(graph.States, s => s.State.SelectedScope == "friends");
        Assert.Contains(graph.States, s => s.State.ActiveTab == "roster");
        Assert.Contains(graph.States, s => s.State.ActiveTab == "shared");
        Assert.Contains(graph.States, s => s.State.ActiveTab == "feed");
        Assert.Contains(graph.States, s => s.State.Handle == "ada");

        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_scope", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_tab", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_handle", StringComparer.Ordinal));

        Assert.Contains("digraph FriendsAppState", graph.ToDot(), StringComparison.Ordinal);
        Assert.Contains(FriendsApp.Bundle.Manifest.Operations, op => op.Name == UiCommand.SignalAlias);
    }

    [Fact]
    public async Task FriendsDomainRules_EnforceHandleScopeCircleAndTabValidation()
    {
        var initial = new FriendsAppState();

        var badScope = initial.SelectScope("secret");
        Assert.True(badScope.IsRefused);
        Assert.Contains("invalid_scope", badScope.Rejection!.Rules);

        var badTab = initial.SelectTab("unknown-tab");
        Assert.True(badTab.IsRefused);
        Assert.Contains("invalid_tab", badTab.Rejection!.Rules);

        var badHandle = initial.UpdateProfileDraft("bad handle!");
        Assert.True(badHandle.IsRefused);
        Assert.Contains("invalid_handle", badHandle.Rejection!.Rules);

        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var friends = await os.Install<FriendsApp>();

        await friends.Request("filter-scope", new FriendsFilterScope("secret"));
        Assert.NotNull(friends.App.LastRejection);
        Assert.Contains("invalid_scope", friends.App.LastRejection!.Rules);
        Assert.Equal("all", friends.App.State.SelectedScope);

        await friends.Request("update-bio", new FriendsUpdateBio("bad handle!"));
        Assert.NotNull(friends.App.LastRejection);
        Assert.Contains("invalid_handle", friends.App.LastRejection!.Rules);
        Assert.Equal("user", friends.App.State.Handle);

        await friends.Request("publish", new FriendsPublish("Hello", Scope: "invalid-circle"));
        Assert.NotNull(friends.App.LastRejection);
        Assert.Contains("invalid_circle", friends.App.LastRejection!.Rules);

        var renderedJson = System.Text.Json.JsonSerializer.Serialize(friends.App.Render());
        Assert.Contains("Action refused", renderedJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FriendsApp_UiIntrospector_UnfoldsCleanAccordantStateGraphAndDot()
    {
        var brain = new BrainId("friends-introspect");
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", brain));
        await using var os = TestOS.Local();
        os.Register(typeof(UiModule).Assembly);
        os.Register<AgentContextNeuron>();
        os.Register<UiIntrospectorNeuron>();

        var introspector = os.Get<IUiIntrospector>(IUiIntrospector.NeuronIdOf(brain));
        var snapshot = await introspector.ExploreStateGraph(typeof(FriendsApp).FullName!, maxStates: 48);

        Assert.True(snapshot.IsHealthy, string.Join("\n", snapshot.Violations));
        Assert.Empty(snapshot.Violations);
        Assert.True(snapshot.States.Length >= 6);
        Assert.Contains(snapshot.Transitions, t => t.Accepted);
        Assert.Contains(snapshot.Transitions, t => !t.Accepted && t.BrokenRules.Contains("invalid_scope", StringComparer.Ordinal));
        Assert.Contains(snapshot.Transitions, t => !t.Accepted && t.BrokenRules.Contains("invalid_handle", StringComparer.Ordinal));
        Assert.Contains("digraph FriendsAppState", snapshot.Dot, StringComparison.Ordinal);
    }
}
