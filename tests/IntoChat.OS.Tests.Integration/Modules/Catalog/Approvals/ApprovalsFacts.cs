using IntoChat.OS.Tests;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Limits;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Tests.Integration.Flows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Approvals;

// Raises confirmation plans as the storage gate and the installs neuron do.
public interface IPlanRaiser
{
    Task Raise(string planId, ConfirmationAction action, string title, string body, string target);
}

[DigitalBrain.Neurons.Neuron(Alias), StatelessNeuron]
public sealed class PlanRaiser(IConfirmations? confirmations = null) : DigitalBrain.Neurons.Neuron, IPlanRaiser
{
    public const string Alias = "test.plan-raiser";

    public Task Raise(string planId, ConfirmationAction action, string title, string body, string target)
    {
        var plan = new ConfirmationPlan(planId, action, Destructive: action != ConfirmationAction.Other, title, body, "Allow", "Deny", target) { RequiresPerson = true };
        _ = (confirmations ?? throw new InvalidOperationException("No confirmations")).Confirm(plan, CancellationToken.None);
        return Task.CompletedTask;
    }
}

public interface IPlanResolver
{
    Task Resolve(string planId, bool approve);
    Task<IReadOnlyList<Approval>> ReadPending();
    Task<IReadOnlyList<Approval>> ReadPendingOf(string brainId);
}

// A neuron answering a plan through the inbox, as a flow step or a synapse would.
[DigitalBrain.Neurons.Neuron(Alias), StatelessNeuron]
public sealed class PlanResolver : DigitalBrain.Neurons.Neuron, IPlanResolver
{
    public const string Alias = "test.plan-resolver";

    public Task Resolve(string planId, bool approve)
        => Context.Get<IConfirmationInbox>(new NeuronId(Id.Brain, "runtime.confirmations", IConfirmations.ConfirmationsNeuronKey)).Resolve(planId, approve);

    public Task<IReadOnlyList<Approval>> ReadPending()
        => Context.Get<IApprovals>(IApprovals.Key(Id.Brain.Value)).Pending();

    public Task<IReadOnlyList<Approval>> ReadPendingOf(string brainId)
        => Context.Get<IApprovals>(IApprovals.Key(brainId)).Pending();
}

// Stands in for the silo's synapse host with one staged source.
internal sealed class StagedHost : ISynapseHost
{
    public Task<DeployOutcome> Deploy(BrainId brain, SynapseSource source, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> bindingsPerAlias, CancellationToken ct)
        => throw new NotSupportedException();

    public Task<DeployOutcome> Retire(BrainId brain, string sourceKey, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<DeployedSource>> List(BrainId brain, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<DeployedSource>>([new DeployedSource("poller.cs", "h1", ["poller"], DeployStatus.Staged, "h2", DateTimeOffset.UnixEpoch)]);
}

// The approval list on a real silo: flow waits, staged synapses and storage actions arrive through the one confirmation inbox.
public sealed class ApprovalsFacts
{
    private const string Diff = "poller now needs the calendar; it needed nothing before.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ApprovalsFixture : BothRuntimesFixture
    {
        protected override IReadOnlyList<Type> Modules => [.. CatalogFamily.Modules, typeof(FlowsFactsModule)];

        protected override void ConfigureSilo(IServiceCollection services)
        {
            services.AddJournalPayloads(typeof(IFlows).Assembly);
            services.AddJournalPayloads(typeof(AppsModule).Assembly);
            services.AddJournalPayloads(typeof(IntoChat.OS.Modules.AI.Agents.Context.Signals.ContextUpdated).Assembly);
            services.AddJournalPayloads(typeof(FlowStarted).Assembly);
            services.AddJournalPayloads(typeof(TestFlow).Assembly);
            services.TryAddSingleton(TimeProvider.System);
            services.AddSingleton<ISynapseHost, StagedHost>();
        }
    }

    private static IApprovals Approvals(INeuronRuntime runtime) => runtime.Get<IApprovals>(IApprovals.Key(runtime.Brain.Value));

    private static IFlows Flows(INeuronRuntime runtime) => runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));

    private static IPlanRaiser Raiser(INeuronRuntime runtime) => runtime.Get<IPlanRaiser>(new NeuronId(runtime.Brain, PlanRaiser.Alias, "raiser"));

    private static IDisposable AsPerson(INeuronRuntime runtime) => runtime.Edge.Enter(runtime.Edge.Person("person", "person", runtime.Brain));

    private static string Inner() => JsonSerializer.Serialize(new TestInner { WaitApproval = true }, TestFlow.Json);

    private static async Task<IReadOnlyList<Approval>> Eventually(INeuronRuntime runtime, Func<IReadOnlyList<Approval>, bool> done)
    {
        IReadOnlyList<Approval> pending = [];
        for (var i = 0; i < 200; i++)
        {
            using (AsPerson(runtime)) { pending = await Approvals(runtime).Pending(Ct); }
            if (done(pending)) { return pending; }
            await Task.Delay(50, Ct);
        }
        Assert.Fail($"The approvals did not reach the expected list; last: {string.Join(", ", pending.Select(a => a.Kind + ":" + a.Subject))}");
        return pending;
    }

    [Fact]
    public async Task PendingShowsFlowWaitStagedSynapseAndStorageWriteFromOneInbox()
    {
        await using var fixture = new ApprovalsFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);
        var flowId = await Flows(runtime).Start(TestFlow.Alias, "Quarterly report", null, Inner(), Ct);
        await Raiser(runtime).Raise("install:poller:1", ConfirmationAction.Other, "poller", Diff, "poller");
        await Raiser(runtime).Raise("delete:1", ConfirmationAction.Delete, "Delete report.pdf?", "It cannot be undone.", "report.pdf");

        var pending = await Eventually(runtime, list => list.Count == 3 && list.Any(a => a.Kind == ApprovalKind.FlowStep));

        var flow = Assert.Single(pending, a => a.Kind == ApprovalKind.FlowStep);
        Assert.Equal(flowId, flow.Subject);
        Assert.Equal("Quarterly report", flow.Title);
        Assert.StartsWith("second: ", flow.Detail, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(flow.TraceId));
        var synapse = Assert.Single(pending, a => a.Kind == ApprovalKind.SynapseStaged);
        Assert.Equal("poller.cs", synapse.Subject);
        var storage = Assert.Single(pending, a => a.Kind == ApprovalKind.StorageWrite);
        Assert.Equal("report.pdf", storage.Subject);
        Assert.All(pending, a => Assert.True(a.Expires > a.Requested));
    }

    private static async Task<FlowStatus> EndOf(IFlows flows, string flowId)
    {
        for (var i = 0; i < 200 && (await flows.Read(flowId, Ct))?.Status is FlowStatus.Waiting or FlowStatus.Running or FlowStatus.Pending; i++) { await Task.Delay(50, Ct); }
        return (await flows.Read(flowId, Ct))!.Status;
    }

    [Fact]
    public async Task ResolvingThroughTheInboxEdgeRemovesThePending()
    {
        await using var fixture = new ApprovalsFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);
        var flows = Flows(runtime);
        var flowId = await flows.Start(TestFlow.Alias, "Edge", null, Inner(), Ct);
        var pending = await Eventually(runtime, list => list.Any(a => a.Kind == ApprovalKind.FlowStep));

        await runtime.ApproveAsPerson(pending.Single(a => a.Kind == ApprovalKind.FlowStep).PlanId, Ct);

        Assert.Equal(FlowStatus.Completed, await EndOf(flows, flowId));
        using (AsPerson(runtime)) { Assert.Empty(await Approvals(runtime).Pending(Ct)); }
    }

    [Fact]
    public async Task ANeuronCannotResolveAPlanThroughTheInbox()
    {
        await using var fixture = new ApprovalsFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);
        var flows = Flows(runtime);
        var flowId = await flows.Start(TestFlow.Alias, "Self", null, Inner(), Ct);
        var pending = await Eventually(runtime, list => list.Any(a => a.Kind == ApprovalKind.FlowStep));
        var planId = pending.Single(a => a.Kind == ApprovalKind.FlowStep).PlanId;
        var resolver = runtime.Get<IPlanResolver>(new NeuronId(runtime.Brain, PlanResolver.Alias, "resolver"));

        using (AsPerson(runtime))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => resolver.Resolve(planId, approve: true));
        }

        // The forged neuron resolution is rejected without poisoning the human's pending plan
        Assert.Equal(FlowStatus.Waiting, (await flows.Read(flowId, Ct))!.Status);
        using (AsPerson(runtime))
        {
            Assert.Contains(await Approvals(runtime).Pending(Ct), a => a.PlanId == planId);
        }

        await flows.Cancel(flowId, "done", Ct);
    }

    [Fact]
    public async Task UnstampedAndForeignBrainCallersCannotReadPending()
    {
        await using var fixture = new ApprovalsFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using (AsPerson(runtime)) { await Raiser(runtime).Raise("delete:2", ConfirmationAction.Delete, "Delete a.txt?", "Gone.", "a.txt"); }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Approvals(runtime).Pending(Ct));
        using (runtime.Edge.Enter(runtime.Edge.Person("person", "person", new BrainId("another-brain"))))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Approvals(runtime).Pending(Ct));
        }

        // Same-brain neuron callers (e.g., InoApp during an async [On] reaction with no HTTP RuntimeCallerContext) can read pending approvals.
        _ = await Eventually(runtime, list => list.Count == 1);
        // A reaction runs as the scheduler of its brain (activation and deferred work, task 7); it reaches the reader neuron as that.
        using var reaction = runtime.Edge.Enter(runtime.Edge.Scheduler(runtime.Brain));
        var fromSameBrainNeuron = await runtime.Get<IPlanResolver>(new NeuronId(runtime.Brain, PlanResolver.Alias, "reader")).ReadPending();
        Assert.Single(fromSameBrainNeuron, a => a.PlanId == "delete:2");
        var foreignNeuron = runtime.Get<IPlanResolver>(new NeuronId(new BrainId("another-brain"), PlanResolver.Alias, "reader"));
        using var foreignReaction = runtime.Edge.Enter(runtime.Edge.Scheduler(new BrainId("another-brain")));
        await Assert.ThrowsAsync<ArgumentException>(() => foreignNeuron.ReadPendingOf(runtime.Brain.Value));
    }

    [Fact]
    public async Task UnmatchedPlanIsOther()
    {
        await using var fixture = new ApprovalsFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);
        await Raiser(runtime).Raise("other:1", ConfirmationAction.Other, "Do a thing", "Really.", "thing");

        var approval = Assert.Single(await Eventually(runtime, list => list.Count == 1));

        Assert.Equal(ApprovalKind.Other, approval.Kind);
        Assert.Equal("thing", approval.Subject);
    }

    [Fact]
    public async Task GrantPlanIsGrant()
    {
        await using var fixture = new ApprovalsFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);
        await Raiser(runtime).Raise("grant:1", ConfirmationAction.Other, "Allow calendar", "Read your calendar.", "grant:calendar.read");

        var approval = Assert.Single(await Eventually(runtime, list => list.Count == 1));

        Assert.Equal(ApprovalKind.Grant, approval.Kind);
        Assert.Equal("calendar.read", approval.Subject);
    }

    [Fact]
    public async Task DetailOfStagedSynapseIsTheSourceDiff()
    {
        await using var fixture = new ApprovalsFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);
        await Raiser(runtime).Raise("install:poller:2", ConfirmationAction.Other, "poller", Diff, "poller");

        var pending = await Eventually(runtime, list => list.Count == 1);

        var approval = Assert.Single(pending);
        Assert.Equal(ApprovalKind.SynapseStaged, approval.Kind);
        Assert.Equal(Diff, approval.Detail);
    }
}
