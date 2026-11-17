using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Flows;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Microsoft.Aspire.Deployment;
using IntoChat.OS.Modules.Microsoft.Orleans;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Microsoft.Aspire.Tests.Unit;

// The roll as a DeployFlow on TestOS: one step per instance with a one-minute schedule wait between them, a second flow going back
// when a step fails, and the deployment folding both flows' signals into its state.
public sealed class DeployFlowFacts
{
    private static readonly BrainId Brain = new("owner");

    private sealed class Host : IClusterHost
    {
        public string Current { get; set; } = "base000";
        public string[] InstanceNames { get; set; } = ["mcp-a", "silo-0", "silo-1"];
        public string? Self { get; set; } = "silo-1";
        public List<string> Restarted { get; } = [];
        public List<string> Waited { get; } = [];
        // "name@sha": that instance never becomes ready on that build.
        public HashSet<string> NeverReady { get; } = [];

        public Task<string> CurrentSha(CancellationToken ct) => Task.FromResult(Current);
        public Task SetLive(string sha, CancellationToken ct) { Current = sha; return Task.CompletedTask; }
        public Task RestartSilos(CancellationToken ct) => Task.CompletedTask;
        // When each instance's current process started; a restart starts a new one just after now.
        public Dictionary<string, DateTimeOffset> Started { get; } = [];
        public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;
        public DateTimeOffset SelfStarted => Self is { } self ? Started.GetValueOrDefault(self, DateTimeOffset.MinValue) : DateTimeOffset.MinValue;
        public Task<DateTimeOffset?> StartedAt(string name, CancellationToken ct) => Task.FromResult<DateTimeOffset?>(Started.GetValueOrDefault(name, DateTimeOffset.MinValue));
        // An instance in this set ignores its restart, as a detached restart that never ran.
        public HashSet<string> NeverRestarts { get; } = [];
        public HashSet<string> CutOffAfter { get; } = [];
        public Task RestartInstance(string name, CancellationToken ct)
        {
            Restarted.Add($"{name}@{Current}");
            if (!NeverRestarts.Contains(name)) { Started[name] = Now().AddTicks(1); }
            // The restart went out and the process running the step stopped before the step could save.
            if (CutOffAfter.Remove(name)) { throw new OperationCanceledException($"cut off after restarting {name}"); }
            return Task.CompletedTask;
        }
        public Task DrainInstance(string name, CancellationToken ct) => Task.CompletedTask;
        public Task WaitOutOfFront(string name, TimeSpan budget, CancellationToken ct) => Task.CompletedTask;
        public Task WaitInstance(string name, DateTimeOffset since, TimeSpan budget, CancellationToken ct)
            => Record($"{name}@{Current}") && NeverReady.Contains($"{name}@{Current}") ? Task.FromException(new InstanceNeverReadyException($"{name} not ready within {budget}")) : Task.CompletedTask;
        private bool Record(string entry) { Waited.Add(entry); return true; }
        public Task<string[]> Instances(CancellationToken ct) => Task.FromResult(InstanceNames);
        public Task<int> Spans(string service, string span, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class Membership : IClusterMembershipFeed
    {
        public ClusterSnapshot Current { get; set; } = new([], 1);
        public IAsyncDisposable Subscribe(Func<ClusterSnapshot?, Task> changed) => new Nothing();
        private sealed class Nothing : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }

    // The brain's flow index, reduced to Start by alias and key.
    [DigitalBrain.Neurons.Neuron("flows")]
    internal sealed class StartOnlyFlows : DigitalBrain.Neurons.Neuron, IFlows
    {
        public async Task<string> Start(string type, string title, string? parentFlowId, string innerJson, CancellationToken ct = default, string? flowId = null)
        {
            var id = new NeuronId(Id.Brain, type, flowId ?? Guid.NewGuid().ToString("N"));
            await Context.Get<IFlowStarter>(id).StartFromJson(title, "test", parentFlowId, innerJson);
            return id.ToString();
        }

        public Task<FlowView[]> List(FlowQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FlowView?> Read(string flowId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task Resume(string flowId, string step, WaitOutcome outcome, CancellationToken ct = default) => throw new NotSupportedException();
        public Task Cancel(string flowId, string reason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task Relate(string flowId, FlowRelation relation, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static TestOS Os(Host host, Membership? membership = null)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IClusterHost>(host)
            .AddSingleton<IClusterMembershipFeed>(membership ?? new Membership());
        services.AddOptions<AspireOptions>();
        var provider = services.BuildServiceProvider();
        var os = TestOS.Local(options => options.Services = provider);
        host.Now = os.Clock.GetUtcNow;
        os.Register<DeployFlow>();
        os.Register<DeploymentNeuron>();
        os.Register<StartOnlyFlows>();
        return os;
    }

    private static string Inner(params string[] remaining)
        => JsonSerializer.Serialize(new DeployInner { TargetSha = "new111", PreviousSha = "base000", Remaining = remaining }, DeployFlow.Json);

    private static async Task Minute(TestOS os)
    {
        os.Clock.Advance(DeployFlow.Pause);
        await os.Idle();
    }

    private static NeuronId DeploymentId => DigitalBrain.Contracts.PlainNeuronContracts.IdOf(typeof(IDeployment), IDeployment.Key(Brain.Value));

    private static IDeployment Deployment(TestOS os) => os.Get<IDeployment>(DeploymentId);

    private static readonly string[] Rolled =
        ["order", "roll:mcp-a:mark", "roll:mcp-a:restart", "roll:mcp-a:ready", "roll:silo-0:mark", "roll:silo-0:restart", "roll:silo-0:ready",
         "roll:silo-1:mark", "roll:silo-1:restart", "roll:silo-1:ready", "handover"];

    [Fact]
    public async Task OneStepPerSiloWithScheduleWaitBetween()
    {
        var host = new Host();
        await using var os = Os(host);
        var flow = os.Get<IFlowStarter>(new NeuronId(Brain, DeployFlow.Alias, "deploy-new111"));

        // Listed with the flow's own silo first: the order step moves it last.
        await flow.StartFromJson("Roll onto new111", "test", null, Inner("silo-1", "mcp-a", "silo-0"));
        await os.Idle();

        var head = await flow.Head();
        Assert.Equal(Rolled, head.Steps);
        Assert.Equal(FlowStatus.Waiting, head.Status);
        Assert.Equal(["mcp-a@new111"], host.Restarted);

        // The schedule wait after each restart holds the readiness check and the next instance until the minute passes.
        os.Clock.Advance(DeployFlow.Pause - TimeSpan.FromSeconds(1));
        await os.Idle();
        Assert.Equal(["mcp-a@new111"], host.Restarted);
        os.Clock.Advance(TimeSpan.FromSeconds(1));
        await os.Idle();
        Assert.Equal(["mcp-a@new111", "silo-0@new111"], host.Restarted);
        Assert.Contains("mcp-a@new111", host.Waited);

        await Minute(os);
        Assert.Equal(["mcp-a@new111", "silo-0@new111", "silo-1@new111"], host.Restarted);
        Assert.Equal(FlowStatus.Waiting, (await flow.Head()).Status);

        await Minute(os);
        head = await flow.Head();
        Assert.Equal(FlowStatus.Completed, head.Status);
        Assert.Equal(Rolled, head.Done);
        // The flow's own silo is ready by running the step; it is never waited for from itself.
        Assert.DoesNotContain("silo-1@new111", host.Waited);
    }

    [Fact]
    public async Task AnInterruptedRestartStepDoesNotRestartTwice()
    {
        var host = new Host();
        host.CutOffAfter.Add("silo-1");
        await using var os = Os(host);
        var flow = os.Get<IFlowStarter>(new NeuronId(Brain, DeployFlow.Alias, "deploy-new111"));

        // The mark is saved by its own step; the restart step is cut off after issuing the restart, before it saves.
        await flow.StartFromJson("Roll onto new111", "test", null, Inner("silo-1"));
        await os.Idle();
        var head = await flow.Head();
        Assert.Equal(["silo-1@new111"], host.Restarted);
        Assert.Contains("roll:silo-1:mark", head.Done);
        Assert.DoesNotContain("roll:silo-1:restart", head.Done);

        // The nudge runs the restart step again on the restarted process (started after the mark): nothing is issued twice.
        await Minute(os);
        await Minute(os);
        Assert.Equal(["silo-1@new111"], host.Restarted);
        Assert.Equal(FlowStatus.Completed, (await flow.Head()).Status);
    }

    [Fact]
    public async Task AReRunRestartStepAfterTheMarkIssuesNothingForAnInstanceAlreadyRestarted()
    {
        var host = new Host();
        await using var os = Os(host);
        var flow = os.Get<IFlowStarter>(new NeuronId(Brain, DeployFlow.Alias, "deploy-new111"));
        // silo-0 restarted after the mark: its process started later than the marker's time.
        host.Started["silo-0"] = os.Clock.GetUtcNow().AddSeconds(1);

        await flow.StartFromJson("Roll onto new111", "test", null, Inner("silo-0"));
        await os.Idle();
        await Minute(os);

        Assert.Empty(host.Restarted);
        Assert.Equal(FlowStatus.Completed, (await flow.Head()).Status);
    }

    [Fact]
    public async Task SelfReadyRequiresAProcessStartedAfterTheMarker()
    {
        var host = new Host();
        host.NeverRestarts.Add("silo-1");
        await using var os = Os(host);
        var flow = os.Get<IFlowStarter>(new NeuronId(Brain, DeployFlow.Alias, "deploy-new111"));

        await flow.StartFromJson("Roll onto new111", "test", null, Inner("silo-1"));
        await os.Idle();
        await Minute(os);
        // The detached restart never ran: the old process is not ready, and the step is retried while the budget lasts.
        Assert.Equal(FlowStatus.Running, (await flow.Head()).Status);

        os.Clock.Advance(new AspireOptions().SiloStepBudget);
        await os.Idle();
        var head = await flow.Head();
        Assert.Equal(FlowStatus.Failed, head.Status);
        Assert.DoesNotContain("roll:silo-1:ready", head.Done);
    }

    [Fact]
    public async Task RollbackStepOnFailure()
    {
        var host = new Host();
        host.NeverReady.Add("silo-0@new111");
        await using var os = Os(host);
        var deployment = Deployment(os);
        Assert.Equal("base000", (await deployment.Read()).CurrentSha);

        await deployment.Roll("new111");
        await os.Idle();
        await Minute(os);
        await Minute(os);

        // silo-0's ready step failed: a second flow goes back onto base000, the failed instance first, then the moved ones newest first.
        var back = await deployment.Read();
        Assert.Equal(DeploymentStage.RollingBack, back.Stage);
        Assert.Equal("base000", back.TargetSha);
        Assert.Equal(["silo-0", "mcp-a"], back.Remaining);
        Assert.Contains("silo-0 never became ready on new111", back.Failure, StringComparison.Ordinal);
        Assert.Equal(["mcp-a@new111", "silo-0@new111", "silo-0@base000"], host.Restarted);

        await Minute(os);
        await Minute(os);
        var steady = await deployment.Read();
        Assert.Equal(DeploymentStage.Steady, steady.Stage);
        Assert.Equal("base000", steady.CurrentSha);
        Assert.Equal(["mcp-a@new111", "silo-0@new111", "silo-0@base000", "mcp-a@base000"], host.Restarted);
        Assert.Contains("silo-0 never became ready on new111", steady.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeploymentStateFoldsFromFlowSignals()
    {
        var host = new Host();
        await using var os = Os(host);
        var deployment = Deployment(os);
        await deployment.Read();

        var started = await deployment.Roll("new111");
        Assert.Equal(DeploymentStage.Rolling, started.Stage);
        await os.Idle();
        Assert.Equal(["mcp-a", "silo-0", "silo-1"], (await deployment.Read()).Remaining);

        await Minute(os);
        Assert.Equal(["silo-0", "silo-1"], (await deployment.Read()).Remaining);
        await Minute(os);
        Assert.Equal(["silo-1"], (await deployment.Read()).Remaining);
        Assert.Equal(DeploymentStage.Rolling, (await deployment.Read()).Stage);

        await Minute(os);
        var done = await deployment.Read();
        Assert.Equal(DeploymentStage.Steady, done.Stage);
        Assert.Equal("new111", done.CurrentSha);
        Assert.Empty(done.Remaining);
        Assert.Null(done.Failure);
    }

    [Fact]
    public async Task LegacyRollInProgressIsCompletedOnActivation()
    {
        var host = new Host { Current = "new111" };
        await using var os = Os(host);
        // What the reminder loop left after its own silo's handover: Rolling, nothing remaining, no flow.
        Seed(os, new AspireDeploymentState { CurrentSha = "base000", Stage = DeploymentStage.Rolling, TargetSha = "new111", PreviousSha = "base000", Rolled = ["mcp-a", "silo-0", "silo-1"], Handover = "silo-1" });

        var read = await Deployment(os).Read();

        Assert.Equal(DeploymentStage.Steady, read.Stage);
        Assert.Equal("new111", read.CurrentSha);
        Assert.Null(read.Failure);
    }

    [Fact]
    public async Task ALegacyRollCutOffBeforeItsHandoverEndsInterrupted()
    {
        var host = new Host { Current = "new111" };
        await using var os = Os(host);
        Seed(os, new AspireDeploymentState { CurrentSha = "base000", Stage = DeploymentStage.Rolling, TargetSha = "new111", PreviousSha = "base000", Remaining = ["silo-1"], Rolled = ["mcp-a", "silo-0"] });

        var read = await Deployment(os).Read();

        Assert.Equal(DeploymentStage.Steady, read.Stage);
        Assert.Equal("the roll was interrupted", read.Failure);
    }

    private static void Seed(TestOS os, AspireDeploymentState state)
        => os.SeedStoredState(DeploymentId, JsonSerializer.Serialize(state, DigitalBrain.Invocation.RuntimeProtocol.OptionsFor(typeof(AspireDeploymentState))));
}
