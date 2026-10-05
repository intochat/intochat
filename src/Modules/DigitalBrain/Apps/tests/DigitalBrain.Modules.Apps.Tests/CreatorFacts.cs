using DigitalBrain.AI;
using DigitalBrain.AI.Scripted;
using DigitalBrain.Apps;
using DigitalBrain.Enforcement;
using DigitalBrain.Framework.Enforcement;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DigitalBrain.Modules.Apps.Tests;

// The Creator loop with scripted Author and Builder models, operating on the live app: the
// request shapes the app in place, implementations hot-land as files, scenario runs commit
// and record verdicts — and red is a state the app lives in, never a refusal.
public sealed class CreatorFacts
{
    private const string Spec = """
        # Shouter

        Shouts back whatever you say.

        ## Scenario: It shouts

        Asking "hello" answers "HELLO!".
        """;

    [Fact]
    public async Task ARequestBecomesALiveAppWhoseScenarioRunsTurnGreen()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = new ScriptedTestRunner();
        await using var brain = await StartAsync(ct, runner);
        await brain.Get<IScriptedLLM>("author").Script([Authored(Spec)]);
        await brain.Get<IScriptedLLM>("builder").Script([
            Built("// proof v1", "Whisper the answer."),
            Built("// proof v2", "Shout the answer in capitals."),
        ]);
        runner.BySourceMarker["// proof v1"] = (1, "dbtest:fail It shouts\tThe answer was \"hello\".");
        runner.BySourceMarker["// proof v2"] = (0, "dbtest:pass It shouts");
        StampAlice();
        var creator = brain.SiloServices.GetRequiredService<CreatorService>();
        var packaging = brain.SiloServices.GetRequiredService<LiveAppPackaging>();
        var package = PackageId.Parse("alice/shouter");
        var app = brain.Get<IApp>(Scope() + "/packages/" + package);

        var authored = await creator.Author(app, "An app that shouts back whatever I say.", ct);
        Assert.Equal("Shouter", authored.Manifest.Title);
        Assert.Equal("prompt", authored.Manifest.RuntimeName);
        Assert.Contains("## Scenario: It shouts", authored.Files[PackageContent.SpecPath], StringComparison.Ordinal);
        Assert.Equal(AppStatus.Installed, (await app.Read()).Status);

        await creator.Implement(app, null, ct);
        var red = await packaging.RunScenarios(app, package, null, ct);
        Assert.False(red.Green);
        Assert.False(Assert.Single((await app.ReadContent()).Verdicts).Passed);

        // A red app saves: the commit is history, not a gate.
        var saved = await packaging.Save(app, package, "Red checkpoint");
        Assert.NotNull(saved.Revision);

        await creator.Implement(app, "Shout in capitals.", ct);
        var green = await packaging.RunScenarios(app, package, null, ct);
        Assert.True(green.Green);
        Assert.True(Assert.Single((await app.ReadContent()).Verdicts).Passed);
    }

    [Fact]
    public async Task PublishStaysTheGateWhateverTheAppsOwnVerdictsClaim()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = new ScriptedTestRunner();
        await using var brain = await StartAsync(ct, runner);
        await brain.Get<IScriptedLLM>("author").Script([Authored(Spec)]);
        await brain.Get<IScriptedLLM>("builder").Script([Built("// still red", "Whisper.")]);
        runner.BySourceMarker["// still red"] = (1, "dbtest:fail It shouts\tStill whispering.");
        StampAlice();
        var creator = brain.SiloServices.GetRequiredService<CreatorService>();
        var packaging = brain.SiloServices.GetRequiredService<LiveAppPackaging>();
        var package = PackageId.Parse("alice/shouter");
        var app = brain.Get<IApp>(Scope() + "/packages/" + package);
        await creator.Author(app, "Shout back.", ct);
        await creator.Implement(app, null, ct);
        await packaging.RunScenarios(app, package, null, ct);

        // Tampered or stale app-side verdicts change nothing: the gate reads the verification.
        await app.RecordVerdicts([new("It shouts", true, "", null)], Guid.NewGuid());
        var head = (await brain.Get<IPackage>(package.ToString()).Read()).Head!;

        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => brain.Get<IPackage>(package.ToString()).Publish(new(Guid.NewGuid(), head)));
    }

    [Fact]
    public async Task AForkMaterializesAsALiveAppEvenWhenItsHistoryIsRed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        StampAlice();
        var source = PackageId.Parse("alice/tracker");
        var tracker = PackageSamples.Tracker();
        await brain.Get<IPackage>(source.ToString()).Commit(new(Guid.NewGuid(), null, tracker, "red history"));
        CallerContextStamper.Stamp(new CallerContext
        {
            PrincipalId = "bob",
            AccountId = "account-bob",
            BrainId = "workspace-bob",
            Kind = CallerKind.User,
            StampedBy = TrustedEdge.AuthenticatedHttp,
        });
        var service = brain.SiloServices.GetRequiredService<AppService>();

        await service.Fork(source, new(null, null, null));

        var app = brain.Get<IApp>(BrainScope.Create("account-bob", "workspace-bob").Id + "/packages/bob/tracker");
        Assert.Equal(AppStatus.Installed, (await app.Read()).Status);
        var content = await app.ReadContent();
        Assert.Equal("// watches the feed", content.Files["behaviors/track.cs"]);
        Assert.Equal("Tracker", content.Manifest.Title);
    }

    [Fact]
    public async Task AnImplementationWithoutTestsIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        await brain.Get<IScriptedLLM>("author").Script([Authored(Spec)]);
        await brain.Get<IScriptedLLM>("builder").Script(["""{"settings":[],"files":{"prompts/system.md":"Shout."}}"""]);
        StampAlice();
        var creator = brain.SiloServices.GetRequiredService<CreatorService>();
        var app = brain.Get<IApp>(Scope() + "/packages/alice/shouter");
        await creator.Author(app, "Shout back.", ct);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => creator.Implement(app, null, ct));

        Assert.Contains("must include tests.cs", error.Message, StringComparison.Ordinal);
        Assert.Equal(AppStatus.Installed, (await app.Read()).Status);
    }

    [Fact]
    public async Task ImplementRefusesUpfrontWhenTheHostCannotRunTests()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct, canRun: false);
        await brain.Get<IScriptedLLM>("author").Script([Authored(Spec)]);
        StampAlice();
        var creator = brain.SiloServices.GetRequiredService<CreatorService>();
        var app = brain.Get<IApp>(Scope() + "/packages/alice/shouter");
        await creator.Author(app, "Shout back.", ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => creator.Implement(app, null, ct));
    }

    [Fact]
    public async Task ABuilderThatCallsAToolGetsItsResultAndStillImplements()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = new ScriptedTestRunner();
        await using var brain = await StartAsync(ct, runner);
        await brain.Get<IScriptedLLM>("author").Script([Authored(Spec)]);
        await brain.Get<IScriptedLLM>("builder").Script([
            """{"tool":"check_csharp","arguments":{"files":{"tests.cs":"var answer = 1;\nConsole.WriteLine(answer);"}}}""",
            Built("// proof v1", "Shout the answer in capitals."),
        ]);
        runner.BySourceMarker["// proof v1"] = (0, "dbtest:pass It shouts");
        StampAlice();
        var creator = brain.SiloServices.GetRequiredService<CreatorService>();
        var app = brain.Get<IApp>(Scope() + "/packages/alice/shouter");
        await creator.Author(app, "Shout back.", ct);

        var content = await creator.Implement(app, null, ct);

        Assert.Equal("// proof v1", content.Files[PackageContent.TestsPath]);
    }

    private static string Scope() => BrainScope.Create("account-alice", "workspace-alice").Id;

    private static string Authored(string spec) => System.Text.Json.JsonSerializer.Serialize(new
    {
        name = "shouter",
        title = "Shouter",
        description = "Shouts back.",
        runtime = "prompt",
        spec,
    });

    private static string Built(string testsMarker, string systemPrompt) => System.Text.Json.JsonSerializer.Serialize(new
    {
        settings = new[] { new { name = "Model", description = "Who answers.", @default = "IGemma4" } },
        files = new Dictionary<string, string> { ["tests.cs"] = testsMarker, ["prompts/system.md"] = systemPrompt },
    });

    private static void StampAlice() => CallerContextStamper.Stamp(new CallerContext
    {
        PrincipalId = "alice",
        AccountId = "account-alice",
        BrainId = "workspace-alice",
        Kind = CallerKind.User,
        StampedBy = TrustedEdge.AuthenticatedHttp,
    });

    private static Task<ModuleBrain> StartAsync(CancellationToken ct, DigitalBrain.Apps.ITestScriptRunner? runner = null, bool canRun = true) => ModuleTest.Create()
        .WithModule<AIModule>()
        .WithModule<AppsModule>()
        .ConfigureModule<AppsModule, AppsOptions>(apps =>
        {
            apps.AuthorModel = IScriptedLLM.ModelPrefix + "author";
            apps.BuilderModel = IScriptedLLM.ModelPrefix + "builder";
        })
        .ConfigureSilo(silo =>
        {
            silo.Services.AddSingleton<DigitalBrain.Apps.ITestScriptRunner>(_ => runner ?? new ScriptedTestRunner());
            silo.Services.AddSingleton<IScriptSandbox>(new CreatorSandbox(canRun));
        })
        .StartAsync(ct);

    private sealed class CreatorSandbox(bool canRun) : IScriptSandbox
    {
        public bool CanRun => canRun;
        public string AuthoringDescription => "csharp uses sandbox contracts.";
        public Task<ScriptContractCatalog> ReadContracts(IReadOnlyList<string> modules, CancellationToken cancellationToken)
            => Task.FromResult(new ScriptContractCatalog([], [], ""));
        public ScriptCompilationCheck Check(IReadOnlyDictionary<string, string> files) => new(true, []);
    }
}
