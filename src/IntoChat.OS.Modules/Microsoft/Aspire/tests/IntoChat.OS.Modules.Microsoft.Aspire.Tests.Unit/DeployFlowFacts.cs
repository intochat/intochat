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
    private static readonly BrainId Brain = new("local");

    private sealed class Host : IClusterHost
    {
        public string Current { get; set; } = "base000";
        public string[] InstanceNames { get; set; } = ["mcp-a", "silo-0", "silo-1"];
        public string? Self { get; set; } = "silo-1";
        public List<string> Restarted { get; } = [];
        // "name@sha": that instance never becomes ready on that build.
        public HashSet<string> NeverReady { get; } = [];

        public Task<string> CurrentSha(CancellationToken ct) => Task.FromResult(Current);
        public Task SetLive(string sha, CancellationToken ct) { Current = sha; return Task.CompletedTask; }
        public Task RestartSilos(CancellationToken ct) => Task.CompletedTask;
        public Task RestartInstance(string name, CancellationToken ct) { Restarted.Add($"{name}@{Current}"); return Task.CompletedTask; }
        public Task DrainInstance(string name, CancellationToken ct) => Task.CompletedTask;
        public Task WaitOutOfFront(string name, TimeSpan budget, CancellationToken ct) => Task.CompletedTask;
        public Task WaitInstance(string name, DateTimeOffset since, TimeSpan budget, CancellationToken ct)
            => NeverReady.Contains($"{name}@{Current}") ? Task.FromException(new InstanceNeverReadyException($"{name} not ready within {budget}")) : Task.CompletedTask;
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
    [DigitalBrain.Neuron("flows")]
    internal sealed class StartOnlyFlows : DigitalBrain.Neuron, IFlows
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

    private static IDeployment Deployment(TestOS os)
        => os.Get<IDeployment>(DigitalBrain.Contracts.PlainNeuronContracts.IdOf(typeof(IDeployment), IDeployment.Key(Brain.Value)));

    [Fact]
    public async Task OneStepPerSiloWithScheduleWaitBetween()
    {
        var host = new Host();
        await using var os = Os(host);
        var flow = os.Get<IFlowStarter>(new NeuronId(Brain, DeployFlow.Alias, "deploy-new111"));

        await flow.StartFromJson("Roll onto new111", "test", null, Inner("mcp-a", "silo-0", "silo-1"));
        await os.Idle();

        var head = await flow.Head();
        Assert.Equal(["roll:mcp-a", "roll:silo-0", "roll:silo-1", "handover"], head.Steps);
        Assert.Equal(FlowStatus.Waiting, head.Status);
        Assert.Equal(["mcp-a@new111"], host.Restarted);

        // The schedule wait holds the next instance until the minute passes, then the expiry reminder resumes the flow.
        os.Clock.Advance(DeployFlow.Pause - TimeSpan.FromSeconds(1));
        await os.Idle();
        Assert.Equal(["mcp-a@new111"], host.Restarted);
        os.Clock.Advance(TimeSpan.FromSeconds(1));
        await os.Idle();
        Assert.Equal(["mcp-a@new111", "silo-0@new111"], host.Restarted);

        await Minute(os);
        Assert.Equal(["mcp-a@new111", "silo-0@new111", "silo-1@new111"], host.Restarted);
        Assert.Equal(FlowStatus.Waiting, (await flow.Head()).Status);

        await Minute(os);
        head = await flow.Head();
        Assert.Equal(FlowStatus.Completed, head.Status);
        Assert.Equal(["roll:mcp-a", "roll:silo-0", "roll:silo-1", "handover"], head.Done);
    }

    [Fact]
    public async Task ARollStepRunAgainAfterItsOwnSilosRestartDoesNotRestartItTwice()
    {
        var host = new Host();
        var membership = new Membership { Current = new([new SiloView("silo-1", "10.0.0.1", "Active", null, "new111")], 2) };
        await using var os = Os(host, membership);
        var flow = os.Get<IFlowStarter>(new NeuronId(Brain, DeployFlow.Alias, "deploy-new111"));

        await flow.StartFromJson("Roll onto new111", "test", null, Inner("silo-1"));
        await os.Idle();
        await Minute(os);

        Assert.Empty(host.Restarted);
        Assert.Equal(FlowStatus.Completed, (await flow.Head()).Status);
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

        // silo-0 failed: a second flow goes back onto base000, the failed instance first, then the moved ones newest first.
        var back = await deployment.Read();
        Assert.Equal(DeploymentStage.RollingBack, back.Stage);
        Assert.Equal("base000", back.TargetSha);
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
        Assert.Equal(["mcp-a", "silo-0", "silo-1"], started.Remaining);
        await os.Idle();

        Assert.Equal(["silo-0", "silo-1"], (await deployment.Read()).Remaining);
        await Minute(os);
        Assert.Equal(["silo-1"], (await deployment.Read()).Remaining);
        await Minute(os);
        Assert.Empty((await deployment.Read()).Remaining);
        Assert.Equal(DeploymentStage.Rolling, (await deployment.Read()).Stage);

        await Minute(os);
        var done = await deployment.Read();
        Assert.Equal(DeploymentStage.Steady, done.Stage);
        Assert.Equal("new111", done.CurrentSha);
        Assert.Null(done.Failure);
    }
}
