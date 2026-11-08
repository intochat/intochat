using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.Apps;

// Loads the features folder at boot (the feature.load spans), then follows the brain's lifecycle the way OsDefaults installs
// on it: every activation (the plain brain neuron's Activated signal, heard on this silo through IClusterListeners, 06-08)
// brings the brain's unedited features to the folder's version once the folder has loaded. Reconciling is fire-and-forget off
// the listener, as the hub pump was: a failure never fails the silo or a brain's activation.
internal sealed class FeatureBoot(
    IServiceProvider plain, FeatureCatalog catalog, DigitalBrain.Spi.IClusterListeners listeners, ILogger<FeatureBoot> logger) : IStartupTask
{
    private Task _loaded = Task.CompletedTask;

    public async Task Execute(CancellationToken cancellationToken)
    {
        // Registered before the load starts, so no activation during the load is missed; each waits for the load.
        // The registration lives as long as the silo (idempotent by listener name), so its handle is not kept.
        _ = await listeners.Listen<Activated>(nameof(FeatureBoot), (publisher, _, _) =>
        {
            var brainId = publisher.Brain.Value;
            _ = Task.Run(() => ReconcileAsync(brainId, cancellationToken), CancellationToken.None);
            return Task.CompletedTask;
        });
        _loaded = Task.Run(LoadAsync, CancellationToken.None);
    }

    private async Task LoadAsync()
    {
        try
        {
            var loaded = await catalog.Bundles();
            logger.LogInformation("Loaded {Count} shipped apps.", loaded.Count);
            foreach (var failure in await catalog.Failures())
            { logger.LogError("Feature {Owner}/{Name} {Version} was skipped: {Reason}", failure.Owner, failure.Name, failure.Version, failure.Reason); }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogError(error, "The shipped apps did not load."); }
    }

    private async Task ReconcileAsync(string brainId, CancellationToken cancellationToken)
    {
        // The listener runs in the turn that published Activated, under that turn's caller; reconciling is the module's own act.
        RequestContext.Remove(RuntimeCallerContext.RequestContextKey);
        try
        {
            if (await HostCaller.ForBrain(plain, brainId) is { } caller)
            {
                RuntimeCallerContext.Stamp(caller);
            }
            await _loaded;
            await SiloNeurons.Get<IFeatureReconciler>(plain, IFeatureReconciler.Key(brainId)).Reconcile(cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogWarning(error, "Reconciling features for brain {Brain} failed.", brainId); }
    }
}

// Runs FeatureBoot when the silo becomes active, as Orleans' AddStartupTask did (06-08: modules register services, not the silo).
internal sealed class FeatureBootLifecycle(IServiceProvider services) : ILifecycleParticipant<ISiloLifecycle>
{
    public void Participate(ISiloLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        lifecycle.Subscribe<FeatureBootLifecycle>(ServiceLifecycleStage.Active,
            cancellationToken => ActivatorUtilities.CreateInstance<FeatureBoot>(services).Execute(cancellationToken));
    }
}
