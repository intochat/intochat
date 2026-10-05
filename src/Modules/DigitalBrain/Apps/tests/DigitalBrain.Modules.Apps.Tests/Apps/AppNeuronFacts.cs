using DigitalBrain.Apps.Signals;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Apps.Tests.Apps;

public sealed class AppNeuronFacts
{
    private const string Brain = "app-neuron-facts";

    private static AppManifest Manifest(string title = "Echo") =>
        new(title, "echoes", [new("ask", "ask it")], [new("Text", "what to say", "hi")], []);

    private static Task<ModuleBrain> Start() =>
        ModuleTest.Create().WithModule<AppsModule>().StartAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task CreatingAnAppMintsVersionZeroOneZeroAndPublishesAppChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        var app = brain.Get<IApp>(IApp.Key(Brain, "echo"));
        await using var changes = await brain.SubscribeAsync<AppChanged>(app, ct);
        var created = await app.Create(Manifest(), "## Scenario: Echoes\nIt echoes.");
        Assert.Equal("0.1.0", created.Version);
        Assert.Equal(AppStatus.Drafted, created.Status);
        Assert.Equal("echo", created.Name);
        await using var stream = changes.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Equal(("echo", "0.1.0", AppStatus.Drafted), (stream.Current.Name, stream.Current.Version, stream.Current.Status));
    }

    [Fact]
    public async Task CreatingAnAppTwiceIsRefused()
    {
        await using var brain = await Start();
        var app = brain.Get<IApp>(IApp.Key(Brain, "twice"));
        await app.Create(Manifest(), "spec");
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.Create(Manifest(), "spec"));
    }

    [Fact]
    public async Task SavingTheSpecBumpsMinorAndSavingAFileBumpsPatch()
    {
        await using var brain = await Start();
        var app = brain.Get<IApp>(IApp.Key(Brain, "bumps"));
        await app.Create(Manifest(), "spec");
        var afterSpec = await app.SaveSpec("## Scenario: More\nMore.", Guid.NewGuid());
        Assert.Equal("0.2.0", afterSpec.Version);
        var afterFile = await app.WriteFile("notes.md", "notes", Guid.NewGuid());
        Assert.Equal("0.2.1", afterFile.Version);
        var afterBehavior = await app.WriteBehavior("behaviors/echo.cs", "// echo", Guid.NewGuid());
        Assert.Equal("0.2.2", afterBehavior.Version);
    }

    [Fact]
    public async Task RollbackRestoresAVersionAndRefusesUnknownOnes()
    {
        await using var brain = await Start();
        var app = brain.Get<IApp>(IApp.Key(Brain, "roll"));
        await app.Create(Manifest(), "first spec");
        await app.SaveSpec("second spec", Guid.NewGuid());
        var rolled = await app.Rollback("0.1.0");
        Assert.Equal("first spec", rolled.Spec);
        Assert.Equal("0.2.1", rolled.Version);
        var unknown = await Assert.ThrowsAsync<ArgumentException>(() => app.Rollback("9.9.9"));
        Assert.Contains("0.1.0", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecondBuildIsRefusedWhileOneIsFresh()
    {
        await using var brain = await Start();
        var app = brain.Get<IApp>(IApp.Key(Brain, "busy"));
        await app.Create(Manifest(), "spec");
        var building = await app.BeginBuild(null);
        Assert.Equal(AppStatus.Building, building.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.BeginBuild(null));
    }

    [Fact]
    public async Task AStaleBuildingAppReArmsAndTheOldWorkersCompleteBuildIsIgnored()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>()
            .ConfigureSilo(silo => silo.Services.PostConfigure<AppsOptions>(options => options.BuildFreshForSeconds = 0))
            .StartAsync(ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "stale"));
        await app.Create(Manifest(), "spec");
        await app.BeginBuild(null);
        var oldAttempt = await app.CurrentBuildAttempt();
        var rearmed = await app.BeginBuild(null);
        Assert.Equal(AppStatus.Building, rearmed.Status);
        Assert.NotEqual(oldAttempt, await app.CurrentBuildAttempt());
        var ignored = await app.CompleteBuild(oldAttempt, new() { ["tests.cs"] = "// stale" }, null);
        Assert.Equal(AppStatus.Building, ignored.Status);
        Assert.False(ignored.Files.ContainsKey("tests.cs"));
    }

    [Fact]
    public async Task CompleteBuildWithFilesGoesLiveAndWithErrorGoesFailed()
    {
        await using var brain = await Start();
        var app = brain.Get<IApp>(IApp.Key(Brain, "finish"));
        await app.Create(Manifest(), "spec");
        await app.BeginBuild(null);
        var attempt = await app.CurrentBuildAttempt();
        var live = await app.CompleteBuild(attempt, new() { ["tests.cs"] = "// t", ["behaviors/echo.cs"] = "// b" }, null);
        Assert.Equal(AppStatus.Live, live.Status);
        Assert.Equal("// t", live.Files["tests.cs"]);
        Assert.Equal("// b", live.Files["behaviors/echo.cs"]);

        await app.BeginBuild(null);
        var second = await app.CurrentBuildAttempt();
        var failed = await app.CompleteBuild(second, null, "the model never answered");
        Assert.Equal(AppStatus.Failed, failed.Status);
        Assert.Contains(failed.Activity, line => line.Contains("the model never answered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProgressAppendsToActivityWhileBuilding()
    {
        await using var brain = await Start();
        var app = brain.Get<IApp>(IApp.Key(Brain, "progress"));
        await app.Create(Manifest(), "spec");
        await app.BeginBuild(null);
        await app.Progress("round 1: compile-checking tests.cs");
        var snapshot = await app.Read();
        Assert.Contains("round 1: compile-checking tests.cs", snapshot.Activity);
    }

    [Fact]
    public async Task ExportRefusesUnverifiedVersionsAndStripsConnections()
    {
        await using var brain = await Start();
        var app = brain.Get<IApp>(IApp.Key(Brain, "export"));
        await app.Create(Manifest(), "spec");
        await app.Configure(new() { ["Text"] = "secret greeting" }, new() { ["gmail"] = "my-connection" });
        await Assert.ThrowsAsync<InvalidOperationException>(app.Export);
        var current = await app.Read();
        await app.RecordVerdicts([new("Echoes", true, "ok")], green: true);
        var bundle = await app.Export();
        Assert.Equal(current.Version, bundle.Version);
        Assert.DoesNotContain("my-connection", System.Text.Json.JsonSerializer.Serialize(bundle), StringComparison.Ordinal);
        Assert.DoesNotContain("secret greeting", System.Text.Json.JsonSerializer.Serialize(bundle), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordingVerdictsPublishesAppVerified()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        var app = brain.Get<IApp>(IApp.Key(Brain, "verified"));
        await app.Create(Manifest(), "spec");
        await using var verdicts = await brain.SubscribeAsync<AppVerified>(app, ct);
        await app.RecordVerdicts([new("Echoes", true, "ok")], green: true);
        await using var stream = verdicts.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.True(stream.Current.Green);
        Assert.Equal("verified", stream.Current.Name);
    }

    [Fact]
    public async Task ImportMaterializesABundleAndRefusesANameClash()
    {
        await using var brain = await Start();
        var bundle = new AppBundle("copy", "1.2.0", Manifest("Copied"), "## Scenario: Echoes\nIt echoes.",
            new() { ["tests.cs"] = "// t", ["behaviors/echo.cs"] = "// b" });
        var app = brain.Get<IApp>(IApp.Key(Brain, "copy"));
        var imported = await app.Import(bundle);
        Assert.Equal(AppStatus.Live, imported.Status);
        Assert.Equal("1.2.0", imported.Version);
        Assert.Empty(imported.Connections);
        Assert.Equal("// b", imported.Files["behaviors/echo.cs"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.Import(bundle));
    }

    [Fact]
    public async Task VerdictsRecordedForAnOlderVersionDoNotMarkTheCurrentOneGreen()
    {
        await using var brain = await Start();
        var app = brain.Get<IApp>(IApp.Key(Brain, "stale-verdict"));
        await app.Create(Manifest(), "## Scenario: Echoes\nIt echoes.");
        var tested = (await app.Read()).Version;
        await app.SaveSpec("## Scenario: Echoes\nIt echoes twice.", Guid.NewGuid());
        await app.RecordVerdicts([new("Echoes", true, "ok")], green: true, tested);
        await Assert.ThrowsAsync<InvalidOperationException>(app.Export);
        var snapshot = await app.Read();
        Assert.True(snapshot.Versions.Single(version => version.Version == tested).Green);
        Assert.Null(snapshot.Versions.Single(version => version.Version == snapshot.Version).Green);
    }

    [Fact]
    public async Task AStaleWorkersManifestIsIgnoredWithItsFiles()
    {
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>()
            .ConfigureSilo(silo => silo.Services.PostConfigure<AppsOptions>(options => options.BuildFreshForSeconds = 0))
            .StartAsync(TestContext.Current.CancellationToken);
        var app = brain.Get<IApp>(IApp.Key(Brain, "stale-manifest"));
        await app.Create(Manifest(), "spec");
        await app.BeginBuild(null);
        var oldAttempt = await app.CurrentBuildAttempt();
        await app.BeginBuild(null);
        var ignored = await app.CompleteBuild(oldAttempt, new() { ["tests.cs"] = "// stale" }, null,
            new AppManifest("Hijacked", "", [], [], []));
        Assert.Equal("Echo", ignored.Manifest.Title);
    }

    [Fact]
    public async Task HistoryIsCappedAtTwentyVersions()
    {
        await using var brain = await Start();
        var app = brain.Get<IApp>(IApp.Key(Brain, "history"));
        await app.Create(Manifest(), "spec");
        for (var i = 0; i < 24; i++) { await app.WriteFile("notes.md", $"notes {i}", Guid.NewGuid()); }
        var snapshot = await app.Read();
        Assert.Equal(20, snapshot.Versions.Length);
        Assert.Equal("0.1.24", snapshot.Version);
    }
}
