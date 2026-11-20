using IntoChat.OS.Tests;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Broker;
using DigitalBrain.Flows;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Flows;

// The build flow on a real silo: author and draft by a scripted Author/Builder answering the X watch, verify on the silo's synapse
// compiler and the unit's own scenarios, deploy through the studio onto the silo's synapse host with the slot bound to a scripted
// connection, and install through the framework's installs neuron, whose ask is the only approval.
public sealed class BuildFlowFacts
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

    // A Builder whose answer breaks the first scenario: it counts one post too many.
    private static readonly string Red = AgentPrompts.XWatchExample.Replace("watch.Unseen + fresh", "watch.Unseen + fresh + 1", StringComparison.Ordinal);

    // The Author answers the X watch (every five minutes when the request says so); the Builder answers Sources in turn (the last
    // one again), or throws with none.
    private sealed class ScriptedDrafter(params string[] sources) : IDrafter
    {
        private int _drafts;

        public int Drafts => _drafts;

        // The existing files each Draft was handed, in call order.
        public List<IReadOnlyDictionary<string, string>?> Existing { get; } = [];

        public List<IReadOnlyDictionary<string, string>?> AuthorExisting { get; } = [];

        public Task<AuthoredUnit> Author(string brainId, string request, IReadOnlyDictionary<string, string>? existing, CancellationToken ct)
        {
            if (sources.Length == 0) { throw new InvalidOperationException("the drafter failed"); }
            lock (AuthorExisting) { AuthorExisting.Add(existing); }
            var unit = Creator.Parse<AuthoredUnit>(request.Contains("every five minutes", StringComparison.Ordinal)
                ? Authored.Replace("\"schedule\": \"every 6m\"", "\"schedule\": \"every 5m\"", StringComparison.Ordinal) : Authored);
            if (request.Contains("count faster", StringComparison.Ordinal))
            {
                unit = unit with
                {
                    Scenarios = [new Scenario("a second poll of the same post adds nothing",
                        JsonElement.Parse("""{"http": {"x": {"mentions": {"data": [{"id": "1", "text": "hi"}]}}}}"""),
                        ["tick 6m", "tick 6m"], ["state x.watch Unseen = 1"])],
                };
            }
            else if (request.Contains("count one more", StringComparison.Ordinal))
            {
                unit = unit with
                {
                    Scenarios = [new Scenario("a poll counts two",
                        JsonElement.Parse("""{"http": {"x": {"mentions": {"data": [{"id": "1", "text": "hi"}]}}}}"""),
                        ["tick 6m"], ["state x.watch Unseen = 2"],
                        Supersedes: request.Contains("instead", StringComparison.Ordinal) ? "a poll counts the new posts" : null)],
                };
            }
            else if (request.Contains("rename", StringComparison.Ordinal))
            {
                unit = unit with { Alias = "y.watch" };
            }
            return Task.FromResult(unit);
        }

        public Task<BuildDraft> Draft(string brainId, AuthoredUnit unit, IReadOnlyDictionary<string, string>? existing, CancellationToken ct)
        {
            lock (Existing) { Existing.Add(existing); }
            var source = sources[Math.Min(Interlocked.Increment(ref _drafts) - 1, sources.Length - 1)];
            return Task.FromResult(new BuildDraft(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["XWatch.cs"] = source,
                [UnitFiles.Spec] = unit.Spec,
                [UnitFiles.Scenarios] = JsonSerializer.Serialize(unit.Scenarios),
            }, null));
        }
    }

    // The brain's http@1 connections, each with its allow-list: ICapabilityConnections and IConnectionHosts over the same list.
    private sealed class HttpConnections(params (string Id, string[] Hosts)[] connections) : ICapabilityConnections, IConnectionHosts
    {
        // A fact may connect others mid-flow.
        public (string Id, string[] Hosts)[] Connections { get; set; } = connections;

        public static string KeyOf(BrainId brain, string id) => ConnectionKeys.For(brain.Value, IHttpConnection.TypeName, id);

        public Task<IReadOnlyList<BrainConnection>> ListInOrder(BrainId brain, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BrainConnection>>([.. Connections.Select(c => new BrainConnection(KeyOf(brain, c.Id), c.Id, true, [Capability.Http.V1], null))]);

        public Task<TCapability> Bind<TCapability>(BrainId brain, string connectionKey, string callerApp, CancellationToken ct = default)
            where TCapability : class
            => throw new NotSupportedException();

        public Task RequestConnection(BrainId brain, CapabilityId capability, string callerApp, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<string>?> AllowedHosts(BrainId brain, string connectionKey, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>?>(Connections.FirstOrDefault(c => KeyOf(brain, c.Id) == connectionKey).Hosts);
    }

    private sealed class BuildFixture(ScriptedDrafter drafter, HttpConnections connections) : BothRuntimesFixture
    {
        protected override IReadOnlyList<Type> Modules => [typeof(AppsModule), typeof(FlutterModule), typeof(FlowsFactsModule)];

        protected override void ConfigureSilo(IServiceCollection services)
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
            services.AddSingleton<ICapabilityConnections>(connections);
            services.AddSingleton<IConnectionHosts>(connections);
            services.TryAddSingleton(TimeProvider.System);
            services.AddSingleton<IDrafter>(drafter);
            services.TryAddSingleton(IntoChat.OS.Host.Kernel.NeuronTypeSearch.Create);
        }
    }

    private static BuildFixture Fixture(HttpConnections connections, params string[] sources) => new(new ScriptedDrafter(sources), connections);

    private static HttpConnections OneCovering() => new(("x1", ["api.x.com"]));

    private static IFlows Flows(INeuronRuntime runtime) => runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));

    private static IInstalls Installs(INeuronRuntime runtime) => runtime.Get<IInstalls>(runtime.Id("synapse.installs", "default"));

    private static string Goal(string goal) => JsonSerializer.Serialize(new BuildInner { Goal = goal }, BuildFlow.Json);

    private static Task<string> Start(INeuronRuntime runtime, string goal = "watch elonmusk on X")
        => Flows(runtime).Start(BuildFlow.Alias, goal, null, Goal(goal), Ct);

    private static IDisposable AsPerson(INeuronRuntime runtime) => runtime.Edge.Enter(runtime.Edge.Person("person", "person", runtime.Brain));

    private static async Task<FlowView> Eventually(Func<Task<FlowView?>> read, Func<FlowView, bool> done)
    {
        FlowView? view = null;
        for (var i = 0; i < 600; i++)
        {
            view = await read();
            if (view is not null && done(view)) { return view; }
            await Task.Delay(50, Ct);
        }
        Assert.Fail($"The flow did not reach the expected view; last: {view}");
        return view!;
    }

    private static Task<FlowView> Settled(INeuronRuntime runtime, string id)
        => Eventually(() => Flows(runtime).Read(id, Ct), v => v.Status is FlowStatus.Waiting or FlowStatus.Failed or FlowStatus.Completed or FlowStatus.Cancelled);

    private static async Task<BuildInner> Inner(INeuronRuntime runtime, string flowId)
    {
        var head = await runtime.Get<IFlowStarter>(Id(flowId)).Head();
        return JsonSerializer.Deserialize<BuildInner>(head.InnerJson, BuildFlow.Json)!;
    }

    private static NeuronId Id(string flowId)
    {
        Assert.True(NeuronId.TryParse(flowId, out var id));
        return id;
    }

    [Fact]
    public async Task AuthorDraftVerifyDeployInstallWaitsOnTheNeedsConsent()
    {
        await using var fixture = Fixture(OneCovering(), AgentPrompts.XWatchExample);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);

        var id = await Start(runtime);

        var view = await Settled(runtime, id);
        Assert.Equal(FlowStatus.Waiting, view.Status);
        Assert.Equal(["author", "draft", "verify", "deploy"], view.Done);
        Assert.Equal(("install", WaitReason.External), (view.WaitingStep, view.WaitingFor));
        var inner = await Inner(runtime, id);
        Assert.Equal("x.watch", inner.Alias);
        Assert.Equal(HttpConnections.KeyOf(runtime.Brain, "x1"), inner.Bindings!["x"]);
        Assert.Equal(DigitalBrain.Units.InstallStatus.Pending, inner.Install);
        Assert.Contains(UnitFiles.Scenarios, inner.Files!.Keys);
        var pending = Assert.Single(await Installs(runtime).Pending(Ct));
        Assert.Equal(inner.PlanId, pending.PlanId);
        Assert.Equal(HttpConnections.KeyOf(runtime.Brain, "x1"), pending.Bindings["x"]);
        IReadOnlyList<Approval> approvals;
        using (AsPerson(runtime)) { approvals = await runtime.Get<IApprovals>(IApprovals.Key(runtime.Brain.Value)).Pending(Ct); }
        var consent = Assert.Single(approvals);
        Assert.Equal((ApprovalKind.SynapseStaged, "x.watch"), (consent.Kind, consent.Subject));
        Assert.Contains("api.x.com", consent.Detail, StringComparison.Ordinal);
        Assert.Null(await Installs(runtime).Describe("x.watch", Ct));
    }

    [Fact]
    public async Task ARedScenarioFailsAtVerifyWithTheTranscript()
    {
        await using var fixture = Fixture(OneCovering(), Red);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);

        var id = await Start(runtime);

        var view = await Settled(runtime, id);
        Assert.Equal(FlowStatus.Failed, view.Status);
        Assert.Equal(["author", "draft"], view.Done);
        Assert.Contains("did not pass verify", view.Detail, StringComparison.Ordinal);
        Assert.Contains("a poll counts the new posts", view.Detail, StringComparison.Ordinal);
        Assert.Contains(await runtime.Signals<FlowStep>(Id(id), Ct), step => step is { Step: "verify", Outcome: StepOutcome.Failed });
        Assert.Empty(await runtime.Get<ISynapseStudio>(ISynapseStudio.Key(runtime.Brain.Value)).List());
    }

    [Fact]
    public async Task NoMatchingConnectionWaitsExternal()
    {
        await using var fixture = Fixture(new HttpConnections(("y1", ["api.y.com", "*.x.org"])), AgentPrompts.XWatchExample);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);

        var id = await Start(runtime);

        var view = await Settled(runtime, id);
        Assert.Equal(FlowStatus.Waiting, view.Status);
        Assert.Equal(["author", "draft", "verify"], view.Done);
        Assert.Equal(("deploy", WaitReason.External), (view.WaitingStep, view.WaitingFor));
        Assert.Equal("connect http@1 for api.x.com", (await Inner(runtime, id)).Missing);
        IReadOnlyList<Approval> approvals;
        using (AsPerson(runtime)) { approvals = await runtime.Get<IApprovals>(IApprovals.Key(runtime.Brain.Value)).Pending(Ct); }
        var card = Assert.Single(approvals);
        Assert.Equal((ApprovalKind.Other, id, "connect http@1 for api.x.com"), (card.Kind, card.Subject, card.Title));
        Assert.Empty(await runtime.Get<ISynapseStudio>(ISynapseStudio.Key(runtime.Brain.Value)).List());
    }

    [Fact]
    public async Task SeveralMatchingConnectionsAskWhich()
    {
        await using var fixture = Fixture(new HttpConnections(("x1", ["api.x.com"]), ("x2", ["*.x.com"])), AgentPrompts.XWatchExample);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);

        var id = await Start(runtime);

        var view = await Settled(runtime, id);
        Assert.Equal(("deploy", WaitReason.External), (view.WaitingStep, view.WaitingFor));
        var asks = await ChoiceCards(runtime, id, 2);
        Assert.Contains(asks, card => card.Title.Contains(HttpConnections.KeyOf(runtime.Brain, "x1"), StringComparison.Ordinal));
        var second = Assert.Single(asks, card => card.Title.Contains(HttpConnections.KeyOf(runtime.Brain, "x2"), StringComparison.Ordinal));

        await runtime.ApproveAsPerson(second.PlanId, Ct);

        var next = await Eventually(() => Flows(runtime).Read(id, Ct), v => v.WaitingStep == "install");
        Assert.Equal(["author", "draft", "verify", "deploy"], next.Done);
        Assert.Equal(HttpConnections.KeyOf(runtime.Brain, "x2"), (await Inner(runtime, id)).Bindings!["x"]);
        // The other candidate's plan was withdrawn: only the needs consent is left.
        IReadOnlyList<Approval> left;
        using (AsPerson(runtime)) { left = await runtime.Get<IApprovals>(IApprovals.Key(runtime.Brain.Value)).Pending(Ct); }
        Assert.Equal(ApprovalKind.SynapseStaged, Assert.Single(left).Kind);
    }

    [Fact]
    public async Task AConnectionArrivingDuringTheWaitWithSeveralFitsAsksWhich()
    {
        var connections = new HttpConnections(("y1", ["api.y.com"]));
        await using var fixture = Fixture(connections, AgentPrompts.XWatchExample);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);
        var id = await Start(runtime);
        await Eventually(() => Flows(runtime).Read(id, Ct), v => v.WaitingStep == "deploy");

        connections.Connections = [("x1", ["api.x.com"]), ("x2", ["*.x.com"])];
        // The re-check runs on ConnectionsChanged, every minute and on activation; a restart is the activation.
        await runtime.Restart(Ct);
        await runtime.Get<IFlowStarter>(Id(id)).Head();

        var asks = await ChoiceCards(runtime, id, 2);
        Assert.Equal(2, asks.Count);
        var inner = await Inner(runtime, id);
        Assert.Null(inner.Missing);
        Assert.False(inner.Bindings?.ContainsKey("x") == true);
        Assert.Equal(("deploy", WaitReason.External), ((await Flows(runtime).Read(id, Ct))!.WaitingStep, (await Flows(runtime).Read(id, Ct))!.WaitingFor));
    }

    // The flow's choice cards once count of them are in the inbox.
    private static async Task<IReadOnlyList<Approval>> ChoiceCards(INeuronRuntime runtime, string flowId, int count)
    {
        IReadOnlyList<Approval> cards = [];
        for (var i = 0; i < 400; i++)
        {
            using (AsPerson(runtime)) { cards = [.. (await runtime.Get<IApprovals>(IApprovals.Key(runtime.Brain.Value)).Pending(Ct)).Where(a => a.Kind == ApprovalKind.FlowStep && a.Subject == flowId)]; }
            if (cards.Count >= count) { return cards; }
            await Task.Delay(50, Ct);
        }
        Assert.Fail($"{cards.Count} choice cards, not {count}");
        return cards;
    }

    [Fact]
    public async Task TheInstallWaitIsCarriedAsSynapseStagedNotFlowStep()
    {
        await using var fixture = Fixture(OneCovering(), AgentPrompts.XWatchExample);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);
        var id = await Start(runtime);

        var view = await Eventually(() => Flows(runtime).Read(id, Ct), v => v.WaitingStep == "install" && v.ConfirmationPlanId is not null);

        var inner = await Inner(runtime, id);
        Assert.Equal(inner.PlanId, view.ConfirmationPlanId);
        IReadOnlyList<Approval> approvals;
        using (AsPerson(runtime)) { approvals = await runtime.Get<IApprovals>(IApprovals.Key(runtime.Brain.Value)).Pending(Ct); }
        var card = Assert.Single(approvals, a => a.PlanId == inner.PlanId);
        Assert.Equal(ApprovalKind.SynapseStaged, card.Kind);
        Assert.Equal(view.TraceId, card.TraceId);
        Assert.DoesNotContain(approvals, a => a.Kind == ApprovalKind.FlowStep);
    }

    // A deploy run again after a crash, before its step persisted: the studio answers the same ask, and the installs neuron holds one.
    [Fact]
    public async Task RedeployAfterACrashRaisesNoSecondAsk()
    {
        await using var fixture = Fixture(OneCovering(), AgentPrompts.XWatchExample);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);
        var id = await Start(runtime);
        await Eventually(() => Flows(runtime).Read(id, Ct), v => v.WaitingStep == "install");
        var inner = await Inner(runtime, id);
        var studio = runtime.Get<ISynapseStudio>(ISynapseStudio.Key(runtime.Brain.Value));

        var again = await studio.DeployFiles("x.watch", inner.Files!, inner.Bindings!);

        Assert.True(again.Ok, again.Text);
        var install = UnitDeploy.Install(again, "x.watch");
        Assert.True(install.Status == DigitalBrain.Units.InstallStatus.Unchanged || (install.Status == DigitalBrain.Units.InstallStatus.Pending && install.Reason == inner.PlanId),
            $"{again.Status} {install.Status} {install.Reason} vs {inner.PlanId}");
        Assert.Equal(inner.PlanId, Assert.Single(await Installs(runtime).Pending(Ct)).PlanId);
        Assert.Equal(inner.Hash, again.Hash);
    }

    [Fact]
    public async Task ApproveInstallsAndTheAppIsLive()
    {
        await using var fixture = Fixture(OneCovering(), AgentPrompts.XWatchExample);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);
        var app = runtime.Get<IApp>(runtime.Id(IntoChat.OS.Host.Primitives.AppGrainType.Name, "apps/xwatch"));
        await app.Create(new AppManifest("X watch", "Watches X", [], [], [], Headless: true), "Count the mentions of elonmusk.");

        await runtime.Services.GetRequiredService<Creator>().Implement(runtime.Services.GetRequiredService<INeuronProxies>(), IntoChat.OS.Host.Primitives.AppRef.Of(runtime.Brain, "xwatch").Key, null, Ct);
        var id = Assert.IsType<string>(await app.BuildFlow());
        await Eventually(() => Flows(runtime).Read(id, Ct), v => v.WaitingStep == "install");
        var building = await app.Read();
        Assert.Equal(AppStatus.Building, building.Status);
        Assert.StartsWith("waiting for approval: ", building.Activity[^1], StringComparison.Ordinal);

        await runtime.ApproveAsPerson((await Inner(runtime, id)).PlanId!, Ct);

        var done = await Eventually(() => Flows(runtime).Read(id, Ct), v => v.Status is FlowStatus.Completed or FlowStatus.Failed or FlowStatus.Cancelled);
        Assert.Equal(FlowStatus.Completed, done.Status);
        Assert.Equal(["author", "draft", "verify", "deploy", "install"], done.Done);
        Assert.NotNull(await Installs(runtime).Describe("x.watch", Ct));
        var live = await app.Read();
        Assert.Equal(AppStatus.Live, live.Status);
        Assert.StartsWith("build finished: synapse unit ", live.Activity[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangeWithSameNeedsGoesLiveWithoutConsent()
    {
        var changed = AgentPrompts.XWatchExample.Replace("// Every six minutes", "// Every six minutes, as before", StringComparison.Ordinal);
        await using var fixture = Fixture(OneCovering(), AgentPrompts.XWatchExample, changed);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);
        var first = await Start(runtime);
        await Eventually(() => Flows(runtime).Read(first, Ct), v => v.WaitingStep == "install");
        await runtime.ApproveAsPerson((await Inner(runtime, first)).PlanId!, Ct);
        await Eventually(() => Flows(runtime).Read(first, Ct), v => v.Status == FlowStatus.Completed);
        var before = Assert.Single(await runtime.Get<ISynapseStudio>(ISynapseStudio.Key(runtime.Brain.Value)).List());

        var second = await Start(runtime, "watch elonmusk on X, and say so in the comment");

        var view = await Settled(runtime, second);
        Assert.Equal(FlowStatus.Completed, view.Status);
        var inner = await Inner(runtime, second);
        Assert.Contains("XWatch.cs", inner.Existing!.Keys);
        Assert.True(inner.Install is DigitalBrain.Units.InstallStatus.Unchanged or DigitalBrain.Units.InstallStatus.Updated, $"{inner.Install}");
        Assert.Empty(await Installs(runtime).Pending(Ct));
        var after = Assert.Single(await runtime.Get<ISynapseStudio>(ISynapseStudio.Key(runtime.Brain.Value)).List());
        Assert.NotEqual(before.Hash, after.Hash);
        Assert.Equal(DeployStatus.Deployed, after.Status);
    }

    // A record left by the first install does not answer a redeploy whose needs are wider: the flow waits on its own new ask, across
    // an activation re-check, until the person approves that ask.
    [Fact]
    public async Task RedeployWithWiderNeedsWaitsOnItsOwnAskDespiteTheOldRecord()
    {
        var wider = AgentPrompts.XWatchExample.Replace("Every = \"every 6m\"", "Every = \"every 5m\"", StringComparison.Ordinal);
        Assert.NotEqual(AgentPrompts.XWatchExample, wider);
        await using var fixture = Fixture(OneCovering(), AgentPrompts.XWatchExample, wider);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);
        var first = await Start(runtime);
        await Eventually(() => Flows(runtime).Read(first, Ct), v => v.WaitingStep == "install");
        await runtime.ApproveAsPerson((await Inner(runtime, first)).PlanId!, Ct);
        await Eventually(() => Flows(runtime).Read(first, Ct), v => v.Status == FlowStatus.Completed);

        var second = await Start(runtime, "watch elonmusk on X every five minutes");
        var waiting = await Settled(runtime, second);
        Assert.Equal((FlowStatus.Waiting, "install", WaitReason.External), (waiting.Status, waiting.WaitingStep, waiting.WaitingFor));
        var plan = (await Inner(runtime, second)).PlanId!;
        Assert.NotEqual((await Inner(runtime, first)).PlanId, plan);
        Assert.NotNull(await Installs(runtime).Describe("x.watch", Ct));

        await runtime.Restart(Ct);
        Assert.Equal(plan, (await Inner(runtime, second)).PlanId);
        await runtime.Idle();
        await Task.Delay(500, Ct);
        Assert.Equal(FlowStatus.Waiting, (await runtime.Get<IFlowStarter>(Id(second)).Head()).Status);
        var still = await Flows(runtime).Read(second, Ct);
        Assert.Equal((FlowStatus.Waiting, "install"), (still!.Status, still.WaitingStep));
        Assert.Equal(plan, Assert.Single(await Installs(runtime).Pending(Ct)).PlanId);

        await runtime.ApproveAsPerson(plan, Ct);
        var done = await Eventually(() => Flows(runtime).Read(second, Ct), v => v.Status is FlowStatus.Completed or FlowStatus.Failed or FlowStatus.Cancelled);
        Assert.Equal(FlowStatus.Completed, done.Status);
    }

    // The host compiles only C#: the studio keeps spec.md and scenarios.json beside the source, and a change run drafts over them.
    [Fact]
    public async Task SidecarsSurviveDeployAndFeedExisting()
    {
        var drafter = new ScriptedDrafter(AgentPrompts.XWatchExample);
        await using var fixture = new BuildFixture(drafter, OneCovering());
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);
        var studio = runtime.Get<ISynapseStudio>(ISynapseStudio.Key(runtime.Brain.Value));
        var first = await Start(runtime);
        await Eventually(() => Flows(runtime).Read(first, Ct), v => v.WaitingStep == "install");

        var sidecars = await studio.Sidecars("x.watch");
        Assert.Equal([UnitFiles.Scenarios, UnitFiles.Spec], sidecars.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("Watches mentions", sidecars[UnitFiles.Spec], StringComparison.Ordinal);

        var second = await Start(runtime, "watch elonmusk on X, twice as often");
        await Eventually(() => Flows(runtime).Read(second, Ct), v => v.Done.Contains("draft"));

        var existing = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(drafter.Existing[^1]);
        Assert.Equal(AgentPrompts.XWatchExample, existing["XWatch.cs"]);
        Assert.Equal(sidecars[UnitFiles.Spec], existing[UnitFiles.Spec]);
        Assert.Equal(sidecars[UnitFiles.Scenarios], existing[UnitFiles.Scenarios]);
    }

    [Fact]
    public async Task CrashBetweenDeployAndInstallResumesAtInstall()
    {
        await using var fixture = Fixture(OneCovering(), AgentPrompts.XWatchExample);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);
        var id = await Start(runtime);
        await Eventually(() => Flows(runtime).Read(id, Ct), v => v.WaitingStep == "install");
        var deployed = await Inner(runtime, id);

        await runtime.Restart(Ct);

        var head = await runtime.Get<IFlowStarter>(Id(id)).Head();
        Assert.Equal(FlowStatus.Waiting, head.Status);
        Assert.Equal(["author", "draft", "verify", "deploy"], head.Done);
        Assert.Equal(deployed.Hash, (await Inner(runtime, id)).Hash);
        // The person approves from the approvals list, whose read restores the stored unit the restarted host has not loaded yet.
        using (AsPerson(runtime)) { Assert.Contains(await runtime.Get<IApprovals>(IApprovals.Key(runtime.Brain.Value)).Pending(Ct), a => a.PlanId == deployed.PlanId); }
        await runtime.ApproveAsPerson(deployed.PlanId!, Ct);
        FlowHead? done = null;
        for (var i = 0; i < 400 && done is not { Status: FlowStatus.Completed or FlowStatus.Failed or FlowStatus.Cancelled }; i++)
        {
            await Task.Delay(50, Ct);
            done = await runtime.Get<IFlowStarter>(Id(id)).Head();
        }
        Assert.Equal(FlowStatus.Completed, done!.Status);
        var unit = Assert.Single(await runtime.Get<ISynapseStudio>(ISynapseStudio.Key(runtime.Brain.Value)).List());
        Assert.Equal(deployed.Hash, unit.Hash);
    }

    [Fact]
    public async Task MakeStartsABuildFlow()
    {
        await using var fixture = Fixture(OneCovering(), AgentPrompts.XWatchExample);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);

        var result = await runtime.Get<IInoStudio>(runtime.Id("ino.studio", "ino/studio")).Make("watch elonmusk on X");

        var card = Assert.IsType<InoCard>(result.Card);
        var view = await Settled(runtime, card.Id);
        Assert.Equal(BuildFlow.Alias, view.Type);
        Assert.Equal(["author", "draft", "verify", "deploy", "install"], view.Steps);
        Assert.Equal("install", view.WaitingStep);
        Assert.Contains(card.Id, result.Text, StringComparison.Ordinal);
        Assert.Equal($"neuron://{runtime.Brain.Value}/apps/tasks#{card.Id}", card.SourceNeuronUri);
        Assert.Contains("Tasks board", result.Text, StringComparison.Ordinal);
        Assert.Equal("x.watch", (await Inner(runtime, card.Id)).Alias);
    }

    [Fact]
    public async Task ABuildFlowCarriesItsUnitAliasAsSubject()
    {
        await using var fixture = Fixture(OneCovering(), AgentPrompts.XWatchExample);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);
        var id = await Start(runtime);

        var view = await Eventually(() => Flows(runtime).Read(id, Ct), v => v.Subject is not null);

        Assert.Equal("x.watch", view.Subject);
    }

    [Fact]
    public async Task ListShowsDeployedUnitsWithTheirSidecars()
    {
        await using var fixture = Fixture(OneCovering(), AgentPrompts.XWatchExample);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);
        var ino = runtime.Get<IInoStudio>(runtime.Id("ino.studio", "ino/studio"));
        var card = Assert.IsType<InoCard>((await ino.Make("watch elonmusk on X")).Card);
        await Settled(runtime, card.Id);

        var listed = await ino.List();

        Assert.Contains("Studios:", listed.Text, StringComparison.Ordinal);
        Assert.Contains("x.watch", listed.Text, StringComparison.Ordinal);
        Assert.Contains("spec.md kept, scenarios.json kept", listed.Text, StringComparison.Ordinal);
    }

    // While the app is Building, a failed build flow shows it Failed with the flow's detail.
    [Fact]
    public async Task AFailedBuildFlowShowsTheBuildingAppFailed()
    {
        await using var fixture = Fixture(OneCovering(), Red);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);
        var app = runtime.Get<IApp>(runtime.Id(IntoChat.OS.Host.Primitives.AppGrainType.Name, "apps/broken"));
        await app.Create(new AppManifest("Broken", "Never builds", [], [], []), "spec");
        await app.BeginBuild(null);
        var flows = Flows(runtime);
        var flowId = Assert.IsType<string>(await app.BuildFlow());
        await flows.Start(BuildFlow.Alias, "Broken", null, Goal("Broken"), Ct, Id(flowId).Key);
        await Eventually(() => flows.Read(flowId, Ct), v => v.Status == FlowStatus.Failed);

        // The app's own FlowDone reaction may already have failed it; the read-side status agrees either way.
        var shown = await AppBuildStatus.Of(await app.Read(), app, flows, Ct);

        Assert.Equal(AppStatus.Failed, shown.Status);
        Assert.Contains(shown.Activity, line => line.StartsWith("build failed: ", StringComparison.Ordinal) && line.Contains("did not pass verify", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedFlowDoneFailsTheBuildingApp()
    {
        await using var fixture = Fixture(OneCovering(), AgentPrompts.XWatchExample);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);
        var app = runtime.Get<IApp>(runtime.Id(IntoChat.OS.Host.Primitives.AppGrainType.Name, "apps/failing"));
        await app.Create(new AppManifest("Failing", "Fails", [], [], []), "spec");
        await app.BeginBuild(null);
        var flowId = Assert.IsType<string>(await app.BuildFlow());

        await runtime.Get<IFlowSignalPump>(runtime.Id(FlowSignalPump.Alias, "pump")).Failed(flowId, "x");

        var snapshot = await SettledApp(app);
        Assert.Equal(AppStatus.Failed, snapshot.Status);
        Assert.Equal("build failed: x", snapshot.Activity[^1]);
    }

    [Fact]
    public async Task AStaleFlowDoneDoesNotFailAFreshAttempt()
    {
        await using var fixture = Fixture(OneCovering(), AgentPrompts.XWatchExample);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);
        var app = runtime.Get<IApp>(runtime.Id(IntoChat.OS.Host.Primitives.AppGrainType.Name, "apps/retried"));
        await app.Create(new AppManifest("Retried", "Fails once", [], [], []), "spec");
        var pump = runtime.Get<IFlowSignalPump>(runtime.Id(FlowSignalPump.Alias, "pump"));
        await app.BeginBuild(null);
        var first = Assert.IsType<string>(await app.BuildFlow());
        await pump.Failed(first, "first");
        Assert.Equal(AppStatus.Failed, (await SettledApp(app)).Status);

        await app.BeginBuild(null);
        var second = Assert.IsType<string>(await app.BuildFlow());
        await pump.Failed(first, "stale");
        await pump.Done(second);
        await Task.Delay(500, Ct);

        var snapshot = await app.Read();
        Assert.NotEqual(first, second);
        Assert.Equal(AppStatus.Building, snapshot.Status);
        Assert.DoesNotContain(snapshot.Activity, line => line.Contains("stale", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFlowThatFailsImmediatelyFailsTheApp()
    {
        await using var fixture = Fixture(OneCovering());
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);
        var app = runtime.Get<IApp>(runtime.Id(IntoChat.OS.Host.Primitives.AppGrainType.Name, "apps/doomed"));
        await app.Create(new AppManifest("Doomed", "Never drafts", [], [], []), "spec");

        await runtime.Services.GetRequiredService<Creator>().Implement(runtime.Services.GetRequiredService<INeuronProxies>(), IntoChat.OS.Host.Primitives.AppRef.Of(runtime.Brain, "doomed").Key, null, Ct);

        var snapshot = await SettledApp(app);
        Assert.Equal(AppStatus.Failed, snapshot.Status);
        Assert.Contains("the drafter failed", snapshot.Activity[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void TheRedBuilderDiffersFromTheExample() => Assert.NotEqual(AgentPrompts.XWatchExample, Red);

    private static async Task FirstLive(INeuronRuntime runtime)
    {
        var first = await Start(runtime);
        await Eventually(() => Flows(runtime).Read(first, Ct), v => v.WaitingStep == "install");
        await runtime.ApproveAsPerson((await Inner(runtime, first)).PlanId!, Ct);
        await Eventually(() => Flows(runtime).Read(first, Ct), v => v.Status == FlowStatus.Completed);
    }

    private static async Task<Scenario[]> Kept(ISynapseStudio studio)
        => JsonSerializer.Deserialize<Scenario[]>((await studio.Sidecars("x.watch"))[UnitFiles.Scenarios], JsonSerializerOptions.Web)!;

    [Fact]
    public async Task AChangeKeepsTheEarlierScenariosAndShowsThemToTheAuthor()
    {
        var drafter = new ScriptedDrafter(AgentPrompts.XWatchExample);
        await using var fixture = new BuildFixture(drafter, OneCovering());
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);
        var studio = runtime.Get<ISynapseStudio>(ISynapseStudio.Key(runtime.Brain.Value));
        await FirstLive(runtime);

        var second = await Start(runtime, "change x.watch: count faster");
        Assert.Equal(FlowStatus.Completed, (await Settled(runtime, second)).Status);
        Assert.Equal(["a poll counts the new posts", "seen clears the count", "a second poll of the same post adds nothing"], (await Kept(studio)).Select(s => s.Name));
        Assert.Equal(["XWatch.cs", UnitFiles.Scenarios, UnitFiles.Spec], drafter.AuthorExisting[^1]!.Keys.Order(StringComparer.Ordinal));
        Assert.Null(drafter.AuthorExisting[0]);
    }

    [Fact]
    public async Task AChangeThatBreaksAnEarlierScenarioFailsVerifyAndNamesIt()
    {
        var drafter = new ScriptedDrafter(AgentPrompts.XWatchExample, Red);
        await using var fixture = new BuildFixture(drafter, OneCovering());
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);
        var studio = runtime.Get<ISynapseStudio>(ISynapseStudio.Key(runtime.Brain.Value));
        await FirstLive(runtime);

        var view = await Settled(runtime, await Start(runtime, "change x.watch: count one more"));
        Assert.Equal(FlowStatus.Failed, view.Status);
        Assert.Contains("a poll counts the new posts", view.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASupersedingChangeGoesLiveAndKeepsTheRetiredScenario()
    {
        var drafter = new ScriptedDrafter(AgentPrompts.XWatchExample, Red);
        await using var fixture = new BuildFixture(drafter, OneCovering());
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);
        var studio = runtime.Get<ISynapseStudio>(ISynapseStudio.Key(runtime.Brain.Value));
        await FirstLive(runtime);

        Assert.Equal(FlowStatus.Completed, (await Settled(runtime, await Start(runtime, "change x.watch: count one more instead"))).Status);
        var kept = await Kept(studio);
        Assert.Equal("change x.watch: count one more instead", kept.Single(s => s.Name == "a poll counts the new posts").Superseded);
        Assert.Equal("a poll counts the new posts", kept.Single(s => s.Name == "a poll counts two").Supersedes);
    }

    [Fact]   // Review Focus 4
    public async Task AChangeThatRenamesTheUnitFails()
    {
        var drafter = new ScriptedDrafter(AgentPrompts.XWatchExample);
        await using var fixture = new BuildFixture(drafter, OneCovering());
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);
        var studio = runtime.Get<ISynapseStudio>(ISynapseStudio.Key(runtime.Brain.Value));
        await FirstLive(runtime);

        var view = await Settled(runtime, await Start(runtime, "change x.watch: rename it"));
        Assert.Equal(FlowStatus.Failed, view.Status);
        Assert.Contains("a change keeps the alias", view.Detail, StringComparison.Ordinal);
        Assert.Single(await studio.List());
    }

    // The app's status once it leaves Building, or Building after ten seconds.
    private static async Task<AppSnapshot> SettledApp(IApp app)
    {
        var snapshot = await app.Read();
        for (var i = 0; i < 200 && snapshot.Status == AppStatus.Building; i++) { await Task.Delay(50, Ct); snapshot = await app.Read(); }
        return snapshot;
    }
}
