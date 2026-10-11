using DigitalBrain.Apps;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Apps.Tests.Runtimes;

public sealed class StorageHookFacts
{
    private sealed class RecordingHook : IAppStorageHook
    {
        public List<(string Brain, string AppKey)> Retired { get; } = [];

        public Task OnRetired(string brainId, string appKey, CancellationToken cancellationToken)
        {
            Retired.Add((brainId, appKey));
            return Task.CompletedTask;
        }

        public List<string> Created { get; } = [];

        public Task OnCreated(string brainId, string appKey, CancellationToken cancellationToken)
        {
            Created.Add(appKey);
            return Task.CompletedTask;
        }

        public List<string> Rebinds { get; } = [];
        public bool Refuse { get; set; }

        public Task RequireRebindable(string brainId, string appKey, string slot, CancellationToken cancellationToken)
        {
            Rebinds.Add(slot);
            return Refuse ? throw new InvalidOperationException("The data depends on this binding.") : Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ConfigureAsksStorageHooksBeforeChangingASlotBinding()
    {
        var hook = new RecordingHook();
        await using var brain = await StartWith(hook);
        var app = brain.Get<IApp>(IApp.Key("hook-facts", "rebound"));
        await app.Create(new("Rebound", "rebound", [], [], [new("db", "postgres")]), "## Scenario: Binds\nIt binds.");

        await app.Configure([], new() { ["db"] = "c1" });
        await app.Configure([], new() { ["db"] = "c1" });
        await app.Configure(new() { ["Text"] = "settings alone" }, []);
        Assert.Equal(["db"], hook.Rebinds);

        await app.Configure([], new() { ["db"] = "c2" });
        Assert.Equal(["db", "db"], hook.Rebinds);
    }

    [Fact]
    public async Task ARefusingHookLeavesTheBindingUnchanged()
    {
        var hook = new RecordingHook();
        await using var brain = await StartWith(hook);
        var app = brain.Get<IApp>(IApp.Key("hook-facts", "refused"));
        await app.Create(new("Refused", "refused", [], [], [new("db", "postgres")]), "## Scenario: Binds\nIt binds.");
        await app.Configure([], new() { ["db"] = "c1" });

        hook.Refuse = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.Configure(new() { ["Text"] = "new" }, new() { ["db"] = "c2" }));

        var snapshot = await app.Read();
        Assert.Equal("c1", snapshot.Connections["db"]);
        Assert.False(snapshot.Settings.ContainsKey("Text"));
    }

    private static Task<ITestBrain> StartWith(RecordingHook hook)
        => Start(hook, new FakeAppRuntime());

    private static Task<ITestBrain> Start(RecordingHook hook, FakeAppRuntime runtime)
        => TestBrain.Create().WithModule<AppsModule>()
            .WithFakeAppRuntime(runtime)
            // A storage hook is what a tables module plugs in; the recording one stands in for Postgres's.
            .ConfigureServices(services => services.AddSingleton<IAppStorageHook>(hook))
            .StartAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task ADeleteRetiresTheAppFromEveryStorageHook()
    {
        var ct = TestContext.Current.CancellationToken;
        var hook = new RecordingHook();
        var runtime = new FakeAppRuntime();
        await using var brain = await Start(hook, runtime);
        var key = IApp.Key("hook-facts", "hooked");
        var app = brain.Get<IApp>(key);
        await app.Create(new("Hooked", "hooked", [], [], []), "## Scenario: Hooks\nIt hooks.");
        await app.BeginBuild(null);

        await app.CompleteBuild(await app.CurrentBuildAttempt(), new() { ["synapses/track.cs"] = "// b" }, null);
        Assert.Empty(hook.Retired);

        await app.Delete();

        var retired = Assert.Single(hook.Retired);
        Assert.Equal(key, retired.AppKey);
    }

    [Fact]
    public async Task AnAppDeletedAndReinstalledReopensItsStorageButAnUpdateDoesNot()
    {
        var hook = new RecordingHook();
        await using var brain = await Start(hook, new FakeAppRuntime());
        var key = IApp.Key("hook-facts", "again");
        var app = brain.Get<IApp>(key);
        var manifest = new AppManifest("Again", "again", [], [], []);
        await app.Import(new("again", "1.0.0", manifest, "spec", new() { ["notes.md"] = "// t" }));
        await app.Replace(new("again", "1.1.0", manifest, "spec", new() { ["notes.md"] = "// t2" }));
        Assert.Equal([key], hook.Created);

        await app.Delete();
        await app.Import(new("again", "1.1.0", manifest, "spec", new() { ["notes.md"] = "// t2" }));

        Assert.Equal([key], hook.Retired.Select(retired => retired.AppKey));
        Assert.Equal([key, key], hook.Created);
    }

    [Fact]
    public async Task ReplacingTheProgramKeepsStorageSettingsAndConnectionsAndDeploysOnlyTheNewOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var hook = new RecordingHook();
        var runtime = new FakeAppRuntime();
        await using var brain = await Start(hook, runtime);
        var key = IApp.Key("hook-facts", "replaced");
        var app = brain.Get<IApp>(key);
        var manifest = new AppManifest("Replaced", "replaced", [], [new("Text", "what to say", "hi")], []);
        await app.Import(new("replaced", "1.0.0", manifest, "## Scenario: Stores\nIt stores.",
            new() { ["synapses/keep.cs"] = "// v1", ["synapses/drop.cs"] = "// v1" }));
        await app.Configure(new() { ["Text"] = "mine" }, new() { ["gmail"] = "my-gmail" });

        var next = manifest with { Settings = [.. manifest.Settings, new("Mood", "how to say it", "calm")] };
        var replaced = await app.Replace(new("replaced", "1.1.0", next, "## Scenario: Stores\nIt stores more.",
            new() { ["synapses/keep.cs"] = "// v2", ["synapses/new.cs"] = "// v2" }));

        // The app's storage is never retired by an update; the whole program is redeployed.
        Assert.Empty(hook.Retired);
        Assert.Equal(["synapses/keep.cs", "synapses/new.cs"], runtime.Deployed.Last().Sources.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("// v2", runtime.Deployed.Last().Sources["synapses/keep.cs"]);
        // What the person chose survives; a newly declared setting gets its default.
        Assert.Equal("1.1.0", replaced.Version);
        Assert.Equal(AppStatus.Live, replaced.Status);
        Assert.Equal("mine", replaced.Settings["Text"]);
        Assert.Equal("calm", replaced.Settings["Mood"]);
        Assert.Equal("my-gmail", replaced.Connections["gmail"]);
        Assert.False(replaced.Files.ContainsKey("synapses/drop.cs"));
    }

    [Fact]
    public async Task ReplacingAnAppThatDoesNotExistOrWithAnotherAppsBundleIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(new RecordingHook(), new FakeAppRuntime());
        var manifest = new AppManifest("Replaced", "replaced", [], [], []);
        var missing = brain.Get<IApp>(IApp.Key("hook-facts", "missing"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => missing.Replace(new("missing", "1.0.0", manifest, "spec", new())));

        var app = brain.Get<IApp>(IApp.Key("hook-facts", "mine"));
        await app.Import(new("mine", "1.0.0", manifest, "spec", new() { ["notes.md"] = "// t" }));
        await Assert.ThrowsAsync<InvalidDataException>(() => app.Replace(new("other", "1.1.0", manifest, "spec", new())));
        Assert.Equal("1.0.0", (await app.Read()).Version);
    }
}
