using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
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

[DigitalBrain.Neuron(Alias), StatelessNeuron]
public sealed class PlanRaiser : DigitalBrain.Neuron, IPlanRaiser
{
    public const string Alias = "test.plan-raiser";

    public Task Raise(string planId, ConfirmationAction action, string title, string body, string target)
    {
        var plan = new ConfirmationPlan(planId, action, Destructive: action != ConfirmationAction.Other, title, body, "Allow", "Deny", target) { RequiresPerson = true };
        _ = Context.Confirm(plan, CancellationToken.None);
        return Task.CompletedTask;
    }
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
        protected override IReadOnlyList<Type> Modules => [typeof(AppsModule), typeof(FlowsFactsModule)];

        protected override void ConfigureSilo(IServiceCollection services)
        {
            services.AddJournalPayloads(typeof(IFlows).Assembly);
            services.AddJournalPayloads(typeof(AppsModule).Assembly);
            services.AddJournalPayloads(typeof(FlowStarted).Assembly);
            services.AddJournalPayloads(typeof(TestFlow).Assembly);
            services.TryAddSingleton(TimeProvider.System);
            services.AddSingleton<ISynapseHost, StagedHost>();
        }
    }

    private static IApprovals Approvals(INeuronRuntime runtime) => runtime.Get<IApprovals>(IApprovals.Key(runtime.Brain.Value));

    private static IFlows Flows(INeuronRuntime runtime) => runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));

    private static IPlanRaiser Raiser(INeuronRuntime runtime) => runtime.Get<IPlanRaiser>(new NeuronId(runtime.Brain, PlanRaiser.Alias, "raiser"));

    private static IDisposable AsPerson(INeuronRuntime runtime) => RuntimeCallerContext.Stamp(CapabilityCallers.Of("person", "person", runtime.Brain.Value));

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

    [Fact]
    public async Task DecideResolvesThePlan()
    {
        await using var fixture = new ApprovalsFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var flows = Flows(runtime);
        var flowId = await flows.Start(TestFlow.Alias, "Decide", null, Inner(), Ct);
        var pending = await Eventually(runtime, list => list.Any(a => a.Kind == ApprovalKind.FlowStep));
        var plan = pending.Single(a => a.Kind == ApprovalKind.FlowStep).PlanId;

        using (AsPerson(runtime))
        {
            await Approvals(runtime).Decide(plan, approve: true, note: null, Ct);
        }

        for (var i = 0; i < 200 && (await flows.Read(flowId, Ct))?.Status != FlowStatus.Completed; i++) { await Task.Delay(50, Ct); }
        Assert.Equal(FlowStatus.Completed, (await flows.Read(flowId, Ct))!.Status);
        using (AsPerson(runtime)) { Assert.Empty(await Approvals(runtime).Pending(Ct)); }
    }

    [Fact]
    public async Task DetailOfStagedSynapseIsTheSourceDiff()
    {
        await using var fixture = new ApprovalsFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        await Raiser(runtime).Raise("install:poller:2", ConfirmationAction.Other, "poller", Diff, "poller");

        var pending = await Eventually(runtime, list => list.Count == 1);

        var approval = Assert.Single(pending);
        Assert.Equal(ApprovalKind.SynapseStaged, approval.Kind);
        Assert.Equal(Diff, approval.Detail);
    }
}
