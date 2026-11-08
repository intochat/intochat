using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections.Broker;
using DigitalBrain.Flows;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Spi;
using DigitalBrain.Testing;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Tests.Integration.Ino;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Flows;

// The build flow on a real silo: draft (a scripted drafter in place of the Builder's model), check (the silo's synapse compiler),
// deploy (the studio over a recording host) and approve.
public sealed class BuildFlowFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Answers a unit draft with one file of Source, an app draft with a notes file and the app's own manifest.
    private sealed class ScriptedDrafter(string source) : IDrafter
    {
        public Task<BuildDraft> Draft(BuildRequest request, CancellationToken ct)
            => Task.FromResult(request.Snapshot is { Manifest.Headless: false } app
                ? new BuildDraft(new Dictionary<string, string>(StringComparer.Ordinal) { ["notes.md"] = source }, app.Manifest)
                : new BuildDraft(new Dictionary<string, string>(StringComparer.Ordinal) { ["unit.cs"] = source }, null));
    }

    // Lets Implement past the sandbox policy; an app of non-program files never compiles.
    private sealed class RunnableSandbox : IScriptSandbox
    {
        public bool CanRun => true;

        public string AuthoringDescription => "";

        public Task<ScriptContractCatalog> ReadContracts(IReadOnlyList<string> modules, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ScriptContractCatalog> ReadContractDetail(string moduleId, string contractName, string? methodName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ScriptCompilationCheck> Compile(IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Verdict[]> Judge(string brain, IReadOnlyDictionary<string, string> bundle, object signal, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    // Counts completed deploys. With Block set, the first deploy never returns, as one cut off by a crash.
    private sealed class RecordingHost : ISynapseHost
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        private readonly List<DeployedSource> _live = [];

        public bool Block { get; init; }

        public int Completed { get; private set; }

        public int Calls => _calls;

        public Task Entered => _entered.Task;

        public async Task<DeployOutcome> Deploy(BrainId brain, SynapseSource source, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> bindingsPerAlias, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1 && Block)
            {
                _entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, CancellationToken.None);
            }
            Completed++;
            var hash = "h" + Completed;
            lock (_live) { _live.RemoveAll(unit => unit.SourceKey == source.SourceKey); _live.Add(new DeployedSource(source.SourceKey, hash, [], DeployStatus.Deployed, null, DateTimeOffset.UtcNow)); }
            return new DeployOutcome(DeployStatus.Deployed, hash, [], new Dictionary<string, DigitalBrain.InstallOutcome>(), null);
        }

        public Task<DeployOutcome> Retire(BrainId brain, string sourceKey, CancellationToken ct)
            => Task.FromResult(new DeployOutcome(DeployStatus.Retired, null, [], new Dictionary<string, DigitalBrain.InstallOutcome>(), null));

        public Task<IReadOnlyList<DeployedSource>> List(BrainId brain, CancellationToken ct)
        {
            lock (_live) { return Task.FromResult<IReadOnlyList<DeployedSource>>([.. _live]); }
        }
    }

    private sealed class NoAppRuntime : IAppRuntime
    {
        public bool Accepts(AppBundle bundle) => false;

        public Task<IReadOnlyDictionary<string, string>> Deploy(string appKey, Dictionary<string, string> files, Dictionary<string, string> configuration, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task Retire(string appKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class EmptyCapabilityConnections : ICapabilityConnections
    {
        public Task<IReadOnlyList<BrainConnection>> ListInOrder(BrainId brain, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BrainConnection>>([]);

        public Task<TCapability> Bind<TCapability>(BrainId brain, string connectionKey, string callerApp, CancellationToken ct = default)
            where TCapability : class
            => throw new NotSupportedException();

        public Task RequestConnection(BrainId brain, CapabilityId capability, string callerApp, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class BuildFixture(string source, RecordingHost host) : BothRuntimesFixture
    {
        protected override IReadOnlyList<Type> Modules => [typeof(AppsModule), typeof(FlutterModule), typeof(FlowsFactsModule)];

        protected override void ConfigureSilo(IServiceCollection services)
        {
            AppsModule.ConfigureServices(services, new ConfigurationBuilder().Build());
            services.AddJournalPayloads(typeof(IApp).Assembly);
            services.AddJournalPayloads(typeof(AppsModule).Assembly);
            services.AddJournalPayloads(typeof(FlutterModule).Assembly);
            services.AddJournalPayloads(typeof(IntoChat.OS.Modules.Flutter.Views.IView).Assembly);
            services.AddJournalPayloads(typeof(DigitalBrain.Signals.Activated).Assembly);
            services.AddJournalPayloads(typeof(FlowStarted).Assembly);
            services.AddJournalPayloads(typeof(TestFlow).Assembly);
            services.TryAddSingleton<ICapabilityConnections, EmptyCapabilityConnections>();
            services.TryAddSingleton<ICallFilter, CallFilter>();
            services.TryAddSingleton(TimeProvider.System);
            services.AddSingleton<IAppRuntime>(new NoAppRuntime());
            services.AddSingleton<IDrafter>(new ScriptedDrafter(source));
            services.AddSingleton<ISynapseHost>(host);
            services.AddSingleton<IScriptSandbox>(new RunnableSandbox());
            services.TryAddSingleton(IntoChat.OS.Connections.Contracts.NeuronTypeSearch.Create);
        }
    }

    private static IFlows Flows(INeuronRuntime runtime) => runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));

    private static string Unit(string goal) => JsonSerializer.Serialize(new BuildInner { Kind = BuildInner.Unit, Goal = goal }, BuildFlow.Json);

    private static async Task<FlowView> Eventually(Func<Task<FlowView?>> read, Func<FlowView, bool> done)
    {
        FlowView? view = null;
        for (var i = 0; i < 400; i++)
        {
            view = await read();
            if (view is not null && done(view)) { return view; }
            await Task.Delay(50, Ct);
        }
        Assert.Fail($"The flow did not reach the expected view; last: {view}");
        return view!;
    }

    private static async Task<BuildInner> Inner(INeuronRuntime runtime, string flowId)
    {
        Assert.True(NeuronId.TryParse(flowId, out var id));
        var head = await runtime.Get<IFlowStarter>(id).Head();
        return JsonSerializer.Deserialize<BuildInner>(head.InnerJson, BuildFlow.Json)!;
    }

    [Fact]
    public async Task BuildFlowDraftsChecksDeploysAndWaits()
    {
        var host = new RecordingHost();
        await using var fixture = new BuildFixture(SynapseStudioFacts.TickPoller, host);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var flows = Flows(runtime);

        var id = await flows.Start(BuildFlow.Alias, "Tick poller", null, Unit("Tick poller"), Ct);

        var view = await Eventually(() => flows.Read(id, Ct), v => v.Status == FlowStatus.Waiting);
        Assert.Equal(["draft", "check", "deploy"], view.Done);
        Assert.Equal(WaitReason.Approval, view.WaitingFor);
        Assert.Equal("approve", view.WaitingStep);
        var inner = await Inner(runtime, id);
        Assert.Equal("h1", inner.Hash);
        Assert.InRange(inner.ApprovalExpires!.Value - (view.Started + TimeSpan.FromDays(2)), TimeSpan.FromMinutes(-1), TimeSpan.FromMinutes(1));
        Assert.Equal(1, host.Completed);
    }

    [Fact]
    public async Task CompileErrorFailsAtCheck()
    {
        var host = new RecordingHost();
        await using var fixture = new BuildFixture("this is not C# {", host);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var flows = Flows(runtime);

        var id = await flows.Start(BuildFlow.Alias, "Broken", null, Unit("Broken"), Ct);

        var view = await Eventually(() => flows.Read(id, Ct), v => v.Status == FlowStatus.Failed);
        Assert.Equal(["draft"], view.Done);
        Assert.Contains("Does not compile", view.Detail, StringComparison.Ordinal);
        Assert.Contains(await runtime.Signals<FlowStep>(Id(id), Ct), step => step is { Step: "check", Outcome: StepOutcome.Failed } && step.Detail!.Contains("Does not compile", StringComparison.Ordinal));
        Assert.Equal(0, host.Completed);
    }

    [Fact]
    public async Task CrashBetweenCheckAndDeployResumesAtDeploy()
    {
        var host = new RecordingHost { Block = true };
        await using var fixture = new BuildFixture(SynapseStudioFacts.TickPoller, host);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);

        var id = await Flows(runtime).Start(BuildFlow.Alias, "Tick poller", null, Unit("Tick poller"), Ct);
        await host.Entered.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var before = await runtime.Get<IFlowStarter>(Id(id)).Head();
        Assert.Equal(["draft", "check"], before.Done);

        // The blocked deploy never returns: the restarted silo reruns the step, as after a crash.
        await runtime.Restart(Ct);

        // Reading the flow activates it; it reruns the interrupted deploy step and waits on the approval.
        FlowHead? head = null;
        for (var i = 0; i < 400 && head is not { Status: FlowStatus.Waiting or FlowStatus.Failed or FlowStatus.Completed or FlowStatus.Cancelled }; i++)
        {
            await Task.Delay(50, Ct);
            head = await runtime.Get<IFlowStarter>(Id(id)).Head();
        }
        Assert.True(head is { Status: FlowStatus.Waiting }, $"calls={host.Calls} {head?.Status} done=[{string.Join(",", head?.Done ?? [])}] steps={string.Join(" | ", (await runtime.Signals<FlowStep>(Id(id), Ct)).Select(s => $"{s.Step}:{s.Outcome}:{s.Detail}"))}");
        Assert.Equal(["draft", "check", "deploy"], head!.Done);
        Assert.Equal(1, host.Completed);
        Assert.Equal("h1", (await Inner(runtime, id)).Hash);
    }

    [Fact]
    public async Task MakeStartsABuildFlow()
    {
        var host = new RecordingHost();
        await using var fixture = new BuildFixture(SynapseStudioFacts.TickPoller, host);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);

        var result = await runtime.Get<IInoStudio>(runtime.Id("ino.studio", "ino/studio")).Make("Count ticks every five minutes");

        var card = Assert.IsType<InoCard>(result.Card);
        Assert.Equal(("approval", "pending"), (card.Kind, card.Status));
        var view = await Eventually(() => Flows(runtime).Read(card.Id, Ct), v => v.Status == FlowStatus.Waiting);
        Assert.Equal(BuildFlow.Alias, view.Type);
        Assert.Equal("Count ticks every five minutes", view.Title);
        Assert.Contains(card.Id, result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImplementBuildsAnAppThroughTheFlow()
    {
        await using var fixture = new BuildFixture("# notes", new RecordingHost());
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var appKey = IApp.Key(runtime.Brain.Value, "notes");
        var app = runtime.Get<IApp>(runtime.Id(IntoChat.OS.Connections.AppGrainType.Name, "apps/notes"));
        await app.Create(new AppManifest("Notes", "Keeps notes", [], [], []), "spec");

        await runtime.Services.GetRequiredService<Creator>().Implement(runtime.Services.GetRequiredService<INeuronProxies>(), appKey, "add notes", Ct);

        var flowId = Assert.IsType<string>(await app.BuildFlow());
        var view = await Eventually(() => Flows(runtime).Read(flowId, Ct), v => v.Status == FlowStatus.Waiting);
        Assert.Equal(["draft", "check", "deploy"], view.Done);
        Assert.Equal("approve", view.WaitingStep);
        var snapshot = await app.Read();
        Assert.Equal(AppStatus.Live, snapshot.Status);
        Assert.Equal("# notes", snapshot.Files["notes.md"]);
        Assert.Contains(snapshot.Activity, line => line.StartsWith("draft: ", StringComparison.Ordinal));
    }

    // While the app is Building, a failed build flow shows it Failed with the flow's detail.
    [Fact]
    public async Task AFailedBuildFlowShowsTheBuildingAppFailed()
    {
        await using var fixture = new BuildFixture("this is not C# {", new RecordingHost());
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var app = runtime.Get<IApp>(runtime.Id(IntoChat.OS.Connections.AppGrainType.Name, "apps/broken"));
        await app.Create(new AppManifest("Broken", "Never builds", [], [], []), "spec");
        await app.BeginBuild(null);
        var flows = Flows(runtime);
        var flowId = await flows.Start(BuildFlow.Alias, "Broken", null, Unit("Broken"), Ct);
        await Eventually(() => flows.Read(flowId, Ct), v => v.Status == FlowStatus.Failed);
        await app.BuildFlowStarted(flowId);

        var stored = await app.Read();
        var shown = await AppBuildStatus.Of(stored, app, flows, Ct);

        Assert.Equal(AppStatus.Building, stored.Status);
        Assert.Equal(AppStatus.Failed, shown.Status);
        Assert.Contains(shown.Activity, line => line.StartsWith("build failed: ", StringComparison.Ordinal) && line.Contains("Does not compile", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedFlowDoneFailsTheBuildingApp()
    {
        await using var fixture = new BuildFixture("# notes", new RecordingHost());
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var app = runtime.Get<IApp>(runtime.Id(IntoChat.OS.Connections.AppGrainType.Name, "apps/failing"));
        await app.Create(new AppManifest("Failing", "Fails", [], [], []), "spec");
        await app.BeginBuild(null);
        var flowId = runtime.Brain.Value + "/flow.build/failing";
        await app.BuildFlowStarted(flowId);

        await runtime.Get<IFlowSignalPump>(runtime.Id(FlowSignalPump.Alias, "pump")).Failed(flowId, "x");

        AppSnapshot snapshot = await app.Read();
        for (var i = 0; i < 200 && snapshot.Status == AppStatus.Building; i++) { await Task.Delay(50, Ct); snapshot = await app.Read(); }
        Assert.Equal(AppStatus.Failed, snapshot.Status);
        Assert.Equal("build failed: x", snapshot.Activity[^1]);
    }

    private static NeuronId Id(string flowId)
    {
        Assert.True(NeuronId.TryParse(flowId, out var id));
        return id;
    }
}
