using System.Text.Json;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flame;
using IntoChat.OS.Modules.Flame.Scene;
using IntoChat.OS.Modules.Flame.Scene.Signals;
using IntoChat.OS.Modules.Flutter.Space;
using IntoChat.OS.Modules.Flutter.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Orleans;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Space;

public sealed class SpaceFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task MigratedAliasRemainsStoredButOnlyItsTargetAppearsAndLegacyFocusResolves()
    {
        var ct = TestContext.Current.CancellationToken;
        var calls = new AppReads();
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().WithModule<AppsModule>().WithModule<FlameModule>()
            .ConfigureServices(s =>
            {
                s.RemoveAll<IAppRuntime>();
                s.Remove(s.Single(service => service.ServiceType == typeof(IHostedService)
                    && service.ImplementationType == typeof(SpaceWatcher)));
                s.AddSingleton<IOutgoingGrainCallFilter>(calls);
            }).StartAsync(ct);
        using var caller = brain.Owner.AsCaller();
        var old = await App(brain, "assistant");
        var target = await App(brain, "ino");
        var oldKey = IApp.Key(brain.Owner.BrainId, "assistant");
        var targetKey = IApp.Key(brain.Owner.BrainId, "ino");
        var workspace = brain.Get<IWorkspace>(IWorkspace.Key(brain.Owner.BrainId));
        await workspace.Home("alias-home", 0);
        var space = brain.Get<ISpace>(ISpace.Key(brain.Owner.BrainId));
        await space.Rebuild();
        var scene = brain.Get<IScene>(brain.Owner.Key("space"));
        await TestWait.UntilAsync(_ => scene.Read(), state => state.Definition.Nodes.Any(node => node.Id == oldKey), TimeSpan.FromSeconds(10), ct);
        var migration = brain.Services.GetRequiredService<IntoChat.OS.Runtime.IPlainNeurons>().Get<IAppMigration>(oldKey);
        await migration.Pause("alias-test");
        await brain.Services.GetRequiredService<IntoChat.OS.Runtime.IPlainNeurons>().Get<IAppMigration>(targetKey).Pause("alias-test");
        await migration.Redirect("alias-test", targetKey);
        var beforeRefresh = (await scene.Read()).Revision;
        await space.RefreshApp(oldKey);
        await TestWait.UntilAsync(_ => scene.Read(), state => state.Revision > beforeRefresh, TimeSpan.FromSeconds(10), ct);
        await space.ReadProfile(brain.Owner.Key("profile"));
        Assert.DoesNotContain((await scene.Read()).Definition.Nodes, node => node.Id == oldKey);
        Assert.Contains((await scene.Read()).Definition.Nodes, node => node.Id == targetKey);
        var retained = await old.Read();
        Assert.Equal("assistant", retained.Manifest.Title);
        Assert.Equal(targetKey, retained.MigrationTarget);
        calls.Count = 0;
        calls.Armed = true;
        beforeRefresh = (await scene.Read()).Revision;
        await space.RefreshApp(targetKey);
        await TestWait.UntilAsync(_ => scene.Read(), state => state.Revision > beforeRefresh, TimeSpan.FromSeconds(10), ct);
        await space.ReadProfile(brain.Owner.Key("profile"));
        calls.Armed = false;
        Assert.Equal(1, calls.Count);
        await workspace.Show(oldKey, "legacy-focus", 1);
        await space.Recompose();
        await TestWait.UntilAsync(_ => scene.Read(), state => state.Definition.Nodes.Any(node => node.Parameters.GetValueOrDefault("focused") == "true"), TimeSpan.FromSeconds(10), ct);
        await space.ReadProfile(brain.Owner.Key("profile"));
        Assert.Equal(oldKey, (await workspace.Read()).Path[^1]);
        Assert.Contains((await scene.Read()).Definition.Nodes, node => node.Id == targetKey && node.Parameters.GetValueOrDefault("focused") == "true");
        await brain.DeactivateAsync(space, ct);
        await space.ReadProfile(brain.Owner.Key("profile"));
        Assert.DoesNotContain((await scene.Read()).Definition.Nodes, node => node.Id == oldKey);
        Assert.Equal("ino", (await target.Read()).Manifest.Title);
    }

    [Trait("host", "own")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneAppChangeReadsOnlyTheChangedAppAndNavigationReadsNone(bool presentation)
    {
        var ct = TestContext.Current.CancellationToken;
        var calls = new AppReads();
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().WithModule<AppsModule>().WithModule<FlameModule>()
            .ConfigureServices(s => { s.RemoveAll<IAppRuntime>(); s.AddSingleton<IOutgoingGrainCallFilter>(calls); }).StartAsync(ct);
        using var caller = brain.Owner.AsCaller();
        await brain.Get<IWorkspace>(IWorkspace.Key(brain.Owner.BrainId)).Home("count-home", 0);
        var apps = new List<IApp>();
        for (var i = 0; i < 15; i++) { apps.Add(await App(brain, "count-" + i)); }
        var scene = brain.Get<IScene>(brain.Owner.Key("space"));
        await TestWait.UntilAsync(_ => scene.Read(), state => state.Definition.Nodes.Count(node => node.Id.Contains("/apps/count-", StringComparison.Ordinal)) == 15,
            TimeSpan.FromSeconds(10), ct);
        await brain.Get<ISpace>(ISpace.Key(brain.Owner.BrainId)).ReadProfile(brain.Owner.Key("profile"));
        calls.Count = 0;
        calls.Armed = true;
        if (presentation)
        {
            using (brain.Owner.AsApp(IApp.Key(brain.Owner.BrainId, "count-7")))
            { await apps[7].Present(0, new(new Sphere("changed-icon", []))); }
        }
        else { await apps[7].SaveManifest(new("Only changed", "description", [], [], []), Guid.NewGuid()); }
        await TestWait.UntilAsync(_ => scene.Read(), state => state.Definition.Nodes.Any(node => node.Parameters.GetValueOrDefault(presentation ? "icon" : "title") == (presentation ? "changed-icon" : "Only changed")),
            TimeSpan.FromSeconds(10), ct);
        await brain.Get<ISpace>(ISpace.Key(brain.Owner.BrainId)).ReadProfile(brain.Owner.Key("profile"));
        calls.Armed = false;
        Assert.Equal(1, calls.Count);
        calls.Count = 0;
        calls.Armed = true;
        await brain.Get<IWorkspace>(IWorkspace.Key(brain.Owner.BrainId)).Show(IApp.Key(brain.Owner.BrainId, "count-7"), "navigate", 1);
        await TestWait.UntilAsync(_ => scene.Read(), state => state.Definition.Nodes.Any(node => node.Parameters.GetValueOrDefault("focused") == "true"),
            TimeSpan.FromSeconds(10), ct);
        await brain.Get<ISpace>(ISpace.Key(brain.Owner.BrainId)).ReadProfile(brain.Owner.Key("profile"));
        calls.Armed = false;
        Assert.Equal(0, calls.Count);
        var beforeActivation = (await scene.Read()).Revision;
        await brain.DeactivateAsync(brain.Get<ISpace>(ISpace.Key(brain.Owner.BrainId)), ct);
        calls.Count = 0;
        calls.Armed = true;
        await brain.Get<ISpace>(ISpace.Key(brain.Owner.BrainId)).ReadProfile(brain.Owner.Key("profile"));
        await TestWait.UntilAsync(_ => scene.Read(), state => state.Revision > beforeActivation, TimeSpan.FromSeconds(10), ct);
        calls.Armed = false;
        Assert.Equal(15, calls.Count);
        Assert.Contains((await scene.Read()).Definition.Nodes, node =>
            node.Parameters.GetValueOrDefault(presentation ? "icon" : "title") == (presentation ? "changed-icon" : "Only changed"));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AFailedAppReadIsRetriedByTheNextComposition()
    {
        var ct = TestContext.Current.CancellationToken;
        var calls = new AppReads { ManualCompositions = true };
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().WithModule<AppsModule>().WithModule<FlameModule>()
            .ConfigureServices(s =>
            {
                s.RemoveAll<IAppRuntime>();
                // The watcher turns the app's creation and the Home into late compositions. One that lands
                // after the failure is armed consumes it, or re-reads the dirty app before the removal is seen.
                // This fact drives every composition itself.
                s.Remove(s.Single(service => service.ServiceType == typeof(IHostedService)
                    && service.ImplementationType == typeof(SpaceWatcher)));
                s.AddSingleton<IOutgoingGrainCallFilter>(calls);
            }).StartAsync(ct);
        using var caller = brain.Owner.AsCaller();
        await App(brain, "retry");
        await brain.Get<IWorkspace>(IWorkspace.Key(brain.Owner.BrainId)).Home("retry-home", 0);
        Assert.True(calls.SuppressedCompositions > 0);
        var space = brain.Get<ISpace>(ISpace.Key(brain.Owner.BrainId));
        var scene = brain.Get<IScene>(brain.Owner.Key("space"));
        var key = IApp.Key(brain.Owner.BrainId, "retry");
        // Activation composes once and finishes before this call returns. Rebuild alone would also activate the
        // space, and its full scan could still be reading the app when the failure is armed.
        await space.ReadProfile(brain.Owner.Key("profile"));
        var activated = (await scene.Read()).Revision;
        await space.Rebuild();
        await TestWait.UntilAsync(_ => scene.Read(), state => state.Revision > activated && state.Definition.Nodes.Any(node => node.Id == key),
            TimeSpan.FromSeconds(10), ct);
        calls.Armed = true;
        calls.FailNext = true;
        await space.RefreshApp(key);
        await TestWait.UntilAsync(_ => scene.Read(), state => state.Definition.Nodes.All(node => node.Id != key), TimeSpan.FromSeconds(10), ct);
        calls.Count = 0;
        await space.Recompose();
        await TestWait.UntilAsync(_ => scene.Read(), state => state.Definition.Nodes.Any(node => node.Id == key), TimeSpan.FromSeconds(10), ct);
        calls.Armed = false;
        Assert.Equal(1, calls.Count);
    }

    private sealed class AppReads : IOutgoingGrainCallFilter
    {
        public bool Armed, FailNext;
        public int Count;
        public bool ManualCompositions;
        public int SuppressedCompositions;
        public Task Invoke(IOutgoingGrainCallContext context)
        {
            // Home posts directly even when SpaceWatcher is removed. This controlled retry fact drives its own
            // invalidations; a late one-way post could otherwise consume the failure or retry before it is observed.
            if (ManualCompositions && Calls.Is(context.InterfaceMethod, context.Request, "ui.space", nameof(ISpace.Recompose))
                && context.Request.GetArgument(0) is DigitalBrain.Invocation.RuntimeInvocation { Behavior.Length: > 0 })
            {
                Interlocked.Increment(ref SuppressedCompositions);
                context.Response = global::Orleans.Serialization.Invocation.Response.Completed;
                return Task.CompletedTask;
            }
            if (Armed && Calls.Is(context.InterfaceMethod, context.Request, "apps.app2", nameof(IApp.Read)))
            {
                Interlocked.Increment(ref Count);
                if (FailNext) { FailNext = false; throw new InvalidOperationException("Transient app read failure."); }
            }
            return context.Invoke();
        }
    }
    [Trait("host", "own")]
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ARebuildBurstSharesOneScanAndOneTrailingScan(bool firstScanFails, bool targeted)
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new RebuildGate(firstScanFails, targeted);
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().WithModule<AppsModule>().WithModule<FlameModule>()
            .ConfigureServices(s =>
            {
                s.RemoveAll<IAppRuntime>();
                // This regression supplies exact invalidations. Other Space facts
                // retain the real watcher and verify automatic lifecycle updates.
                s.Remove(s.Single(service => service.ServiceType == typeof(IHostedService)
                    && service.ImplementationType == typeof(SpaceWatcher)));
                s.AddSingleton<IIncomingGrainCallFilter>(gate);
                s.AddSingleton<IOutgoingGrainCallFilter>(gate);
            }).StartAsync(ct);
        using var caller = brain.Owner.AsCaller();
        await brain.Get<IWorkspace>(IWorkspace.Key(brain.Owner.BrainId)).Home("burst-home", 0);
        var app = await App(brain, "burst");
        if (targeted) { for (var i = 0; i < 14; i++) { await App(brain, "unrelated-" + i); } }
        var space = brain.Get<ISpace>(ISpace.Key(brain.Owner.BrainId));
        var scene = brain.Get<IScene>(brain.Owner.Key("space"));
        // Reconcile apps created after Workspace.Home activated the space.
        // The watcher is removed, so these creations supply no invalidations.
        await space.Rebuild();
        await TestWait.UntilAsync(_ => scene.Read(), state => state.Definition.Nodes.Count(node => node.Id.Contains("/apps/", StringComparison.Ordinal)) == (targeted ? 15 : 1), TimeSpan.FromSeconds(10), ct);
        await space.ReadProfile(brain.Owner.Key("profile"));
        gate.Armed = true;
        try
        {
            await Invalidate();
            await gate.Blocked.Task.WaitAsync(ct);
            for (var i = 0; i < 12; i++) { await Invalidate(); }
            await app.SaveManifest(new("Final title", "description", [], [], []), Guid.NewGuid());
            // Rebuild must accept invalidations while its existing scan is suspended.
            await gate.BurstArrived.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
        finally { gate.Release.TrySetResult(); }
        await gate.WorkerFinished.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await TestWait.UntilAsync(_ => scene.Read(), snapshot => snapshot.Definition.Nodes.Any(node =>
            node.Id == IApp.Key(brain.Owner.BrainId, "burst") && node.Parameters["title"] == "Final title"), TimeSpan.FromSeconds(10), ct);
        Assert.InRange(gate.Reads, 1, 2);
        Assert.Equal(1, gate.MaximumActive);
        Task Invalidate() => targeted ? space.RefreshApp(IApp.Key(brain.Owner.BrainId, "burst")) : space.Rebuild();
    }

    private sealed class RebuildGate(bool firstScanFails, bool targeted) : IIncomingGrainCallFilter, IOutgoingGrainCallFilter
    {

        public bool Armed;
        public int Reads, MaximumActive;
        private int _active, _rebuilds;
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BurstArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WorkerFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Invoke(IIncomingGrainCallContext context)
        {
            var first = false;
            // The space is a plain neuron (06-08): its calls arrive on the runtime's host grain as one RuntimeInvocation.
            var (_, method) = Calls.Of(context.InterfaceMethod, context.Request);
            if (Armed && method is nameof(ISpace.Rebuild) or nameof(ISpace.RefreshApp))
            {
                var count = Interlocked.Increment(ref _rebuilds);
                first = count == 1;
                if (count >= 13) { BurstArrived.TrySetResult(); }
            }
            try { await context.Invoke(); }
            finally { if (first) { WorkerFinished.TrySetResult(); } }
        }
        public async Task Invoke(IOutgoingGrainCallContext context)
        {
            // A single-app refresh reuses the workspace, so its scan waits on the app read.
            var contract = firstScanFails && !targeted ? IWorkspace.Type : "apps.app2";
            var (called, method) = Calls.Of(context.InterfaceMethod, context.Request);
            if (!Armed || called != contract || method != "Read")
            { await context.Invoke(); return; }
            var active = Interlocked.Increment(ref _active);
            MaximumActive = Math.Max(MaximumActive, active);
            try
            {
                await context.Invoke();
                if (Interlocked.Increment(ref Reads) == 1)
                {
                    Blocked.TrySetResult();
                    await Release.Task;
                    if (firstScanFails) { throw new InvalidOperationException("The first space scan failed."); }
                }
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    [Trait("host", "own")]
    [Fact]
    public async Task RepeatedActivityOnlyInvalidatesThroughChangedPresentation()
    {
        var ct = TestContext.Current.CancellationToken;
        var calls = new ActivityCalls();
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().WithModule<AppsModule>().WithModule<FlameModule>()
            .ConfigureServices(s =>
            {
                s.RemoveAll<IAppRuntime>();
                s.Remove(s.Single(service => service.ServiceType == typeof(IHostedService)
                    && service.ImplementationType == typeof(SpaceWatcher)));
                s.AddSingleton<IOutgoingGrainCallFilter>(calls);
            }).StartAsync(ct);
        var app = await App(brain, "activity");
        await using var presentations = await brain.Observe<IntoChat.OS.Modules.Apps.Signals.PresentationChanged>(app, ct);
        await app.Progress("working", "first detail");
        var first = (await app.Read()).Presentation!;
        Assert.Equal("true", first.Blackboard["running"]);
        Assert.Equal("false", first.Blackboard["attention"]);
        await presentations.NextAsync(ct: ct);
        calls.Armed = true;
        for (var i = 0; i < 5; i++)
        {
            await app.Progress("reading", $"detail {i}");
        }
        calls.Armed = false;
        Assert.Equal(0, calls.Rebuilds);
        Assert.Equal(first.Revision, (await app.Read()).Presentation!.Revision);
        await app.Progress("attention", "needs input");
        var last = (await app.Read()).Presentation!;
        Assert.Equal("false", last.Blackboard["running"]);
        Assert.Equal("true", last.Blackboard["attention"]);
        Assert.Equal(first.Revision + 1, last.Revision);
    }

    private sealed class ActivityCalls : IOutgoingGrainCallFilter
    {
        public bool Armed;
        public int Rebuilds;
        public Task Invoke(IOutgoingGrainCallContext context)
        {
            if (Armed && Calls.Is(context.InterfaceMethod, context.Request, ISpace.Type, nameof(ISpace.Rebuild))) { Interlocked.Increment(ref Rebuilds); }
            return context.Invoke();
        }
    }

    [Trait("host", "own")]
    [Fact]
    public async Task IdleAppLifecycleRefreshesTheSceneWithoutNavigation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().WithModule<AppsModule>().WithModule<FlameModule>()
            .ConfigureServices(s => s.RemoveAll<IAppRuntime>()).StartAsync(ct);
        var workspace = brain.Get<IWorkspace>(IWorkspace.Key(brain.Owner.BrainId));
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

    [Trait("host", "own")]
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
        await brain.Services.GetRequiredService<IntoChat.OS.Runtime.IPlainNeurons>().Get<ISpace>(ISpace.Key(brain.Owner.BrainId)).Rebuild();
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

    [Trait("host", "own")]
    [Fact]
    public async Task RefreshingOneAppDoesNotRereadTheWorkspace()
    {
        var ct = TestContext.Current.CancellationToken;
        var reads = new WorkspaceReads();
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().WithModule<AppsModule>().WithModule<FlameModule>()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IAppRuntime>();
                services.Remove(services.Single(service => service.ServiceType == typeof(IHostedService)
                    && service.ImplementationType == typeof(SpaceWatcher)));
                services.AddSingleton<IOutgoingGrainCallFilter>(reads);
                services.AddSingleton<IIncomingGrainCallFilter>(reads);
            }).StartAsync(ct);
        using var caller = brain.Owner.AsCaller();
        var key = IApp.Key(brain.Owner.BrainId, "panel");
        var app = await App(brain, "panel");
        await brain.Get<IWorkspace>(IWorkspace.Key(brain.Owner.BrainId)).Home("panel-home", 0);
        var space = brain.Get<ISpace>(ISpace.Key(brain.Owner.BrainId));
        var scene = brain.Get<IScene>(brain.Owner.Key("space"));
        // Home queues a one-way recompose, and activation may still be composing. The app
        // appearing in the scene is not the end of that work, and a later revision may belong
        // to an earlier request, so once the app is composed wait until every one-way request
        // sent to the space has been received and has finished; only then count reads.
        await using var drained = await brain.Observe<SceneChanged>(scene, ct);
        var revision = (await scene.Read()).Revision;
        await space.Rebuild();
        do { await drained.NextAsync(ct: ct); }
        while ((await scene.Read()).Revision <= revision || !(await scene.Read()).Definition.Nodes.Any(node => node.Id == key));
        await TestWait.UntilAsync(_ => Task.FromResult(reads.PendingSpaceRequests), pending => pending == 0, TimeSpan.FromSeconds(10), ct);
        reads.Armed = true;
        await app.SaveManifest(new("Renamed", "description", [], [], []), Guid.NewGuid());
        await space.RefreshApp(key);
        await TestWait.UntilAsync(_ => scene.Read(), state => state.Definition.Nodes.Any(node =>
            node.Id == key && node.Parameters["title"] == "Renamed"), TimeSpan.FromSeconds(10), ct);
        Assert.Equal(0, reads.Count);
    }

    private sealed class WorkspaceReads : IOutgoingGrainCallFilter, IIncomingGrainCallFilter
    {
        public bool Armed;
        public int Count;
        private int _sent, _finished;

        // One-way requests sent to the space and not yet finished by it. A request that finds the worker running returns
        // at once and leaves its work to that worker, which finishes last, so zero means nothing is queued or running.
        public int PendingSpaceRequests => Volatile.Read(ref _sent) - Volatile.Read(ref _finished);

        private static bool IsRequest(System.Reflection.MethodInfo? method, global::Orleans.Serialization.Invocation.IInvokable request)
            => Calls.Of(method, request) is (ISpace.Type, nameof(ISpace.Rebuild) or nameof(ISpace.RefreshApp) or nameof(ISpace.Recompose));

        public async Task Invoke(IIncomingGrainCallContext context)
        {
            if (!IsRequest(context.InterfaceMethod, context.Request)) { await context.Invoke(); return; }
            try { await context.Invoke(); }
            finally { Interlocked.Increment(ref _finished); }
        }

        public Task Invoke(IOutgoingGrainCallContext context)
        {
            if (IsRequest(context.InterfaceMethod, context.Request)) { Interlocked.Increment(ref _sent); }
            if (Armed && Calls.Is(context.InterfaceMethod, context.Request, IWorkspace.Type, nameof(IWorkspace.Read)))
            { Interlocked.Increment(ref Count); }
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

    [Trait("host", "own")]
    [Fact]
    public async Task APublishedPresentationAppearsInTheDerivedScene()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        var app = await App(brain, "files");
        var workspace = brain.Get<IWorkspace>(IWorkspace.Key(brain.Owner.BrainId));
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

    [Trait("host", "own")]
    [Fact]
    public async Task ShowAndBackMoveTheCameraTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        await App(brain, "ino");
        await App(brain, "files");
        var scene = brain.Get<IScene>(brain.Owner.BrainId + "/space");
        await using var changed = await brain.Observe<SceneChanged>(scene, ct);
        var workspace = brain.Get<IWorkspace>(IWorkspace.Key(brain.Owner.BrainId));
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

    [Trait("host", "own")]
    [Fact]
    public async Task RunningFollowsActivityAndAScriptWriteToItIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        var app = await App(brain, "files");
        var key = IApp.Key(brain.Owner.BrainId, "files");
        var scene = brain.Get<IScene>(brain.Owner.BrainId + "/space");
        var workspace = brain.Get<IWorkspace>(IWorkspace.Key(brain.Owner.BrainId));
        await workspace.Show(key, "show", 0);
        await using var changed = await brain.Observe<SceneChanged>(scene, ct);
        await using var presentation = await brain.Observe<IntoChat.OS.Modules.Apps.Signals.PresentationChanged>(app, ct);
        await app.Progress("working", "reading files");
        await presentation.NextAsync(ct: ct);
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

    [Trait("host", "own")]
    [Fact]
    public async Task AScriptCannotWriteTheSpaceScene()
    {
        await using var brain = await Start();
        using (brain.Owner.AsApp(IApp.Key(brain.Owner.BrainId, "files")))
        { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => brain.Get<IScene>(brain.Owner.BrainId + "/space").Set(new([], new()), 0)); }
    }
}

// The (contract alias, method) of a grain call or of a plain call carried to its runtime host grain (06-08: apps, spaces and
// workspaces are plain neurons, so their calls reach Orleans' filters as one RuntimeInvocation).
internal static class Calls
{
    public static (string? Contract, string? Method) Of(System.Reflection.MethodInfo? method, global::Orleans.Serialization.Invocation.IInvokable request)
    {
        if (request.GetArgumentCount() > 0 && request.GetArgument(0) is global::DigitalBrain.Invocation.RuntimeInvocation call) { return (call.Contract, call.Method); }
        var alias = method?.DeclaringType?.GetCustomAttributes(typeof(global::Orleans.AliasAttribute), false).OfType<global::Orleans.AliasAttribute>().FirstOrDefault()?.Alias;
        return (alias, method?.Name);
    }

    public static bool Is(System.Reflection.MethodInfo? method, global::Orleans.Serialization.Invocation.IInvokable request, string contract, string name)
        => Of(method, request) is var (called, invoked) && called == contract && invoked == name;
}
