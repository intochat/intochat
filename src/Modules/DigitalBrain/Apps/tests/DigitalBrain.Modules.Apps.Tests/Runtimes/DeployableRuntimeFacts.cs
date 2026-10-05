using DigitalBrain.Apps;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Apps.Tests.Runtimes;

public sealed class DeployableRuntimeFacts
{
    private sealed class FakeRuntime : IDeployableRuntime
    {
        public List<RuntimeDeployment> Deployed { get; } = [];
        public List<(string AppKey, string[] Keys)> Retired { get; } = [];
        public string Name => "fake";

        public string[] Keys(string appKey, int generation, IReadOnlyList<string> paths)
            => paths.Select(path => "fake-" + path).ToArray();

        public Task<string[]> Deploy(RuntimeDeployment deployment, CancellationToken cancellationToken)
        {
            Deployed.Add(deployment);
            return Task.FromResult(deployment.Programs.Keys.Select(path => "fake-" + deployment.Generation + "-" + path).ToArray());
        }

        public Task Retire(string appKey, string[] storageKeys, CancellationToken cancellationToken)
        {
            Retired.Add((appKey, storageKeys));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task InstallDeploysThroughTheRuntimeAndUninstallRetiresTheSameKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new FakeRuntime();
        await using var brain = await PackageBrain.StartAsync(ct,
            silo => silo.Services.AddSingleton<IDeployableRuntime>(runtime));
        var package = PackageId.Parse("alice/fake-tracker");
        Caller.As("alice");
        await brain.AuthorizeCallerAsync();
        var tracker = PackageSamples.Tracker();
        var content = tracker with { Manifest = tracker.Manifest with { Runtime = "fake" } };
        var revision = await brain.Get<IPackage>(package.ToString()).Commit(brain.Commit(null, content, "fake runtime"));
        await brain.Get<IPackage>(package.ToString()).Publish(new(Guid.NewGuid(), revision.Id));
        Caller.Clear();
        var app = brain.Get<IApp>("workspace-test/apps/" + Guid.NewGuid().ToString("N"));

        var installed = await app.Install(new(Guid.NewGuid(), new(package, revision.Id), new Dictionary<string, string>()));

        var deployment = Assert.Single(runtime.Deployed);
        Assert.Equal(["behaviors/report.cs", "behaviors/track.cs"], deployment.Programs.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(installed.CSharpFiles.Order(StringComparer.Ordinal),
            runtime.Deployed.Single().Programs.Keys.Select(path => "fake-1-" + path).Order(StringComparer.Ordinal));
        Assert.True(deployment.Configuration.ContainsKey("App"));

        await app.Uninstall(new(Guid.NewGuid()));

        var retired = Assert.Single(runtime.Retired);
        Assert.Equal(installed.CSharpFiles.Order(StringComparer.Ordinal), retired.Keys.Order(StringComparer.Ordinal));
    }
}
