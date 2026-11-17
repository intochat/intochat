using DigitalBrain.Confirmation;
using DigitalBrain.Contracts;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;

namespace DigitalBrain.Operations;

/// <summary>
/// Durable operation neuron hosted identically across runtimes.
/// Keyed by operation id under brain/runtime.operation/id.
/// </summary>
[Neuron("runtime.operation")]
public sealed class OperationNeuron : Neuron<OperationState>, IOperationNeuron, IDisposable
{
    public const string NeuronType = "runtime.operation";

    private CancellationTokenSource? _runningCts;

    public void Dispose()
    {
        _runningCts?.Dispose();
    }

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await Reconcile().ConfigureAwait(true);
    }

    public async Task Reconcile()
    {
        if (State.Phase != OperationPhase.Done)
        {
            var driverLifetime = Seat.Services.GetService(typeof(IOperationDriverLifetime)) as IOperationDriverLifetime;
            bool? ended = null;
            if (State.DriverLifetime is { } token)
            {
                ended = driverLifetime?.HasEnded(token);
            }
            if (ended is null && State.Driver is { } driver)
            {
                ended = CheckClusterMembershipFallback(Seat.Services, driver);
            }
            if (ended == true)
            {
                await Complete(new OperationResult.Failed("interrupted", "The process running this operation stopped. Inspect the destination before retrying.")).ConfigureAwait(true);
                return;
            }
        }

        if (State.Phase == OperationPhase.AwaitingApproval && State.ApprovalPlanId is { } planId)
        {
            var outcome = await Context.Get<IConfirmationRequests>(IConfirmationRequests.Of(Id.Brain)).Outcome(planId).ConfigureAwait(true);
            if (outcome is not null)
            {
                await Settle(outcome).ConfigureAwait(true);
                return;
            }

            await Context.Synapses.Ensure<ConfirmationResolved>(IConfirmationRequests.Of(Id.Brain)).ConfigureAwait(true);
            return;
        }
    }

    public async Task<OperationState> Begin(OperationState initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        if (State.Exists)
        {
            return State;
        }

        if (initial.Id != Id.Key)
        {
            throw new ArgumentException($"Operation {initial.Id} does not belong to entity {Id}.", nameof(initial));
        }

        var signals = new Signal[] { new OperationStarted(new OperationId(initial.Id), initial.Kind, initial.Origin) };
        await Save(initial, signals).ConfigureAwait(true);
        return State;
    }

    public async Task Prepared(OperationApproval? approval)
    {
        if (State.Phase != OperationPhase.Running)
        {
            return;
        }

        if (approval is null)
        {
            Drive();
            return;
        }

        if (await Apply("operation.await-approval", State.AwaitApproval(approval.Question.Id, approval.Guard, Context.Time.GetUtcNow())).ConfigureAwait(true))
        {
            await Context.Synapses.Ensure<ConfirmationResolved>(IConfirmationRequests.Of(Id.Brain)).ConfigureAwait(true);
            await Context.Get<IConfirmationRequests>(IConfirmationRequests.Of(Id.Brain)).Request(approval.Question).ConfigureAwait(true);
        }
    }

    [On]
    public async Task OnResolved(ConfirmationResolved resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        if (State.Phase != OperationPhase.AwaitingApproval || !string.Equals(resolved.PlanId, State.ApprovalPlanId, StringComparison.Ordinal))
        {
            return;
        }

        var outcome = await Context.Get<IConfirmationRequests>(IConfirmationRequests.Of(Id.Brain)).Outcome(resolved.PlanId).ConfigureAwait(true);
        if (outcome is not null)
        {
            await Settle(outcome).ConfigureAwait(true);
        }
    }

    private async Task Settle(ConfirmationResult outcome)
    {
        var moved = await Apply("operation.answer", State.Answer(outcome, Context.Time.GetUtcNow())).ConfigureAwait(true);

#pragma warning disable CA1031
        try
        {
            await Context.Synapses.Remove(IConfirmationRequests.Of(Id.Brain)).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            GetLogger().LogSwallowed(error, "Unsubscribing settled operation from confirmation requests failed.");
        }
#pragma warning restore CA1031

        // Only the answer that moved the operation out of AwaitingApproval may drive it: after a restart both Reconcile and the
        // ConfirmationResolved signal settle the same plan, and a second Drive ran the handler twice.
        if (!moved)
        {
            return;
        }

        if (State.Stage.Value is OperationStages.Running)
        {
            Drive();
        }
        else if (State.Stage.Value is OperationStages.Done { Result: var result })
        {
            OperationPlanRegistry.Remove(State.Id);
            if ((Seat.Services.GetService(typeof(OperationRegistry)) as OperationRegistry)?.TryGet(new OperationId(State.Id), out var handle) == true)
            {
                handle.NotifyDone(result);
            }
        }
    }

    public async Task<bool> Progress(double fraction)
    {
        return await Apply("operation.progress", State.Progress(fraction, Context.Time.GetUtcNow())).ConfigureAwait(true);
    }

    public async Task<OperationState> Complete(OperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (State.Phase == OperationPhase.Done)
        {
            return State;
        }

        await Apply("operation.complete", State.Complete(result, Context.Time.GetUtcNow())).ConfigureAwait(true);

        OperationPlanRegistry.Remove(State.Id);
        _runningCts?.Cancel();
        if ((Seat.Services.GetService(typeof(OperationRegistry)) as OperationRegistry)?.TryGet(new OperationId(State.Id), out var handle) == true)
        {
            handle.NotifyDone(result);
        }
        return State;
    }

    public async Task<bool> RequestCancel()
    {
        if (State.Phase == OperationPhase.Done)
        {
            return false;
        }

        _runningCts?.Cancel();
        var handlerRuns = _runningCts is not null;
        return await Apply("operation.cancel", State.RequestCancel(handlerRuns, Context.Time.GetUtcNow())).ConfigureAwait(true);
    }

    // Commits a move, or publishes why the operation refused it; true when the operation moved.
    private async Task<bool> Apply(string operation, Outcome<OperationState> outcome)
    {
        switch (outcome)
        {
            case Transition<OperationState> { Signals.Count: > 0 } moved:
                await Save(moved.Next, moved.Signals).ConfigureAwait(true);
                return true;
            case Transition<OperationState> moved:
                await Store(moved.Next).ConfigureAwait(true);
                return true;
            case Refusal refusal:
                await PublishEphemeral(refusal.ToRejected(operation)).ConfigureAwait(true);
                return false;
            default:
                throw new InvalidOperationException($"{operation}: an outcome with no case.");
        }
    }

    public Task<OperationState?> Get() => Task.FromResult<OperationState?>(State.Exists ? State : null);

    private void Drive()
    {
        var handlers = Seat.Services.GetService(typeof(OperationHandlers)) as OperationHandlers;
        if (handlers?.Find(State.Kind) is not { } execution)
        {
            return;
        }

        var runningCts = _runningCts = new CancellationTokenSource();
        var scope = new OperationScope(
            Id.Brain,
            State.Origin,
            Seat.Services,
            State.Guard,
            Seat.Services.GetService(typeof(IOperationsQuery)) as IOperationsQuery ?? new NeuronOperationsQuery(Seat.Services, Id.Brain));

        var plan = OperationPlanRegistry.Take(State.Id) ?? State.Restore(execution.Deserialize(State.PlanJson));
        var self = Context.Get<IOperationNeuron>(Id);

        Context.Detach(async ct =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(runningCts.Token, ct);
            try
            {
                var result = await execution.Run(plan, self, scope, linked.Token).ConfigureAwait(false);
                await self.Complete(result).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                await self.Complete(new OperationResult.Cancelled()).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                await self.Complete(new OperationResult.Failed("error", error.Message)).ConfigureAwait(false);
            }
        });
    }

    private static bool? CheckClusterMembershipFallback(IServiceProvider services, string driver)
    {
        var membershipType = Type.GetType("Orleans.Runtime.IClusterMembershipService, Orleans.Core.Abstractions")
            ?? Type.GetType("Orleans.Runtime.IClusterMembershipService, Orleans.Core")
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("Orleans.Runtime.IClusterMembershipService"))
                .FirstOrDefault(t => t is not null);
        if (membershipType is null)
        {
            return null;
        }

        var membership = services.GetService(membershipType);
        if (membership is null)
        {
            return null;
        }

        var snapshotProp = membershipType.GetProperty("CurrentSnapshot");
        var snapshot = snapshotProp?.GetValue(membership);
        if (snapshot is null)
        {
            return null;
        }

        var siloDriverLifetimeType = Type.GetType("DigitalBrain.Runtime.Orleans.Hosting.SiloDriverLifetime, DigitalBrain.Runtime.Orleans")
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("DigitalBrain.Runtime.Orleans.Hosting.SiloDriverLifetime"))
                .FirstOrDefault(t => t is not null);
        var method = siloDriverLifetimeType?.GetMethod("DriverHasEnded", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (method is not null)
        {
            return method.Invoke(null, [driver, snapshot]) as bool?;
        }

        return null;
    }
}
