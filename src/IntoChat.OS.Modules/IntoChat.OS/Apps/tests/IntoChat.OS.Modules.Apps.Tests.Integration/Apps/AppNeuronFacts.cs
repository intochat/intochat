using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.Apps.Tests.Integration.Runtimes;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests.Apps;

public sealed class AppNeuronFacts
{
    private const string Brain = "app-neuron-facts";

    private static AppManifest Manifest(string title = "Echo") =>
        new(title, "echoes", [new("ask", "ask it")], [new("Text", "what to say", "hi")], []);

    private static Task<ITestBrain> Start(FakeAppRuntime? runtime = null, int buildFreshForSeconds = 900)
        => TestBrain.Create().WithModule<AppsModule, AppsOptions>(options => options.BuildFreshForSeconds = buildFreshForSeconds)
            .WithFakeAppRuntime(runtime ?? new())
            .StartAsync(TestContext.Current.CancellationToken);

    [Trait("host", "own")]
    [Fact]
    public async Task CreatingAnAppMintsVersionZeroOneZeroAndPublishesAppChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        var app = brain.Get<IApp>(IApp.Key(Brain, "echo"));
        await using var changes = await brain.Observe<AppChanged>(app, ct);
        var created = await app.Create(Manifest(), "## Scenario: Echoes\nIt echoes.");
        Assert.Equal("0.1.0", created.Version);
        Assert.Equal(AppStatus.Drafted, created.Status);
        Assert.Equal("echo", created.Name);
        var changed = await changes.NextAsync(ct: ct);
        Assert.Equal(("echo", "0.1.0", AppStatus.Drafted), (changed.Name, changed.Version, changed.Status));
    }

    // A writer whose app was deleted mid-deploy reconciles the runtime to an empty program, which retires it,
    // and an Unsettle that lands afterwards changes nothing: neither recreates the app.
    [Trait("host", "own")]
    [Fact]
    public async Task ADeletedAppAnswersItsWriterAnEmptyProgramAndIgnoresAnUnsettle()
    {
        await using var brain = await Start();
        var key = IApp.Key(Brain, "deleted");
        var app = brain.Get<IApp>(key);
        await app.Create(Manifest(), "spec");
        await app.WriteBehavior("synapses/echo.cs", "// echo", Guid.NewGuid());
        await app.Delete();
        var deploy = brain.Services.GetRequiredService<IGrainFactory>().GetGrain<IAppDeploy>(key);

        var program = await deploy.Program();
        var unsettled = await deploy.Unsettle(Guid.NewGuid(), "late", 1, 1, null);

        Assert.Empty(program.Sources);
        Assert.Empty(unsettled.Synapses ?? []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.SaveSpec("again", Guid.NewGuid()));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task CreatingAnAppTwiceIsRefused()
    {
        await using var brain = await Start();
        var app = brain.Get<IApp>(IApp.Key(Brain, "twice"));
        await app.Create(Manifest(), "spec");
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.Create(Manifest(), "spec"));
    }

    [Trait("host", "own")]
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

    [Trait("host", "own")]
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

    [Trait("host", "own")]
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

    [Trait("host", "own")]
    [Fact]
    public async Task AStaleBuildingAppReArmsAndTheOldWorkersCompleteBuildIsIgnored()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(buildFreshForSeconds: 0);
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

    [Trait("host", "own")]
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

    [Trait("host", "own")]
    [Fact]
    public async Task ABuildWhoseProgramDoesNotCompileGoesFailedAndKeepsItsFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new FakeAppRuntime { Refuse = sources => sources.ContainsKey("synapses/bad.cs") ? new InvalidOperationException("The program does not compile: synapses/bad.cs line 1 CS0029") : null };
        await using var brain = await Start(runtime);
        var app = brain.Get<IApp>(IApp.Key(Brain, "uncompilable"));
        await app.Create(Manifest(), "spec");
        await app.BeginBuild(null);

        const string Bad = """
            await using var brain = await IntoChatOSClient.ConnectAsync(args);
            await foreach (var request in brain.On<IntoChat.OS.Modules.Apps.Signals.AppRequested>(brain.Get<IntoChat.OS.Modules.Apps.IApp>(brain.Setting("App")!), brain.Stopping))
            { int x = "no"; }
            """;

        var failed = await app.CompleteBuild(await app.CurrentBuildAttempt(), new() { ["synapses/bad.cs"] = Bad }, null);

        Assert.Equal(AppStatus.Failed, failed.Status);
        Assert.Contains(failed.Activity, line => line.Contains("CS0029", StringComparison.Ordinal));
        Assert.Equal(Bad, failed.Files["synapses/bad.cs"]);
    }

    [Trait("host", "own")]
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

    [Trait("host", "own")]
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

    [Trait("host", "own")]
    [Fact]
    public async Task ProgressAnnouncesAChangeOnlyWhenTheActivityLineChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        var app = brain.Get<IApp>(IApp.Key(Brain, "quiet"));
        await app.Create(Manifest(), "spec");
        await app.BeginBuild(null);
        var building = (await app.Read()).Version;
        await using var changes = await brain.Observe<AppChanged>(app, ct);
        await app.Progress("working", "round 1");
        await app.Progress("working", "round 1");
        await app.Progress("reading", "round 2");
        // A file write is the next change after the progress ones: everything before it is what progress announced.
        await app.WriteFile("notes.md", "sentinel", Guid.NewGuid());

        var announced = 0;
        while ((await changes.NextAsync(ct: ct)).Version == building) { announced++; }
        Assert.Equal(2, announced);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ProgressPublishesAppActivityEvenWhenTheAppIsNotBuilding()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        var app = brain.Get<IApp>(IApp.Key(Brain, "activity"));
        await app.Create(Manifest(), "spec");
        await using var activity = await brain.Observe<AppActivity>(app, ct);
        await app.Progress("listening", "Heard you");
        var heard = await activity.NextAsync(ct: ct);
        Assert.Equal(("activity", "listening", "Heard you"), (heard.App, heard.State, heard.Detail));
        Assert.DoesNotContain("Heard you", (await app.Read()).Activity);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ProgressRefusesAStateOutsideTheTen()
    {
        await using var brain = await Start();
        var app = brain.Get<IApp>(IApp.Key(Brain, "badstate"));
        await app.Create(Manifest(), "spec");
        await Assert.ThrowsAsync<ArgumentException>(() => app.Progress("dancing", "x"));
    }

    [Trait("host", "own")]
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

    [Trait("host", "own")]
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

    [Trait("host", "own")]
    [Fact]
    public async Task AStaleWorkersManifestIsIgnoredWithItsFiles()
    {
        await using var brain = await Start(buildFreshForSeconds: 0);
        var app = brain.Get<IApp>(IApp.Key(Brain, "stale-manifest"));
        await app.Create(Manifest(), "spec");
        await app.BeginBuild(null);
        var oldAttempt = await app.CurrentBuildAttempt();
        await app.BeginBuild(null);
        var ignored = await app.CompleteBuild(oldAttempt, new() { ["synapses/echo.cs"] = "// stale" }, null,
            new AppManifest("Hijacked", "", [], [], []));
        Assert.Equal("Echo", ignored.Manifest.Title);
    }

    [Trait("host", "own")]
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

    [Trait("host", "own")]
    [Fact]
    public async Task HistoryTimestampsComeFromTheHostClock()
    {
        var ct = TestContext.Current.CancellationToken;
        var moment = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        await using var brain = await TestBrain.Create().WithModule<AppsModule>().WithFakeAppRuntime(new())
            // The version history stamps the host clock; a fixed one makes the stamp exact.
            .ConfigureServices(services => services.AddSingleton<TimeProvider>(new FixedClock(moment)))
            .StartAsync(ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "clocked"));
        var created = await app.Create(Manifest(), "spec");
        Assert.Equal(moment, created.Versions.Single().SavedAt);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Trait("host", "own")]
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

    [Trait("host", "own")]
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
