using System.Text.Json;
using DigitalBrain.Runtime.Synapses;
using DigitalBrain.Testing.Synapses;
using IntoChat.OS.Modules.AI.Inference;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScenarioTick = DigitalBrain.Testing.Synapses.Tick;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Authoring;

// The Creator's Author and Builder over a scripted model: the Author describes a unit, the Builder writes it with check_unit
// (the deploy's compile plus the authored scenarios on fake slots) until green.
public sealed class UnitAuthoringFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Authored = """
        {"alias": "x.watch", "title": "X watch",
         "spec": "Watches mentions of @elonmusk on X and counts the unseen ones.",
         "state": {"Unseen": "int", "Latest": "string", "LastId": "string"},
         "signals": [{"alias": "x.polled", "fields": {"Fresh": "int"}}],
         "needs": {"slots": [{"name": "x", "capability": "http", "hosts": ["api.x.com"]}], "schedule": "every 6m", "view": true},
         "scenarios": [{"name": "a poll counts the new posts",
                        "given": {"http": {"x": {"mentions": {"data": [{"id": "1", "text": "hi"}]}}}},
                        "when": ["tick 6m"],
                        "then": ["state x.watch Unseen = 1", "signal x.polled"]},
                       {"name": "seen clears the count",
                        "given": {"http": {"x": {"*": {"data": [{"id": "1", "text": "hi"}]}}}},
                        "when": ["tick 6m", "publish x.seen"],
                        "then": ["state x.watch Unseen = 0"]}]}
        """;

    // Answers each Generate with the next scripted turn; records every request it saw.
    private sealed class ScriptedModel(params Func<InferenceRequest, AiMessage>[] turns) : ICreatorModel
    {
        private readonly Queue<Func<InferenceRequest, AiMessage>> _turns = new(turns);

        public Queue<string> Completions { get; } = new();

        public void Reply(params string[] replies) { foreach (var reply in replies) { Completions.Enqueue(reply); } }

        public List<InferenceRequest> Requests { get; } = [];

        public List<string> Prompts { get; } = [];

        public Task<string> Complete(string brainId, string model, string system, string prompt, CancellationToken ct)
        {
            Prompts.Add(prompt);
            return Task.FromResult(Completions.TryDequeue(out var reply) ? reply : throw new InvalidOperationException("no scripted completion"));
        }

        public Task<InferenceResult> Generate(string brainId, string model, InferenceRequest request, CancellationToken ct)
        {
            Requests.Add(request with { Messages = [.. request.Messages] }); // the Creator keeps adding to its list
            var turn = _turns.TryDequeue(out var next) ? next : throw new InvalidOperationException("no scripted turn");
            return Task.FromResult(new InferenceResult([turn(request)], "stop", null, null, model));
        }
    }

    // Stands for the host's compiler, which a silo registers over its unit reference set.
    private static readonly SynapseCompiler HostCompiler = new(ReferenceSet.FromDefaultContext());

    private static readonly UnitNeeds XNeeds = new([new SlotNeed("x", "http", ["api.x.com"])], "every 6m", true);

    private static Creator Creator(ICreatorModel model, SynapseCompiler? compiler = null)
        => new(new ServiceCollection().BuildServiceProvider(), Options.Create(new AppsOptions()), NullLogger<Creator>.Instance,
            model: model, compiler: compiler ?? HostCompiler);

    private static AiMessage CheckUnit(string source)
        => new("assistant", [new AiToolCall("c1", BuilderTools.CheckUnit, JsonSerializer.Serialize(new { files = new Dictionary<string, string> { ["XWatch.cs"] = source } }))]);

    private static AiMessage Final(string source)
        => new("assistant", [new AiText(JsonSerializer.Serialize(new { files = new Dictionary<string, string> { ["XWatch.cs"] = source } }))]);

    private static string ToolResult(InferenceRequest request)
        => request.Messages[^1].Content.OfType<AiToolResult>().Single().ResultJson;

    private static async Task<AuthoredUnit> AuthorXWatch()
    {
        var model = new ScriptedModel();
        model.Completions.Enqueue(Authored);
        return await Creator(model).Author("b", "watch elonmusk on X", null, Ct);
    }

    [Fact]
    public async Task AuthorReturnsAUnitWithNeedsAndScenarios()
    {
        var model = new ScriptedModel();
        model.Reply("not json", "{\"alias\": \"X Watch\", \"title\": \"t\", \"spec\": \"s\", \"state\": {}, \"signals\": [], \"needs\": {\"slots\": [], \"view\": false}, \"scenarios\": []}", Authored);

        var unit = await Creator(model).Author("b", "watch elonmusk on X", null, Ct);

        Assert.Equal("x.watch", unit.Alias);
        Assert.Equal("int", unit.State["Unseen"]);
        Assert.Equal("x.polled", Assert.Single(unit.Signals).Alias);
        var slot = Assert.Single(unit.Needs.Slots);
        Assert.Equal(("x", "http", "api.x.com"), (slot.Name, slot.Capability, Assert.Single(slot.Hosts)));
        Assert.Equal("every 6m", unit.Needs.Schedule);
        Assert.True(unit.Needs.View);
        Assert.Equal(2, unit.Scenarios.Count);
        // Two unusable replies, two retries, each told why.
        Assert.Equal(3, model.Prompts.Count);
        Assert.Contains("not a valid unit alias", model.Prompts[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAuthorThatNeverAnswersAUnitFails()
    {
        var model = new ScriptedModel();
        model.Reply("no", "no", "no");
        await Assert.ThrowsAsync<InvalidDataException>(() => Creator(model).Author("b", "watch elonmusk", null, Ct));
    }

    [Fact]
    public async Task BuilderStopsWhenCheckUnitIsGreen()
    {
        var unit = await AuthorXWatch();
        var model = new ScriptedModel(_ => CheckUnit(AgentPrompts.XWatchExample), _ => Final(AgentPrompts.XWatchExample));

        var draft = await Creator(model).Draft("b", unit, null, Ct);

        Assert.Equal(2, model.Requests.Count);
        var check = JsonDocument.Parse(ToolResult(model.Requests[1])).RootElement;
        Assert.True(check.GetProperty("passed").GetBoolean(), check.GetRawText());
        Assert.Equal(AgentPrompts.XWatchExample, draft.Files["XWatch.cs"]);
        Assert.Equal(AgentPrompts.BuildUnit, model.Requests[0].Messages[0].Content.OfType<AiText>().Single().Text);
        Assert.Equal([BuilderTools.ReadContracts, BuilderTools.CheckUnit], model.Requests[0].Tools!.Select(tool => tool.Name));
    }

    [Fact]
    public async Task BuilderSeesScenarioFailuresAsErrors()
    {
        var unit = await AuthorXWatch();
        var wrong = AgentPrompts.XWatchExample.Replace("watch.Unseen + fresh", "watch.Unseen + fresh + 1", StringComparison.Ordinal);
        Assert.NotEqual(AgentPrompts.XWatchExample, wrong);
        // The wrong code as a final reply is sent back, not accepted; then the right code is.
        var model = new ScriptedModel(_ => CheckUnit(wrong), _ => Final(wrong), _ => Final(AgentPrompts.XWatchExample));

        var draft = await Creator(model).Draft("b", unit, null, Ct);

        var check = JsonDocument.Parse(ToolResult(model.Requests[1])).RootElement;
        Assert.False(check.GetProperty("passed").GetBoolean());
        Assert.Empty(check.GetProperty("diagnostics").EnumerateArray());
        Assert.Contains(check.GetProperty("failures").EnumerateArray(), f => f.GetString()!.Contains("ExpectState x.watch Unseen", StringComparison.Ordinal));
        var correction = model.Requests[2].Messages[^1].Content.OfType<AiText>().Single().Text;
        Assert.Contains("not green", correction, StringComparison.Ordinal);
        Assert.Contains("ExpectState", correction, StringComparison.Ordinal);
        Assert.Equal(AgentPrompts.XWatchExample, draft.Files["XWatch.cs"]);
    }

    [Fact]
    public async Task DraftIncludesSpecAndScenariosFiles()
    {
        var unit = await AuthorXWatch();
        var model = new ScriptedModel(_ => Final(AgentPrompts.XWatchExample));

        var draft = await Creator(model).Draft("b", unit, new Dictionary<string, string> { ["XWatch.cs"] = "// before" }, Ct);

        Assert.Equal(["XWatch.cs", UnitFiles.Scenarios, UnitFiles.Spec], draft.Files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(unit.Spec, draft.Files[UnitFiles.Spec]);
        var scenarios = JsonSerializer.Deserialize<Scenario[]>(draft.Files[UnitFiles.Scenarios], JsonSerializerOptions.Web)!;
        Assert.Equal(unit.Scenarios.Select(s => s.Name), scenarios.Select(s => s.Name));
        Assert.Null(draft.Manifest);
        Assert.Contains("// before", model.Requests[0].Messages[1].Content.OfType<AiText>().Single().Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ScenarioGrammarParses()
    {
        var scenario = new Scenario("all of it", JsonElement.Parse("""{"http": {"x": {"*": {"data": []}}}, "tools": {"mail": {"send": {"ok": true}}}}"""),
            ["tick 6m", "then state x.watch Unseen = 0", "publish x.seen {\"by\": \"me\"}", "tick 1h"],
            ["state x.watch Unseen = 0", "state x.watch Last.Count = \"two words\"", "signal x.polled", "view x.watch contains \"0 unseen\"", "never signal x.error", "failed", "failed Poll"]);

        var parsed = ScenarioSteps.Parse(scenario);

        Assert.Equal(["x", "mail"], parsed.Fakes.Slots);
        Assert.Collection(parsed.Steps,
            step => Assert.Equal(ScenarioSteps.Start.AddMinutes(6), Assert.IsType<ScenarioTick>(step).At),
            step =>
            {
                var state = Assert.IsType<ExpectState>(step);
                Assert.Equal(("x.watch", "Unseen", 0), (state.Alias, state.JsonPath, state.Expected.GetInt32()));
            },
            step =>
            {
                var publish = Assert.IsType<Publish>(step);
                Assert.Equal("x.seen", publish.SignalAlias);
                Assert.Equal("me", publish.Payload.GetProperty("by").GetString());
            },
            step => Assert.Equal(ScenarioSteps.Start.AddMinutes(66), Assert.IsType<ScenarioTick>(step).At),
            step =>
            {
                var state = Assert.IsType<ExpectState>(step);
                Assert.Equal(("x.watch", "Unseen", 0), (state.Alias, state.JsonPath, state.Expected.GetInt32()));
            },
            step => Assert.Equal("two words", Assert.IsType<ExpectState>(step).Expected.GetString()),
            step => Assert.Equal("x.polled", Assert.IsType<ExpectSignal>(step).Alias),
            step => Assert.Equal(new ExpectView("x.watch", "0 unseen"), step),
            step => Assert.Equal(new ExpectNoSignal("x.error"), step),
            step => Assert.Equal(new ExpectFailure(null), step),
            step => Assert.Equal(new ExpectFailure("Poll"), step));

        Assert.Equal("{}", Assert.IsType<Publish>(ScenarioSteps.Parse(new Scenario("n", default, ["publish x.seen"], [])).Steps[0]).Payload.GetRawText());
        foreach (var (when, then) in new[]
        {
            ("tick soon", ""), ("tick -1m", ""), ("poke x", ""), ("then", ""), ("then poke", ""),
            ("", "state x.watch = 1"), ("", "signal"), ("", "state x.watch Unseen = nope"),
            ("", "view x.watch has \"x\""), ("", "view x.watch contains unquoted"),
            ("", "never x.polled"), ("", "never signal"), ("", "failed two words"),
        })
        {
            Assert.Throws<FormatException>(() => ScenarioSteps.Parse(new Scenario("bad", default, when.Length > 0 ? [when] : [], then.Length > 0 ? [then] : [])));
        }
        foreach (var given in new[]
        {
            """{"smtp": {"x": {}}}""",
            """{"http": {"x": {"*": {"$each": []}}}}""",
            """{"http": {"x": {"*": {"$fail": 200}}}}""",
            """{"http": {"x": {"*": {"$fail": "slow"}}}}""",
        })
        {
            Assert.Throws<FormatException>(() => ScenarioSteps.Parse(new Scenario("bad", JsonElement.Parse(given), [], [])));
        }
        Assert.Equal(["x"], ScenarioSteps.Parse(new Scenario("t", JsonElement.Parse("""{"http": {"x": {"*": {"$each": [{"$fail": "timeout"}, {"$fail": 599}, {"data": []}]}}}}"""), [], [])).Fakes.Slots);
    }

    [Fact]
    public async Task CheckUnitReportsBoundaryViolations()
    {
        const string direct = """
            using System.Net.Http;

            namespace Units.Direct;

            [State("x.direct")]
            public sealed record Direct(int Count = 0)
            {
                public Direct() : this(0) { }
            }

            public static class Poller
            {
                [Synapse(typeof(Tick), "x.direct", Every = "every 6m")]
                public static async Task<Direct> Poll(Tick tick, Direct state, CancellationToken ct)
                {
                    using var http = new HttpClient();
                    var body = await http.GetStringAsync("https://api.x.com/2/users/elonmusk/mentions", ct);
                    return state with { Count = state.Count + body.Length };
                }
            }
            """;
        var check = await BuilderTools.Check(HostCompiler, "x.direct", new Dictionary<string, string> { ["Poller.cs"] = direct, [UnitFiles.Spec] = "# not C#" },
            new UnitNeeds([], "every 6m", false), [], Ct);

        Assert.False(check.Passed);
        Assert.Contains(check.Diagnostics, d => d.Id == "DB0045");
        Assert.Contains(check.Diagnostics, d => d.Message.Contains("HttpClient", StringComparison.Ordinal));
        Assert.Empty(check.Failures);
    }

    [Fact]
    public async Task CheckUnitFailsOnAScheduleMismatch()
    {
        var files = new Dictionary<string, string> { ["XWatch.cs"] = AgentPrompts.XWatchExample };

        var agreed = await BuilderTools.Check(HostCompiler, "x.watch", files, XNeeds, [], Ct);
        var other = await BuilderTools.Check(HostCompiler, "x.watch", files, XNeeds with { Schedule = "every 1h" }, [], Ct);
        var none = await BuilderTools.Check(HostCompiler, "x.watch", files, XNeeds with { Schedule = null }, [], Ct);
        var unscheduled = AgentPrompts.XWatchExample.Replace("[Synapse(typeof(Tick), \"x.watch\", Every = \"every 6m\")]", "[Synapse(typeof(Polled), \"x.watch\")]", StringComparison.Ordinal);
        Assert.NotEqual(AgentPrompts.XWatchExample, unscheduled);

        Assert.True(agreed.Passed, string.Join("; ", agreed.Failures));
        Assert.Equal("needs.schedule is 'every 1h' but the code says 'every 6m'", Assert.Single(other.Failures));
        Assert.Equal("needs.schedule is none but the code says 'every 6m'", Assert.Single(none.Failures));
        Assert.Equal("needs.schedule is 'every 6m' but the code says no schedule",
            BuilderTools.ScheduleMismatch("every 6m", new Dictionary<string, string> { ["XWatch.cs"] = unscheduled }));
    }

    [Fact]
    public async Task AnAuthorWithNoScenariosIsRetried()
    {
        var model = new ScriptedModel();
        var bare = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Authored)!;
        bare["scenarios"] = JsonElement.Parse("[]");
        model.Reply(JsonSerializer.Serialize(bare), Authored);

        var unit = await Creator(model).Author("b", "watch elonmusk on X", null, Ct);

        Assert.Equal(2, unit.Scenarios.Count);
        Assert.Equal(2, model.Prompts.Count);
        Assert.Contains("no scenarios", model.Prompts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckUnitIsUnavailableWithoutTheHostCompiler()
    {
        var unit = await AuthorXWatch();
        var model = new ScriptedModel(_ => CheckUnit(AgentPrompts.XWatchExample), _ => Final(AgentPrompts.XWatchExample));
        var creator = new Creator(new ServiceCollection().BuildServiceProvider(), Options.Create(new AppsOptions()),
            NullLogger<Creator>.Instance, model: model);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => creator.Draft("b", unit, null, Ct));

        Assert.Equal(SynapseStudioNeuron.Unavailable, error.Message);
        // The tool call already told the model; the final check is the build's end.
        Assert.Contains(SynapseStudioNeuron.Unavailable, ToolResult(model.Requests[1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTeachingExampleCountsAPostOnce()
    {
        var twoPolls = new Scenario("two polls of the same post count it once",
            JsonElement.Parse("""{"http": {"x": {"mentions": {"data": [{"id": "1", "text": "hi"}]}}}}"""),
            ["tick 6m", "tick 6m"], ["state x.watch Unseen = 1", "state x.watch LastId = \"1\""]);
        var check = await BuilderTools.Check(HostCompiler, "x.watch", new Dictionary<string, string> { ["XWatch.cs"] = AgentPrompts.XWatchExample }, XNeeds, [twoPolls], Ct);
        Assert.True(check.Passed, string.Join("; ", check.Failures));
    }

    [Fact]
    public void TheAuthorIsToldToRememberTheNewestItem()
    {
        Assert.Contains("\"LastId\": \"string\"", AgentPrompts.AuthorUnit, StringComparison.Ordinal);
        Assert.Contains("counts only newer ones", AgentPrompts.AuthorUnit, StringComparison.Ordinal);
        Assert.Contains("\"when\": [\"tick 6m\", \"tick 6m\"]", AgentPrompts.AuthorUnit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASupersededScenarioIsKeptAndNotRun()
    {
        var wrong = new Scenario("a poll counts two", JsonElement.Parse("""{"http": {"x": {"*": {"data": [{"id": "1", "text": "hi"}]}}}}"""),
            ["tick 6m"], ["state x.watch Unseen = 2"])
        { Superseded = "change x.watch: count once" };
        var right = new Scenario("a poll counts one", wrong.Given, ["tick 6m"], ["state x.watch Unseen = 1"]);
        var files = new Dictionary<string, string> { ["XWatch.cs"] = AgentPrompts.XWatchExample };

        var check = await BuilderTools.Check(HostCompiler, "x.watch", files, XNeeds, [wrong, right], Ct);
        Assert.True(check.Passed, string.Join("; ", check.Failures));

        files[UnitFiles.Scenarios] = JsonSerializer.Serialize(new[] { wrong, right }, JsonSerializerOptions.Web);
        var transcript = await IntoChat.OS.Modules.Apps.Debugger.ScenarioTranscripts.Run("x.watch", files, null, Ct);
        Assert.Contains("- a poll counts two: superseded", transcript, StringComparison.Ordinal);
        Assert.Contains("✓ a poll counts one", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("✗", transcript, StringComparison.Ordinal);
    }

    [Fact] public void TheRunnerRunsUpToThirtyTwoScenarios() => Assert.Equal(32, IntoChat.OS.Modules.Apps.Debugger.ScenarioTranscripts.MaxScenarios);

    [Fact]
    public async Task AUnitWhoseScenariosAreAllSupersededIsNotAUnit()
    {
        var model = new ScriptedModel();
        var bare = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Authored)!;
        bare["scenarios"] = JsonElement.Parse("""[{"name": "old", "given": {}, "when": ["tick 6m"], "then": ["signal x.polled"], "superseded": "change"}]""");
        model.Reply(JsonSerializer.Serialize(bare), Authored);
        await Creator(model).Author("b", "watch elonmusk on X", null, Ct);
        Assert.Contains("no scenarios", model.Prompts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAuthorOfAChangeIsGivenTheCurrentFiles()
    {
        var model = new ScriptedModel();
        model.Reply(Authored);
        var existing = new Dictionary<string, string> { ["XWatch.cs"] = "// code before", [UnitFiles.Scenarios] = """[{"name": "kept one", "given": {}, "when": ["tick 6m"], "then": ["signal x.polled"]}]""" };
        await Creator(model).Author("b", "change x.watch: red over ten", existing, Ct);
        Assert.Contains("Current files:", model.Prompts[0], StringComparison.Ordinal);
        Assert.Contains("kept one", model.Prompts[0], StringComparison.Ordinal);
        Assert.Contains("// code before", model.Prompts[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAuthorThatSupersedesAnUnknownScenarioIsRetried()
    {
        var model = new ScriptedModel();
        var wrong = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Authored)!;
        wrong["scenarios"] = JsonElement.Parse("""[{"name": "new", "given": {}, "when": ["tick 6m"], "then": ["signal x.polled"], "supersedes": "no such"}]""");
        model.Reply(JsonSerializer.Serialize(wrong), Authored);
        await Creator(model).Author("b", "change x.watch: the opposite", null, Ct);
        Assert.Equal(2, model.Prompts.Count);
        Assert.Contains("which the unit does not have", model.Prompts[1], StringComparison.Ordinal);
    }

    [Fact] public void TheAuthorIsToldEarlierScenariosStay() => Assert.Contains("\"supersedes\"", AgentPrompts.AuthorUnit, StringComparison.Ordinal);

    private static readonly Scenario GrowingAndFailing = new("a failed poll changes nothing; the next one counts",
        JsonElement.Parse("""{"http": {"x": {"mentions": {"$each": [{"data": [{"id": "1", "text": "a"}]}, {"$fail": 503}, {"data": [{"id": "2", "text": "b"}, {"id": "1", "text": "a"}]}]}}}}"""),
        ["tick 6m", "then state x.watch Unseen = 1", "tick 6m", "then failed Poll", "then state x.watch Unseen = 1", "tick 6m"],
        ["state x.watch Unseen = 2", "view x.watch contains \"2 unseen\"", "never signal x.error", "signal x.polled"]);

    [Fact]
    public async Task EachFailViewNeverAndInlineThenRunAgainstTheExample()
    {
        var check = await BuilderTools.Check(HostCompiler, "x.watch", new Dictionary<string, string> { ["XWatch.cs"] = AgentPrompts.XWatchExample }, XNeeds, [GrowingAndFailing], Ct);
        Assert.True(check.Passed, string.Join("; ", check.Failures));
    }

    // A reaction that throws whatever the slot form: pressing "seen" with nothing to mark.
    private static readonly string ThrowingSeen = AgentPrompts.XWatchExample.Replace("watch with { Unseen = 0 }",
        "watch.Unseen == 0 ? throw new InvalidOperationException(\"nothing to mark\") : watch with { Unseen = 0 }", StringComparison.Ordinal);

    [Fact]
    public async Task AFailedReactionFailsTheScenarioUnlessALineExpectsIt()
    {
        Assert.NotEqual(AgentPrompts.XWatchExample, ThrowingSeen);
        var files = new Dictionary<string, string> { ["XWatch.cs"] = ThrowingSeen };
        // The given binds the "x" slot although no tick polls: a scenario that leaves a declared slot unbound is refused at install
        // ("slot 'x' has no binding") before its first step runs.
        var silent = new Scenario("seen with nothing to mark", JsonElement.Parse("""{"http": {"x": {"*": {"data": []}}}}"""), ["publish x.seen"], []);
        var expected = silent with { Then = ["failed Seen"] };
        Assert.Contains((await BuilderTools.Check(HostCompiler, "x.watch", files, XNeeds, [silent], Ct)).Failures, f => f.Contains("InvalidOperationException", StringComparison.Ordinal));
        var check = await BuilderTools.Check(HostCompiler, "x.watch", files, XNeeds, [expected], Ct);
        Assert.True(check.Passed, string.Join("; ", check.Failures));
    }

    [Fact]
    public async Task ARedViewLineReachesTheBuilderAsAnError()
    {
        var red = new Scenario("the badge says three", JsonElement.Parse("""{"http": {"x": {"*": {"data": [{"id": "1", "text": "hi"}]}}}}"""),
            ["tick 6m"], ["view x.watch contains \"3 unseen\""]);
        var check = await BuilderTools.Check(HostCompiler, "x.watch", new Dictionary<string, string> { ["XWatch.cs"] = AgentPrompts.XWatchExample }, XNeeds, [red], Ct);
        Assert.Contains(check.Failures, f => f.Contains("ExpectView", StringComparison.Ordinal));
    }

    [Fact]
    public void TheAuthorIsTaughtTheWholeGrammar()
    {
        foreach (var taught in new[] { "\"$each\"", "\"$fail\"", "then state x.watch Unseen = 1", "never signal", "contains \"1 unseen\"", "failed <Function>" })
        { Assert.Contains(taught, AgentPrompts.AuthorUnit, StringComparison.Ordinal); }
        Assert.DoesNotContain("write a separate scenario", AgentPrompts.AuthorUnit, StringComparison.Ordinal);
    }
}
