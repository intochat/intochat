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
}
