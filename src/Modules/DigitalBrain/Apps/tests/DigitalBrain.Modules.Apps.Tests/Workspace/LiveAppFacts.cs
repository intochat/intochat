using DigitalBrain.Apps;
using DigitalBrain.Microsoft.CSharp;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Apps.Tests.Workspace;

public sealed class LiveAppFacts
{
    private sealed class FakeRuntime : IDeployableRuntime
    {
        public List<RuntimeDeployment> Deployed { get; } = [];
        public List<(string AppKey, string[] Keys)> Retired { get; } = [];
        public string Name => "fake";

        public Task<string[]> Deploy(RuntimeDeployment deployment, CancellationToken cancellationToken)
        {
            if (deployment.Programs.Values.Any(source => source.Contains("BROKEN", StringComparison.Ordinal)))
            { throw new InvalidOperationException("The behavior does not compile."); }
            Deployed.Add(deployment);
            return Task.FromResult(deployment.Programs.Keys.Select(path => "fake-" + path).ToArray());
        }

        public Task Retire(string appKey, string[] storageKeys, CancellationToken cancellationToken)
        {
            Retired.Add((appKey, storageKeys));
            return Task.CompletedTask;
        }
    }

    private static string Key() => "workspace-test/apps/" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task ACreatedAppIsAliveEditablePerBehaviorAndAllowedRed()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new FakeRuntime();
        await using var brain = await PackageBrain.StartAsync(ct,
            silo => silo.Services.AddSingleton<IDeployableRuntime>(runtime));
        var app = brain.Get<IApp>(Key());

        var created = await app.Create(new(Guid.NewGuid(), "Tracker", "Tracks things.", "fake"));
        Assert.Equal(AppStatus.Installed, created.Status);
        Assert.Null(created.Revision);

        // Writing a behavior deploys exactly that file.
        var written = await app.WriteBehavior("behaviors/track.cs", "// watches", Guid.NewGuid());
        var deployment = Assert.Single(runtime.Deployed);
        Assert.Equal(["behaviors/track.cs"], deployment.Programs.Keys);
        Assert.Equal(["fake-behaviors/track.cs"], written.CSharpFiles);

        // A broken edit records the failure on the behavior; the app keeps serving.
        var broken = await app.WriteBehavior("behaviors/report.cs", "// BROKEN", Guid.NewGuid());
        Assert.Equal(AppStatus.Installed, broken.Status);
        var content = await app.ReadContent();
        Assert.Equal("The behavior does not compile.", content.BehaviorErrors["behaviors/report.cs"]);
        Assert.Equal("// BROKEN", content.Files["behaviors/report.cs"]);
        Assert.Equal("Tracker", content.Manifest.Title);

        // Fixing the behavior clears the failure.
        await app.WriteBehavior("behaviors/report.cs", "// fixed", Guid.NewGuid());
        Assert.Empty((await app.ReadContent()).BehaviorErrors);

        // The red/fixed state survives reactivation.
        await brain.Brain.DeactivateAsync(app, ct);
        var reloaded = await app.ReadContent();
        Assert.Equal("// fixed", reloaded.Files["behaviors/report.cs"]);
        Assert.Equal(AppStatus.Installed, (await app.Read()).Status);
    }

    [Fact]
    public async Task TheDocumentIsCanonicalAndVerdictsBindToItsScenarios()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new FakeRuntime();
        await using var brain = await PackageBrain.StartAsync(ct,
            silo => silo.Services.AddSingleton<IDeployableRuntime>(runtime));
        var app = brain.Get<IApp>(Key());
        await app.Create(new(Guid.NewGuid(), "Tracker", "Tracks things.", "fake"));

        var document = new AppAuthoringDocument(1, "Tracks the feed.",
            [new AppBehaviorDocument("b1", "Track", "Watches the feed.", ["behaviors/track.cs"], ["s1"])],
            [new AppScenarioDocument("s1", "It tracks", "When something happens it is tracked.", false)]);
        await app.SaveDocument(document, Guid.NewGuid());

        await app.RecordVerdicts([new("It tracks", true, "", "s1")], Guid.NewGuid());
        var content = await app.ReadContent();
        Assert.Equal("s1", Assert.Single(content.Verdicts).ScenarioId);
        Assert.True(content.Verdicts.Single().Passed);
    }

    [Fact]
    public async Task DeletingABehaviorRetiresItsDeployedKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new FakeRuntime();
        await using var brain = await PackageBrain.StartAsync(ct,
            silo => silo.Services.AddSingleton<IDeployableRuntime>(runtime));
        var app = brain.Get<IApp>(Key());
        await app.Create(new(Guid.NewGuid(), "Tracker", "Tracks things.", "fake"));
        await app.WriteBehavior("behaviors/track.cs", "// watches", Guid.NewGuid());

        await app.DeleteBehavior("behaviors/track.cs", Guid.NewGuid());

        var retired = Assert.Single(runtime.Retired);
        Assert.Equal(["fake-behaviors/track.cs"], retired.Keys);
        Assert.Empty((await app.ReadContent()).Files);
        Assert.Empty((await app.Read()).CSharpFiles);
    }

    [Fact]
    public async Task EditingACSharpBehaviorHotSwapsTheSameFileAndRestartsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await PackageBrain.StartAsync(ct);
        var app = brain.Get<IApp>(Key());
        await app.Create(new(Guid.NewGuid(), "Shouter", "Shouts.", PackageManifest.CSharpRuntime));

        var first = await app.WriteBehavior("behaviors/shout.cs", "// v1", Guid.NewGuid());
        var key = Assert.Single(first.CSharpFiles);
        Assert.Equal("// v1", RecordingCSharpFile.Files[key].Source);
        Assert.Equal(CSharpFileStatus.Running, RecordingCSharpFile.Files[key].Status);

        var second = await app.WriteBehavior("behaviors/shout.cs", "// v2", Guid.NewGuid());

        // Same key, new source, running again: the edit swapped the file, not the app.
        Assert.Equal(key, Assert.Single(second.CSharpFiles));
        Assert.Equal("// v2", RecordingCSharpFile.Files[key].Source);
        Assert.Equal(CSharpFileStatus.Running, RecordingCSharpFile.Files[key].Status);
    }

    [Fact]
    public async Task TwoBrainsLiveAppsAreIsolated()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new FakeRuntime();
        await using var brain = await PackageBrain.StartAsync(ct,
            silo => silo.Services.AddSingleton<IDeployableRuntime>(runtime));
        var alice = brain.Get<IApp>("workspace-alice/apps/" + Guid.NewGuid().ToString("N"));
        var bob = brain.Get<IApp>("workspace-bob/apps/" + Guid.NewGuid().ToString("N"));
        await alice.Create(new(Guid.NewGuid(), "Alpha", "A.", "fake"));
        await bob.Create(new(Guid.NewGuid(), "Beta", "B.", "fake"));
        await alice.WriteBehavior("behaviors/a.cs", "// a", Guid.NewGuid());

        Assert.Empty((await bob.ReadContent()).Files);
        Assert.Equal("Alpha", (await alice.ReadContent()).Manifest.Title);
        Assert.Equal("Beta", (await bob.ReadContent()).Manifest.Title);
    }
}
