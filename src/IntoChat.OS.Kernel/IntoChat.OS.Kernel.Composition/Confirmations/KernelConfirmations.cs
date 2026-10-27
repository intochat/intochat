using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Operations;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Kernel.Composition.Confirmations;

internal sealed class KernelConfirmations(IServiceProvider services, IOptions<ConfirmationOptions> options) : IConfirmationContexts
{
    public IConfirmations For(BrainId brain, string requester)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requester);
        return new Bound(services, options, brain, requester);
    }

    private sealed class Bound(IServiceProvider services, IOptions<ConfirmationOptions> options, BrainId brain, string requester) : IConfirmations
    {
        public ConfirmationPolicy Policy => options.Value.For(brain.Value);

        private IConfirmationEntity Entity => SiloNeurons.Get<IConfirmationEntity>(services, new(brain, "runtime.confirmations", "confirmations"));

        public async Task<ConfirmationResult> Confirm(ConfirmationPlan plan, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var entity = Entity;
            try
            {
                await entity.Begin(plan, requester).WaitAsync(ct);
                while (true)
                {
                    if (await entity.Result(plan.Id).WaitAsync(ct) is { } answer) { return answer; }
                    await Task.Delay(100, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await entity.Cancel(plan.Id);
                throw;
            }
        }

        // A neuron relaying an answer remains the requester even if its turn inherited a human caller stamp.
        public Task Resolve(string planId, bool approve, CancellationToken ct = default)
            => Entity.ResolveAsRequester(planId, approve).WaitAsync(ct);
    }
}
