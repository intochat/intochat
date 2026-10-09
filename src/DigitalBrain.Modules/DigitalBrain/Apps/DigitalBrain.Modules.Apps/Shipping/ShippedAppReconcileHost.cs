using DigitalBrain;
using DigitalBrain.Framework;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace DigitalBrain.Apps;

// Watches the brain's lifecycle the way OsDefaults installs on it: every activation (the kernel's
// Activated signal) brings the brain's unedited shipped apps to the catalog's version. The pump is
// fire-and-forget from the startup task; a reconcile failure never fails the brain's activation.
internal sealed class ShippedAppReconcileHost(
    IGrainFactory grains, LocalSignalHub hub, ILogger<ShippedAppReconcileHost> logger) : IStartupTask
{
    public Task Execute(CancellationToken cancellationToken)
    {
        var activations = hub.Subscribe<Activated>();
        _ = Task.Run(() => PumpAsync(activations, cancellationToken), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task PumpAsync(LocalSignalHub.Subscription<Activated> activations, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var activated in activations.Reader.ReadAllAsync(cancellationToken))
            {
                // The brain neuron publishes Activated; its publisher name is "brain/{brainId}".
                var name = activated.Publisher.Name;
                var brainId = name[(name.IndexOf('/') + 1)..];
                try { await grains.GetGrain<IShippedAppReconciler>(IShippedAppReconciler.Key(brainId)).Reconcile(cancellationToken); }
                catch (Exception error) when (error is not OperationCanceledException)
                { logger.LogWarning(error, "Reconciling shipped apps for brain {Brain} failed.", brainId); }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogError(error, "The shipped-app reconciler stopped watching activations."); }
    }
}