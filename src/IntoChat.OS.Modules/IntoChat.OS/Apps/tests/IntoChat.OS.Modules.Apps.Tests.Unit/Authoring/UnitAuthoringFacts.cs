using System.Text.Json;
using DigitalBrain.Testing.Synapses;
using IntoChat.OS.Modules.AI.Inference;
using DigitalBrain.Runtime.Synapses;
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
         "state": {"Unseen": "int", "Latest": "string"},
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
        return await Creator(model).Author("b", "watch elonmusk on X", Ct);
    }

    [Fact]
    public async Task AuthorReturnsAUnitWithNeedsAndScenarios()
    {
        var model = new ScriptedModel();
        model.Reply("not json", "{\"alias\": \"X Watch\", \"title\": \"t\", \"spec\": \"s\", \"state\": {}, \"signals\": [], \"needs\": {\"slots\": [], \"view\": false}, \"scenarios\": []}", Authored);

        var unit = await Creator(model).Author("b", "watch elonmusk on X", Ct);

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
        await Assert.ThrowsAsync<InvalidDataException>(() => Creator(model).Author("b", "watch elonmusk", Ct));
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
            ["tick 6m", "publish x.seen {\"by\": \"me\"}", "tick 1h"],
            ["state x.watch Unseen = 0", "state x.watch Last.Count = \"two words\"", "signal x.polled"]);

        var parsed = ScenarioSteps.Parse(scenario);

        Assert.Equal(["x", "mail"], parsed.Fakes.Slots);
        Assert.Collection(parsed.Steps,
            step => Assert.Equal(ScenarioSteps.Start.AddMinutes(6), Assert.IsType<ScenarioTick>(step).At),
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
            step => Assert.Equal("x.polled", Assert.IsType<ExpectSignal>(step).Alias));

        Assert.Equal("{}", Assert.IsType<Publish>(ScenarioSteps.Parse(new Scenario("n", default, ["publish x.seen"], [])).Steps[0]).Payload.GetRawText());
        foreach (var (when, then) in new[] { ("tick soon", ""), ("tick -1m", ""), ("poke x", ""), ("", "state x.watch = 1"), ("", "signal"), ("", "state x.watch Unseen = nope") })
        {
            Assert.Throws<FormatException>(() => ScenarioSteps.Parse(new Scenario("bad", default, when.Length > 0 ? [when] : [], then.Length > 0 ? [then] : [])));
        }
        Assert.Throws<FormatException>(() => ScenarioSteps.Parse(new Scenario("bad", JsonElement.Parse("""{"smtp": {"x": {}}}"""), [], [])));
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

        var unit = await Creator(model).Author("b", "watch elonmusk on X", Ct);

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
}
