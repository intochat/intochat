using DigitalBrain.Apps.Signals;
using DigitalBrain.Modules.Apps.Tests.Runtimes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.Apps.Tests.Apps;

public sealed class AppNeuronFacts
{
    private const string Brain = "app-neuron-facts";

    private static AppManifest Manifest(string title = "Echo") =>
        new(title, "echoes", [new("ask", "ask it")], [new("Text", "what to say", "hi")], []);

    private static Task<ModuleBrain> Start()
        => Start(new FakeAppRuntime());

    private static Task<ModuleBrain> Start(FakeAppRuntime runtime)
        => ModuleTest.Create().WithModule<AppsModule>()
            .ConfigureSilo(silo => { silo.Services.RemoveAll<IAppRuntime>(); silo.Services.AddSingleton<IAppRuntime>(runtime); })
            .StartAsync(TestContext.Current.CancellationToken);

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
        var afterBehavior = await app.WriteBehavior("synapses/echo.cs", "// echo", Guid.NewGuid());
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
        var ignored = await app.CompleteBuild(oldAttempt, new() { ["synapses/echo.cs"] = "// stale" }, null);
        Assert.Equal(AppStatus.Building, ignored.Status);
        Assert.False(ignored.Files.ContainsKey("synapses/echo.cs"));
    }

    [Fact]
    public async Task CompleteBuildWithFilesGoesLiveAndWithErrorGoesFailed()
    {
        await using var brain = await Start();
        var app = brain.Get<IApp>(IApp.Key(Brain, "finish"));
        await app.Create(Manifest(), "spec");
        await app.BeginBuild(null);
        var attempt = await app.CurrentBuildAttempt();
        var live = await app.CompleteBuild(attempt, new() { ["synapses/echo.cs"] = "// b" }, null);
        Assert.Equal(AppStatus.Live, live.Status);
        Assert.Equal("// b", live.Files["synapses/echo.cs"]);

        await app.BeginBuild(null);
        var second = await app.CurrentBuildAttempt();
        var failed = await app.CompleteBuild(second, null, "the model never answered");
        Assert.Equal(AppStatus.Failed, failed.Status);
        Assert.Contains(failed.Activity, line => line.Contains("the model never answered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABuildWhoseProgramDoesNotCompileGoesFailedAndKeepsItsFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new FakeAppRuntime { Refuse = sources => sources.ContainsKey("synapses/bad.cs") ? new InvalidOperationException("The program does not compile: synapses/bad.cs line 1 CS0029") : null };
        await using var brain = await Start(runtime);
        var app = brain.Get<IApp>(IApp.Key(Brain, "uncompilable"));
        await app.Create(Manifest(), "spec");
        await app.BeginBuild(null);

        var failed = await app.CompleteBuild(await app.CurrentBuildAttempt(), new() { ["synapses/bad.cs"] = "int x = \"no\";" }, null);

        Assert.Equal(AppStatus.Failed, failed.Status);
        Assert.Contains(failed.Activity, line => line.Contains("CS0029", StringComparison.Ordinal));
        Assert.Equal("int x = \"no\";", failed.Files["synapses/bad.cs"]);
    }

    [Fact]
    public async Task TheFirstBuildBumpsMinorAndLaterBuildsBumpPatch()
    {
        await using var brain = await Start();
        var app = brain.Get<IApp>(IApp.Key(Brain, "bumps"));
        await app.Create(Manifest(), "spec");
        await app.BeginBuild(null);
        var first = await app.CompleteBuild(await app.CurrentBuildAttempt(), new() { ["synapses/a.cs"] = "// a" }, null);
        Assert.Equal("0.2.0", first.Version);
        await app.BeginBuild(null);
        var second = await app.CompleteBuild(await app.CurrentBuildAttempt(), new() { ["synapses/b.cs"] = "// b" }, null);
        Assert.Equal("0.2.1", second.Version);
    }

    [Fact]
    public async Task ProgressAppendsToActivityWhileBuilding()
    {
        await using var brain = await Start();
        var app = brain.Get<IApp>(IApp.Key(Brain, "progress"));
        await app.Create(Manifest(), "spec");
        await app.BeginBuild(null);
        await app.Progress("working", "round 1: compile-checking synapses/echo.cs");
        var snapshot = await app.Read();
        Assert.Contains("round 1: compile-checking synapses/echo.cs", snapshot.Activity);
    }

    [Fact]
    public async Task ProgressAnnouncesAChangeOnlyWhenTheActivityLineChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        var app = brain.Get<IApp>(IApp.Key(Brain, "quiet"));
        await app.Create(Manifest(), "spec");
        await app.BeginBuild(null);
        await using var changes = await brain.SubscribeAsync<AppChanged>(app, ct);
        await using var activity = await brain.SubscribeAsync<AppActivity>(app, ct);
        await app.Progress("working", "round 1");
        await app.Progress("working", "round 1");
        await app.Progress("reading", "round 2");

        await using var seen = activity.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        for (var i = 0; i < 3; i++) { Assert.True(await seen.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct)); }
        var count = 0;
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(TimeSpan.FromSeconds(1));
        try { await foreach (var _ in changes.ReadAllAsync(window.Token)) { count++; } }
        catch (OperationCanceledException) when (window.IsCancellationRequested && !ct.IsCancellationRequested) { }
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task ProgressPublishesAppActivityEvenWhenTheAppIsNotBuilding()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        var app = brain.Get<IApp>(IApp.Key(Brain, "activity"));
        await app.Create(Manifest(), "spec");
        await using var activity = await brain.SubscribeAsync<AppActivity>(app, ct);
        await app.Progress("listening", "Heard you");
        await using var stream = activity.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Equal(("activity", "listening", "Heard you"), (stream.Current.App, stream.Current.State, stream.Current.Detail));
        Assert.DoesNotContain("Heard you", (await app.Read()).Activity);
    }

    [Fact]
    public async Task ProgressRefusesAStateOutsideTheTen()
    {
        await using var brain = await Start();
        var app = brain.Get<IApp>(IApp.Key(Brain, "badstate"));
        await app.Create(Manifest(), "spec");
        await Assert.ThrowsAsync<ArgumentException>(() => app.Progress("dancing", "x"));
    }

    [Fact]
    public async Task ExportStripsConnectionsAndPrivateSettings()
    {
        await using var brain = await Start();
        var app = brain.Get<IApp>(IApp.Key(Brain, "export"));
        await app.Create(Manifest(), "spec");
        await app.Configure(new() { ["Text"] = "secret greeting" }, new() { ["gmail"] = "my-connection" });
        var bundle = await app.Export();
        Assert.Equal((await app.Read()).Version, bundle.Version);
        Assert.DoesNotContain("my-connection", System.Text.Json.JsonSerializer.Serialize(bundle), StringComparison.Ordinal);
        Assert.DoesNotContain("secret greeting", System.Text.Json.JsonSerializer.Serialize(bundle), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportMaterializesABundleAndRefusesANameClash()
    {
        await using var brain = await Start();
        var bundle = new AppBundle("copy", "1.2.0", Manifest("Copied"), "## Scenario: Echoes\nIt echoes.",
            new() { ["synapses/echo.cs"] = "// b" });
        var app = brain.Get<IApp>(IApp.Key(Brain, "copy"));
        var imported = await app.Import(bundle);
        Assert.Equal(AppStatus.Live, imported.Status);
        Assert.Equal("1.2.0", imported.Version);
        Assert.Empty(imported.Connections);
        Assert.Equal("// b", imported.Files["synapses/echo.cs"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.Import(bundle));
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
        var ignored = await app.CompleteBuild(oldAttempt, new() { ["synapses/echo.cs"] = "// stale" }, null,
            new AppManifest("Hijacked", "", [], [], []));
        Assert.Equal("Echo", ignored.Manifest.Title);
    }

    [Fact]
    public async Task ABuildThatRewritesTheDeclarationFailsItBelongsToTheAuthor()
    {
        await using var brain = await Start();
        var app = brain.Get<IApp>(IApp.Key(Brain, "spec-guard"));
        await app.Create(Manifest(), "the authored spec");
        await app.BeginBuild(null);
        var failed = await app.CompleteBuild(await app.CurrentBuildAttempt(),
            new() { [AppFiles.DeclarationPath] = "// hijacked" }, null);
        Assert.Equal(AppStatus.Failed, failed.Status);
        Assert.Equal("the authored spec", failed.Spec);
        Assert.False(failed.Files.ContainsKey(AppFiles.DeclarationPath));
        Assert.Contains(failed.Activity, line => line.Contains("app.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HistoryTimestampsComeFromTheHostClock()
    {
        var ct = TestContext.Current.CancellationToken;
        var moment = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>()
            .ConfigureSilo(silo =>
            {
                silo.Services.RemoveAll<TimeProvider>();
                silo.Services.AddSingleton<TimeProvider>(new FixedClock(moment));
            })
            .StartAsync(ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "clocked"));
        var created = await app.Create(Manifest(), "spec");
        Assert.Equal(moment, created.Versions.Single().SavedAt);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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

    [Fact]
    public async Task AProgramIsAnyCsFileButTheDeclarationIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new FakeAppRuntime();
        await using var brain = await Start(runtime);
        var app = brain.Get<IApp>(IApp.Key(Brain, "keyed"));
        await app.Create(Manifest(), "## Scenario: Keys\nIt keys.");
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => app.WriteBehavior("app.cs", "// p", Guid.NewGuid()));
        Assert.Contains("SaveManifest", refused.Message, StringComparison.Ordinal);
        var snapshot = await app.WriteBehavior("new.cs", "// p", Guid.NewGuid());
        Assert.Contains("new.cs", snapshot.Files.Keys);
        var deployment = Assert.Single(runtime.Deployed);
        Assert.Equal(["new.cs"], deployment.Sources.Keys);
        Assert.Equal("// p", deployment.Sources["new.cs"]);
    }
}
