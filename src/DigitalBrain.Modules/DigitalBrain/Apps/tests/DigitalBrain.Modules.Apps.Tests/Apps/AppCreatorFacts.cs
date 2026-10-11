using DigitalBrain.AI;
using DigitalBrain.AI.Models.OpenAI;
using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Testing.Model;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Apps.Tests.Apps;

// The Creator on the app neuron: Author is one synchronous call, the Builder runs in the
// background and lands on the live app — red is a state to revisit, never a refusal.
public sealed class AppCreatorFacts
{
    private const string Brain = "app-creator-facts";

    private const string Author = "gpt-5.6-luna";
    private const string Builder = "gpt-5.6-sol";

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
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartAsync(model, ct);
        model.Script(Author, Authored(Spec));
        var creator = brain.Services.GetRequiredService<Creator>();
        var digitalBrain = brain.Services.GetRequiredService<IDigitalBrain>();

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
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartAsync(model, ct);
        model.Script(Author, Authored(Spec), Authored(Spec + "\n\n## Scenario: It whispers\nQuietly."));
        var creator = brain.Services.GetRequiredService<Creator>();
        var digitalBrain = brain.Services.GetRequiredService<IDigitalBrain>();
        await creator.Author(digitalBrain, Brain, null, "Shout back.", ct);

        var revised = await creator.Author(digitalBrain, Brain, "shouter", "Also whisper.", ct);

        Assert.Contains("## Scenario: It whispers", revised.Spec, StringComparison.Ordinal);
        Assert.Equal("0.2.0", revised.Version);
    }

    [Fact]
    public async Task ImplementReturnsBuildingImmediatelyAndGoesLiveWhenTheBuilderFinishes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartAsync(model, ct);
        model.Script(Author, Authored(Spec));
        model.Script(Builder, Built("// proof v1", "Shout in capitals."));
        var creator = brain.Services.GetRequiredService<Creator>();
        var digitalBrain = brain.Services.GetRequiredService<IDigitalBrain>();
        await creator.Author(digitalBrain, Brain, null, "Shout back.", ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "shouter"));
        await using var changes = await brain.Observe<AppChanged>(app, ct);

        var building = await creator.Implement(digitalBrain, IApp.Key(Brain, "shouter"), null);
        Assert.Equal(AppStatus.Building, building.Status);

        Assert.Equal(AppStatus.Live, (await changes.NextAsync(change => change.Status is AppStatus.Live or AppStatus.Failed, ct)).Status);
        var live = await app.Read();
        Assert.Equal("// proof v1", live.Files["synapses/shout.cs"]);
        Assert.Equal("Shout in capitals.", live.Files["prompts/system.md"]);
        Assert.Equal("0.2.0", live.Version);
    }

    [Fact]
    public async Task ABuilderCrashLandsAsFailedNotAsAnUnhandledException()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartAsync(model, ct);
        model.Script(Author, Authored(Spec));
        // An empty builder script: the model answers 500.
        model.Script(Builder);
        var creator = brain.Services.GetRequiredService<Creator>();
        var digitalBrain = brain.Services.GetRequiredService<IDigitalBrain>();
        await creator.Author(digitalBrain, Brain, null, "Shout back.", ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "shouter"));
        await using var changes = await brain.Observe<AppChanged>(app, ct);

        await creator.Implement(digitalBrain, IApp.Key(Brain, "shouter"), null);

        await changes.NextAsync(change => change.Status == AppStatus.Failed, ct);
        var failed = await app.Read();
        Assert.Equal(AppStatus.Failed, failed.Status);
    }

    [Fact]
    public async Task ABuildTheAppRefusesReportsAttentionNotCompleted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartAsync(model, ct);
        model.Script(Author, Authored(Spec));
        model.Script(Builder, System.Text.Json.JsonSerializer.Serialize(new
        {
            files = new Dictionary<string, string> { ["synapses/shout.cs"] = "// t", [AppFiles.DeclarationPath] = "// not yours" },
        }));
        var creator = brain.Services.GetRequiredService<Creator>();
        var digitalBrain = brain.Services.GetRequiredService<IDigitalBrain>();
        await creator.Author(digitalBrain, Brain, null, "Shout back.", ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "shouter"));
        await using var activity = await brain.Observe<AppActivity>(app, ct);

        await creator.Implement(digitalBrain, IApp.Key(Brain, "shouter"), null);

        var outcome = await activity.NextAsync(signal => signal.State is "completed" or "attention", ct);
        Assert.Equal("attention", outcome.State);
        Assert.Contains(AppFiles.DeclarationPath, outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABuilderReplyWithEmptyOperationsKeepsTheAuthoredOnes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartAsync(model, ct);
        model.Script(Author, Authored(Spec));
        model.Script(Builder, System.Text.Json.JsonSerializer.Serialize(new
        {
            operations = Array.Empty<object>(),
            settings = new[] { new { name = "Model", description = "Who answers.", @default = "IGemma4" } },
            files = new Dictionary<string, string> { ["synapses/shout.cs"] = "// t" },
        }));
        var creator = brain.Services.GetRequiredService<Creator>();
        var digitalBrain = brain.Services.GetRequiredService<IDigitalBrain>();
        await creator.Author(digitalBrain, Brain, null, "Shout back.", ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "shouter"));
        await using var changes = await brain.Observe<AppChanged>(app, ct);

        await creator.Implement(digitalBrain, IApp.Key(Brain, "shouter"), null);

        await changes.NextAsync(change => change.Status == AppStatus.Live, ct);
        var live = await app.Read();
        Assert.Contains(live.Manifest.Operations, operation => operation.Name == "ask");
        Assert.Equal("IGemma4", live.Settings["Model"]);
    }

    [Fact]
    public async Task ABuilderReplyWithOperationsButNoSettingsKeepsTheAuthoredSettings()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartAsync(model, ct);
        model.Script(Author, Authored(Spec));
        model.Script(Builder, System.Text.Json.JsonSerializer.Serialize(new
        {
            operations = new[] { new { name = "shout", description = "Shout." } },
            files = new Dictionary<string, string> { ["synapses/shout.cs"] = "// t" },
        }));
        var creator = brain.Services.GetRequiredService<Creator>();
        var digitalBrain = brain.Services.GetRequiredService<IDigitalBrain>();
        await creator.Author(digitalBrain, Brain, null, "Shout back.", ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "shouter"));
        await using var changes = await brain.Observe<AppChanged>(app, ct);

        await creator.Implement(digitalBrain, IApp.Key(Brain, "shouter"), null);

        await changes.NextAsync(change => change.Status == AppStatus.Live, ct);
        var live = await app.Read();
        Assert.Contains(live.Manifest.Settings, setting => setting.Name == "Volume");
        Assert.Contains(live.Manifest.Operations, operation => operation.Name == "shout");
    }

    [Fact]
    public async Task ABuilderThatCallsAToolGetsItsResultAndStillImplements()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartAsync(model, ct);
        model.Script(Author, Authored(Spec));
        model.Script(Builder,
            FakeReply.Tool("check_csharp", """{"files":{"synapses/shout.cs":"var answer = 1;\nConsole.WriteLine(answer);"}}"""),
            Built("// proof v1", "Shout in capitals."));
        var creator = brain.Services.GetRequiredService<Creator>();
        var digitalBrain = brain.Services.GetRequiredService<IDigitalBrain>();
        await creator.Author(digitalBrain, Brain, null, "Shout back.", ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "shouter"));
        await using var changes = await brain.Observe<AppChanged>(app, ct);

        await creator.Implement(digitalBrain, IApp.Key(Brain, "shouter"), null);

        await changes.NextAsync(change => change.Status == AppStatus.Live, ct);
        Assert.Equal("// proof v1", (await app.Read()).Files["synapses/shout.cs"]);
    }

    [Fact]
    public async Task AStaleWorkersOutcomePublishesNoActivity()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new GatedSandbox();
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartAsync(model, ct, sandbox, buildFreshForSeconds: 0);
        model.Script(Author, Authored(Spec));
        model.Script(Builder,
            FakeReply.Tool("check_csharp", """{"files":{"synapses/shout.cs":"var answer = 1;"}}"""),
            Built("// stale", "Shout."));
        var creator = brain.Services.GetRequiredService<Creator>();
        var digitalBrain = brain.Services.GetRequiredService<IDigitalBrain>();
        await creator.Author(digitalBrain, Brain, null, "Shout back.", ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "shouter"));
        await using var activity = await brain.Observe<AppActivity>(app, ct);
        await creator.Implement(digitalBrain, IApp.Key(Brain, "shouter"), null);
        await sandbox.Entered.Task.WaitAsync(ct);

        await app.BeginBuild(null);
        sandbox.Release.SetResult();

        // An absence has no signal to wait for: the stale worker gets a bounded window to report.
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(TimeSpan.FromSeconds(3));
        try { await activity.NextAsync(signal => signal.State is "completed" or "attention", window.Token); Assert.Fail("A stale worker reported its outcome."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    [Fact]
    public void TheBuilderIsNotAskedForTests()
    {
        Assert.DoesNotContain("tests.cs", AgentPrompts.Builder, StringComparison.Ordinal);
        Assert.DoesNotContain("dbtest", AgentPrompts.Builder, StringComparison.Ordinal);
        Assert.DoesNotContain("## Scenario", AgentPrompts.Author, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBuilderIsToldToWriteFlatCsFilesAndNeverTheDeclaration()
    {
        Assert.Contains("\"notify.cs\"", AgentPrompts.Builder, StringComparison.Ordinal);
        Assert.Contains("app.cs", AgentPrompts.Builder, StringComparison.Ordinal);
        Assert.DoesNotContain("synapses/", AgentPrompts.Builder, StringComparison.Ordinal);
        Assert.DoesNotContain("behaviors/", AgentPrompts.Builder, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBuilderPromptTeachesOneReactionPerSynapseAndNeverTheInvocationPipeline()
    {
        Assert.Contains("brain.On(app, \"open\"", AgentPrompts.Builder, StringComparison.Ordinal);
        Assert.DoesNotContain("Invocations(", AgentPrompts.Builder, StringComparison.Ordinal);
        Assert.DoesNotContain("Respond(", AgentPrompts.Builder, StringComparison.Ordinal);
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

    private static string Built(string behaviorMarker, string systemPrompt) => System.Text.Json.JsonSerializer.Serialize(new
    {
        files = new Dictionary<string, string> { ["synapses/shout.cs"] = behaviorMarker, ["prompts/system.md"] = systemPrompt },
    });

    private static Task<ITestBrain> StartAsync(FakeModelServer model, CancellationToken ct, IScriptSandbox? sandbox = null, int buildFreshForSeconds = 900)
        => TestBrain.Create()
            .WithModule<AIModule>()
            .WithModule<AppsModule, AppsOptions>(apps =>
            {
                apps.AuthorModel = nameof(IGpt56Luna);
                apps.BuilderModel = nameof(IGpt56Sol);
                apps.BuildFreshForSeconds = buildFreshForSeconds;
            })
            .WithFakeModel(model)
            // The Builder's check_csharp tool compiles through the sandbox; the fake answers for the compiler.
            .ConfigureServices(services => services.AddSingleton(sandbox ?? new CreatorSandbox()))
            .StartAsync(ct);

    private sealed class GatedSandbox : IScriptSandbox
    {
        public Task<Verdict[]> Judge(string brain, IReadOnlyDictionary<string, string> bundle, Signal signal, CancellationToken cancellationToken)
            => Task.FromResult<Verdict[]>([]);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CanRun => true;
        public string AuthoringDescription => "csharp uses sandbox contracts.";
        public Task<ScriptContractCatalog> ReadContracts(IReadOnlyList<string> modules, CancellationToken cancellationToken)
            => Task.FromResult(new ScriptContractCatalog([], [], ""));
        public async Task<ScriptCompilationCheck> Compile(IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new ScriptCompilationCheck(true, []);
        }
    }

    private sealed class CreatorSandbox : IScriptSandbox
    {
        public Task<Verdict[]> Judge(string brain, IReadOnlyDictionary<string, string> bundle, Signal signal, CancellationToken cancellationToken)
            => Task.FromResult<Verdict[]>([]);
        public bool CanRun => true;
        public string AuthoringDescription => "csharp uses sandbox contracts.";
        public Task<ScriptContractCatalog> ReadContracts(IReadOnlyList<string> modules, CancellationToken cancellationToken)
            => Task.FromResult(new ScriptContractCatalog([], [], ""));
        public Task<ScriptCompilationCheck> Compile(IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken) => Task.FromResult(new ScriptCompilationCheck(true, []));
    }
}
