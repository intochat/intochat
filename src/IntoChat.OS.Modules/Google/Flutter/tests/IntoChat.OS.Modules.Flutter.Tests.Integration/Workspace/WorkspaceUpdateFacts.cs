using System.Text.Json;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using IntoChat.OS.Modules.Flutter.Workspace;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Workspace;

public sealed class WorkspaceUpdateFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task TypedViewsSurviveReactivationAndClosingTheLastLeavesNoFocus()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var scope = brain.Owner.BrainId;
        var workspace = brain.Get<IWorkspace>(scope);
        await Shipped(brain, scope);
        using var caller = brain.Owner.AsApp(scope + "/apps/workspace");
        WorkspaceViewRef[] views = [new("person", scope + "/apps/profile", "full", "person:alice"), new("document", scope + "/apps/files", "full", "document:123")];
        await workspace.Apply(new(views, "person", []), "open", 0);
        await brain.DeactivateAsync(workspace, ct);
        var restored = await workspace.Read();
        Assert.Equal(views, restored.OpenViews);
        Assert.Equal("person", restored.FocusedViewId);
        var empty = await workspace.Apply(new([], null, []), "close", restored.Revision);
        Assert.Empty(empty.OpenViews);
        Assert.Null(empty.FocusedViewId);
        Assert.Empty((await workspace.Read()).OpenViews);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ReplayedOperationIsBoundToItsPayloadAndStaleUpdatesCannotReopenViews()
    {
        await using var brain = await Start(TestContext.Current.CancellationToken);
        var scope = brain.Owner.BrainId;
        var workspace = brain.Get<IWorkspace>(scope);
        await Shipped(brain, scope);
        using var caller = brain.Owner.AsApp(scope + "/apps/workspace");
        var open = new WorkspaceUpdate([new("a", scope + "/apps/files", "full", "")], "a", []);
        await workspace.Apply(open, "open", 0);
        Assert.Equal(1, (await workspace.Apply(open, "open", 0)).Revision);
        await Assert.ThrowsAsync<ArgumentException>(() => workspace.Apply(new([], null, []), "open", 0));
        await workspace.Apply(new([], null, []), "close", 1);
        await Assert.ThrowsAsync<RevisionConflictException>(() => workspace.Apply(open, "late", 1));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task OnlyTheWorkspaceAppCanApplyAndFocusMustNameAnOpenView()
    {
        await using var brain = await Start(TestContext.Current.CancellationToken);
        var scope = brain.Owner.BrainId;
        var workspace = brain.Get<IWorkspace>(scope);
        await Shipped(brain, scope);
        using (brain.Owner.AsCaller())
        { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => workspace.Apply(new([], null, []), "op", 0)); }
        using (brain.Owner.AsApp(scope + "/apps/calculator"))
        { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => workspace.Apply(new([], null, []), "op", 0)); }
        using (brain.Owner.AsApp(scope + "/apps/workspace"))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => workspace.Apply(new([], "missing", []), "op", 0));
            await Assert.ThrowsAsync<ArgumentException>(() => workspace.Apply(new([new("a", "other/brain/apps/private", "full", "")], "a", []), "op", 0));
        }
        using (brain.Owner.AsApp(scope + "/apps/desktop"))
        { Assert.Empty((await workspace.Apply(new([], null, []), "desktop-op", (await workspace.Read()).Revision)).OpenViews); }
    }

    // Authority follows the shipped program, not the name: a foreign or edited app called desktop is refused.
    [Trait("host", "own")]
    [Fact]
    public async Task AnAppThatOnlyBearsTheDesktopNameCannotApply()
    {
        await using var brain = await Start(TestContext.Current.CancellationToken);
        var scope = brain.Owner.BrainId;
        var workspace = brain.Get<IWorkspace>(scope);
        var desktop = brain.Get<IApp>(IApp.Key(scope, "desktop"));
        await desktop.Import(Bundle("desktop"), new("mallory", new AppSource.Brain("account-mallory", "mallory"), "desktop", "0.1.1"));
        using (brain.Owner.AsApp(scope + "/apps/desktop"))
        { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => workspace.Apply(new([], null, []), "foreign", 0)); }

        await desktop.RecordOrigin(new("os", new AppSource.Shipped(), "desktop", "0.1.1"));
        await desktop.SaveManifest(Bundle("desktop").Manifest with { Description = "edited" }, Guid.NewGuid());
        using (brain.Owner.AsApp(scope + "/apps/desktop"))
        { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => workspace.Apply(new([], null, []), "edited", 0)); }
    }

    [Trait("host", "own")]
    [Fact]
    public async Task OpenViewsWithoutWindowsDeriveOneSingleTabWindowEach()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var scope = brain.Owner.BrainId;
        var workspace = brain.Get<IWorkspace>(scope);
        await Shipped(brain, scope);
        using var caller = brain.Owner.AsApp(scope + "/apps/workspace");
        WorkspaceViewRef ino = View(scope, "ino"), notes = View(scope, "notes");
        await workspace.Apply(new([ino, notes], "notes", []), "open", 0);
        var windows = (await workspace.Read()).Windows;
        Assert.Equal(["w-1", "w-2"], windows.Select(window => window.Id));
        Assert.Equal([ino], windows[0].Tabs);
        Assert.Equal([notes], windows[1].Tabs);
        Assert.All(windows, window => Assert.Equal(window.Tabs.Single().Id, window.ActiveTabId));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task WindowsAndFavouritesRoundTripAndSurviveReactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var scope = brain.Owner.BrainId;
        var workspace = brain.Get<IWorkspace>(scope);
        await Shipped(brain, scope);
        using var caller = brain.Owner.AsApp(scope + "/apps/desktop");
        WorkspaceViewRef ino = View(scope, "ino"), ino2 = new("ino-2", scope + "/apps/ino", "full", "");
        WorkspaceViewRef favourite = new("notes", scope + "/apps/notes", "", "");
        await workspace.Apply(new([ino, ino2], "ino-2", [])
        { Windows = [new("w-1", [ino, ino2], "ino-2")], Favourites = [favourite] }, "open", 0);
        await brain.DeactivateAsync(workspace, ct);
        var restored = await workspace.Read();
        var window = Assert.Single(restored.Windows);
        Assert.Equal("w-1", window.Id);
        Assert.Equal([ino, ino2], window.Tabs);
        Assert.Equal("ino-2", window.ActiveTabId);
        Assert.Equal([favourite], restored.Favourites);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task UpdateWithoutWindowsReconcilesTabsWithOpenViews()
    {
        await using var brain = await Start(TestContext.Current.CancellationToken);
        var scope = brain.Owner.BrainId;
        var workspace = brain.Get<IWorkspace>(scope);
        await Shipped(brain, scope);
        WorkspaceViewRef ino = View(scope, "ino"), notes = View(scope, "notes"), files = View(scope, "files"), calc = View(scope, "calc");
        using (brain.Owner.AsApp(scope + "/apps/desktop"))
        {
            await workspace.Apply(new([ino, notes, files], "notes", [])
            { Windows = [new("w-1", [ino, notes], "notes"), new("w-2", [files], "files")] }, "desktop", 0);
        }
        using (brain.Owner.AsApp(scope + "/apps/workspace"))
        { await workspace.Apply(new([ino, files, calc], "ino", []), "classic", 1); }
        var windows = (await workspace.Read()).Windows;
        Assert.Equal(["w-1", "w-2", "w-3"], windows.Select(window => window.Id));
        Assert.Equal([ino], windows[0].Tabs);
        Assert.Equal("ino", windows[0].ActiveTabId);
        Assert.Equal([files], windows[1].Tabs);
        Assert.Equal([calc], windows[2].Tabs);
        Assert.Equal("calc", windows[2].ActiveTabId);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task WindowsMustMatchOpenViewsAndRespectCaps()
    {
        await using var brain = await Start(TestContext.Current.CancellationToken);
        var scope = brain.Owner.BrainId;
        var workspace = brain.Get<IWorkspace>(scope);
        await Shipped(brain, scope);
        using var caller = brain.Owner.AsApp(scope + "/apps/desktop");
        var views = Enumerable.Range(0, 9).Select(i => View(scope, "app" + i)).ToArray();
        Task Try(WorkspaceViewRef[] open, params WorkspaceWindow[] windows)
            => Assert.ThrowsAsync<ArgumentException>(() => workspace.Apply(new(open, null, []) { Windows = windows }, Guid.NewGuid().ToString(), 0));
        await Try([views[0]], new WorkspaceWindow("w", [views[0], views[1]], views[0].Id));
        await Try(views, [.. views.Select(view => new WorkspaceWindow("w-" + view.Id, [view], view.Id))]);
        await Try(views, new WorkspaceWindow("w", views, views[0].Id));
        await Try([views[0]], new WorkspaceWindow("w", [views[0]], views[0].Id), new WorkspaceWindow("e", [], ""));
        await Try([views[0]], new WorkspaceWindow("w", [views[0]], "missing"));
        // A tab must be the open view with its id, not another app, variant or context under that id.
        await Try([views[0]], new WorkspaceWindow("w", [views[0] with { App = views[1].App }], views[0].Id));
        await Try([views[0]], new WorkspaceWindow("w", [views[0] with { Variant = "thread" }], views[0].Id));
        await Try([views[0], views[1]], new WorkspaceWindow("w", [views[0]], views[0].Id), new WorkspaceWindow("w", [views[1]], views[1].Id));
        Assert.Equal(0, (await workspace.Read()).Revision);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task FavouritesHoldAtMostFiveAppsAndFiveViews()
    {
        await using var brain = await Start(TestContext.Current.CancellationToken);
        var scope = brain.Owner.BrainId;
        var workspace = brain.Get<IWorkspace>(scope);
        await Shipped(brain, scope);
        using var caller = brain.Owner.AsApp(scope + "/apps/desktop");
        WorkspaceViewRef App(int i) => new("app" + i, scope + "/apps/app" + i, "", "");
        WorkspaceViewRef ViewOf(int i) => new("view" + i, scope + "/apps/app" + i, "full", "c" + i);
        WorkspaceViewRef[] apps = [.. Enumerable.Range(0, 5).Select(App)], views = [.. Enumerable.Range(0, 5).Select(ViewOf)];
        var state = await workspace.Apply(new([], null, []) { Favourites = [.. apps, .. views] }, "ten", 0);
        Assert.Equal(10, state.Favourites.Length);
        await Assert.ThrowsAsync<ArgumentException>(() => workspace.Apply(new([], null, []) { Favourites = [.. apps, App(5), .. views] }, "apps", state.Revision));
        await Assert.ThrowsAsync<ArgumentException>(() => workspace.Apply(new([], null, []) { Favourites = [.. apps, .. views, ViewOf(5)] }, "views", state.Revision));
        await Assert.ThrowsAsync<ArgumentException>(() => workspace.Apply(new([App(0)], null, []), "open", state.Revision));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnUpdateWithoutFavouritesKeepsThem()
    {
        await using var brain = await Start(TestContext.Current.CancellationToken);
        var scope = brain.Owner.BrainId;
        var workspace = brain.Get<IWorkspace>(scope);
        await Shipped(brain, scope);
        WorkspaceViewRef favourite = new("notes", scope + "/apps/notes", "", "");
        using (brain.Owner.AsApp(scope + "/apps/desktop"))
        { await workspace.Apply(new([], null, []) { Favourites = [favourite] }, "star", 0); }
        using (brain.Owner.AsApp(scope + "/apps/workspace"))
        { await workspace.Apply(new([View(scope, "ino")], "ino", []), "classic", 1); }
        Assert.Equal([favourite], (await workspace.Read()).Favourites);
    }

    // The replay digest is the JSON of the update; a pre-window update must serialise exactly as it did before.
    [Fact]
    public void APreWindowUpdateSerialisesAsBeforeSoItsReceiptStillMatches()
    {
        WorkspaceViewRef[] views = [new("a", "b/apps/files", "full", "")];
        Assert.Equal(JsonSerializer.Serialize(new PreWindowUpdate(views, null, [])), JsonSerializer.Serialize(new WorkspaceUpdate(views, null, [])));
        Assert.Contains("\"Windows\":[]", JsonSerializer.Serialize(new WorkspaceUpdate(views, null, []) { Windows = [] }), StringComparison.Ordinal);
    }

    private sealed record PreWindowUpdate(WorkspaceViewRef[] OpenViews, string? FocusedViewId, WorkspaceViewRef[] Pins);

    [Trait("host", "own")]
    [Fact]
    public async Task APreWindowReplayWithNoFocusReturnsTheSameState()
    {
        await using var brain = await Start(TestContext.Current.CancellationToken);
        var scope = brain.Owner.BrainId;
        var workspace = brain.Get<IWorkspace>(scope);
        await Shipped(brain, scope);
        using var caller = brain.Owner.AsApp(scope + "/apps/workspace");
        var update = new WorkspaceUpdate([View(scope, "ino")], null, []);
        await workspace.Apply(update, "op", 0);
        Assert.Equal(1, (await workspace.Apply(update, "op", 0)).Revision);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ManyClassicViewsStayWithinTheWindowCapsAndEchoBack()
    {
        await using var brain = await Start(TestContext.Current.CancellationToken);
        var scope = brain.Owner.BrainId;
        var workspace = brain.Get<IWorkspace>(scope);
        await Shipped(brain, scope);
        var odd = new WorkspaceViewRef(new string('x', 100) + " / ü", scope + "/apps/notes", "full", "");
        WorkspaceViewRef[] views = [odd, .. Enumerable.Range(0, 63).Select(i => new WorkspaceViewRef("v" + i, scope + "/apps/app" + i, "full", ""))];
        using (brain.Owner.AsApp(scope + "/apps/workspace"))
        { await workspace.Apply(new(views, null, []), "classic", 0); }
        var state = await workspace.Read();
        Assert.Equal(8, state.Windows.Length);
        Assert.All(state.Windows, window => Assert.InRange(window.Tabs.Length, 1, 8));
        Assert.All(state.Windows, window => Assert.Matches("^[A-Za-z0-9_.-]{1,64}$", window.Id));
        Assert.Equal(views.Select(view => view.Id).Order(), state.Windows.SelectMany(window => window.Tabs).Select(tab => tab.Id).Order());
        using (brain.Owner.AsApp(scope + "/apps/desktop"))
        { await workspace.Apply(new(state.OpenViews, null, []) { Windows = state.Windows }, "echo", state.Revision); }
        Assert.Equal(2, (await workspace.Read()).Revision);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ATabInTwoWindowsIsRefused()
    {
        await using var brain = await Start(TestContext.Current.CancellationToken);
        var scope = brain.Owner.BrainId;
        var workspace = brain.Get<IWorkspace>(scope);
        await Shipped(brain, scope);
        using var caller = brain.Owner.AsApp(scope + "/apps/desktop");
        var ino = View(scope, "ino");
        await Assert.ThrowsAsync<ArgumentException>(() => workspace.Apply(new([ino], null, [])
        { Windows = [new("w-1", [ino], "ino"), new("w-2", [ino], "ino")] }, "dup", 0));
    }

    private static WorkspaceViewRef View(string scope, string app) => new(app, scope + "/apps/" + app, "full", "");

    private static Task<ITestBrain> Start(CancellationToken ct) => TestBrain.Create().WithModule<FlutterModule>().WithModule<AppsModule>()
        .ConfigureServices(services => { services.RemoveAll<IAppRuntime>(); services.AddSingleton<IAppRuntime, NoRuntime>(); })
        .StartAsync(ct);

    // The workspace and desktop the OS ships, installed as Connect installs them.
    private static async Task Shipped(ITestBrain brain, string scope)
    {
        foreach (var name in new[] { "workspace", "desktop" })
        { await brain.Get<IApp>(IApp.Key(scope, name)).Import(Bundle(name), new("os", new AppSource.Shipped(), name, "0.1.1")); }
    }

    private static AppBundle Bundle(string name) => new(name, "0.1.1",
        new(name, "the " + name, [new("open", "")], [], []), "## Scenario: Opens\nIt opens.", []);

    private sealed class NoRuntime : IAppRuntime
    {
        public Task<IReadOnlyDictionary<string, string>> Deploy(string appKey, Dictionary<string, string> sources, Dictionary<string, string> configuration, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task Retire(string appKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
