using IntoChat.OS.AI;
using IntoChat.OS.AI.Models;
using IntoChat.OS.AI.Models.OpenAI;
using IntoChat.OS.AI.Models.Signals;
using IntoChat.OS.Apps;
using IntoChat.OS.Apps.Signals;
using IntoChat.OS.Testing;
using IntoChat.OS.Testing.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Apps.Tests.Apps;

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

    [Trait("host", "own")]
    [Fact]
    public async Task AuthorAndBuilderPreserveTheDeclaredTitle()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartAsync(model, ct);
        model.Script(Author, Authored(Spec).Insert(1, "\"entry\":{\"kind\":\"text\",\"name\":\"answer\"},"));
        model.Script(Builder, Built("// built", "prompt").Insert(1, "\"operations\":[{\"name\":\"ask\",\"description\":\"Answer\"}],"));
        var creator = brain.Services.GetRequiredService<Creator>();
        var intoChatOS = brain.Services.GetRequiredService<IIntoChatOS>();
        var authored = await creator.Author(intoChatOS, Brain, null, "Shout back.", ct);
        Assert.Equal("Shouter", authored.Manifest.Title);
        var app = brain.Get<IApp>(IApp.Key(Brain, "shouter"));
        await using var changes = await brain.Observe<AppChanged>(app, ct);
        await creator.Implement(intoChatOS, IApp.Key(Brain, "shouter"), null);
        await changes.NextAsync(change => change.Status is AppStatus.Live or AppStatus.Failed, ct);
        Assert.Equal(authored.Manifest.Title, (await app.Read()).Manifest.Title);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AuthorCreatesADraftedAppFromARequest()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartAsync(model, ct);
        model.Script(Author, Authored(Spec));
        var creator = brain.Services.GetRequiredService<Creator>();
        var intoChatOS = brain.Services.GetRequiredService<IIntoChatOS>();

        var created = await creator.Author(intoChatOS, Brain, null, "An app that shouts back.", ct);

        Assert.Equal("shouter", created.Name);
        Assert.Equal("Shouter", created.Manifest.Title);
        Assert.Equal(AppStatus.Drafted, created.Status);
        Assert.Contains("## Scenario: It shouts", created.Spec, StringComparison.Ordinal);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task TheAuthorsModelCallLandsInTheBrainOfTheApp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartAsync(model, ct);
        model.Script(Author, Authored(Spec));
        var creator = brain.Services.GetRequiredService<Creator>();
        await using var started = await brain.Observe<InferenceStarted>(brain.Get<IGpt56Luna>(ModelAddress.Key(Brain)), ct);

        await creator.Author(brain.Services.GetRequiredService<IIntoChatOS>(), Brain, null, "An app that shouts back.", ct);

        Assert.Equal($"{Brain}/models", (await started.NextAsync(ct: ct)).NeuronId);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AuthorRevisesAnExistingAppsSpec()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartAsync(model, ct);
        model.Script(Author, Authored(Spec), Authored(Spec + "\n\n## Scenario: It whispers\nQuietly."));
        var creator = brain.Services.GetRequiredService<Creator>();
        var intoChatOS = brain.Services.GetRequiredService<IIntoChatOS>();
        await creator.Author(intoChatOS, Brain, null, "Shout back.", ct);

        var revised = await creator.Author(intoChatOS, Brain, "shouter", "Also whisper.", ct);

        Assert.Contains("## Scenario: It whispers", revised.Spec, StringComparison.Ordinal);
        Assert.Equal("0.2.0", revised.Version);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ImplementReturnsBuildingImmediatelyAndGoesLiveWhenTheBuilderFinishes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartAsync(model, ct);
        model.Script(Author, Authored(Spec));
        model.Script(Builder, Built("// proof v1", "Shout in capitals."));
        var creator = brain.Services.GetRequiredService<Creator>();
        var intoChatOS = brain.Services.GetRequiredService<IIntoChatOS>();
        await creator.Author(intoChatOS, Brain, null, "Shout back.", ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "shouter"));
        await using var changes = await brain.Observe<AppChanged>(app, ct);

        var building = await creator.Implement(intoChatOS, IApp.Key(Brain, "shouter"), null);
        Assert.Equal(AppStatus.Building, building.Status);

        Assert.Equal(AppStatus.Live, (await changes.NextAsync(change => change.Status is AppStatus.Live or AppStatus.Failed, ct)).Status);
        var live = await app.Read();
        Assert.Equal("// proof v1", live.Files["synapses/shout.cs"]);
        Assert.Equal("Shout in capitals.", live.Files["prompts/system.md"]);
        Assert.Equal("0.2.0", live.Version);
    }

    [Trait("host", "own")]
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
        var intoChatOS = brain.Services.GetRequiredService<IIntoChatOS>();
        await creator.Author(intoChatOS, Brain, null, "Shout back.", ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "shouter"));
        await using var changes = await brain.Observe<AppChanged>(app, ct);

        await creator.Implement(intoChatOS, IApp.Key(Brain, "shouter"), null);

        await changes.NextAsync(change => change.Status == AppStatus.Failed, ct);
        var failed = await app.Read();
        Assert.Equal(AppStatus.Failed, failed.Status);
    }

    [Trait("host", "own")]
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
        var intoChatOS = brain.Services.GetRequiredService<IIntoChatOS>();
        await creator.Author(intoChatOS, Brain, null, "Shout back.", ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "shouter"));
        await using var activity = await brain.Observe<AppActivity>(app, ct);

        await creator.Implement(intoChatOS, IApp.Key(Brain, "shouter"), null);

        var outcome = await activity.NextAsync(signal => signal.State is "completed" or "attention", ct);
        Assert.Equal("attention", outcome.State);
        Assert.Contains(AppFiles.DeclarationPath, outcome.Detail, StringComparison.Ordinal);
    }

    [Trait("host", "own")]
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
        var intoChatOS = brain.Services.GetRequiredService<IIntoChatOS>();
        await creator.Author(intoChatOS, Brain, null, "Shout back.", ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "shouter"));
        await using var changes = await brain.Observe<AppChanged>(app, ct);

        await creator.Implement(intoChatOS, IApp.Key(Brain, "shouter"), null);

        await changes.NextAsync(change => change.Status == AppStatus.Live, ct);
        var live = await app.Read();
        Assert.Contains(live.Manifest.Operations, operation => operation.Name == "ask");
        Assert.Equal("IGemma4", live.Settings["Model"]);
    }

    [Trait("host", "own")]
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
        var intoChatOS = brain.Services.GetRequiredService<IIntoChatOS>();
        await creator.Author(intoChatOS, Brain, null, "Shout back.", ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "shouter"));
        await using var changes = await brain.Observe<AppChanged>(app, ct);

        await creator.Implement(intoChatOS, IApp.Key(Brain, "shouter"), null);

        await changes.NextAsync(change => change.Status == AppStatus.Live, ct);
        var live = await app.Read();
        Assert.Contains(live.Manifest.Settings, setting => setting.Name == "Volume");
        Assert.Contains(live.Manifest.Operations, operation => operation.Name == "shout");
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ABuilderThatCallsAToolGetsItsResultAndStillImplements()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartAsync(model, ct);
        model.Script(Author, Authored(Spec));
        model.Script(Builder,
            FakeReply.Tool("check_csharp", """{"files":{"shout.cs":"await using var brain = await IntoChatOSClient.ConnectAsync(args);\nawait foreach (var request in brain.On(brain.Get<IntoChat.OS.Apps.IApp>(brain.Setting(\"App\")!), \"ask\", brain.Stopping)) { Console.WriteLine(request.Input); }"}}"""),
            Built("// proof v1", "Shout in capitals."));
        var creator = brain.Services.GetRequiredService<Creator>();
        var intoChatOS = brain.Services.GetRequiredService<IIntoChatOS>();
        await creator.Author(intoChatOS, Brain, null, "Shout back.", ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "shouter"));
        await using var changes = await brain.Observe<AppChanged>(app, ct);

        await creator.Implement(intoChatOS, IApp.Key(Brain, "shouter"), null);

        await changes.NextAsync(change => change.Status == AppStatus.Live, ct);
        Assert.Equal("// proof v1", (await app.Read()).Files["synapses/shout.cs"]);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task TheBuildersCheckReportsASynapseThatIsNotOneReaction()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartAsync(model, ct);
        model.Script(Author, Authored(Spec));
        model.Script(Builder,
            FakeReply.Tool("check_csharp", """{"files":{"shout.cs":"await using var brain = await IntoChatOSClient.ConnectAsync(args);\nvar app = brain.Get<IntoChat.OS.Apps.IApp>(brain.Setting(\"App\")!);\nawait foreach (var a in brain.On(app, \"ask\", brain.Stopping)) { }\nawait foreach (var b in brain.On(app, \"ask\", brain.Stopping)) { }"}}"""),
            Built("// proof v1", "Shout in capitals."));
        var creator = brain.Services.GetRequiredService<Creator>();
        var intoChatOS = brain.Services.GetRequiredService<IIntoChatOS>();
        await creator.Author(intoChatOS, Brain, null, "Shout back.", ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "shouter"));
        await using var changes = await brain.Observe<AppChanged>(app, ct);

        await creator.Implement(intoChatOS, IApp.Key(Brain, "shouter"), null);

        await changes.NextAsync(change => change.Status == AppStatus.Live, ct);
        // The compiler accepted the file; the check still answers the shape the gates will refuse.
        Assert.Contains(SynapseShape.SecondReaction, model.Requests(Builder)[1].ToString(), StringComparison.Ordinal);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task TheFinalBundleIsCheckedAgainAndItsErrorsReturnToTheBuilder()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FinalBundleSandbox();
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartAsync(model, ct, sandbox);
        model.Script(Author, Authored(Spec));
        model.Script(Builder,
            FakeReply.Tool("check_csharp", """{"files":{"shout.cs":"// valid"}}"""),
            Built("// invalid final", "data"),
            Built("// corrected final", "data"));
        var creator = brain.Services.GetRequiredService<Creator>();
        var intoChatOS = brain.Services.GetRequiredService<IIntoChatOS>();
        await creator.Author(intoChatOS, Brain, null, "Shout back.", ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "shouter"));
        await app.BeginBuild(null);
        await app.CompleteBuild(await app.CurrentBuildAttempt(), new() { ["shared.cs"] = "public static class Shared { }" }, null);
        await app.WriteFile("notes.md", "not C#", Guid.NewGuid());
        await using var changes = await brain.Observe<AppChanged>(app, ct);

        await creator.Implement(intoChatOS, IApp.Key(Brain, "shouter"), null);

        await changes.NextAsync(change => change.Status is AppStatus.Live or AppStatus.Failed, ct);
        var result = await app.Read();
        Assert.Equal(AppStatus.Live, result.Status);
        Assert.Equal("// corrected final", result.Files["synapses/shout.cs"]);
        Assert.Contains("CS1503", model.Requests(Builder)[2].ToString(), StringComparison.Ordinal);
        Assert.Contains(sandbox.Bundles, files => files.ContainsKey("shared.cs")
            && files.GetValueOrDefault("synapses/shout.cs") == "// invalid final"
            && !files.ContainsKey("notes.md") && !files.ContainsKey("prompts/system.md"));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task InvalidFinalRepliesExhaustTheExistingBudgetWithoutAssigningTheFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartAsync(model, ct, new FinalBundleSandbox());
        model.Script(Author, Authored(Spec));
        model.Script(Builder, Enumerable.Repeat((FakeReply)Built("// invalid final", "data"), 13).ToArray());
        var creator = brain.Services.GetRequiredService<Creator>();
        var intoChatOS = brain.Services.GetRequiredService<IIntoChatOS>();
        await creator.Author(intoChatOS, Brain, null, "Shout back.", ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "shouter"));
        await using var changes = await brain.Observe<AppChanged>(app, ct);

        await creator.Implement(intoChatOS, IApp.Key(Brain, "shouter"), null);

        await changes.NextAsync(change => change.Status is AppStatus.Live or AppStatus.Failed, ct);
        var result = await app.Read();
        Assert.Equal(AppStatus.Failed, result.Status);
        Assert.Empty(result.Files);
        Assert.Null(result.PublishedVersion);
        Assert.Equal(12, model.Requests(Builder).Count);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AStaleWorkersOutcomePublishesNoActivity()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new GatedSandbox();
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartAsync(model, ct, sandbox, buildFreshForSeconds: 0);
        model.Script(Author, Authored(Spec));
        model.Script(Builder,
            FakeReply.Tool("check_csharp", """{"files":{"shout.cs":"await using var brain = await IntoChatOSClient.ConnectAsync(args);\nawait foreach (var request in brain.On(brain.Get<IntoChat.OS.Apps.IApp>(brain.Setting(\"App\")!), \"ask\", brain.Stopping)) { }"}}"""),
            Built("// stale", "Shout."));
        var creator = brain.Services.GetRequiredService<Creator>();
        var intoChatOS = brain.Services.GetRequiredService<IIntoChatOS>();
        await creator.Author(intoChatOS, Brain, null, "Shout back.", ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "shouter"));
        await using var activity = await brain.Observe<AppActivity>(app, ct);
        await creator.Implement(intoChatOS, IApp.Key(Brain, "shouter"), null);
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
    public void AuthoringPromptsRequireObservableResultsWithoutBanningHeadlessOperations()
    {
        Assert.Contains("observable outcome", AgentPrompts.Author, StringComparison.Ordinal);
        foreach (var prompt in new[] { AgentPrompts.Author, AgentPrompts.Builder, AgentPrompts.Synapse })
        {
            Assert.Contains("declared entry", prompt, StringComparison.Ordinal);
            Assert.Contains("diagnostics, never the person's", prompt, StringComparison.Ordinal);
            Assert.Contains("external", prompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheBuilderBuildsAppsAsALayoutClassPlusHandlers()
    {
        foreach (var phrase in new[] { "layout class", "Layout.cs", "static UiNode Render", "ViewPublisher", "UIVocabulary.ViewType", "ViewAction", "verb:<arg>", "at most 40 lines", "No colours", "Never encode a command as JSON" })
        {
            Assert.Contains(phrase, AgentPrompts.Builder, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheBuilderDistinguishesRendererKindsFromChildNames()
    {
        Assert.Contains("Read the UI contracts", AgentPrompts.Builder, StringComparison.Ordinal);
        Assert.Contains("UiChildRef.Kind", AgentPrompts.Builder, StringComparison.Ordinal);
        Assert.Contains("in Name, never Kind", AgentPrompts.Builder, StringComparison.Ordinal);
        Assert.Contains("Different text children all use kind \"text\"", AgentPrompts.Builder, StringComparison.Ordinal);
        Assert.Contains("UIVocabulary.TextType", AgentPrompts.Builder, StringComparison.Ordinal);
        Assert.Contains("UIVocabulary.SurfaceType", AgentPrompts.Builder, StringComparison.Ordinal);
        Assert.Contains("entry that matches the root you actually compose", AgentPrompts.Builder, StringComparison.Ordinal);
        Assert.Contains("Never add <AppRequested>", AgentPrompts.Builder, StringComparison.Ordinal);
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
            .ConfigureServices(services =>
            {
                services.AddSingleton(sandbox ?? new CreatorSandbox());
                if (sandbox is IAppRuntime runtime)
                {
                    services.RemoveAll<IAppRuntime>();
                    services.AddSingleton(runtime);
                }
            })
            .StartAsync(ct);

    private sealed class FinalBundleSandbox : IScriptSandbox, IAppRuntime
    {
        // Compile runs on silo threads (the builder's check and the deploy) while the fact reads; snapshot under the lock.
        private readonly Lock gate = new();
        private readonly List<Dictionary<string, string>> bundles = [];
        public IReadOnlyList<Dictionary<string, string>> Bundles { get { lock (gate) { return [.. bundles]; } } }
        public bool CanRun => true;
        public string AuthoringDescription => "csharp uses sandbox contracts.";
        public Task<Verdict[]> Judge(string brain, IReadOnlyDictionary<string, string> bundle, Signal signal, CancellationToken cancellationToken)
            => Task.FromResult<Verdict[]>([]);
        public Task<ScriptContractCatalog> ReadContracts(IReadOnlyList<string> modules, CancellationToken cancellationToken)
            => Task.FromResult(new ScriptContractCatalog([], [], ""));
        public Task<ScriptCompilationCheck> Compile(IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken)
        {
            lock (gate) { bundles.Add(new(files)); }
            return Task.FromResult(files.Values.Any(source => source == "// invalid final")
                ? new ScriptCompilationCheck(false, [new("synapses/shout.cs", "CS1503", 1, "Invalid operation overload")])
                : new ScriptCompilationCheck(true, []));
        }
        public async Task<IReadOnlyDictionary<string, string>> Deploy(string appKey, Dictionary<string, string> sources, Dictionary<string, string> configuration, CancellationToken cancellationToken)
        {
            if (!(await Compile(sources, cancellationToken)).Success) { throw new InvalidOperationException("Invalid final bundle"); }
            return new Dictionary<string, string>();
        }
        public Task Retire(string appKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }

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
