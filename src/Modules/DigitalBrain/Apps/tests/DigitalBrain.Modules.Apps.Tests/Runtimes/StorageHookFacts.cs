using DigitalBrain.Apps;
using Microsoft.Extensions.DependencyInjection;

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
    public async Task DeployAndUninstallDriveTheRegisteredStorageHookWithTheAppsKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        var hook = new RecordingHook();
        await using var brain = await PackageBrain.StartAsync(ct,
            silo => silo.Services.AddSingleton<IAppStorageHook>(hook));
        Caller.As("alice");
        await brain.AuthorizeCallerAsync();
        var package = PackageId.Parse("alice/hooked");
        var grain = brain.Get<IPackage>(package.ToString());
        var revision = await grain.Commit(brain.Commit(null, PackageSamples.Tracker(), "hooked"));
        await grain.Publish(new(Guid.NewGuid(), revision.Id));
        Caller.Clear();
        var key = "workspace-test/apps/" + Guid.NewGuid().ToString("N");
        var app = brain.Get<IApp>(key);

        var installed = await app.Install(new(Guid.NewGuid(), new(package, revision.Id), new Dictionary<string, string>()));

        var deployed = Assert.Single(hook.Deployed);
        Assert.Equal(key, deployed.AppKey);
        Assert.Equal(installed.CSharpFiles.Order(StringComparer.Ordinal), deployed.Keys.Order(StringComparer.Ordinal));

        await app.Uninstall(new(Guid.NewGuid()));

        var retired = Assert.Single(hook.Retired);
        Assert.Equal(installed.CSharpFiles.Order(StringComparer.Ordinal), retired.Keys.Order(StringComparer.Ordinal));
    }
}
