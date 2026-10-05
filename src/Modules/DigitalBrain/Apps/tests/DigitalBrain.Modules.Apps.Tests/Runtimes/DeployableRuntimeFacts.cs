using DigitalBrain.Apps;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.Modules.Apps.Tests.Runtimes;

public sealed class DeployableRuntimeFacts
{
    internal sealed class FakeRuntime : IDeployableRuntime
    {
        public List<RuntimeDeployment> Deployed { get; } = [];
        public List<(string AppKey, string[] Keys)> Retired { get; } = [];
        public string Name => AppFiles.Runtime;

        public string[] Keys(string appKey, int generation, IReadOnlyList<string> paths)
            => [.. paths.Select(path => "fake-" + path)];

        public Task<string[]> Deploy(RuntimeDeployment deployment, CancellationToken cancellationToken)
        {
            Deployed.Add(deployment);
            return Task.FromResult<string[]>([.. deployment.Programs.Keys.Select(path => "fake-" + path)]);
        }

        public Task Retire(string appKey, string[] storageKeys, CancellationToken cancellationToken)
        {
            Retired.Add((appKey, storageKeys));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ABuildDeploysBehaviorsThroughTheRuntimeAndDeleteRetiresTheSameKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new FakeRuntime();
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>()
            .ConfigureSilo(silo => { silo.Services.RemoveAll<IDeployableRuntime>(); silo.Services.AddSingleton<IDeployableRuntime>(runtime); })
            .StartAsync(ct);
        var app = brain.Get<IApp>(IApp.Key("runtime-facts", "tracker"));
        await app.Create(new("Tracker", "tracks", [new("ask", "ask")], [new("Feed", "what to watch", "news")], []), "## Scenario: Tracks\nIt tracks.");
        await app.BeginBuild(null);

        await app.CompleteBuild(await app.CurrentBuildAttempt(),
            new() { ["tests.cs"] = "// t", ["behaviors/track.cs"] = "// b" }, null);

        var deployment = Assert.Single(runtime.Deployed);
        Assert.Equal(["behaviors/track.cs"], [.. deployment.Programs.Keys]);
        Assert.True(deployment.Configuration.ContainsKey("App"));
        Assert.Equal("news", deployment.Configuration["Feed"]);

        await app.Delete();
        var retired = Assert.Single(runtime.Retired);
        Assert.Contains("fake-behaviors/track.cs", retired.Keys);
    }
}
