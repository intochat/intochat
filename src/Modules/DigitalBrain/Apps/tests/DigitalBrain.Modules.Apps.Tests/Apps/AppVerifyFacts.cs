using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Modules.Apps.Tests;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Apps.Tests.Apps;

public sealed class AppVerifyFacts
{
    // A live scenario talks to real services, so no test can report it; it never gates.
    [Fact]
    public void ALiveScenarioIsOutsideTheCheckableSet()
        => Assert.Equal(["Echoes"], AppVerifyJob.Scenarios("## Scenario: Echoes\nIt echoes.\n## Scenario: Echoes the internet (live)\nIt really does."));

    private const string Brain = "app-verify-facts";

    private static AppManifest Manifest() =>
        new("Echo", "echoes", [new("ask", "ask it")], [], []);

    private const string Spec = "## Scenario: Echoes\nIt echoes.\n\n## Scenario: Shouts\nIt shouts.";

    private static async Task<(ModuleBrain Brain, IApp App)> Start(ScriptedTestRunner runner, CancellationToken ct)
    {
        var brain = await ModuleTest.Create().WithModule<AppsModule>()
            .ConfigureSilo(silo => silo.Services.AddSingleton<ITestScriptRunner>(runner))
            .StartAsync(ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "echo"));
        await app.Create(Manifest(), Spec);
        await app.BeginBuild(null);
        await app.CompleteBuild(await app.CurrentBuildAttempt(),
            new() { ["tests.cs"] = "// scripted-tests", ["behaviors/echo.cs"] = "// b" }, null);
        return (brain, app);
    }

    [Fact]
    public async Task VerifyRunsTestsInTheBackgroundAndPublishesAppVerified()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = new ScriptedTestRunner();
        runner.BySourceMarker["scripted-tests"] = (0, "dbtest:pass Echoes\ndbtest:pass Shouts\n");
        var (brain, app) = await Start(runner, ct);
        await using var _ = brain;
        await using var verified = await brain.SubscribeAsync<AppVerified>(app, ct);
        await app.BeginVerify();
        AppVerifyJob.Enqueue(brain.SiloServices, IApp.Key(Brain, "echo"));
        await using var stream = verified.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.True(stream.Current.Green);
        var snapshot = await app.Read();
        Assert.True(snapshot.Versions.Single(version => version.Version == snapshot.Version).Green);
    }

    [Fact]
    public async Task ScratchVerificationCopiesNeverAppearInTheAppIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = new ScriptedTestRunner();
        runner.BySourceMarker["scripted-tests"] = (0, "dbtest:pass Echoes\ndbtest:pass Shouts\n");
        var (brain, _) = await Start(runner, ct);
        await using var __ = brain;
        // Even a scratch copy that outlives its run (a swallowed delete) stays out of the index.
        var scratch = brain.Get<IApp>(IApp.Key(Brain, "echo--verify-leak"));
        await scratch.Import(new("echo--verify-leak", "1.0.0",
            new("Echo", "echoes", [], [], []), "## Scenario: Echoes\nIt echoes.", new() { ["tests.cs"] = "// t" }));
        Assert.Equal(["echo"], await brain.Get<IAppIndex>(Brain).List());
    }

    [Fact]
    public async Task AVerifyRunWithAFailingScenarioMarksTheVersionRed()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = new ScriptedTestRunner();
        runner.BySourceMarker["scripted-tests"] = (1, "dbtest:pass Echoes\ndbtest:fail Shouts\ttoo quiet\n");
        var (brain, app) = await Start(runner, ct);
        await using var _ = brain;
        await using var verified = await brain.SubscribeAsync<AppVerified>(app, ct);
        AppVerifyJob.Enqueue(brain.SiloServices, IApp.Key(Brain, "echo"));
        await using var stream = verified.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.False(stream.Current.Green);
        var snapshot = await app.Read();
        Assert.Contains(snapshot.Verdicts, verdict => verdict.Name == "Shouts" && !verdict.Passed && verdict.Message == "too quiet");
    }

    [Fact]
    public async Task AVerifyRunReportingNoScenariosIsNeverGreen()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = new ScriptedTestRunner();
        runner.BySourceMarker["scripted-tests"] = (0, "no dbtest lines at all\n");
        var (brain, app) = await Start(runner, ct);
        await using var _ = brain;
        await using var verified = await brain.SubscribeAsync<AppVerified>(app, ct);
        AppVerifyJob.Enqueue(brain.SiloServices, IApp.Key(Brain, "echo"));
        await using var stream = verified.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.False(stream.Current.Green);
    }

    [Fact]
    public async Task AScenarioTheSpecDoesNotDeclareFailsTheRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = new ScriptedTestRunner();
        runner.BySourceMarker["scripted-tests"] = (0, "dbtest:pass Echoes\ndbtest:pass Shouts\ndbtest:pass Imaginary\n");
        var (brain, app) = await Start(runner, ct);
        await using var _ = brain;
        await using var verified = await brain.SubscribeAsync<AppVerified>(app, ct);
        AppVerifyJob.Enqueue(brain.SiloServices, IApp.Key(Brain, "echo"));
        await using var stream = verified.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.False(stream.Current.Green);
    }

    [Fact]
    public async Task ASpecScenarioWithoutAVerdictFailsTheRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = new ScriptedTestRunner();
        runner.BySourceMarker["scripted-tests"] = (0, "dbtest:pass Echoes\n");
        var (brain, app) = await Start(runner, ct);
        await using var _ = brain;
        await using var verified = await brain.SubscribeAsync<AppVerified>(app, ct);
        AppVerifyJob.Enqueue(brain.SiloServices, IApp.Key(Brain, "echo"));
        await using var stream = verified.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.False(stream.Current.Green);
        var snapshot = await app.Read();
        Assert.Contains(snapshot.Verdicts, verdict => verdict.Name == "Shouts" && !verdict.Passed);
    }
}
