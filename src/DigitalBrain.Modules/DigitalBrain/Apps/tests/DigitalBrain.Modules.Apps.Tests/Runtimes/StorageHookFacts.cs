using DigitalBrain.Apps;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.Modules.Apps.Tests.Runtimes;

public sealed class StorageHookFacts
{
    private sealed class RecordingHook : IAppStorageHook
    {
        public List<(string Brain, string AppKey, string[] Keys)> Deployed { get; } = [];
        public List<(string Brain, string AppKey, string[] Keys)> Retired { get; } = [];

        public Task OnDeployed(string brainId, string appKey, string[] storageKeys, CancellationToken cancellationToken)
        {
            Deployed.Add((brainId, appKey, storageKeys));
            return Task.CompletedTask;
        }

        public Task OnRetired(string brainId, string appKey, string[] storageKeys, CancellationToken cancellationToken)
        {
            Retired.Add((brainId, appKey, storageKeys));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ABuildAndADeleteDriveTheRegisteredStorageHookWithTheAppsKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        var hook = new RecordingHook();
        var runtime = new DeployableRuntimeFacts.FakeRuntime();
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>()
            .ConfigureSilo(silo =>
            {
                silo.Services.RemoveAll<IDeployableRuntime>();
                silo.Services.AddSingleton<IDeployableRuntime>(runtime);
                silo.Services.AddSingleton<IAppStorageHook>(hook);
            })
            .StartAsync(ct);
        var key = IApp.Key("hook-facts", "hooked");
        var app = brain.Get<IApp>(key);
        await app.Create(new("Hooked", "hooked", [], [], []), "## Scenario: Hooks\nIt hooks.");
        await app.BeginBuild(null);

        await app.CompleteBuild(await app.CurrentBuildAttempt(), new() { ["tests.cs"] = "// t", ["behaviors/track.cs"] = "// b" }, null);

        var deployed = Assert.Single(hook.Deployed);
        Assert.Equal(key, deployed.AppKey);
        Assert.Contains("fake-behaviors/track.cs", deployed.Keys);

        await app.Delete();

        var retired = Assert.Single(hook.Retired);
        Assert.Equal(key, retired.AppKey);
        Assert.Contains("fake-behaviors/track.cs", retired.Keys);
    }

    [Fact]
    public async Task ReplacingTheProgramKeepsStorageSettingsAndConnectionsAndStopsOnlyRemovedBehaviors()
    {
        var ct = TestContext.Current.CancellationToken;
        var hook = new RecordingHook();
        var runtime = new DeployableRuntimeFacts.FakeRuntime();
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>()
            .ConfigureSilo(silo =>
            {
                silo.Services.RemoveAll<IDeployableRuntime>();
                silo.Services.AddSingleton<IDeployableRuntime>(runtime);
                silo.Services.AddSingleton<IAppStorageHook>(hook);
            })
            .StartAsync(ct);
        var key = IApp.Key("hook-facts", "replaced");
        var app = brain.Get<IApp>(key);
        var manifest = new AppManifest("Replaced", "replaced", [], [new("Text", "what to say", "hi")], []);
        await app.Import(new("replaced", "1.0.0", manifest, "## Scenario: Stores\nIt stores.",
            new() { ["tests.cs"] = "// t", ["behaviors/keep.cs"] = "// v1", ["behaviors/drop.cs"] = "// v1" }));
        await app.Configure(new() { ["Text"] = "mine" }, new() { ["gmail"] = "my-gmail" });

        var next = manifest with { Settings = [.. manifest.Settings, new("Mood", "how to say it", "calm")] };
        var replaced = await app.Replace(new("replaced", "1.1.0", next, "## Scenario: Stores\nIt stores more.",
            new() { ["tests.cs"] = "// t", ["behaviors/keep.cs"] = "// v2", ["behaviors/new.cs"] = "// v2" }));

        // The app's storage is never retired: only the removed behavior's file stops.
        Assert.Empty(hook.Retired);
        Assert.Equal(["fake-behaviors/drop.cs"], Assert.Single(runtime.Retired).Keys);
        // Kept behaviors hot-swap, new ones deploy, and ownership moves to the current keys.
        Assert.Equal("// v2", runtime.Swapped.Last(item => item.Programs.ContainsKey("behaviors/keep.cs")).Programs["behaviors/keep.cs"]);
        Assert.Contains(runtime.Deployed, item => item.Programs.ContainsKey("behaviors/new.cs"));
        Assert.Equal(["fake-behaviors/keep.cs", "fake-behaviors/new.cs"], hook.Deployed.Last().Keys.Order(StringComparer.Ordinal).ToArray());
        // What the person chose survives; a newly declared setting gets its default.
        Assert.Equal("1.1.0", replaced.Version);
        Assert.Equal(AppStatus.Live, replaced.Status);
        Assert.Equal("mine", replaced.Settings["Text"]);
        Assert.Equal("calm", replaced.Settings["Mood"]);
        Assert.Equal("my-gmail", replaced.Connections["gmail"]);
        Assert.False(replaced.Files.ContainsKey("behaviors/drop.cs"));
    }

    [Fact]
    public async Task ReplacingAnAppThatDoesNotExistOrWithAnotherAppsBundleIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>()
            .ConfigureSilo(silo => { silo.Services.RemoveAll<IDeployableRuntime>(); silo.Services.AddSingleton<IDeployableRuntime>(new DeployableRuntimeFacts.FakeRuntime()); })
            .StartAsync(ct);
        var manifest = new AppManifest("Replaced", "replaced", [], [], []);
        var missing = brain.Get<IApp>(IApp.Key("hook-facts", "missing"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => missing.Replace(new("missing", "1.0.0", manifest, "spec", new())));

        var app = brain.Get<IApp>(IApp.Key("hook-facts", "mine"));
        await app.Import(new("mine", "1.0.0", manifest, "spec", new() { ["tests.cs"] = "// t" }));
        await Assert.ThrowsAsync<InvalidDataException>(() => app.Replace(new("other", "1.1.0", manifest, "spec", new())));
        Assert.Equal("1.0.0", (await app.Read()).Version);
    }
}
