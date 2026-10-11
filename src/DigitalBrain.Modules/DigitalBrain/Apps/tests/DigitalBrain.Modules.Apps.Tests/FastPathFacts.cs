using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain.AI;
using DigitalBrain.AI.Models;
using DigitalBrain.AI.Models.OpenAI;
using DigitalBrain.AI.Models.Signals;
using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp.Runtime;
using DigitalBrain.Modules.Apps.Tests.Runtimes;
using DigitalBrain.Testing;
using DigitalBrain.Testing.Model;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Modules.Apps.Tests;

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
        using DigitalBrain.Apps;
        using DigitalBrain.Flutter.Text;

        // Answers lemons on every request.
        await using var brain = await DigitalBrainClient.ConnectAsync(args);
        var app = brain.Get<IApp>(brain.Setting("App")!);
        await foreach (var request in brain.On(app, "ask", brain.Stopping))
        { await brain.Get<IText>(app.GetPrimaryKeyString() + "/answer").Set("lemons"); }
        """;

    private const string TwoReactions = """
        await using var brain = await DigitalBrainClient.ConnectAsync(args);
        var app = brain.Get<DigitalBrain.Apps.IApp>(brain.Setting("App")!);
        await foreach (var open in brain.On(app, "ask", brain.Stopping)) { }
        await foreach (var close in brain.On(app, "ask", brain.Stopping)) { }
        """;

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

    [Fact]
    public async Task FocusContractsLeadHoweverLargeTheirModulesAre()
    {
        var ct = TestContext.Current.CancellationToken;
        // A reading far past any prompt budget, cut short as the sandbox cuts a large one, with the focus contract absent.
        string[] bulk = [.. Enumerable.Range(0, 400).Select(index => $"BULK-CONTRACT-{index:D3} " + new string('x', 400))];
        var sandbox = new FastPathSandbox { Contracts = new([new("apps")], bulk, "", Truncated: true) };
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, new FakeAppRuntime(), sandbox, ct);
        model.Script(Writer, Reply("lemons.cs", Lemons));
        await Created(brain, "pantry");
        var app = await Created(brain, "grocer");
        await using var written = await brain.Observe<SynapseWritten>(app, ct);

        await app.WriteSynapse("when the pantry changes, answer lemons", [IApp.Key(Brain, "pantry")], Guid.NewGuid());

        await written.NextAsync(ct: ct);
        var prompt = Prompt(model);
        Assert.Contains($"{typeof(IApp).FullName} [neuron id:", prompt, StringComparison.Ordinal);
        Assert.Contains(typeof(AppChanged).FullName!, prompt, StringComparison.Ordinal);
        // The rest of the module follows inside a budget: its first contracts, never all of them.
        Assert.Contains(bulk[0], prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(bulk[^1], prompt, StringComparison.Ordinal);
        Assert.True(prompt.Length < bulk.Sum(line => line.Length) / 3, $"The prompt is {prompt.Length} characters.");
    }

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

    [Fact]
    public async Task ThreeSentencesWrittenToOneAppAtOnceAllEndWrittenOrRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new FakeAppRuntime();
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await Start(model, runtime, new FastPathSandbox(), ct);
        model.Script(Writer, Reply("apples.cs", Lemons), Reply("pears.cs", Lemons), Reply("plums.cs", Lemons));
        var app = await Created(brain, "grocer");
        await using var written = await brain.Observe<SynapseWritten>(app, ct);

        await Task.WhenAll(
            app.WriteSynapse("when asked, answer apples", [], Guid.NewGuid()),
            app.WriteSynapse("when asked, answer pears", [], Guid.NewGuid()),
            app.WriteSynapse("when asked, answer plums", [], Guid.NewGuid()));

        string[] paths = [(await written.NextAsync(ct: ct)).Path, (await written.NextAsync(ct: ct)).Path, (await written.NextAsync(ct: ct)).Path];
        Assert.Equal(["apples.cs", "pears.cs", "plums.cs"], paths.Order(StringComparer.Ordinal));
        var live = await app.Read();
        Assert.Equal(3, live.Synapses!.Length);
        Assert.All(live.Synapses, synapse => Assert.Equal(SynapseStatus.Written, synapse.Status));
        Assert.All(live.Synapses, synapse => Assert.True(live.Files.ContainsKey(synapse.Path!)));
    }

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

    private static Task<ITestBrain> Start(FakeModelServer model, FakeAppRuntime runtime, FastPathSandbox sandbox, CancellationToken ct, string? features = null)
        => TestBrain.Create()
            .WithModule<AIModule>()
            .WithModule<AppsModule, AppsOptions>(apps =>
            {
                apps.FastPathModel = nameof(IGpt56Luna);
                apps.Features = features;
            })
            .WithFakeModel(model)
            .WithFakeAppRuntime(runtime)
            .ConfigureServices(services => services.AddSingleton<IScriptSandbox>(sandbox))
            .StartAsync(ct);

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
        await using var brain = await DigitalBrainClient.ConnectAsync(args);
        var app = brain.Get<DigitalBrain.Apps.IApp>(brain.Setting("App")!);
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
        public ConcurrentQueue<IReadOnlyDictionary<string, string>> Compiled { get; } = new();
        public ConcurrentQueue<string[]> Read { get; } = new();
        public bool CanRun => true;
        public string AuthoringDescription => "csharp: one synapse per file.";

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
