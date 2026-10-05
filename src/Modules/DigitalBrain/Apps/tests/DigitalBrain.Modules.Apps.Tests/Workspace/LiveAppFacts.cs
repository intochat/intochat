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

        public string[] Keys(string appKey, int generation, IReadOnlyList<string> paths)
            => paths.Select(path => "fake-" + path).ToArray();

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
    public async Task AFailedDeployStillTracksItsKeySoUninstallRetiresIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new FakeRuntime();
        await using var brain = await PackageBrain.StartAsync(ct,
            silo => silo.Services.AddSingleton<IDeployableRuntime>(runtime));
        var app = brain.Get<IApp>(Key());
        await app.Create(new(Guid.NewGuid(), "Tracker", "Tracks things.", "fake"));
        await app.WriteBehavior("behaviors/report.cs", "// BROKEN", Guid.NewGuid());

        await app.Uninstall(new(Guid.NewGuid()));

        var retired = Assert.Single(runtime.Retired);
        Assert.Contains("fake-behaviors/report.cs", retired.Keys);
    }

    [Fact]
    public async Task EditingAnExistingBehaviorSwapsItInsteadOfRecreatingIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await PackageBrain.StartAsync(ct);
        var app = brain.Get<IApp>(Key());
        await app.Create(new(Guid.NewGuid(), "Shouter", "Shouts.", PackageManifest.CSharpRuntime));
        var first = await app.WriteBehavior("behaviors/shout.cs", "// v1", Guid.NewGuid());
        var key = Assert.Single(first.CSharpFiles);
        Assert.False(RecordingCSharpFile.Swaps.ContainsKey(key));

        await app.WriteBehavior("behaviors/shout.cs", "// v2", Guid.NewGuid());

        // The second write swapped: the file's subscriptions and buffers survive the edit.
        Assert.Equal(1, RecordingCSharpFile.Swaps[key]);
    }

    [Fact]
    public async Task AnUninstalledPackageAppCanBeRebornAsALiveApp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await PackageBrain.StartAsync(ct);
        Caller.As("alice");
        await brain.AuthorizeCallerAsync();
        var package = PackageId.Parse("alice/reborn");
        var grain = brain.Get<IPackage>(package.ToString());
        var revision = await grain.Commit(brain.Commit(null, PackageSamples.Tracker(), "v1"));
        await grain.Publish(new(Guid.NewGuid(), revision.Id));
        Caller.Clear();
        var app = brain.Get<IApp>(Key());
        await app.Install(new(Guid.NewGuid(), new(package, revision.Id), new Dictionary<string, string>()));
        await app.Uninstall(new(Guid.NewGuid()));

        await app.Create(new(Guid.NewGuid(), "Reborn", "Lives again.", PackageManifest.CSharpRuntime));
        var written = await app.WriteBehavior("behaviors/again.cs", "// alive", Guid.NewGuid());

        Assert.Null((await app.Read()).Revision);
        Assert.Single(written.CSharpFiles);
    }

    private sealed class EchoRuntime : IAppRuntime
    {
        public string Name => "test-echo";
        public string AuthoringDescription => "test-echo answers input without settings or files.";
        public Task<string> Answer(AppRuntimeRequest request, CancellationToken cancellationToken)
            => Task.FromResult("echo: " + request.Input);
    }

    [Fact]
    public async Task ALiveAppOnAnAnswerRuntimeAnswersInvocationsFromItsOwnContent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await PackageBrain.StartAsync(ct,
            silo => silo.Services.AddAppRuntime<EchoRuntime>());
        var app = brain.Get<IApp>(Key());
        await app.Create(new(Guid.NewGuid(), "Echo", "Echoes.", "test-echo"));
        await app.SaveManifest(new("Echo", "Echoes.", [new PackageOperation("ask", "Ask.")], []), Guid.NewGuid());

        var invocation = await app.Invoke(new(Guid.NewGuid(), "ask", "hello", null));
        var started = TimeProvider.System.GetTimestamp();
        while (invocation.Status == InvocationStatus.Pending && TimeProvider.System.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(100, ct);
            invocation = await app.ReadInvocation(invocation.Id);
        }

        Assert.Equal(InvocationStatus.Completed, invocation.Status);
        Assert.Equal("echo: hello", invocation.Output);
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
