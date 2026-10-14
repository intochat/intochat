using System.Text.Json;
using DigitalBrain.Apps;
using DigitalBrain.Flame;
using DigitalBrain.Flame.Scene;
using DigitalBrain.Flame.Scene.Signals;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Space;
using DigitalBrain.Flutter.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans;

namespace DigitalBrain.Modules.Flutter.Tests.Space;

public sealed class SpaceFacts
{
    [Fact]
    public async Task IdleAppLifecycleRefreshesTheSceneWithoutNavigation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().WithModule<AppsModule>().WithModule<FlameModule>()
            .ConfigureServices(s => s.RemoveAll<IAppRuntime>()).StartAsync(ct);
        var workspace = brain.Get<IWorkspace>(brain.Owner.BrainId);
        var scene = brain.Get<IScene>(brain.Owner.Key("space"));
        await using (var home = await brain.Observe<SceneChanged>(scene, ct))
        {
            await workspace.Home("home", 0);
            await home.NextAsync(ct: ct);
        }
        var key = IApp.Key(brain.Owner.BrainId, "idle");
        await using (var changes = await brain.Observe<SceneChanged>(scene, ct))
        {
            await App(brain, "idle");
            do { await changes.NextAsync(ct: ct); }
            while (!(await scene.Read()).Definition.Nodes.Any(n => n.Id == key));
        }
        var app = brain.Get<IApp>(key);
        var original = await app.Read();
        await using (var changes = await brain.Observe<SceneChanged>(scene, ct))
        {
            await app.SaveManifest(new("Renamed", "New description", [new("refresh", "Refresh")], [], [],
                Toolbar: [new("refresh", Label: "Reload")]), Guid.NewGuid());
            do { await changes.NextAsync(ct: ct); }
            while (!(await scene.Read()).Definition.Nodes.Any(n => n.Id == key && n.Parameters["title"] == "Renamed"));
            var node = Assert.Single((await scene.Read()).Definition.Nodes, n => n.Id == key);
            Assert.Equal("New description", node.Parameters["description"]);
            Assert.Contains("Reload", node.Parameters["toolbar"], StringComparison.Ordinal);
            Assert.Equal(original.ProgramRevision, (await app.Read()).ProgramRevision);
        }
        await using (var changes = await brain.Observe<SceneChanged>(scene, ct))
        {
            await app.Delete();
            do { await changes.NextAsync(ct: ct); }
            while ((await scene.Read()).Definition.Nodes.Any(n => n.Id == key));
        }
        Assert.Equal(1, (await workspace.Read()).Revision);
    }

    [Fact]
    public async Task AModuleRebuildNeverReadsAnUnscopedTypeTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var calls = new UnscopedTypeReads();
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().WithModule<AppsModule>().WithModule<FlameModule>()
            .ConfigureServices(services => services.AddSingleton<IIncomingGrainCallFilter>(calls)).StartAsync(ct);
        await App(brain, "files");
        var scene = brain.Get<IScene>(brain.Owner.BrainId + "/space");
        await using var changed = await brain.Observe<SceneChanged>(scene, ct);
        calls.Count = 0;
        await brain.Services.GetRequiredService<IGrainFactory>().GetGrain<ISpace>(brain.Owner.BrainId).Rebuild();
        await changed.NextAsync(ct: ct);
        Assert.Equal(0, calls.Count);
    }

    private sealed class UnscopedTypeReads : IIncomingGrainCallFilter
    {
        public int Count;
        public Task Invoke(IIncomingGrainCallContext context)
        {
            if (context.TargetId.Type.ToString() == "kernel.typetable" && context.TargetId.Key.ToString() == "/") { Interlocked.Increment(ref Count); }
            return context.Invoke();
        }
    }

    private static Task<ITestBrain> Start() => TestBrain.Create().WithModule<FlutterModule>().WithModule<AppsModule>().WithModule<FlameModule>().StartAsync(TestContext.Current.CancellationToken);
    private static async Task<IApp> App(ITestBrain brain, string name)
    {
        var app = brain.Get<IApp>(IApp.Key(brain.Owner.BrainId, name));
        await app.Create(new(name, "description", [], [], []), "spec");
        return app;
    }

    [Fact]
    public void TheComposerPlacesTheFocusedSphereUnderTheCamera()
    {
        SpaceComposer.SpaceEntry[] field =
        [new("owner/a/apps/ino", "Ino", "assistant", new(0, 1, new(new Sphere("icon.svg", []), Main: new("chat", "owner/a/chat")), null, []), []),
         new("owner/a/apps/files", "Files", "files", null, [])];
        var scene = SpaceComposer.Compose(new(0, ["owner/a", "owner/a/apps/ino"], ["owner/a/apps/ino"]), field);
        var ino = Assert.Single(scene.Nodes, node => node.Id == "owner/a/apps/ino");
        Assert.Equal("true", ino.Parameters["focused"]);
        Assert.Equal(2.2, ino.Transform.Scale);
        Assert.Equal((ino.Transform.X, ino.Transform.Y), (scene.Camera.X, scene.Camera.Y));
        Assert.Contains("chat", ino.Parameters["main"], StringComparison.Ordinal);
        var home = SpaceComposer.Compose(new(1, ["owner/a"], []), field);
        Assert.Equal("unready", Assert.Single(home.Nodes, node => node.Id == "owner/a/apps/files").Parameters["state"]);
        Assert.All(home.Nodes, node => Assert.False(node.Parameters.ContainsKey("main")));
    }

    [Fact]
    public async Task APublishedPresentationAppearsInTheDerivedScene()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        var app = await App(brain, "files");
        var workspace = brain.Get<IWorkspace>(brain.Owner.BrainId);
        await workspace.Home("home", 0);
        var scene = brain.Get<IScene>(brain.Owner.BrainId + "/space");
        await using var changed = await brain.Observe<SceneChanged>(scene, ct);
        using (brain.Owner.AsApp(IApp.Key(brain.Owner.BrainId, "files")))
        { await app.Present(0, new(new Sphere("folder", []))); }
        SceneState published;
        do { await changed.NextAsync(ct: ct); published = await scene.Read(); }
        while (!published.Definition.Nodes.Any(node => node.Id == IApp.Key(brain.Owner.BrainId, "files") && node.Parameters["state"] == "ready"));
        var node = Assert.Single(published.Definition.Nodes, node => node.Id == IApp.Key(brain.Owner.BrainId, "files"));
        Assert.Equal("folder", node.Parameters["icon"]);
        Assert.Equal("ready", node.Parameters["state"]);
    }

    [Fact]
    public async Task ShowAndBackMoveTheCameraTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        await App(brain, "ino");
        await App(brain, "files");
        var scene = brain.Get<IScene>(brain.Owner.BrainId + "/space");
        await using var changed = await brain.Observe<SceneChanged>(scene, ct);
        var workspace = brain.Get<IWorkspace>(brain.Owner.BrainId);
        await workspace.Show(IApp.Key(brain.Owner.BrainId, "files"), "show", 0);
        SceneState shown;
        do { await changed.NextAsync(ct: ct); shown = await scene.Read(); }
        while (!shown.Definition.Nodes.Any(node => node.Id == IApp.Key(brain.Owner.BrainId, "files") && node.Parameters["focused"] == "true"));
        var files = Assert.Single(shown.Definition.Nodes, node => node.Parameters["focused"] == "true");
        Assert.Equal(IApp.Key(brain.Owner.BrainId, "files"), files.Id);
        await workspace.Back("back", 1);
        SceneState backed;
        do { await changed.NextAsync(ct: ct); backed = await scene.Read(); }
        while (!backed.Definition.Nodes.Any(node => node.Id == IApp.Key(brain.Owner.BrainId, "ino") && node.Parameters["focused"] == "true"));
        Assert.Equal(IApp.Key(brain.Owner.BrainId, "ino"), Assert.Single(backed.Definition.Nodes, node => node.Parameters["focused"] == "true").Id);
        Assert.NotEqual(shown.Definition.Camera, backed.Definition.Camera);
    }

    [Fact]
    public async Task RunningFollowsActivityAndAScriptWriteToItIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        var app = await App(brain, "files");
        var key = IApp.Key(brain.Owner.BrainId, "files");
        var scene = brain.Get<IScene>(brain.Owner.BrainId + "/space");
        var workspace = brain.Get<IWorkspace>(brain.Owner.BrainId);
        await workspace.Show(key, "show", 0);
        await using var changed = await brain.Observe<SceneChanged>(scene, ct);
        await using var presentation = await brain.Observe<DigitalBrain.Apps.Signals.PresentationChanged>(app, ct);
        await app.Progress("working", "reading files");
        await presentation.NextAsync(ct: ct);
        await brain.Services.GetRequiredService<Orleans.IGrainFactory>().GetGrain<ISpace>(brain.Owner.BrainId).Rebuild();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        SceneState state;
        do { await changed.NextAsync(ct: deadline.Token); state = await scene.Read(); }
        while (!state.Definition.Nodes.Any(node => node.Id == key
            && JsonSerializer.Deserialize<Dictionary<string, string>>(node.Parameters["blackboard"])?.GetValueOrDefault("running") == "true"));
        var node = Assert.Single(state.Definition.Nodes, node => node.Id == key);
        using var board = JsonDocument.Parse(node.Parameters["blackboard"]);
        Assert.Equal("true", board.RootElement.GetProperty("running").GetString());
        using (brain.Owner.AsApp(key))
        {
            await app.Present(0, new(new Sphere("folder", [])));
            await Assert.ThrowsAsync<ArgumentException>(() => app.WriteBlackboard(0, new() { ["running"] = "false" }));
        }
    }

    [Fact]
    public async Task AScriptCannotWriteTheSpaceScene()
    {
        await using var brain = await Start();
        using (brain.Owner.AsApp(IApp.Key(brain.Owner.BrainId, "files")))
        { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => brain.Get<IScene>(brain.Owner.BrainId + "/space").Set(new([], new()), 0)); }
    }
}
