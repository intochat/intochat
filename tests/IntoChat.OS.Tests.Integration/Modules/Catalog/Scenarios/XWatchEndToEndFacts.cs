using IntoChat.OS.Tests;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Broker;
using DigitalBrain.Capabilities.Http;
using DigitalBrain.Flows;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using DigitalBrain.Units;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Debugger;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Tests.Integration.Flows;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Scenarios;

// The first live scenario (design 6.6), recorded on TestOS: a person asks Ino to track elonmusk on X, the scripted Author and
// Builder answer the X watch, the flow waits on the needs consent, the person approves, six minutes pass and the view counts the
// one post; then "change it" redeploys over the same needs without a new consent. The run doc is OS/docs/runs/2026-11-09-x-watch.md.
public sealed class XWatchEndToEndFacts
{
    private const string Alias = "watch.x.elonmusk";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Authored = """
        {"alias": "watch.x.elonmusk", "title": "X watch",
         "spec": "Watches mentions of @elonmusk on X and counts the unseen ones.",
         "state": {"Unseen": "int", "Latest": "string", "LastId": "string"},
         "signals": [{"alias": "x.polled", "fields": {"Fresh": "int"}}],
         "needs": {"slots": [{"name": "x", "capability": "http@1", "hosts": ["api.x.com"]}], "schedule": "every 6m", "view": true},
         "scenarios": [{"name": "a poll counts the new posts",
                        "given": {"http": {"x": {"mentions": {"data": [{"id": "1", "text": "hi"}]}}}},
                        "when": ["tick 6m"],
                        "then": ["state watch.x.elonmusk Unseen = 1", "signal x.polled"]}]}
        """;

    private static readonly string Source = AgentPrompts.XWatchExample.Replace("\"x.watch\"", "\"" + Alias + "\"", StringComparison.Ordinal);

    // "make the badge red when over 10": the view changes, the needs do not.
    private static readonly string Edited = Source.Replace("watch.Unseen > 0 ? \"accent\" : \"neutral\"",
        "watch.Unseen > 10 ? \"red\" : watch.Unseen > 0 ? \"accent\" : \"neutral\"", StringComparison.Ordinal);

    private sealed class ScriptedDrafter : IDrafter
    {
        private int _drafts;

        public Task<AuthoredUnit> Author(string brainId, string request, IReadOnlyDictionary<string, string>? existing, CancellationToken ct) => Task.FromResult(Creator.Parse<AuthoredUnit>(Authored));

        public Task<BuildDraft> Draft(string brainId, AuthoredUnit unit, IReadOnlyDictionary<string, string>? existing, CancellationToken ct)
            => Task.FromResult(new BuildDraft(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["XWatch.cs"] = Interlocked.Increment(ref _drafts) == 1 ? Source : Edited,
                [UnitFiles.Spec] = unit.Spec,
                [UnitFiles.Scenarios] = JsonSerializer.Serialize(unit.Scenarios),
            }, null));
    }

    // A feed that grows by one post per poll at api.x.com; refuses every other host.
    private sealed class FakeX : IHttpRead
    {
        public int Calls;

        public Task<JsonElement> GetJson(string url, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref Calls);
            if (new Uri(url).Host != "api.x.com") { throw new HostNotAllowedException(new Uri(url).Host); }
            var posts = string.Join(", ", Enumerable.Range(1, n).Reverse().Select(id => $$"""{"id": "{{id}}", "text": "post {{id}}"}"""));
            return Task.FromResult(JsonDocument.Parse($$"""{"data": [{{posts}}]}""").RootElement.Clone());
        }

        public Task<string> GetText(string url, CancellationToken ct) => throw new NotSupportedException();
    }

    // One http@1 connection, allowedHosts ["api.x.com"], binding to FakeX.
    private sealed class XConnection(FakeX x) : ICapabilityConnections, IConnectionHosts
    {
        public static string KeyOf(BrainId brain) => ConnectionKeys.For(brain.Value, IHttpConnection.TypeName, "x1");

        public Task<IReadOnlyList<BrainConnection>> ListInOrder(BrainId brain, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BrainConnection>>([new BrainConnection(KeyOf(brain), "x1", true, [Capability.Http.V1], null)]);

        public Task<TCapability> Bind<TCapability>(BrainId brain, string connectionKey, string callerApp, CancellationToken ct = default)
            where TCapability : class
            => x is TCapability capability ? Task.FromResult(capability) : throw new NotSupportedException(typeof(TCapability).Name);

        public Task RequestConnection(BrainId brain, CapabilityId capability, string callerApp, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<string>?> AllowedHosts(BrainId brain, string connectionKey, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>?>(connectionKey == KeyOf(brain) ? ["api.x.com"] : null);
    }

    private sealed class Fixture(XConnection connection) : BothRuntimesFixture
    {
        protected override IReadOnlyList<Type> Modules => [typeof(AppsModule), typeof(FlutterModule), typeof(FlowsFactsModule)];

        protected override void ConfigureTestOS(IServiceCollection services) => Configure(services);

        protected override void ConfigureSilo(IServiceCollection services) => Configure(services);

        private void Configure(IServiceCollection services)
        {
            AppsModule.ConfigureServices(services, new ConfigurationBuilder().Build());
            services.AddJournalPayloads(typeof(IApp).Assembly);
            services.AddJournalPayloads(typeof(AppsModule).Assembly);
            services.AddJournalPayloads(typeof(IntoChat.OS.Modules.AI.Agents.Context.Signals.ContextUpdated).Assembly);
            services.AddJournalPayloads(typeof(FlutterModule).Assembly);
            services.AddJournalPayloads(typeof(IntoChat.OS.Modules.Flutter.Views.IView).Assembly);
            services.AddJournalPayloads(typeof(DigitalBrain.Signals.Activated).Assembly);
            services.AddJournalPayloads(typeof(FlowStarted).Assembly);
            services.AddJournalPayloads(typeof(TestFlow).Assembly);
            services.AddSingleton<ICapabilityConnections>(connection);
            services.AddSingleton<IConnectionHosts>(connection);
            services.TryAddSingleton(TimeProvider.System);
            services.AddSingleton<IDrafter>(new ScriptedDrafter());
            services.TryAddSingleton(IntoChat.OS.Host.Kernel.NeuronTypeSearch.Create);
        }
    }

    private static IFlows Flows(INeuronRuntime runtime) => runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));

    private static IInstalls Installs(INeuronRuntime runtime) => runtime.Get<IInstalls>(runtime.Id("synapse.installs", "default"));

    private static async Task<FlowView> Eventually(INeuronRuntime runtime, string id, Func<FlowView, bool> done)
    {
        FlowView? view = null;
        for (var i = 0; i < 600; i++)
        {
            await runtime.Idle();
            view = await Flows(runtime).Read(id, Ct);
            if (view is not null && done(view)) { return view; }
            await Task.Delay(50, Ct);
        }
        Assert.Fail($"The flow did not reach the expected view; last: {view}");
        return view!;
    }

    private static async Task<BuildInner> Inner(INeuronRuntime runtime, string flowId)
    {
        Assert.True(NeuronId.TryParse(flowId, out var id));
        return JsonSerializer.Deserialize<BuildInner>((await runtime.Get<IFlowStarter>(id).Head()).InnerJson, BuildFlow.Json)!;
    }

    private static async Task<JsonElement> Badge(INeuronRuntime runtime)
    {
        var shown = await runtime.Get<IViewReader>(new NeuronId(runtime.Brain, Alias, "default")).Read(Alias);
        Assert.NotNull(shown);
        return Nodes(shown.View).Single(node => node.TryGetProperty("kind", out var kind) && kind.GetString() == "badge");
    }

    private static IEnumerable<JsonElement> Nodes(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;
            foreach (var property in element.EnumerateObject()) { foreach (var node in Nodes(property.Value)) { yield return node; } }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) { foreach (var node in Nodes(item)) { yield return node; } }
        }
    }

    [Fact]
    public async Task TrackElonmuskOnXEndToEnd()
    {
        var x = new FakeX();
        await using var fixture = new Fixture(new XConnection(x));
        await using var runtime = await fixture.Start(NeuronRuntimeKind.TestOS, Ct);
        using var person = runtime.As(Who.Person);
        var ino = runtime.Get<IInoStudio>(runtime.Id("ino.studio", "ino/studio"));

        // 1. The sentence: Ino starts the build flow, which runs author, draft, verify, deploy and waits on install.
        var made = await ino.Make("track elonmusk on X every 6 minutes and show the unseen count");
        var flowId = Assert.IsType<InoCard>(made.Card).Id;
        var waiting = await Eventually(runtime, flowId, v => v.Status is FlowStatus.Waiting or FlowStatus.Failed or FlowStatus.Completed);
        Assert.True(waiting.Status == FlowStatus.Waiting, $"{waiting.Status} after {string.Join(",", waiting.Done)}: {waiting.Detail}");
        Assert.Equal(["author", "draft", "verify", "deploy"], waiting.Done);
        Assert.Equal(("install", WaitReason.External), (waiting.WaitingStep, waiting.WaitingFor));
        var inner = await Inner(runtime, flowId);
        Assert.Equal(Alias, inner.Alias);
        Assert.Equal(XConnection.KeyOf(runtime.Brain), inner.Bindings!["x"]);

        // 2. The consent: the framework's install ask, whose sentence names http, the 6m schedule and the view; the flow waits on its plan.
        // (The Approvals listing reads the silo's confirmations inbox neuron, which TestOS does not host: not covered here.)
        var consent = Assert.Single(await Installs(runtime).Pending(Ct));
        Assert.Equal((Alias, inner.PlanId), (consent.Alias, consent.PlanId));
        Assert.Equal(inner.PlanId, waiting.ConfirmationPlanId);
        Assert.Contains("http", consent.Sentence, StringComparison.Ordinal);
        Assert.Contains("6m", consent.Sentence, StringComparison.Ordinal);
        Assert.Contains("view", consent.Sentence, StringComparison.Ordinal);
        TestContext.Current.SendDiagnosticMessage($"flow {flowId} trace {waiting.TraceId}; consent {consent.PlanId} needs {consent.NeedsHash}: {consent.Sentence}");

        // 3. Approve as the person: installed, the flow completes.
        await runtime.ApproveAsPerson(inner.PlanId!, Ct);
        var done = await Eventually(runtime, flowId, v => v.Status is FlowStatus.Completed or FlowStatus.Failed or FlowStatus.Cancelled);
        Assert.True(done.Status == FlowStatus.Completed, $"{done.Status}: {done.Detail}");
        Assert.Equal(["author", "draft", "verify", "deploy", "install"], done.Done);
        Assert.NotNull(await Installs(runtime).Describe(Alias, Ct));

        // 4. Six minutes pass: one poll, the view's badge says 1.
        await runtime.Elapse(TimeSpan.FromMinutes(6), Ct);
        await runtime.Idle();
        Assert.True(x.Calls >= 1, "the tick did not poll X");
        var badge = await Badge(runtime);
        TestContext.Current.SendDiagnosticMessage($"view before: {badge.GetRawText()}");
        Assert.StartsWith("1", badge.GetProperty("label").GetString(), StringComparison.Ordinal);

        // 5. The tick's trace, on one trace id: the tick, Poll's Polled, the re-rendered view.
        // The Creator's reads list signals a loaded unit consumes: the unit consumes time.tick, so the tick names the trace.
        var ticks = (await runtime.Get<IDebuggerReads>(IDebuggerReads.Key(runtime.Brain.Value)).Signals("time.tick", null, 10)).Entries;
        var traceId = Assert.IsType<string>(Assert.Single(ticks).TraceId);
        var trace = (await runtime.Get<ITraceReader>(ITraceReader.Key(runtime.Brain.Value)).ReadTrace(traceId, 0, 1000)).Entries;
        TestContext.Current.SendDiagnosticMessage($"trace {traceId}: {string.Join(" -> ", trace.Select(e => e.Signal))}");
        // A unit's state neuron journals no synapse.state-changed: Poll's state change shows as its Polled and the view it re-rendered.
        Assert.Equal(["time.tick", "x.polled", "synapse.view-changed"], trace.Select(e => e.Signal));
        Assert.All(trace, e => Assert.Equal(traceId, e.TraceId));

        // 6. "Change it": the same needs, so the edited unit goes live without a new consent.
        var change = Assert.IsType<InoCard>((await ino.Make("change watch.x.elonmusk: make the badge red when over 10")).Card).Id;
        var changedFlow = await Eventually(runtime, change, v => v.Status is FlowStatus.Completed or FlowStatus.Failed or FlowStatus.Waiting or FlowStatus.Cancelled);
        Assert.True(changedFlow.Status == FlowStatus.Completed, $"{changedFlow.Status} at {changedFlow.WaitingStep}: {changedFlow.Detail}");
        var second = await Inner(runtime, change);
        Assert.True(second.Install is DigitalBrain.Units.InstallStatus.Updated or DigitalBrain.Units.InstallStatus.Unchanged, $"{second.Install}");
        Assert.NotEqual(inner.Hash, second.Hash);
        Assert.Empty(await Installs(runtime).Pending(Ct));

        // Ten more polls bring ten new posts: 11 unseen, red.
        for (var i = 0; i < 10; i++) { await runtime.Elapse(TimeSpan.FromMinutes(6), Ct); }
        await runtime.Idle();
        var after = await Badge(runtime);
        TestContext.Current.SendDiagnosticMessage($"view after: {after.GetRawText()}");
        Assert.StartsWith("11", after.GetProperty("label").GetString(), StringComparison.Ordinal);
        Assert.Equal("red", after.GetProperty("tone").GetString());
    }

    private const string Flaky = """
        namespace Units.Flaky;

        [State("flaky.unit")]
        public sealed record Runs(int Count = 0)
        {
            public Runs() : this(0) { }
        }

        public static class FlakyUnit
        {
            [Synapse(typeof(Tick), "flaky.unit", Every = "every 6m")]
            public static Runs Run(Tick tick, Runs runs)
                => tick.At.UtcTicks / TimeSpan.TicksPerMinute / 6 % 2 == 0
                    ? runs with { Count = runs.Count + 1 }
                    : throw new InvalidOperationException("odd tick; Bearer abcdefgh12345678");
        }
        """;

    [Fact]
    public async Task AFailedReactionIsReadableUntilTheNextGoodOne()
    {
        await using var fixture = new Fixture(new XConnection(new FakeX()));
        await using var runtime = await fixture.Start(NeuronRuntimeKind.TestOS, Ct);
        using var person = runtime.As(Who.Person);
        var studio = runtime.Get<ISynapseStudio>(ISynapseStudio.Key(runtime.Brain.Value));
        var reads = runtime.Get<IDebuggerReads>(IDebuggerReads.Key(runtime.Brain.Value));
        var deployed = await studio.DeployFiles("flaky.unit", new Dictionary<string, string> { ["Flaky.cs"] = Flaky }, new Dictionary<string, string>());
        Assert.True(deployed.Ok, deployed.Text);
        await runtime.ApproveAsPerson(deployed.Installs["flaky.unit"].Reason!, Ct);
        await runtime.Idle();
        Assert.Equal("flaky.unit has no recorded failures.", (await studio.Failures("flaky.unit")).Text);

        var failing = new List<TraceEntry?>();
        for (var i = 0; i < 4; i++)
        {
            await runtime.Elapse(TimeSpan.FromMinutes(6), Ct);
            await runtime.Idle();
            failing.Add(await reads.Failing("flaky.unit"));
        }

        Assert.Equal(2, failing.Count(entry => entry is not null));
        Assert.NotEqual(failing[0] is null, failing[1] is null); // a failure shows, and the next good run clears it
        Assert.NotEqual(failing[2] is null, failing[3] is null);
        var page = await reads.Failures("flaky.unit", 10);
        Assert.Equal(2, page.Entries.Length);
        Assert.All(page.Entries, entry => Assert.Equal("synapse.failed", entry.Signal));
        Assert.Contains("\"function\":\"Units.Flaky.FlakyUnit.Run\"", page.Entries[0].Payload, StringComparison.Ordinal); // the journal holds the full name
        Assert.DoesNotContain("abcdefgh12345678", page.Entries[0].Payload, StringComparison.Ordinal); // redacted by TraceReaderNeuron.Entry
        var told = (await studio.Failures("flaky.unit")).Text;
        Assert.Contains(" Run on time.tick: InvalidOperationException: odd tick", told, StringComparison.Ordinal); // a person reads the method name
        Assert.DoesNotContain("FlakyUnit", told, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefgh12345678", told, StringComparison.Ordinal);
        Assert.Equal(2, told.Split('\n').Length);
        Assert.Empty((await reads.Failures("watch.x.elonmusk", 10)).Entries); // an alias nothing is loaded for
    }
}
