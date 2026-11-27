using DigitalBrain.Confirmation;
using DigitalBrain.Neurons;
using DigitalBrain.Operations;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DigitalBrain.Tests.Unit.Operations;

// After a restart an approved operation is settled twice, by Reconcile on activation and by the ConfirmationResolved signal.
// The handler must still run once: a second run (without the payload) used to fail and report its failure first.
public sealed class OperationSettleOnceFacts
{
    public sealed record CountedPlan() : OperationPlan(false);

    private sealed class CountingHandler : IOperationHandler<CountedPlan>
    {
        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        // A real run is detached and slow: the operation stays Running while the second settle arrives.
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<OperationApproval?> Describe(CountedPlan plan, OperationScope scope, CancellationToken ct) => Task.FromResult<OperationApproval?>(null);

        public async Task<OperationResult> Run(CountedPlan plan, IOperationNeuron operation, OperationScope scope, CancellationToken ct)
        {
            Interlocked.Increment(ref _runs);
            await Finish.Task;
            return new OperationResult.Succeeded();
        }
    }

    // Holds every Outcome until released, so both settle paths are past their phase check before either settles.
    private sealed class ApprovedRequests : IConfirmationRequests
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ConfirmationResult?> Request(ConfirmationPlan plan) => Task.FromResult<ConfirmationResult?>(null);
        public async Task<ConfirmationResult?> Outcome(string planId)
        {
            await Gate.Task;
            return new ConfirmationResult(planId, ConfirmationOutcome.Approved, ConfirmationDenyReason.None, false);
        }
        public Task Withdraw(string planId) => Task.CompletedTask;
    }

    [Fact]
    public async Task ReconcileThenTheResolvedSignalRunTheHandlerOnce()
    {
        var handler = new CountingHandler();
        var services = new ServiceCollection().AddSingleton(new OperationHandlers().Register(handler)).BuildServiceProvider();
        var brain = new BrainId("b1");
        var context = new FakeNeuronContext(new NeuronId(brain, "runtime.operation", "op-1"), services);
        var requests = new ApprovedRequests();
        context.Proxies.Register<IConfirmationRequests>(IConfirmationRequests.Of(brain), requests);
        var neuron = new OperationNeuron();
        NeuronAttachment.Attach(neuron, context);
        context.Proxies.Register<IOperationNeuron>(context.Id, neuron);
        await neuron.Begin(new OperationState("op-1", "b1", "origin", nameof(CountedPlan), "{}", null, null, OperationPhase.AwaitingApproval,
            false, 0d, null, context.Time.GetUtcNow(), context.Time.GetUtcNow(), ApprovalPlanId: "cf-1"));

        var reconciled = neuron.Reconcile();
        var resolved = neuron.OnResolved(new ConfirmationResolved("cf-1", ConfirmationAction.Other, "target", ConfirmationPrincipal.App("requester"), null,
            ConfirmationPolicy.Always, ConfirmationOutcome.Approved, ConfirmationDenyReason.None, false, context.Time.GetUtcNow()));
        requests.Gate.SetResult();
        await Task.WhenAll(reconciled, resolved);
        var runs = handler.Runs;
        handler.Finish.SetResult();

        Assert.True(runs == 1, $"runs={runs}");
    }
}
