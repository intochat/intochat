using System.Collections.Concurrent;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Operations;
using DigitalBrain.Spi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Kernel.Composition.Confirmations;

internal sealed class KernelConfirmations(IServiceProvider services, IOptions<ConfirmationOptions> options) : IConfirmationContexts
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ConfirmationResult>> _pending = new(StringComparer.Ordinal);
    private readonly object _listenerLock = new();
    private Task? _listenerTask;
    private IClusterListeners? _clusterListeners;

    public IConfirmations For(BrainId brain, string requester)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requester);
        return new Bound(this, services, options, brain, requester);
    }

    private Task EnsureListenerAsync()
    {
        if (_listenerTask is not null) { return _listenerTask; }
        lock (_listenerLock)
        {
            return _listenerTask ??= InitializeListenerAsync();
        }
    }

    private async Task InitializeListenerAsync()
    {
        _clusterListeners = services.GetService<IClusterListeners>();
        if (_clusterListeners is not null)
        {
            await _clusterListeners.Listen<ConfirmationResolved>("kernel-confirmations", (_, resolved, _) =>
            {
                CompletePending(resolved.PlanId, new ConfirmationResult(resolved.PlanId, resolved.Outcome, resolved.Reason, resolved.Skipped));
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }

    private void CompletePending(string planId, ConfirmationResult result)
    {
        if (_pending.TryRemove(planId, out var tcs))
        {
            tcs.TrySetResult(result);
        }
    }

    private sealed class Bound(KernelConfirmations owner, IServiceProvider services, IOptions<ConfirmationOptions> options, BrainId brain, string requester) : IConfirmations
    {
        public ConfirmationPolicy Policy => options.Value.For(brain.Value);

        private IConfirmationEntity Entity => SiloNeurons.Get<IConfirmationEntity>(services, new DigitalBrain.NeuronId(brain, "runtime.confirmations", "confirmations"));

        public async Task<ConfirmationResult> Confirm(ConfirmationPlan plan, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            await owner.EnsureListenerAsync().ConfigureAwait(false);
            var entity = Entity;
            var tcs = new TaskCompletionSource<ConfirmationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            owner._pending[plan.Id] = tcs;
            try
            {
                await entity.Begin(plan, requester).WaitAsync(ct).ConfigureAwait(false);
                if (await entity.Result(plan.Id).WaitAsync(ct).ConfigureAwait(false) is { } immediate)
                {
                    owner._pending.TryRemove(plan.Id, out _);
                    return immediate;
                }

                if (owner._clusterListeners is null)
                {
                    // Fallback when running in minimal test environments without cluster event listeners
                    while (!tcs.Task.IsCompleted)
                    {
                        if (await entity.Result(plan.Id).WaitAsync(ct).ConfigureAwait(false) is { } polled)
                        {
                            owner.CompletePending(plan.Id, polled);
                            return polled;
                        }
                        await Task.Delay(50, ct).ConfigureAwait(false);
                    }
                }

                return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                owner._pending.TryRemove(plan.Id, out _);
                await entity.Cancel(plan.Id).ConfigureAwait(false);
                throw;
            }
            finally
            {
                owner._pending.TryRemove(plan.Id, out _);
            }
        }

        // A neuron relaying an answer remains the requester even if its turn inherited a human caller stamp.
        public async Task Resolve(string planId, bool approve, CancellationToken ct = default)
        {
            await Entity.ResolveAsRequester(planId, approve).WaitAsync(ct).ConfigureAwait(false);
            if (await Entity.Result(planId).WaitAsync(ct).ConfigureAwait(false) is { } answer)
            {
                owner.CompletePending(planId, answer);
            }
        }
    }
}
