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

    // Answers every draft with one file of Source.
    private sealed class ScriptedDrafter(string source) : IDrafter
    {
        public Task<BuildDraft> Draft(BuildRequest request, CancellationToken ct)
            => Task.FromResult(new BuildDraft(new Dictionary<string, string>(StringComparer.Ordinal) { ["unit.cs"] = source }, null));
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
        protected override IReadOnlyList<Type> Modules => [typeof(AppsModule), typeof(FlutterModule)];

        protected override void ConfigureSilo(IServiceCollection services)
        {
            AppsModule.ConfigureServices(services, new ConfigurationBuilder().Build());
            services.AddJournalPayloads(typeof(IApp).Assembly);
            services.AddJournalPayloads(typeof(AppsModule).Assembly);
            services.AddJournalPayloads(typeof(FlutterModule).Assembly);
            services.AddJournalPayloads(typeof(IntoChat.OS.Modules.Flutter.Views.IView).Assembly);
            services.AddJournalPayloads(typeof(DigitalBrain.Signals.Activated).Assembly);
            services.AddJournalPayloads(typeof(FlowStarted).Assembly);
            services.TryAddSingleton<ICapabilityConnections, EmptyCapabilityConnections>();
            services.TryAddSingleton<ICallFilter, CallFilter>();
            services.TryAddSingleton(TimeProvider.System);
            services.AddSingleton<IAppRuntime>(new NoAppRuntime());
            services.AddSingleton<IDrafter>(new ScriptedDrafter(source));
            services.AddSingleton<ISynapseHost>(host);
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
        var head = await runtime.Get<IBuildFlow>(id).Head();
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
        var before = await runtime.Get<IBuildFlow>(Id(id)).Head();
        Assert.Equal(["draft", "check"], before.Done);

        // The blocked deploy never returns: the restarted silo reruns the step, as after a crash.
        await runtime.Restart(Ct);

        // Reading the flow activates it; it reruns the interrupted deploy step and waits on the approval.
        FlowHead? head = null;
        for (var i = 0; i < 400 && head is not { Status: FlowStatus.Waiting or FlowStatus.Failed or FlowStatus.Completed or FlowStatus.Cancelled }; i++)
        {
            await Task.Delay(50, Ct);
            head = await runtime.Get<IBuildFlow>(Id(id)).Head();
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

    private static NeuronId Id(string flowId)
    {
        Assert.True(NeuronId.TryParse(flowId, out var id));
        return id;
    }
}
