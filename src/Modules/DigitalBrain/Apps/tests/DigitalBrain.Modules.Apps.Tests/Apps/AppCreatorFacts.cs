using DigitalBrain.AI;
using DigitalBrain.AI.Scripted;
using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Modules.Apps.Tests;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Apps.Tests.Apps;

// The Creator on the app neuron: Author is one synchronous call, the Builder runs in the
// background and lands on the live app — red is a state to revisit, never a refusal.
public sealed class AppCreatorFacts
{
    private const string Brain = "app-creator-facts";

    private const string Spec = """
        # Shouter

        Shouts back whatever you say.

        ## Scenario: It shouts

        Asking "hello" answers "HELLO!".
        """;

    [Fact]
    public async Task AuthorCreatesADraftedAppFromARequest()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        await brain.Get<IScriptedLLM>("author").Script([Authored(Spec)]);
        var creator = brain.SiloServices.GetRequiredService<Creator>();
        var digitalBrain = brain.SiloServices.GetRequiredService<IDigitalBrain>();

        var created = await creator.Author(digitalBrain, Brain, null, "An app that shouts back.", ct);

        Assert.Equal("shouter", created.Name);
        Assert.Equal("Shouter", created.Manifest.Title);
        Assert.Equal(AppStatus.Drafted, created.Status);
        Assert.Contains("## Scenario: It shouts", created.Spec, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthorRevisesAnExistingAppsSpec()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        await brain.Get<IScriptedLLM>("author").Script([Authored(Spec), Authored(Spec + "\n\n## Scenario: It whispers\nQuietly.")]);
        var creator = brain.SiloServices.GetRequiredService<Creator>();
        var digitalBrain = brain.SiloServices.GetRequiredService<IDigitalBrain>();
        await creator.Author(digitalBrain, Brain, null, "Shout back.", ct);

        var revised = await creator.Author(digitalBrain, Brain, "shouter", "Also whisper.", ct);

        Assert.Contains("## Scenario: It whispers", revised.Spec, StringComparison.Ordinal);
        Assert.Equal("0.2.0", revised.Version);
    }

    [Fact]
    public async Task ImplementReturnsBuildingImmediatelyAndGoesLiveWhenTheBuilderFinishes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        await brain.Get<IScriptedLLM>("author").Script([Authored(Spec)]);
        await brain.Get<IScriptedLLM>("builder").Script([Built("// proof v1", "Shout in capitals.")]);
        var creator = brain.SiloServices.GetRequiredService<Creator>();
        var digitalBrain = brain.SiloServices.GetRequiredService<IDigitalBrain>();
        await creator.Author(digitalBrain, Brain, null, "Shout back.", ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "shouter"));
        await using var changes = await brain.SubscribeAsync<AppChanged>(app, ct);

        var building = await creator.Implement(digitalBrain, IApp.Key(Brain, "shouter"), null);
        Assert.Equal(AppStatus.Building, building.Status);

        await foreach (var change in changes.ReadAllAsync(ct))
        {
            if (change.Status == AppStatus.Live) { break; }
            Assert.NotEqual(AppStatus.Failed, change.Status);
        }
        var live = await app.Read();
        Assert.Equal("// proof v1", live.Files["tests.cs"]);
        Assert.Equal("Shout in capitals.", live.Files["prompts/system.md"]);
        Assert.Equal("0.2.0", live.Version);
    }

    [Fact]
    public async Task ABuilderReplyWithoutTestsFailsTheBuildWithTheReasonInActivity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        await brain.Get<IScriptedLLM>("author").Script([Authored(Spec)]);
        await brain.Get<IScriptedLLM>("builder").Script(["""{"files":{"prompts/system.md":"Shout."}}"""]);
        var creator = brain.SiloServices.GetRequiredService<Creator>();
        var digitalBrain = brain.SiloServices.GetRequiredService<IDigitalBrain>();
        await creator.Author(digitalBrain, Brain, null, "Shout back.", ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "shouter"));
        await using var changes = await brain.SubscribeAsync<AppChanged>(app, ct);

        await creator.Implement(digitalBrain, IApp.Key(Brain, "shouter"), null);

        await foreach (var change in changes.ReadAllAsync(ct)) { if (change.Status == AppStatus.Failed) { break; } }
        var failed = await app.Read();
        Assert.Contains(failed.Activity, line => line.Contains("tests.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABuilderCrashLandsAsFailedNotAsAnUnhandledException()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        await brain.Get<IScriptedLLM>("author").Script([Authored(Spec)]);
        // No builder script at all: the scripted model throws when asked.
        var creator = brain.SiloServices.GetRequiredService<Creator>();
        var digitalBrain = brain.SiloServices.GetRequiredService<IDigitalBrain>();
        await creator.Author(digitalBrain, Brain, null, "Shout back.", ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "shouter"));
        await using var changes = await brain.SubscribeAsync<AppChanged>(app, ct);

        await creator.Implement(digitalBrain, IApp.Key(Brain, "shouter"), null);

        await foreach (var change in changes.ReadAllAsync(ct)) { if (change.Status == AppStatus.Failed) { break; } }
        var failed = await app.Read();
        Assert.Equal(AppStatus.Failed, failed.Status);
    }

    [Fact]
    public async Task ABuilderReplyWithEmptyOperationsKeepsTheAuthoredOnes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        await brain.Get<IScriptedLLM>("author").Script([Authored(Spec)]);
        await brain.Get<IScriptedLLM>("builder").Script([System.Text.Json.JsonSerializer.Serialize(new
        {
            operations = Array.Empty<object>(),
            settings = new[] { new { name = "Model", description = "Who answers.", @default = "IGemma4" } },
            files = new Dictionary<string, string> { ["tests.cs"] = "// t" },
        })]);
        var creator = brain.SiloServices.GetRequiredService<Creator>();
        var digitalBrain = brain.SiloServices.GetRequiredService<IDigitalBrain>();
        await creator.Author(digitalBrain, Brain, null, "Shout back.", ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "shouter"));
        await using var changes = await brain.SubscribeAsync<AppChanged>(app, ct);

        await creator.Implement(digitalBrain, IApp.Key(Brain, "shouter"), null);

        await foreach (var change in changes.ReadAllAsync(ct)) { if (change.Status == AppStatus.Live) { break; } }
        var live = await app.Read();
        Assert.Contains(live.Manifest.Operations, operation => operation.Name == "ask");
        Assert.Equal("IGemma4", live.Settings["Model"]);
    }

    [Fact]
    public async Task ABuilderThatCallsAToolGetsItsResultAndStillImplements()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        await brain.Get<IScriptedLLM>("author").Script([Authored(Spec)]);
        await brain.Get<IScriptedLLM>("builder").Script([
            """{"tool":"check_csharp","arguments":{"files":{"tests.cs":"var answer = 1;\nConsole.WriteLine(answer);"}}}""",
            Built("// proof v1", "Shout in capitals."),
        ]);
        var creator = brain.SiloServices.GetRequiredService<Creator>();
        var digitalBrain = brain.SiloServices.GetRequiredService<IDigitalBrain>();
        await creator.Author(digitalBrain, Brain, null, "Shout back.", ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "shouter"));
        await using var changes = await brain.SubscribeAsync<AppChanged>(app, ct);

        await creator.Implement(digitalBrain, IApp.Key(Brain, "shouter"), null);

        await foreach (var change in changes.ReadAllAsync(ct)) { if (change.Status == AppStatus.Live) { break; } }
        Assert.Equal("// proof v1", (await app.Read()).Files["tests.cs"]);
    }

    private static string Authored(string spec) => System.Text.Json.JsonSerializer.Serialize(new
    {
        name = "shouter",
        title = "Shouter",
        description = "Shouts back.",
        operations = new[] { new { name = "ask", description = "ask it" } },
        settings = new[] { new { name = "Volume", description = "How loud.", @default = "11" } },
        spec,
    });

    private static string Built(string testsMarker, string systemPrompt) => System.Text.Json.JsonSerializer.Serialize(new
    {
        files = new Dictionary<string, string> { ["tests.cs"] = testsMarker, ["prompts/system.md"] = systemPrompt },
    });

    private static Task<ModuleBrain> StartAsync(CancellationToken ct) => ModuleTest.Create()
        .WithModule<AIModule>()
        .WithModule<AppsModule>()
        .ConfigureModule<AppsModule, AppsOptions>(apps =>
        {
            apps.AuthorModel = IScriptedLLM.ModelPrefix + "author";
            apps.BuilderModel = IScriptedLLM.ModelPrefix + "builder";
        })
        .ConfigureSilo(silo =>
        {
            silo.Services.AddSingleton<ITestScriptRunner>(_ => new ScriptedTestRunner());
            silo.Services.AddSingleton<IScriptSandbox>(new CreatorSandbox());
        })
        .StartAsync(ct);

    private sealed class CreatorSandbox : IScriptSandbox
    {
        public bool CanRun => true;
        public string AuthoringDescription => "csharp uses sandbox contracts.";
        public Task<ScriptContractCatalog> ReadContracts(IReadOnlyList<string> modules, CancellationToken cancellationToken)
            => Task.FromResult(new ScriptContractCatalog([], [], ""));
        public ScriptCompilationCheck Check(IReadOnlyDictionary<string, string> files) => new(true, []);
    }
}
