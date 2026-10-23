using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using IntoChat.OS.AI;
using IntoChat.OS.AI.Models;
using IntoChat.OS.AI.Models.OpenAI;
using IntoChat.OS.AI.Models.Signals;
using IntoChat.OS.Apps;
using IntoChat.OS.Apps.Signals;
using IntoChat.OS.Framework;
using IntoChat.OS.Microsoft.CSharp.Runtime;
using IntoChat.OS.Modules.Apps.Tests.Integration.Runtimes;
using IntoChat.OS.Testing;
using IntoChat.OS.Testing.Model;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Modules.Apps.Tests.Integration;

// The fast path: one sentence becomes one live synapse, written by one model call (plus at most one repair)
// against what the caller can see, gated by the shape and the compiler, deployed through WriteBehavior.
public sealed class FastPathFacts : IDisposable
{
    private const string Brain = "fast-path-facts";
    // The fast path's model is a setting; the fake model server answers for the OpenAI one.
    private const string Writer = "gpt-5.6-luna";

    private readonly string _features = Directory.CreateTempSubdirectory("fast-path-features-").FullName;

    public void Dispose() => Directory.Delete(_features, recursive: true);

    private const string Lemons = """
        using IntoChat.OS.Apps;
        using IntoChat.OS.Flutter.Text;

        // Answers lemons on every request.
        await using var brain = await IntoChatOSClient.ConnectAsync(args);
        var app = brain.Get<IApp>(brain.Setting("App")!);
        await foreach (var request in brain.On(app, "ask", brain.Stopping))
        { await brain.Get<IText>(app.GetPrimaryKeyString() + "/answer").Set("lemons"); }
        """;

    private const string TwoReactions = """
        await using var brain = await IntoChatOSClient.ConnectAsync(args);
        var app = brain.Get<IntoChat.OS.Apps.IApp>(brain.Setting("App")!);
        await foreach (var open in brain.On(app, "ask", brain.Stopping)) { }
        await foreach (var close in brain.On(app, "ask", brain.Stopping)) { }
        """;

    [Trait("host", "own")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStagedAssignmentCanPublishBeforeCompletionAndRollbackUsesAFreshRevision(bool refuse)
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new PublishingRuntime();
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await TestBrain.Create().WithModule<AIModule>()
            .WithModule<AppsModule, AppsOptions>(apps => apps.FastPathModel = nameof(IGpt56Luna))
            .WithFakeModel(model).ConfigureServices(services =>
            {
                services.RemoveAll<IAppRuntime>();
                services.AddSingleton<IAppRuntime>(runtime);
                services.AddSingleton<IScriptSandbox>(new FastPathSandbox());
            }).StartAsync(ct);
        var app = await Created(brain, "startup");
        var key = IApp.Key(Brain, "startup");
        await app.WriteBehavior("old.cs", "public static class Old { }", Guid.NewGuid());
        var oldRevision = (await app.Read()).ProgramRevision;
        using (brain.Owner.AsAppIn(Brain, key)) { await app.Present(oldRevision, new(new Sphere("old", []))); }
        var attempted = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        long proposedRevision = 0;
        runtime.Startup = async (sources, configuration) =>
        {
            var revision = long.Parse(configuration["ProgramRevision"]);
            var proposed = sources.ContainsKey("lemons.cs");
            if (proposed) { proposedRevision = revision; }
            Exception? failure = null;
            try
            {
                Assert.Equal(revision, (await app.Read()).ProgramRevision);
                Assert.Null((await app.Read()).Presentation);
                using (brain.Owner.AsAppIn(Brain, key))
                { await app.Present(revision, new(new Sphere(proposed ? "proposed" : "restored", []))); }
            }
            catch (Exception error) { failure = error; }
            if (proposed) { attempted.TrySetResult(failure); }
            if (proposed && refuse) { throw new InvalidOperationException("assignment refused after startup"); }
        };
        model.Script(Writer, Reply("lemons.cs", Lemons));
        await using var outcomes = await brain.Observe<Signal>(app, ct);
        await app.WriteSynapse("when asked, answer lemons", [], Guid.NewGuid());
        var failure = await attempted.Task.WaitAsync(ct);
        await outcomes.NextAsync(signal => signal is SynapseWritten or SynapseRejected, ct);
        Assert.Null(failure);
        var state = await app.Read();
        Assert.Equal(refuse ? "restored" : "proposed", state.Presentation!.Root!.Sphere!.Icon);
        Assert.Equal(refuse ? proposedRevision + 1 : proposedRevision, state.ProgramRevision);
        Assert.Equal(!refuse, state.Files.ContainsKey("lemons.cs"));
        using (brain.Owner.AsAppIn(Brain, key))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => app.Present(oldRevision, new(new Sphere("stale", []))));
            if (refuse) { await Assert.ThrowsAsync<InvalidOperationException>(() => app.Present(proposedRevision, new(new Sphere("stale", [])))); }
        }
    }

    private sealed class PublishingRuntime : IAppRuntime
    {
        public Func<Dictionary<string, string>, Dictionary<string, string>, Task>? Startup { get; set; }
        public Action? Retiring { get; set; }
        public async Task<IReadOnlyDictionary<string, string>> Deploy(string appKey, Dictionary<string, string> sources, Dictionary<string, string> configuration, CancellationToken cancellationToken)
        {
            if (Startup is { } startup) { await startup(sources, configuration); }
            return sources.Keys.ToDictionary(path => path, path => "hash:" + path);
        }
        public Task Retire(string appKey, CancellationToken cancellationToken) { Retiring?.Invoke(); return Task.CompletedTask; }
    }

    [Trait("host", "own")]
    [Fact]
    public async Task SettingsCannotReplaceTheAssignmentsProgramRevision()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new PublishingRuntime();
        await using var brain = await TestBrain.Create().WithModule<AppsModule>()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IAppRuntime>();
                services.AddSingleton<IAppRuntime>(runtime);
            }).StartAsync(ct);
        var app = await Created(brain, "configured");
        await app.WriteBehavior("old.cs", "public static class Old { }", Guid.NewGuid());
        Dictionary<string, string>? assigned = null;
        long heldRevision = -1;
        runtime.Startup = async (_, configuration) =>
        {
            assigned = configuration;
            heldRevision = (await app.Read()).ProgramRevision;
        };
        var state = await app.Configure(new() { ["ProgramRevision"] = "spoofed", ["App"] = "other", ["Brain"] = "other", ["theme"] = "dark" }, []);
        Assert.NotNull(assigned);
        Assert.Equal(state.ProgramRevision, heldRevision);
        Assert.Equal(state.ProgramRevision.ToString(System.Globalization.CultureInfo.InvariantCulture), assigned["ProgramRevision"]);
        Assert.Equal(IApp.Key(Brain, "configured"), assigned["App"]);
        Assert.Equal(Brain, assigned["Brain"]);
        Assert.Equal("dark", assigned["theme"]);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AWriterReconcilingAfterDeletionCannotRegisterAPhantomApp()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new PublishingRuntime();
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await TestBrain.Create().WithModule<AIModule>()
            .WithModule<AppsModule, AppsOptions>(apps => apps.FastPathModel = nameof(IGpt56Luna))
            .WithFakeModel(model).ConfigureServices(services =>
            {
                services.RemoveAll<IAppRuntime>();
                services.AddSingleton<IAppRuntime>(runtime);
                services.AddSingleton<IScriptSandbox>(new FastPathSandbox());
            }).StartAsync(ct);
        var app = await Created(brain, "deleted");
        var key = IApp.Key(Brain, "deleted");
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retireCalls = 0;
        runtime.Retiring = () => { if (Interlocked.Increment(ref retireCalls) == 2) { retired.TrySetResult(); } };
        runtime.Startup = async (sources, _) =>
        {
            staged.TrySetResult();
            await deleted.Task.WaitAsync(ct);
            throw new InvalidOperationException("assignment refused after deletion");
        };
        model.Script(Writer, Reply("lemons.cs", Lemons));
        await app.WriteSynapse("when asked, answer lemons", [], Guid.NewGuid());
        await staged.Task.WaitAsync(ct);
        await app.Delete();
        deleted.TrySetResult();
        await retired.Task.WaitAsync(ct);
        Assert.Equal(0, (await app.Read()).ProgramRevision);
        var kernel = brain.Services.GetRequiredService<IKernel>();
        await foreach (var instance in kernel.InstancesIn(Brain, kernel.TypeOf(typeof(IApp)), ct))
        { Assert.NotEqual(key, instance.Key); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.Request("ask", ""));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ASentenceBecomesALiveSynapse()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new FakeAppRuntime();
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, runtime, new FastPathSandbox(), ct);
        model.Script(Writer, Reply("lemons.cs", Lemons));
        var app = await Created(brain, "grocer");
        await using var written = await brain.Observe<SynapseWritten>(app, ct);

        var requested = await app.WriteSynapse("when asked, answer lemons", [], Guid.NewGuid());

        Assert.Equal(SynapseStatus.Requested, Assert.Single(requested.Synapses!).Status);
        var signal = await written.NextAsync(ct: ct);
        Assert.Equal(("lemons.cs", "hash:lemons.cs", 1), (signal.Path, signal.Hash, signal.Attempts));
        var live = await app.Read();
        Assert.Equal(Lemons, live.Files["lemons.cs"]);
        var entry = Assert.Single(live.Synapses!);
        Assert.Equal((SynapseStatus.Written, "lemons.cs", "hash:lemons.cs", 1, null), (entry.Status, entry.Path, entry.Hash, entry.Attempts, entry.Base));
        Assert.Empty(entry.Errors);
        Assert.Contains(runtime.Deployed, deployed => deployed.Sources.GetValueOrDefault("lemons.cs") == Lemons);
        var prompt = Prompt(model);
        Assert.Contains("when asked, answer lemons", prompt, StringComparison.Ordinal);
        Assert.Contains("exactly one reaction", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("base synapse to fork", prompt, StringComparison.Ordinal);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task TheWritersModelCallLandsInTheBrainOfTheTargetApp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, new FakeAppRuntime(), new FastPathSandbox(), ct);
        model.Script(Writer, Reply("lemons.cs", Lemons));
        var app = await Created(brain, "grocer");
        await using var started = await brain.Observe<InferenceStarted>(brain.Get<IGpt56Luna>(ModelAddress.Key(Brain)), ct);
        await using var written = await brain.Observe<SynapseWritten>(app, ct);

        await app.WriteSynapse("when asked, answer lemons", [], Guid.NewGuid());

        Assert.Equal($"{Brain}/models", (await started.NextAsync(ct: ct)).NeuronId);
        Assert.Equal("lemons.cs", (await written.NextAsync(ct: ct)).Path);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task TheWritersModelCallCapsReasoningAndOutput()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, new FakeAppRuntime(), new FastPathSandbox(), ct);
        model.Script(Writer, Reply("lemons.cs", Lemons));
        var app = await Created(brain, "grocer");
        await using var written = await brain.Observe<SynapseWritten>(app, ct);

        await app.WriteSynapse("when asked, answer lemons", [], Guid.NewGuid());

        await written.NextAsync(ct: ct);
        var request = Assert.Single(model.Requests(Writer));
        Assert.Equal("none", request.GetProperty("reasoning_effort").GetString());
        var cap = request.TryGetProperty("max_completion_tokens", out var completion) ? completion : request.GetProperty("max_tokens");
        Assert.Equal(new AppsOptions().FastPathMaxOutputTokens, cap.GetInt32());
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ACompileFailureGetsOneRepairThenIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new FakeAppRuntime();
        var sandbox = new FastPathSandbox
        {
            Result = files => new(false, [new("lemons.cs", "CS0103", 9, "The name 'lemon' does not exist in the current context")]),
        };
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, runtime, sandbox, ct);
        model.Script(Writer, Reply("lemons.cs", Lemons), Reply("lemons.cs", Lemons));
        var app = await Created(brain, "grocer");
        await using var rejected = await brain.Observe<SynapseRejected>(app, ct);

        await app.WriteSynapse("when asked, answer lemons", [], Guid.NewGuid());

        var signal = await rejected.NextAsync(ct: ct);
        Assert.Equal("when asked, answer lemons", signal.Sentence);
        Assert.Contains(signal.Errors, error => error.Contains("CS0103", StringComparison.Ordinal));
        var entry = Assert.Single((await app.Read()).Synapses!);
        Assert.Equal((SynapseStatus.Rejected, 2), (entry.Status, entry.Attempts));
        Assert.Contains(entry.Errors, error => error.Contains("lemons.cs:9 CS0103", StringComparison.Ordinal));
        // One repair, and the repair carries the errors it has to fix.
        Assert.Equal(2, model.Requests(Writer).Count);
        Assert.Contains("CS0103", Prompt(model, 1), StringComparison.Ordinal);
        Assert.DoesNotContain(runtime.Deployed, deployed => deployed.Sources.ContainsKey("lemons.cs"));
        Assert.DoesNotContain("lemons.cs", (await app.Read()).Files.Keys);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AShapeFailureIsNeverDeployed()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new FakeAppRuntime();
        var sandbox = new FastPathSandbox();
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, runtime, sandbox, ct);
        model.Script(Writer, Reply("twice.cs", TwoReactions), Reply("twice.cs", TwoReactions));
        var app = await Created(brain, "grocer");
        await using var rejected = await brain.Observe<SynapseRejected>(app, ct);

        await app.WriteSynapse("when asked, answer twice", [], Guid.NewGuid());

        var signal = await rejected.NextAsync(ct: ct);
        Assert.Contains(signal.Errors, error => error.Contains(SynapseShape.SecondReaction, StringComparison.Ordinal));
        Assert.Contains(SynapseShape.SecondReaction, Prompt(model, 1), StringComparison.Ordinal);
        // The shape is checked before the compiler, and nothing that failed it reaches the runtime.
        Assert.DoesNotContain(sandbox.Compiled, files => files.ContainsKey("twice.cs"));
        Assert.Empty(runtime.Deployed);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AModelThatDoesNotAnswerIsARejectionNotARepair()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, new FakeAppRuntime(), new FastPathSandbox(), ct);
        // An empty script: the model answers 500.
        model.Script(Writer);
        var app = await Created(brain, "grocer");
        await using var rejected = await brain.Observe<SynapseRejected>(app, ct);

        await app.WriteSynapse("when asked, answer lemons", [], Guid.NewGuid());

        Assert.Contains("did not answer", Assert.Single((await rejected.NextAsync(ct: ct)).Errors), StringComparison.Ordinal);
        var entry = Assert.Single((await app.Read()).Synapses!);
        // One attempt: the adapter's own HTTP retries are not repairs.
        Assert.Equal((SynapseStatus.Rejected, 1), (entry.Status, entry.Attempts));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ARefusalIsARejectionWithItsReasonAndNothingIsCompiledOrDeployed()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new FakeAppRuntime();
        var sandbox = new FastPathSandbox();
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, runtime, sandbox, ct);
        model.Script(Writer, "REFUSE: no contract publishes BTC prices", Reply("btc.cs", Lemons));
        var app = await Created(brain, "trader");
        await using var rejected = await brain.Observe<SynapseRejected>(app, ct);

        await app.WriteSynapse("when BTC drops 5 %, notify me", [], Guid.NewGuid());

        var reason = Assert.Single((await rejected.NextAsync(ct: ct)).Errors);
        Assert.Equal(SynapseWriter.NoContract + "no contract publishes BTC prices", reason);
        var live = await app.Read();
        var entry = Assert.Single(live.Synapses!);
        Assert.Equal((SynapseStatus.Rejected, 1, reason), (entry.Status, entry.Attempts, Assert.Single(entry.Errors)));
        // A refusal is final: no repair, no compile, no WriteBehavior.
        Assert.Single(model.Requests(Writer));
        Assert.Empty(sandbox.Compiled);
        Assert.Empty(runtime.Deployed);
        Assert.DoesNotContain("btc.cs", live.Files.Keys);
    }

    [Theory]
    [InlineData("REFUSE: no price feed", "no price feed")]
    [InlineData("```\nrefuse:   no price feed  \n```", "no price feed")]
    [InlineData("REFUSE:", "the model did not say what is missing")]
    [InlineData("{\"path\": \"a.cs\", \"source\": \"// REFUSE: not a refusal\"}", null)]
    public void OnlyALeadingMarkerLineIsARefusal(string reply, string? missing)
        => Assert.Equal(missing, SynapseWriter.Refusal(reply));

    [Trait("host", "own")]
    [Fact]
    public async Task ThePromptCarriesTheKeyRuleAndTheRefusal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, new FakeAppRuntime(), new FastPathSandbox(), ct);
        model.Script(Writer, Reply("lemons.cs", Lemons));
        var app = await Created(brain, "grocer");
        await using var written = await brain.Observe<SynapseWritten>(app, ct);

        await app.WriteSynapse("when asked, answer lemons", [], Guid.NewGuid());

        await written.NextAsync(ct: ct);
        var prompt = Prompt(model);
        Assert.Contains("Every neuron key a synapse creates or reads starts with the app key or the brain scope", prompt, StringComparison.Ordinal);
        Assert.Contains("app.GetPrimaryKeyString() + \"/alert\"", prompt, StringComparison.Ordinal);
        Assert.Contains("A sentence that needs a missing contract is refused, never faked", prompt, StringComparison.Ordinal);
        Assert.Contains(AgentPrompts.Refusal + " <what is missing", prompt, StringComparison.Ordinal);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task TheFastPathNeverWritesTheDeclaration()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new FakeAppRuntime();
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, runtime, new FastPathSandbox(), ct);
        model.Script(Writer, Reply(AppFiles.DeclarationPath, Lemons), Reply(AppFiles.DeclarationPath, Lemons));
        var app = await Created(brain, "grocer");
        await using var rejected = await brain.Observe<SynapseRejected>(app, ct);

        await app.WriteSynapse("when asked, answer lemons", [], Guid.NewGuid());

        Assert.Contains(AppFiles.DeclarationPath, Assert.Single((await rejected.NextAsync(ct: ct)).Errors), StringComparison.Ordinal);
        Assert.Empty(runtime.Deployed);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AReplayedOperationWritesNothingAndCallsNoModel()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new FakeAppRuntime();
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, runtime, new FastPathSandbox(), ct);
        model.Script(Writer, Reply("lemons.cs", Lemons));
        var app = await Created(brain, "grocer");
        var operation = Guid.NewGuid();
        await using (var written = await brain.Observe<SynapseWritten>(app, ct))
        {
            await app.WriteSynapse("when asked, answer lemons", [], operation);
            await written.NextAsync(ct: ct);
        }
        var version = (await app.Read()).Version;
        await using var activity = await brain.Observe<AppActivity>(app, ct);

        var replayed = await app.WriteSynapse("when asked, answer lemons", [], operation);

        Assert.Equal(version, replayed.Version);
        Assert.Equal(SynapseStatus.Written, Assert.Single(replayed.Synapses!).Status);
        // An absence has no signal to wait for: a writer would report "working" within this window.
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(TimeSpan.FromSeconds(2));
        try { await activity.NextAsync(signal => signal.State == "working", window.Token); Assert.Fail("A replayed operation ran the writer."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        Assert.Single(model.Requests(Writer));
        Assert.Single(runtime.Deployed);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ExamplesComeOnlyFromVisibleSynapses()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFeature("os", "Orchard", "orchard", "count.cs", Synapse("Counts the lemons in the orchard", "FEATURE-LEMONS"));
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, new FakeAppRuntime(), new FastPathSandbox(), ct, features: _features);
        model.Script(Writer, Reply("lemons.cs", Lemons));
        var pantry = await Created(brain, "pantry");
        await pantry.WriteBehavior("count.cs", Synapse("Counts the lemons in the pantry", "OWN-LEMONS"), Guid.NewGuid());
        var elsewhere = brain.Get<IApp>(IApp.Key("fast-path-elsewhere", "pantry"));
        await elsewhere.Create(new("pantry", "another brain's", [new("ask", "ask it")], [], []), "spec");
        await elsewhere.WriteBehavior("count.cs", Synapse("Counts the lemons in the pantry", "HIDDEN-LEMONS"), Guid.NewGuid());
        var app = await Created(brain, "grocer");
        await using var written = await brain.Observe<SynapseWritten>(app, ct);

        await app.WriteSynapse("count the lemons", [], Guid.NewGuid());

        await written.NextAsync(ct: ct);
        var prompt = Prompt(model);
        Assert.Contains("OWN-LEMONS", prompt, StringComparison.Ordinal);
        Assert.Contains("FEATURE-LEMONS", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("HIDDEN-LEMONS", prompt, StringComparison.Ordinal);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task LayoutClassesAreReferenceExamplesOtherSharedFilesAreNot()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFeature("os", "Orchard", "orchard", "OrchardLayout.cs", "public static class OrchardLayout { public static UiNode? Render(int lemons) => null; /* LAYOUT-LEMONS */ }");
        File.WriteAllText(Path.Combine(_features, "os", "Orchard", "Helper.cs"), "public static class Helper { public static int Twice(int n) => n * 2; /* HELPER-LEMONS */ }");
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, new FakeAppRuntime(), new FastPathSandbox(), ct, features: _features);

        var ranking = await Examples(brain, embeddings: null).Rank(Brain, "lemons orchard layout", ct);

        Assert.Contains(ranking.Layouts, example => example.Source.Contains("LAYOUT-LEMONS", StringComparison.Ordinal));
        Assert.DoesNotContain(ranking.Layouts.Concat(ranking.Examples), example => example.Source.Contains("HELPER-LEMONS", StringComparison.Ordinal));
        Assert.DoesNotContain(ranking.Examples, example => example.Source.Contains("LAYOUT-LEMONS", StringComparison.Ordinal));
        Assert.Null(ranking.Base);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task TheShippedCalculatorAndDesktopLayoutsComeFirstAtMostTwoAndAreLabelled()
    {
        var ct = TestContext.Current.CancellationToken;
        var started = Stopwatch.GetTimestamp();
        var stages = new ConcurrentQueue<(string Name, double ElapsedMs)>();
        void Mark(string name) => stages.Enqueue((name, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        var kernelCalls = new ConcurrentQueue<(long Id, string Method, string Phase, string Stage, double ElapsedMs)>();
        string? kernelPhase = null;
        void RecordKernel(long id, string method, string phase, string stage)
            => kernelCalls.Enqueue((id, method, phase, stage, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        var grainReads = new ConcurrentQueue<(long Id, string Method, string Phase, string Stage, double ElapsedMs)>();
        void RecordGrainRead(long id, string method, string phase, string stage)
            => grainReads.Enqueue((id, method, phase, stage, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        var syntaxBoundaries = new ConcurrentQueue<(long Invocation, string Stage, string State, string Phase, double DurationMs, long AllocatedBytes, double ElapsedMs)>();
        void RecordSyntaxBoundary(long invocation, string stage, string state, string phase, double durationMs, long allocatedBytes)
            => syntaxBoundaries.Enqueue((invocation, stage, state, phase, durationMs, allocatedBytes, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        var runtime = new FakeAppRuntime();
        var sandbox = new FastPathSandbox();
        var shipped = ShippedFeatures();
        await using var model = await FakeModelServer.StartAsync(ct);
        model.Intercept = request =>
        {
            if (request.GetProperty("model").GetString() == Writer) { Mark("fake-http-arrived"); }
            return null;
        };
        await using var brain = await Start(model, runtime, sandbox, ct, features: shipped, configure: services =>
        {
            services.AddSingleton<IKernel>(provider => new TimedKernel(provider.GetRequiredService<Kernel>(),
                () => Volatile.Read(ref kernelPhase), RecordKernel));
            services.AddSingleton<IOutgoingGrainCallFilter>(new TimedGrainReads(() => Volatile.Read(ref kernelPhase), RecordGrainRead));
            services.AddSingleton<ILogger<SynapseExamples>>(provider => new SyntaxBoundaryLog(
                provider.GetRequiredService<ILoggerFactory>().CreateLogger<SynapseExamples>(),
                () => Volatile.Read(ref kernelPhase), RecordSyntaxBoundary));
        });
        Mark("host-ready");
        model.Script(Writer, Reply("lemons.cs", Lemons));
        var app = await Created(brain, "grocer");
        Mark("app-created");

        Volatile.Write(ref kernelPhase, "setup-ranking");
        Mark("ranking-started");
        var ranking = await Examples(brain, embeddings: null).Rank(Brain, "count the lemons", ct);
        Volatile.Write(ref kernelPhase, null);
        Mark("ranking-complete");

        Assert.Equal(2, ranking.Layouts.Length);
        Assert.All(ranking.Layouts, layout => Assert.Matches("(?i)^features/os/(calculator|desktop)$", layout.Origin));
        Assert.DoesNotContain(ranking.Examples, example => example.Source.Contains("static UiNode Render", StringComparison.Ordinal));

        await using var inference = await brain.Observe<Signal>(brain.Get<IGpt56Luna>(ModelAddress.Key(Brain)), ct);
        await using var written = await brain.Observe<SynapseWritten>(app, ct);
        var operation = Guid.NewGuid();
        var compiledBeforeWrite = sandbox.Compiled.Count;
        var deployedBeforeWrite = runtime.Deployed.Count;
        try
        {
            Volatile.Write(ref kernelPhase, "writer");
            Mark("write-requested");
            await app.WriteSynapse("count the lemons", [], operation);
            Mark("write-accepted");
            await written.NextAsync(ct: ct);
            Mark("written-observed");
            var prompt = Prompt(model);
            Assert.Contains("reference only: shared layout class, not a synapse", prompt, StringComparison.Ordinal);
            Assert.Contains("static UiNode Render", prompt, StringComparison.Ordinal);
        }
        finally
        {
            Volatile.Write(ref kernelPhase, null);
            // Snapshot before any subscription, brain or fake-model disposal; never include prompt or signal payloads.
            var signals = inference.Snapshot;
            TestContext.Current.TestOutputHelper?.WriteLine(JsonSerializer.Serialize(new
            {
                diagnostic = "fastpath-shipped-layouts",
                operation,
                elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                stages = stages.Select(stage => new { name = stage.Name, elapsedMs = stage.ElapsedMs }),
                kernel = kernelCalls.Select(call => new { id = call.Id, method = call.Method, phase = call.Phase, stage = call.Stage, elapsedMs = call.ElapsedMs }),
                grainReads = grainReads.Select(call => new { id = call.Id, method = call.Method, phase = call.Phase, stage = call.Stage, elapsedMs = call.ElapsedMs }),
                syntax = syntaxBoundaries.Select(boundary => new
                {
                    invocation = boundary.Invocation,
                    stage = boundary.Stage,
                    state = boundary.State,
                    phase = boundary.Phase,
                    durationMs = boundary.DurationMs,
                    allocatedBytes = boundary.AllocatedBytes,
                    elapsedMs = boundary.ElapsedMs,
                }),
                inferenceStarted = signals.OfType<InferenceStarted>().Count(),
                inferenceCompleted = signals.OfType<InferenceCompleted>().Count(),
                inferenceFailed = signals.OfType<InferenceFailed>().Count(),
                modelRequests = model.Requests(Writer).Count,
                compiledBeforeWrite,
                compiled = sandbox.Compiled.Count,
                deployedBeforeWrite,
                deployed = runtime.Deployed.Count,
                written = written.Snapshot.Count,
            }));
        }
    }

    private static string ShippedFeatures()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var features = Path.Combine(dir.FullName, "features");
            if (Directory.Exists(Path.Combine(features, "os", "Calculator"))) { return features; }
        }
        throw new DirectoryNotFoundException("features/os/Calculator not found above the test output.");
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AStrongMatchIsTheBaseTheModelForks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, new FakeAppRuntime(), new FastPathSandbox(), ct);
        model.Script(Writer, Reply("limes.cs", Lemons));
        var pantry = await Created(brain, "pantry");
        await pantry.WriteBehavior("count.cs", Synapse("Counts the lemons in the pantry", "BASE-LEMONS"), Guid.NewGuid());
        var app = await Created(brain, "grocer");
        await using var written = await brain.Observe<SynapseWritten>(app, ct);

        await app.WriteSynapse("count the lemons in the pantry", [], Guid.NewGuid());

        await written.NextAsync(ct: ct);
        var prompt = Prompt(model);
        var fork = prompt.IndexOf("base synapse to fork", StringComparison.Ordinal);
        Assert.True(fork >= 0, "The prompt names no base synapse.");
        Assert.Contains("BASE-LEMONS", prompt[fork..], StringComparison.Ordinal);
        Assert.Equal(IApp.Key(Brain, "pantry") + "/count.cs", Assert.Single((await app.Read()).Synapses!).Base);
    }

    // The spec is "pantry: count / Counts the lemons in the pantry": count, lemons and pantry are in it, pears and apples are not.
    [Trait("host", "own")]
    [Theory]
    [InlineData("count lemons pantry pears", true)]
    [InlineData("count lemons pantry apples pears", false)]
    public async Task AWordShareForksFromThreeInFour(string sentence, bool forked)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, new FakeAppRuntime(), new FastPathSandbox(), ct);
        var pantry = await Created(brain, "pantry");
        await pantry.WriteBehavior("count.cs", Synapse("Counts the lemons in the pantry", "BASE-LEMONS"), Guid.NewGuid());

        var ranking = await Examples(brain, embeddings: null).Rank(Brain, sentence, ct);

        // Three words in five is 0.6: past the cosine threshold, short of the word one.
        Assert.Equal(forked, ranking.Base is not null);
    }

    [Trait("host", "own")]
    [Theory]
    [InlineData(0.65, true)]
    [InlineData(0.55, false)]
    public async Task AMeaningForksFromTheCosineThreshold(double similarity, bool forked)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, new FakeAppRuntime(), new FastPathSandbox(), ct);
        var pantry = await Created(brain, "pantry");
        await pantry.WriteBehavior("count.cs", Synapse("Counts the lemons in the pantry", "BASE-LEMONS"), Guid.NewGuid());

        // No word of the sentence is in the spec: only the meaning can fork it.
        var ranking = await Examples(brain, new FixedSimilarity("tally citrus", similarity)).Rank(Brain, "tally citrus", ct);

        Assert.Equal(similarity, Assert.Single(ranking.Examples).Score, 3);
        Assert.Equal(forked, ranking.Base is not null);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AFullVectorCacheStillRanksEveryExampleByMeaning()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, new FakeAppRuntime(), new FastPathSandbox(), ct);
        var pantry = await Created(brain, "pantry");
        await pantry.WriteBehavior("count.cs", Synapse("Counts the lemons", "COUNT-LEMONS"), Guid.NewGuid());
        await pantry.WriteBehavior("weigh.cs", Synapse("Weighs the lemons", "WEIGH-LEMONS"), Guid.NewGuid());
        var examples = Examples(brain, new FixedSimilarity("tally citrus", 0.65), vectorCapacity: 2);
        Assert.Equal(2, (await examples.Rank(Brain, "tally citrus", ct)).Examples.Length);
        await pantry.WriteBehavior("peel.cs", Synapse("Peels the lemons", "PEEL-LEMONS"), Guid.NewGuid());

        var ranking = await examples.Rank(Brain, "tally citrus", ct);

        Assert.Equal(3, ranking.Examples.Length);
        // Every score is the embeddings' cosine: a vector lost to the overflow would drop the ranking to words, which score 0 here.
        Assert.All(ranking.Examples, example => Assert.Equal(0.65, example.Score, 3));
        Assert.NotNull(ranking.Base);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AProvenSynapseOutranksAnEquallySimilarNeverRunOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, new FakeAppRuntime(), new FastPathSandbox(), ct);
        var pantry = await Created(brain, "pantry");
        await pantry.WriteBehavior("count.cs", Synapse("Counts the lemons", "NEW-LEMONS"), Guid.NewGuid());
        await pantry.WriteBehavior("tally.cs", Synapse("Tallies the lemons", "PROVEN-LEMONS"), Guid.NewGuid());
        // Both are deployed and running; only one has ever run, forty times cleanly.
        await Script(brain, Ran("count.cs", runs: 0, failures: 0), Ran("tally.cs", runs: 40, failures: 0));

        var ranking = await Examples(brain, new FixedSimilarity("tally citrus", 0.65)).Rank(Brain, "tally citrus", ct);

        Assert.Equal(["tally.cs", "count.cs"], ranking.Examples.Select(example => example.Path));
        Assert.Equal("tally.cs", ranking.Base?.Path);
        // Never run is neutral: its score is its similarity alone.
        Assert.Equal(0.65, ranking.Examples[1].Score, 3);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AFailingSynapseIsNeverTheForkBaseOverAHealthyOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, new FakeAppRuntime(), new FastPathSandbox(), ct);
        var pantry = await Created(brain, "pantry");
        await pantry.WriteBehavior("count.cs", Synapse("Counts the lemons", "FAILING-LEMONS"), Guid.NewGuid());
        await pantry.WriteBehavior("tally.cs", Synapse("Tallies the lemons", "HEALTHY-LEMONS"), Guid.NewGuid());
        // Both are close enough to fork; the far closer one fails four runs in five while it keeps running.
        await Script(brain, Ran("count.cs", runs: 30, failures: 24), Ran("tally.cs", runs: 30, failures: 0));
        var similarity = new ScriptedSimilarity("tally citrus", ("Counts the lemons", 0.85), ("Tallies the lemons", 0.65));

        var ranking = await Examples(brain, similarity).Rank(Brain, "tally citrus", ct);

        Assert.Equal("tally.cs", ranking.Base?.Path);
        // History stays bounded: the far closer synapse still leads the examples.
        Assert.Equal(["count.cs", "tally.cs"], ranking.Examples.Select(example => example.Path));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ACrashedSynapseIsNeverTheForkBaseOverAProvenOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, new FakeAppRuntime(), new FastPathSandbox(), ct);
        var pantry = await Created(brain, "pantry");
        await pantry.WriteBehavior("count.cs", Synapse("Counts the lemons", "CRASHED-LEMONS"), Guid.NewGuid());
        await pantry.WriteBehavior("tally.cs", Synapse("Tallies the lemons", "PROVEN-LEMONS"), Guid.NewGuid());
        // The closer one dies before it subscribes, so no run is ever counted against it; the sandbox reports it down.
        await Script(brain, Down("count.cs", "boom"), Ran("tally.cs", runs: 40, failures: 0));
        var similarity = new ScriptedSimilarity("tally citrus", ("Counts the lemons", 0.80), ("Tallies the lemons", 0.70));

        var ranking = await Examples(brain, similarity).Rank(Brain, "tally citrus", ct);

        Assert.Equal("tally.cs", ranking.Base?.Path);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task OneFailedRunDoesNotCostAFarCloserSynapseTheBase()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, new FakeAppRuntime(), new FastPathSandbox(), ct);
        var pantry = await Created(brain, "pantry");
        await pantry.WriteBehavior("count.cs", Synapse("Counts the lemons", "UNLUCKY-LEMONS"), Guid.NewGuid());
        await pantry.WriteBehavior("tally.cs", Synapse("Tallies the lemons", "NEW-LEMONS"), Guid.NewGuid());
        // One failed run is no record: the far closer synapse stays the base over one that never ran.
        await Script(brain, Ran("count.cs", runs: 1, failures: 1), Ran("tally.cs", runs: 0, failures: 0));
        var similarity = new ScriptedSimilarity("tally citrus", ("Counts the lemons", 0.90), ("Tallies the lemons", 0.65));

        var ranking = await Examples(brain, similarity).Rank(Brain, "tally citrus", ct);

        Assert.Equal("count.cs", ranking.Base?.Path);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AFocusPastItsCapIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, new FakeAppRuntime(), new FastPathSandbox(), ct);
        var app = await Created(brain, "grocer");
        string[] crowded = [.. Enumerable.Range(0, AppNeuron.MaxFocus + 1).Select(index => IApp.Key(Brain, $"pantry-{index}"))];

        await Assert.ThrowsAsync<ArgumentException>(() => app.WriteSynapse("when the pantry changes, answer lemons", crowded, Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            app.WriteSynapse("when the pantry changes, answer lemons", [new string('k', AppNeuron.MaxFocusKeyLength + 1)], Guid.NewGuid()));

        Assert.Empty((await app.Read()).Synapses!);
        Assert.Empty(model.Requests(Writer));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task FocusContractsAreInThePrompt()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FastPathSandbox { Contracts = new([new("apps")], ["FOCUS-CONTRACT the app neuron"], "") };
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, new FakeAppRuntime(), sandbox, ct);
        model.Script(Writer, Reply("lemons.cs", Lemons));
        await Created(brain, "pantry");
        var app = await Created(brain, "grocer");
        await using var written = await brain.Observe<SynapseWritten>(app, ct);

        await app.WriteSynapse("when the pantry changes, answer lemons", [IApp.Key(Brain, "pantry")], Guid.NewGuid());

        await written.NextAsync(ct: ct);
        var prompt = Prompt(model);
        Assert.Contains(sandbox.Read, modules => modules.SequenceEqual(["apps"]));
        Assert.Contains("FOCUS-CONTRACT the app neuron", prompt, StringComparison.Ordinal);
        Assert.Contains($"{IApp.Key(Brain, "pantry")} ({typeof(IApp).FullName})", prompt, StringComparison.Ordinal);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task FocusReadsCompleteDetailInsteadOfClippingALargeCatalog()
    {
        var ct = TestContext.Current.CancellationToken;
        // A reading far past any prompt budget, cut short as the sandbox cuts a large one, with the focus contract absent.
        string[] bulk = [.. Enumerable.Range(0, 400).Select(index => $"BULK-CONTRACT-{index:D3} " + new string('x', 400))];
        var complete = "FULL-REQUEST " + new string('x', 4100) + " RequiredTailField { get; init; }";
        var sandbox = new FastPathSandbox { Contracts = new([new("apps")], bulk, "", Truncated: true), Detail = new([], [complete], "") };
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, new FakeAppRuntime(), sandbox, ct);
        model.Script(Writer, Reply("lemons.cs", Lemons));
        await Created(brain, "pantry");
        var app = await Created(brain, "grocer");
        await using var written = await brain.Observe<SynapseWritten>(app, ct);

        await app.WriteSynapse("when the pantry changes, answer lemons", [IApp.Key(Brain, "pantry")], Guid.NewGuid());

        await written.NextAsync(ct: ct);
        var prompt = Prompt(model);
        Assert.Contains(complete, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(bulk[0], prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(bulk[^1], prompt, StringComparison.Ordinal);
        Assert.True(prompt.Length < bulk.Sum(line => line.Length) / 3, $"The prompt is {prompt.Length} characters.");
    }

    [Trait("host", "own")]
    [Theory]
    [InlineData(12001, false, false)]
    [InlineData(200, true, false)]
    [InlineData(61, false, true)]
    public async Task IncompleteOrOversizedDetailFailsBeforeTheModelSeesAPartialContract(int length, bool truncated, bool multiline)
    {
        var ct = TestContext.Current.CancellationToken;
        var schema = multiline ? string.Join('\n', Enumerable.Repeat("field", length)) : new string('x', length);
        var sandbox = new FastPathSandbox { Detail = new([], [schema], "", truncated) };
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, new FakeAppRuntime(), sandbox, ct);
        await Created(brain, "pantry");
        var app = await Created(brain, "grocer");
        await using var refused = await brain.Observe<SynapseRejected>(app, ct);
        await app.WriteSynapse("when the pantry changes, answer lemons", [IApp.Key(Brain, "pantry")], Guid.NewGuid());
        await refused.NextAsync(ct: ct);
        Assert.Empty(model.Requests(Writer));
        Assert.Empty(sandbox.Compiled);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task TheSynapseRouteAnswersAtOnceAndTheSynapseGoesLiveForItsOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, new FakeAppRuntime(), new FastPathSandbox(), ct);
        model.Script(Writer, Reply("lemons.cs", Lemons));
        var alice = await brain.PersonAsync("alice", ct);
        var app = brain.Get<IApp>(IApp.Key(alice.BrainId, "grocer"));
        using (alice.AsCaller()) { await app.Create(new("Grocer", "groceries", [new("ask", "ask it")], [], []), "spec"); }
        await using var written = await brain.Observe<SynapseWritten>(app, ct);

        using var accepted = await alice.Http.PostAsJsonAsync($"/brains/{alice.BrainId}/apps/grocer/synapse",
            new { sentence = "when asked, answer lemons", focus = Array.Empty<string>() }, ct);

        var body = await accepted.Content.ReadAsStringAsync(ct);
        Assert.True(accepted.StatusCode == HttpStatusCode.Accepted, $"{(int)accepted.StatusCode}: {body}");
        Assert.Equal("Requested", JsonDocument.Parse(body).RootElement.GetProperty("synapses")[0].GetProperty("status").GetString());
        Assert.Equal("lemons.cs", (await written.NextAsync(ct: ct)).Path);
        using (alice.AsCaller()) { Assert.Equal(SynapseStatus.Written, Assert.Single((await app.Read()).Synapses!).Status); }
    }

    [Trait("host", "own")]
    [Fact]
    public async Task NoEdgeCallerRecordsAFastPathOutcome()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, new FakeAppRuntime(), new FastPathSandbox(), ct);
        var alice = await brain.PersonAsync("alice", ct);
        var app = brain.Get<IApp>(IApp.Key(alice.BrainId, "grocer"));
        using var caller = alice.AsCaller();
        await app.Create(new("Grocer", "groceries", [new("ask", "ask it")], [], []), "spec");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => app.CompleteSynapse(Guid.NewGuid(), [], 1, 1));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ThreeSentencesWrittenToOneAppAtOnceAllEndWrittenAndRun()
    {
        var ct = TestContext.Current.CancellationToken;
        // Every deploy outlasts a call's wait, as a busy Assign does live: each writer's deploy lands only once one
        // of its calls has timed out, however loaded the test host is, so every first call times out mid-deploy.
        var runtime = new SlowAppRuntime(TimeSpan.Zero) { LandsAfterATimeout = true };
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartSlow(model, runtime, ct);
        model.Script(Writer, Reply("apples.cs", Lemons), Reply("pears.cs", Lemons), Reply("plums.cs", Lemons));
        var app = await Created(brain, "grocer-" + Guid.NewGuid().ToString("N"));
        await using var outcomes = await brain.Observe<Signal>(app, ct);

        await Task.WhenAll(
            app.WriteSynapse("when asked, answer apples", [], Guid.NewGuid()),
            app.WriteSynapse("when asked, answer pears", [], Guid.NewGuid()),
            app.WriteSynapse("when asked, answer plums", [], Guid.NewGuid()));

        for (var landed = 0; landed < 3; landed++)
        { await outcomes.NextAsync(signal => signal is SynapseWritten or SynapseRejected, ct); }
        var live = await app.Read();
        Assert.Equal(3, live.Synapses!.Length);
        // Every deploy here lands, so none may be rejected for having waited on another, and each written file is
        // both in the app and in what the runtime runs.
        var program = runtime.Current(app.GetPrimaryKeyString()) ?? [];
        Assert.All(live.Synapses, synapse => Assert.Equal(SynapseStatus.Written, synapse.Status));
        Assert.All(live.Synapses, synapse => Assert.True(live.Files.ContainsKey(synapse.Path!) && program.ContainsKey(synapse.Path!)));
        Assert.True(runtime.TimedOutCalls >= 3, $"{runtime.TimedOutCalls} deploy calls timed out");
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ADeploySlowerThanTheResponseTimeoutStillEndsWrittenWithTheFilePresent()
    {
        var ct = TestContext.Current.CancellationToken;
        // CallTimeout stands in for the Orleans response timeout and runs on this fact's clock: the fact lets it elapse once
        // a call is waiting, and the deploy lands only after that call timed out, so no real time decides the order.
        var time = new FakeTimeProvider();
        var runtime = new SlowAppRuntime(TimeSpan.Zero) { LandsAfterATimeout = true, Time = time };
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartSlow(model, runtime, ct);
        model.Script(Writer, Reply("lemons.cs", Lemons));
        var app = await Created(brain, "grocer-" + Guid.NewGuid().ToString("N"));
        await using var written = await brain.Observe<SynapseWritten>(app, ct);

        var writing = app.WriteSynapse("when asked, answer lemons", [], Guid.NewGuid());
        await runtime.CallWaiting(ct);
        time.Advance(CallTimeout);
        await writing;

        var signal = await written.NextAsync(ct: ct);
        Assert.Equal(("lemons.cs", "hash:lemons.cs"), (signal.Path, signal.Hash));
        var live = await app.Read();
        var entry = Assert.Single(live.Synapses!);
        Assert.Equal((SynapseStatus.Written, "lemons.cs", "hash:lemons.cs"), (entry.Status, entry.Path, entry.Hash));
        Assert.Empty(entry.Errors);
        Assert.Equal(Lemons, live.Files["lemons.cs"]);
        Assert.Equal(Lemons, runtime.Current(app.GetPrimaryKeyString())?["lemons.cs"]);
        // The first call timed out and the repeat joined the deploy in flight: one deploy, not two.
        Assert.True(runtime.TimedOutCalls >= 1, $"{runtime.TimedOutCalls} deploy calls timed out");
        Assert.Equal(1, runtime.Deploys);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ADeployThatNeverAnswersEndsRejectedAndTheRuntimeHoldsNoProgramForIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new SlowAppRuntime(TimeSpan.Zero) { Never = sources => sources.ContainsKey("stuck.cs") };
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartSlow(model, runtime, ct);
        model.Script(Writer, Reply("stuck.cs", Lemons));
        var app = await Created(brain, "grocer-" + Guid.NewGuid().ToString("N"));
        await using var rejected = await brain.Observe<SynapseRejected>(app, ct);

        await app.WriteSynapse("when asked, answer lemons", [], Guid.NewGuid());

        var signal = await rejected.NextAsync(ct: ct);
        Assert.Contains(signal.Errors, error => error.Contains("did not answer", StringComparison.Ordinal));
        var live = await app.Read();
        var entry = Assert.Single(live.Synapses!);
        Assert.Equal(SynapseStatus.Rejected, entry.Status);
        Assert.False(live.Files.ContainsKey("stuck.cs"));
        Assert.Equal(SynapseWriter.MaxDeployCalls, runtime.TimedOutCalls);
        // The runtime was brought back to the app's files: whatever it holds for the app has no stuck.cs.
        Assert.False(runtime.Current(app.GetPrimaryKeyString())?.ContainsKey("stuck.cs") ?? false);
        Assert.Contains(app.GetPrimaryKeyString(), runtime.Retired);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ADeployThatNeverAnswersRevertsTheRuntimeToTheAppsPreviousProgram()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new SlowAppRuntime(TimeSpan.Zero);
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartSlow(model, runtime, ct);
        model.Script(Writer, Reply("stuck.cs", Lemons));
        var app = await Created(brain, "grocer-" + Guid.NewGuid().ToString("N"));
        await app.WriteBehavior("lemons.cs", Lemons, Guid.NewGuid());
        runtime.Never = sources => sources.ContainsKey("stuck.cs");
        await using var rejected = await brain.Observe<SynapseRejected>(app, ct);

        await app.WriteSynapse("when asked, answer lemons", [], Guid.NewGuid());

        await rejected.NextAsync(ct: ct);
        var entry = Assert.Single((await app.Read()).Synapses!);
        Assert.Equal(SynapseStatus.Rejected, entry.Status);
        // The program before the write landed again after the stuck one: the runtime runs lemons.cs alone.
        Assert.Equal(["lemons.cs"], runtime.Current(app.GetPrimaryKeyString())!.Keys);
        Assert.Equal(2, runtime.Landed.Count(program => program.Keys.SequenceEqual(["lemons.cs"])));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ADeployAndARevertThatBothNeverAnswerEndUnsettledAndKeepTheSource()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new SlowAppRuntime(TimeSpan.Zero);
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartSlow(model, runtime, ct);
        model.Script(Writer, Reply("stuck.cs", Lemons));
        var app = await Created(brain, "grocer-" + Guid.NewGuid().ToString("N"));
        await app.WriteBehavior("lemons.cs", Lemons, Guid.NewGuid());
        runtime.Never = _ => true;
        await using var rejected = await brain.Observe<SynapseRejected>(app, ct);

        await app.WriteSynapse("when asked, answer lemons", [], Guid.NewGuid());

        var signal = await rejected.NextAsync(ct: ct);
        Assert.StartsWith("Unsettled: runtime state unknown.", Assert.Single(signal.Errors), StringComparison.Ordinal);
        var live = await app.Read();
        var entry = Assert.Single(live.Synapses!);
        Assert.Equal((SynapseStatus.Unsettled, "stuck.cs", Lemons), (entry.Status, entry.Path, entry.Source));
        Assert.False(live.Files.ContainsKey("stuck.cs"));
        Assert.Equal(2 * SynapseWriter.MaxDeployCalls, runtime.TimedOutCalls);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AStagedSynapseIsNotInTheAppsFilesWhileItsDeployRuns()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new SlowAppRuntime(TimeSpan.Zero);
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartSlow(model, runtime, ct);
        model.Script(Writer, Reply("lemons.cs", Lemons));
        var app = await Created(brain, "grocer-" + Guid.NewGuid().ToString("N"));
        var seen = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Landing = async key => seen.TrySetResult([.. (await brain.Get<IApp>(key).Read()).Files.Keys]);
        await using var written = await brain.Observe<SynapseWritten>(app, ct);

        await app.WriteSynapse("when asked, answer lemons", [], Guid.NewGuid());

        Assert.DoesNotContain("lemons.cs", await seen.Task.WaitAsync(ct));
        await written.NextAsync(ct: ct);
        Assert.Equal(Lemons, (await app.Read()).Files["lemons.cs"]);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AFastPathDeployRunsAsTheAppsPrincipal()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new SlowAppRuntime(TimeSpan.Zero);
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartSlow(model, runtime, ct);
        model.Script(Writer, Reply("lemons.cs", Lemons));
        var alice = await brain.PersonAsync("alice", ct);
        var app = brain.Get<IApp>(IApp.Key(alice.BrainId, "grocer"));
        using (alice.AsCaller()) { await app.Create(new("Grocer", "groceries", [new("ask", "ask it")], [], []), "spec"); }
        await using var written = await brain.Observe<SynapseWritten>(app, ct);

        // Asked with no caller at all: the deploy still runs as the app's creator.
        await app.WriteSynapse("when asked, answer lemons", [], Guid.NewGuid());

        await written.NextAsync(ct: ct);
        var caller = Assert.Single(runtime.Callers);
        Assert.Equal((alice.PrincipalId, CallerKind.User), (caller?.PrincipalId, caller?.Kind));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ADeployThatCompletesAfterItsRequestTimedOutIsRecordedWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        var compiling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sandbox = new FastPathSandbox { Hold = (compiling, release.Task) };
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, new FakeAppRuntime(), sandbox, ct);
        model.Script(Writer, Reply("lemons.cs", Lemons));
        var app = await Created(brain, "grocer");
        var operation = Guid.NewGuid();
        await app.WriteSynapse("when asked, answer lemons", [], operation);
        await compiling.Task.WaitAsync(ct);

        // The deploy lands, then the writer's report that it timed out waiting for it arrives.
        await app.WriteBehavior("lemons.cs", Lemons, operation);
        await app.CompleteSynapse(operation, ["The deploy did not answer in time: timed out"], 1, 1);
        release.SetResult();

        var entry = Assert.Single((await app.Read()).Synapses!);
        Assert.Equal((SynapseStatus.Written, "lemons.cs", "hash:lemons.cs"), (entry.Status, entry.Path, entry.Hash));
        Assert.Empty(entry.Errors);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AWriteWhoseJobThrowsIsRecordedRejectedWithTheReason()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FastPathSandbox { Result = _ => throw new NotSupportedException("the compiler fell over") };
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, new FakeAppRuntime(), sandbox, ct);
        model.Script(Writer, Reply("lemons.cs", Lemons));
        var app = await Created(brain, "grocer");
        await using var rejected = await brain.Observe<SynapseRejected>(app, ct);

        await app.WriteSynapse("when asked, answer lemons", [], Guid.NewGuid());

        var signal = await rejected.NextAsync(ct: ct);
        Assert.Contains(signal.Errors, error => error.Contains("the compiler fell over", StringComparison.Ordinal));
        var entry = Assert.Single((await app.Read()).Synapses!);
        Assert.Equal(SynapseStatus.Rejected, entry.Status);
        Assert.Contains(entry.Errors, error => error.Contains("NotSupportedException: the compiler fell over", StringComparison.Ordinal));
    }

    private static Task<ITestBrain> Start(FakeModelServer model, FakeAppRuntime runtime, FastPathSandbox sandbox, CancellationToken ct,
        string? features = null, Action<IServiceCollection>? configure = null)
        => TestBrain.Create()
            .WithModule<AIModule>()
            .WithModule<AppsModule, AppsOptions>(apps =>
            {
                apps.FastPathModel = nameof(IGpt56Luna);
                apps.Features = features;
            })
            .WithFakeModel(model)
            .WithFakeAppRuntime(runtime)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IScriptSandbox>(sandbox);
                configure?.Invoke(services);
            })
            .StartAsync(ct);

    private sealed class SyntaxBoundaryLog(ILogger<SynapseExamples> inner, Func<string?> phase,
        Action<long, string, string, string, double, long> record) : ILogger<SynapseExamples>
    {
        private readonly ConcurrentDictionary<long, string> _phases = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Debug || inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Debug && eventId.Id == 0 && eventId.Name == "SynapseSyntaxBoundary"
                && state is IReadOnlyList<KeyValuePair<string, object?>> fields)
            {
                object? Field(string name) => fields.FirstOrDefault(field => field.Key == name).Value;
                if (Field("Invocation") is long invocation && Field("Stage") is string stage && Field("State") is string boundaryState
                    && Field("ElapsedMs") is double durationMs && Field("AllocatedBytes") is long allocatedBytes
                    && stage is "catalog-call" or "shipped-add" or "layouts" or "shared-filter"
                    && boundaryState is "entered" or "completed" or "stopped")
                {
                    var capturedPhase = _phases.GetOrAdd(invocation, _ => phase() ?? "outside");
                    record(invocation, stage, boundaryState, capturedPhase, durationMs, allocatedBytes);
                }
            }
            inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }

    // Only this fact decorates its host's IKernel; concrete Kernel and IKernelView stay registered as before.
    private sealed class TimedKernel(Kernel inner, Func<string?> phase, Action<long, string, string, string> record) : IKernel
    {
        private long _next;

        public IAsyncEnumerable<Instance> InstancesIn(string brain, string? type, CancellationToken cancellationToken = default)
        {
            var capturedPhase = phase();
            var values = inner.InstancesIn(brain, type, cancellationToken);
            return capturedPhase is null ? values : ObserveInstances(values, capturedPhase, cancellationToken);
        }

        private async IAsyncEnumerable<Instance> ObserveInstances(IAsyncEnumerable<Instance> values, string capturedPhase,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var id = Interlocked.Increment(ref _next);
            record(id, "InstancesIn", capturedPhase, "entered");
            var completed = false;
            try
            {
                await foreach (var value in values.WithCancellation(cancellationToken).ConfigureAwait(false)) { yield return value; }
                completed = true;
            }
            finally { record(id, "InstancesIn", capturedPhase, completed ? "completed" : "stopped"); }
        }

        public Task<TypeEntry[]> Types(CancellationToken cancellationToken = default)
        {
            var capturedPhase = phase();
            if (capturedPhase is null) { return inner.Types(cancellationToken); }
            var id = Interlocked.Increment(ref _next);
            record(id, "Types", capturedPhase, "entered");
            Task<TypeEntry[]> pending;
            try { pending = inner.Types(cancellationToken); }
            catch { record(id, "Types", capturedPhase, "stopped"); throw; }
            return ObserveTypes(pending, id, capturedPhase);
        }

        private async Task<TypeEntry[]> ObserveTypes(Task<TypeEntry[]> pending, long id, string capturedPhase)
        {
            var completed = false;
            try
            {
                var values = await pending.ConfigureAwait(false);
                completed = true;
                return values;
            }
            finally { record(id, "Types", capturedPhase, completed ? "completed" : "stopped"); }
        }

        public IAsyncEnumerable<Instance> Instances<T>(CancellationToken cancellationToken = default) where T : INeuron
            => inner.Instances<T>(cancellationToken);
        public IAsyncEnumerable<Instance> Instances(string? type, CancellationToken cancellationToken = default) => inner.Instances(type, cancellationToken);
        public Task<string[]> Brains(CancellationToken cancellationToken = default) => inner.Brains(cancellationToken);
        public Task<Instance[]> Running(CancellationToken cancellationToken = default) => inner.Running(cancellationToken);
        public string TypeOf(Type contract) => inner.TypeOf(contract);
        public Task Retire(string type, string key, CancellationToken cancellationToken = default) => inner.Retire(type, key, cancellationToken);
        public Task<Feature[]> Features(string brain, CancellationToken cancellationToken = default) => inner.Features(brain, cancellationToken);
        public Task LoadFeature(string brain, Feature feature, TypeEntry[] types, CancellationToken cancellationToken = default)
            => inner.LoadFeature(brain, feature, types, cancellationToken);
        public Task UnloadFeature(string brain, string name, CancellationToken cancellationToken = default) => inner.UnloadFeature(brain, name, cancellationToken);
    }

    private sealed class TimedGrainReads(Func<string?> phase, Action<long, string, string, string> record) : IOutgoingGrainCallFilter
    {
        private long _next;

        public Task Invoke(IOutgoingGrainCallContext context)
        {
            var capturedPhase = phase();
            if (capturedPhase is null) { return context.Invoke(); }
            var method = context.InterfaceMethod;
            var label = method?.DeclaringType == typeof(IApp) && method.Name == nameof(IApp.Read) ? "App.Read"
                : method?.DeclaringType == typeof(IBrainRuntime) && method.Name == nameof(IBrainRuntime.Inspect) ? "Runtime.Inspect" : null;
            if (label is null) { return context.Invoke(); }
            var id = Interlocked.Increment(ref _next);
            record(id, label, capturedPhase, "entered");
            Task pending;
            try { pending = context.Invoke(); }
            catch { record(id, label, capturedPhase, "stopped"); throw; }
            return Observe(pending, id, label, capturedPhase);
        }

        private async Task Observe(Task pending, long id, string label, string capturedPhase)
        {
            var completed = false;
            try
            {
                await pending.ConfigureAwait(false);
                completed = true;
            }
            finally { record(id, label, capturedPhase, completed ? "completed" : "stopped"); }
        }
    }

    private const int SynapseWriterCalls = 4;
    private static readonly TimeSpan CallTimeout = TimeSpan.FromMilliseconds(250);

    private static Task<ITestBrain> StartSlow(FakeModelServer model, SlowAppRuntime runtime, CancellationToken ct)
        => TestBrain.Create()
            .WithModule<AIModule>()
            .WithModule<AppsModule, AppsOptions>(apps => apps.FastPathModel = nameof(IGpt56Luna))
            .WithFakeModel(model)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IAppRuntime>();
                services.AddSingleton<IAppRuntime>(runtime);
                services.AddSingleton<IScriptSandbox>(new FastPathSandbox());
            })
            .StartAsync(ct);

    // The IAppRuntime contract with a slow Assign behind a call timeout, as the brain runtime is live: a call
    // waits at most CallTimeout and then throws TimeoutException while the deploy goes on; the same program
    // asked again while it is in flight joins it. A program Never matches is a deploy that never lands.
    // (The brain runtime's own join and ticket order are facts of the CSharp module.)
    private sealed class SlowAppRuntime(TimeSpan delay) : IAppRuntime
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, Task<IReadOnlyDictionary<string, string>>> _inFlight = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, string>> _current = new(StringComparer.Ordinal);
        private int _deploys;
        private int _timedOut;
        private readonly SemaphoreSlim _waiting = new(0);

        // What CallTimeout is measured on; a fact that must decide when a call times out supplies a FakeTimeProvider.
        public TimeProvider Time { get; init; } = TimeProvider.System;
        // Completes once a call is waiting on its deploy with the timeout already armed.
        public async Task CallWaiting(CancellationToken cancellationToken) => Assert.True(await _waiting.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken), "No deploy call is waiting.");

        public Func<Dictionary<string, string>, bool> Never { get; set; } = _ => false;
        // A deploy lands right after a call waiting on it times out, instead of after the delay, and the next call
        // for the same program answers at once.
        public bool LandsAfterATimeout { get; init; }
        private readonly Dictionary<string, TaskCompletionSource> _callTimedOut = new(StringComparer.Ordinal);
        // Runs as a deploy starts landing, before it lands: what the app shows while its deploy runs.
        public Func<string, Task>? Landing { get; set; }
        public ConcurrentQueue<string> Retired { get; } = new();
        public ConcurrentQueue<Dictionary<string, string>> Landed { get; } = new();
        public ConcurrentQueue<CallerContext?> Callers { get; } = new();
        public int Deploys => Volatile.Read(ref _deploys);
        public int TimedOutCalls => Volatile.Read(ref _timedOut);

        public Dictionary<string, string>? Current(string appKey) { lock (_gate) { return _current.GetValueOrDefault(appKey); } }

        public async Task<IReadOnlyDictionary<string, string>> Deploy(string appKey, Dictionary<string, string> sources, Dictionary<string, string> configuration, CancellationToken cancellationToken)
        {
            Callers.Enqueue(CallerContextStamper.TryGet(out var caller) ? caller : null);
            var key = appKey + "\n" + string.Join("\n", sources.OrderBy(file => file.Key, StringComparer.Ordinal).Select(file => file.Key + "=" + file.Value));
            Task<IReadOnlyDictionary<string, string>> deploy;
            lock (_gate)
            {
                if (!_inFlight.TryGetValue(key, out deploy!))
                {
                    deploy = Land(appKey, key, new(sources, StringComparer.Ordinal));
                    _inFlight[key] = deploy;
                }
            }
            try
            {
                using var armed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var timeout = Task.Delay(CallTimeout, Time, armed.Token);
                _waiting.Release();
                if (await Task.WhenAny(deploy, timeout) == timeout) { await timeout; throw new TimeoutException(); }
                await armed.CancelAsync();
                return await deploy;
            }
            catch (TimeoutException)
            {
                Interlocked.Increment(ref _timedOut);
                lock (_gate) { TimedOut(key).TrySetResult(); }
                throw new TimeoutException($"Response did not arrive on time in {CallTimeout} for Assign.");
            }
        }

        private async Task<IReadOnlyDictionary<string, string>> Land(string appKey, string key, Dictionary<string, string> sources)
        {
            await Task.Yield();
            try
            {
                Interlocked.Increment(ref _deploys);
                if (Landing is { } landing) { await landing(appKey); }
                if (Never(sources)) { await Task.Delay(Timeout.Infinite); }
                if (LandsAfterATimeout) { Task timedOut; lock (_gate) { timedOut = TimedOut(key).Task; } await timedOut; }
                await Task.Delay(delay);
                lock (_gate) { _current[appKey] = sources; }
                Landed.Enqueue(sources);
                return sources.Keys.ToDictionary(path => path, path => "hash:" + path, StringComparer.Ordinal);
            }
            // A deploy that lands after a timed-out call stays answerable, so the writer's next call reads it.
            finally { if (!LandsAfterATimeout) { lock (_gate) { _inFlight.Remove(key); } } }
        }

        private TaskCompletionSource TimedOut(string key)
        {
            if (!_callTimedOut.TryGetValue(key, out var timedOut))
            { _callTimedOut[key] = timedOut = new(TaskCreationOptions.RunContinuationsAsynchronously); }
            return timedOut;
        }

        public Task Retire(string appKey, CancellationToken cancellationToken)
        {
            lock (_gate) { _current.Remove(appKey); }
            Retired.Enqueue(appKey);
            return Task.CompletedTask;
        }
    }

    private static async Task<IApp> Created(ITestBrain brain, string name)
    {
        var app = brain.Get<IApp>(IApp.Key(Brain, name));
        await app.Create(new(name, name + " things", [new("ask", "ask it")], [], []), "spec");
        return app;
    }

    private void WriteFeature(string owner, string folder, string name, string path, string source)
    {
        var directory = Path.Combine(_features, owner, folder);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, AppFiles.DeclarationPath), Declarations.Of(name, "1.0.0", folder, "A feature.", [new("ask", "ask it")]));
        File.WriteAllText(Path.Combine(directory, path), source);
    }

    private static string Synapse(string spec, string marker) => $$"""
        // {{spec}}
        await using var brain = await IntoChatOSClient.ConnectAsync(args);
        var app = brain.Get<IntoChat.OS.Apps.IApp>(brain.Setting("App")!);
        await foreach (var request in brain.On(app, "ask", brain.Stopping))
        { Console.WriteLine("{{marker}}"); }
        """;

    // The fast path's retrieval over the brain's own grains, with the embedding model and cache size the fact picks.
    private static SynapseExamples Examples(ITestBrain brain, IEmbeddingGenerator<string, Embedding<float>>? embeddings,
        int vectorCapacity = SynapseExamples.DefaultVectorCapacity)
        => new(brain.Services.GetRequiredService<IGrainFactory>(), brain.Services.GetRequiredService<IKernel>(),
            brain.Services.GetRequiredService<FeatureCatalog>(), embeddings, brain.Services.GetRequiredService<ILogger<SynapseExamples>>(), vectorCapacity);

    private static Task Script(ITestBrain brain, params BehaviorView[] behaviors)
        => brain.Get<IBrainRuntime>(BrainRuntimeKeys.For(Brain)).AsReference<IScriptedRuntime>().Script(behaviors);

    // A pantry synapse the runtime has deployed and keeps running, with its run record.
    private static BehaviorView Ran(string path, long runs, long failures)
        => new(BehaviorIds.For(IApp.Key(Brain, "pantry"), path), "hash:" + path, "Running", null, [], runs, failures);

    // A pantry synapse the sandbox reports exited with an error, with no run recorded.
    private static BehaviorView Down(string path, string error)
        => new(BehaviorIds.For(IApp.Key(Brain, "pantry"), path), "hash:" + path, "Exited", error, []);

    private static FakeReply Reply(string path, string source) => JsonSerializer.Serialize(new { path, source });

    // Every message the writer sent in its index-th model call, as one text.
    private static string Prompt(FakeModelServer model, int index = 0)
        => string.Join("\n", model.Requests(Writer)[index].GetProperty("messages").EnumerateArray().Select(message =>
        {
            var content = message.GetProperty("content");
            return content.ValueKind == JsonValueKind.String ? content.GetString()
                : string.Concat(content.EnumerateArray().Where(part => part.TryGetProperty("text", out _)).Select(part => part.GetProperty("text").GetString()));
        }));

    // Every text but the query sits at one cosine from it: the query is [1, 0], the rest [s, √(1 − s²)].
    private sealed class FixedSimilarity(string query, double similarity) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(text => new Embedding<float>(text == query
                ? new float[] { 1, 0 } : [(float)similarity, (float)Math.Sqrt(1 - similarity * similarity)]))));
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    // The query is [1, 0]; a text holding one of the phrases sits at that phrase's cosine from it, any other at 0.
    private sealed class ScriptedSimilarity(string query, params (string Phrase, double Similarity)[] phrases) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(text => new Embedding<float>(text == query
                ? [1, 0] : At(phrases.FirstOrDefault(phrase => text.Contains(phrase.Phrase, StringComparison.Ordinal)).Similarity)))));
        private static float[] At(double similarity) => [(float)similarity, (float)Math.Sqrt(1 - similarity * similarity)];
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class FastPathSandbox : IScriptSandbox
    {
        public Func<IReadOnlyDictionary<string, string>, ScriptCompilationCheck> Result { get; init; } = _ => new(true, []);
        public ScriptContractCatalog Contracts { get; init; } = new([], [], "");
        public ScriptContractCatalog? Detail { get; init; }
        public ConcurrentQueue<IReadOnlyDictionary<string, string>> Compiled { get; } = new();
        public ConcurrentQueue<string[]> Read { get; } = new();
        public bool CanRun => true;
        public string AuthoringDescription => "csharp: one synapse per file.";

        public Task<ScriptContractCatalog> ReadContractDetail(string moduleId, string contractName, string? methodName, CancellationToken cancellationToken)
        {
            Read.Enqueue([moduleId]);
            return Task.FromResult(Detail ?? Contracts);
        }

        public Task<ScriptContractCatalog> ReadContracts(IReadOnlyList<string> modules, CancellationToken cancellationToken)
        {
            Read.Enqueue([.. modules]);
            return Task.FromResult(Contracts);
        }

        // Holds the first compile until the fact releases it, signalling once it is reached.
        public (TaskCompletionSource Reached, Task Released)? Hold { get; init; }

        public async Task<ScriptCompilationCheck> Compile(IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken)
        {
            Compiled.Enqueue(new Dictionary<string, string>(files, StringComparer.Ordinal));
            if (Hold is { } hold) { hold.Reached.TrySetResult(); await hold.Released; }
            return Result(files);
        }

        public Task<Verdict[]> Judge(string brain, IReadOnlyDictionary<string, string> bundle, Signal signal, CancellationToken cancellationToken)
            => Task.FromResult<Verdict[]>([]);
    }
}
