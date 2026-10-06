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
        public List<RuntimeDeployment> Swapped { get; } = [];
        public List<(string AppKey, string[] Keys)> Retired { get; } = [];
        public string Name => AppFiles.Runtime;

        public string[] Keys(string appKey, int generation, IReadOnlyList<string> paths)
            => [.. paths.Select(path => "fake-" + path)];

        public Task<string[]> Deploy(RuntimeDeployment deployment, CancellationToken cancellationToken)
        {
            Deployed.Add(deployment);
            return Task.FromResult<string[]>([.. deployment.Programs.Keys.Select(path => "fake-" + path)]);
        }

        public Task<string[]> Swap(RuntimeDeployment deployment, CancellationToken cancellationToken)
        {
            Swapped.Add(deployment);
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
            new() { ["tests.cs"] = "// t", ["synapses/track.cs"] = "// b" }, null);

        var deployment = Assert.Single(runtime.Deployed);
        Assert.Equal(["synapses/track.cs"], [.. deployment.Programs.Keys]);
        Assert.True(deployment.Configuration.ContainsKey("App"));
        Assert.Equal("news", deployment.Configuration["Feed"]);

        await app.Delete();
        var retired = Assert.Single(runtime.Retired);
        Assert.Contains("fake-synapses/track.cs", retired.Keys);
    }

    [Fact]
    public async Task ABuildDeploysNewBehaviorsAndSwapsOnlyExistingOnes()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new FakeRuntime();
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>()
            .ConfigureSilo(silo => { silo.Services.RemoveAll<IDeployableRuntime>(); silo.Services.AddSingleton<IDeployableRuntime>(runtime); })
            .StartAsync(ct);
        var app = brain.Get<IApp>(IApp.Key("runtime-facts", "fresh"));
        await app.Create(new("Fresh", "fresh", [], [], []), "spec");
        await app.WriteBehavior("synapses/old.cs", "// old", Guid.NewGuid());
        runtime.Deployed.Clear();
        runtime.Swapped.Clear();
        await app.BeginBuild(null);

        await app.CompleteBuild(await app.CurrentBuildAttempt(),
            new() { ["tests.cs"] = "// t", ["synapses/old.cs"] = "// old v2", ["synapses/new.cs"] = "// new" }, null);

        Assert.Equal(["synapses/new.cs"], runtime.Deployed.SelectMany(deployment => deployment.Programs.Keys).Order());
        Assert.Equal(["synapses/old.cs"], runtime.Swapped.SelectMany(deployment => deployment.Programs.Keys).Order());
    }

    [Fact]
    public async Task RollbackRetiresBehaviorsTheTargetVersionLacks()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new FakeRuntime();
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>()
            .ConfigureSilo(silo => { silo.Services.RemoveAll<IDeployableRuntime>(); silo.Services.AddSingleton<IDeployableRuntime>(runtime); })
            .StartAsync(ct);
        var app = brain.Get<IApp>(IApp.Key("runtime-facts", "rollback"));
        await app.Create(new("Tracker", "tracks", [], [], []), "spec");
        await app.WriteBehavior("synapses/keep.cs", "// keep", Guid.NewGuid());
        var before = (await app.Read()).Version;
        await app.WriteBehavior("synapses/extra.cs", "// extra", Guid.NewGuid());

        var rolled = await app.Rollback(before);

        Assert.False(rolled.Files.ContainsKey("synapses/extra.cs"));
        Assert.Contains(runtime.Retired, retired => retired.Keys.Contains("fake-synapses/extra.cs"));
    }
}
