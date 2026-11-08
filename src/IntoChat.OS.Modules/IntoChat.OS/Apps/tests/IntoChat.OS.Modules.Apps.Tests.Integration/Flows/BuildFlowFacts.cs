using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Broker;
using DigitalBrain.Flows;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Spi;
using DigitalBrain.Testing;
using IntoChat.OS.Framework;
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

    // A Builder whose answer breaks the first scenario: it counts one post too many.
    private static readonly string Red = AgentPrompts.XWatchExample.Replace("watch.Unseen + fresh", "watch.Unseen + fresh + 1", StringComparison.Ordinal);

    // The Author answers the X watch; the Builder answers Sources in turn (the last one again), or throws with none.
    private sealed class ScriptedDrafter(params string[] sources) : IDrafter
    {
        private int _drafts;

        public int Drafts => _drafts;

        // The existing files each Draft was handed, in call order.
        public List<IReadOnlyDictionary<string, string>?> Existing { get; } = [];

        public Task<AuthoredUnit> Author(string brainId, string request, CancellationToken ct)
            => sources.Length == 0 ? throw new InvalidOperationException("the drafter failed") : Task.FromResult(Creator.Parse<AuthoredUnit>(Authored));

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

    // Lets Implement past the sandbox policy.
    private sealed class RunnableSandbox : IScriptSandbox
    {
        public bool CanRun => true;

        public Task<ScriptContractCatalog> ReadContracts(IReadOnlyList<string> modules, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ScriptContractCatalog> ReadContractDetail(string moduleId, string contractName, string? methodName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ScriptCompilationCheck> Compile(IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Verdict[]> Judge(string brain, IReadOnlyDictionary<string, string> bundle, object signal, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class NoAppRuntime : IAppRuntime
    {
        public bool Accepts(AppBundle bundle) => false;

        public Task<IReadOnlyDictionary<string, string>> Deploy(string appKey, Dictionary<string, string> files, Dictionary<string, string> configuration, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task Retire(string appKey, CancellationToken cancellationToken) => Task.CompletedTask;
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
            services.TryAddSingleton<ICallFilter, CallFilter>();
            services.TryAddSingleton(TimeProvider.System);
            services.AddSingleton<IAppRuntime>(new NoAppRuntime());
            services.AddSingleton<IDrafter>(drafter);
            services.AddSingleton<IScriptSandbox>(new RunnableSandbox());
            services.TryAddSingleton(IntoChat.OS.Connections.Contracts.NeuronTypeSearch.Create);
        }
    }

    private static BuildFixture Fixture(HttpConnections connections, params string[] sources) => new(new ScriptedDrafter(sources), connections);

    private static HttpConnections OneCovering() => new(("x1", ["api.x.com"]));

    private static IFlows Flows(INeuronRuntime runtime) => runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));

    private static IInstalls Installs(INeuronRuntime runtime) => runtime.Get<IInstalls>(runtime.Id("synapse.installs", "default"));

    private static string Goal(string goal) => JsonSerializer.Serialize(new BuildInner { Goal = goal }, BuildFlow.Json);

    private static Task<string> Start(INeuronRuntime runtime, string goal = "watch elonmusk on X")
        => Flows(runtime).Start(BuildFlow.Alias, goal, null, Goal(goal), Ct);

    private static IDisposable AsPerson(INeuronRuntime runtime) => RuntimeCallerContext.Stamp(CapabilityCallers.Of("person", "person", runtime.Brain.Value));

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

        var id = await Start(runtime);

        var view = await Settled(runtime, id);
        Assert.Equal(FlowStatus.Waiting, view.Status);
        Assert.Equal(["author", "draft", "verify", "deploy"], view.Done);
        Assert.Equal(("install", WaitReason.External), (view.WaitingStep, view.WaitingFor));
        var inner = await Inner(runtime, id);
        Assert.Equal("x.watch", inner.Alias);
        Assert.Equal(HttpConnections.KeyOf(runtime.Brain, "x1"), inner.Bindings!["x"]);
        Assert.Equal(DigitalBrain.InstallStatus.Pending, inner.Install);
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
        var id = await Start(runtime);
        await Eventually(() => Flows(runtime).Read(id, Ct), v => v.WaitingStep == "install");
        var inner = await Inner(runtime, id);
        var studio = runtime.Get<ISynapseStudio>(ISynapseStudio.Key(runtime.Brain.Value));

        var again = await studio.DeployFiles("x.watch", inner.Files!, inner.Bindings!);

        Assert.True(again.Ok, again.Text);
        var install = UnitDeploy.Install(again, "x.watch");
        Assert.True(install.Status == DigitalBrain.InstallStatus.Unchanged || (install.Status == DigitalBrain.InstallStatus.Pending && install.Reason == inner.PlanId),
            $"{again.Status} {install.Status} {install.Reason} vs {inner.PlanId}");
        Assert.Equal(inner.PlanId, Assert.Single(await Installs(runtime).Pending(Ct)).PlanId);
        Assert.Equal(inner.Hash, again.Hash);
    }

    [Fact]
    public async Task ApproveInstallsAndTheAppIsLive()
    {
        await using var fixture = Fixture(OneCovering(), AgentPrompts.XWatchExample);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var app = runtime.Get<IApp>(runtime.Id(IntoChat.OS.Connections.AppGrainType.Name, "apps/xwatch"));
        await app.Create(new AppManifest("X watch", "Watches X", [], [], [], Headless: true), "Count the mentions of elonmusk.");

        await runtime.Services.GetRequiredService<Creator>().Implement(runtime.Services.GetRequiredService<INeuronProxies>(), IApp.Key(runtime.Brain.Value, "xwatch"), null, Ct);
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
        Assert.True(inner.Install is DigitalBrain.InstallStatus.Unchanged or DigitalBrain.InstallStatus.Updated, $"{inner.Install}");
        Assert.Empty(await Installs(runtime).Pending(Ct));
        var after = Assert.Single(await runtime.Get<ISynapseStudio>(ISynapseStudio.Key(runtime.Brain.Value)).List());
        Assert.NotEqual(before.Hash, after.Hash);
        Assert.Equal(DeployStatus.Deployed, after.Status);
    }

    // The host compiles only C#: the studio keeps spec.md and scenarios.json beside the source, and a change run drafts over them.
    [Fact]
    public async Task SidecarsSurviveDeployAndFeedExisting()
    {
        var drafter = new ScriptedDrafter(AgentPrompts.XWatchExample);
        await using var fixture = new BuildFixture(drafter, OneCovering());
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
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

        var result = await runtime.Get<IInoStudio>(runtime.Id("ino.studio", "ino/studio")).Make("watch elonmusk on X");

        var card = Assert.IsType<InoCard>(result.Card);
        var view = await Settled(runtime, card.Id);
        Assert.Equal(BuildFlow.Alias, view.Type);
        Assert.Equal("install", view.WaitingStep);
        Assert.Contains(card.Id, result.Text, StringComparison.Ordinal);
    }

    // While the app is Building, a failed build flow shows it Failed with the flow's detail.
    [Fact]
    public async Task AFailedBuildFlowShowsTheBuildingAppFailed()
    {
        await using var fixture = Fixture(OneCovering(), Red);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var app = runtime.Get<IApp>(runtime.Id(IntoChat.OS.Connections.AppGrainType.Name, "apps/broken"));
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
        var app = runtime.Get<IApp>(runtime.Id(IntoChat.OS.Connections.AppGrainType.Name, "apps/failing"));
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
        var app = runtime.Get<IApp>(runtime.Id(IntoChat.OS.Connections.AppGrainType.Name, "apps/retried"));
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
        var app = runtime.Get<IApp>(runtime.Id(IntoChat.OS.Connections.AppGrainType.Name, "apps/doomed"));
        await app.Create(new AppManifest("Doomed", "Never drafts", [], [], []), "spec");

        await runtime.Services.GetRequiredService<Creator>().Implement(runtime.Services.GetRequiredService<INeuronProxies>(), IApp.Key(runtime.Brain.Value, "doomed"), null, Ct);

        var snapshot = await SettledApp(app);
        Assert.Equal(AppStatus.Failed, snapshot.Status);
        Assert.Contains("the drafter failed", snapshot.Activity[^1], StringComparison.Ordinal);
    }

    // The app's status once it leaves Building, or Building after ten seconds.
    private static async Task<AppSnapshot> SettledApp(IApp app)
    {
        var snapshot = await app.Read();
        for (var i = 0; i < 200 && snapshot.Status == AppStatus.Building; i++) { await Task.Delay(50, Ct); snapshot = await app.Read(); }
        return snapshot;
    }
}
