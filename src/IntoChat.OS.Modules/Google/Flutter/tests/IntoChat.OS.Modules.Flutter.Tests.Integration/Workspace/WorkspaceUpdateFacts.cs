using IntoChat.OS.Apps;
using IntoChat.OS.Flutter;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using IntoChat.OS.Flutter.Workspace;

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
